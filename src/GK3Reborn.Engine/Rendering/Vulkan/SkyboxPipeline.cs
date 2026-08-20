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

        // One triangle covering the screen, from the vertex index alone. No vertex buffer,
        // no attributes and nothing passed to the fragment stage: the direction to sample
        // is worked out from where the fragment is, which is the one input that was ever
        // demonstrably reaching it.
        void main()
        {
            vec2 corner = vec2((gl_VertexIndex << 1) & 2, gl_VertexIndex & 2);

            // Depth at the far plane, so the sky loses to anything the room drew. Written
            // as clip coordinates outright, which is also why nothing here can be clipped
            // by a near plane.
            gl_Position = vec4((corner * 2.0) - 1.0, 1.0, 1.0);
        }
        """;

    private const string FragmentSource = """
        #version 450

        layout(binding = 0) uniform samplerCube sky;

        layout(push_constant) uniform Push
        {
            vec4 forward;   // xyz: where the camera looks, already turned by the azimuth
            vec4 right;     // xyz: its right, scaled by nothing; w: tan of half the horizontal fov
            vec4 up;        // xyz: its up;                      w: tan of half the vertical fov
            vec4 viewport;  // xy: size in pixels
        } push;

        layout(location = 0) out vec4 outColor;

        void main()
        {
            // The ray through this pixel, built from the camera's own basis rather than by
            // inverting a projection. It is the same arithmetic the projection does, run
            // forwards: an inverse is a thing that can be ill-conditioned or wrong in a way
            // that is invisible until every pixel comes back with the same answer.
            vec2 ndc = ((gl_FragCoord.xy / push.viewport.xy) * 2.0) - 1.0;

            // gl_FragCoord counts down the screen and up counts up it.
            vec3 direction = push.forward.xyz
                           + (push.right.xyz * (ndc.x * push.right.w))
                           - (push.up.xyz * (ndc.y * push.up.w));

            outColor = vec4(texture(sky, normalize(direction)).rgb, 1.0);
        }
        """;

    /// <summary>The corners of a cube, two triangles a face, wound to be seen from inside.</summary>
    private readonly Vk _vk;
    private readonly VulkanContext _context;

    private ShaderModule _vertexModule;
    private ShaderModule _fragmentModule;
    private DescriptorSetLayout _setLayout;
    private DescriptorPool _pool;
    private DescriptorSet _set;
    private PipelineLayout _layout;
    private Pipeline _pipeline;
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

        if (_cube is null || width <= 0 || height <= 0)
        {
            return;
        }

        // The camera's basis, the way CreateLookAtLeftHanded builds it.
        Vector3 forward = Vector3.Normalize(camera.Target - camera.Position);
        Vector3 right = Vector3.Normalize(Vector3.Cross(camera.Up, forward));
        Vector3 up = Vector3.Cross(forward, right);

        // Turning the sky by its azimuth is turning the ray the other way, which is one
        // rotation of three vectors rather than a matrix through the whole pass.
        Matrix4x4 azimuth = Matrix4x4.CreateRotationY(-Azimuth);

        float tanY = MathF.Tan(camera.FieldOfView / 2f);
        float tanX = tanY * width / height;

        var push = new SkyPush
        {
            Forward = new Vector4(Vector3.TransformNormal(forward, azimuth), 0),
            Right = new Vector4(Vector3.TransformNormal(right, azimuth), tanX),
            Up = new Vector4(Vector3.TransformNormal(up, azimuth), tanY),
            Viewport = new Vector4(width, height, 0, 0),
        };

        var viewport = new Viewport { Width = width, Height = height, MaxDepth = 1f };
        var scissor = new Rect2D { Extent = new Extent2D((uint)width, (uint)height) };

        _vk.CmdSetViewport(command, 0, 1, in viewport);
        _vk.CmdSetScissor(command, 0, 1, in scissor);
        _vk.CmdBindPipeline(command, PipelineBindPoint.Graphics, _pipeline);

        DescriptorSet set = _set;
        _vk.CmdBindDescriptorSets(
            command, PipelineBindPoint.Graphics, _layout, 0, 1, in set, 0, null);

        _vk.CmdPushConstants(
            command, _layout, ShaderStageFlags.FragmentBit, 0,
            (uint)Marshal.SizeOf<SkyPush>(), &push);

        _vk.CmdDraw(command, 3, 1, 0, 0);
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


        _cube?.Dispose();
        _cube = null;
    }

    /// <summary>A unit cube as thirty-six corners, seen from the inside.</summary>
    /// <summary>What the fragment stage needs to turn a pixel into a direction.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct SkyPush
    {
        /// <summary>Where the camera looks, turned by the sky's azimuth.</summary>
        public Vector4 Forward;

        /// <summary>Its right, with the tangent of half the horizontal field of view in w.</summary>
        public Vector4 Right;

        /// <summary>Its up, with the tangent of half the vertical field of view in w.</summary>
        public Vector4 Up;

        /// <summary>Width and height in pixels.</summary>
        public Vector4 Viewport;
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
            StageFlags = ShaderStageFlags.FragmentBit,
            Offset = 0,
            Size = (uint)Marshal.SizeOf<SkyPush>(),
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

            // Nothing comes in. The one triangle is built from the vertex index and the
            // ray through each pixel from the camera's own basis, so there is no buffer,
            // no attribute and nothing passed between the stages.
            var vertexInput = new PipelineVertexInputStateCreateInfo
            {
                SType = StructureType.PipelineVertexInputStateCreateInfo,
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

            // Three attachments, because the frame has three and a pipeline has to describe
            // every one of them. This pass writes the picture and nothing else, so the other
            // two are masked off rather than left to write whatever the shader happens to
            // leave in them.
            PipelineColorBlendAttachmentState* blendAttachments =
                stackalloc PipelineColorBlendAttachmentState[(int)GBuffer.Targets];

            blendAttachments[GBuffer.Colour] = new PipelineColorBlendAttachmentState
            {
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
                throw new VulkanException($"Could not create the skybox pipeline: {created}.");
            }
        }
        finally
        {
            SilkMarshal.Free(entryPoint);
        }
    }
}
