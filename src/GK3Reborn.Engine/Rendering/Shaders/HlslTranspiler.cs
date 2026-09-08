using Silk.NET.Core.Native;
using Silk.NET.SPIRV.Cross;
using CrossCompiler = Silk.NET.SPIRV.Cross.Compiler;

namespace GK3Reborn.Rendering.Shaders;

/// <summary>
/// Turns SPIR-V back into HLSL, so that shaders written once can be given to DXC.
/// </summary>
public sealed class HlslTranspiler : IDisposable
{
    private readonly Cross _cross = ShaderToolchain.Cross;

    /// <summary>Which locations a module reads as stage inputs.</summary>
    /// <param name="spirv">SPIR-V words as bytes.</param>
    /// <param name="name">Name used in error messages.</param>
    /// <returns>The locations, which for a fragment shader are the varyings it consumes.</returns>
    /// <exception cref="ShaderCompilationException">The module could not be read.</exception>
    public IReadOnlySet<uint> StageInputLocations(
        ReadOnlySpan<byte> spirv, string name = "shader") =>
        Locations(spirv, name, ResourceType.StageInput);

    private unsafe HashSet<uint> Locations(
        ReadOnlySpan<byte> spirv, string name, ResourceType kind)
    {
        ArgumentNullException.ThrowIfNull(name);

        Context* context = null;

        try
        {
            if (_cross.ContextCreate(&context) != Result.Success)
            {
                throw new ShaderCompilationException(
                    $"Could not read '{name}': SPIRV-Cross would not start.");
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
                    context, Backend.None, ir, CaptureMode.TakeOwnership, &compiler) != Result.Success)
            {
                throw Failure(context, name, "reflect");
            }

            Resources* resources;
            if (_cross.CompilerCreateShaderResources(compiler, &resources) != Result.Success)
            {
                throw Failure(context, name, "reflect");
            }

            ReflectedResource* list;
            nuint count;

            if (_cross.ResourcesGetResourceListForType(
                    resources, kind, &list, &count) != Result.Success)
            {
                throw Failure(context, name, "reflect the inputs of");
            }

            HashSet<uint> locations = [];

            for (nuint i = 0; i < count; i++)
            {
                locations.Add(
                    _cross.CompilerGetDecoration(compiler, list[i].Id, Silk.NET.SPIRV.Decoration.Location));
            }

            return locations;
        }
        finally
        {
            if (context is not null)
            {
                _cross.ContextDestroy(context);
            }
        }
    }

    /// <summary>Which locations a module writes as stage outputs.</summary>
    /// <param name="spirv">SPIR-V words as bytes.</param>
    /// <param name="name">Name used in error messages.</param>
    /// <returns>The locations, which for a vertex shader are the varyings it hands on.</returns>
    /// <exception cref="ShaderCompilationException">The module could not be read.</exception>
    public IReadOnlySet<uint> StageOutputLocations(
        ReadOnlySpan<byte> spirv, string name = "shader") =>
        Locations(spirv, name, ResourceType.StageOutput);

    /// <summary>Turns a SPIR-V module into HLSL.</summary>
    /// <param name="spirv">SPIR-V words as bytes.</param>
    /// <param name="name">Name used in error messages.</param>
    /// <param name="readBy">
    /// The locations the next stage reads, or null to keep every output. Given for a vertex
    /// shader whose fragment shader has already been reflected; see the note on stage
    /// linkage above.
    /// </param>
    /// <returns>HLSL source, with <c>main</c> as its entry point.</returns>
    /// <exception cref="ShaderCompilationException">The module could not be translated.</exception>
    public unsafe string Translate(
        ReadOnlySpan<byte> spirv, string name = "shader", IReadOnlySet<uint>? readBy = null)
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

            // Everything the next stage does not read. Both stages then pack their varyings
            // into the same registers, which is the only way Direct3D will link them.
            if (readBy is not null)
            {
                foreach (uint location in StageOutputLocations(compiler))
                {
                    if (!readBy.Contains(location))
                    {
                        _cross.CompilerMaskStageOutputByLocation(compiler, location, 0);
                    }
                }
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
    }

    /// <summary>Which locations a compiler's module writes as stage outputs.</summary>
    private unsafe List<uint> StageOutputLocations(CrossCompiler* compiler)
    {
        Resources* resources;
        if (_cross.CompilerCreateShaderResources(compiler, &resources) != Result.Success)
        {
            return [];
        }

        ReflectedResource* list;
        nuint count;

        if (_cross.ResourcesGetResourceListForType(
                resources, ResourceType.StageOutput, &list, &count) != Result.Success)
        {
            return [];
        }

        var locations = new List<uint>((int)count);

        for (nuint i = 0; i < count; i++)
        {
            locations.Add(
                _cross.CompilerGetDecoration(compiler, list[i].Id, Silk.NET.SPIRV.Decoration.Location));
        }

        return locations;
    }

    private unsafe ShaderCompilationException Failure(Context* context, string name, string verb)
    {
        string message = SilkMarshal.PtrToString((nint)_cross.ContextGetLastErrorString(context))
            ?? "unknown error";

        return new ShaderCompilationException($"Could not {verb} '{name}': {message}");
    }
}
