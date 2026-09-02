// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Runtime.InteropServices;
using GK3Reborn.Rendering.Shaders;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;
using Silk.NET.Maths;

namespace GK3Reborn.Rendering.Direct3D12;

/// <summary>
/// Draws a room's smoke and embers over the finished picture, on Direct3D.
/// </summary>
/// <remarks>
/// The Vulkan pass's counterpart, from the same two shaders: see
/// <see cref="Vulkan.ParticlePipeline"/> for why it exists at all and
/// <see cref="ParticleShaders"/> for how one blend draws both smoke and sparks.
/// </remarks>
public sealed unsafe class D3D12ParticlePass : IDisposable
{
    private readonly D3D12Context _context;
    private readonly D3D12Pipeline _pipeline;
    private readonly D3D12Buffer[] _vertices;
    private int _slot;
    private int _count;
    private bool _disposed;

    private D3D12ParticlePass(
        D3D12Context context, D3D12Pipeline pipeline, D3D12Buffer[] vertices)
    {
        _context = context;
        _pipeline = pipeline;
        _vertices = vertices;
    }

    /// <summary>How many particles were written for this frame.</summary>
    public int Count => _count / ParticleVertex.Corners;

    /// <summary>Builds the pass.</summary>
    /// <param name="context">The device.</param>
    /// <param name="compiler">Where the shaders come from.</param>
    /// <param name="colorFormat">What the lit target it draws onto holds.</param>
    /// <param name="depthFormat">What the depth target it tests against holds.</param>
    /// <param name="frames">How many frames are in flight.</param>
    /// <returns>The pass.</returns>
    /// <exception cref="D3D12Exception">It could not be built.</exception>
    public static D3D12ParticlePass Create(
        D3D12Context context,
        ShaderCompiler compiler,
        Format colorFormat,
        Format depthFormat,
        int frames)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(compiler);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frames);

        var layout = new ShaderLayout([], (uint)Marshal.SizeOf<ParticleConstants>());

        D3D12Pipeline? pipeline = null;
        var buffers = new List<D3D12Buffer>();

        try
        {
            pipeline = D3D12Pipeline.CreateGraphics(
                context.Device,
                compiler,
                ParticleShaders.Vertex,
                ParticleShaders.Fragment,
                "particle",
                layout,
                [colorFormat],
                depthFormat,
                [
                    new VertexInput(0, Format.FormatR32G32B32A32Float, 0),
                    new VertexInput(1, Format.FormatR32G32B32A32Float, 16),
                    new VertexInput(2, Format.FormatR32G32B32A32Float, 32),
                ],
                [new VertexBufferLayout((uint)Marshal.SizeOf<ParticleVertex>())],
                ShaderLanguage.Glsl,

                // Tested and never written. A puff behind a wall is hidden by it; two puffs
                // in front of one another both draw, which is the whole point of blending
                // them, and a sprite that wrote depth would delete every sprite behind it.
                depthWrite: false,
                depthTest: true,
                depthEqual: true,

                // A sprite is turned to face the camera and has no back.
                cull: CullMode.None,
                blend: true,
                premultiplied: true);

            // One buffer per frame in flight. Rewriting the one the device is still reading
            // for an earlier frame is a cloud of smoke assembled out of two different
            // moments, which reads as the sprites tearing rather than as a bug.
            ulong bytes = (ulong)(ParticleVertex.Capacity * ParticleVertex.Corners *
                                  Marshal.SizeOf<ParticleVertex>());

            for (int i = 0; i < frames; i++)
            {
                buffers.Add(D3D12Buffer.CreateHostVisible(context, bytes));
            }

            return new D3D12ParticlePass(context, pipeline, [.. buffers]);
        }
        catch
        {
            foreach (D3D12Buffer buffer in buffers)
            {
                buffer.Dispose();
            }

            pipeline?.Dispose();
            throw;
        }
    }

    /// <summary>Turns a frame's particles into vertices, ready to draw.</summary>
    /// <param name="particles">The particles, furthest from the eye first.</param>
    /// <param name="frame">Which of the frames in flight is being recorded.</param>
    public void Prepare(IReadOnlyList<Particle> particles, int frame)
    {
        ArgumentNullException.ThrowIfNull(particles);

        _count = 0;
        _slot = ((frame % _vertices.Length) + _vertices.Length) % _vertices.Length;

        if (particles.Count == 0)
        {
            return;
        }

        var vertices = new ParticleVertex[ParticleVertex.Capacity * ParticleVertex.Corners];
        int written = ParticleVertex.Build(particles, vertices);

        if (written == 0)
        {
            return;
        }

        _vertices[_slot].Write<ParticleVertex>(vertices.AsSpan(0, written));
        _count = written;
    }

    /// <summary>Records the draw.</summary>
    /// <param name="list">Command list to record into.</param>
    /// <param name="target">Where the finished picture is.</param>
    /// <param name="depth">The depth the room left.</param>
    /// <param name="width">Viewport width in pixels.</param>
    /// <param name="height">Its height.</param>
    /// <param name="constants">The camera, as <see cref="ParticleShaders.Describe"/> gives it.</param>
    public void Record(
        ID3D12GraphicsCommandList4* list,
        CpuDescriptorHandle target,
        CpuDescriptorHandle depth,
        int width,
        int height,
        ParticleConstants constants)
    {
        if (list is null || _count == 0)
        {
            return;
        }

        list->SetGraphicsRootSignature(_pipeline.Signature.Handle);
        list->SetPipelineState(_pipeline.Handle);
        list->IASetPrimitiveTopology(
            Silk.NET.Core.Native.D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglelist);

        VertexBufferView vertices =
            _vertices[_slot].AsVertices((uint)Marshal.SizeOf<ParticleVertex>());

        list->IASetVertexBuffers(0, 1, &vertices);

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
        list->OMSetRenderTargets(1, &target, false, &depth);

        list->SetGraphicsRoot32BitConstants(
            (uint)_pipeline.Signature.PushConstantParameter,
            (uint)(Marshal.SizeOf<ParticleConstants>() / 4),
            &constants,
            0);

        list->DrawInstanced((uint)_count, 1, 0, 0);
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

        foreach (D3D12Buffer buffer in _vertices)
        {
            buffer.Dispose();
        }

        _pipeline.Dispose();
    }
}
