using Silk.NET.Core.Native;
using Silk.NET.SPIRV.Cross;
using CrossCompiler = Silk.NET.SPIRV.Cross.Compiler;

namespace GK3Reborn.Rendering.Shaders;

/// <summary>
/// Turns SPIR-V back into HLSL, so that shaders written once can be given to DXC.
/// </summary>
/// <remarks>
/// <para>
/// The engine's shading is GLSL (ADR 0008) and Direct3D cannot read a word of it. The
/// alternative to this class is a second copy of every shader in HLSL, which was rejected:
/// two dialects of the same lighting drift apart, and the symptom of the drift is a
/// picture that differs between backends in a way nobody can attribute to a line.
/// </para>
/// <para>
/// SPIRV-Cross's HLSL back end is what makes this workable rather than merely possible.
/// It knows the constructs the renderer actually uses — <c>rayQueryEXT</c> becomes
/// <c>RayQuery</c> and <c>TraceRayInline</c>, <c>nonuniformEXT</c> becomes
/// <c>NonUniformResourceIndex</c>, a read-only storage buffer becomes a
/// <c>ByteAddressBuffer</c> — and it lays the bindings out exactly as
/// <see cref="ShaderBindings"/> describes without being asked to.
/// </para>
/// <para>
/// It is asked for two things all the same. Push constants are placed deliberately, because
/// left alone they land on top of whatever is at <c>b0</c>; and the vertex Y flip is undone,
/// because the projection carries one for Vulkan's clip space and Direct3D's is the other
/// way up. Doing the flip here rather than in the projection keeps one matrix for both
/// backends and puts the correction next to the reason for it.
/// </para>
/// </remarks>
public sealed class HlslTranspiler : IDisposable
{
    private readonly Cross _cross;
    private bool _disposed;

    /// <summary>Creates a transpiler.</summary>
    public HlslTranspiler() => _cross = Cross.GetApi();

    /// <summary>Turns a SPIR-V module into HLSL.</summary>
    /// <param name="spirv">SPIR-V words as bytes.</param>
    /// <param name="name">Name used in error messages.</param>
    /// <returns>HLSL source, with <c>main</c> as its entry point.</returns>
    /// <exception cref="ShaderCompilationException">The module could not be translated.</exception>
    public unsafe string Translate(ReadOnlySpan<byte> spirv, string name = "shader")
    {
        ArgumentNullException.ThrowIfNull(name);

        if (spirv.Length == 0 || spirv.Length % 4 != 0)
        {
            throw new ShaderCompilationException(
                $"Could not translate '{name}': {spirv.Length} bytes is not a SPIR-V module.");
        }

        Context* context = null;

        try
        {
            if (_cross.ContextCreate(&context) != Result.Success)
            {
                throw new ShaderCompilationException(
                    $"Could not translate '{name}': SPIRV-Cross would not start.");
            }

            ParsedIr* ir;
            fixed (byte* words = spirv)
            {
                if (_cross.ContextParseSpirv(context, (uint*)words, (nuint)(spirv.Length / 4), &ir)
                    != Result.Success)
                {
                    throw Failure(context, name, "parse");
                }
            }

            CrossCompiler* compiler;
            if (_cross.ContextCreateCompiler(
                    context, Backend.Hlsl, ir, CaptureMode.TakeOwnership, &compiler) != Result.Success)
            {
                throw Failure(context, name, "prepare");
            }

            CompilerOptions* options;
            if (_cross.CompilerCreateCompilerOptions(compiler, &options) != Result.Success)
            {
                throw Failure(context, name, "configure");
            }

            _cross.CompilerOptionsSetUint(options, CompilerOption.HlslShaderModel, ShaderBindings.ShaderModel);

            // The projection flips Y for Vulkan's clip space, where +Y is down. Direct3D's
            // is +Y up, so the flip has to come back out; doing it here rather than by
            // holding two projections keeps every matrix the camera hands out, every
            // matrix Streamline is told about, and every matrix a ray is built from, the
            // same one.
            _cross.CompilerOptionsSetBool(options, CompilerOption.FlipVertexY, 1);

            if (_cross.CompilerInstallCompilerOptions(compiler, options) != Result.Success)
            {
                throw Failure(context, name, "configure");
            }

            // Push constants have no register of their own in HLSL and SPIRV-Cross will
            // pick one; left alone it picks b0, which is where the frame's uniform buffer
            // already is. See ShaderBindings.PushConstantSpace.
            var pushConstants = new HlslResourceBinding
            {
                Stage = _cross.CompilerGetExecutionModel(compiler),
                DescSet = ShaderBindings.PushConstantDescriptorSet,
                Binding = ShaderBindings.PushConstantBinding,
                Cbv = new HlslResourceBindingMapping
                {
                    RegisterSpace = ShaderBindings.PushConstantSpace,
                    RegisterBinding = ShaderBindings.PushConstantRegister,
                },
            };

            if (_cross.CompilerHlslAddResourceBinding(compiler, &pushConstants) != Result.Success)
            {
                throw Failure(context, name, "bind the push constants of");
            }

            byte* source;
            if (_cross.CompilerCompile(compiler, &source) != Result.Success)
            {
                throw Failure(context, name, "translate");
            }

            return SilkMarshal.PtrToString((nint)source)
                ?? throw new ShaderCompilationException(
                    $"Could not translate '{name}': SPIRV-Cross produced nothing.");
        }
        finally
        {
            if (context is not null)
            {
                _cross.ContextDestroy(context);
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cross.Dispose();
    }

    private unsafe ShaderCompilationException Failure(Context* context, string name, string verb)
    {
        string message = SilkMarshal.PtrToString((nint)_cross.ContextGetLastErrorString(context))
            ?? "unknown error";

        return new ShaderCompilationException($"Could not {verb} '{name}': {message}");
    }
}
