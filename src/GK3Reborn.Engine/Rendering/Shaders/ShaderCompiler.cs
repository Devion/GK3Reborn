using System.Security.Cryptography;
using System.Text;
using GK3Reborn.Foundation;

namespace GK3Reborn.Rendering.Shaders;

/// <summary>
/// The one way a shader gets from its source to a backend.
/// </summary>
/// <remarks>
/// <para>
/// Every shader in the engine has a single source, in the language ADR 0008 chose, and
/// two possible destinations. Vulkan wants SPIR-V and stops after the first step;
/// Direct3D 12 wants DXIL and goes on through SPIRV-Cross and DXC to get it. The steps
/// themselves are <see cref="SpirvCompiler"/>, <see cref="HlslTranspiler"/> and
/// <see cref="DxilCompiler"/>; this is what sequences them and remembers the answer.
/// </para>
/// <para>
/// Results are cached on disk by a hash of the source, entry point, stage and target.
/// After the first build the compilation is effectively offline, which is what the plan
/// wanted from DXC in the first place, and a cache miss is the only time any compiler runs
/// at all. That matters more on the Direct3D path than on the Vulkan one, because it is
/// three tools deep rather than one.
/// </para>
/// <para>
/// The three tools are created on first use and not before. A Vulkan session never loads
/// <c>dxcompiler</c>, a Direct3D session that finds every shader in the cache loads none
/// of the three, and a machine with no Direct3D at all can still compile everything it
/// needs without the missing library being an error.
/// </para>
/// </remarks>
public sealed class ShaderCompiler : IDisposable
{
    private readonly string? _cacheDirectory;
    private readonly Lock _gate = new();

    private SpirvCompiler? _spirv;
    private HlslTranspiler? _hlsl;
    private DxilCompiler? _dxil;
    private bool _disposed;

    /// <summary>How this compiler is set up, as part of every cache key.</summary>
    /// <remarks>
    /// <para>
    /// A cache key has to cover everything that changes the output, and the source is not all
    /// of it: the flags the compilers are driven with change it too. That was found the hard
    /// way. Turning off glslang optimisation for the Direct3D path changed every module it
    /// produces and changed no source, so the cache went on handing back the modules from
    /// before — and the pipeline went on being refused for a reason that had already been
    /// fixed.
    /// </para>
    /// <para>
    /// <b>Raise this whenever the toolchain is driven differently.</b> A new optimisation
    /// level, a new shader model, a different set of DXC arguments, a SPIRV-Cross option:
    /// all of them belong here, because none of them appears in the text of a shader.
    /// </para>
    /// </remarks>
    private const string Recipe = "4";

    /// <summary>
    /// Where compiled shaders are cached when nobody has said otherwise.
    /// </summary>
    /// <remarks>
    /// Beside the executable, so an unpacked install stays self-contained and carries its
    /// warm cache when it is moved. On a macOS <c>.app</c> in <c>/Applications</c> nothing
    /// can be written beside the executable at all — the bundle is read-only, and writing
    /// into a signed one would invalidate the signature even where the permissions allow
    /// it — so the cache moves to the user's own directory instead. See
    /// <see cref="InstallPaths.WritableDirectory"/>.
    /// </remarks>
    public static string DefaultCacheDirectory => InstallPaths.WritableDirectory("shader-cache");

    /// <summary>Creates a compiler.</summary>
    /// <param name="cacheDirectory">Where to cache compiled shaders, or null to not cache.</param>
    /// <remarks>
    /// A cache that cannot be created is not an error: every shader still compiles, just
    /// not once. Refusing to start the renderer because a directory is read-only would
    /// trade a slow first frame for no frames at all.
    /// </remarks>
    public ShaderCompiler(string? cacheDirectory = null)
    {
        if (cacheDirectory is null)
        {
            _cacheDirectory = null;
            return;
        }

        try
        {
            Directory.CreateDirectory(cacheDirectory);
            _cacheDirectory = cacheDirectory;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _cacheDirectory = null;
        }
    }

    /// <summary>Compiles a shader to SPIR-V.</summary>
    /// <param name="source">Shader source.</param>
    /// <param name="stage">Which stage to compile for.</param>
    /// <param name="name">Name used in error messages.</param>
    /// <param name="entryPoint">Entry point function.</param>
    /// <param name="language">Which language the source is written in.</param>
    /// <returns>SPIR-V words as bytes.</returns>
    /// <exception cref="ShaderCompilationException">The shader did not compile.</exception>
    /// <remarks>
    /// The shape the Vulkan backend has always called, kept so that the backend reads the
    /// same as it did before there were two of them.
    /// </remarks>
    public byte[] Compile(
        string source,
        ShaderStage stage,
        string name = "shader",
        string entryPoint = "main",
        ShaderLanguage language = ShaderLanguage.Hlsl) =>
        CompileTo(ShaderTarget.SpirV, source, stage, name, entryPoint, language);

    /// <summary>Compiles a shader for a particular backend.</summary>
    /// <param name="target">Which intermediate language to produce.</param>
    /// <param name="source">Shader source.</param>
    /// <param name="stage">Which stage to compile for.</param>
    /// <param name="name">Name used in error messages.</param>
    /// <param name="entryPoint">Entry point function.</param>
    /// <param name="language">Which language the source is written in.</param>
    /// <returns>SPIR-V words or a signed DXIL container, as bytes.</returns>
    /// <exception cref="ShaderCompilationException">The shader did not survive a step.</exception>
    public byte[] CompileTo(
        ShaderTarget target,
        string source,
        ShaderStage stage,
        string name = "shader",
        string entryPoint = "main",
        ShaderLanguage language = ShaderLanguage.Hlsl)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(entryPoint);
        ObjectDisposedException.ThrowIf(_disposed, this);

        string? cachePath = CachePathFor(target, source, stage, entryPoint, language);
        if (cachePath is not null && File.Exists(cachePath))
        {
            try
            {
                return File.ReadAllBytes(cachePath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Another thread is replacing this entry right now — on Windows the
                // rename fails the reader rather than swapping underneath it. Compiling
                // it again costs a few milliseconds and always succeeds.
            }
        }

        byte[] compiled = Build(target, source, stage, name, entryPoint, language);
        Store(cachePath, compiled);
        return compiled;
    }

    /// <summary>Compiles a vertex and fragment shader that will be linked together.</summary>
    /// <param name="target">Which intermediate language to produce.</param>
    /// <param name="vertexSource">Vertex shader source.</param>
    /// <param name="fragmentSource">Fragment shader source.</param>
    /// <param name="name">Name used in error messages.</param>
    /// <param name="vertexEntryPoint">Entry point of the vertex shader.</param>
    /// <param name="fragmentEntryPoint">Entry point of the fragment shader.</param>
    /// <param name="language">Which language the sources are written in.</param>
    /// <returns>The two compiled stages.</returns>
    /// <exception cref="ShaderCompilationException">Either stage did not survive a step.</exception>
    /// <remarks>
    /// <para>
    /// The two stages have to be compiled together for Direct3D, and it is not a convenience.
    /// Each is translated to HLSL on its own and DXC packs each stage’s varyings into
    /// consecutive hardware registers by itself, so a varying the fragment shader does not
    /// read leaves a hole in one stage and not the other. The mesh shader has exactly that:
    /// six outputs, five of them read. Direct3D refuses the pipeline outright, and the message
    /// it gives names a semantic rather than a location.
    /// </para>
    /// <para>
    /// So the fragment shader is reflected first, and the vertex shader is translated with
    /// every output the fragment shader does not read masked off. Vulkan needs none of this -
    /// it links by location and does not care about holes - which is why the SPIR-V path
    /// simply compiles the two.
    /// </para>
    /// </remarks>
    public (byte[] Vertex, byte[] Fragment) CompileGraphics(
        ShaderTarget target,
        string vertexSource,
        string fragmentSource,
        string name = "shader",
        string vertexEntryPoint = "main",
        string fragmentEntryPoint = "main",
        ShaderLanguage language = ShaderLanguage.Glsl)
    {
        ArgumentNullException.ThrowIfNull(vertexSource);
        ArgumentNullException.ThrowIfNull(fragmentSource);
        ArgumentNullException.ThrowIfNull(name);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (target == ShaderTarget.SpirV)
        {
            return (
                CompileTo(target, vertexSource, ShaderStage.Vertex, name + ".vert", vertexEntryPoint, language),
                CompileTo(target, fragmentSource, ShaderStage.Fragment, name + ".frag", fragmentEntryPoint, language));
        }

        // The pair is cached together, because the vertex half depends on the fragment half
        // and caching them apart would let a stale one be paired with a fresh one.
        string? cachePath = PairCachePathFor(
            vertexSource, fragmentSource, vertexEntryPoint, fragmentEntryPoint, language);

        if (cachePath is not null && ReadPair(cachePath) is { } cached)
        {
            return cached;
        }

        lock (_gate)
        {
            _spirv ??= new SpirvCompiler();

            byte[] vertexSpirv = _spirv.Compile(
                vertexSource, ShaderStage.Vertex, name + ".vert", vertexEntryPoint, language);

            byte[] fragmentSpirv = _spirv.Compile(
                fragmentSource, ShaderStage.Fragment, name + ".frag", fragmentEntryPoint, language);

            _hlsl ??= new HlslTranspiler();

            // The two stages have to agree about their varyings or Direct3D will not link
            // them, and the message it gives names a semantic rather than a location. This
            // is the same check ShaderInterfaceTests makes of every pair in the tree; it is
            // repeated here because a shader edited later would otherwise fail at pipeline
            // creation with nothing to say which varying was at fault.
            IReadOnlySet<uint> written = _hlsl.StageOutputLocations(vertexSpirv, name + ".vert");
            IReadOnlySet<uint> read = _hlsl.StageInputLocations(fragmentSpirv, name + ".frag");

            if (!written.SetEquals(read))
            {
                throw new ShaderCompilationException(
                    $"The two stages of {name} disagree about their varyings: the vertex stage "
                    + $"writes [{string.Join(", ", written.Order())}] and the fragment stage reads "
                    + $"[{string.Join(", ", read.Order())}]. Direct3D packs each stage into "
                    + "consecutive registers by itself, so a varying one of them does not use "
                    + "makes the two disagree about every varying after it.");
            }

            string fragmentHlsl = _hlsl.Translate(fragmentSpirv, name + ".frag");
            string vertexHlsl = _hlsl.Translate(vertexSpirv, name + ".vert");

            _dxil ??= new DxilCompiler();

            // SPIRV-Cross names the entry point of what it emits "main" whatever the source
            // called it, so the entry point DXC is given is not the one the source used.
            var pair = (
                _dxil.Compile(vertexHlsl, ShaderStage.Vertex, name + ".vert", "main"),
                _dxil.Compile(fragmentHlsl, ShaderStage.Fragment, name + ".frag", "main"));

            WritePair(cachePath, pair);
            return pair;
        }
    }

    /// <summary>Turns a shader into the HLSL the Direct3D backend will be given.</summary>
    /// <param name="source">Shader source.</param>
    /// <param name="stage">Which stage to compile for.</param>
    /// <param name="name">Name used in error messages.</param>
    /// <param name="entryPoint">Entry point function.</param>
    /// <param name="language">Which language the source is written in.</param>
    /// <returns>Generated HLSL.</returns>
    /// <exception cref="ShaderCompilationException">The shader did not survive a step.</exception>
    /// <remarks>
    /// Not on the path to a pipeline — <see cref="CompileTo"/> goes straight through to
    /// DXIL — but it is what makes a translation failure legible. A shader that DXC
    /// refuses is refused at a line of source nobody wrote, and being able to print that
    /// source is the difference between a fixable report and a mystery.
    /// </remarks>
    public string Translate(
        string source,
        ShaderStage stage,
        string name = "shader",
        string entryPoint = "main",
        ShaderLanguage language = ShaderLanguage.Hlsl)
    {
        ArgumentNullException.ThrowIfNull(source);
        ObjectDisposedException.ThrowIf(_disposed, this);

        byte[] spirv = CompileTo(ShaderTarget.SpirV, source, stage, name, entryPoint, language);

        lock (_gate)
        {
            _hlsl ??= new HlslTranspiler();
            return _hlsl.Translate(spirv, name);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _spirv?.Dispose();
            _hlsl?.Dispose();
            _dxil?.Dispose();
        }
    }

    private byte[] Build(
        ShaderTarget target,
        string source,
        ShaderStage stage,
        string name,
        string entryPoint,
        ShaderLanguage language)
    {
        lock (_gate)
        {
            _spirv ??= new SpirvCompiler();
            byte[] spirv = _spirv.Compile(source, stage, name, entryPoint, language);

            if (target == ShaderTarget.SpirV)
            {
                return spirv;
            }

            _hlsl ??= new HlslTranspiler();
            string hlsl = _hlsl.Translate(spirv, name);

            _dxil ??= new DxilCompiler();

            // SPIRV-Cross names the entry point of what it emits "main" whatever the
            // source called it, so the entry point DXC is given is not the one the source
            // was compiled with.
            return _dxil.Compile(hlsl, stage, name, "main");
        }
    }

    private static void Store(string? cachePath, byte[] compiled)
    {
        if (cachePath is null)
        {
            return;
        }

        // Written to a uniquely named temporary first, so a crash mid-write cannot leave a
        // truncated module that would later load as valid. The name has to be unique rather
        // than the destination plus a suffix: two threads compiling the same shader would
        // otherwise collide on the temporary, and one of them would fail on a file the
        // other still has open.
        string temporary = $"{cachePath}.{Environment.CurrentManagedThreadId}.tmp";

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            File.WriteAllBytes(temporary, compiled);
            File.Move(temporary, cachePath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Another thread or process got there first, and Windows reports that as either
            // a sharing violation or an access denial depending on which side of the
            // replace collided. Its bytes are the same bytes, so the only thing lost is
            // this copy of the work.
            try
            {
                File.Delete(temporary);
            }
            catch (IOException)
            {
                // Nothing further to do: a stale temporary is harmless.
            }
        }
    }

    private string? PairCachePathFor(
        string vertexSource,
        string fragmentSource,
        string vertexEntryPoint,
        string fragmentEntryPoint,
        ShaderLanguage language)
    {
        if (_cacheDirectory is null)
        {
            return null;
        }

        byte[] key = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"pair|{Recipe}|{language}|{vertexEntryPoint}|{fragmentEntryPoint}|{vertexSource}|{fragmentSource}"));

        return Path.Combine(_cacheDirectory, Convert.ToHexStringLower(key)[..32] + ".dxilpair");
    }

    /// <summary>Reads a cached pair, which is the two modules with their lengths in front.</summary>
    private static (byte[] Vertex, byte[] Fragment)? ReadPair(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length < 8)
            {
                return null;
            }

            int vertex = BitConverter.ToInt32(bytes, 0);
            int fragment = BitConverter.ToInt32(bytes, 4);

            if (vertex < 0 || fragment < 0 || 8 + vertex + fragment != bytes.Length)
            {
                return null;
            }

            return (bytes[8..(8 + vertex)], bytes[(8 + vertex)..]);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void WritePair(string? path, (byte[] Vertex, byte[] Fragment) pair)
    {
        if (path is null)
        {
            return;
        }

        byte[] bytes = new byte[8 + pair.Vertex.Length + pair.Fragment.Length];
        BitConverter.TryWriteBytes(bytes.AsSpan(0), pair.Vertex.Length);
        BitConverter.TryWriteBytes(bytes.AsSpan(4), pair.Fragment.Length);
        pair.Vertex.CopyTo(bytes.AsSpan(8));
        pair.Fragment.CopyTo(bytes.AsSpan(8 + pair.Vertex.Length));

        Store(path, bytes);
    }

    private string? CachePathFor(
        ShaderTarget target,
        string source,
        ShaderStage stage,
        string entryPoint,
        ShaderLanguage language)
    {
        if (_cacheDirectory is null)
        {
            return null;
        }

        // The key covers everything that changes the output, so a shader edit invalidates
        // its own entry and nothing else. The target is part of it because the same source
        // has two answers and they are not interchangeable.
        byte[] key = SHA256.HashData(
            Encoding.UTF8.GetBytes($"{Recipe}|{target}|{language}|{stage}|{entryPoint}|{source}"));

        string extension = target == ShaderTarget.SpirV ? ".spv" : ".dxil";
        return Path.Combine(_cacheDirectory, Convert.ToHexStringLower(key)[..32] + extension);
    }
}
