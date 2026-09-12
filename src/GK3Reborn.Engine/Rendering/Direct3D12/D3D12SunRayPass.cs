// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Runtime.InteropServices;
using GK3Reborn.Rendering.Geometry;
using GK3Reborn.Rendering.Shaders;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;
using Silk.NET.Maths;

namespace GK3Reborn.Rendering.Direct3D12;

/// <summary>
/// Draws the sun's rays over the finished picture, on Direct3D.
/// </summary>
public sealed unsafe class D3D12SunRayPass : IDisposable
{
    /// <summary>How many descriptors one draw's table holds: the depth and the shafts.</summary>
    private const uint TableSize = 2;

    /// <summary>How many frames of descriptors the ring holds.</summary>
    private const uint RingDepth = 3;

    private readonly D3D12Context _context;
    private readonly D3D12Pipeline _pipeline;
    private readonly D3D12DescriptorHeap _views;
    private readonly D3D12DescriptorHeap _samplers;
    private readonly D3D12Samplers _shared;
    private readonly D3D12Buffer _shafts;
    private uint _ring;
    private bool _disposed;

    private D3D12SunRayPass(
        D3D12Context context,
        D3D12Pipeline pipeline,
        D3D12DescriptorHeap views,
        D3D12DescriptorHeap samplers,
        D3D12Samplers shared,
        D3D12Buffer shafts)
    {
        _context = context;
        _pipeline = pipeline;
        _views = views;
        _samplers = samplers;
        _shared = shared;
        _shafts = shafts;
    }

    /// <summary>Gives the pass the room's shafts of daylight.</summary>
    /// <param name="shafts">The shafts, or none.</param>
    public void Shafts(IReadOnlyList<LightShaft> shafts)
    {
        ArgumentNullException.ThrowIfNull(shafts);
        ObjectDisposedException.ThrowIf(_disposed, this);

        _shafts.Write<byte>(SunRayLayout.PackShafts(shafts));
    }

    /// <summary>Builds the pass.</summary>
    /// <param name="context">The device.</param>
    /// <param name="compiler">Where the shaders come from.</param>
    /// <param name="colorFormat">What the lit target it draws onto holds.</param>
    /// <returns>The pass.</returns>
    /// <exception cref="D3D12Exception">It could not be built.</exception>
    public static D3D12SunRayPass Create(
        D3D12Context context, ShaderCompiler compiler, Format colorFormat)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(compiler);

        D3D12DescriptorHeap? views = null;
        D3D12DescriptorHeap? samplers = null;
        D3D12Samplers? shared = null;
        D3D12Pipeline? pipeline = null;
        D3D12Buffer? shafts = null;

        try
        {
            shafts = D3D12Buffer.CreateHostVisible(context, (ulong)SunRayLayout.ShaftBufferBytes);
            shafts.Write<byte>(SunRayLayout.PackShafts([]));

            views = D3D12DescriptorHeap.Create(
                context.Device, DescriptorHeapType.CbvSrvUav, TableSize * RingDepth,
                shaderVisible: true);

            samplers = D3D12DescriptorHeap.Create(
                context.Device, DescriptorHeapType.Sampler, 1, shaderVisible: true);

            shared = D3D12Samplers.Create(context);

            pipeline = D3D12Pipeline.CreateGraphics(
                context.Device,
                compiler,
                CompositeShaders.Vertex,
                SunRayShaders.Fragment,
                "sunrays",
                SunRayLayout.Bindings,
                [colorFormat],

                // No depth attachment: the depth is what this pass reads, not what it tests
                // against.
                Format.FormatUnknown,
                attributes: null,
                buffers: null,
                ShaderLanguage.Glsl,
                depthWrite: false,
                depthTest: false,
                depthEqual: false,
                cull: CullMode.None,

                // Premultiplied, and what the shader writes has alpha nought: light is added
                // to the picture and nothing behind it is taken away.
                blend: true,
                premultiplied: true);

            var pass = new D3D12SunRayPass(context, pipeline, views, samplers, shared, shafts);
            pass.WriteSampler();

            return pass;
        }
        catch
        {
            pipeline?.Dispose();
            shared?.Dispose();
            samplers?.Dispose();
            views?.Dispose();
            shafts?.Dispose();
            throw;
        }
    }

    /// <summary>Records the draw.</summary>
    /// <param name="list">Command list to record into.</param>
    /// <param name="target">Where the finished picture is.</param>
    /// <param name="depth">The depth the room left, already in a shader-readable state.</param>
    /// <param name="width">Viewport width in pixels.</param>
    /// <param name="height">Its height.</param>
    /// <param name="constants">What the walk is told.</param>
    public void Record(
        ID3D12GraphicsCommandList4* list,
        CpuDescriptorHandle target,
        D3D12Texture depth,
        int width,
        int height,
        in SunRayConstants constants)
    {
        ArgumentNullException.ThrowIfNull(depth);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (list is null)
        {
            return;
        }

        // The next run in the ring. Written now and read for the life of the frame, which is
        // why the ring is deeper than one.
        uint first = _ring * TableSize;
        _ring = (_ring + 1) % RingDepth;

        depth.Describe(_context, _views.Cpu(first));
        _shafts.DescribeRead(_context, _views.Cpu(first + 1));

        ID3D12DescriptorHeap** heaps = stackalloc ID3D12DescriptorHeap*[2];
        heaps[0] = _views.Handle;
        heaps[1] = _samplers.Handle;
        list->SetDescriptorHeaps(2, heaps);

        list->SetGraphicsRootSignature(_pipeline.Signature.Handle);
        list->SetPipelineState(_pipeline.Handle);
        list->IASetPrimitiveTopology(
            Silk.NET.Core.Native.D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglelist);

        list->SetGraphicsRootDescriptorTable(
            (uint)_pipeline.Signature.ParameterFor(SunRayLayout.RaySet), _views.Gpu(first));

        int sampler = _pipeline.Signature.SamplerParameterFor(SunRayLayout.RaySet);
        if (sampler >= 0)
        {
            list->SetGraphicsRootDescriptorTable((uint)sampler, _samplers.Gpu(0));
        }

        fixed (SunRayConstants* pushed = &constants)
        {
            list->SetGraphicsRoot32BitConstants(
                (uint)_pipeline.Signature.PushConstantParameter,
                (uint)(Marshal.SizeOf<SunRayConstants>() / 4),
                pushed,
                0);
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

        _pipeline.Dispose();
        _shared.Dispose();
        _samplers.Dispose();
        _views.Dispose();
        _shafts.Dispose();
    }

    private void WriteSampler()
    {
        uint slot = _samplers.Allocate();

        // Clamped. The depth is read across its own extent, and a wrapped fetch at an edge
        // would take the distance from the far side of the picture.
        _shared.CopyInto(_context, SamplerAddressing.Clamp, _samplers.Cpu(slot));
    }
}
