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

/// <summary>Traces occlusion once a pixel and filters it into something smooth.</summary>
/// <remarks>
/// <para>
/// The Direct3D half of <c>ShadowDenoiser</c>. Same three shaders, same five stages, same
/// three channels; what differs is entirely bookkeeping, and it differs in two ways worth
/// knowing about.
/// </para>
/// <para>
/// <b>Descriptors live in one heap and are addressed by table offset, not by binding.</b>
/// Vulkan writes a descriptor to a binding number; Direct3D writes it to a slot in a
/// contiguous run, and the run packs the bindings in order with the samplers taken out. The
/// denoising layout has its sampler at binding seven of sixteen, so every binding above it
/// sits one slot earlier than its number. <see cref="D3D12RootSignature.ViewOffset"/> is
/// asked rather than counted, because getting it wrong is not an error anywhere — it is a
/// shader reading the wrong texture and a picture that is merely odd.
/// </para>
/// <para>
/// <b>A target is in one state at a time, so the stages transition rather than barrier.</b>
/// Vulkan leaves all of these in <c>General</c> and separates the stages with a memory
/// barrier. Direct3D has no state that is both readable as a texture and writable as an
/// unordered access view, and the scratch targets are read by one stage and written by the
/// next: the classify pass writes the first and the blurs read it. So each stage is preceded
/// by the transitions it needs. The one thing this relies on is that no single dispatch both
/// reads and writes the same target — which is true, and is why the blurs alternate between
/// two scratch targets rather than filtering in place.
/// </para>
/// </remarks>
public sealed unsafe class D3D12ShadowDenoiser : IDisposable
{
    /// <summary>How many descriptor sets each channel needs.</summary>
    /// <remarks>Two reprojections, one per parity of the moments, and three blurs.</remarks>
    private const uint SetsPerChannel = 5;

    private readonly D3D12Context _context;
    private readonly int _width;
    private readonly int _height;

    private readonly D3D12Pipeline _trace;
    private readonly D3D12Pipeline _classify;
    private readonly D3D12Pipeline _filter;

    private readonly D3D12DescriptorHeap _views;
    private readonly D3D12DescriptorHeap _samplers;
    private readonly D3D12Samplers _shared;

    private readonly Channel[] _channels;
    private readonly D3D12Buffer _uniform;
    private readonly D3D12Texture _previousDepth;

    private readonly uint _traceTable;

    private ulong _structure;
    private Matrix4x4? _previousViewProjection;
    private int _frame;
    private bool _first = true;
    private bool _disposed;

    private D3D12ShadowDenoiser(
        D3D12Context context,
        int width,
        int height,
        D3D12Pipeline trace,
        D3D12Pipeline classify,
        D3D12Pipeline filter,
        D3D12DescriptorHeap views,
        D3D12DescriptorHeap samplers,
        D3D12Samplers shared,
        Channel[] channels,
        D3D12Buffer uniform,
        D3D12Texture previousDepth,
        uint traceTable)
    {
        _context = context;
        _width = width;
        _height = height;
        _trace = trace;
        _classify = classify;
        _filter = filter;
        _views = views;
        _samplers = samplers;
        _shared = shared;
        _channels = channels;
        _uniform = uniform;
        _previousDepth = previousDepth;
        _traceTable = traceTable;
    }

    /// <summary>The denoised fraction of the direct light that reaches each pixel.</summary>
    public D3D12Texture Shadow => _channels[0].Result;

    /// <summary>The denoised fraction of the hemisphere each pixel can see.</summary>
    public D3D12Texture Occlusion => _channels[1].Result;

    /// <summary>
    /// The denoised fraction of the direct light that the things standing in the room —
    /// characters and props — leave alone.
    /// </summary>
    /// <remarks>
    /// One where nothing is in the way, which is every pixel of a room with nobody in it, so
    /// such a room composites exactly as it did before this existed.
    /// </remarks>
    public D3D12Texture DynamicShadow => _channels[2].Result;

    /// <summary>Builds every stage and every target, for one viewport size.</summary>
    /// <param name="context">The device.</param>
    /// <param name="compiler">Where the shaders come from.</param>
    /// <param name="width">Viewport width in pixels.</param>
    /// <param name="height">Viewport height in pixels.</param>
    /// <returns>The denoiser, or null if the device cannot trace rays.</returns>
    /// <exception cref="D3D12Exception">A stage could not be built.</exception>
    public static D3D12ShadowDenoiser? Create(
        D3D12Context context, ShaderCompiler compiler, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(compiler);

        if (!context.SupportsRayTracing || width <= 0 || height <= 0)
        {
            return null;
        }

        D3D12Pipeline? trace = null;
        D3D12Pipeline? classify = null;
        D3D12Pipeline? filter = null;
        D3D12DescriptorHeap? views = null;
        D3D12DescriptorHeap? samplers = null;
        D3D12Samplers? shared = null;
        D3D12Buffer? uniform = null;
        D3D12Texture? previous = null;
        Channel[] channels = [];

        try
        {
            trace = D3D12Pipeline.CreateCompute(
                context, compiler, DenoiserShaders.ComposeTrace(), "shadow.trace", DenoiseLayout.Trace);

            classify = D3D12Pipeline.CreateCompute(
                context,
                compiler,
                DenoiserShaders.ComposeClassify(),
                "shadow.classify",
                DenoiseLayout.Denoise);

            filter = D3D12Pipeline.CreateCompute(
                context,
                compiler,
                DenoiserShaders.ComposeFilter(),
                "shadow.filter",
                DenoiseLayout.Denoise);

            // The trace table and five tables for each of the three channels, each run
            // contiguous because a descriptor table is addressed by where it starts.
            uint perSet = classify.Signature.ViewDescriptorCount;
            uint total = trace.Signature.ViewDescriptorCount +
                ((uint)DenoiseLayout.Channels * SetsPerChannel * perSet);

            views = D3D12DescriptorHeap.Create(
                context.Device, DescriptorHeapType.CbvSrvUav, total, shaderVisible: true);

            // One sampler, shared by all fifteen denoising tables. They all want the same
            // thing — linear and clamped — and a sampler table is bound by where it starts,
            // so fifteen copies of one descriptor would be fifteen ways to write one handle.
            samplers = D3D12DescriptorHeap.Create(
                context.Device, DescriptorHeapType.Sampler, 1, shaderVisible: true);

            shared = D3D12Samplers.Create(context);
            shared.CopyInto(context, SamplerAddressing.Clamp, samplers.Cpu(samplers.Allocate()));

            // Rounded up, because a constant buffer view is a multiple of 256 bytes whether
            // the block is or not. The uniforms are 240, so a view of the whole thing runs
            // sixteen bytes past the end of a resource sized to fit them exactly.
            uniform = D3D12Buffer.CreateHostVisible(
                context, D3D12Buffer.Align((ulong)Marshal.SizeOf<DenoiseUniforms>()));

            // Created exactly as the frame's depth is, because the two are copied one to the
            // other and a whole-resource copy wants identical descriptions.
            previous = D3D12Texture.CreateDepthTarget(
                context, GBufferFormats.Depth, width, height, sampled: true);

            int tiles = Tiles(width, height);

            channels =
            [
                Channel.Create(context, width, height, tiles),
                Channel.Create(context, width, height, tiles),
                Channel.Create(context, width, height, tiles),
            ];

            uint traceTable = views.Allocate(trace.Signature.ViewDescriptorCount);

            foreach (Channel channel in channels)
            {
                channel.Tables = new uint[SetsPerChannel];

                for (int i = 0; i < SetsPerChannel; i++)
                {
                    channel.Tables[i] = views.Allocate(perSet);
                }
            }

            return new D3D12ShadowDenoiser(
                context,
                width,
                height,
                trace,
                classify,
                filter,
                views,
                samplers,
                shared,
                channels,
                uniform,
                previous,
                traceTable);
        }
        catch
        {
            foreach (Channel channel in channels)
            {
                channel.Dispose();
            }

            previous?.Dispose();
            uniform?.Dispose();
            shared?.Dispose();
            samplers?.Dispose();
            views?.Dispose();
            filter?.Dispose();
            classify?.Dispose();
            trace?.Dispose();
            throw;
        }
    }

    /// <summary>Points every stage at the frame's targets.</summary>
    /// <param name="depth">The frame's depth target.</param>
    /// <param name="normal">The frame's normals.</param>
    /// <param name="motion">The frame's motion vectors.</param>
    /// <param name="structure">The scene's acceleration structure.</param>
    /// <param name="rig">The buffer of lights.</param>
    /// <remarks>
    /// Once for a set of targets rather than once a frame: nothing here changes between
    /// frames except the contents, and the moments swap by having two sets of descriptors
    /// rather than by rewriting one.
    /// </remarks>
    public void Bind(
        D3D12Texture depth,
        D3D12Texture normal,
        D3D12Texture motion,
        D3D12AccelerationStructure structure,
        D3D12Buffer rig)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(depth);
        ArgumentNullException.ThrowIfNull(normal);
        ArgumentNullException.ThrowIfNull(motion);
        ArgumentNullException.ThrowIfNull(structure);
        ArgumentNullException.ThrowIfNull(rig);

        _structure = structure.Address;

        D3D12RootSignature signature = _trace.Signature;

        CpuDescriptorHandle TraceSlot(uint binding) =>
            _views.Cpu(_traceTable + signature.ViewOffset(0, binding));

        depth.Describe(_context, TraceSlot(0));
        normal.Describe(_context, TraceSlot(1));
        structure.Describe(_context, TraceSlot(2));

        // A storage buffer nothing writes, so a read-only raw view. The rig outgrew a
        // constant block when the light limit went: a constant block is sized at compile
        // time and guaranteed only sixteen kilobytes, which is 255 lights and no more.
        rig.DescribeRead(_context, TraceSlot(5));

        for (int c = 0; c < _channels.Length; c++)
        {
            Channel channel = _channels[c];
            channel.Mask.DescribeWrite(_context, TraceSlot(DenoiseLayout.MaskBinding[c]));
            channel.Fraction.DescribeWrite(_context, TraceSlot(DenoiseLayout.FractionBinding[c]));

            for (int i = 0; i < SetsPerChannel; i++)
            {
                WriteDenoiseTable(channel, i, depth, normal, motion);
            }
        }
    }

    /// <summary>Points the tracing stage at a rebuilt acceleration structure.</summary>
    /// <param name="structure">The structure to trace against now.</param>
    /// <remarks>
    /// It is rebuilt whenever anything in the room moves, which means a new address and a
    /// stale descriptor — so this is checked every frame and does the one write when it has
    /// to.
    /// </remarks>
    public void Point(D3D12AccelerationStructure structure)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(structure);

        if (structure.Address == _structure)
        {
            return;
        }

        _structure = structure.Address;
        structure.Describe(_context, _views.Cpu(_traceTable + _trace.Signature.ViewOffset(0, 2)));
    }

    /// <summary>Records the trace and the five filtering stages.</summary>
    /// <param name="list">Command list to record into.</param>
    /// <param name="camera">The camera the frame was drawn from.</param>
    /// <param name="depth">The frame's depth target, to keep for next time.</param>
    /// <param name="normal">The frame's normals.</param>
    /// <param name="motion">The frame's motion vectors.</param>
    /// <param name="radius">How far an occlusion ray looks.</param>
    /// <param name="samples">How many rays each pixel spends on each signal.</param>
    public void Record(
        ID3D12GraphicsCommandList4* list,
        Camera camera,
        D3D12Texture depth,
        D3D12Texture normal,
        D3D12Texture motion,
        float radius,
        int samples)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(camera);
        ArgumentNullException.ThrowIfNull(depth);
        ArgumentNullException.ThrowIfNull(normal);
        ArgumentNullException.ThrowIfNull(motion);

        float aspect = (float)_width / _height;
        Matrix4x4 projection = camera.Projection(aspect);
        Matrix4x4 viewProjection = camera.View * projection;

        Matrix4x4.Invert(projection, out Matrix4x4 inverseProjection);
        Matrix4x4.Invert(viewProjection, out Matrix4x4 inverseViewProjection);

        Matrix4x4 previous = _previousViewProjection ?? viewProjection;

        _uniform.Write<DenoiseUniforms>(
        [
            new DenoiseUniforms(
                inverseProjection,

                // Where a pixel of this frame sat in the last one's clip space, which is what
                // tells a reprojection whether it is looking at the same surface.
                inverseViewProjection * previous,
                inverseViewProjection,
                new Vector4(camera.Position, _first ? 1f : 0f),
                _width,
                _height,
                1f / _width,
                1f / _height,

                // How far apart two depths may be before they stop being the same surface.
                // AMD's own default.
                new Vector4(0.01f, 0, 0, 0)),
        ]);

        _previousViewProjection = viewProjection;

        ID3D12DescriptorHeap** heaps = stackalloc ID3D12DescriptorHeap*[2];
        heaps[0] = _views.Handle;
        heaps[1] = _samplers.Handle;
        list->SetDescriptorHeaps(2, heaps);

        // --- the rays ---
        depth.Transition(list, ResourceStates.NonPixelShaderResource);
        normal.Transition(list, ResourceStates.NonPixelShaderResource);

        foreach (Channel channel in _channels)
        {
            channel.Fraction.Transition(list, ResourceStates.UnorderedAccess);
        }

        var push = new TraceConstants(
            inverseViewProjection,
            _width,
            _height,
            radius,

            // A different seed each frame. Grain that stands still cannot be averaged away,
            // and averaging it away is now somebody's job.
            (_frame % 64) * 0.61803398875f,
            Math.Max(samples, 1),
            0);

        list->SetComputeRootSignature(_trace.Signature.Handle);
        list->SetPipelineState(_trace.Handle);
        list->SetComputeRootDescriptorTable(
            (uint)_trace.Signature.ParameterFor(0), _views.Gpu(_traceTable));
        list->SetComputeRoot32BitConstants(
            (uint)_trace.Signature.PushConstantParameter,
            DenoiseLayout.TraceConstantBytes / 4,
            &push,
            0);

        list->Dispatch(
            (uint)Divide(_width, DenoiseLayout.TileWidth),
            (uint)Divide(_height, DenoiseLayout.TileHeight),
            1);

        D3D12Context.Barrier(list, null);

        // --- the filter ---
        //
        // One eight by eight group a tile. AMD dispatch twice as many rows as there are
        // groups and let the surplus write out of bounds; the addresses are the same either
        // way, so half of them are simply not launched here.
        uint groupsX = (uint)Divide(_width, 8);
        uint groupsY = (uint)Divide(_height, 8);

        motion.Transition(list, ResourceStates.NonPixelShaderResource);

        int parity = _frame & 1;

        foreach (Channel channel in _channels)
        {
            _previousDepth.Transition(list, ResourceStates.NonPixelShaderResource);

            // The reprojection reads the moments of the frame before and writes this one's,
            // so which of the two is which alternates.
            channel.Moments[parity].Transition(list, ResourceStates.NonPixelShaderResource);
            channel.Moments[1 - parity].Transition(list, ResourceStates.UnorderedAccess);
            channel.Fraction.Transition(list, ResourceStates.NonPixelShaderResource);
            channel.Scratch1.Transition(list, ResourceStates.NonPixelShaderResource);
            channel.Scratch0.Transition(list, ResourceStates.UnorderedAccess);

            list->SetComputeRootSignature(_classify.Signature.Handle);
            list->SetPipelineState(_classify.Handle);
            BindDenoiseTable(list, _classify.Signature, channel.Tables[parity]);

            var stage = new StageConstants(1, 0);
            list->SetComputeRoot32BitConstants(
                (uint)_classify.Signature.PushConstantParameter,
                DenoiseLayout.StageConstantBytes / 4,
                &stage,
                0);

            list->Dispatch(groupsX, groupsY, 1);
            D3D12Context.Barrier(list, null);

            list->SetComputeRootSignature(_filter.Signature.Handle);
            list->SetPipelineState(_filter.Handle);
            channel.Result.Transition(list, ResourceStates.UnorderedAccess);

            for (int i = 0; i < 3; i++)
            {
                // Which scratch target this blur reads and which it writes. They must
                // alternate: written the other way round, the first blur read and wrote the
                // same target while the second read the one nothing had written that frame,
                // so it blurred its own result over and over, decaying towards nothing. That
                // target is also what the reprojection reads back as its history, so every
                // pixel's past became a thing quietly fading out — a room that started at
                // the right brightness and went dark over half a second, and did it again
                // every time the camera moved and reset the counts.
                (D3D12Texture input, D3D12Texture output) = i == 1
                    ? (channel.Scratch1, channel.Scratch0)
                    : (channel.Scratch0, channel.Scratch1);

                input.Transition(list, ResourceStates.NonPixelShaderResource);
                output.Transition(list, ResourceStates.UnorderedAccess);

                BindDenoiseTable(list, _filter.Signature, channel.Tables[2 + i]);

                stage = new StageConstants(1 << i, i);
                list->SetComputeRoot32BitConstants(
                    (uint)_filter.Signature.PushConstantParameter,
                    DenoiseLayout.StageConstantBytes / 4,
                    &stage,
                    0);

                list->Dispatch(groupsX, groupsY, 1);
                D3D12Context.Barrier(list, null);
            }

            // What the composite reads.
            channel.Result.Transition(list, ResourceStates.AllShaderResource);
        }

        // This frame's depth becomes the one the next frame reprojects against.
        depth.Transition(list, ResourceStates.CopySource);
        _previousDepth.Transition(list, ResourceStates.CopyDest);
        list->CopyResource(_previousDepth.Handle, depth.Handle);
        _previousDepth.Transition(list, ResourceStates.NonPixelShaderResource);
        depth.Transition(list, ResourceStates.NonPixelShaderResource);

        _frame++;
        _first = false;
    }

    /// <summary>Puts every target this owns into the state the stages start from.</summary>
    /// <param name="list">Command list to record into.</param>
    /// <remarks>
    /// Once, when the targets are new. They are created in unordered access already, so this
    /// is a no-op the first time and the transitions it would record are the ones
    /// <see cref="Record"/> makes for itself afterwards. It exists so a caller has one place
    /// to say "these are ready", the way the Vulkan denoiser needs for its initial layouts.
    /// </remarks>
    public void Settle(ID3D12GraphicsCommandList4* list)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(list);

        foreach (Channel channel in _channels)
        {
            foreach (D3D12Texture surface in channel.Surfaces)
            {
                surface.Transition(list, ResourceStates.UnorderedAccess);
            }
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
        _context.Wait();

        foreach (Channel channel in _channels)
        {
            channel.Dispose();
        }

        _previousDepth.Dispose();
        _uniform.Dispose();
        _shared.Dispose();
        _samplers.Dispose();
        _views.Dispose();
        _filter.Dispose();
        _classify.Dispose();
        _trace.Dispose();
    }

    private static int Divide(int value, int divisor) => (value + divisor - 1) / divisor;

    private static int Tiles(int width, int height) =>
        Divide(width, DenoiseLayout.TileWidth) * Divide(height, DenoiseLayout.TileHeight);

    private void BindDenoiseTable(
        ID3D12GraphicsCommandList4* list, D3D12RootSignature signature, uint table)
    {
        list->SetComputeRootDescriptorTable((uint)signature.ParameterFor(0), _views.Gpu(table));

        int samplers = signature.SamplerParameterFor(0);
        if (samplers >= 0)
        {
            list->SetComputeRootDescriptorTable((uint)samplers, _samplers.Gpu(0));
        }
    }

    private void WriteDenoiseTable(
        Channel channel, int stage, D3D12Texture depth, D3D12Texture normal, D3D12Texture motion)
    {
        D3D12RootSignature signature = _classify.Signature;
        uint table = channel.Tables[stage];

        CpuDescriptorHandle Slot(uint binding) =>
            _views.Cpu(table + signature.ViewOffset(0, binding));

        bool reprojecting = stage < 2;

        // The same table as the Vulkan denoiser writes, read the same way round. The blurs
        // alternate between the two scratch targets; the reprojection reads whichever set of
        // moments belongs to the frame before and writes the other.
        D3D12Texture input = reprojecting || stage != 3 ? channel.Scratch0 : channel.Scratch1;
        D3D12Texture output = reprojecting || stage != 3 ? channel.Scratch1 : channel.Scratch0;
        D3D12Texture older = channel.Moments[reprojecting ? stage : 0];
        D3D12Texture newer = channel.Moments[reprojecting ? 1 - stage : 1];

        depth.Describe(_context, Slot(0));
        normal.Describe(_context, Slot(1));
        motion.Describe(_context, Slot(2));
        _previousDepth.Describe(_context, Slot(3));
        older.Describe(_context, Slot(4));
        channel.Scratch1.Describe(_context, Slot(5));
        input.Describe(_context, Slot(6));
        channel.Mask.DescribeWrite(_context, Slot(8));
        channel.Metadata.DescribeWrite(_context, Slot(9));
        channel.Scratch0.DescribeWrite(_context, Slot(10));
        newer.DescribeWrite(_context, Slot(11));
        output.DescribeWrite(_context, Slot(12));
        channel.Result.DescribeWrite(_context, Slot(13));
        _uniform.DescribeConstants(_context, Slot(14));
        channel.Fraction.Describe(_context, Slot(15));
    }

    /// <summary>Everything one denoised signal needs of its own.</summary>
    private sealed class Channel : IDisposable
    {
        /// <summary>One bit a pixel, packed into a word a tile.</summary>
        public required D3D12Buffer Mask { get; init; }

        /// <summary>What the classify pass worked out about each tile.</summary>
        public required D3D12Buffer Metadata { get; init; }

        /// <summary>Reprojected, then blurred back and forth between these two.</summary>
        public required D3D12Texture Scratch0 { get; init; }

        /// <summary>The other one.</summary>
        public required D3D12Texture Scratch1 { get; init; }

        /// <summary>Running mean, sum of squares and sample count, one frame apart.</summary>
        public required D3D12Texture[] Moments { get; init; }

        /// <summary>What everything else reads.</summary>
        public required D3D12Texture Result { get; init; }

        /// <summary>What this frame's rays actually found, before any filtering.</summary>
        public required D3D12Texture Fraction { get; init; }

        /// <summary>Where each stage's descriptor table starts in the heap.</summary>
        public uint[] Tables { get; set; } = [];

        /// <summary>Every target this channel owns.</summary>
        public IEnumerable<D3D12Texture> Surfaces =>
            [Scratch0, Scratch1, Moments[0], Moments[1], Result, Fraction];

        public static Channel Create(D3D12Context context, int width, int height, int tiles)
        {
            ulong words = (ulong)tiles * sizeof(uint);

            D3D12Texture Make(Format format) =>
                D3D12Texture.CreateStorage(context, format, width, height);

            return new Channel
            {
                Mask = D3D12Buffer.CreateEmpty(context, words, writable: true),
                Metadata = D3D12Buffer.CreateEmpty(context, words, writable: true),
                Scratch0 = Make(Format.FormatR16G16B16A16Float),
                Scratch1 = Make(Format.FormatR16G16B16A16Float),
                Moments =
                [
                    Make(Format.FormatR32G32B32A32Float),
                    Make(Format.FormatR32G32B32A32Float),
                ],
                Result = Make(Format.FormatR32Float),
                Fraction = Make(Format.FormatR16Float),
            };
        }

        public void Dispose()
        {
            foreach (D3D12Texture surface in Surfaces)
            {
                surface.Dispose();
            }

            Metadata.Dispose();
            Mask.Dispose();
        }
    }
}
