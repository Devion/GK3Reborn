// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using GK3Reborn.Rendering.Geometry;
using GK3Reborn.Rendering.Shaders;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;
using System.Numerics;
using System.Runtime.InteropServices;

namespace GK3Reborn.Rendering.Direct3D12;

/// <summary>Reflects the frame in whatever in it is smooth enough to reflect.</summary>
/// <remarks>
/// <para>
/// The Direct3D half of <c>Reflections</c>: a min-depth pyramid, then one ray a pixel
/// marched over it, then an average over frames so that a rough surface — which takes a
/// different sample each frame — settles rather than boils.
/// </para>
/// <para>
/// <b>The pyramid is transitioned a level at a time.</b> Building level <c>n</c> reads level
/// <c>n - 1</c> as a texture and writes level <c>n</c> as an unordered access view, and both
/// are subresources of one resource. Moving the whole resource would mean saying it is
/// readable and writable at once, which Direct3D has no state for; moving the two levels
/// separately says exactly what is true. Vulkan needs none of this because a storage image
/// stays in <c>General</c> and one memory barrier covers the lot.
/// </para>
/// </remarks>
public sealed unsafe class D3D12Reflections : IDisposable
{
    private readonly D3D12Context _context;
    private readonly int _width;
    private readonly int _height;

    private readonly D3D12Pipeline _downsample;
    private readonly D3D12Pipeline _march;

    private readonly D3D12DescriptorHeap _views;
    private readonly D3D12DescriptorHeap _samplers;
    private readonly D3D12Samplers _shared;

    private readonly D3D12Buffer _uniform;
    private readonly D3D12Texture _pyramid;
    private readonly D3D12Texture[] _reflected;

    private readonly uint[] _tables;

    /// <summary>What state each level of the pyramid is in.</summary>
    /// <remarks>
    /// Kept here rather than on the texture, because the texture tracks one state for the
    /// whole resource and the whole point of the pyramid is that its levels differ.
    /// </remarks>
    private readonly ResourceStates[] _levels = new ResourceStates[ReflectLayout.Levels];

    private int _frame;
    private bool _disposed;

    private D3D12Reflections(
        D3D12Context context,
        int width,
        int height,
        D3D12Pipeline downsample,
        D3D12Pipeline march,
        D3D12DescriptorHeap views,
        D3D12DescriptorHeap samplers,
        D3D12Samplers shared,
        D3D12Buffer uniform,
        D3D12Texture pyramid,
        D3D12Texture[] reflected,
        uint[] tables)
    {
        _context = context;
        _width = width;
        _height = height;
        _downsample = downsample;
        _march = march;
        _views = views;
        _samplers = samplers;
        _shared = shared;
        _uniform = uniform;
        _pyramid = pyramid;
        _reflected = reflected;
        _tables = tables;

        Array.Fill(_levels, ResourceStates.UnorderedAccess);
    }

    /// <summary>Which of the two targets this frame's answer landed in.</summary>
    public int Parity => _frame & 1;

    /// <summary>What the frame reflects, as the composite reads it.</summary>
    public D3D12Texture Reflected => _reflected[Parity];

    /// <summary>Builds both stages and everything they read and write.</summary>
    /// <param name="context">The device.</param>
    /// <param name="compiler">Where the shaders come from.</param>
    /// <param name="width">Viewport width in pixels.</param>
    /// <param name="height">Viewport height in pixels.</param>
    /// <returns>The pass.</returns>
    /// <exception cref="D3D12Exception">A stage could not be built.</exception>
    public static D3D12Reflections Create(
        D3D12Context context, ShaderCompiler compiler, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(compiler);

        D3D12Pipeline? downsample = null;
        D3D12Pipeline? march = null;
        D3D12DescriptorHeap? views = null;
        D3D12DescriptorHeap? samplers = null;
        D3D12Samplers? shared = null;
        D3D12Buffer? uniform = null;
        D3D12Texture? pyramid = null;
        var reflected = new D3D12Texture[2];

        try
        {
            downsample = D3D12Pipeline.CreateCompute(
                context,
                compiler,
                ReflectionShaders.ComposeDownsample(),
                "reflect.downsample",
                ReflectLayout.Bindings);

            march = D3D12Pipeline.CreateCompute(
                context,
                compiler,
                ReflectionShaders.ComposeMarch(),
                "reflect.march",
                ReflectLayout.Bindings);

            // One table for each level of the pyramid, and one for each parity of the march.
            uint perSet = downsample.Signature.ViewDescriptorCount;
            uint count = ReflectLayout.Levels + 2;

            views = D3D12DescriptorHeap.Create(
                context.Device, DescriptorHeapType.CbvSrvUav, count * perSet, shaderVisible: true);

            samplers = D3D12DescriptorHeap.Create(
                context.Device, DescriptorHeapType.Sampler, 1, shaderVisible: true);

            shared = D3D12Samplers.Create(context);
            shared.CopyInto(context, SamplerAddressing.Clamp, samplers.Cpu(samplers.Allocate()));

            // Rounded up: a constant buffer view is a multiple of 256 bytes whether the block
            // it describes is or not.
            uniform = D3D12Buffer.CreateHostVisible(
                context, D3D12Buffer.Align((ulong)Marshal.SizeOf<ReflectUniforms>()));

            pyramid = D3D12Texture.CreateStorage(
                context, Format.FormatR32Float, width, height, ReflectLayout.Levels);

            for (int i = 0; i < 2; i++)
            {
                reflected[i] = D3D12Texture.CreateStorage(
                    context, Format.FormatR16G16B16A16Float, width, height);
            }

            var tables = new uint[count];

            for (int i = 0; i < count; i++)
            {
                tables[i] = views.Allocate(perSet);
            }

            return new D3D12Reflections(
                context,
                width,
                height,
                downsample,
                march,
                views,
                samplers,
                shared,
                uniform,
                pyramid,
                reflected,
                tables);
        }
        catch
        {
            foreach (D3D12Texture? target in reflected)
            {
                target?.Dispose();
            }

            pyramid?.Dispose();
            uniform?.Dispose();
            shared?.Dispose();
            samplers?.Dispose();
            views?.Dispose();
            march?.Dispose();
            downsample?.Dispose();
            throw;
        }
    }

    /// <summary>Points both stages at the frame's targets.</summary>
    /// <param name="depth">The frame's depth.</param>
    /// <param name="normal">The frame's normals, with roughness in their alpha.</param>
    /// <param name="motion">The frame's motion vectors.</param>
    /// <param name="lit">The previous frame's finished picture.</param>
    public void Bind(
        D3D12Texture depth, D3D12Texture normal, D3D12Texture motion, D3D12Texture lit)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(depth);
        ArgumentNullException.ThrowIfNull(normal);
        ArgumentNullException.ThrowIfNull(motion);
        ArgumentNullException.ThrowIfNull(lit);

        D3D12RootSignature signature = _downsample.Signature;

        for (int i = 0; i < _tables.Length; i++)
        {
            uint table = _tables[i];

            CpuDescriptorHandle Slot(uint binding) =>
                _views.Cpu(table + signature.ViewOffset(0, binding));

            bool marching = i >= ReflectLayout.Levels;
            int parity = i - ReflectLayout.Levels;

            // While marching, the two targets take turns: one holds what the last frame
            // settled on, the other takes this frame's answer.
            D3D12Texture history = marching ? _reflected[1 - parity] : _reflected[0];
            D3D12Texture result = marching ? _reflected[parity] : _reflected[0];
            uint level = marching ? 0 : (uint)i;

            depth.Describe(_context, Slot(0));
            normal.Describe(_context, Slot(1));
            motion.Describe(_context, Slot(2));
            lit.Describe(_context, Slot(3));
            _pyramid.Describe(_context, Slot(4));
            history.Describe(_context, Slot(5));
            result.DescribeWrite(_context, Slot(7));
            _pyramid.DescribeWrite(_context, Slot(8), level);
            _uniform.DescribeConstants(_context, Slot(9));
        }
    }

    /// <summary>Records the pyramid and the march.</summary>
    /// <param name="list">Command list to record into.</param>
    /// <param name="camera">The camera the frame was drawn from.</param>
    /// <param name="depth">The frame's depth, which the first level is built from.</param>
    /// <param name="normal">The frame's normals.</param>
    /// <param name="motion">The frame's motion vectors.</param>
    /// <param name="lit">The previous frame's finished picture.</param>
    /// <param name="roughest">The roughest surface still worth a ray.</param>
    public void Record(
        ID3D12GraphicsCommandList4* list,
        Camera camera,
        D3D12Texture depth,
        D3D12Texture normal,
        D3D12Texture motion,
        D3D12Texture lit,
        float roughest)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(camera);

        float aspect = (float)_width / _height;
        Matrix4x4 projection = camera.Projection(aspect);
        Matrix4x4 viewProjection = camera.View * projection;

        Matrix4x4.Invert(projection, out Matrix4x4 inverseProjection);
        Matrix4x4.Invert(viewProjection, out Matrix4x4 inverseViewProjection);

        _frame++;

        _uniform.Write<ReflectUniforms>(
        [
            new ReflectUniforms(
                projection,
                inverseProjection,
                camera.View,
                inverseViewProjection,
                new Vector4(camera.Position, (_frame % 64) * 0.61803398875f),
                _width,
                _height,
                1f / _width,
                1f / _height,
                new Vector4(ReflectLayout.Thickness, roughest, ReflectLayout.Levels, 0f)),
        ]);

        ID3D12DescriptorHeap** heaps = stackalloc ID3D12DescriptorHeap*[2];
        heaps[0] = _views.Handle;
        heaps[1] = _samplers.Handle;
        list->SetDescriptorHeaps(2, heaps);

        depth.Transition(list, ResourceStates.NonPixelShaderResource);
        normal.Transition(list, ResourceStates.NonPixelShaderResource);
        motion.Transition(list, ResourceStates.NonPixelShaderResource);
        lit.Transition(list, ResourceStates.NonPixelShaderResource);

        // --- the pyramid, coarsest last ---
        list->SetComputeRootSignature(_downsample.Signature.Handle);
        list->SetPipelineState(_downsample.Handle);

        for (int i = 0; i < ReflectLayout.Levels; i++)
        {
            int width = Math.Max(1, _width >> i);
            int height = Math.Max(1, _height >> i);

            // The level being written, and the one below it that is being read. Separately,
            // because they are subresources of one texture and no single state is both.
            SetLevel(list, i, ResourceStates.UnorderedAccess);

            if (i > 0)
            {
                SetLevel(list, i - 1, ResourceStates.NonPixelShaderResource);
            }

            BindTable(list, _downsample.Signature, _tables[i]);

            var level = new LevelConstants(width, height, i);
            list->SetComputeRoot32BitConstants(
                (uint)_downsample.Signature.PushConstantParameter,
                ReflectLayout.LevelConstantBytes / 4,
                &level,
                0);

            list->Dispatch((uint)Divide(width, 8), (uint)Divide(height, 8), 1);
            D3D12Context.Barrier(list, null);
        }

        // --- one ray a pixel over it ---
        for (int i = 0; i < ReflectLayout.Levels; i++)
        {
            SetLevel(list, i, ResourceStates.NonPixelShaderResource);
        }

        int parity = Parity;
        _reflected[1 - parity].Transition(list, ResourceStates.NonPixelShaderResource);
        _reflected[parity].Transition(list, ResourceStates.UnorderedAccess);

        list->SetComputeRootSignature(_march.Signature.Handle);
        list->SetPipelineState(_march.Handle);
        BindTable(list, _march.Signature, _tables[ReflectLayout.Levels + parity]);

        // The march takes no push constants of its own — it reads the uniform block — but the
        // root signature declares them because both stages share one layout, and a root
        // parameter left unset is undefined rather than zero.
        var unused = new LevelConstants(_width, _height, 0);
        list->SetComputeRoot32BitConstants(
            (uint)_march.Signature.PushConstantParameter,
            ReflectLayout.LevelConstantBytes / 4,
            &unused,
            0);

        list->Dispatch((uint)Divide(_width, 8), (uint)Divide(_height, 8), 1);
        D3D12Context.Barrier(list, null);

        // What the composite reads.
        _reflected[parity].Transition(list, ResourceStates.AllShaderResource);
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

        foreach (D3D12Texture target in _reflected)
        {
            target.Dispose();
        }

        _pyramid.Dispose();
        _uniform.Dispose();
        _shared.Dispose();
        _samplers.Dispose();
        _views.Dispose();
        _march.Dispose();
        _downsample.Dispose();
    }

    private static int Divide(int value, int divisor) => (value + divisor - 1) / divisor;

    private void SetLevel(ID3D12GraphicsCommandList4* list, int level, ResourceStates to)
    {
        D3D12Context.TransitionSubresource(
            list, _pyramid.Handle, _levels[level], to, (uint)level);

        _levels[level] = to;
    }

    private void BindTable(
        ID3D12GraphicsCommandList4* list, D3D12RootSignature signature, uint table)
    {
        list->SetComputeRootDescriptorTable((uint)signature.ParameterFor(0), _views.Gpu(table));

        int samplers = signature.SamplerParameterFor(0);
        if (samplers >= 0)
        {
            list->SetComputeRootDescriptorTable((uint)samplers, _samplers.Gpu(0));
        }
    }
}
