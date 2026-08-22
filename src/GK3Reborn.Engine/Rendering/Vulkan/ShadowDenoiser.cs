// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;

namespace GK3Reborn.Rendering.Vulkan;

/// <summary>Traces occlusion once a pixel and filters it into something smooth.</summary>
/// <remarks>
/// <para>
/// Two signals go through the same five stages: how much of the direct light reaches each
/// pixel, and how open the sky is above it. Each is one ray a frame, which is a single bit
/// and looks like static; the filter chain — AMD's, ported in <see cref="DenoiserShaders"/>
/// — turns a bit a frame into a smooth fraction by remembering where every pixel was and
/// what it answered last time.
/// </para>
/// <para>
/// This replaces tracing inside the mesh shader. That could only afford a handful of rays
/// per pixel per light and had nothing to average them with, so its grain was pinned to
/// the screen and read as dirt on whatever it fell on.
/// </para>
/// </remarks>
internal sealed unsafe class ShadowDenoiser : IDisposable
{
    /// <summary>How many pixels across a bitmask tile is.</summary>
    private const int TileWidth = 8;

    /// <summary>And down. Eight by four is thirty-two pixels, one bit each, one word.</summary>
    private const int TileHeight = 4;

    private readonly VulkanContext _context;
    private readonly Vk _vk;
    private readonly Device _device;
    private readonly int _width;
    private readonly int _height;

    private readonly ComputePipeline _trace;
    private readonly ComputePipeline _classify;
    private readonly ComputePipeline _filter;

    private readonly Channel[] _channels;
    private readonly VulkanBuffer _uniform;
    private readonly Sampler _sampler;
    private readonly DescriptorPool _pool;

    private readonly Image _previousDepth;
    private readonly DeviceMemory _previousDepthMemory;
    private readonly ImageView _previousDepthView;

    private DescriptorSet _traceSet;
    private AccelerationStructureKHR _structure;
    private Matrix4x4? _previousViewProjection;
    private int _frame;
    private bool _first = true;

    private ShadowDenoiser(
        VulkanContext context,
        int width,
        int height,
        ComputePipeline trace,
        ComputePipeline classify,
        ComputePipeline filter,
        Channel[] channels,
        VulkanBuffer uniform,
        Sampler sampler,
        DescriptorPool pool,
        Image previousDepth,
        DeviceMemory previousDepthMemory,
        ImageView previousDepthView)
    {
        _context = context;
        _vk = context.Api;
        _device = context.Device;
        _width = width;
        _height = height;
        _trace = trace;
        _classify = classify;
        _filter = filter;
        _channels = channels;
        _uniform = uniform;
        _sampler = sampler;
        _pool = pool;
        _previousDepth = previousDepth;
        _previousDepthMemory = previousDepthMemory;
        _previousDepthView = previousDepthView;
    }

    /// <summary>The denoised fraction of the direct light that reaches each pixel.</summary>
    public ImageView Shadow => _channels[0].Result.View;

    /// <summary>The denoised fraction of the hemisphere each pixel can see.</summary>
    public ImageView Occlusion => _channels[1].Result.View;

    /// <summary>
    /// The denoised fraction of the direct light that the things standing in the room —
    /// characters and props — leave alone.
    /// </summary>
    /// <remarks>
    /// One where nothing is in the way, which is every pixel of a scene with nobody in it,
    /// so a room with no one in it composites exactly as it did before this existed.
    /// <see cref="Shadow"/> is the room's own shadowing and is the half the bake already
    /// contains; this is the half it cannot.
    /// </remarks>
    public ImageView DynamicShadow => _channels[2].Result.View;

    /// <summary>Where each channel's tile bitmask is bound in the trace stage.</summary>
    /// <remarks>
    /// Not <c>3 + c</c>. The rig uniform sits at five and a third channel would have
    /// landed on it — silently, because a descriptor write does not object to being
    /// pointed at a binding of another type until the shader reads it.
    /// </remarks>
    private static ReadOnlySpan<uint> TraceMaskBinding => [3, 4, 8];

    /// <summary>Where each channel's per-pixel fraction image is bound.</summary>
    private static ReadOnlySpan<uint> TraceFractionBinding => [6, 7, 9];

    /// <summary>Builds every stage and every buffer, for one viewport size.</summary>
    /// <param name="context">The device.</param>
    /// <param name="compiler">Compiler for the stages.</param>
    /// <param name="width">Viewport width in pixels.</param>
    /// <param name="height">Viewport height in pixels.</param>
    /// <returns>The denoiser, or null if the device cannot trace rays.</returns>
    public static ShadowDenoiser? Create(
        VulkanContext context, ShaderCompiler compiler, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(compiler);

        if (!context.SupportsRayTracing || width <= 0 || height <= 0)
        {
            return null;
        }

        Vk vk = context.Api;
        Device device = context.Device;

        ComputePipeline trace = ComputePipeline.Create(
            context, compiler, DenoiserShaders.ComposeTrace(), TraceBindings(), 88);

        ComputePipeline classify = ComputePipeline.Create(
            context, compiler, DenoiserShaders.ComposeClassify(), DenoiseBindings(), 8);

        ComputePipeline filter = ComputePipeline.Create(
            context, compiler, DenoiserShaders.ComposeFilter(), DenoiseBindings(), 8);

        var sampleInfo = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Linear,
            MinFilter = Filter.Linear,
            AddressModeU = SamplerAddressMode.ClampToEdge,
            AddressModeV = SamplerAddressMode.ClampToEdge,
            AddressModeW = SamplerAddressMode.ClampToEdge,
            MaxLod = 0f,
        };

        vk.CreateSampler(device, in sampleInfo, null, out Sampler sampler);

        // Eleven sets: one that traces, and five for each of the two signals — two
        // reprojections, one per parity of the moments, and three blurs.
        var poolSizes = stackalloc DescriptorPoolSize[]
        {
            new DescriptorPoolSize(DescriptorType.CombinedImageSampler, 1),
            new DescriptorPoolSize(DescriptorType.SampledImage, 256),
            new DescriptorPoolSize(DescriptorType.Sampler, 32),
            new DescriptorPoolSize(DescriptorType.StorageImage, 128),
            new DescriptorPoolSize(DescriptorType.StorageBuffer, 128),
            new DescriptorPoolSize(DescriptorType.UniformBuffer, 64),
            new DescriptorPoolSize(DescriptorType.AccelerationStructureKhr, 4),
        };

        // Five sets a channel plus the one the trace stage uses. The old sixteen was
        // exactly the two channels' worth, so a third would have failed to allocate at
        // the last set rather than at the first — and an out-of-pool descriptor set is a
        // null handle, not an exception.
        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            PoolSizeCount = 7,
            PPoolSizes = poolSizes,
            MaxSets = 32,
        };

        vk.CreateDescriptorPool(device, in poolInfo, null, out DescriptorPool pool);

        int tiles = Tiles(width, height);

        var uniform = VulkanBuffer.CreateHostVisible(
            context, (ulong)Marshal.SizeOf<DenoiseUniforms>(), BufferUsageFlags.UniformBufferBit);

        Channel[] channels =
        [
            Channel.Create(context, width, height, tiles),
            Channel.Create(context, width, height, tiles),
            Channel.Create(context, width, height, tiles),
        ];

        (Image image, DeviceMemory memory, ImageView view) previous = CreateImage(
            context,
            width,
            height,
            Format.D32Sfloat,
            ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit,
            ImageAspectFlags.DepthBit);

        return new ShadowDenoiser(
            context,
            width,
            height,
            trace,
            classify,
            filter,
            channels,
            uniform,
            sampler,
            pool,
            previous.image,
            previous.memory,
            previous.view);
    }

    /// <summary>Points every stage at the frame's targets.</summary>
    /// <param name="depth">The frame's depth, as a sampleable view.</param>
    /// <param name="normal">The frame's normals.</param>
    /// <param name="motion">The frame's motion vectors.</param>
    /// <param name="structure">The scene's acceleration structure.</param>
    /// <param name="rig">The buffer of lights.</param>
    /// <param name="rigBytes">How long that buffer is.</param>
    /// <remarks>
    /// Called once for a set of targets rather than once a frame: nothing here changes
    /// between frames except the contents, and the moments swap by having two sets rather
    /// than by rewriting one.
    /// </remarks>
    public void Bind(
        ImageView depth,
        ImageView normal,
        ImageView motion,
        AccelerationStructureKHR structure,
        Silk.NET.Vulkan.Buffer rig,
        ulong rigBytes)
    {
        DescriptorSet[] sets = Allocate(_trace.SetLayout, 1);
        _traceSet = sets[0];
        _structure = structure;

        var depthInfo = new DescriptorImageInfo
        {
            ImageView = depth,
            ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
        };

        var normalInfo = new DescriptorImageInfo
        {
            ImageView = normal,
            ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
        };

        var motionInfo = new DescriptorImageInfo
        {
            ImageView = motion,
            ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
        };

        var previousInfo = new DescriptorImageInfo
        {
            ImageView = _previousDepthView,
            ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
        };

        var samplerInfo = new DescriptorImageInfo { Sampler = _sampler };

        var structureHandle = structure;

        var structureInfo = new WriteDescriptorSetAccelerationStructureKHR
        {
            SType = StructureType.WriteDescriptorSetAccelerationStructureKhr,
            AccelerationStructureCount = 1,
            PAccelerationStructures = &structureHandle,
        };

        var rigInfo = new DescriptorBufferInfo { Buffer = rig, Range = rigBytes };
        var uniformInfo = new DescriptorBufferInfo
        {
            Buffer = _uniform.Handle,
            Range = (ulong)Marshal.SizeOf<DenoiseUniforms>(),
        };

        var writes = new List<WriteDescriptorSet>
        {
            Sampled(_traceSet, 0, &depthInfo),
            Sampled(_traceSet, 1, &normalInfo),
            new()
            {
                SType = StructureType.WriteDescriptorSet,
                PNext = &structureInfo,
                DstSet = _traceSet,
                DstBinding = 2,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.AccelerationStructureKhr,
            },
            Buffered(_traceSet, 5, DescriptorType.UniformBuffer, &rigInfo),
        };

        for (int c = 0; c < _channels.Length; c++)
        {
            Channel channel = _channels[c];
            var maskInfo = new DescriptorBufferInfo
            {
                Buffer = channel.Mask.Handle,
                Range = channel.Mask.Size,
            };

            writes.Add(Buffered(
                _traceSet, TraceMaskBinding[c], DescriptorType.StorageBuffer, &maskInfo));

            var fractionInfo = new DescriptorImageInfo
            {
                ImageView = channel.Fraction.View,
                ImageLayout = ImageLayout.General,
            };

            writes.Add(Storage(_traceSet, TraceFractionBinding[c], &fractionInfo));

            channel.Sets = Allocate(_classify.SetLayout, 5);

            for (int i = 0; i < 5; i++)
            {
                // Which scratch image each stage reads and writes. The reprojection lands
                // in the first; the blurs then walk it back and forth, and the last one
                // writes the result instead.
                //
                // The two must alternate. Written the other way round, the first blur read
                // and wrote the same image while the second read the one nothing had
                // written that frame — its own output from the frame before — so it blurred
                // its own result over and over, decaying towards nothing. That buffer is
                // also what the reprojection reads back as its history, so every pixel's
                // past was a thing quietly fading out: a room that started at the right
                // brightness and went dark over half a second, and did it again every time
                // the camera moved and reset the counts.
                bool reprojecting = i < 2;
                Surface input = reprojecting || i == 2 || i == 4 ? channel.Scratch0 : channel.Scratch1;
                Surface output = reprojecting || i == 2 ? channel.Scratch1 : channel.Scratch0;
                Surface older = channel.Moments[reprojecting ? i : 0];
                Surface newer = channel.Moments[reprojecting ? 1 - i : 1];

                DescriptorSet set = channel.Sets[i];

                var inputInfo = new DescriptorImageInfo
                {
                    ImageView = input.View,
                    ImageLayout = ImageLayout.General,
                };

                var historyInfo = new DescriptorImageInfo
                {
                    ImageView = channel.Scratch1.View,
                    ImageLayout = ImageLayout.General,
                };

                var olderInfo = new DescriptorImageInfo
                {
                    ImageView = older.View,
                    ImageLayout = ImageLayout.General,
                };

                var newerInfo = new DescriptorImageInfo
                {
                    ImageView = newer.View,
                    ImageLayout = ImageLayout.General,
                };

                var reprojectionInfo = new DescriptorImageInfo
                {
                    ImageView = channel.Scratch0.View,
                    ImageLayout = ImageLayout.General,
                };

                var outputInfo = new DescriptorImageInfo
                {
                    ImageView = output.View,
                    ImageLayout = ImageLayout.General,
                };

                var resultInfo = new DescriptorImageInfo
                {
                    ImageView = channel.Result.View,
                    ImageLayout = ImageLayout.General,
                };

                var metaInfo = new DescriptorBufferInfo
                {
                    Buffer = channel.Metadata.Handle,
                    Range = channel.Metadata.Size,
                };

                writes.Add(Sampled(set, 0, &depthInfo));
                writes.Add(Sampled(set, 1, &normalInfo));
                writes.Add(Sampled(set, 2, &motionInfo));
                writes.Add(Sampled(set, 3, &previousInfo));
                writes.Add(Sampled(set, 4, &olderInfo));
                writes.Add(Sampled(set, 5, &historyInfo));
                writes.Add(Sampled(set, 6, &inputInfo));
                writes.Add(new WriteDescriptorSet
                {
                    SType = StructureType.WriteDescriptorSet,
                    DstSet = set,
                    DstBinding = 7,
                    DescriptorCount = 1,
                    DescriptorType = DescriptorType.Sampler,
                    PImageInfo = &samplerInfo,
                });
                writes.Add(Buffered(set, 8, DescriptorType.StorageBuffer, &maskInfo));
                writes.Add(Buffered(set, 9, DescriptorType.StorageBuffer, &metaInfo));
                writes.Add(Storage(set, 10, &reprojectionInfo));
                writes.Add(Storage(set, 11, &newerInfo));
                writes.Add(Storage(set, 12, &outputInfo));
                writes.Add(Storage(set, 13, &resultInfo));
                writes.Add(Buffered(set, 14, DescriptorType.UniformBuffer, &uniformInfo));
                writes.Add(Sampled(set, 15, &fractionInfo));

                Commit(writes);
            }
        }

        Commit(writes);
    }

    /// <summary>Points the tracing stage at a rebuilt acceleration structure.</summary>
    /// <param name="structure">The structure to trace against now.</param>
    /// <remarks>
    /// It is rebuilt whenever anything in the room moves, which means a new handle and a
    /// stale descriptor — so this is checked every frame and does the one write when it
    /// has to.
    /// </remarks>
    public void Point(AccelerationStructureKHR structure)
    {
        if (structure.Handle == _structure.Handle)
        {
            return;
        }

        _structure = structure;

        AccelerationStructureKHR handle = structure;

        var structureInfo = new WriteDescriptorSetAccelerationStructureKHR
        {
            SType = StructureType.WriteDescriptorSetAccelerationStructureKhr,
            AccelerationStructureCount = 1,
            PAccelerationStructures = &handle,
        };

        var write = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            PNext = &structureInfo,
            DstSet = _traceSet,
            DstBinding = 2,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.AccelerationStructureKhr,
        };

        _vk.UpdateDescriptorSets(_device, 1, in write, 0, null);
    }

    /// <summary>Records the trace and the five filtering stages.</summary>
    /// <param name="command">Command buffer to record into.</param>
    /// <param name="camera">The camera the frame was drawn from.</param>
    /// <param name="depthImage">The frame's depth image, to keep for next time.</param>
    /// <param name="radius">How far an occlusion ray looks.</param>
    /// <param name="samples">How many rays each pixel spends on each signal.</param>
    public void Record(
        CommandBuffer command, Camera camera, Image depthImage, float radius, int samples)
    {
        ArgumentNullException.ThrowIfNull(camera);

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

                // Where a pixel of this frame sat in the last one's clip space, which is
                // what tells a reprojection whether it is looking at the same surface.
                inverseViewProjection * previous,
                inverseViewProjection,
                new Vector4(camera.Position, _first ? 1f : 0f),
                _width,
                _height,
                1f / _width,
                1f / _height,

                // How far apart two depths may be before they stop being the same
                // surface. AMD's own default.
                new Vector4(0.01f, 0, 0, 0)),
        ]);

        _previousViewProjection = viewProjection;

        var push = new TraceConstants(
            inverseViewProjection,
            _width,
            _height,
            radius,

            // A different seed each frame. Grain that stands still cannot be averaged
            // away, and averaging it away is now somebody's job.
            (_frame % 64) * 0.61803398875f,
            Math.Max(samples, 1),
            0);

        _vk.CmdBindPipeline(command, PipelineBindPoint.Compute, _trace.Handle);
        _vk.CmdBindDescriptorSets(
            command, PipelineBindPoint.Compute, _trace.Layout, 0, 1, in _traceSet, 0, null);

        _vk.CmdPushConstants(
            command, _trace.Layout, ShaderStageFlags.ComputeBit, 0, 88, &push);

        _vk.CmdDispatch(
            command,
            (uint)Divide(_width, TileWidth),
            (uint)Divide(_height, TileHeight),
            1);

        Barrier(command);

        // One eight by eight group a tile. AMD dispatch twice as many rows as there are
        // groups and let the surplus write out of bounds; the addresses are the same
        // either way, so half of them are simply not launched here.
        uint groupsX = (uint)Divide(_width, 8);
        uint groupsY = (uint)Divide(_height, 8);

        foreach (Channel channel in _channels)
        {
            _vk.CmdBindPipeline(command, PipelineBindPoint.Compute, _classify.Handle);

            DescriptorSet reproject = channel.Sets[_frame & 1];

            _vk.CmdBindDescriptorSets(
                command, PipelineBindPoint.Compute, _classify.Layout, 0, 1, in reproject, 0, null);

            var stage = new StageConstants(1, 0);
            _vk.CmdPushConstants(
                command, _classify.Layout, ShaderStageFlags.ComputeBit, 0, 8, &stage);

            _vk.CmdDispatch(command, groupsX, groupsY, 1);
            Barrier(command);

            _vk.CmdBindPipeline(command, PipelineBindPoint.Compute, _filter.Handle);

            for (int i = 0; i < 3; i++)
            {
                DescriptorSet set = channel.Sets[2 + i];

                _vk.CmdBindDescriptorSets(
                    command, PipelineBindPoint.Compute, _filter.Layout, 0, 1, in set, 0, null);

                stage = new StageConstants(1 << i, i);
                _vk.CmdPushConstants(
                    command, _filter.Layout, ShaderStageFlags.ComputeBit, 0, 8, &stage);

                _vk.CmdDispatch(command, groupsX, groupsY, 1);
                Barrier(command);
            }
        }

        // This frame's depth becomes the one the next frame reprojects against.
        _context.Transition(
            command,
            _previousDepth,
            _first ? ImageLayout.Undefined : ImageLayout.ShaderReadOnlyOptimal,
            ImageLayout.TransferDstOptimal,
            ImageAspectFlags.DepthBit);

        _context.Transition(
            command,
            depthImage,
            ImageLayout.ShaderReadOnlyOptimal,
            ImageLayout.TransferSrcOptimal,
            ImageAspectFlags.DepthBit);

        var region = new ImageCopy
        {
            SrcSubresource = new ImageSubresourceLayers
            {
                AspectMask = ImageAspectFlags.DepthBit,
                LayerCount = 1,
            },
            DstSubresource = new ImageSubresourceLayers
            {
                AspectMask = ImageAspectFlags.DepthBit,
                LayerCount = 1,
            },
            Extent = new Extent3D((uint)_width, (uint)_height, 1),
        };

        _vk.CmdCopyImage(
            command,
            depthImage,
            ImageLayout.TransferSrcOptimal,
            _previousDepth,
            ImageLayout.TransferDstOptimal,
            1,
            in region);

        _context.Transition(
            command,
            depthImage,
            ImageLayout.TransferSrcOptimal,
            ImageLayout.ShaderReadOnlyOptimal,
            ImageAspectFlags.DepthBit);

        _context.Transition(
            command,
            _previousDepth,
            ImageLayout.TransferDstOptimal,
            ImageLayout.ShaderReadOnlyOptimal,
            ImageAspectFlags.DepthBit);

        _frame++;
        _first = false;
    }

    /// <summary>Puts every image this owns into the layout the stages expect.</summary>
    /// <param name="command">Command buffer to record into.</param>
    /// <remarks>
    /// Once, when the images are new. They stay in <c>General</c> from then on, which is
    /// the only layout a storage image can be written through.
    /// </remarks>
    public void Settle(CommandBuffer command)
    {
        foreach (Channel channel in _channels)
        {
            foreach (Surface surface in channel.Surfaces)
            {
                _context.Transition(
                    command, surface.Image, ImageLayout.Undefined, ImageLayout.General);
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _vk.DeviceWaitIdle(_device);

        foreach (Channel channel in _channels)
        {
            channel.Dispose(_context);
        }

        _vk.DestroyImageView(_device, _previousDepthView, null);
        _vk.DestroyImage(_device, _previousDepth, null);
        _vk.FreeMemory(_device, _previousDepthMemory, null);

        _vk.DestroyDescriptorPool(_device, _pool, null);
        _vk.DestroySampler(_device, _sampler, null);
        _uniform.Dispose();

        _filter.Dispose();
        _classify.Dispose();
        _trace.Dispose();
    }

    private static int Divide(int value, int divisor) => (value + divisor - 1) / divisor;

    private static int Tiles(int width, int height) =>
        Divide(width, TileWidth) * Divide(height, TileHeight);

    private static DescriptorSetLayoutBinding[] TraceBindings() =>
    [
        Binding(0, DescriptorType.SampledImage),
        Binding(1, DescriptorType.SampledImage),
        Binding(2, DescriptorType.AccelerationStructureKhr),
        Binding(3, DescriptorType.StorageBuffer),
        Binding(4, DescriptorType.StorageBuffer),
        Binding(5, DescriptorType.UniformBuffer),
        Binding(6, DescriptorType.StorageImage),
        Binding(7, DescriptorType.StorageImage),

        // The dynamic-shadow channel, out of order because the rig took five.
        // TraceMaskBinding and TraceFractionBinding are the same table read the other way.
        Binding(8, DescriptorType.StorageBuffer),
        Binding(9, DescriptorType.StorageImage),
    ];

    private static DescriptorSetLayoutBinding[] DenoiseBindings() =>
    [
        Binding(0, DescriptorType.SampledImage),
        Binding(1, DescriptorType.SampledImage),
        Binding(2, DescriptorType.SampledImage),
        Binding(3, DescriptorType.SampledImage),
        Binding(4, DescriptorType.SampledImage),
        Binding(5, DescriptorType.SampledImage),
        Binding(6, DescriptorType.SampledImage),
        Binding(7, DescriptorType.Sampler),
        Binding(8, DescriptorType.StorageBuffer),
        Binding(9, DescriptorType.StorageBuffer),
        Binding(10, DescriptorType.StorageImage),
        Binding(11, DescriptorType.StorageImage),
        Binding(12, DescriptorType.StorageImage),
        Binding(13, DescriptorType.StorageImage),
        Binding(14, DescriptorType.UniformBuffer),
        Binding(15, DescriptorType.SampledImage),
    ];

    private static DescriptorSetLayoutBinding Binding(uint index, DescriptorType type) =>
        new()
        {
            Binding = index,
            DescriptorType = type,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.ComputeBit,
        };

    private static WriteDescriptorSet Sampled(
        DescriptorSet set, uint binding, DescriptorImageInfo* info) =>
        new()
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = set,
            DstBinding = binding,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.SampledImage,
            PImageInfo = info,
        };

    private static WriteDescriptorSet Storage(
        DescriptorSet set, uint binding, DescriptorImageInfo* info) =>
        new()
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = set,
            DstBinding = binding,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.StorageImage,
            PImageInfo = info,
        };

    private static WriteDescriptorSet Buffered(
        DescriptorSet set, uint binding, DescriptorType type, DescriptorBufferInfo* info) =>
        new()
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = set,
            DstBinding = binding,
            DescriptorCount = 1,
            DescriptorType = type,
            PBufferInfo = info,
        };

    private static (Image Image, DeviceMemory Memory, ImageView View) CreateImage(
        VulkanContext context,
        int width,
        int height,
        Format format,
        ImageUsageFlags usage,
        ImageAspectFlags aspect)
    {
        Vk vk = context.Api;
        Device device = context.Device;

        var imageInfo = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = format,
            Extent = new Extent3D((uint)width, (uint)height, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = usage,
            InitialLayout = ImageLayout.Undefined,
        };

        if (vk.CreateImage(device, in imageInfo, null, out Image image) != Result.Success)
        {
            throw new VulkanException("Could not create a denoiser image.");
        }

        vk.GetImageMemoryRequirements(device, image, out MemoryRequirements requirements);

        DeviceMemory memory = context.Allocate(requirements, MemoryPropertyFlags.DeviceLocalBit);
        vk.BindImageMemory(device, image, memory, 0);

        var viewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = image,
            ViewType = ImageViewType.Type2D,
            Format = format,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = aspect,
                LevelCount = 1,
                LayerCount = 1,
            },
        };

        if (vk.CreateImageView(device, in viewInfo, null, out ImageView view) != Result.Success)
        {
            throw new VulkanException("Could not create a denoiser image view.");
        }

        return (image, memory, view);
    }

    private DescriptorSet[] Allocate(DescriptorSetLayout layout, int count)
    {
        DescriptorSetLayout* layouts = stackalloc DescriptorSetLayout[count];

        for (int i = 0; i < count; i++)
        {
            layouts[i] = layout;
        }

        var sets = new DescriptorSet[count];

        var info = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _pool,
            DescriptorSetCount = (uint)count,
            PSetLayouts = layouts,
        };

        fixed (DescriptorSet* allocated = sets)
        {
            if (_vk.AllocateDescriptorSets(_device, in info, allocated) != Result.Success)
            {
                throw new VulkanException("Could not allocate the denoiser's descriptor sets.");
            }
        }

        return sets;
    }

    private void Commit(List<WriteDescriptorSet> writes)
    {
        if (writes.Count == 0)
        {
            return;
        }

        WriteDescriptorSet[] array = [.. writes];

        fixed (WriteDescriptorSet* pointer = array)
        {
            _vk.UpdateDescriptorSets(_device, (uint)array.Length, pointer, 0, null);
        }

        writes.Clear();
    }

    // Every stage reads what the one before it wrote, and they all go through storage
    // images and storage buffers, which nothing synchronises on its own.
    private void Barrier(CommandBuffer command)
    {
        var barrier = new MemoryBarrier
        {
            SType = StructureType.MemoryBarrier,
            SrcAccessMask = AccessFlags.ShaderWriteBit,
            DstAccessMask = AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit,
        };

        _vk.CmdPipelineBarrier(
            command,
            PipelineStageFlags.ComputeShaderBit,
            PipelineStageFlags.ComputeShaderBit,
            0,
            1,
            in barrier,
            0,
            null,
            0,
            null);
    }

    /// <summary>One image the denoiser owns.</summary>
    private readonly record struct Surface(Image Image, DeviceMemory Memory, ImageView View);

    /// <summary>Everything one denoised signal needs of its own.</summary>
    private sealed class Channel
    {
        public required VulkanBuffer Mask { get; init; }

        public required VulkanBuffer Metadata { get; init; }

        /// <summary>Reprojected, then blurred back and forth between these two.</summary>
        public required Surface Scratch0 { get; init; }

        public required Surface Scratch1 { get; init; }

        /// <summary>Running mean, sum of squares and sample count, one frame apart.</summary>
        public required Surface[] Moments { get; init; }

        /// <summary>What everything else reads.</summary>
        public required Surface Result { get; init; }

        /// <summary>What this frame's rays actually found, before any filtering.</summary>
        public required Surface Fraction { get; init; }

        public DescriptorSet[] Sets { get; set; } = [];

        public IEnumerable<Surface> Surfaces =>
            [Scratch0, Scratch1, Moments[0], Moments[1], Result, Fraction];

        public static Channel Create(VulkanContext context, int width, int height, int tiles)
        {
            ulong words = (ulong)(tiles * sizeof(uint));

            Surface Make(Format format)
            {
                (Image image, DeviceMemory memory, ImageView view) = CreateImage(
                    context,
                    width,
                    height,
                    format,
                    ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit,
                    ImageAspectFlags.ColorBit);

                return new Surface(image, memory, view);
            }

            return new Channel
            {
                Mask = VulkanBuffer.CreateEmpty(
                    context, words, BufferUsageFlags.StorageBufferBit),
                Metadata = VulkanBuffer.CreateEmpty(
                    context, words, BufferUsageFlags.StorageBufferBit),
                Scratch0 = Make(Format.R16G16B16A16Sfloat),
                Scratch1 = Make(Format.R16G16B16A16Sfloat),
                Moments =
                [
                    Make(Format.R32G32B32A32Sfloat),
                    Make(Format.R32G32B32A32Sfloat),
                ],
                Result = Make(Format.R32Sfloat),
                Fraction = Make(Format.R16Sfloat),
            };
        }

        public void Dispose(VulkanContext context)
        {
            foreach (Surface surface in Surfaces)
            {
                context.Api.DestroyImageView(context.Device, surface.View, null);
                context.Api.DestroyImage(context.Device, surface.Image, null);
                context.Api.FreeMemory(context.Device, surface.Memory, null);
            }

            Mask.Dispose();
            Metadata.Dispose();
        }
    }

    /// <summary>What the tracing stage is told, in eighty bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct TraceConstants(
        Matrix4x4 ViewProjectionInverse,
        int Width,
        int Height,
        float Radius,
        float Seed,
        int Samples,
        int Padding);

    /// <summary>Which of the three blurs this is, and how far apart its taps are.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct StageConstants(int StepSize, int Index);

    /// <summary>What the filtering stages read, once a frame.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct DenoiseUniforms(
        Matrix4x4 ProjectionInverse,
        Matrix4x4 ReprojectionMatrix,
        Matrix4x4 ViewProjectionInverse,
        Vector4 EyeAndFirst,
        int Width,
        int Height,
        float InverseWidth,
        float InverseHeight,
        Vector4 Sigma);
}
