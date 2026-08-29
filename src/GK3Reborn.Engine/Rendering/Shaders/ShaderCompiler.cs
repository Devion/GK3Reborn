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
            Encoding.UTF8.GetBytes($"{target}|{language}|{stage}|{entryPoint}|{source}"));

        string extension = target == ShaderTarget.SpirV ? ".spv" : ".dxil";
        return Path.Combine(_cacheDirectory, Convert.ToHexStringLower(key)[..32] + extension);
    }
}
