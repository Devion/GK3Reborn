using GK3Reborn.Rendering.Shaders;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;

namespace GK3Reborn.Rendering.Direct3D12;

/// <summary>How one vertex attribute reaches a shader.</summary>
/// <param name="Location">
/// The GLSL location. Becomes the semantic index of a <c>TEXCOORD</c>, because that is the
/// only name SPIRV-Cross has to give it — see <see cref="ShaderBindings.VertexInputSemantic"/>.
/// </param>
/// <param name="Format">What the attribute holds.</param>
/// <param name="Offset">How far into the vertex it starts, in bytes.</param>
/// <param name="Buffer">Which bound vertex buffer it comes from.</param>
public readonly record struct VertexInput(
    uint Location,
    Format Format,
    uint Offset,
    uint Buffer = 0);

/// <summary>How the vertices of one buffer are laid out.</summary>
/// <param name="Stride">Bytes from one vertex to the next.</param>
/// <param name="PerInstance">Whether the buffer advances once per instance rather than per vertex.</param>
public readonly record struct VertexBufferLayout(uint Stride, bool PerInstance = false);

/// <summary>
/// A graphics or compute pipeline, and the root signature it binds through.
/// </summary>
public sealed unsafe class D3D12Pipeline : IDisposable
{
    private ComPtr<ID3D12PipelineState> _state;
    private readonly bool _ownsSignature;
    private bool _disposed;

    private D3D12Pipeline(
        ComPtr<ID3D12PipelineState> state, D3D12RootSignature signature, bool ownsSignature = true)
    {
        _state = state;
        Signature = signature;
        _ownsSignature = ownsSignature;
    }

    /// <summary>The root signature this pipeline binds through.</summary>
    public D3D12RootSignature Signature { get; }

    /// <summary>The pipeline state, for binding.</summary>
    public ID3D12PipelineState* Handle => _state.Handle;

    /// <summary>Builds a graphics pipeline.</summary>
    /// <param name="device">The device.</param>
    /// <param name="compiler">Where the shaders come from.</param>
    /// <param name="vertexSource">Vertex shader source.</param>
    /// <param name="fragmentSource">Fragment shader source.</param>
    /// <param name="name">Name used in error messages.</param>
    /// <param name="layout">What the pipeline binds.</param>
    /// <param name="colorFormats">What the render targets hold.</param>
    /// <param name="depthFormat">What the depth target holds, or unknown for none.</param>
    /// <param name="attributes">The vertex attributes, or nothing to draw from the vertex index.</param>
    /// <param name="buffers">How the vertex buffers are laid out.</param>
    /// <param name="language">Which language the sources are written in.</param>
    /// <param name="depthWrite">Whether the depth target is written.</param>
    /// <param name="depthTest">Whether the depth target is tested.</param>
    /// <param name="depthEqual">
    /// Whether a fragment at exactly the depth already there passes. What anything drawn at
    /// the far plane needs: see the remark in the depth state.
    /// </param>
    /// <param name="cull">Which faces are discarded.</param>
    /// <param name="frontCounterClockwise">
    /// Which way round a front face is wound. Clockwise for everything this renderer draws
    /// except the mirror pass, whose reflected view turns every triangle the other way.
    /// </param>
    /// <param name="blend">Whether the colour target is blended over rather than replaced.</param>
    /// <param name="premultiplied">
    /// Whether the fragment's colour already carries its own alpha, so the source factor is
    /// one rather than the alpha. What lets one blend both cover and add: see
    /// <see cref="Shaders.ParticleShaders"/>. Meaningless unless <paramref name="blend"/>.
    /// </param>
    /// <param name="mode">
    /// Which way the colour is combined with the target. Meaningless unless
    /// <paramref name="blend"/>, and <paramref name="premultiplied"/> only says anything
    /// about <see cref="OverlayBlend.Alpha"/>.
    /// </param>
    /// <param name="vertexEntryPoint">Entry point of the vertex shader in its own source.</param>
    /// <param name="fragmentEntryPoint">Entry point of the fragment shader in its own source.</param>
    /// <param name="reuse">
    /// A root signature to bind through rather than building another. What a second pipeline
    /// differing only in fixed-function state wants: Direct3D checks that the signature set
    /// on the command list is the one the pipeline was created with, so two pipelines a pass
    /// switches between have to share it — and the borrower does not free it.
    /// </param>
    /// <returns>The pipeline.</returns>
    /// <exception cref="D3D12Exception">It could not be created.</exception>
    public static D3D12Pipeline CreateGraphics(
        ID3D12Device5* device,
        ShaderCompiler compiler,
        string vertexSource,
        string fragmentSource,
        string name,
        ShaderLayout? layout = null,
        IReadOnlyList<Format>? colorFormats = null,
        Format depthFormat = Format.FormatUnknown,
        IReadOnlyList<VertexInput>? attributes = null,
        IReadOnlyList<VertexBufferLayout>? buffers = null,
        ShaderLanguage language = ShaderLanguage.Glsl,
        bool depthWrite = true,
        bool depthTest = true,
        bool depthEqual = false,
        CullMode cull = CullMode.Back,
        bool frontCounterClockwise = false,
        bool blend = false,
        bool premultiplied = false,
        OverlayBlend mode = OverlayBlend.Alpha,
        string vertexEntryPoint = "main",
        string fragmentEntryPoint = "main",
        D3D12RootSignature? reuse = null)
    {
        ArgumentNullException.ThrowIfNull(compiler);
        ArgumentNullException.ThrowIfNull(name);

        layout ??= ShaderLayout.Empty;
        colorFormats ??= [Format.FormatR8G8B8A8Unorm];
        attributes ??= [];
        buffers ??= [];

        // Compiled as a pair rather than one at a time, because the vertex half is masked
        // down to what the fragment half reads. See ShaderCompiler.CompileGraphics: the two
        // stages otherwise pack their varyings into different registers and Direct3D refuses
        // to link them.
        (byte[] vertex, byte[] fragment) = compiler.CompileGraphics(
            ShaderTarget.Dxil,
            vertexSource,
            fragmentSource,
            name,
            vertexEntryPoint,
            fragmentEntryPoint,
            language);

        D3D12RootSignature signature = reuse ??
            D3D12RootSignature.Create(device, layout, allowInputLayout: attributes.Count > 0);

        try
        {
            // The semantic name is one string for every attribute, so it is pinned once and
            // every element points at the same bytes. SPIRV-Cross has no other name to give
            // a GLSL vertex input than TEXCOORD plus its location.
            byte[] semantic = System.Text.Encoding.ASCII.GetBytes(
                ShaderBindings.VertexInputSemantic + "\0");

            var elements = new InputElementDesc[attributes.Count];

            fixed (byte* semanticName = semantic)
            fixed (byte* vertexBytes = vertex)
            fixed (byte* fragmentBytes = fragment)
            fixed (InputElementDesc* elementsPointer = elements)
            {
                for (int i = 0; i < attributes.Count; i++)
                {
                    VertexInput attribute = attributes[i];
                    bool perInstance = attribute.Buffer < buffers.Count
                        && buffers[(int)attribute.Buffer].PerInstance;

                    elements[i] = new InputElementDesc
                    {
                        SemanticName = semanticName,
                        SemanticIndex = attribute.Location,
                        Format = attribute.Format,
                        InputSlot = attribute.Buffer,
                        AlignedByteOffset = attribute.Offset,
                        InputSlotClass = perInstance
                            ? InputClassification.PerInstanceData
                            : InputClassification.PerVertexData,
                        InstanceDataStepRate = perInstance ? 1u : 0u,
                    };
                }

                var description = new GraphicsPipelineStateDesc
                {
                    PRootSignature = signature.Handle,
                    VS = new ShaderBytecode { PShaderBytecode = vertexBytes, BytecodeLength = (nuint)vertex.Length },
                    PS = new ShaderBytecode { PShaderBytecode = fragmentBytes, BytecodeLength = (nuint)fragment.Length },
                    SampleMask = uint.MaxValue,
                    PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
                    NumRenderTargets = (uint)colorFormats.Count,
                    DSVFormat = depthFormat,
                    SampleDesc = new SampleDesc(1, 0),
                    InputLayout = new InputLayoutDesc
                    {
                        PInputElementDescs = attributes.Count > 0 ? elementsPointer : null,
                        NumElements = (uint)attributes.Count,
                    },
                    RasterizerState = Rasterizer(cull, frontCounterClockwise),
                    BlendState = Blender(blend, premultiplied, mode, colorFormats.Count),
                    DepthStencilState = DepthStencil(
                        depthFormat != Format.FormatUnknown, depthWrite, depthTest, depthEqual),
                };

                for (int i = 0; i < colorFormats.Count && i < 8; i++)
                {
                    description.RTVFormats[i] = colorFormats[i];
                }

                ComPtr<ID3D12PipelineState> state = default;
                Guid stateId = ID3D12PipelineState.Guid;

                D3D12Exception.ThrowIfFailed(
                    device->CreateGraphicsPipelineState(
                        &description, &stateId, (void**)state.GetAddressOf()),
                    $"create the {name} pipeline");

                return new D3D12Pipeline(state, signature, ownsSignature: reuse is null);
            }
        }
        catch
        {
            if (reuse is null)
            {
                signature.Dispose();
            }

            throw;
        }
    }

    /// <summary>Builds a compute pipeline.</summary>
    /// <param name="context">The device.</param>
    /// <param name="compiler">Where the shader comes from.</param>
    /// <param name="source">Compute shader source.</param>
    /// <param name="name">Name used in error messages.</param>
    /// <param name="layout">What the pipeline binds.</param>
    /// <param name="language">Which language the source is written in.</param>
    /// <param name="entryPoint">Entry point of the shader in its own source.</param>
    /// <returns>The pipeline.</returns>
    /// <exception cref="D3D12Exception">It could not be created.</exception>
    public static D3D12Pipeline CreateCompute(
        D3D12Context context,
        ShaderCompiler compiler,
        string source,
        string name,
        ShaderLayout? layout = null,
        ShaderLanguage language = ShaderLanguage.Glsl,
        string entryPoint = "main")
    {
        ArgumentNullException.ThrowIfNull(context);
        return CreateCompute(context.Device, compiler, source, name, layout, language, entryPoint);
    }

    /// <summary>Builds a compute pipeline.</summary>
    /// <param name="device">The device.</param>
    /// <param name="compiler">Where the shader comes from.</param>
    /// <param name="source">Compute shader source.</param>
    /// <param name="name">Name used in error messages.</param>
    /// <param name="layout">What the pipeline binds.</param>
    /// <param name="language">Which language the source is written in.</param>
    /// <param name="entryPoint">Entry point of the shader in its own source.</param>
    /// <returns>The pipeline.</returns>
    /// <exception cref="D3D12Exception">It could not be created.</exception>
    public static D3D12Pipeline CreateCompute(
        ID3D12Device5* device,
        ShaderCompiler compiler,
        string source,
        string name,
        ShaderLayout? layout = null,
        ShaderLanguage language = ShaderLanguage.Glsl,
        string entryPoint = "main")
    {
        ArgumentNullException.ThrowIfNull(compiler);
        ArgumentNullException.ThrowIfNull(name);

        byte[] code = compiler.CompileTo(
            ShaderTarget.Dxil, source, ShaderStage.Compute, name, entryPoint, language);

        // A compute signature must not claim an input assembler. It costs a root slot and
        // the runtime says nothing about why one went missing.
        D3D12RootSignature signature =
            D3D12RootSignature.Create(device, layout ?? ShaderLayout.Empty, allowInputLayout: false);

        try
        {
            fixed (byte* bytes = code)
            {
                var description = new ComputePipelineStateDesc
                {
                    PRootSignature = signature.Handle,
                    CS = new ShaderBytecode { PShaderBytecode = bytes, BytecodeLength = (nuint)code.Length },
                };

                ComPtr<ID3D12PipelineState> state = default;
                Guid stateId = ID3D12PipelineState.Guid;

                D3D12Exception.ThrowIfFailed(
                    device->CreateComputePipelineState(
                        &description, &stateId, (void**)state.GetAddressOf()),
                    $"create the {name} compute pipeline");

                return new D3D12Pipeline(state, signature);
            }
        }
        catch
        {
            signature.Dispose();
            throw;
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
        _state.Dispose();

        // A variant borrows the signature of the pipeline it was cut from; only the
        // original may free it. See the `reuse` argument to CreateGraphics.
        if (_ownsSignature)
        {
            Signature.Dispose();
        }
    }

    private static RasterizerDesc Rasterizer(CullMode cull, bool frontCounterClockwise = false) => new()
    {
        FillMode = FillMode.Solid,
        CullMode = cull,

        // GK3's world is left-handed and its scenes were authored for Direct3D, so a
        // front face is clockwise. The Vulkan path says the same thing in its own words;
        // see Rendering/Camera.cs and docs/known-issues.md. Reversed only for the mirror
        // pass, which draws the room through a reflected view and so sees every triangle
        // wound the other way.
        FrontCounterClockwise = frontCounterClockwise,
        DepthBias = 0,
        DepthBiasClamp = 0f,
        SlopeScaledDepthBias = 0f,
        DepthClipEnable = true,
        MultisampleEnable = false,
        AntialiasedLineEnable = false,
        ForcedSampleCount = 0,
        ConservativeRaster = ConservativeRasterizationMode.Off,
    };

    private static BlendDesc Blender(
        bool blend, bool premultiplied, OverlayBlend mode, int targets)
    {
        var description = new BlendDesc
        {
            AlphaToCoverageEnable = false,

            // Off, so the first target's state applies to all of them. The renderer never
            // wants one target blended and another not within a pass.
            IndependentBlendEnable = false,
        };

        // Screen is S + D(1 - S) and multiply is D * S, both exactly; what makes either
        // right at less than full opacity is the colour the shader hands them, which is
        // where the arithmetic is written down. See Shaders.OverlayShaders. Neither
        // touches the destination's alpha: neither is covering anything.
        var target = mode switch
        {
            OverlayBlend.Screen => new RenderTargetBlendDesc
            {
                BlendEnable = blend,
                LogicOpEnable = false,
                SrcBlend = Blend.One,
                DestBlend = Blend.InvSrcColor,
                BlendOp = BlendOp.Add,
                SrcBlendAlpha = Blend.Zero,
                DestBlendAlpha = Blend.One,
                BlendOpAlpha = BlendOp.Add,
                LogicOp = LogicOp.Noop,
                RenderTargetWriteMask = (byte)ColorWriteEnable.All,
            },

            OverlayBlend.Multiply => new RenderTargetBlendDesc
            {
                BlendEnable = blend,
                LogicOpEnable = false,
                SrcBlend = Blend.DestColor,
                DestBlend = Blend.Zero,
                BlendOp = BlendOp.Add,
                SrcBlendAlpha = Blend.Zero,
                DestBlendAlpha = Blend.One,
                BlendOpAlpha = BlendOp.Add,
                LogicOp = LogicOp.Noop,
                RenderTargetWriteMask = (byte)ColorWriteEnable.All,
            },

            _ => new RenderTargetBlendDesc
            {
                BlendEnable = blend,
                LogicOpEnable = false,

                // One where the colour already carries its own alpha, which is what lets a
                // fragment choose between covering what is behind it and adding to it by
                // what it writes in the alpha channel alone.
                SrcBlend = premultiplied ? Blend.One : Blend.SrcAlpha,
                DestBlend = Blend.InvSrcAlpha,
                BlendOp = BlendOp.Add,

                // The destination alpha is kept as the source's rather than blended,
                // because the only thing that reads it is the composite, which wants
                // coverage and not a weighted average of two coverages.
                SrcBlendAlpha = Blend.One,
                DestBlendAlpha = Blend.InvSrcAlpha,
                BlendOpAlpha = BlendOp.Add,
                LogicOp = LogicOp.Noop,
                RenderTargetWriteMask = (byte)ColorWriteEnable.All,
            },
        };

        for (int i = 0; i < Math.Max(1, targets) && i < 8; i++)
        {
            description.RenderTarget[i] = target;
        }

        return description;
    }

    private static DepthStencilDesc DepthStencil(
        bool hasDepth, bool write, bool test, bool equal) => new()
    {
        DepthEnable = hasDepth && test,
        DepthWriteMask = hasDepth && write ? DepthWriteMask.All : DepthWriteMask.Zero,

        // Less rather than greater: the projection puts the near plane at zero, as it does
        // on the Vulkan side. Both APIs agree about depth, which is the one convention
        // that did not have to be reconciled.
        //
        // <b>Anything drawn at the far plane needs the equality.</b> The depth buffer is
        // cleared to one and a sky fragment is written at exactly one, so under a strict
        // less-than it loses to the clear and nothing is drawn — a black sky rather than an
        // error, in every scene the game has one. The Vulkan sky and horizon pipelines have
        // always asked for LessOrEqual; this is the same statement.
        DepthFunc = equal ? ComparisonFunc.LessEqual : ComparisonFunc.Less,
        StencilEnable = false,
        StencilReadMask = 0xFF,
        StencilWriteMask = 0xFF,
    };
}
