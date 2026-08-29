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

        // Declared identically in both stages, members this one never reads included. A
        // push constant block is one block across the pipeline, and two stages describing
        // it differently is a validation error at best and a driver disagreement at worst.
        layout(push_constant) uniform Fit
        {
            // How much of the window the picture covers, and where it starts. In clip
            // space, so the whole of the letterboxing is two numbers and an offset.
            vec2 scale;
            vec2 offset;

            // The fragment stage's, and unread here.
            vec4 display;
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

    /// <summary>The fragment stage, with the shared display encode spliced in.</summary>
    /// <remarks>See <see cref="DisplayEncoding"/>: one copy of ST.2084 rather than four.</remarks>
    private static readonly string FragmentSource =
        FragmentPrelude + "\n" + DisplayEncoding.Glsl + "\n" + FragmentBody;

    private const string FragmentPrelude = """
        #version 450

        layout(binding = 0) uniform sampler2D picture;

        // The same block the vertex stage declares, member for member.
        layout(push_constant) uniform Fit
        {
            // The vertex stage's, and unread here.
            vec2 scale;
            vec2 offset;

            // Which encoding the swapchain wants, where paper white sits, and how far
            // above it the display goes. All nought on an ordinary sRGB surface.
            vec4 display;
        } fit;

        layout(location = 0) in vec2 fragTexCoord;
        layout(location = 0) out vec4 outColor;
        """;

    private const string FragmentBody = """
        void main()
        {
            // Outside the picture is the letterbox, and the letterbox is black. Leaving it
            // alone would show the room behind the cutscene down both sides.
            vec2 uv = fragTexCoord;

            if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0)
            {
                // Black is black in every encoding, so there is nothing to convert.
                outColor = vec4(0.0, 0.0, 0.0, 1.0);
                return;
            }

            // Opaque. A movie has nothing behind it worth showing through, and a frame
            // whose alpha channel the decoder filled with something unhelpful should not
            // be able to make the room appear underneath it.
            //
            // The film is standard-range material and stays at paper white on an HDR
            // display: a 1999 cutscene has no highlights above white to recover, and
            // stretching it into the headroom would only make the whites glare.
            outColor = vec4(
                EncodeForDisplay(texture(picture, uv).rgb, fit.display.xyz), 1.0);
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

    /// <summary>What the swapchain wants written into it.</summary>
    /// <remarks>
    /// Set by the renderer. Standard by default, which is the sRGB target the hardware
    /// encodes and where the film is written exactly as it always was.
    /// </remarks>
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

    /// <summary>Hands over a still that is already in blocks.</summary>
    /// <param name="picture">The compressed image.</param>
    /// <remarks>
    /// For the title screen, which in a shipped game comes out of a pack and is therefore
    /// BC7 rather than pixels. Nothing decompresses it on the way past: the blocks go to
    /// the device as they are, the same as every texture in a room.
    /// </remarks>
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

        (float x, float y) = Fit(_width, _height, width, height, Cover);

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

    /// <summary>
    /// How much of the window the picture covers, in each direction.
    /// </summary>
    /// <param name="pictureWidth">The picture's width in pixels.</param>
    /// <param name="pictureHeight">Its height.</param>
    /// <param name="windowWidth">The window's width in pixels.</param>
    /// <param name="windowHeight">Its height.</param>
    /// <param name="cover">Whether to fill the window rather than fit inside it.</param>
    /// <returns>
    /// The share of the window the picture spans, horizontally and vertically. One means
    /// exactly the window; less leaves a bar; more runs off the edge and is cropped.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>The picture's shape is never changed.</b> Whatever comes back,
    /// <c>windowWidth * x</c> over <c>windowHeight * y</c> is the picture's own aspect —
    /// which is the one property of this worth testing, and the one nobody notices is
    /// broken until everybody in a cutscene is short and wide.
    /// </para>
    /// <para>
    /// Fitting puts the whole picture in the window and leaves bars. Covering fills the
    /// window and crops — <b>but only so far</b>. Past <see cref="MostCropped"/> it stops
    /// and lets the bars come back, because a 4:3 title screen on an ultrawide display
    /// would otherwise be cropped until the game's own name ran off the bottom of it.
    /// </para>
    /// </remarks>
    public static (float X, float Y) Fit(
        int pictureWidth, int pictureHeight, int windowWidth, int windowHeight, bool cover)
    {
        if (pictureWidth <= 0 || pictureHeight <= 0 || windowWidth <= 0 || windowHeight <= 0)
        {
            return (1f, 1f);
        }

        float picture = (float)pictureWidth / pictureHeight;
        float window = (float)windowWidth / windowHeight;

        // How much covering would have to crop, and how much of that is allowed.
        float needed = picture > window ? picture / window : window / picture;
        float allowed = cover ? Math.Clamp(needed, 1f, MostCropped) : 1f;

        // The axis that grows is whichever the picture has to spare; the other follows from
        // it, and the two together always describe the picture's own shape.
        return picture > window
            ? (allowed, allowed * window / picture)
            : (allowed * picture / window, allowed);
    }

    /// <summary>
    /// How far a covering picture may be cropped before bars are preferred.
    /// </summary>
    /// <remarks>
    /// A third. It is enough to fill any ordinary display with the game's 4:3 title art —
    /// 16:9 needs exactly a third — and not enough for an ultrawide to cut the lettering
    /// off the bottom of it.
    /// </remarks>
    public const float MostCropped = 1.34f;

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
