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
/// Draws the reconstructed horizon: real terrain, its forest, and a generated sky with
/// procedural cloud cover, where the painted skybox was.
/// </summary>
/// <remarks>
/// <para>
/// The twin of <c>TerrainPipeline</c>. What is drawn — the mesh, the forest, which trees are
/// near enough to be models, and the two constant blocks a frame is drawn with — is
/// <see cref="TerrainPlan"/>, which both backends share, and the stages are
/// <see cref="TerrainShaders"/>. What is here is the buffers, the textures and the four
/// pipeline states.
/// </para>
/// <para>
/// <b>One view heap and one sampler heap for the whole pass.</b> Direct3D allows one of each
/// bound at a time, so the six ground textures and every tree sheet live in the same pair:
/// the ground's table starts at zero and a sheet's table starts at six plus its own index.
/// A table is a base pointer, so binding a sheet is one call and no heap is ever swapped
/// inside the pass.
/// </para>
/// <para>
/// When this draws, the painted cubemap does not: its mountains are baked into the picture
/// and would double-expose against the reconstructed ridge.
/// </para>
/// </remarks>
public sealed unsafe class D3D12TerrainPass : IDisposable
{
    /// <summary>How many textures the ground, the impostors and the models share.</summary>
    /// <remarks>
    /// Four tiles, the splat weights and the vista's tint. The first four repeat and the
    /// last two are clamped, which is the whole of why the samplers are not all one.
    /// </remarks>
    private const uint GroundTextures = 6;

    /// <summary>Bytes from one corner of the ground or of an impostor to the next.</summary>
    private const uint VertexStride = 24;

    /// <summary>Bytes from one corner of a modelled tree to the next.</summary>
    private const uint TreeVertexStride = 32;

    /// <summary>Bytes from one placed tree to the next, in either instance stream.</summary>
    private const uint InstanceStride = TerrainPlan.Stride * sizeof(float);

    private readonly D3D12Context _context;
    private readonly TerrainPlan _plan;

    private D3D12Pipeline? _ground;
    private D3D12Pipeline? _trees;
    private D3D12Pipeline? _models;
    private D3D12Pipeline? _sky;

    private D3D12DescriptorHeap? _views;
    private D3D12DescriptorHeap? _samplers;
    private D3D12Samplers? _shared;

    private readonly D3D12Texture?[] _textures = new D3D12Texture?[GroundTextures];
    private readonly List<D3D12Texture> _sheets = [];

    private D3D12Buffer? _vertices;
    private D3D12Buffer? _indices;
    private D3D12Buffer? _treeVertices;
    private D3D12Buffer? _treeIndices;
    private D3D12Buffer? _treeInstances;
    private D3D12Buffer? _modelVertices;
    private D3D12Buffer? _modelIndices;
    private D3D12Buffer? _modelInstances;

    private bool _disposed;

    private D3D12TerrainPass(D3D12Context context, TerrainPlan plan)
    {
        _context = context;
        _plan = plan;
    }

    /// <summary>
    /// The backdrop's own arithmetic: its meshes, its forest, and what a frame is drawn
    /// with. Everything tunable about the horizon lives on it.
    /// </summary>
    public TerrainPlan Plan => _plan;

    /// <summary>Builds the pass for one scene's backdrop.</summary>
    /// <param name="context">The device.</param>
    /// <param name="compiler">Where the shaders come from.</param>
    /// <param name="colorFormat">What the target it draws onto holds.</param>
    /// <param name="depthFormat">What the depth target holds.</param>
    /// <param name="backdrop">The terrain, forest and layers to build and draw.</param>
    /// <returns>The pass.</returns>
    /// <exception cref="D3D12Exception">It could not be built.</exception>
    public static D3D12TerrainPass Create(
        D3D12Context context,
        ShaderCompiler compiler,
        Format colorFormat,
        Format depthFormat,
        TerrainBackdrop backdrop)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(compiler);
        ArgumentNullException.ThrowIfNull(backdrop);

        var pass = new D3D12TerrainPass(
            context, TerrainPlan.Create(backdrop, backdrop.TreeTextures.Count));

        try
        {
            pass.UploadTextures(backdrop);
            pass.UploadMesh();
            pass.UploadTrees();
            pass.UploadTreeModels(backdrop);
            pass.Describe();
            pass.BuildPipelines(compiler, colorFormat, depthFormat);

            return pass;
        }
        catch
        {
            pass.Dispose();
            throw;
        }
    }

    /// <summary>Records the backdrop: terrain, forest, then the sky behind them.</summary>
    /// <param name="list">Command list to record into.</param>
    /// <param name="camera">Where the player is looking from, in room units.</param>
    /// <param name="width">Viewport width.</param>
    /// <param name="height">Its height.</param>
    /// <remarks>
    /// Records into whatever render targets are already bound, so the caller keeps the
    /// room's own targets set. The backdrop writes depth as well as colour — it has to sort
    /// against itself — so unlike the painted cubemap it must be given a writable depth view.
    /// </remarks>
    public void Record(ID3D12GraphicsCommandList4* list, Camera camera, int width, int height)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(camera);

        if (_ground is null || _vertices is null || _indices is null || width <= 0 || height <= 0)
        {
            return;
        }

        // Where the camera stands in the backdrop, which trees are near enough to be models,
        // and the two blocks the stages read. None of that is a device question, so none of
        // it is answered here — see TerrainPlan.
        TerrainFrame frame = _plan.Frame(camera, width, height);
        TerrainConstants push = frame.Ground;

        // The near band, where it was rebuilt this frame. Written straight into an upload
        // heap the device reads as a vertex buffer, which is safe because every caller
        // drains the queue before it records — see D3D12Renderer.DrawFrame and the headless
        // renderer, both of which wait on the frame ring first.
        if (frame.Reselected && _modelInstances is not null)
        {
            _modelInstances.Write<float>(
                _plan.ModelInstanceData.AsSpan(0, (int)_plan.ModelCount * TerrainPlan.Stride));
        }

        ID3D12DescriptorHeap** heaps = stackalloc ID3D12DescriptorHeap*[2];
        heaps[0] = _views!.Handle;
        heaps[1] = _samplers!.Handle;
        list->SetDescriptorHeaps(2, heaps);

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
        list->IASetPrimitiveTopology(
            Silk.NET.Core.Native.D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglelist);

        // --- the ground ---
        BindGround(list, _ground, &push);

        VertexBufferView* streams = stackalloc VertexBufferView[2];
        streams[0] = _vertices.AsVertices(VertexStride);
        IndexBufferView indices = _indices.AsIndices(sixteenBit: false);

        list->IASetVertexBuffers(0, 1, streams);
        list->IASetIndexBuffer(&indices);
        list->DrawIndexedInstanced((uint)_plan.Indices.Length, 1, 0, 0, 0);

        // --- the forest, as impostors ---
        if (_trees is not null && _treeInstances is not null && _plan.TreeCount > 0)
        {
            BindGround(list, _trees, &push);

            streams[0] = _treeVertices!.AsVertices(VertexStride);
            streams[1] = _treeInstances.AsVertices(InstanceStride);
            IndexBufferView shapes = _treeIndices!.AsIndices(sixteenBit: true);

            list->IASetVertexBuffers(0, 2, streams);
            list->IASetIndexBuffer(&shapes);

            for (int kind = 0; kind < _plan.Stands.Length; kind++)
            {
                if (_plan.Stands[kind].Count == 0)
                {
                    continue;
                }

                (uint firstIndex, int vertexOffset, uint indexCount) =
                    _plan.ImpostorRanges[kind];

                list->DrawIndexedInstanced(
                    indexCount, _plan.Stands[kind].Count,
                    firstIndex, vertexOffset, _plan.Stands[kind].First);
            }
        }

        // --- and the near band as real trees ---
        //
        // After the impostors rather than before, so the cheap pass has already put its
        // depth down and the alpha-tested cards — which are the expensive fragments here —
        // are rejected wherever a cone is already nearer.
        if (_models is not null && _modelInstances is not null && _plan.ModelCount > 0)
        {
            BindGround(list, _models, &push);

            streams[0] = _modelVertices!.AsVertices(TreeVertexStride);
            streams[1] = _modelInstances.AsVertices(InstanceStride);
            IndexBufferView corners = _modelIndices!.AsIndices(sixteenBit: false);

            list->IASetVertexBuffers(0, 2, streams);
            list->IASetIndexBuffer(&corners);

            int sheetViews = _models.Signature.ParameterFor(1);
            int sheetSamplers = _models.Signature.SamplerParameterFor(1);
            int bound = -1;

            for (int model = 0; model < _plan.Models.Length; model++)
            {
                if (_plan.ModelStands[model].Count == 0)
                {
                    continue;
                }

                foreach ((int sheet, uint firstIndex, uint indexCount) in
                    _plan.Models[model].Parts)
                {
                    if (sheet != bound && sheetViews >= 0)
                    {
                        list->SetGraphicsRootDescriptorTable(
                            (uint)sheetViews, _views.Gpu(GroundTextures + (uint)sheet));

                        if (sheetSamplers >= 0)
                        {
                            list->SetGraphicsRootDescriptorTable(
                                (uint)sheetSamplers,
                                _samplers.Gpu(GroundTextures + (uint)sheet));
                        }

                        bound = sheet;
                    }

                    list->DrawIndexedInstanced(
                        indexCount, _plan.ModelStands[model].Count, firstIndex,
                        _plan.Models[model].VertexOffset, _plan.ModelStands[model].First);
                }
            }
        }

        // --- the sky last, at the far plane, over exactly the pixels nothing claimed ---
        if (_sky is not null)
        {
            TerrainSkyConstants above = frame.Sky;

            list->SetGraphicsRootSignature(_sky.Signature.Handle);
            list->SetPipelineState(_sky.Handle);
            list->SetGraphicsRoot32BitConstants(
                (uint)_sky.Signature.PushConstantParameter,
                (uint)(Marshal.SizeOf<TerrainSkyConstants>() / sizeof(float)),
                &above,
                0);

            list->DrawInstanced(3, 1, 0, 0);
        }
    }

    /// <summary>Binds a pipeline and the six textures every backdrop stage but the sky reads.</summary>
    private void BindGround(
        ID3D12GraphicsCommandList4* list, D3D12Pipeline pipeline, TerrainConstants* push)
    {
        list->SetGraphicsRootSignature(pipeline.Signature.Handle);
        list->SetPipelineState(pipeline.Handle);

        int views = pipeline.Signature.ParameterFor(0);
        if (views >= 0)
        {
            list->SetGraphicsRootDescriptorTable((uint)views, _views!.Gpu(0));
        }

        int samplers = pipeline.Signature.SamplerParameterFor(0);
        if (samplers >= 0)
        {
            list->SetGraphicsRootDescriptorTable((uint)samplers, _samplers!.Gpu(0));
        }

        list->SetGraphicsRoot32BitConstants(
            (uint)pipeline.Signature.PushConstantParameter,
            (uint)(Marshal.SizeOf<TerrainConstants>() / sizeof(float)),
            push,
            0);
    }

    /// <summary>
    /// Puts the four tiles, the splat weights and the vista's tint onto the device.
    /// </summary>
    /// <remarks>
    /// <b>All six carry a mip chain, and the last two are why the ridges used to crawl.</b>
    /// A thousand-cell splat map is stretched over a kilometre and a half of terrain, so a
    /// mountain at the far edge of it puts twenty cells inside one pixel. Sampled from the
    /// top level with no chain to fall back on, that pixel takes whichever cell it happens
    /// to land in — rock here, forest at the neighbouring pixel, rock again at the next —
    /// and a hillside a kilometre away comes out as a shimmering grey-and-green weave that
    /// moves with the camera.
    /// </remarks>
    private void UploadTextures(TerrainBackdrop backdrop)
    {
        _textures[0] = D3D12TextureUpload.Create(_context, backdrop.TileForest);
        _textures[1] = D3D12TextureUpload.Create(_context, backdrop.TileRock);
        _textures[2] = D3D12TextureUpload.Create(_context, backdrop.TileGrass);
        _textures[3] = D3D12TextureUpload.Create(_context, backdrop.TileDirt);

        // The splat is data and must not be sRGB-decoded; the tint is colour. Blocks where
        // the pack holds them, which is the same picture with its chain already built and no
        // PNG decode in front of it — the linear and sRGB choice moves into the block format
        // there, so it is stated once either way.
        _textures[4] = backdrop.SplatBlocks is { } splat
            ? D3D12TextureUpload.Create(_context, splat)
            : D3D12TextureUpload.Create(_context, backdrop.Splat, mipmaps: true, linear: true);

        _textures[5] = backdrop.TintBlocks is { } tint
            ? D3D12TextureUpload.Create(_context, tint)
            : D3D12TextureUpload.Create(_context, backdrop.Tint, mipmaps: true);
    }

    /// <summary>Puts the ground the plan worked out onto the device.</summary>
    private void UploadMesh()
    {
        _vertices = D3D12Buffer.CreateDeviceLocal<TerrainVertex>(
            _context, _plan.Vertices, ResourceStates.VertexAndConstantBuffer);
        _indices = D3D12Buffer.CreateDeviceLocal<uint>(
            _context, _plan.Indices, ResourceStates.IndexBuffer);
    }

    /// <summary>Puts the impostor shapes and the whole forest onto the device.</summary>
    private void UploadTrees()
    {
        if (_plan.TreeCount == 0)
        {
            return;
        }

        _treeVertices = D3D12Buffer.CreateDeviceLocal<TerrainVertex>(
            _context, _plan.TreeVertices, ResourceStates.VertexAndConstantBuffer);
        _treeIndices = D3D12Buffer.CreateDeviceLocal<ushort>(
            _context, _plan.TreeIndices, ResourceStates.IndexBuffer);
        _treeInstances = D3D12Buffer.CreateDeviceLocal<float>(
            _context, _plan.TreeInstances, ResourceStates.VertexAndConstantBuffer);
    }

    /// <summary>
    /// Puts the modelled trees, and the sheets they are painted with, onto the device.
    /// </summary>
    private void UploadTreeModels(TerrainBackdrop backdrop)
    {
        if (_plan.Models.Length == 0)
        {
            return;
        }

        foreach (DecodedImage image in backdrop.TreeTextures)
        {
            _sheets.Add(D3D12TextureUpload.Create(_context, image));
        }

        _modelVertices = D3D12Buffer.CreateDeviceLocal<TerrainTreeVertex>(
            _context, _plan.ModelVertices, ResourceStates.VertexAndConstantBuffer);
        _modelIndices = D3D12Buffer.CreateDeviceLocal<uint>(
            _context, _plan.ModelIndices, ResourceStates.IndexBuffer);

        // Rewritten every time the near band is reselected, which is why it is host-visible
        // and sized for the widest band the budget could ever ask for.
        _modelInstances = D3D12Buffer.CreateHostVisible(
            _context, (ulong)(_plan.ModelInstanceData.Length * sizeof(float)));
    }

    /// <summary>Writes every descriptor the pass will bind, once.</summary>
    /// <remarks>
    /// The six ground textures first, then one per tree sheet, in both heaps and at the same
    /// indices — which is what makes a sheet's whole binding one number.
    /// </remarks>
    private void Describe()
    {
        uint count = GroundTextures + (uint)_sheets.Count;

        _views = D3D12DescriptorHeap.Create(
            _context.Device, DescriptorHeapType.CbvSrvUav, count, shaderVisible: true);
        _samplers = D3D12DescriptorHeap.Create(
            _context.Device, DescriptorHeapType.Sampler, count, shaderVisible: true);

        _shared = D3D12Samplers.Create(_context);

        for (uint i = 0; i < GroundTextures; i++)
        {
            _textures[i]!.Describe(_context, _views.Cpu(_views.Allocate()));

            // The tiles repeat over kilometres of ground; the splat and the tint are one
            // map stretched over the whole grid, and a repeat on either would wrap the far
            // edge of the terrain onto the near one.
            _shared.CopyInto(
                _context,
                i < 4 ? SamplerAddressing.Repeat : SamplerAddressing.Clamp,
                _samplers.Cpu(_samplers.Allocate()));
        }

        foreach (D3D12Texture sheet in _sheets)
        {
            sheet.Describe(_context, _views.Cpu(_views.Allocate()));
            _shared.CopyInto(
                _context, SamplerAddressing.Repeat, _samplers.Cpu(_samplers.Allocate()));
        }
    }

    private void BuildPipelines(ShaderCompiler compiler, Format colorFormat, Format depthFormat)
    {
        var pushBytes = (uint)Marshal.SizeOf<TerrainConstants>();

        ShaderBinding[] ground =
        [
            .. Enumerable.Range(0, (int)GroundTextures).Select(
                i => new ShaderBinding(
                    0, (uint)i, ShaderBindingKind.CombinedImageSampler, ShaderStages.Fragment)),
        ];

        var groundLayout = new ShaderLayout(ground, pushBytes);

        // Terrain: one 24-byte stream of position and normal.
        VertexInput[] terrain =
        [
            new(0, Format.FormatR32G32B32Float, 0),
            new(1, Format.FormatR32G32B32Float, 12),
        ];

        _ground = D3D12Pipeline.CreateGraphics(
            _context.Device,
            compiler,
            TerrainShaders.Vertex,
            TerrainShaders.Fragment,
            "terrain",
            groundLayout,
            [colorFormat],
            depthFormat,
            terrain,
            [new VertexBufferLayout(VertexStride)],
            ShaderLanguage.Glsl,
            depthWrite: true,
            depthTest: true,
            depthEqual: true,
            cull: CullMode.None);

        // Trees: every impostor shape in stream 0, one 24-byte placement per instance in
        // stream 1. The shapes share a buffer and are drawn as ranges of it, so a hillside of
        // four species is four draws rather than four pipelines.
        VertexInput[] trees =
        [
            new(0, Format.FormatR32G32B32Float, 0),
            new(1, Format.FormatR32G32B32Float, 12),
            new(2, Format.FormatR32G32B32A32Float, 0, 1),
            new(3, Format.FormatR32Float, 16, 1),
            new(4, Format.FormatR32Float, 20, 1),
        ];

        VertexBufferLayout[] instanced =
        [
            new(VertexStride),
            new(InstanceStride, PerInstance: true),
        ];

        _trees = D3D12Pipeline.CreateGraphics(
            _context.Device,
            compiler,
            TerrainShaders.TreeVertex,
            TerrainShaders.TreeFragment,
            "horizon-trees",
            groundLayout,
            [colorFormat],
            depthFormat,
            trees,
            instanced,
            ShaderLanguage.Glsl,
            depthWrite: true,
            depthTest: true,
            depthEqual: true,
            cull: CullMode.None);

        // The modelled trees of the near band. Two descriptor sets rather than one: the
        // splat and the tint they share with the ground, and the one sheet a part is painted
        // with, which changes per part.
        if (_plan.Models.Length > 0 && _sheets.Count > 0)
        {
            var modelLayout = new ShaderLayout(
                [
                    .. ground,
                    new ShaderBinding(
                        1, 0, ShaderBindingKind.CombinedImageSampler, ShaderStages.Fragment),
                ],
                pushBytes);

            VertexInput[] models =
            [
                new(0, Format.FormatR32G32B32Float, 0),
                new(1, Format.FormatR32G32B32Float, 12),
                new(2, Format.FormatR32G32Float, 24),
                new(3, Format.FormatR32G32B32A32Float, 0, 1),
                new(4, Format.FormatR32Float, 16, 1),
                new(5, Format.FormatR32Float, 20, 1),
            ];

            _models = D3D12Pipeline.CreateGraphics(
                _context.Device,
                compiler,
                TerrainShaders.TreeModelVertex,
                TerrainShaders.TreeModelFragment,
                "horizon-tree-model",
                modelLayout,
                [colorFormat],
                depthFormat,
                models,
                [new VertexBufferLayout(TreeVertexStride), instanced[1]],
                ShaderLanguage.Glsl,
                depthWrite: true,
                depthTest: true,
                depthEqual: true,
                cull: CullMode.None);
        }

        // The sky: no vertex input at all, and no depth writes — it must lose to everything
        // and stop nothing.
        _sky = D3D12Pipeline.CreateGraphics(
            _context.Device,
            compiler,
            TerrainShaders.SkyVertex,
            TerrainShaders.SkyFragment,
            "horizon-sky",
            new ShaderLayout([], (uint)Marshal.SizeOf<TerrainSkyConstants>()),
            [colorFormat],
            depthFormat,
            attributes: null,
            buffers: null,
            ShaderLanguage.Glsl,
            depthWrite: false,
            depthTest: true,
            depthEqual: true,
            cull: CullMode.None);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _sky?.Dispose();
        _models?.Dispose();
        _trees?.Dispose();
        _ground?.Dispose();

        _modelInstances?.Dispose();
        _modelIndices?.Dispose();
        _modelVertices?.Dispose();
        _treeInstances?.Dispose();
        _treeIndices?.Dispose();
        _treeVertices?.Dispose();
        _indices?.Dispose();
        _vertices?.Dispose();

        foreach (D3D12Texture sheet in _sheets)
        {
            sheet.Dispose();
        }

        _sheets.Clear();

        for (int i = 0; i < _textures.Length; i++)
        {
            _textures[i]?.Dispose();
            _textures[i] = null;
        }

        _shared?.Dispose();
        _samplers?.Dispose();
        _views?.Dispose();
    }
}
