// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using GK3Reborn.Rendering.Geometry;
using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;

using GK3Reborn.Rendering.Shaders;

namespace GK3Reborn.Rendering.Vulkan;

/// <summary>What the output pass is told about the display, per frame.</summary>
/// <param name="Tuning">
/// x which transfer function, y where paper white sits, z how far above it the display can
/// go, w which tone curve.
/// </param>
/// <param name="Sharpen">
/// x how hard to sharpen — nought for not at all — and yz the size of one source pixel.
/// </param>
[StructLayout(LayoutKind.Sequential)]
internal readonly record struct OutputConstants(Vector4 Tuning, Vector4 Sharpen);

/// <summary>
/// The last thing that happens to a frame: a tone curve, a sharpen, and whatever encoding
/// the display's colour space wants.
/// </summary>
/// <remarks>
/// <para>
/// Everything before this writes linear light into a floating-point target, where a value
/// of one means "diffuse white" and values above it are allowed. That is the only form in
/// which upscaling, ray tracing and HDR all work; it is also not a picture any display can
/// accept. This pass turns it into one.
/// </para>
/// <para>
/// It exists in the standard-range path as well, doing almost nothing — a copy with an
/// optional sharpen — and that is deliberate. Having one place where the frame becomes a
/// picture is what makes the HDR path a different set of push constants rather than a
/// different renderer, and what lets the interface keep being drawn afterwards onto the
/// swapchain in exactly the way it always was.
/// </para>
/// <para>
/// The sharpen is contrast-adaptive: it takes the five-tap cross around a pixel, works out
/// how much local contrast there is to spend, and sharpens by an amount that cannot
/// overshoot the neighbourhood. Run over an already-upscaled picture it is what puts back
/// the acuity a resample costs, and unlike an unsharp mask it will not ring along a hard
/// edge — which in this game means the hotel's door numbers and Sidney's screen text.
/// </para>
/// </remarks>
internal sealed unsafe class OutputPipeline : IDisposable
{
    /// <summary>The hardware encodes: write linear and let the sRGB target do the curve.</summary>
    public const float TransferHardware = 0f;

    /// <summary>ST.2084, in Rec.2020 primaries, with luminance in absolute nits.</summary>
    public const float TransferPerceptualQuantiser = 1f;

    /// <summary>scRGB: linear light in sRGB primaries, where 1.0 is 80 nits.</summary>
    public const float TransferExtendedLinear = 2f;

    private static string Vertex => OutputShaders.Vertex;

    private static string Fragment => OutputShaders.Fragment;

    private readonly Vk _vk;
    private readonly Device _device;
    private readonly ShaderModule _vertexModule;
    private readonly ShaderModule _fragmentModule;
    private readonly DescriptorSetLayout _setLayout;
    private readonly DescriptorPool _pool;
    private readonly Sampler _sampler;

    private DescriptorSet _set;

    private OutputPipeline(
        Vk vk,
        Device device,
        ShaderModule vertexModule,
        ShaderModule fragmentModule,
        DescriptorSetLayout setLayout,
        PipelineLayout layout,
        Pipeline handle,
        DescriptorPool pool,
        Sampler sampler)
    {
        _vk = vk;
        _device = device;
        _vertexModule = vertexModule;
        _fragmentModule = fragmentModule;
        _setLayout = setLayout;
        _pool = pool;
        _sampler = sampler;
        Layout = layout;
        Handle = handle;
    }

    /// <summary>The pipeline.</summary>
    public Pipeline Handle { get; }

    /// <summary>Its layout.</summary>
    public PipelineLayout Layout { get; }

    /// <summary>The format this was built to write into.</summary>
    /// <remarks>
    /// Kept so the renderer can tell whether a swapchain rebuild invalidated it. A pipeline
    /// carries the attachment format it was created with, so one built for an 8-bit sRGB
    /// swapchain cannot be used to write a 10-bit HDR one.
    /// </remarks>
    public Format ColorFormat { get; private set; }

    /// <summary>Builds the pass.</summary>
    /// <param name="context">The device.</param>
    /// <param name="compiler">Compiler for the two stages.</param>
    /// <param name="colorFormat">Format of the swapchain image being written.</param>
    /// <returns>The pass.</returns>
    public static OutputPipeline Create(
        VulkanContext context, ShaderCompiler compiler, Format colorFormat)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(compiler);

        Vk vk = context.Api;
        Device device = context.Device;

        ShaderModule vertexModule = Module(vk, device, compiler, Vertex, ShaderStage.Vertex);
        ShaderModule fragmentModule = Module(vk, device, compiler, Fragment, ShaderStage.Fragment);

        var binding = new DescriptorSetLayoutBinding
        {
            Binding = 0,
            DescriptorType = DescriptorType.CombinedImageSampler,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.FragmentBit,
        };

        var layoutInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 1,
            PBindings = &binding,
        };

        vk.CreateDescriptorSetLayout(device, in layoutInfo, null, out DescriptorSetLayout setLayout);

        DescriptorSetLayout local = setLayout;

        var range = new PushConstantRange
        {
            StageFlags = ShaderStageFlags.FragmentBit,
            Offset = 0,
            Size = (uint)Marshal.SizeOf<OutputConstants>(),
        };

        var pipelineLayoutInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1,
            PSetLayouts = &local,
            PushConstantRangeCount = 1,
            PPushConstantRanges = &range,
        };

        vk.CreatePipelineLayout(device, in pipelineLayoutInfo, null, out PipelineLayout layout);

        var poolSize = new DescriptorPoolSize(DescriptorType.CombinedImageSampler, 8);

        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            PoolSizeCount = 1,
            PPoolSizes = &poolSize,
            MaxSets = 8,

            // Rebound whenever the source changes, which is every time the upscaler is
            // switched on or off from the settings page.
            Flags = DescriptorPoolCreateFlags.FreeDescriptorSetBit,
        };

        vk.CreateDescriptorPool(device, in poolInfo, null, out DescriptorPool pool);

        // Linear, because the sharpen samples between pixels and because a source that is
        // not quite the size of the target — which a driver can hand back after a resize —
        // should stretch rather than alias.
        var samplerInfo = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Linear,
            MinFilter = Filter.Linear,
            AddressModeU = SamplerAddressMode.ClampToEdge,
            AddressModeV = SamplerAddressMode.ClampToEdge,
            AddressModeW = SamplerAddressMode.ClampToEdge,
        };

        vk.CreateSampler(device, in samplerInfo, null, out Sampler sampler);

        byte* entryPoint = stackalloc byte[] { (byte)'m', (byte)'a', (byte)'i', (byte)'n', 0 };

        PipelineShaderStageCreateInfo* stages = stackalloc PipelineShaderStageCreateInfo[2];
        stages[0] = new PipelineShaderStageCreateInfo
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.VertexBit,
            Module = vertexModule,
            PName = entryPoint,
        };
        stages[1] = new PipelineShaderStageCreateInfo
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.FragmentBit,
            Module = fragmentModule,
            PName = entryPoint,
        };

        var vertexInput = new PipelineVertexInputStateCreateInfo
        {
            SType = StructureType.PipelineVertexInputStateCreateInfo,
        };

        var inputAssembly = new PipelineInputAssemblyStateCreateInfo
        {
            SType = StructureType.PipelineInputAssemblyStateCreateInfo,
            Topology = PrimitiveTopology.TriangleList,
        };

        var viewportState = new PipelineViewportStateCreateInfo
        {
            SType = StructureType.PipelineViewportStateCreateInfo,
            ViewportCount = 1,
            ScissorCount = 1,
        };

        var rasterizer = new PipelineRasterizationStateCreateInfo
        {
            SType = StructureType.PipelineRasterizationStateCreateInfo,
            PolygonMode = PolygonMode.Fill,
            CullMode = CullModeFlags.None,
            FrontFace = FrontFace.CounterClockwise,
            LineWidth = 1f,
        };

        var multisampling = new PipelineMultisampleStateCreateInfo
        {
            SType = StructureType.PipelineMultisampleStateCreateInfo,
            RasterizationSamples = SampleCountFlags.Count1Bit,
        };

        var depthStencil = new PipelineDepthStencilStateCreateInfo
        {
            SType = StructureType.PipelineDepthStencilStateCreateInfo,
        };

        var blendAttachment = new PipelineColorBlendAttachmentState
        {
            ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit |
                             ColorComponentFlags.BBit | ColorComponentFlags.ABit,
        };

        var blend = new PipelineColorBlendStateCreateInfo
        {
            SType = StructureType.PipelineColorBlendStateCreateInfo,
            AttachmentCount = 1,
            PAttachments = &blendAttachment,
        };

        DynamicState* dynamic = stackalloc DynamicState[]
        {
            DynamicState.Viewport,
            DynamicState.Scissor,
        };

        var dynamicState = new PipelineDynamicStateCreateInfo
        {
            SType = StructureType.PipelineDynamicStateCreateInfo,
            DynamicStateCount = 2,
            PDynamicStates = dynamic,
        };

        Format format = colorFormat;

        var rendering = new PipelineRenderingCreateInfo
        {
            SType = StructureType.PipelineRenderingCreateInfo,
            ColorAttachmentCount = 1,
            PColorAttachmentFormats = &format,
        };

        var createInfo = new GraphicsPipelineCreateInfo
        {
            SType = StructureType.GraphicsPipelineCreateInfo,
            PNext = &rendering,
            StageCount = 2,
            PStages = stages,
            PVertexInputState = &vertexInput,
            PInputAssemblyState = &inputAssembly,
            PViewportState = &viewportState,
            PRasterizationState = &rasterizer,
            PMultisampleState = &multisampling,
            PDepthStencilState = &depthStencil,
            PColorBlendState = &blend,
            PDynamicState = &dynamicState,
            Layout = layout,
        };

        if (vk.CreateGraphicsPipelines(device, default, 1, in createInfo, null, out Pipeline handle) !=
            Result.Success)
        {
            throw new VulkanException("Could not create the output pipeline.");
        }

        return new OutputPipeline(
            vk, device, vertexModule, fragmentModule, setLayout, layout, handle, pool, sampler)
        {
            ColorFormat = colorFormat,
        };
    }

    /// <summary>Points the pass at the finished picture.</summary>
    /// <param name="picture">The linear frame, at the size it will be shown.</param>
    /// <remarks>
    /// Called whenever that image changes, which is on every resize and every time the
    /// upscaler is switched — the source is the upscaled image when there is one and the
    /// rendered one when there is not.
    /// </remarks>
    public void Bind(ImageView picture)
    {
        if (_set.Handle != 0)
        {
            DescriptorSet previous = _set;
            _vk.FreeDescriptorSets(_device, _pool, 1, in previous);
            _set = default;
        }

        DescriptorSetLayout layout = _setLayout;

        var info = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _pool,
            DescriptorSetCount = 1,
            PSetLayouts = &layout,
        };

        if (_vk.AllocateDescriptorSets(_device, in info, out _set) != Result.Success)
        {
            throw new VulkanException("Could not allocate the output descriptor set.");
        }

        var image = new DescriptorImageInfo
        {
            Sampler = _sampler,
            ImageView = picture,
            ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
        };

        var write = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = _set,
            DstBinding = 0,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.CombinedImageSampler,
            PImageInfo = &image,
        };

        _vk.UpdateDescriptorSets(_device, 1, in write, 0, null);
    }

    /// <summary>Whether anything has been bound to draw.</summary>
    public bool Ready => _set.Handle != 0;

    /// <summary>Draws the frame onto the swapchain.</summary>
    /// <param name="command">Command buffer, inside an active rendering scope.</param>
    /// <param name="width">Swapchain width.</param>
    /// <param name="height">Swapchain height.</param>
    /// <param name="constants">What to tell the shader about the display.</param>
    public void Record(CommandBuffer command, int width, int height, OutputConstants constants)
    {
        if (_set.Handle == 0)
        {
            return;
        }

        var viewport = new Viewport { Width = width, Height = height, MaxDepth = 1f };
        var scissor = new Rect2D { Extent = new Extent2D((uint)width, (uint)height) };

        _vk.CmdSetViewport(command, 0, 1, in viewport);
        _vk.CmdSetScissor(command, 0, 1, in scissor);
        _vk.CmdBindPipeline(command, PipelineBindPoint.Graphics, Handle);

        DescriptorSet set = _set;

        _vk.CmdBindDescriptorSets(
            command, PipelineBindPoint.Graphics, Layout, 0, 1, in set, 0, null);

        OutputConstants pushed = constants;

        _vk.CmdPushConstants(
            command,
            Layout,
            ShaderStageFlags.FragmentBit,
            0,
            (uint)Marshal.SizeOf<OutputConstants>(),
            &pushed);

        _vk.CmdDraw(command, 3, 1, 0, 0);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _vk.DestroyPipeline(_device, Handle, null);
        _vk.DestroyPipelineLayout(_device, Layout, null);
        _vk.DestroyDescriptorPool(_device, _pool, null);
        _vk.DestroyDescriptorSetLayout(_device, _setLayout, null);
        _vk.DestroySampler(_device, _sampler, null);
        _vk.DestroyShaderModule(_device, _fragmentModule, null);
        _vk.DestroyShaderModule(_device, _vertexModule, null);
    }

    private static ShaderModule Module(
        Vk vk, Device device, ShaderCompiler compiler, string source, ShaderStage stage)
    {
        byte[] code = compiler.Compile(source, stage, "output", "main", ShaderLanguage.Glsl);

        fixed (byte* spirv = code)
        {
            var info = new ShaderModuleCreateInfo
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)code.Length,
                PCode = (uint*)spirv,
            };

            if (vk.CreateShaderModule(device, in info, null, out ShaderModule module) !=
                Result.Success)
            {
                throw new VulkanException("Could not create an output shader module.");
            }

            return module;
        }
    }
}
