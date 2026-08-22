using GK3Reborn.Formats.Bitmaps;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;

namespace GK3Reborn.Rendering.Vulkan;

/// <summary>
/// Draws a movie over everything.
/// </summary>
/// <remarks>
/// <para>
/// One texture, one triangle, one draw, and no vertex buffer at all: the corners come from
/// the vertex index and the letterboxing comes from a push constant, because a movie is
/// always the same shape and only ever needs to know how much of the window it should
/// cover.
/// </para>
/// <para>
/// <b>Letterboxed rather than stretched.</b> GK3's movies are 4:3 — 320x240 originally, and
/// larger where they have been re-upscaled — and a modern window is not. Filling it would
/// make everybody in the cutscene short and wide, so the picture is fitted to whichever
/// dimension runs out first and the rest is left black. The scans and the parchment
/// close-ups are not 4:3 at all, which is the other reason to fit rather than assume.
/// </para>
/// <para>
/// Sampled linearly and clamped. A movie is a photograph rather than a bitmap font, so
/// filtering it is what a player expects; clamping keeps the edge pixels from wrapping
/// round into the letterbox.
/// </para>
/// </remarks>
public sealed unsafe class MoviePipeline : IDisposable
{
    private const string VertexSource = """
        #version 450

        layout(push_constant) uniform Fit
        {
            // How much of the window the picture covers, and where it starts. In clip
            // space, so the whole of the letterboxing is two numbers and an offset.
            vec2 scale;
            vec2 offset;
        } fit;

        layout(location = 0) out vec2 fragTexCoord;

        void main()
        {
            // One triangle covering the whole window, from nothing but the vertex index.
            // Two of its corners are outside the window and are clipped, which is cheaper
            // than the two triangles of a quad and has no seam down the middle.
            vec2 uv = vec2((gl_VertexIndex << 1) & 2, gl_VertexIndex & 2);

            gl_Position = vec4((uv * 2.0) - 1.0, 0.0, 1.0);

            // The picture is fitted inside the window rather than the triangle being
            // shrunk to fit it: covering every pixel is what lets the bars be painted
            // black here instead of leaving whatever was on screen showing through them.
            fragTexCoord = ((uv - 0.5) / fit.scale) + 0.5;
        }
        """;

    private const string FragmentSource = """
        #version 450

        layout(binding = 0) uniform sampler2D picture;

        layout(location = 0) in vec2 fragTexCoord;
        layout(location = 0) out vec4 outColor;

        void main()
        {
            // Outside the picture is the letterbox, and the letterbox is black. Leaving it
            // alone would show the room behind the cutscene down both sides.
            vec2 uv = fragTexCoord;

            if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0)
            {
                outColor = vec4(0.0, 0.0, 0.0, 1.0);
                return;
            }

            // Opaque. A movie has nothing behind it worth showing through, and a frame
            // whose alpha channel the decoder filled with something unhelpful should not
            // be able to make the room appear underneath it.
            outColor = vec4(texture(picture, uv).rgb, 1.0);
        }
        """;

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
                    VertexSource, ShaderStage.Vertex, "movie.vert", "main", ShaderLanguage.Glsl));

            pipeline._fragmentModule = Module(
                context, compiler.Compile(
                    FragmentSource, ShaderStage.Fragment, "movie.frag", "main", ShaderLanguage.Glsl));

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
    /// <remarks>
    /// The texture is created on the first frame and refreshed on every one after, so a
    /// movie costs one allocation rather than one a frame. A movie of a different size —
    /// the game's are anything from 41x51 to 1440x1080 — gets a new texture, which is what
    /// changing movies does.
    /// </remarks>
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
    /// <remarks>
    /// Off for a cutscene, which is letterboxed: filling the window would make everybody in
    /// it short and wide. On for a still behind the menu, where there is nothing to distort
    /// and black bars down both sides of the title art look like a fault.
    /// </remarks>
    public bool Cover { get; set; }

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

        // Whichever dimension runs out first decides the scale; the other keeps its share
        // of the window and the remainder is the letterbox.
        float wanted = (float)_width / _height;
        float window = (float)width / height;

        // Fitting takes whichever dimension runs out first; covering takes the other, so
        // the picture runs off the edges instead of leaving bars.
        bool wide = Cover ? wanted < window : wanted > window;

        float x = wide ? 1f : wanted / window;
        float y = wide ? window / wanted : 1f;

        Span<float> fit = [x, y, 0f, 0f];

        fixed (float* values = fit)
        {
            _vk.CmdPushConstants(
                command, _layout, ShaderStageFlags.VertexBit, 0, sizeof(float) * 4, values);
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
            StageFlags = ShaderStageFlags.VertexBit,
            Offset = 0,
            Size = sizeof(float) * 4,
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
