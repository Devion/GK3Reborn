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
using System.Numerics;

namespace GK3Reborn.Rendering.Direct3D12;

/// <summary>
/// Draws the sky behind everything the room left, on Direct3D.
/// </summary>
/// <remarks>
/// <para>
/// One triangle, a cube map, and the ray each pixel looks along worked out from the camera's
/// own basis. There is no cube of geometry and no inverse projection: the direction is the
/// same arithmetic the projection does, run forwards, because an inverse is a thing that can
/// be ill-conditioned or wrong in a way that is invisible until every pixel comes back with
/// the same answer.
/// </para>
/// <para>
/// <b>It writes depth at the far plane and does not test against it — it tests, but never
/// wins.</b> The sky is drawn after the room with the depth target still bound, so anything
/// the room drew is nearer and keeps its pixel; the sky fills only what the room left empty.
/// Drawing it first and letting the room overdraw it would work and would shade every sky
/// pixel twice.
/// </para>
/// </remarks>
public sealed unsafe class D3D12SkyboxPass : IDisposable
{
    private readonly D3D12Context _context;
    private readonly D3D12Pipeline _pipeline;
    private readonly D3D12DescriptorHeap _views;
    private readonly D3D12DescriptorHeap _samplers;
    private readonly D3D12Samplers _shared;
    private readonly D3D12Texture _cube;
    private bool _disposed;

    private D3D12SkyboxPass(
        D3D12Context context,
        D3D12Pipeline pipeline,
        D3D12DescriptorHeap views,
        D3D12DescriptorHeap samplers,
        D3D12Samplers shared,
        D3D12Texture cube,
        float azimuth)
    {
        _context = context;
        _pipeline = pipeline;
        _views = views;
        _samplers = samplers;
        _shared = shared;
        _cube = cube;
        Azimuth = azimuth;
    }

    /// <summary>Which way round the sky is turned, in radians about the vertical.</summary>
    public float Azimuth { get; }

    /// <summary>Builds the pass for one scene's sky.</summary>
    /// <param name="context">The device.</param>
    /// <param name="compiler">Where the shaders come from.</param>
    /// <param name="colorFormat">What the target it draws onto holds.</param>
    /// <param name="depthFormat">What the depth target holds.</param>
    /// <param name="faces">The six sides: right, left, up, down, front, back.</param>
    /// <param name="azimuth">How far the sky is turned, in radians.</param>
    /// <returns>The pass.</returns>
    /// <exception cref="D3D12Exception">It could not be built.</exception>
    public static D3D12SkyboxPass Create(
        D3D12Context context,
        ShaderCompiler compiler,
        Format colorFormat,
        Format depthFormat,
        IReadOnlyList<DecodedImage> faces,
        float azimuth)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(compiler);

        var layout = new ShaderLayout(
            [new ShaderBinding(0, 0, ShaderBindingKind.CombinedImageSampler, ShaderStages.Fragment)],
            PushConstantBytes: 64);

        D3D12Pipeline? pipeline = null;
        D3D12DescriptorHeap? views = null;
        D3D12DescriptorHeap? samplers = null;
        D3D12Samplers? shared = null;
        D3D12Texture? cube = null;

        try
        {
            pipeline = D3D12Pipeline.CreateGraphics(
                context.Device,
                compiler,
                SkyboxShaders.Vertex,
                SkyboxShaders.Fragment,
                "skybox",
                layout,
                [colorFormat],
                depthFormat,
                attributes: null,
                buffers: null,
                ShaderLanguage.Glsl,

                // Tested but never written. The sky is at the far plane, so it loses to
                // everything the room drew and there is nothing after it that could lose to
                // the sky in turn.
                //
                // And tested for equality as well, because "at the far plane" is exactly
                // where the depth buffer was cleared to: under a strict less-than every sky
                // fragment loses to the clear and the room draws against black.
                depthWrite: false,
                depthTest: true,
                depthEqual: true,
                cull: CullMode.None);

            views = D3D12DescriptorHeap.Create(
                context.Device, DescriptorHeapType.CbvSrvUav, 1, shaderVisible: true);

            samplers = D3D12DescriptorHeap.Create(
                context.Device, DescriptorHeapType.Sampler, 1, shaderVisible: true);

            shared = D3D12Samplers.Create(context);
            shared.CopyInto(context, SamplerAddressing.Clamp, samplers.Cpu(samplers.Allocate()));

            cube = D3D12TextureUpload.CreateCube(context, faces);
            cube.DescribeCube(context, views.Cpu(views.Allocate()));

            return new D3D12SkyboxPass(context, pipeline, views, samplers, shared, cube, azimuth);
        }
        catch
        {
            cube?.Dispose();
            shared?.Dispose();
            samplers?.Dispose();
            views?.Dispose();
            pipeline?.Dispose();
            throw;
        }
    }

    /// <summary>Records the sky.</summary>
    /// <param name="list">Command list to record into.</param>
    /// <param name="camera">Where it is seen from.</param>
    /// <param name="width">Width of the target in pixels.</param>
    /// <param name="height">Its height.</param>
    /// <remarks>
    /// Records into whatever render targets are already bound, so the caller keeps the room's
    /// own targets set. That is what lets the depth test do its work.
    /// </remarks>
    public void Record(ID3D12GraphicsCommandList4* list, Camera camera, int width, int height)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(camera);

        ID3D12DescriptorHeap** heaps = stackalloc ID3D12DescriptorHeap*[2];
        heaps[0] = _views.Handle;
        heaps[1] = _samplers.Handle;
        list->SetDescriptorHeaps(2, heaps);

        list->SetGraphicsRootSignature(_pipeline.Signature.Handle);
        list->SetPipelineState(_pipeline.Handle);
        list->IASetPrimitiveTopology(
            Silk.NET.Core.Native.D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglelist);

        list->SetGraphicsRootDescriptorTable(
            (uint)_pipeline.Signature.ParameterFor(0), _views.Gpu(0));

        int samplers = _pipeline.Signature.SamplerParameterFor(0);
        if (samplers >= 0)
        {
            list->SetGraphicsRootDescriptorTable((uint)samplers, _samplers.Gpu(0));
        }

        SkyboxConstants block = SkyboxShaders.Describe(camera, Azimuth, width, height);

        list->SetGraphicsRoot32BitConstants(
            (uint)_pipeline.Signature.PushConstantParameter, 16, &block, 0);

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
        _context.Wait();

        _cube.Dispose();
        _shared.Dispose();
        _samplers.Dispose();
        _views.Dispose();
        _pipeline.Dispose();
    }
}
