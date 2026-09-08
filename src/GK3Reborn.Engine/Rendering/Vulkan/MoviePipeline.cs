using System.Numerics;
using GK3Reborn.Formats.Bitmaps;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;

using GK3Reborn.Rendering.Shaders;

namespace GK3Reborn.Rendering.Vulkan;

/// <summary>
/// Draws a movie over everything.
/// </summary>
public sealed unsafe class MoviePipeline : IDisposable
{
    private readonly Vk _vk;
    private readonly VulkanContext _context;

    private ShaderModule _vertexModule;
    private ShaderModule _fragmentModule;
    private DescriptorSetLayout _setLayout;
    private DescriptorPool _pool;
    private DescriptorSet _set;
    private PipelineLayout _layout;
    private Pipeline _pipeline;

    private VulkanTexture? _picture;
    private int _width;
    private int _height;
    private bool _bound;

    private MoviePipeline(Vk vk, VulkanContext context)
    {
        _vk = vk;
        _context = context;
    }

    /// <summary>Whether there is a frame to draw.</summary>
    public bool HasFrame => _picture is not null && _bound;

    /// <summary>What the swapchain wants written into it.</summary>
    public DisplayEncode Display { get; set; } = DisplayEncode.Standard;

    /// <summary>Builds the pass.</summary>
    /// <param name="context">Device context.</param>
    /// <param name="compiler">Shader compiler.</param>
    /// <param name="format">The colour format it draws into.</param>
    /// <returns>The pipeline.</returns>
    public static MoviePipeline Create(
        VulkanContext context, ShaderCompiler compiler, Format format)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(compiler);

        var pipeline = new MoviePipeline(context.Api, context);

        try
        {
            pipeline._vertexModule = Module(
                context, compiler.Compile(
                    MovieShaders.Vertex, ShaderStage.Vertex, "movie.vert", "main", ShaderLanguage.Glsl));

            pipeline._fragmentModule = Module(
                context, compiler.Compile(
                    MovieShaders.Fragment, ShaderStage.Fragment, "movie.frag", "main", ShaderLanguage.Glsl));

            pipeline.BuildLayout();
            pipeline.BuildPipeline(format);

            return pipeline;
        }
        catch
        {
            pipeline.Dispose();
            throw;
        }
    }

    /// <summary>Hands over the frame to draw next.</summary>
    /// <param name="frame">The picture, four bytes a pixel.</param>
    public void SetFrame(DecodedImage frame)
    {
        ArgumentNullException.ThrowIfNull(frame.Pixels);

        if (frame.Width <= 0 || frame.Height <= 0)
        {
            return;
        }

        if (_picture is null || frame.Width != _width || frame.Height != _height)
        {
            _context.Api.DeviceWaitIdle(_context.Device);
            _picture?.Dispose();

            _picture = VulkanTexture.Create(
                _context, frame, mipmaps: false, SamplerAddressMode.ClampToEdge);

            _width = frame.Width;
            _height = frame.Height;
            _bound = false;
        }
        else
        {
            _picture.Refresh(frame.Pixels, frame.Width, frame.Height);
        }

        if (!_bound)
        {
            Bind();
            _bound = true;
        }
    }

    /// <summary>Hands over a still that is already in blocks.</summary>
    /// <param name="picture">The compressed image.</param>
    public void SetPicture(CompressedImage picture)
    {
        if (picture.Blocks.IsEmpty || picture.Width <= 0 || picture.Height <= 0)
        {
            return;
        }

        _context.Api.DeviceWaitIdle(_context.Device);
        _picture?.Dispose();

        _picture = VulkanTexture.Create(_context, picture, SamplerAddressMode.ClampToEdge);

        _width = picture.Width;
        _height = picture.Height;

        Bind();
        _bound = true;
    }

    /// <summary>Lets go of the picture, when a movie has finished.</summary>
    public void Clear()
    {
        if (_picture is null)
        {
            return;
        }

        _context.Api.DeviceWaitIdle(_context.Device);
        _picture.Dispose();
        _picture = null;
        _bound = false;
        _width = 0;
        _height = 0;
    }

    /// <summary>
    /// Whether to fill the window rather than fit inside it.
    /// </summary>
    public bool Cover { get; set; }

    /// <summary>Where the picture lands in the window, in pixels.</summary>
    /// <param name="width">Window width.</param>
    /// <param name="height">Window height.</param>
    /// <returns>Left, top, width and height, or all noughts when there is no picture.</returns>
    public Vector4 Rectangle(int width, int height) =>
        HasFrame ? PictureFit.Rectangle(_width, _height, width, height, Cover) : Vector4.Zero;

    /// <summary>Draws the frame, fitted to the window.</summary>
    /// <param name="command">Command buffer, inside an active rendering scope.</param>
    /// <param name="width">Window width.</param>
    /// <param name="height">Window height.</param>
    public void Record(CommandBuffer command, int width, int height)
    {
        if (!HasFrame || width <= 0 || height <= 0)
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

        (float x, float y) = PictureFit.Fit(_width, _height, width, height, Cover);

        Span<float> fit =
        [
            x, y, 0f, 0f,
            Display.Transfer, Display.PaperWhite, Display.Headroom, 0f,
        ];

        fixed (float* values = fit)
        {
            // Both stages, because a push constant block is one block: the vertex stage
            // reads the first four floats and the fragment stage the last four, and Vulkan
            // requires the range to name every stage that reads any of it.
            _vk.CmdPushConstants(
                command,
                _layout,
                ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
                0,
                sizeof(float) * 8,
                values);
        }

        _vk.CmdDraw(command, 3, 1, 0, 0);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _picture?.Dispose();
        _picture = null;

        if (_pipeline.Handle != 0)
        {
            _vk.DestroyPipeline(_context.Device, _pipeline, null);
        }

        if (_layout.Handle != 0)
        {
            _vk.DestroyPipelineLayout(_context.Device, _layout, null);
        }

        if (_pool.Handle != 0)
        {
            _vk.DestroyDescriptorPool(_context.Device, _pool, null);
        }

        if (_setLayout.Handle != 0)
        {
            _vk.DestroyDescriptorSetLayout(_context.Device, _setLayout, null);
        }

        if (_fragmentModule.Handle != 0)
        {
            _vk.DestroyShaderModule(_context.Device, _fragmentModule, null);
        }

        if (_vertexModule.Handle != 0)
        {
            _vk.DestroyShaderModule(_context.Device, _vertexModule, null);
        }
    }

    private static ShaderModule Module(VulkanContext context, byte[] spirv)
    {
        fixed (byte* code = spirv)
        {
            var info = new ShaderModuleCreateInfo
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)spirv.Length,
                PCode = (uint*)code,
            };

            if (context.Api.CreateShaderModule(context.Device, in info, null, out ShaderModule module)
                != Result.Success)
            {
                throw new VulkanException("Could not create a movie shader module.");
            }

            return module;
        }
    }

    private void BuildLayout()
    {
        var binding = new DescriptorSetLayoutBinding
        {
            Binding = 0,
            DescriptorType = DescriptorType.CombinedImageSampler,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.FragmentBit,
        };

        var setInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 1,
            PBindings = &binding,
        };

        if (_vk.CreateDescriptorSetLayout(_context.Device, in setInfo, null, out _setLayout)
            != Result.Success)
        {
            throw new VulkanException("Could not create the movie descriptor layout.");
        }

        var size = new DescriptorPoolSize
        {
            Type = DescriptorType.CombinedImageSampler,
            DescriptorCount = 1,
        };

        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            PoolSizeCount = 1,
            PPoolSizes = &size,
            MaxSets = 1,

            // Freed and reallocated whenever the movie changes size.
            Flags = DescriptorPoolCreateFlags.FreeDescriptorSetBit,
        };

        if (_vk.CreateDescriptorPool(_context.Device, in poolInfo, null, out _pool) != Result.Success)
        {
            throw new VulkanException("Could not create the movie descriptor pool.");
        }

        DescriptorSetLayout setLayout = _setLayout;

        var range = new PushConstantRange
        {
            StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
            Offset = 0,
            Size = sizeof(float) * 8,
        };

        var layoutInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1,
            PSetLayouts = &setLayout,
            PushConstantRangeCount = 1,
            PPushConstantRanges = &range,
        };

        if (_vk.CreatePipelineLayout(_context.Device, in layoutInfo, null, out _layout)
            != Result.Success)
        {
            throw new VulkanException("Could not create the movie pipeline layout.");
        }
    }

    private void Bind()
    {
        if (_set.Handle != 0)
        {
            DescriptorSet held = _set;
            _vk.FreeDescriptorSets(_context.Device, _pool, 1, in held);
            _set = default;
        }

        DescriptorSetLayout setLayout = _setLayout;

        var allocation = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _pool,
            DescriptorSetCount = 1,
            PSetLayouts = &setLayout,
        };

        if (_vk.AllocateDescriptorSets(_context.Device, in allocation, out _set) != Result.Success)
        {
            throw new VulkanException("Could not allocate the movie descriptor set.");
        }

        var image = new DescriptorImageInfo
        {
            ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
            ImageView = _picture!.View,
            Sampler = _picture.Sampler,
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

        _vk.UpdateDescriptorSets(_context.Device, 1, in write, 0, null);
    }

    private void BuildPipeline(Format format)
    {
        byte* name = (byte*)SilkMarshal.StringToPtr("main");

        try
        {
            PipelineShaderStageCreateInfo* stages = stackalloc PipelineShaderStageCreateInfo[2];

            stages[0] = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.VertexBit,
                Module = _vertexModule,
                PName = name,
            };

            stages[1] = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.FragmentBit,
                Module = _fragmentModule,
                PName = name,
            };

            // No vertex buffer: the corners come from gl_VertexIndex.
            var vertexInput = new PipelineVertexInputStateCreateInfo
            {
                SType = StructureType.PipelineVertexInputStateCreateInfo,
            };

            var assembly = new PipelineInputAssemblyStateCreateInfo
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

            var raster = new PipelineRasterizationStateCreateInfo
            {
                SType = StructureType.PipelineRasterizationStateCreateInfo,
                PolygonMode = PolygonMode.Fill,
                CullMode = CullModeFlags.None,
                FrontFace = FrontFace.CounterClockwise,
                LineWidth = 1f,
            };

            var multisample = new PipelineMultisampleStateCreateInfo
            {
                SType = StructureType.PipelineMultisampleStateCreateInfo,
                RasterizationSamples = SampleCountFlags.Count1Bit,
            };

            var blend = new PipelineColorBlendAttachmentState
            {
                ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit |
                                 ColorComponentFlags.BBit | ColorComponentFlags.ABit,
                BlendEnable = false,
            };

            var blendState = new PipelineColorBlendStateCreateInfo
            {
                SType = StructureType.PipelineColorBlendStateCreateInfo,
                AttachmentCount = 1,
                PAttachments = &blend,
            };

            DynamicState* dynamics = stackalloc DynamicState[2]
            {
                DynamicState.Viewport,
                DynamicState.Scissor,
            };

            var dynamicState = new PipelineDynamicStateCreateInfo
            {
                SType = StructureType.PipelineDynamicStateCreateInfo,
                DynamicStateCount = 2,
                PDynamicStates = dynamics,
            };

            Format colour = format;

            var rendering = new PipelineRenderingCreateInfo
            {
                SType = StructureType.PipelineRenderingCreateInfo,
                ColorAttachmentCount = 1,
                PColorAttachmentFormats = &colour,
            };

            var info = new GraphicsPipelineCreateInfo
            {
                SType = StructureType.GraphicsPipelineCreateInfo,
                PNext = &rendering,
                StageCount = 2,
                PStages = stages,
                PVertexInputState = &vertexInput,
                PInputAssemblyState = &assembly,
                PViewportState = &viewportState,
                PRasterizationState = &raster,
                PMultisampleState = &multisample,
                PColorBlendState = &blendState,
                PDynamicState = &dynamicState,
                Layout = _layout,
            };

            if (_vk.CreateGraphicsPipelines(_context.Device, default, 1, in info, null, out _pipeline)
                != Result.Success)
            {
                throw new VulkanException("Could not create the movie pipeline.");
            }
        }
        finally
        {
            SilkMarshal.Free((nint)name);
        }
    }
}
