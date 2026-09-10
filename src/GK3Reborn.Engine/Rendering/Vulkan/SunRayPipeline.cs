// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using GK3Reborn.Rendering.Geometry;
using GK3Reborn.Rendering.Shaders;
using Silk.NET.Vulkan;

namespace GK3Reborn.Rendering.Vulkan;

/// <summary>
/// Draws the sun's rays over the finished picture.
/// </summary>
public sealed unsafe class SunRayPipeline : IDisposable
{
    private static string Vertex => CompositeShaders.Vertex;

    private static string Fragment => SunRayShaders.Fragment;

    private readonly Vk _vk;
    private readonly VulkanContext _context;

    private ShaderModule _vertexModule;
    private ShaderModule _fragmentModule;
    private DescriptorSetLayout _setLayout;
    private DescriptorPool _pool;
    private DescriptorSet _set;
    private Sampler _sampler;
    private PipelineLayout _layout;
    private Pipeline _pipeline;
    private VulkanBuffer? _shafts;
    private bool _bound;

    private SunRayPipeline(VulkanContext context)
    {
        _context = context;
        _vk = context.Api;
    }

    /// <summary>Whether the pass has been told where to read its depth from.</summary>
    public bool Ready => _bound;

    /// <summary>Builds the pass.</summary>
    /// <param name="context">The device it belongs to.</param>
    /// <param name="colorFormat">Format of the picture it draws over.</param>
    /// <param name="compiler">What compiles the two stages.</param>
    /// <returns>The pass.</returns>
    public static SunRayPipeline Create(
        VulkanContext context, Format colorFormat, ShaderCompiler compiler)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(compiler);

        SunRayLayout.Bindings.Validate();

        var pipeline = new SunRayPipeline(context);

        try
        {
            pipeline._vertexModule = pipeline.Module(compiler, Vertex, ShaderStage.Vertex);
            pipeline._fragmentModule = pipeline.Module(compiler, Fragment, ShaderStage.Fragment);

            pipeline.BuildSet();
            pipeline.BuildPipeline(colorFormat);

            return pipeline;
        }
        catch
        {
            pipeline.Dispose();
            throw;
        }
    }

    /// <summary>Points the pass at the depth it walks over.</summary>
    /// <param name="depth">The depth the room left, in shader-read layout when it is drawn.</param>
    public void Bind(ImageView depth)
    {
        var image = new DescriptorImageInfo
        {
            Sampler = _sampler,
            ImageView = depth,
            ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
        };

        var buffer = new DescriptorBufferInfo { Buffer = _shafts!.Handle, Range = Vk.WholeSize };

        WriteDescriptorSet* writes = stackalloc WriteDescriptorSet[2];

        writes[0] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = _set,
            DstBinding = 0,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.CombinedImageSampler,
            PImageInfo = &image,
        };

        writes[1] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = _set,
            DstBinding = 1,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.StorageBuffer,
            PBufferInfo = &buffer,
        };

        _vk.UpdateDescriptorSets(_context.Device, 2, writes, 0, null);
        _bound = true;
    }

    /// <summary>Gives the pass the room's shafts of daylight.</summary>
    /// <param name="shafts">The shafts, or none.</param>
    /// <remarks>
    /// Written straight into the buffer the last frame may still be reading. A room's
    /// shafts change at its door, where the renderer has a great deal else to rebuild
    /// and the one frame that might read a half-written box is one nobody sees.
    /// </remarks>
    public void Shafts(IReadOnlyList<LightShaft> shafts)
    {
        ArgumentNullException.ThrowIfNull(shafts);
        _shafts?.Write<byte>(SunRayLayout.PackShafts(shafts));
    }

    /// <summary>Draws the rays.</summary>
    /// <param name="command">Command buffer, inside an active rendering scope.</param>
    /// <param name="width">Viewport width in pixels.</param>
    /// <param name="height">Its height.</param>
    /// <param name="constants">What the walk is told.</param>
    public void Record(CommandBuffer command, int width, int height, in SunRayConstants constants)
    {
        if (!_bound)
        {
            return;
        }

        var viewport = new Viewport { Width = width, Height = height, MaxDepth = 1f };
        var scissor = new Rect2D { Extent = new Extent2D((uint)width, (uint)height) };

        _vk.CmdSetViewport(command, 0, 1, in viewport);
        _vk.CmdSetScissor(command, 0, 1, in scissor);
        _vk.CmdBindPipeline(command, PipelineBindPoint.Graphics, _pipeline);

        DescriptorSet set = _set;
        _vk.CmdBindDescriptorSets(
            command, PipelineBindPoint.Graphics, _layout, 0, 1, in set, 0, null);

        fixed (SunRayConstants* pushed = &constants)
        {
            _vk.CmdPushConstants(
                command,
                _layout,
                ShaderStageFlags.FragmentBit,
                0,
                (uint)sizeof(SunRayConstants),
                pushed);
        }

        _vk.CmdDraw(command, 3, 1, 0, 0);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Device device = _context.Device;

        if (_pipeline.Handle != 0)
        {
            _vk.DestroyPipeline(device, _pipeline, null);
            _pipeline = default;
        }

        if (_layout.Handle != 0)
        {
            _vk.DestroyPipelineLayout(device, _layout, null);
            _layout = default;
        }

        if (_pool.Handle != 0)
        {
            _vk.DestroyDescriptorPool(device, _pool, null);
            _pool = default;
        }

        if (_setLayout.Handle != 0)
        {
            _vk.DestroyDescriptorSetLayout(device, _setLayout, null);
            _setLayout = default;
        }

        if (_sampler.Handle != 0)
        {
            _vk.DestroySampler(device, _sampler, null);
            _sampler = default;
        }

        _shafts?.Dispose();
        _shafts = null;

        if (_fragmentModule.Handle != 0)
        {
            _vk.DestroyShaderModule(device, _fragmentModule, null);
            _fragmentModule = default;
        }

        if (_vertexModule.Handle != 0)
        {
            _vk.DestroyShaderModule(device, _vertexModule, null);
            _vertexModule = default;
        }

        GC.SuppressFinalize(this);
    }

    private void BuildSet()
    {
        Device device = _context.Device;

        // The shafts, empty until a room hands some over. Host-visible: it is written once
        // a room and read every frame.
        _shafts = VulkanBuffer.CreateHostVisible(
            _context, (ulong)SunRayLayout.ShaftBufferBytes, BufferUsageFlags.StorageBufferBit);
        _shafts.Write<byte>(SunRayLayout.PackShafts([]));

        DescriptorSetLayoutBinding* bindings = stackalloc DescriptorSetLayoutBinding[2];

        bindings[0] = new DescriptorSetLayoutBinding
        {
            Binding = 0,
            DescriptorType = DescriptorType.CombinedImageSampler,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.FragmentBit,
        };

        bindings[1] = new DescriptorSetLayoutBinding
        {
            Binding = 1,
            DescriptorType = DescriptorType.StorageBuffer,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.FragmentBit,
        };

        var setInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 2,
            PBindings = bindings,
        };

        if (_vk.CreateDescriptorSetLayout(device, in setInfo, null, out _setLayout) != Result.Success)
        {
            throw new VulkanException("Could not create the sun ray descriptor set layout.");
        }

        DescriptorPoolSize* sizes = stackalloc DescriptorPoolSize[2];
        sizes[0] = new DescriptorPoolSize(DescriptorType.CombinedImageSampler, 1);
        sizes[1] = new DescriptorPoolSize(DescriptorType.StorageBuffer, 1);

        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            PoolSizeCount = 2,
            PPoolSizes = sizes,
            MaxSets = 1,
        };

        if (_vk.CreateDescriptorPool(device, in poolInfo, null, out _pool) != Result.Success)
        {
            throw new VulkanException("Could not create the sun ray descriptor pool.");
        }

        DescriptorSetLayout layout = _setLayout;

        var allocate = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _pool,
            DescriptorSetCount = 1,
            PSetLayouts = &layout,
        };

        if (_vk.AllocateDescriptorSets(device, in allocate, out _set) != Result.Success)
        {
            throw new VulkanException("Could not allocate the sun ray descriptor set.");
        }

        // Nearest and clamped, for the fog's reason: a filtered depth halfway between a
        // near surface and the sky is a distance nothing is at.
        var samplerInfo = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Nearest,
            MinFilter = Filter.Nearest,
            AddressModeU = SamplerAddressMode.ClampToEdge,
            AddressModeV = SamplerAddressMode.ClampToEdge,
            AddressModeW = SamplerAddressMode.ClampToEdge,
        };

        if (_vk.CreateSampler(device, in samplerInfo, null, out _sampler) != Result.Success)
        {
            throw new VulkanException("Could not create the sun ray depth sampler.");
        }
    }

    private void BuildPipeline(Format colorFormat)
    {
        Device device = _context.Device;

        var range = new PushConstantRange
        {
            StageFlags = ShaderStageFlags.FragmentBit,
            Offset = 0,
            Size = (uint)sizeof(SunRayConstants),
        };

        DescriptorSetLayout setLayout = _setLayout;

        var layoutInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1,
            PSetLayouts = &setLayout,
            PushConstantRangeCount = 1,
            PPushConstantRanges = &range,
        };

        if (_vk.CreatePipelineLayout(device, in layoutInfo, null, out _layout) != Result.Success)
        {
            throw new VulkanException("Could not create the sun ray pipeline layout.");
        }

        byte* entryPoint = stackalloc byte[] { (byte)'m', (byte)'a', (byte)'i', (byte)'n', 0 };

        PipelineShaderStageCreateInfo* stages = stackalloc PipelineShaderStageCreateInfo[2];
        stages[0] = new PipelineShaderStageCreateInfo
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.VertexBit,
            Module = _vertexModule,
            PName = entryPoint,
        };
        stages[1] = new PipelineShaderStageCreateInfo
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.FragmentBit,
            Module = _fragmentModule,
            PName = entryPoint,
        };

        // Nothing comes in: the triangle is built from the vertex index.
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

        // The depth is what this pass reads, not what it tests against.
        var depthStencil = new PipelineDepthStencilStateCreateInfo
        {
            SType = StructureType.PipelineDepthStencilStateCreateInfo,
        };

        // Premultiplied, like the fog, and what this pass writes has alpha nought: light is
        // added to the picture and nothing behind it is taken away.
        var blendAttachment = new PipelineColorBlendAttachmentState
        {
            BlendEnable = true,
            SrcColorBlendFactor = BlendFactor.One,
            DstColorBlendFactor = BlendFactor.OneMinusSrcAlpha,
            ColorBlendOp = BlendOp.Add,
            SrcAlphaBlendFactor = BlendFactor.One,
            DstAlphaBlendFactor = BlendFactor.OneMinusSrcAlpha,
            AlphaBlendOp = BlendOp.Add,
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
            Layout = _layout,
        };

        if (_vk.CreateGraphicsPipelines(device, default, 1, in createInfo, null, out _pipeline)
            != Result.Success)
        {
            throw new VulkanException("Could not create the sun ray pipeline.");
        }
    }

    private ShaderModule Module(ShaderCompiler compiler, string source, ShaderStage stage)
    {
        byte[] code = compiler.Compile(source, stage, "sunrays", "main", ShaderLanguage.Glsl);

        fixed (byte* spirv = code)
        {
            var info = new ShaderModuleCreateInfo
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)code.Length,
                PCode = (uint*)spirv,
            };

            if (_vk.CreateShaderModule(_context.Device, in info, null, out ShaderModule module)
                != Result.Success)
            {
                throw new VulkanException("Could not create a sun ray shader module.");
            }

            return module;
        }
    }
}
