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
/// Marches a room's fog over the finished picture, on Direct3D.
/// </summary>
/// <remarks>
/// <para>
/// The Vulkan pass's counterpart, from the same two shaders: see
/// <see cref="Vulkan.FogPipeline"/> for what it draws and <see cref="FogShaders"/> for how.
/// </para>
/// <para>
/// <b>Not a <see cref="D3D12ScreenPass"/>, and the reason is the rig.</b> That class covers
/// every full-screen pass that reads nothing but textures, which is all of the others; this
/// one reads three buffers as well, so its table is written here. What is shared is the
/// layout and the shader, which is where two backends actually drift apart.
/// </para>
/// </remarks>
public sealed unsafe class D3D12FogPass : IDisposable
{
    /// <summary>How many descriptors one draw's table holds.</summary>
    /// <remarks>The rig, the cells, the list inside a cell, and the depth.</remarks>
    private const uint TableSize = 4;

    /// <summary>How many frames of descriptors the ring holds.</summary>
    /// <remarks>
    /// Three, for the reason <see cref="D3D12ScreenPass"/> gives: the run is written once a
    /// frame and read for as long as that frame is on the device, so it has to outlast the
    /// deepest thing that can still be reading it.
    /// </remarks>
    private const uint RingDepth = 3;

    private readonly D3D12Context _context;
    private readonly D3D12Pipeline _pipeline;
    private readonly D3D12DescriptorHeap _views;
    private readonly D3D12DescriptorHeap _samplers;
    private readonly D3D12Samplers _shared;
    private uint _ring;
    private bool _disposed;

    private D3D12FogPass(
        D3D12Context context,
        D3D12Pipeline pipeline,
        D3D12DescriptorHeap views,
        D3D12DescriptorHeap samplers,
        D3D12Samplers shared)
    {
        _context = context;
        _pipeline = pipeline;
        _views = views;
        _samplers = samplers;
        _shared = shared;
    }

    /// <summary>Builds the pass.</summary>
    /// <param name="context">The device.</param>
    /// <param name="compiler">Where the shaders come from.</param>
    /// <param name="colorFormat">What the lit target it draws onto holds.</param>
    /// <returns>The pass.</returns>
    /// <exception cref="D3D12Exception">It could not be built.</exception>
    public static D3D12FogPass Create(
        D3D12Context context, ShaderCompiler compiler, Format colorFormat)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(compiler);

        D3D12DescriptorHeap? views = null;
        D3D12DescriptorHeap? samplers = null;
        D3D12Samplers? shared = null;
        D3D12Pipeline? pipeline = null;

        try
        {
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
                FogShaders.Fragment,
                "fog",
                FogLayout.Bindings,
                [colorFormat],

                // No depth attachment. The depth is what this pass reads, not what it tests
                // against: the march has already stopped at whatever the room drew.
                Format.FormatUnknown,
                attributes: null,
                buffers: null,
                ShaderLanguage.Glsl,
                depthWrite: false,
                depthTest: false,
                depthEqual: false,

                // One triangle, and which way it faces is whichever way the vertex index
                // happened to wind it.
                cull: CullMode.None,

                // Premultiplied: what the shader writes is already weighted by how much of
                // it survives to the eye, and alpha is how much of the picture behind it
                // does not.
                blend: true,
                premultiplied: true);

            var pass = new D3D12FogPass(context, pipeline, views, samplers, shared);
            pass.WriteSampler();

            return pass;
        }
        catch
        {
            pipeline?.Dispose();
            shared?.Dispose();
            samplers?.Dispose();
            views?.Dispose();
            throw;
        }
    }

    /// <summary>Records the draw.</summary>
    /// <param name="list">Command list to record into.</param>
    /// <param name="target">Where the finished picture is.</param>
    /// <param name="frames">The set holding the rig the fog is lit by.</param>
    /// <param name="frame">Which frame in flight is being recorded.</param>
    /// <param name="depth">The depth the room left, already in a shader-readable state.</param>
    /// <param name="width">Viewport width in pixels.</param>
    /// <param name="height">Its height.</param>
    /// <param name="constants">What the march is told.</param>
    public void Record(
        ID3D12GraphicsCommandList4* list,
        CpuDescriptorHandle target,
        D3D12FrameSet frames,
        int frame,
        D3D12Texture depth,
        int width,
        int height,
        in FogConstants constants)
    {
        ArgumentNullException.ThrowIfNull(frames);
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

        frames.Rig(frame).DescribeRead(_context, _views.Cpu(first));
        frames.Cells(frame).DescribeRead(_context, _views.Cpu(first + 1));
        frames.Reaching(frame).DescribeRead(_context, _views.Cpu(first + 2));
        depth.Describe(_context, _views.Cpu(first + 3));

        ID3D12DescriptorHeap** heaps = stackalloc ID3D12DescriptorHeap*[2];
        heaps[0] = _views.Handle;
        heaps[1] = _samplers.Handle;
        list->SetDescriptorHeaps(2, heaps);

        list->SetGraphicsRootSignature(_pipeline.Signature.Handle);
        list->SetPipelineState(_pipeline.Handle);
        list->IASetPrimitiveTopology(
            Silk.NET.Core.Native.D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglelist);

        list->SetGraphicsRootDescriptorTable(
            (uint)_pipeline.Signature.ParameterFor(FogLayout.FogSet), _views.Gpu(first));

        int sampler = _pipeline.Signature.SamplerParameterFor(FogLayout.FogSet);
        if (sampler >= 0)
        {
            list->SetGraphicsRootDescriptorTable((uint)sampler, _samplers.Gpu(0));
        }

        fixed (FogConstants* pushed = &constants)
        {
            list->SetGraphicsRoot32BitConstants(
                (uint)_pipeline.Signature.PushConstantParameter,
                (uint)(Marshal.SizeOf<FogConstants>() / 4),
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
    }

    private void WriteSampler()
    {
        uint slot = _samplers.Allocate();

        // Clamped. The depth is read across its own extent, and a wrapped fetch at an edge
        // would take the distance from the far side of the picture.
        _shared.CopyInto(_context, SamplerAddressing.Clamp, _samplers.Cpu(slot));
    }
}
