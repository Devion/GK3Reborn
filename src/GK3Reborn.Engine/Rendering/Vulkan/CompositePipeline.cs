// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System;
using Silk.NET.Vulkan;

namespace GK3Reborn.Rendering.Vulkan;

/// <summary>Puts the room back together from its parts.</summary>
/// <remarks>
/// The mesh pass writes the two halves of the lighting separately and shadows neither of
/// them: what a lamp would give a pixel with nothing in the way, and what the bake and the
/// ambient give it with nothing above it. Both occlusion terms are traced and filtered
/// afterwards, by which time the geometry is long gone, so the multiply happens here — one
/// triangle over the whole frame, four samples a pixel.
/// </remarks>
internal sealed unsafe class CompositePipeline : IDisposable
{
    private const string Vertex = """
        #version 460

        layout(location = 0) out vec2 outUv;

        void main()
        {
            // One triangle covering the frame, from nothing but the vertex index.
            outUv = vec2((gl_VertexIndex << 1) & 2, gl_VertexIndex & 2);
            gl_Position = vec4((outUv * 2.0) - 1.0, 0.0, 1.0);
        }
        """;

    private const string Fragment = """
        #version 460

        layout(location = 0) in vec2 inUv;
        layout(location = 0) out vec4 outColor;

        layout(set = 0, binding = 0) uniform sampler2D indirectTarget;
        layout(set = 0, binding = 1) uniform sampler2D directTarget;
        layout(set = 0, binding = 2) uniform sampler2D shadowTarget;
        layout(set = 0, binding = 3) uniform sampler2D occlusionTarget;

        void main()
        {
            ivec2 pixel = ivec2(gl_FragCoord.xy);

            vec3 indirect = texelFetch(indirectTarget, pixel, 0).rgb;
            vec3 direct = texelFetch(directTarget, pixel, 0).rgb;
            float shadow = clamp(texelFetch(shadowTarget, pixel, 0).r, 0.0, 1.0);
            float occlusion = clamp(texelFetch(occlusionTarget, pixel, 0).r, 0.0, 1.0);

            outColor = vec4((indirect * occlusion) + (direct * shadow), 1.0);
        }
        """;

    private readonly Vk _vk;
    private readonly Device _device;
    private readonly ShaderModule _vertexModule;
    private readonly ShaderModule _fragmentModule;
    private readonly DescriptorSetLayout _setLayout;
    private readonly DescriptorPool _pool;
    private readonly Sampler _sampler;

    private DescriptorSet _set;

    private CompositePipeline(
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

    /// <summary>Builds the pass.</summary>
    /// <param name="context">The device.</param>
    /// <param name="compiler">Compiler for the two stages.</param>
    /// <param name="colorFormat">Format of the image being composited into.</param>
    /// <returns>The pass.</returns>
    public static CompositePipeline Create(
        VulkanContext context, ShaderCompiler compiler, Format colorFormat)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(compiler);

        Vk vk = context.Api;
        Device device = context.Device;

        ShaderModule vertexModule = Module(vk, device, compiler, Vertex, ShaderStage.Vertex);
        ShaderModule fragmentModule = Module(vk, device, compiler, Fragment, ShaderStage.Fragment);

        DescriptorSetLayoutBinding* bindings =
            stackalloc DescriptorSetLayoutBinding[4];

        for (uint i = 0; i < 4; i++)
        {
            bindings[i] = new DescriptorSetLayoutBinding
            {
                Binding = i,
                DescriptorType = DescriptorType.CombinedImageSampler,
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.FragmentBit,
            };
        }

        var layoutInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 4,
            PBindings = bindings,
        };

        vk.CreateDescriptorSetLayout(device, in layoutInfo, null, out DescriptorSetLayout setLayout);

        DescriptorSetLayout local = setLayout;

        var pipelineLayoutInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1,
            PSetLayouts = &local,
        };

        vk.CreatePipelineLayout(device, in pipelineLayoutInfo, null, out PipelineLayout layout);

        var poolSize = new DescriptorPoolSize(DescriptorType.CombinedImageSampler, 8);

        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            PoolSizeCount = 1,
            PPoolSizes = &poolSize,
            MaxSets = 2,
        };

        vk.CreateDescriptorPool(device, in poolInfo, null, out DescriptorPool pool);

        var samplerInfo = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Nearest,
            MinFilter = Filter.Nearest,
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

        // No depth at all: this covers the frame and everything in it has already been
        // depth-tested once.
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
            throw new VulkanException("Could not create the compositing pipeline.");
        }

        return new CompositePipeline(
            vk, device, vertexModule, fragmentModule, setLayout, layout, handle, pool, sampler);
    }

    /// <summary>Points the pass at the four things it reads.</summary>
    /// <param name="indirect">Ambient and baked light, before occlusion.</param>
    /// <param name="direct">The rig's light, before shadowing.</param>
    /// <param name="shadow">The denoised fraction of that light which arrives.</param>
    /// <param name="occlusion">The denoised fraction of the hemisphere that is open.</param>
    public void Bind(ImageView indirect, ImageView direct, ImageView shadow, ImageView occlusion)
    {
        var info = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _pool,
            DescriptorSetCount = 1,
        };

        DescriptorSetLayout layout = _setLayout;
        info.PSetLayouts = &layout;

        fixed (DescriptorSet* set = &_set)
        {
            if (_vk.AllocateDescriptorSets(_device, in info, set) != Result.Success)
            {
                throw new VulkanException("Could not allocate the compositing descriptor set.");
            }
        }

        DescriptorImageInfo* images = stackalloc DescriptorImageInfo[4];
        ImageView[] views = [indirect, direct, shadow, occlusion];
        WriteDescriptorSet* writes = stackalloc WriteDescriptorSet[4];

        for (uint i = 0; i < 4; i++)
        {
            images[i] = new DescriptorImageInfo
            {
                Sampler = _sampler,
                ImageView = views[i],

                // The two denoised terms are storage images and stay in General; the two
                // colour targets are read after being written as attachments.
                ImageLayout = i < 2
                    ? ImageLayout.ShaderReadOnlyOptimal
                    : ImageLayout.General,
            };

            writes[i] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = _set,
                DstBinding = i,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.CombinedImageSampler,
                PImageInfo = &images[i],
            };
        }

        _vk.UpdateDescriptorSets(_device, 4, writes, 0, null);
    }

    /// <summary>Draws the frame.</summary>
    /// <param name="command">Command buffer, inside an active rendering scope.</param>
    /// <param name="width">Viewport width.</param>
    /// <param name="height">Viewport height.</param>
    public void Record(CommandBuffer command, int width, int height)
    {
        var viewport = new Viewport { Width = width, Height = height, MaxDepth = 1f };
        var scissor = new Rect2D { Extent = new Extent2D((uint)width, (uint)height) };

        _vk.CmdSetViewport(command, 0, 1, in viewport);
        _vk.CmdSetScissor(command, 0, 1, in scissor);
        _vk.CmdBindPipeline(command, PipelineBindPoint.Graphics, Handle);
        _vk.CmdBindDescriptorSets(
            command, PipelineBindPoint.Graphics, Layout, 0, 1, in _set, 0, null);

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
        byte[] code = compiler.Compile(source, stage, "composite", "main", ShaderLanguage.Glsl);

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
                throw new VulkanException("Could not create a compositing shader module.");
            }

            return module;
        }
    }
}
