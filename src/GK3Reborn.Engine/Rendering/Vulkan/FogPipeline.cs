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
/// Marches the room's fog over the finished picture.
/// </summary>
/// <remarks>
/// <para>
/// One triangle, blended over whatever the room came to. What the shader writes is
/// premultiplied — the light the fog put in, and in alpha the light it stopped — so the
/// picture is finished in one blend and this pass never has to read the target it is
/// drawing onto. That is what lets it run against the lit target directly instead of
/// needing a copy of it.
/// </para>
/// <para>
/// The three light buffers are the frame's own, bound rather than copied. They are written
/// once when a room loads and read by everything that shades in it, and a rig this pass had
/// a private copy of would be a second place for a light to move.
/// </para>
/// </remarks>
public sealed unsafe class FogPipeline : IDisposable
{
    private static string Vertex => CompositeShaders.Vertex;

    private static string Fragment => FogShaders.Fragment;

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
    private bool _bound;

    private FogPipeline(VulkanContext context)
    {
        _context = context;
        _vk = context.Api;
    }

    /// <summary>Whether the pass has been told where to read its rig and depth from.</summary>
    /// <remarks>
    /// A draw before that is a read through descriptors nothing has written, which is not a
    /// dark pixel but a device removed. The renderer binds when the targets are made and
    /// again whenever they are remade; this is what makes forgetting it a no-op rather than
    /// a crash.
    /// </remarks>
    public bool Ready => _bound;

    /// <summary>Builds the pass.</summary>
    /// <param name="context">The device it belongs to.</param>
    /// <param name="colorFormat">Format of the picture it draws over.</param>
    /// <param name="compiler">What compiles the two stages.</param>
    /// <returns>The pass.</returns>
    public static FogPipeline Create(
        VulkanContext context, Format colorFormat, ShaderCompiler compiler)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(compiler);

        FogLayout.Bindings.Validate();

        var pipeline = new FogPipeline(context);

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

    /// <summary>Points the pass at the rig it lights the fog with and the depth it stops at.</summary>
    /// <param name="rig">The lights, as the mesh pass has them.</param>
    /// <param name="cells">Where each cell of the light grid starts.</param>
    /// <param name="reaching">Which lights are in each cell.</param>
    /// <param name="depth">The depth the room left, in shader-read layout when it is drawn.</param>
    public void Bind(
        VulkanBuffer rig, VulkanBuffer cells, VulkanBuffer reaching, ImageView depth)
    {
        ArgumentNullException.ThrowIfNull(rig);
        ArgumentNullException.ThrowIfNull(cells);
        ArgumentNullException.ThrowIfNull(reaching);

        DescriptorBufferInfo* buffers = stackalloc DescriptorBufferInfo[3];
        buffers[0] = new DescriptorBufferInfo { Buffer = rig.Handle, Range = Vk.WholeSize };
        buffers[1] = new DescriptorBufferInfo { Buffer = cells.Handle, Range = Vk.WholeSize };
        buffers[2] = new DescriptorBufferInfo { Buffer = reaching.Handle, Range = Vk.WholeSize };

        var image = new DescriptorImageInfo
        {
            Sampler = _sampler,
            ImageView = depth,
            ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
        };

        WriteDescriptorSet* writes = stackalloc WriteDescriptorSet[4];

        for (uint i = 0; i < 3; i++)
        {
            writes[i] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = _set,
                DstBinding = i,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.StorageBuffer,
                PBufferInfo = &buffers[i],
            };
        }

        writes[3] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = _set,
            DstBinding = 3,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.CombinedImageSampler,
            PImageInfo = &image,
        };

        _vk.UpdateDescriptorSets(_context.Device, 4, writes, 0, null);
        _bound = true;
    }

    /// <summary>Draws the fog.</summary>
    /// <param name="command">Command buffer, inside an active rendering scope.</param>
    /// <param name="width">Viewport width in pixels.</param>
    /// <param name="height">Its height.</param>
    /// <param name="constants">What the march is told.</param>
    public void Record(CommandBuffer command, int width, int height, in FogConstants constants)
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

        fixed (FogConstants* pushed = &constants)
        {
            _vk.CmdPushConstants(
                command,
                _layout,
                ShaderStageFlags.FragmentBit,
                0,
                (uint)sizeof(FogConstants),
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

        DescriptorSetLayoutBinding* bindings = stackalloc DescriptorSetLayoutBinding[4];

        for (uint i = 0; i < 3; i++)
        {
            bindings[i] = new DescriptorSetLayoutBinding
            {
                Binding = i,
                DescriptorType = DescriptorType.StorageBuffer,
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.FragmentBit,
            };
        }

        bindings[3] = new DescriptorSetLayoutBinding
        {
            Binding = 3,
            DescriptorType = DescriptorType.CombinedImageSampler,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.FragmentBit,
        };

        var setInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 4,
            PBindings = bindings,
        };

        if (_vk.CreateDescriptorSetLayout(device, in setInfo, null, out _setLayout) != Result.Success)
        {
            throw new VulkanException("Could not create the fog descriptor set layout.");
        }

        DescriptorPoolSize* sizes = stackalloc DescriptorPoolSize[2];
        sizes[0] = new DescriptorPoolSize(DescriptorType.StorageBuffer, 3);
        sizes[1] = new DescriptorPoolSize(DescriptorType.CombinedImageSampler, 1);

        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            PoolSizeCount = 2,
            PPoolSizes = sizes,
            MaxSets = 1,
        };

        if (_vk.CreateDescriptorPool(device, in poolInfo, null, out _pool) != Result.Success)
        {
            throw new VulkanException("Could not create the fog descriptor pool.");
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
            throw new VulkanException("Could not allocate the fog descriptor set.");
        }

        // Nearest and clamped. The depth is read once across its own extent at its own
        // resolution, and a filtered depth halfway between a near surface and a far one is
        // a distance nothing in the room is at.
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
            throw new VulkanException("Could not create the fog depth sampler.");
        }
    }

    private void BuildPipeline(Format colorFormat)
    {
        Device device = _context.Device;

        var range = new PushConstantRange
        {
            StageFlags = ShaderStageFlags.FragmentBit,
            Offset = 0,
            Size = (uint)sizeof(FogConstants),
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
            throw new VulkanException("Could not create the fog pipeline layout.");
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

        // The depth is what this pass reads, not what it tests against: the march has
        // already stopped at whatever the room drew.
        var depthStencil = new PipelineDepthStencilStateCreateInfo
        {
            SType = StructureType.PipelineDepthStencilStateCreateInfo,
        };

        // Premultiplied. What the shader writes is already weighted by how much of it
        // survives to the eye, and alpha is how much of the picture behind it does not — so
        // the source is added whole and the destination is scaled by what got through.
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
            throw new VulkanException("Could not create the fog pipeline.");
        }
    }

    private ShaderModule Module(ShaderCompiler compiler, string source, ShaderStage stage)
    {
        byte[] code = compiler.Compile(source, stage, "fog", "main", ShaderLanguage.Glsl);

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
                throw new VulkanException("Could not create a fog shader module.");
            }

            return module;
        }
    }
}
