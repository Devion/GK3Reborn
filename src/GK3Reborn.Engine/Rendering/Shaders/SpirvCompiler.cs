using System.Text;
using Silk.NET.Shaderc;

namespace GK3Reborn.Rendering.Shaders;

/// <summary>
/// Compiles HLSL and GLSL to SPIR-V.
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
/// GLSL is accepted as well, for one reason: glslang — which is what shaderc uses for
/// both languages — implements ray query only in its GLSL front end. Its HLSL front end
/// does not know <c>RaytracingAccelerationStructure</c> at all and fails at the
/// declaration. Ray-traced shading therefore has to be GLSL unless DXC is adopted, and
/// adopting DXC as the *front* end brings back exactly the Vulkan SDK prerequisite this
/// class exists to avoid. See ADR 0008.
/// </para>
/// <para>
/// DXC is now in the tree all the same — <see cref="DxilCompiler"/> — but as the *back*
/// end of the Direct3D path, from a NuGet package of its own and never as a prerequisite
/// for a build. Nothing about that changes the argument above: the shaders are still
/// authored in the language shaderc can read, and this is still the only thing that reads
/// them.
/// </para>
/// <para>
/// This is the source-to-SPIR-V step alone. Caching, and the further steps that turn
/// SPIR-V into something Direct3D can load, are <see cref="ShaderCompiler"/>'s.
/// </para>
/// </remarks>
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
    /// <remarks>
    /// <b>The Direct3D path asks for no optimisation, and it is not about speed.</b> The
    /// optimiser prunes a stage input nothing reads, which is harmless under Vulkan - it
    /// links stages by location and does not mind a hole - and fatal under Direct3D, which
    /// links by the register each stage packed its varyings into. The mesh fragment shader
    /// declares six inputs and reads five in the raster variant, so an optimised module
    /// leaves the two stages packing differently and the pipeline is refused with a message
    /// about a semantic. Nothing is lost by turning it off: DXC optimises the HLSL
    /// afterwards, which is where the optimisation that matters happens.
    /// </remarks>
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
    /// <remarks>
    /// Nothing to release. The shaderc handle belongs to <see cref="ShaderToolchain"/> and
    /// outlives every compiler that borrows it; releasing it here is what unmapped glslang
    /// out from under the other threads still compiling, and killed the Linux build at
    /// exit. Everything this class allocates from shaderc — the compiler, its options and
    /// each result — is already released by the compile that made it. <c>IDisposable</c>
    /// stays because callers hold this in a <c>using</c>.
    /// </remarks>
    public void Dispose()
    {
    }
}
