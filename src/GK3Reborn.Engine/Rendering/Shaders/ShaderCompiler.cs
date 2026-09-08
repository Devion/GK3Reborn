using System.Security.Cryptography;
using System.Text;
using GK3Reborn.Foundation;

namespace GK3Reborn.Rendering.Shaders;

/// <summary>
/// The one way a shader gets from its source to a backend.
/// </summary>
public sealed class ShaderCompiler : IDisposable
{
    private readonly string? _cacheDirectory;
    private readonly Lock _gate = new();

    private SpirvCompiler? _spirv;
    private HlslTranspiler? _hlsl;
    private DxilCompiler? _dxil;
    private bool _disposed;

    /// <summary>How this compiler is set up, as part of every cache key.</summary>
    private const string Recipe = "6";

    /// <summary>
    /// Where compiled shaders are cached when nobody has said otherwise.
    /// </summary>
    public static string DefaultCacheDirectory => InstallPaths.WritableDirectory("shader-cache");

    /// <summary>The shader model the DXIL is compiled for, as D3D writes it (0x65 is 6.5).</summary>
    public uint DxilShaderModel { get; init; } = DxilCompiler.DefaultShaderModel;

    /// <summary>Creates a compiler.</summary>
    /// <param name="cacheDirectory">Where to cache compiled shaders, or null to not cache.</param>
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
                _dxil.Compile(vertexHlsl, ShaderStage.Vertex, name + ".vert", "main", DxilShaderModel),
                _dxil.Compile(fragmentHlsl, ShaderStage.Fragment, name + ".frag", "main", DxilShaderModel));

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
            return _dxil.Compile(hlsl, stage, name, "main", DxilShaderModel);
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
            $"pair|{Recipe}|{TargetKey(ShaderTarget.Dxil)}|{language}|{vertexEntryPoint}|{fragmentEntryPoint}|{vertexSource}|{fragmentSource}"));

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

    /// <summary>The target as the cache key spells it: DXIL carries the shader model, SPIR-V does not.</summary>
    private string TargetKey(ShaderTarget target) =>
        target == ShaderTarget.Dxil ? $"Dxil-{DxilShaderModel:X}" : "SpirV";

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
            Encoding.UTF8.GetBytes($"{Recipe}|{TargetKey(target)}|{language}|{stage}|{entryPoint}|{source}"));

        string extension = target == ShaderTarget.SpirV ? ".spv" : ".dxil";
        return Path.Combine(_cacheDirectory, Convert.ToHexStringLower(key)[..32] + extension);
    }
}
