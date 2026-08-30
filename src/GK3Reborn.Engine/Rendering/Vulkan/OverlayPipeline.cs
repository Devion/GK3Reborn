using System.Numerics;
using System.Runtime.InteropServices;
using GK3Reborn.Formats.Bitmaps;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

using GK3Reborn.Rendering.Shaders;

namespace GK3Reborn.Rendering.Vulkan;

/// <summary>
/// Draws the interface on top of the room.
/// </summary>
/// <remarks>
/// <para>
/// One pipeline, one texture, one vertex buffer, one draw. The interface is a few hundred
/// rectangles at most and they all come from the same atlas, so batching them by anything
/// would cost more than it saved.
/// </para>
/// <para>
/// No depth test and no depth write, because the overlay is on top by definition and
/// competing with the room for the depth buffer would only produce ways for it to be
/// hidden. Alpha blending is straight rather than premultiplied, since the atlas holds
/// letters cut out of a sheet rather than composited artwork.
/// </para>
/// <para>
/// The display list is in pixels and the vertices are in clip space, converted while they
/// are being written. Doing it there rather than in the shader costs one multiply per
/// corner and keeps the pipeline down to a vertex buffer and one texture.
/// </para>
/// </remarks>
public sealed unsafe class OverlayPipeline : IDisposable
{
    /// <remarks>
    /// GLSL rather than the HLSL the raster shaders use. Two things this needs are
    /// ambiguous through shaderc's HLSL front end — a combined image sampler, and a push
    /// constant — and both fail by producing a shader that compiles and draws nothing,
    /// which is the worst way for a bring-up to fail. In GLSL they are one declaration each
    /// and mean exactly one thing.
    /// </remarks>
    private readonly Vk _vk;
    private readonly VulkanContext _context;
    private readonly int _capacity;

    private ShaderModule _vertexModule;
    private ShaderModule _fragmentModule;
    private DescriptorSetLayout _setLayout;
    private DescriptorPool _pool;
    private DescriptorSet _set;
    private PipelineLayout _layout;
    private Pipeline _pipeline;
    private VulkanTexture? _atlas;
    private VulkanBuffer? _vertices;
    private int _count;

    /// <summary>The screens' own pictures, and a descriptor set for each.</summary>
    /// <remarks>
    /// Indexed from one: zero is the sheet of letters, which every other quad uses. Loaded
    /// once when a screen that needs art first opens, and kept — the driving map is a
    /// 640-by-480 painting and reloading it every time somebody opens the map would be a
    /// stall the player can feel.
    /// </remarks>
    private readonly List<(VulkanTexture Texture, DescriptorSet Set)> _pictures = [];

    /// <summary>Which picture each run of six vertices belongs to.</summary>
    private readonly List<OverlayRun> _runs = [];

    private OverlayPipeline(VulkanContext context, int capacity)
    {
        _context = context;
        _vk = context.Api;
        _capacity = capacity;
    }

    /// <summary>How many of the screens' own pictures may be held at once.</summary>
    /// <remarks>
    /// The driving map's background and its sixteen markers, the save slots' thumbnails,
    /// and one for each of the 133 things the game lets a player carry — which is what
    /// this used to be too small for at 64. Each is a texture and a descriptor set, both
    /// cheap, and none is made until something asks for it; what is not cheap is reloading
    /// a 640-by-480 painting every time somebody opens the map, which is why they are kept.
    /// </remarks>
    public const int MostPictures = 256;

    /// <summary>What the swapchain wants written into it.</summary>
    /// <remarks>
    /// Set by the renderer whenever the output chain changes, which in practice is when
    /// somebody turns HDR on. Standard by default, which is the sRGB target the hardware
    /// encodes and where this shader writes exactly what it always wrote.
    /// </remarks>
    public DisplayEncode Display { get; set; } = DisplayEncode.Standard;

    /// <summary>How many rectangles have been prepared for this frame.</summary>
    public int Rectangles => _count / 6;

    /// <summary>How many pictures are loaded.</summary>
    public int Pictures => _pictures.Count;

    /// <summary>
    /// Gives the pipeline one of the screens' own pictures.
    /// </summary>
    /// <param name="image">The decoded picture.</param>
    /// <returns>Its number, for <c>Overlay.Picture</c>, or zero when there is no room.</returns>
    public int AddPicture(DecodedImage image)
    {
        if (_pictures.Count >= MostPictures)
        {
            return 0;
        }

        // Clamped rather than repeated, and no mips: a map drawn at close to its own size
        // wants neither, and repeating one would wrap the edge of the picture around the
        // opposite side of the panel.
        VulkanTexture texture = VulkanTexture.Create(
            _context, image, mipmaps: false, SamplerAddressMode.ClampToEdge);

        DescriptorSetLayout setLayout = _setLayout;
        var allocate = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _pool,
            DescriptorSetCount = 1,
            PSetLayouts = &setLayout,
        };

        if (_vk.AllocateDescriptorSets(_context.Device, in allocate, out DescriptorSet set)
            != Result.Success)
        {
            texture.Dispose();

            return 0;
        }

        var info = new DescriptorImageInfo
        {
            ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
            ImageView = texture.View,
            Sampler = texture.Sampler,
        };

        var write = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = set,
            DstBinding = 0,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.CombinedImageSampler,
            PImageInfo = &info,
        };

        _vk.UpdateDescriptorSets(_context.Device, 1, in write, 0, null);
        _pictures.Add((texture, set));

        return _pictures.Count;
    }

    /// <summary>
    /// Puts a different sheet of letters behind the same pipeline.
    /// </summary>
    /// <param name="atlas">The sheet to draw from now.</param>
    /// <remarks>
    /// <para>
    /// The interface has more than one sheet — the room's captions and the menu are cut at
    /// different sizes — and it swaps between them whenever the player opens the menu and
    /// closes it again. Only the atlas texture and binding zero of its descriptor set
    /// change; the shaders, the pipeline, the pool and <em>every picture already loaded</em>
    /// stay exactly where they are.
    /// </para>
    /// <para>
    /// <b>Which is the whole point.</b> This used to be done by disposing the pipeline and
    /// building another, and a new pipeline has an empty descriptor pool: the driving map
    /// and its sixteen markers, loaded once at startup, were thrown away the first time the
    /// front end drew a frame. Nothing reloaded them, so the map the moped is ridden around
    /// fell back to a list of place names for the rest of the session. Recompiling two
    /// shaders every time somebody opens the menu was the smaller of the two costs.
    /// </para>
    /// <para>
    /// The caller waits for the device to be idle first: the sheet being replaced may still
    /// be read by a frame that has not finished.
    /// </para>
    /// </remarks>
    public void SetAtlas(OverlayAtlas atlas)
    {
        ArgumentNullException.ThrowIfNull(atlas);

        VulkanTexture replacement = VulkanTexture.Create(
            _context, atlas.Image, mipmaps: false, SamplerAddressMode.ClampToEdge);

        var image = new DescriptorImageInfo
        {
            ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
            ImageView = replacement.View,
            Sampler = replacement.Sampler,
        };

        var rebind = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = _set,
            DstBinding = 0,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.CombinedImageSampler,
            PImageInfo = &image,
        };

        _vk.UpdateDescriptorSets(_context.Device, 1, in rebind, 0, null);

        _atlas?.Dispose();
        _atlas = replacement;
    }

    /// <summary>Creates the pipeline.</summary>
    /// <param name="context">Device context.</param>
    /// <param name="colorFormat">Colour target format.</param>
    /// <param name="depthFormat">Depth target format.</param>
    /// <param name="compiler">Shader compiler.</param>
    /// <param name="atlas">The sheet the interface is drawn from.</param>
    /// <param name="capacity">How many rectangles it can hold in one frame.</param>
    /// <returns>The pipeline.</returns>
    public static OverlayPipeline Create(
        VulkanContext context,
        Format colorFormat,
        Format depthFormat,
        ShaderCompiler compiler,
        OverlayAtlas atlas,
        int capacity = 4096)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(compiler);
        ArgumentNullException.ThrowIfNull(atlas);

        var pipeline = new OverlayPipeline(context, capacity);

        try
        {
            pipeline._vertexModule = pipeline.CreateModule(compiler.Compile(
                OverlayShaders.Vertex, ShaderStage.Vertex, "overlay.vert", "main", ShaderLanguage.Glsl));

            pipeline._fragmentModule = pipeline.CreateModule(compiler.Compile(
                OverlayShaders.Fragment, ShaderStage.Fragment, "overlay.frag", "main", ShaderLanguage.Glsl));

            // No mipmaps and clamped addressing: this is a packed sheet, so a coarser level
            // would average one letter into the next, and repeating would wrap the edge of
            // a glyph round to the other side of the atlas.
            pipeline._atlas = VulkanTexture.Create(
                context, atlas.Image, mipmaps: false, SamplerAddressMode.ClampToEdge);

            pipeline._vertices = VulkanBuffer.CreateHostVisible(
                context,
                (ulong)(capacity * 6 * Marshal.SizeOf<OverlayVertex>()),
                BufferUsageFlags.VertexBufferBit);

            pipeline.CreateDescriptors();
            pipeline.BuildPipeline(colorFormat, depthFormat);

            return pipeline;
        }
        catch
        {
            pipeline.Dispose();
            throw;
        }
    }

    /// <summary>Turns a display list into vertices, ready to draw.</summary>
    /// <param name="overlay">What to draw.</param>
    /// <remarks>
    /// Two triangles a rectangle, written straight into host-visible memory. Indexing them
    /// would save a third of the space and cost a second buffer; at a few hundred
    /// rectangles a frame that trade is not worth making.
    /// </remarks>
    public void Prepare(Overlay overlay)
    {
        ArgumentNullException.ThrowIfNull(overlay);

        _count = 0;

        if (_vertices is null || overlay.Quads.Count == 0)
        {
            return;
        }

        // The same triangles either backend wants, built once in OverlayMesh.
        OverlayVertex[] vertices = OverlayMesh.Build(overlay, _capacity, _pictures.Count, _runs);

        _vertices.Write<OverlayVertex>(vertices);
        _count = vertices.Length;
    }

    /// <summary>Records the draw.</summary>
    /// <param name="command">Command buffer to record into.</param>
    /// <param name="width">Viewport width in pixels.</param>
    /// <param name="height">Viewport height in pixels.</param>
    public void Record(CommandBuffer command, int width, int height)
    {
        if (_count == 0 || _vertices is null)
        {
            return;
        }

        var viewport = new Viewport { Width = width, Height = height, MaxDepth = 1f };
        var scissor = new Rect2D
        {
            Extent = new Extent2D((uint)width, (uint)height),
        };

        _vk.CmdSetViewport(command, 0, 1, in viewport);
        _vk.CmdSetScissor(command, 0, 1, in scissor);
        _vk.CmdBindPipeline(command, PipelineBindPoint.Graphics, _pipeline);

        Buffer handle = _vertices.Handle;
        ulong offset = 0;
        _vk.CmdBindVertexBuffers(command, 0, 1, in handle, in offset);

        foreach ((int picture, int first, int count) in _runs)
        {
            DescriptorSet set = picture > 0 && picture <= _pictures.Count
                ? _pictures[picture - 1].Set
                : _set;

            _vk.CmdBindDescriptorSets(
                command, PipelineBindPoint.Graphics, _layout, 0, 1, in set, 0, null);

            // The kind of run, and what the display wants. Both in one push because they
            // are one push constant range and Vulkan will not let a range be written in
            // pieces of different stages.
            OverlayConstants pushed = new(
                picture > 0 ? 1 : 0,
                0,
                0,
                0,
                Display.Transfer,
                Display.PaperWhite,
                Display.Headroom,
                0f);

            _vk.CmdPushConstants(
                command,
                _layout,
                ShaderStageFlags.FragmentBit,
                0,
                (uint)sizeof(OverlayConstants),
                &pushed);

            _vk.CmdDraw(command, (uint)count, 1, (uint)first, 0);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_pipeline.Handle != 0)
        {
            _vk.DestroyPipeline(_context.Device, _pipeline, null);
            _pipeline = default;
        }

        if (_layout.Handle != 0)
        {
            _vk.DestroyPipelineLayout(_context.Device, _layout, null);
            _layout = default;
        }

        if (_pool.Handle != 0)
        {
            _vk.DestroyDescriptorPool(_context.Device, _pool, null);
            _pool = default;
        }

        if (_setLayout.Handle != 0)
        {
            _vk.DestroyDescriptorSetLayout(_context.Device, _setLayout, null);
            _setLayout = default;
        }

        if (_vertexModule.Handle != 0)
        {
            _vk.DestroyShaderModule(_context.Device, _vertexModule, null);
            _vertexModule = default;
        }

        if (_fragmentModule.Handle != 0)
        {
            _vk.DestroyShaderModule(_context.Device, _fragmentModule, null);
            _fragmentModule = default;
        }

        _vertices?.Dispose();
        _vertices = null;

        _atlas?.Dispose();
        _atlas = null;
    }

    private ShaderModule CreateModule(byte[] spirv)
    {
        fixed (byte* code = spirv)
        {
            var createInfo = new ShaderModuleCreateInfo
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)spirv.Length,
                PCode = (uint*)code,
            };

            if (_vk.CreateShaderModule(_context.Device, in createInfo, null, out ShaderModule module)
                != Result.Success)
            {
                throw new VulkanException("Could not create the overlay shader module.");
            }

            return module;
        }
    }

    private void CreateDescriptors()
    {
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

        if (_vk.CreateDescriptorSetLayout(_context.Device, in layoutInfo, null, out _setLayout)
            != Result.Success)
        {
            throw new VulkanException("Could not create the overlay descriptor layout.");
        }

        // One for the sheet of letters and the rest for the screens' own pictures. Sized
        // for the driving map and its sixteen markers, with room to spare.
        var size = new DescriptorPoolSize
        {
            Type = DescriptorType.CombinedImageSampler,
            DescriptorCount = MostPictures + 1,
        };

        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            MaxSets = MostPictures + 1,
            PoolSizeCount = 1,
            PPoolSizes = &size,
        };

        if (_vk.CreateDescriptorPool(_context.Device, in poolInfo, null, out _pool) != Result.Success)
        {
            throw new VulkanException("Could not create the overlay descriptor pool.");
        }

        DescriptorSetLayout setLayout = _setLayout;
        var allocate = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _pool,
            DescriptorSetCount = 1,
            PSetLayouts = &setLayout,
        };

        if (_vk.AllocateDescriptorSets(_context.Device, in allocate, out _set) != Result.Success)
        {
            throw new VulkanException("Could not allocate the overlay descriptor set.");
        }

        var image = new DescriptorImageInfo
        {
            ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
            ImageView = _atlas!.View,
            Sampler = _atlas.Sampler,
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

    /// <summary>Rebuilds the pipeline for a different colour target.</summary>
    /// <param name="colorFormat">The new format.</param>
    /// <param name="depthFormat">Depth target format, unchanged in practice.</param>
    /// <remarks>
    /// For the swapchain changing format underneath, which happens exactly once in a
    /// session: when the player turns HDR on and an 8-bit sRGB surface becomes a ten-bit
    /// one. A pipeline carries the format it writes and the old one is then invalid.
    /// <para>
    /// Only the pipeline and its layout. Everything expensive the interface owns — the
    /// sheet of letters, and every picture the screens have handed over — is kept, because
    /// throwing those away would leave the menu drawing blank squares where the save
    /// thumbnails were and nothing would know to put them back.
    /// </para>
    /// </remarks>
    public void Retarget(Format colorFormat, Format depthFormat)
    {
        if (_pipeline.Handle != 0)
        {
            _vk.DestroyPipeline(_context.Device, _pipeline, null);
            _pipeline = default;
        }

        if (_layout.Handle != 0)
        {
            _vk.DestroyPipelineLayout(_context.Device, _layout, null);
            _layout = default;
        }

        BuildPipeline(colorFormat, depthFormat);
    }

    private void BuildPipeline(Format colorFormat, Format depthFormat)
    {
        DescriptorSetLayout setLayout = _setLayout;

        // One integer: whether this run is a picture or a glyph.
        var pushed = new PushConstantRange
        {
            StageFlags = ShaderStageFlags.FragmentBit,
            Offset = 0,
            Size = (uint)sizeof(OverlayConstants),
        };

        var layoutInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1,
            PSetLayouts = &setLayout,
            PushConstantRangeCount = 1,
            PPushConstantRanges = &pushed,
        };

        if (_vk.CreatePipelineLayout(_context.Device, in layoutInfo, null, out _layout)
            != Result.Success)
        {
            throw new VulkanException("Could not create the overlay pipeline layout.");
        }

        // GLSL has one entry point per stage and it is always called main. The name here
        // must be the name in the SPIR-V or the driver rejects the pipeline, with no
        // explanation beyond "unknown".
        nint entryPoint = SilkMarshal.StringToPtr("main");

        try
        {
            PipelineShaderStageCreateInfo* stages = stackalloc PipelineShaderStageCreateInfo[2];
            stages[0] = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.VertexBit,
                Module = _vertexModule,
                PName = (byte*)entryPoint,
            };
            stages[1] = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.FragmentBit,
                Module = _fragmentModule,
                PName = (byte*)entryPoint,
            };

            var binding = new VertexInputBindingDescription
            {
                Binding = 0,
                Stride = (uint)Marshal.SizeOf<OverlayVertex>(),
                InputRate = VertexInputRate.Vertex,
            };

            VertexInputAttributeDescription* attributes = stackalloc VertexInputAttributeDescription[3];
            attributes[0] = new VertexInputAttributeDescription
            {
                Location = 0, Binding = 0, Format = Format.R32G32Sfloat, Offset = 0,
            };
            attributes[1] = new VertexInputAttributeDescription
            {
                Location = 1, Binding = 0, Format = Format.R32G32Sfloat, Offset = 8,
            };
            attributes[2] = new VertexInputAttributeDescription
            {
                Location = 2, Binding = 0, Format = Format.R32G32B32A32Sfloat, Offset = 16,
            };

            var vertexInput = new PipelineVertexInputStateCreateInfo
            {
                SType = StructureType.PipelineVertexInputStateCreateInfo,
                VertexBindingDescriptionCount = 1,
                PVertexBindingDescriptions = &binding,
                VertexAttributeDescriptionCount = 3,
                PVertexAttributeDescriptions = attributes,
            };

            var inputAssembly = new PipelineInputAssemblyStateCreateInfo
            {
                SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                Topology = PrimitiveTopology.TriangleList,
            };

            DynamicState* dynamicStates = stackalloc DynamicState[2]
            {
                DynamicState.Viewport,
                DynamicState.Scissor,
            };

            var dynamic = new PipelineDynamicStateCreateInfo
            {
                SType = StructureType.PipelineDynamicStateCreateInfo,
                DynamicStateCount = 2,
                PDynamicStates = dynamicStates,
            };

            var viewport = new PipelineViewportStateCreateInfo
            {
                SType = StructureType.PipelineViewportStateCreateInfo,
                ViewportCount = 1,
                ScissorCount = 1,
            };

            var rasterization = new PipelineRasterizationStateCreateInfo
            {
                SType = StructureType.PipelineRasterizationStateCreateInfo,
                PolygonMode = PolygonMode.Fill,
                LineWidth = 1f,
                CullMode = CullModeFlags.None,
                FrontFace = FrontFace.CounterClockwise,
            };

            var multisample = new PipelineMultisampleStateCreateInfo
            {
                SType = StructureType.PipelineMultisampleStateCreateInfo,
                RasterizationSamples = SampleCountFlags.Count1Bit,
            };

            var depth = new PipelineDepthStencilStateCreateInfo
            {
                SType = StructureType.PipelineDepthStencilStateCreateInfo,
                DepthTestEnable = false,
                DepthWriteEnable = false,
                DepthCompareOp = CompareOp.Always,
            };

            // Three attachments, because the frame has three and a pipeline has to describe
            // every one of them. This pass writes the picture and nothing else, so the other
            // two are masked off rather than left to write whatever the shader happens to
            // leave in them.
            PipelineColorBlendAttachmentState* blendAttachments =
                stackalloc PipelineColorBlendAttachmentState[(int)GBuffer.Targets];

            blendAttachments[GBuffer.Colour] = new PipelineColorBlendAttachmentState
            {
                BlendEnable = true,
                SrcColorBlendFactor = BlendFactor.SrcAlpha,
                DstColorBlendFactor = BlendFactor.OneMinusSrcAlpha,
                ColorBlendOp = BlendOp.Add,
                SrcAlphaBlendFactor = BlendFactor.One,
                DstAlphaBlendFactor = BlendFactor.OneMinusSrcAlpha,
                AlphaBlendOp = BlendOp.Add,
                ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit |
                                 ColorComponentFlags.BBit | ColorComponentFlags.ABit,
            };

            for (int i = 1; i < (int)GBuffer.Targets; i++)
            {
                blendAttachments[i] = default;
            }

            var blend = new PipelineColorBlendStateCreateInfo
            {
                SType = StructureType.PipelineColorBlendStateCreateInfo,
                AttachmentCount = GBuffer.Targets,
                PAttachments = blendAttachments,
            };

            Format* colors = stackalloc Format[(int)GBuffer.Targets]
            {
                colorFormat,
                GBuffer.NormalFormat,
                GBuffer.MotionFormat,
                GBuffer.LightFormat,
            };
            var rendering = new PipelineRenderingCreateInfo
            {
                SType = StructureType.PipelineRenderingCreateInfo,
                ColorAttachmentCount = GBuffer.Targets,
                PColorAttachmentFormats = colors,
                DepthAttachmentFormat = depthFormat,
            };

            var createInfo = new GraphicsPipelineCreateInfo
            {
                SType = StructureType.GraphicsPipelineCreateInfo,
                PNext = &rendering,
                StageCount = 2,
                PStages = stages,
                PVertexInputState = &vertexInput,
                PInputAssemblyState = &inputAssembly,
                PViewportState = &viewport,
                PRasterizationState = &rasterization,
                PMultisampleState = &multisample,
                PDepthStencilState = &depth,
                PColorBlendState = &blend,
                PDynamicState = &dynamic,
                Layout = _layout,
            };

            Result created = _vk.CreateGraphicsPipelines(
                _context.Device, default, 1, in createInfo, null, out _pipeline);

            if (created != Result.Success)
            {
                throw new VulkanException($"Could not create the overlay pipeline: {created}.");
            }
        }
        finally
        {
            SilkMarshal.Free(entryPoint);
        }
    }
}
