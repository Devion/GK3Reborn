using System.Security.Cryptography;
using System.Text;
using GK3Reborn.Foundation;
using Silk.NET.Shaderc;

namespace GK3Reborn.Rendering.Vulkan;

/// <summary>Which stage a shader is compiled for.</summary>
public enum ShaderStage
{
    /// <summary>Vertex shader.</summary>
    Vertex,

    /// <summary>Fragment shader.</summary>
    Fragment,

    /// <summary>Compute shader.</summary>
    Compute,
}

/// <summary>
/// Compiles HLSL to SPIR-V.
/// </summary>
/// <remarks>
/// <para>
/// <c>Plan/01-architecture.md</c> section 1 chose HLSL compiled with DXC. HLSL stands;
/// the compiler does not. DXC ships with the Vulkan SDK, which would make the SDK a
/// prerequisite for building the project at all — a real barrier for contributors who
/// only want to change gameplay code. shaderc compiles HLSL to SPIR-V just as well and
/// arrives as a NuGet package, so the toolchain installs itself.
/// </para>
/// <para>
/// Results are cached on disk by a hash of the source, entry point and stage. After the
/// first build the compilation is effectively offline, which is what the plan wanted from
/// DXC in the first place, and a cache miss is the only time the compiler runs at all.
/// </para>
/// </remarks>
public sealed class ShaderCompiler : IDisposable
{
    private readonly Shaderc _shaderc;
    private readonly string? _cacheDirectory;

    /// <summary>Creates a compiler.</summary>
    /// <param name="cacheDirectory">Where to cache compiled SPIR-V, or null to not cache.</param>
    public ShaderCompiler(string? cacheDirectory = null)
    {
        _shaderc = Shaderc.GetApi();
        _cacheDirectory = cacheDirectory;

        if (_cacheDirectory is not null)
        {
            Directory.CreateDirectory(_cacheDirectory);
        }
    }

    /// <summary>Compiles a shader.</summary>
    /// <param name="source">HLSL source.</param>
    /// <param name="stage">Which stage to compile for.</param>
    /// <param name="name">Name used in error messages.</param>
    /// <param name="entryPoint">Entry point function.</param>
    /// <returns>SPIR-V words as bytes.</returns>
    /// <exception cref="VulkanException">The shader did not compile.</exception>
    public unsafe byte[] Compile(
        string source, ShaderStage stage, string name = "shader", string entryPoint = "main")
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(entryPoint);

        string? cachePath = CachePathFor(source, stage, entryPoint);
        if (cachePath is not null && File.Exists(cachePath))
        {
            return File.ReadAllBytes(cachePath);
        }

        Compiler* compiler = _shaderc.CompilerInitialize();
        CompileOptions* options = _shaderc.CompileOptionsInitialize();

        try
        {
            _shaderc.CompileOptionsSetSourceLanguage(options, SourceLanguage.Hlsl);
            _shaderc.CompileOptionsSetTargetEnv(options, TargetEnv.Vulkan, (uint)EnvVersion.Vulkan13);
            _shaderc.CompileOptionsSetOptimizationLevel(options, OptimizationLevel.Performance);

            byte[] sourceBytes = Encoding.UTF8.GetBytes(source);
            byte[] nameBytes = Encoding.UTF8.GetBytes(name + "\0");
            byte[] entryBytes = Encoding.UTF8.GetBytes(entryPoint + "\0");

            CompilationResult* result;
            fixed (byte* sourcePointer = sourceBytes)
            fixed (byte* namePointer = nameBytes)
            fixed (byte* entryPointer = entryBytes)
            {
                result = _shaderc.CompileIntoSpv(
                    compiler,
                    sourcePointer,
                    (nuint)sourceBytes.Length,
                    stage switch
                    {
                        ShaderStage.Vertex => ShaderKind.VertexShader,
                        ShaderStage.Fragment => ShaderKind.FragmentShader,
                        _ => ShaderKind.ComputeShader,
                    },
                    namePointer,
                    entryPointer,
                    options);
            }

            try
            {
                if (_shaderc.ResultGetCompilationStatus(result) != CompilationStatus.Success)
                {
                    string message = Silk.NET.Core.Native.SilkMarshal.PtrToString(
                        (nint)_shaderc.ResultGetErrorMessage(result)) ?? "unknown error";

                    throw new VulkanException($"Could not compile '{name}': {message}");
                }

                nuint length = _shaderc.ResultGetLength(result);
                byte* bytes = _shaderc.ResultGetBytes(result);

                byte[] spirv = new byte[length];
                new ReadOnlySpan<byte>(bytes, (int)length).CopyTo(spirv);

                if (cachePath is not null)
                {
                    // Written through the atomic helper so a crash mid-write cannot leave
                    // a truncated module that would later be loaded as valid.
                    Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
                    File.WriteAllBytes(cachePath + ".tmp", spirv);
                    File.Move(cachePath + ".tmp", cachePath, overwrite: true);
                }

                return spirv;
            }
            finally
            {
                _shaderc.ResultRelease(result);
            }
        }
        finally
        {
            _shaderc.CompileOptionsRelease(options);
            _shaderc.CompilerRelease(compiler);
        }
    }

    /// <inheritdoc/>
    public void Dispose() => _shaderc.Dispose();

    private string? CachePathFor(string source, ShaderStage stage, string entryPoint)
    {
        if (_cacheDirectory is null)
        {
            return null;
        }

        // The key covers everything that changes the output, so a shader edit invalidates
        // its own entry and nothing else.
        byte[] key = SHA256.HashData(Encoding.UTF8.GetBytes($"{stage}|{entryPoint}|{source}"));
        return Path.Combine(_cacheDirectory, Convert.ToHexStringLower(key)[..32] + ".spv");
    }
}
