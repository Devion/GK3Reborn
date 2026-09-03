// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Rendering.Shaders;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;
using Silk.NET.Maths;
using System.Runtime.InteropServices;

namespace GK3Reborn.Rendering.Direct3D12;

/// <summary>
/// Draws the interface on top of the room, on Direct3D.
/// </summary>
/// <remarks>
/// <para>
/// One pipeline, one atlas and one vertex buffer. The interface is a few hundred rectangles
/// at most and they all come from the same sheet, so the only thing that breaks the draw
/// into more than one is a screen showing one of the game's own pictures — a map, a scan of
/// a parchment — which is bound in place of the atlas for the stretch of quads that use it.
/// </para>
/// <para>
/// <b>The vertex buffer is written every frame and read for the life of that frame.</b> With
/// frames in flight that means one buffer per frame of the ring, not one shared: writing the
/// buffer the device is still drawing from is the oldest hazard in the renderer and it shows
/// up as an interface that flickers between two layouts rather than as a crash.
/// </para>
/// </remarks>
public sealed unsafe class D3D12OverlayPass : IDisposable
{
    /// <summary>The most pictures the interface can hold at once.</summary>
    public const int MostPictures = 256;

    /// <summary>How many rectangles the vertex buffer holds.</summary>
    /// <summary>
    /// The most rectangles one frame of interface may hold.
    /// </summary>
    /// <remarks>
    /// Four thousand was enough for a screen of panels and words and is not enough for
    /// Sidney's map: a circle drawn as axis-aligned rectangles costs about its
    /// circumference, and four figures laid over a 4K map come to five and a half thousand
    /// between them. Sixteen thousand is about three megabytes a frame in flight, which is
    /// nothing beside a texture, and leaves the room the warning below is there to notice
    /// running out of.
    /// </remarks>
    private const int Capacity = 16384;

    private readonly D3D12Context _context;
    private readonly ShaderCompiler _compiler;
    private D3D12Pipeline _pipeline;
    private readonly D3D12DescriptorHeap _views;
    private readonly D3D12DescriptorHeap _samplers;
    private readonly D3D12Samplers _shared;
    private readonly D3D12Buffer[] _vertices;
    private readonly List<D3D12Texture> _pictures = [];
    private readonly List<OverlayRun> _runs = [];

    private D3D12Texture? _atlas;
    private uint _slot;
    private int _count;
    private bool _disposed;

    private D3D12OverlayPass(
        D3D12Context context,
        ShaderCompiler compiler,
        D3D12Pipeline pipeline,
        D3D12DescriptorHeap views,
        D3D12DescriptorHeap samplers,
        D3D12Samplers shared,
        D3D12Buffer[] vertices)
    {
        _context = context;
        _compiler = compiler;
        _pipeline = pipeline;
        _views = views;
        _samplers = samplers;
        _shared = shared;
        _vertices = vertices;
    }

    /// <summary>What the swapchain wants written into it.</summary>
    public DisplayEncode Display { get; set; } = DisplayEncode.Standard;

    /// <summary>How many rectangles the last display list came to.</summary>
    public int Rectangles => _count / 6;

    /// <summary>How many of the screens' own pictures are loaded.</summary>
    public int Pictures => _pictures.Count;

    /// <summary>Builds the pass.</summary>
    /// <param name="context">The device.</param>
    /// <param name="compiler">Where the shaders come from.</param>
    /// <param name="format">What the target it draws onto holds.</param>
    /// <param name="frames">How many frames the processor may be ahead by.</param>
    /// <returns>The pass.</returns>
    /// <exception cref="D3D12Exception">It could not be built.</exception>
    public static D3D12OverlayPass Create(
        D3D12Context context, ShaderCompiler compiler, Format format, uint frames)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(compiler);

        frames = Math.Max(1, frames);

        var layout = new ShaderLayout(
            [new ShaderBinding(0, 0, ShaderBindingKind.CombinedImageSampler, ShaderStages.Fragment)],
            OverlayShaders.ConstantBytes);

        uint stride = (uint)Marshal.SizeOf<OverlayVertex>();

        D3D12Pipeline? pipeline = null;
        D3D12DescriptorHeap? views = null;
        D3D12DescriptorHeap? samplers = null;
        D3D12Samplers? shared = null;
        var vertices = new D3D12Buffer[frames];

        try
        {
            pipeline = Build(context, compiler, format);

            // The atlas and every picture, once per frame of the ring, so a descriptor is
            // never rewritten while a frame that reads it is still on the device.
            views = D3D12DescriptorHeap.Create(
                context.Device,
                DescriptorHeapType.CbvSrvUav,
                (uint)(MostPictures + 1) * frames,
                shaderVisible: true);

            samplers = D3D12DescriptorHeap.Create(
                context.Device, DescriptorHeapType.Sampler, 1, shaderVisible: true);

            shared = D3D12Samplers.Create(context);
            shared.CopyInto(context, SamplerAddressing.Clamp, samplers.Cpu(samplers.Allocate()));

            for (uint i = 0; i < frames; i++)
            {
                vertices[i] = D3D12Buffer.CreateHostVisible(context, (ulong)Capacity * 6 * stride);
            }

            return new D3D12OverlayPass(
                context, compiler, pipeline, views, samplers, shared, vertices);
        }
        catch
        {
            foreach (D3D12Buffer? buffer in vertices)
            {
                buffer?.Dispose();
            }

            shared?.Dispose();
            samplers?.Dispose();
            views?.Dispose();
            pipeline?.Dispose();
            throw;
        }
    }

    /// <summary>Builds the pipeline again, for a target of another format.</summary>
    /// <param name="format">What the target now holds.</param>
    /// <exception cref="D3D12Exception">The pipeline could not be built.</exception>
    /// <remarks>
    /// Called when the window moves onto a high dynamic range display or off one, which
    /// changes the swapchain's format under everything built for it. Only the pipeline is
    /// rebuilt: the atlas, the pictures and the vertex buffers know nothing about the format
    /// and an interface that lost its letters every time a display changed would be a worse
    /// bug than the one this fixes.
    /// </remarks>
    public void Retarget(Format format)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _context.Wait();

        D3D12Pipeline rebuilt = Build(_context, _compiler, format);
        _pipeline.Dispose();
        _pipeline = rebuilt;
    }

    /// <summary>Gives the interface its sheet of glyphs.</summary>
    /// <param name="atlas">The sheet.</param>
    public void SetAtlas(OverlayAtlas atlas)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(atlas);

        _context.Wait();
        _atlas?.Dispose();

        // Linear, not sRGB. The sheet is a stencil rather than a photograph — what the
        // shader reads from it is coverage, and running coverage through an sRGB decode
        // makes the letters thin.
        _atlas = D3D12TextureUpload.Create(
            _context, atlas.Image, mipmaps: false, linear: true);
    }

    /// <summary>Puts one of a screen's own pictures on the device.</summary>
    /// <param name="image">The picture.</param>
    /// <returns>Its index in the picture list, counting from one.</returns>
    /// <remarks>
    /// Counted from one because nought means the atlas. A quad with no picture is a glyph,
    /// and a glyph is much the commonest thing the interface draws.
    /// </remarks>
    public int AddPicture(DecodedImage image)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_pictures.Count >= MostPictures)
        {
            return 0;
        }

        _context.Wait();

        // Not linear: this one really is a photograph, and it was authored encoded.
        _pictures.Add(D3D12TextureUpload.Create(_context, image, mipmaps: false, linear: false));
        return _pictures.Count;
    }

    /// <summary>Forgets every picture.</summary>
    public void DropPictures()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _context.Wait();

        foreach (D3D12Texture picture in _pictures)
        {
            picture.Dispose();
        }

        _pictures.Clear();
    }

    /// <summary>Turns a display list into vertices, ready to draw.</summary>
    /// <param name="overlay">What to draw.</param>
    /// <param name="frame">Which frame of the ring is being recorded.</param>
    public void Prepare(Overlay overlay, uint frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(overlay);

        _count = 0;
        _slot = frame % (uint)_vertices.Length;

        if (overlay.Quads.Count == 0)
        {
            _runs.Clear();
            return;
        }

        OverlayVertex[] vertices = OverlayMesh.Build(overlay, Capacity, _pictures.Count, _runs);

        if (vertices.Length == 0)
        {
            return;
        }

        _vertices[_slot].Write<OverlayVertex>(vertices);
        _count = vertices.Length;
    }

    /// <summary>Records the draw.</summary>
    /// <param name="list">Command list to record into.</param>
    /// <param name="target">Where to draw.</param>
    /// <param name="width">Width of that target in pixels.</param>
    /// <param name="height">Its height.</param>
    public void Record(
        ID3D12GraphicsCommandList4* list, CpuDescriptorHandle target, int width, int height)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(list);

        if (_count == 0 || _atlas is null)
        {
            return;
        }

        // Every picture this frame might name, written into this frame's own run of the
        // ring. One write of a handful of descriptors is cheaper than working out which of
        // them changed since the last frame.
        uint first = _slot * (uint)(MostPictures + 1);

        _atlas.Transition(list, ResourceStates.AllShaderResource);
        _atlas.Describe(_context, _views.Cpu(first));

        for (int i = 0; i < _pictures.Count; i++)
        {
            _pictures[i].Transition(list, ResourceStates.AllShaderResource);
            _pictures[i].Describe(_context, _views.Cpu(first + 1 + (uint)i));
        }

        ID3D12DescriptorHeap** heaps = stackalloc ID3D12DescriptorHeap*[2];
        heaps[0] = _views.Handle;
        heaps[1] = _samplers.Handle;
        list->SetDescriptorHeaps(2, heaps);

        list->SetGraphicsRootSignature(_pipeline.Signature.Handle);
        list->SetPipelineState(_pipeline.Handle);
        list->IASetPrimitiveTopology(
            Silk.NET.Core.Native.D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglelist);

        VertexBufferView vertices = _vertices[_slot].AsVertices(
            (uint)Marshal.SizeOf<OverlayVertex>());

        list->IASetVertexBuffers(0, 1, &vertices);

        int samplers = _pipeline.Signature.SamplerParameterFor(0);
        if (samplers >= 0)
        {
            list->SetGraphicsRootDescriptorTable((uint)samplers, _samplers.Gpu(0));
        }

        var viewport = new Viewport
        {
            Width = width,
            Height = height,
            MinDepth = 0f,
            MaxDepth = 1f,
        };

        var scissor = new Box2D<int>(0, 0, width, height);
        list->RSSetViewports(1, &viewport);
        list->RSSetScissorRects(1, &scissor);
        list->OMSetRenderTargets(1, &target, false, (CpuDescriptorHandle*)null);

        uint table = (uint)_pipeline.Signature.ParameterFor(0);
        uint constants = (uint)_pipeline.Signature.PushConstantParameter;

        foreach (OverlayRun run in _runs)
        {
            // Nought is the atlas and anything else is one of the pictures, which is what
            // the fragment stage's flag says too: a picture is drawn as it is and a glyph is
            // a shape cut out of a colour.
            list->SetGraphicsRootDescriptorTable(table, _views.Gpu(first + (uint)run.Picture));

            var block = new OverlayConstants(
                run.Picture == 0 ? 0 : 1,
                0,
                0,
                0,
                Display.Transfer,
                Display.PaperWhite,
                Display.Headroom,
                0f);

            list->SetGraphicsRoot32BitConstants(
                constants, OverlayShaders.ConstantBytes / 4, &block, 0);

            list->DrawInstanced((uint)run.Count, 1, (uint)run.First, 0);
        }
    }

    private static D3D12Pipeline Build(
        D3D12Context context, ShaderCompiler compiler, Format format)
    {
        var layout = new ShaderLayout(
            [new ShaderBinding(0, 0, ShaderBindingKind.CombinedImageSampler, ShaderStages.Fragment)],
            OverlayShaders.ConstantBytes);

        return D3D12Pipeline.CreateGraphics(
            context.Device,
            compiler,
            OverlayShaders.Vertex,
            OverlayShaders.Fragment,
            "overlay",
            layout,
            [format],
            Format.FormatUnknown,
            [
                new VertexInput(0, Format.FormatR32G32Float, 0),
                new VertexInput(1, Format.FormatR32G32Float, 8),
                new VertexInput(2, Format.FormatR32G32B32A32Float, 16),
            ],
            [new VertexBufferLayout((uint)Marshal.SizeOf<OverlayVertex>())],
            ShaderLanguage.Glsl,
            depthWrite: false,
            depthTest: false,

            // The interface is quads laid out on the screen, and which way they happen to be
            // wound is not something the layout has any opinion about.
            cull: CullMode.None,
            blend: true);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _context.Wait();

        foreach (D3D12Texture picture in _pictures)
        {
            picture.Dispose();
        }

        _pictures.Clear();
        _atlas?.Dispose();

        foreach (D3D12Buffer buffer in _vertices)
        {
            buffer.Dispose();
        }

        _shared.Dispose();
        _samplers.Dispose();
        _views.Dispose();
        _pipeline.Dispose();
    }
}
