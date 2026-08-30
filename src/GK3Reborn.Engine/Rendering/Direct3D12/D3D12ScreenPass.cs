using GK3Reborn.Rendering.Shaders;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;
using Silk.NET.Maths;

namespace GK3Reborn.Rendering.Direct3D12;

/// <summary>
/// A pass that covers the frame with one triangle and reads some textures.
/// </summary>
/// <remarks>
/// <para>
/// Most of the renderer is this shape. The composite adds the traced light to the raster
/// picture, the output applies a tone curve and an encode, the fade darkens, the film covers
/// everything, the reflection downsample halves a target: six textures at most, a handful of
/// constants, no vertex buffer and no depth. Writing six classes to say that six times would
/// be six places to get the descriptor bookkeeping wrong.
/// </para>
/// <para>
/// <b>The inputs change every frame and the descriptors have to change with them.</b> A
/// G-buffer target this frame is a different resource from the one last frame, so the
/// descriptors are written into a ring rather than once at creation: each frame takes the
/// next run of slots and the ring is large enough that no run is rewritten while the device
/// is still reading it. That is the same hazard as an animated vertex buffer, and it has the
/// same answer.
/// </para>
/// <para>
/// The samplers are one run, shared, and clamped. Every pass here reads a full-screen target
/// exactly once across its own extent; a wrapped sample at the edge of one would fetch the
/// far side of the picture, which shows up as a bright seam along one edge and nothing else.
/// </para>
/// </remarks>
public sealed unsafe class D3D12ScreenPass : IDisposable
{
    /// <summary>How many frames of descriptors the ring holds.</summary>
    /// <remarks>
    /// Three, to match the swapchain rather than the frames in flight: the ring is written
    /// once a frame and read for as long as that frame is on the device, so it has to outlast
    /// the deepest thing that can still be reading it.
    /// </remarks>
    private const uint RingDepth = 3;

    private readonly D3D12Context _context;
    private readonly D3D12Pipeline _pipeline;
    private readonly D3D12DescriptorHeap _views;
    private readonly D3D12DescriptorHeap _samplers;
    private readonly D3D12Samplers _shared;
    private readonly uint _inputs;
    private readonly uint _constantWords;
    private uint _ring;
    private bool _disposed;

    private D3D12ScreenPass(
        D3D12Context context,
        D3D12Pipeline pipeline,
        D3D12DescriptorHeap views,
        D3D12DescriptorHeap samplers,
        D3D12Samplers shared,
        uint inputs,
        uint constantWords)
    {
        _context = context;
        _pipeline = pipeline;
        _views = views;
        _samplers = samplers;
        _shared = shared;
        _inputs = inputs;
        _constantWords = constantWords;
    }

    /// <summary>The root signature this pass binds through.</summary>
    public D3D12RootSignature Signature => _pipeline.Signature;

    /// <summary>Builds a pass.</summary>
    /// <param name="context">The device.</param>
    /// <param name="compiler">Where the shaders come from.</param>
    /// <param name="vertexSource">Vertex shader source.</param>
    /// <param name="fragmentSource">Fragment shader source.</param>
    /// <param name="name">Name used in error messages.</param>
    /// <param name="inputs">How many textures the fragment shader reads.</param>
    /// <param name="constantBytes">How many bytes of push constants it takes.</param>
    /// <param name="colorFormats">What the targets hold.</param>
    /// <param name="blend">Whether to blend over what is there rather than replacing it.</param>
    /// <returns>The pass.</returns>
    /// <exception cref="D3D12Exception">The pipeline could not be created.</exception>
    public static D3D12ScreenPass Create(
        D3D12Context context,
        ShaderCompiler compiler,
        string vertexSource,
        string fragmentSource,
        string name,
        uint inputs,
        uint constantBytes,
        IReadOnlyList<Format> colorFormats,
        bool blend = false)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(compiler);

        var bindings = new List<ShaderBinding>((int)inputs);

        for (uint i = 0; i < inputs; i++)
        {
            bindings.Add(new ShaderBinding(0, i, ShaderBindingKind.CombinedImageSampler, ShaderStages.Fragment));
        }

        var layout = new ShaderLayout(bindings, constantBytes);

        D3D12DescriptorHeap? views = null;
        D3D12DescriptorHeap? samplers = null;
        D3D12Samplers? shared = null;

        try
        {
            views = D3D12DescriptorHeap.Create(
                context.Device,
                DescriptorHeapType.CbvSrvUav,
                Math.Max(1, inputs * RingDepth),
                shaderVisible: true);

            samplers = D3D12DescriptorHeap.Create(
                context.Device,
                DescriptorHeapType.Sampler,
                Math.Max(1, inputs),
                shaderVisible: true);

            shared = D3D12Samplers.Create(context);

            D3D12Pipeline pipeline = D3D12Pipeline.CreateGraphics(
                context.Device,
                compiler,
                vertexSource,
                fragmentSource,
                name,
                layout,
                colorFormats,

                // No depth anywhere here. A full-screen triangle covers everything and is
                // drawn in the order the passes run, so a depth test would be a buffer to
                // clear and a comparison to get wrong for no benefit.
                Format.FormatUnknown,
                attributes: null,
                buffers: null,
                ShaderLanguage.Glsl,
                depthWrite: false,
                depthTest: false,

                // Nothing to cull: one triangle, and which way it faces is whichever way the
                // vertex index happened to wind it.
                cull: CullMode.None,
                blend: blend);

            var pass = new D3D12ScreenPass(
                context, pipeline, views, samplers, shared, inputs, constantBytes / 4);

            pass.WriteSamplers();
            return pass;
        }
        catch
        {
            views?.Dispose();
            samplers?.Dispose();
            shared?.Dispose();
            throw;
        }
    }

    /// <summary>Draws the pass over the whole of a target.</summary>
    /// <typeparam name="TConstants">The push constant block.</typeparam>
    /// <param name="list">The list to record into.</param>
    /// <param name="targets">Where to draw, already transitioned and cleared as wanted.</param>
    /// <param name="inputs">What to read, in the order the shader declares them.</param>
    /// <param name="constants">What to tell the shader.</param>
    /// <param name="width">Target width in pixels.</param>
    /// <param name="height">Target height in pixels.</param>
    /// <exception cref="ArgumentException">The wrong number of inputs was given.</exception>
    public void Draw<TConstants>(
        ID3D12GraphicsCommandList4* list,
        ReadOnlySpan<CpuDescriptorHandle> targets,
        ReadOnlySpan<D3D12Texture> inputs,
        in TConstants constants,
        int width,
        int height)
        where TConstants : unmanaged
    {
        ArgumentNullException.ThrowIfNull(list);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (inputs.Length != _inputs)
        {
            throw new ArgumentException(
                $"This pass reads {_inputs} textures and was given {inputs.Length}.", nameof(inputs));
        }

        // The next run in the ring. Written now and read for the life of the frame, which is
        // why the ring is deeper than one.
        uint first = _ring * _inputs;
        _ring = (_ring + 1) % RingDepth;

        for (uint i = 0; i < _inputs; i++)
        {
            inputs[(int)i].Describe(_context, _views.Cpu(first + i));
        }

        ID3D12DescriptorHeap** heaps = stackalloc ID3D12DescriptorHeap*[2];
        heaps[0] = _views.Handle;
        heaps[1] = _samplers.Handle;
        list->SetDescriptorHeaps(2, heaps);

        list->SetGraphicsRootSignature(_pipeline.Signature.Handle);
        list->SetPipelineState(_pipeline.Handle);
        list->IASetPrimitiveTopology(
            Silk.NET.Core.Native.D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglelist);

        if (_inputs > 0)
        {
            list->SetGraphicsRootDescriptorTable(
                (uint)_pipeline.Signature.ParameterFor(0), _views.Gpu(first));

            int samplers = _pipeline.Signature.SamplerParameterFor(0);
            if (samplers >= 0)
            {
                list->SetGraphicsRootDescriptorTable((uint)samplers, _samplers.Gpu(0));
            }
        }

        if (_constantWords > 0)
        {
            fixed (TConstants* block = &constants)
            {
                list->SetGraphicsRoot32BitConstants(
                    (uint)_pipeline.Signature.PushConstantParameter, _constantWords, block, 0);
            }
        }

        var viewport = new Viewport
        {
            TopLeftX = 0f,
            TopLeftY = 0f,
            Width = width,
            Height = height,
            MinDepth = 0f,
            MaxDepth = 1f,
        };

        var scissor = new Box2D<int>(0, 0, width, height);
        list->RSSetViewports(1, &viewport);
        list->RSSetScissorRects(1, &scissor);

        fixed (CpuDescriptorHandle* first_ = targets)
        {
            list->OMSetRenderTargets(
                (uint)targets.Length, first_, false, (CpuDescriptorHandle*)null);
        }

        // Three vertices and no buffer. The shader makes the triangle from the vertex index,
        // which is why every pass here shares one vertex stage.
        list->DrawInstanced(3, 1, 0, 0);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _pipeline.Dispose();
        _shared.Dispose();
        _samplers.Dispose();
        _views.Dispose();
    }

    private void WriteSamplers()
    {
        if (_inputs == 0)
        {
            return;
        }

        uint first = _samplers.Allocate(_inputs);

        for (uint i = 0; i < _inputs; i++)
        {
            // Clamped, every one. These read a full-screen target across its own extent, and
            // a wrapped sample at an edge fetches the far side of the picture.
            _shared.CopyInto(_context, SamplerAddressing.Clamp, _samplers.Cpu(first + i));
        }
    }
}
