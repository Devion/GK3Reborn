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

using GK3Reborn.Rendering.Shaders;

namespace GK3Reborn.Rendering.Vulkan;

/// <summary>Reflects the frame in whatever in it is smooth enough to reflect.</summary>
/// <remarks>
/// A min-depth pyramid, then one ray a pixel marched over it — AMD's SSSR intersection,
/// ported in <see cref="ReflectionShaders"/> — then an average over frames so that a rough
/// surface, which takes a different sample every frame, settles rather than boils.
/// </remarks>
internal sealed unsafe class Reflections : IDisposable
{
    /// <summary>How many levels the depth pyramid has, the full-size one included.</summary>
    /// <remarks>
    /// Six halvings takes a 1280 by 720 frame down to 40 by 23, which is coarse enough
    /// that a ray crossing empty space clears it in a step or two.
    /// </remarks>
    private const int Levels = 7;

    private readonly VulkanContext _context;
    private readonly Vk _vk;
    private readonly Device _device;
    private readonly int _width;
    private readonly int _height;

    private readonly ComputePipeline _downsample;
    private readonly ComputePipeline _march;
    private readonly DescriptorPool _pool;
    private readonly Sampler _sampler;
    private readonly VulkanBuffer _uniform;

    private readonly Image _pyramid;
    private readonly DeviceMemory _pyramidMemory;
    private readonly ImageView _pyramidView;
    private readonly ImageView[] _levelViews = new ImageView[Levels];

    private readonly Image[] _images = new Image[2];
    private readonly DeviceMemory[] _memory = new DeviceMemory[2];
    private readonly ImageView[] _views = new ImageView[2];

    private DescriptorSet[] _levelSets = [];
    private DescriptorSet[] _marchSets = [];
    private int _frame;

    private Reflections(
        VulkanContext context,
        int width,
        int height,
        ComputePipeline downsample,
        ComputePipeline march,
        DescriptorPool pool,
        Sampler sampler,
        VulkanBuffer uniform,
        Image pyramid,
        DeviceMemory pyramidMemory,
        ImageView pyramidView,
        ImageView[] levelViews,
        Image[] images,
        DeviceMemory[] memory,
        ImageView[] views)
    {
        _context = context;
        _vk = context.Api;
        _device = context.Device;
        _width = width;
        _height = height;
        _downsample = downsample;
        _march = march;
        _pool = pool;
        _sampler = sampler;
        _uniform = uniform;
        _pyramid = pyramid;
        _pyramidMemory = pyramidMemory;
        _pyramidView = pyramidView;
        _levelViews = levelViews;
        _images = images;
        _memory = memory;
        _views = views;
    }

    /// <summary>What the compositing pass adds, weighted by its own alpha.</summary>
    public ImageView Reflected => _views[_frame & 1];

    /// <summary>Both buffers, in the order a compositing pass should index them.</summary>
    public ReadOnlySpan<ImageView> Buffers => _views;

    /// <summary>Which of the two this frame's answer landed in.</summary>
    public int Parity => _frame & 1;

    /// <summary>Builds both stages and everything they read and write.</summary>
    /// <param name="context">The device.</param>
    /// <param name="compiler">Compiler for the stages.</param>
    /// <param name="width">Viewport width in pixels.</param>
    /// <param name="height">Viewport height in pixels.</param>
    /// <returns>The pass.</returns>
    public static Reflections Create(
        VulkanContext context, ShaderCompiler compiler, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(compiler);

        Vk vk = context.Api;
        Device device = context.Device;

        ComputePipeline downsample = ComputePipeline.Create(
            context, compiler, ReflectionShaders.ComposeDownsample(), Bindings(), 12);

        ComputePipeline march = ComputePipeline.Create(
            context, compiler, ReflectionShaders.ComposeMarch(), Bindings(), 12);

        var samplerInfo = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Linear,
            MinFilter = Filter.Linear,
            AddressModeU = SamplerAddressMode.ClampToEdge,
            AddressModeV = SamplerAddressMode.ClampToEdge,
            AddressModeW = SamplerAddressMode.ClampToEdge,
            MaxLod = Levels,
        };

        vk.CreateSampler(device, in samplerInfo, null, out Sampler sampler);

        var poolSizes = stackalloc DescriptorPoolSize[]
        {
            new DescriptorPoolSize(DescriptorType.SampledImage, 128),
            new DescriptorPoolSize(DescriptorType.Sampler, 16),
            new DescriptorPoolSize(DescriptorType.StorageImage, 64),
            new DescriptorPoolSize(DescriptorType.UniformBuffer, 32),
        };

        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            PoolSizeCount = 4,
            PPoolSizes = poolSizes,
            MaxSets = Levels + 4,
        };

        vk.CreateDescriptorPool(device, in poolInfo, null, out DescriptorPool pool);

        var uniform = VulkanBuffer.CreateHostVisible(
            context, (ulong)Marshal.SizeOf<Uniforms>(), BufferUsageFlags.UniformBufferBit);

        (Image pyramid, DeviceMemory pyramidMemory, ImageView pyramidView) = CreateImage(
            context, width, height, Format.R32Sfloat, Levels);

        var levelViews = new ImageView[Levels];

        for (int i = 0; i < Levels; i++)
        {
            levelViews[i] = CreateView(context, pyramid, Format.R32Sfloat, i);
        }

        var images = new Image[2];
        var memory = new DeviceMemory[2];
        var views = new ImageView[2];

        for (int i = 0; i < 2; i++)
        {
            (images[i], memory[i], views[i]) = CreateImage(
                context, width, height, Format.R16G16B16A16Sfloat, 1);
        }

        return new Reflections(
            context, width, height, downsample, march, pool, sampler, uniform,
            pyramid, pyramidMemory, pyramidView, levelViews, images, memory, views);
    }

    /// <summary>Points both stages at the frame's targets.</summary>
    /// <param name="depth">The frame's depth.</param>
    /// <param name="normal">The frame's normals, with roughness in their alpha.</param>
    /// <param name="motion">The frame's motion vectors.</param>
    /// <param name="lit">The previous frame's finished picture.</param>
    public void Bind(ImageView depth, ImageView normal, ImageView motion, ImageView lit)
    {
        _levelSets = Allocate(_downsample.SetLayout, Levels);
        _marchSets = Allocate(_march.SetLayout, 2);

        var depthInfo = Read(depth);
        var normalInfo = Read(normal);
        var motionInfo = Read(motion);
        var litInfo = Read(lit);
        // Both of these are storage images the rest of the time, and a storage image
        // stays in General; a descriptor has to say the layout the image is actually in.
        var pyramidInfo = Write(_pyramidView);
        var samplerInfo = new DescriptorImageInfo { Sampler = _sampler };

        var uniformInfo = new DescriptorBufferInfo
        {
            Buffer = _uniform.Handle,
            Range = (ulong)Marshal.SizeOf<Uniforms>(),
        };

        var writes = new List<WriteDescriptorSet>();

        for (int i = 0; i < Levels + 2; i++)
        {
            bool marching = i >= Levels;
            DescriptorSet set = marching ? _marchSets[i - Levels] : _levelSets[i];

            // While marching, the two reflection images take turns: one holds what the
            // last frame settled on, the other takes this frame's answer.
            var historyInfo = Write(marching ? _views[1 - (i - Levels)] : _views[0]);
            var resultInfo = Write(marching ? _views[i - Levels] : _views[0]);
            var levelInfo = Write(marching ? _levelViews[0] : _levelViews[i]);

            writes.Add(Sampled(set, 0, &depthInfo));
            writes.Add(Sampled(set, 1, &normalInfo));
            writes.Add(Sampled(set, 2, &motionInfo));
            writes.Add(Sampled(set, 3, &litInfo));
            writes.Add(Sampled(set, 4, &pyramidInfo));
            writes.Add(Sampled(set, 5, &historyInfo));
            writes.Add(new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = set,
                DstBinding = 6,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.Sampler,
                PImageInfo = &samplerInfo,
            });
            writes.Add(Storage(set, 7, &resultInfo));
            writes.Add(Storage(set, 8, &levelInfo));
            writes.Add(new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = set,
                DstBinding = 9,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.UniformBuffer,
                PBufferInfo = &uniformInfo,
            });

            Commit(writes);
        }
    }

    /// <summary>Records the pyramid and the march.</summary>
    /// <param name="command">Command buffer to record into.</param>
    /// <param name="camera">The camera the frame was drawn from.</param>
    /// <param name="roughest">The roughest surface still worth a ray.</param>
    public void Record(CommandBuffer command, Camera camera, float roughest)
    {
        ArgumentNullException.ThrowIfNull(camera);

        float aspect = (float)_width / _height;
        Matrix4x4 projection = camera.Projection(aspect);
        Matrix4x4 viewProjection = camera.View * projection;

        Matrix4x4.Invert(projection, out Matrix4x4 inverseProjection);
        Matrix4x4.Invert(viewProjection, out Matrix4x4 inverseViewProjection);

        _frame++;

        _uniform.Write<Uniforms>(
        [
            new Uniforms(
                projection,
                inverseProjection,
                camera.View,
                inverseViewProjection,
                new Vector4(camera.Position, (_frame % 64) * 0.61803398875f),
                _width,
                _height,
                1f / _width,
                1f / _height,

                // How far behind a surface a hit may land and still be that surface. In
                // scene units, where a hotel room is about a thousand across.
                new Vector4(250f, roughest, Levels, 0f)),
        ]);

        _vk.CmdBindPipeline(command, PipelineBindPoint.Compute, _downsample.Handle);

        for (int i = 0; i < Levels; i++)
        {
            int width = Math.Max(1, _width >> i);
            int height = Math.Max(1, _height >> i);

            DescriptorSet set = _levelSets[i];

            _vk.CmdBindDescriptorSets(
                command, PipelineBindPoint.Compute, _downsample.Layout, 0, 1, in set, 0, null);

            var level = new LevelConstants(width, height, i);

            _vk.CmdPushConstants(
                command, _downsample.Layout, ShaderStageFlags.ComputeBit, 0, 12, &level);

            _vk.CmdDispatch(command, (uint)Divide(width, 8), (uint)Divide(height, 8), 1);
            Barrier(command);
        }

        DescriptorSet marching = _marchSets[_frame & 1];

        _vk.CmdBindPipeline(command, PipelineBindPoint.Compute, _march.Handle);
        _vk.CmdBindDescriptorSets(
            command, PipelineBindPoint.Compute, _march.Layout, 0, 1, in marching, 0, null);

        _vk.CmdDispatch(command, (uint)Divide(_width, 8), (uint)Divide(_height, 8), 1);
        Barrier(command);
    }

    /// <summary>Puts every image this owns into the layout the stages expect.</summary>
    /// <param name="command">Command buffer to record into.</param>
    public void Settle(CommandBuffer command)
    {
        _context.Transition(command, _pyramid, ImageLayout.Undefined, ImageLayout.General);

        foreach (Image image in _images)
        {
            _context.Transition(command, image, ImageLayout.Undefined, ImageLayout.General);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _vk.DeviceWaitIdle(_device);

        foreach (ImageView view in _levelViews)
        {
            if (view.Handle != 0)
            {
                _vk.DestroyImageView(_device, view, null);
            }
        }

        _vk.DestroyImageView(_device, _pyramidView, null);
        _vk.DestroyImage(_device, _pyramid, null);
        _vk.FreeMemory(_device, _pyramidMemory, null);

        for (int i = 0; i < _views.Length; i++)
        {
            _vk.DestroyImageView(_device, _views[i], null);
            _vk.DestroyImage(_device, _images[i], null);
            _vk.FreeMemory(_device, _memory[i], null);
        }

        _vk.DestroyDescriptorPool(_device, _pool, null);
        _vk.DestroySampler(_device, _sampler, null);
        _uniform.Dispose();

        _march.Dispose();
        _downsample.Dispose();
    }

    private static int Divide(int value, int divisor) => (value + divisor - 1) / divisor;

    private static DescriptorSetLayoutBinding[] Bindings() =>
    [
        Binding(0, DescriptorType.SampledImage),
        Binding(1, DescriptorType.SampledImage),
        Binding(2, DescriptorType.SampledImage),
        Binding(3, DescriptorType.SampledImage),
        Binding(4, DescriptorType.SampledImage),
        Binding(5, DescriptorType.SampledImage),
        Binding(6, DescriptorType.Sampler),
        Binding(7, DescriptorType.StorageImage),
        Binding(8, DescriptorType.StorageImage),
        Binding(9, DescriptorType.UniformBuffer),
    ];

    private static DescriptorSetLayoutBinding Binding(uint index, DescriptorType type) =>
        new()
        {
            Binding = index,
            DescriptorType = type,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.ComputeBit,
        };

    private static DescriptorImageInfo Read(ImageView view) =>
        new() { ImageView = view, ImageLayout = ImageLayout.ShaderReadOnlyOptimal };

    private static DescriptorImageInfo Write(ImageView view) =>
        new() { ImageView = view, ImageLayout = ImageLayout.General };

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

    private static (Image, DeviceMemory, ImageView) CreateImage(
        VulkanContext context, int width, int height, Format format, int mips)
    {
        Vk vk = context.Api;
        Device device = context.Device;

        var imageInfo = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = format,
            Extent = new Extent3D((uint)width, (uint)height, 1),
            MipLevels = (uint)mips,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit,
            InitialLayout = ImageLayout.Undefined,
        };

        if (vk.CreateImage(device, in imageInfo, null, out Image image) != Result.Success)
        {
            throw new VulkanException("Could not create a reflection image.");
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
                AspectMask = ImageAspectFlags.ColorBit,
                LevelCount = (uint)mips,
                LayerCount = 1,
            },
        };

        if (vk.CreateImageView(device, in viewInfo, null, out ImageView view) != Result.Success)
        {
            throw new VulkanException("Could not create a reflection image view.");
        }

        return (image, memory, view);
    }

    // One level of a pyramid, which is what a storage image can be written through: a
    // storage image is always one level, however many the image behind it has.
    private static ImageView CreateView(VulkanContext context, Image image, Format format, int level)
    {
        var viewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = image,
            ViewType = ImageViewType.Type2D,
            Format = format,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = (uint)level,
                LevelCount = 1,
                LayerCount = 1,
            },
        };

        if (context.Api.CreateImageView(context.Device, in viewInfo, null, out ImageView view) !=
            Result.Success)
        {
            throw new VulkanException("Could not create a pyramid level view.");
        }

        return view;
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
                throw new VulkanException("Could not allocate the reflection descriptor sets.");
            }
        }

        return sets;
    }

    private void Commit(List<WriteDescriptorSet> writes)
    {
        WriteDescriptorSet[] array = [.. writes];

        fixed (WriteDescriptorSet* pointer = array)
        {
            _vk.UpdateDescriptorSets(_device, (uint)array.Length, pointer, 0, null);
        }

        writes.Clear();
    }

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
            0, 1, in barrier, 0, null, 0, null);
    }

    /// <summary>Which level of the pyramid is being written, and how big it is.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct LevelConstants(int Width, int Height, int Level);

    /// <summary>What the marching stage reads, once a frame.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Uniforms(
        Matrix4x4 Projection,
        Matrix4x4 InverseProjection,
        Matrix4x4 View,
        Matrix4x4 InverseViewProjection,
        Vector4 EyeAndSeed,
        int Width,
        int Height,
        float InverseWidth,
        float InverseHeight,
        Vector4 Tuning);
}
