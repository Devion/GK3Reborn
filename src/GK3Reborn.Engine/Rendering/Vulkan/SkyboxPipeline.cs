using System.Numerics;
using System.Runtime.InteropServices;
using GK3Reborn.Formats.Bitmaps;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace GK3Reborn.Rendering.Vulkan;

/// <summary>
/// Draws the sky behind everything else.
/// </summary>
/// <remarks>
/// <para>
/// 177 of the game's 229 scene assets name a sky, and none of them was drawn, so every
/// window and courtyard opened onto whatever the depth buffer was cleared to. The names
/// are already in the <c>.SCN</c>; this is the part that puts them on the screen.
/// </para>
/// <para>
/// Day and night come free. A scene has one asset per time of day — <c>ARM_A</c>,
/// <c>ARM_M</c>, <c>ARM_N</c> — each naming its own sky, and the timeblock already decides
/// which asset is read. Nothing here knows what time it is.
/// </para>
/// <para>
/// Drawn <b>after</b> the room rather than before it, with the depth test on and depth
/// writes off, so it fills only what the room left empty. Drawing it first would shade
/// every pixel of the screen and then paint the room over most of them.
/// </para>
/// </remarks>
public sealed unsafe class SkyboxPipeline : IDisposable
{
    private const string VertexSource = """
        #version 450

        layout(location = 0) in vec3 inPosition;
        layout(location = 0) out vec3 fragDirection;

        layout(push_constant) uniform Push
        {
            mat4 viewProjection;
        } push;

        void main()
        {
            // The corner's own position is the direction to sample, which is what makes
            // this a cube map rather than six quads with hand-written coordinates.
            fragDirection = inPosition;

            vec4 clip = push.viewProjection * vec4(inPosition, 1.0);

            // Pinned to the far plane. Equal depth rather than nearer, so the sky loses to
            // anything the room drew and wins everywhere else.
            gl_Position = clip.xyww;
        }
        """;

    private const string FragmentSource = """
        #version 450

        layout(binding = 0) uniform samplerCube sky;

        layout(location = 0) in vec3 fragDirection;
        layout(location = 0) out vec4 outColor;

        void main()
        {
            outColor = vec4(texture(sky, normalize(fragDirection)).rgb, 1.0);
        }
        """;

    /// <summary>The corners of a cube, two triangles a face, wound to be seen from inside.</summary>
    private static readonly Vector3[] Corners = Cube();

    private readonly Vk _vk;
    private readonly VulkanContext _context;

    private ShaderModule _vertexModule;
    private ShaderModule _fragmentModule;
    private DescriptorSetLayout _setLayout;
    private DescriptorPool _pool;
    private DescriptorSet _set;
    private PipelineLayout _layout;
    private Pipeline _pipeline;
    private VulkanBuffer? _vertices;
    private VulkanTexture? _cube;

    private SkyboxPipeline(VulkanContext context)
    {
        _context = context;
        _vk = context.Api;
    }

    /// <summary>Which way round the sky is turned, in radians about the vertical.</summary>
    public float Azimuth { get; private set; }

    /// <summary>Creates the pipeline for one scene's sky.</summary>
    /// <param name="context">Device context.</param>
    /// <param name="colorFormat">Colour target format.</param>
    /// <param name="depthFormat">Depth target format.</param>
    /// <param name="compiler">Shader compiler.</param>
    /// <param name="faces">The six sides: right, left, up, down, front, back.</param>
    /// <param name="azimuth">How far the sky is turned, in radians.</param>
    /// <returns>The pipeline.</returns>
    public static SkyboxPipeline Create(
        VulkanContext context,
        Format colorFormat,
        Format depthFormat,
        ShaderCompiler compiler,
        IReadOnlyList<DecodedImage> faces,
        float azimuth)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(compiler);
        ArgumentNullException.ThrowIfNull(faces);

        var pipeline = new SkyboxPipeline(context) { Azimuth = azimuth };

        try
        {
            pipeline._vertexModule = pipeline.CreateModule(compiler.Compile(
                VertexSource, ShaderStage.Vertex, "skybox.vert", "main", ShaderLanguage.Glsl));

            pipeline._fragmentModule = pipeline.CreateModule(compiler.Compile(
                FragmentSource, ShaderStage.Fragment, "skybox.frag", "main", ShaderLanguage.Glsl));

            pipeline._cube = VulkanTexture.CreateCube(context, faces);

            pipeline._vertices = VulkanBuffer.CreateDeviceLocal<Vector3>(
                context, Corners, BufferUsageFlags.VertexBufferBit);

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

    /// <summary>Records the sky.</summary>
    /// <param name="command">Command buffer to record into.</param>
    /// <param name="camera">Where the player is looking from.</param>
    /// <param name="width">Viewport width.</param>
    /// <param name="height">Viewport height.</param>
    /// <remarks>
    /// The view's translation is dropped, so the sky turns with the head and never moves
    /// with the feet — which is what makes it read as distance rather than as a box the
    /// player is standing in.
    /// </remarks>
    public void Record(CommandBuffer command, Camera camera, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(camera);

        if (_vertices is null || height <= 0)
        {
            return;
        }

        Matrix4x4 view = camera.View;
        view.M41 = 0;
        view.M42 = 0;
        view.M43 = 0;

        Matrix4x4 turned = Matrix4x4.CreateRotationY(Azimuth) *
                           view *
                           camera.Projection((float)width / height);
        Matrix4x4 transposed = Matrix4x4.Transpose(turned);

        var viewport = new Viewport { Width = width, Height = height, MaxDepth = 1f };
        var scissor = new Rect2D { Extent = new Extent2D((uint)width, (uint)height) };

        _vk.CmdSetViewport(command, 0, 1, in viewport);
        _vk.CmdSetScissor(command, 0, 1, in scissor);
        _vk.CmdBindPipeline(command, PipelineBindPoint.Graphics, _pipeline);

        DescriptorSet set = _set;
        _vk.CmdBindDescriptorSets(
            command, PipelineBindPoint.Graphics, _layout, 0, 1, in set, 0, null);

        _vk.CmdPushConstants(
            command, _layout, ShaderStageFlags.VertexBit, 0,
            (uint)Marshal.SizeOf<Matrix4x4>(), &transposed);

        Buffer handle = _vertices.Handle;
        ulong offset = 0;

        _vk.CmdBindVertexBuffers(command, 0, 1, in handle, in offset);
        _vk.CmdDraw(command, (uint)Corners.Length, 1, 0, 0);
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

        _cube?.Dispose();
        _cube = null;
    }

    /// <summary>A unit cube as thirty-six corners, seen from the inside.</summary>
    private static Vector3[] Cube()
    {
        Vector3[] corners = new Vector3[36];
        int at = 0;

        // Each face as two triangles, listed so that the winding faces inwards; culling is
        // off for this pipeline anyway, which makes the order a readability question rather
        // than a correctness one.
        void Face(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            corners[at++] = a;
            corners[at++] = b;
            corners[at++] = c;
            corners[at++] = c;
            corners[at++] = d;
            corners[at++] = a;
        }

        Face(new(1, 1, -1), new(1, 1, 1), new(1, -1, 1), new(1, -1, -1));
        Face(new(-1, 1, 1), new(-1, 1, -1), new(-1, -1, -1), new(-1, -1, 1));
        Face(new(-1, 1, 1), new(1, 1, 1), new(1, 1, -1), new(-1, 1, -1));
        Face(new(-1, -1, -1), new(1, -1, -1), new(1, -1, 1), new(-1, -1, 1));
        Face(new(1, 1, 1), new(-1, 1, 1), new(-1, -1, 1), new(1, -1, 1));
        Face(new(-1, 1, -1), new(1, 1, -1), new(1, -1, -1), new(-1, -1, -1));

        return corners;
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
                throw new VulkanException("Could not create the skybox shader module.");
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
            throw new VulkanException("Could not create the skybox descriptor layout.");
        }

        var size = new DescriptorPoolSize
        {
            Type = DescriptorType.CombinedImageSampler,
            DescriptorCount = 1,
        };

        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            MaxSets = 1,
            PoolSizeCount = 1,
            PPoolSizes = &size,
        };

        if (_vk.CreateDescriptorPool(_context.Device, in poolInfo, null, out _pool) != Result.Success)
        {
            throw new VulkanException("Could not create the skybox descriptor pool.");
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
            throw new VulkanException("Could not allocate the skybox descriptor set.");
        }

        var image = new DescriptorImageInfo
        {
            ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
            ImageView = _cube!.View,
            Sampler = _cube.Sampler,
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

    private void BuildPipeline(Format colorFormat, Format depthFormat)
    {
        DescriptorSetLayout setLayout = _setLayout;

        var pushConstants = new PushConstantRange
        {
            StageFlags = ShaderStageFlags.VertexBit,
            Offset = 0,
            Size = (uint)Marshal.SizeOf<Matrix4x4>(),
        };

        var layoutInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1,
            PSetLayouts = &setLayout,
            PushConstantRangeCount = 1,
            PPushConstantRanges = &pushConstants,
        };

        if (_vk.CreatePipelineLayout(_context.Device, in layoutInfo, null, out _layout)
            != Result.Success)
        {
            throw new VulkanException("Could not create the skybox pipeline layout.");
        }

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
                Stride = (uint)Marshal.SizeOf<Vector3>(),
                InputRate = VertexInputRate.Vertex,
            };

            var attribute = new VertexInputAttributeDescription
            {
                Location = 0,
                Binding = 0,
                Format = Format.R32G32B32Sfloat,
                Offset = 0,
            };

            var vertexInput = new PipelineVertexInputStateCreateInfo
            {
                SType = StructureType.PipelineVertexInputStateCreateInfo,
                VertexBindingDescriptionCount = 1,
                PVertexBindingDescriptions = &binding,
                VertexAttributeDescriptionCount = 1,
                PVertexAttributeDescriptions = &attribute,
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

                // Tested but not written: the sky must lose to the room and must not stop
                // anything drawn after it.
                DepthTestEnable = true,
                DepthWriteEnable = false,
                DepthCompareOp = CompareOp.LessOrEqual,
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

            Format color = colorFormat;
            var rendering = new PipelineRenderingCreateInfo
            {
                SType = StructureType.PipelineRenderingCreateInfo,
                ColorAttachmentCount = 1,
                PColorAttachmentFormats = &color,
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
                throw new VulkanException($"Could not create the skybox pipeline: {created}.");
            }
        }
        finally
        {
            SilkMarshal.Free(entryPoint);
        }
    }
}
