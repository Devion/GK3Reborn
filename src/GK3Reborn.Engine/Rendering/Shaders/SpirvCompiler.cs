using System.Text;
using Silk.NET.Shaderc;

namespace GK3Reborn.Rendering.Shaders;

/// <summary>
/// Compiles HLSL and GLSL to SPIR-V.
/// </summary>
public sealed class SpirvCompiler : IDisposable
{
    private readonly Shaderc _shaderc = ShaderToolchain.Shaderc;

    /// <summary>Compiles a shader to SPIR-V.</summary>
    /// <param name="source">Shader source.</param>
    /// <param name="stage">Which stage to compile for.</param>
    /// <param name="name">Name used in error messages.</param>
    /// <param name="entryPoint">Entry point function.</param>
    /// <param name="language">Which language the source is written in.</param>
    /// <param name="optimise">
    /// Whether to let glslang optimise the module.
    /// </param>
    /// <returns>SPIR-V words as bytes.</returns>
    /// <exception cref="ShaderCompilationException">The shader did not compile.</exception>
    public unsafe byte[] Compile(
        string source,
        ShaderStage stage,
        string name = "shader",
        string entryPoint = "main",
        ShaderLanguage language = ShaderLanguage.Hlsl,
        bool optimise = true)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(entryPoint);

        Compiler* compiler = _shaderc.CompilerInitialize();
        CompileOptions* options = _shaderc.CompileOptionsInitialize();

        try
        {
            _shaderc.CompileOptionsSetSourceLanguage(
                options,
                language == ShaderLanguage.Glsl ? SourceLanguage.Glsl : SourceLanguage.Hlsl);
            _shaderc.CompileOptionsSetTargetEnv(options, TargetEnv.Vulkan, (uint)EnvVersion.Vulkan13);
            _shaderc.CompileOptionsSetOptimizationLevel(
                options, optimise ? OptimizationLevel.Performance : OptimizationLevel.Zero);

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

                    throw new ShaderCompilationException($"Could not compile '{name}': {message}");
                }

                nuint length = _shaderc.ResultGetLength(result);
                byte* bytes = _shaderc.ResultGetBytes(result);

                byte[] spirv = new byte[length];
                new ReadOnlySpan<byte>(bytes, (int)length).CopyTo(spirv);
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
    public void Dispose()
    {
    }
}
