using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;

namespace GK3Reborn.Rendering.Vulkan;

/// <summary>
/// Draws the reconstructed horizon: real terrain where the painted sky was.
/// </summary>
/// <remarks>
/// <para>
/// The backdrop lives in its own metric space — metres around the camera — and is drawn
/// with its own projection, so no room unit ever meets a terrain metre. Like the sky,
/// its translation is dropped: it turns with the head and never moves with the feet.
/// Unlike the sky it is geometry, so ridges occlude ridges and the sun shades it.
/// </para>
/// <para>
/// It cannot share the room's depth range — the room's projection has no idea what four
/// kilometres are — so the vertex stage squeezes the terrain's whole depth into the far
/// tail of the buffer, above 0.999. The room always wins the depth test against it, the
/// terrain still sorts against itself inside the tail, and the sky at exactly 1.0 loses
/// to both. Drawn after the room and before the sky for exactly that reason.
/// </para>
/// <para>
/// Texturing is four tileable ground textures blended by the offline splat weights,
/// with rock forced onto steep faces, each sampled at two scales so the repeat period
/// never shows, and the vista's colour applied hue-only over the top. The full recipe
/// and why each rule exists: <c>ContentWorkspace/enhanced/skyboxes/terrain-plan.md</c>.
/// </para>
/// </remarks>
public sealed unsafe class TerrainPipeline : IDisposable
{
    private const string VertexSource = """
        #version 450

        layout(location = 0) in vec3 inPosition;
        layout(location = 1) in vec3 inNormal;

        layout(push_constant) uniform Push
        {
            mat4 viewProjection;  // azimuth turn, rotation-only view, terrain projection
            vec4 sun;             // xyz: toward the sun; w: 1 when there is one
            vec4 params;          // x: tile metres, y: tint amount, z: fog density
        } push;

        layout(location = 0) out vec3 vWorld;
        layout(location = 1) out vec3 vNormal;

        void main()
        {
            vWorld = inPosition;
            vNormal = inNormal;

            vec4 clip = push.viewProjection * vec4(inPosition, 1.0);

            // The room's projection and this one share nothing, so the terrain takes the
            // far tail of the depth buffer for itself: every fragment lands in
            // [0.999, 1), the room is always nearer, the sky at 1.0 is always farther,
            // and the terrain still sorts against itself inside the tail.
            float zNdc = clamp(clip.z / max(clip.w, 1e-4), 0.0, 1.0);
            clip.z = (0.9990 + 0.000999 * zNdc) * clip.w;
            gl_Position = clip;
        }
        """;

    private const string FragmentSource = """
        #version 450

        layout(binding = 0) uniform sampler2D tileForest;
        layout(binding = 1) uniform sampler2D tileRock;
        layout(binding = 2) uniform sampler2D tileGrass;
        layout(binding = 3) uniform sampler2D tileDirt;
        layout(binding = 4) uniform sampler2D splat;
        layout(binding = 5) uniform sampler2D tint;

        layout(push_constant) uniform Push
        {
            mat4 viewProjection;
            vec4 sun;
            vec4 params;          // x: tile metres, y: tint amount, z: fog density, w: extent
        } push;

        layout(location = 0) in vec3 vWorld;
        layout(location = 1) in vec3 vNormal;

        layout(location = 0) out vec4 outColor;

        // The same texture at two scales, mixed: a single period is visible from one
        // ridge to the next, the pair never lines up.
        vec3 tile2(sampler2D t, vec2 uv)
        {
            return mix(texture(t, uv).rgb, texture(t, uv * 0.23 + vec2(7.31, 3.7)).rgb, 0.45);
        }

        void main()
        {
            vec2 gridUv = (vWorld.xz / (2.0 * push.params.w)) + 0.5;
            vec4 w = texture(splat, gridUv);

            // A cliff is rock whatever grew on the map: the weights were read off a
            // painting seen from face on, and a face-on painting has no slopes in it.
            float slope = 1.0 - clamp(vNormal.y, 0.0, 1.0);
            w.g = max(w.g, smoothstep(0.5, 0.8, slope));
            w /= max(w.r + w.g + w.b + w.a, 1e-4);

            vec2 uv = vWorld.xz / push.params.x;
            vec3 albedo = (w.r * tile2(tileForest, uv))
                        + (w.g * tile2(tileRock, uv))
                        + (w.b * tile2(tileGrass, uv))
                        + (w.a * tile2(tileDirt, uv));

            // Hue only: the vista's colour mood without the old painting's darkness.
            vec3 mood = texture(tint, gridUv).rgb;
            float luminance = dot(mood, vec3(0.299, 0.587, 0.114));
            albedo = mix(albedo, albedo * (mood / max(luminance, 1e-3)), push.params.y);

            // A sunless hour is a dark one: the night sets carry their day sibling's
            // geometry and colours, and the hour's whole difference is made here.
            float toSun = max(dot(normalize(vNormal), push.sun.xyz), 0.0) * push.sun.w;
            vec3 ambient = mix(vec3(0.045, 0.055, 0.085), vec3(0.40, 0.44, 0.52), push.sun.w);
            vec3 lit = albedo * (ambient + (vec3(1.10, 1.02, 0.90) * toSun));

            // Distance haze against a sky-ish grey, which is what makes four kilometres
            // of geometry read as four kilometres rather than a model railway. Nearly
            // black at night, for the same reason the ambient is.
            vec3 haze = mix(vec3(0.05, 0.06, 0.09), vec3(0.75, 0.82, 0.88), push.sun.w);
            float away = length(vWorld);
            float fog = 1.0 - exp(-push.params.z * push.params.z * away * away);
            outColor = vec4(mix(lit, haze, fog), 1.0);
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
    private VulkanBuffer? _vertices;
    private VulkanBuffer? _indices;
    private uint _indexCount;
    private readonly VulkanTexture?[] _textures = new VulkanTexture?[6];
    private float _extent;
    private Vector3? _sunDirection;
    private float _azimuth;

    private TerrainPipeline(VulkanContext context)
    {
        _context = context;
        _vk = context.Api;
    }

    /// <summary>How many metres of ground one tile of texture covers.</summary>
    public float TileMeters { get; set; } = 60f;

    /// <summary>How strongly the vista's colour is laid over the tiles, zero to one.</summary>
    public float TintAmount { get; set; } = 0.6f;

    /// <summary>Creates the pipeline for one scene's backdrop.</summary>
    /// <param name="context">Device context.</param>
    /// <param name="colorFormat">Colour target format.</param>
    /// <param name="depthFormat">Depth target format.</param>
    /// <param name="compiler">Shader compiler.</param>
    /// <param name="backdrop">The terrain to build and draw.</param>
    /// <returns>The pipeline.</returns>
    public static TerrainPipeline Create(
        VulkanContext context,
        Format colorFormat,
        Format depthFormat,
        ShaderCompiler compiler,
        TerrainBackdrop backdrop)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(compiler);
        ArgumentNullException.ThrowIfNull(backdrop);

        var pipeline = new TerrainPipeline(context)
        {
            _extent = backdrop.ExtentMeters,
            _sunDirection = backdrop.SunDirection,
            _azimuth = backdrop.Azimuth,
        };

        try
        {
            pipeline._vertexModule = pipeline.CreateModule(compiler.Compile(
                VertexSource, ShaderStage.Vertex, "terrain.vert", "main", ShaderLanguage.Glsl));

            pipeline._fragmentModule = pipeline.CreateModule(compiler.Compile(
                FragmentSource, ShaderStage.Fragment, "terrain.frag", "main", ShaderLanguage.Glsl));

            pipeline.BuildMesh(backdrop);

            // The tiles repeat and are colour; the splat is data and must not be
            // sRGB-decoded or wrapped; the tint is colour but clamped like the splat.
            pipeline._textures[0] = VulkanTexture.Create(context, backdrop.TileForest);
            pipeline._textures[1] = VulkanTexture.Create(context, backdrop.TileRock);
            pipeline._textures[2] = VulkanTexture.Create(context, backdrop.TileGrass);
            pipeline._textures[3] = VulkanTexture.Create(context, backdrop.TileDirt);
            pipeline._textures[4] = VulkanTexture.Create(
                context, backdrop.Splat, mipmaps: false,
                SamplerAddressMode.ClampToEdge, linear: true);
            pipeline._textures[5] = VulkanTexture.Create(
                context, backdrop.Tint, mipmaps: false, SamplerAddressMode.ClampToEdge);

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

    /// <summary>Records the backdrop.</summary>
    /// <param name="command">Command buffer to record into.</param>
    /// <param name="camera">Where the player is looking from.</param>
    /// <param name="width">Viewport width.</param>
    /// <param name="height">Viewport height.</param>
    public void Record(CommandBuffer command, Camera camera, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(camera);

        if (_vertices is null || _indices is null || width <= 0 || height <= 0)
        {
            return;
        }

        // The camera's rotation without its position, the way the sky drops it, plus the
        // sky's own azimuth so the two stay in register. The projection is the terrain's
        // own: near and far in metres, sized to the grid.
        Vector3 forward = Vector3.Normalize(camera.Target - camera.Position);
        Matrix4x4 view = Matrix4x4.CreateLookAtLeftHanded(Vector3.Zero, forward, camera.Up);
        Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfViewLeftHanded(
            camera.FieldOfView, (float)width / height, 2f, _extent * 3f);
        projection.M22 *= -1;

        var push = new TerrainPush
        {
            ViewProjection = Matrix4x4.CreateRotationY(_azimuth) * view * projection,
            Sun = _sunDirection is { } travelling
                ? new Vector4(Vector3.Normalize(-travelling), 1f)
                : new Vector4(0f, 1f, 0f, 0f),
            Params = new Vector4(TileMeters, TintAmount, 1.6e-4f, _extent),
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
            command, _layout, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, 0,
            (uint)Marshal.SizeOf<TerrainPush>(), &push);

        Silk.NET.Vulkan.Buffer vertexBuffer = _vertices.Handle;
        ulong offset = 0;
        _vk.CmdBindVertexBuffers(command, 0, 1, in vertexBuffer, in offset);
        _vk.CmdBindIndexBuffer(command, _indices.Handle, 0, IndexType.Uint32);
        _vk.CmdDrawIndexed(command, _indexCount, 1, 0, 0, 0);
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
        _indices?.Dispose();
        _indices = null;

        for (int i = 0; i < _textures.Length; i++)
        {
            _textures[i]?.Dispose();
            _textures[i] = null;
        }
    }

    /// <summary>One corner of the grid: where it is and which way its ground faces.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct TerrainVertex(Vector3 Position, Vector3 Normal);

    [StructLayout(LayoutKind.Sequential)]
    private struct TerrainPush
    {
        /// <summary>Azimuth turn, rotation-only view, and the terrain's own projection.</summary>
        public Matrix4x4 ViewProjection;

        /// <summary>Toward the sun in xyz; w is zero for a sunless hour.</summary>
        public Vector4 Sun;

        /// <summary>Tile metres, tint amount, fog density, grid extent.</summary>
        public Vector4 Params;
    }

    private void BuildMesh(TerrainBackdrop backdrop)
    {
        // Every other grid cell: 512 by 512 corners over the 1024 grid is a quarter of
        // the vertices for a silhouette the eye cannot tell apart at these distances.
        const int Stride = 2;

        int grid = backdrop.Grid;
        float extent = backdrop.ExtentMeters;
        float[] heights = backdrop.Heights;

        if (heights.Length != grid * grid)
        {
            throw new VulkanException(
                $"A terrain backdrop's heights are {heights.Length} values for a " +
                $"{grid} by {grid} grid.");
        }

        int side = ((grid - 1) / Stride) + 1;
        float step = (2f * extent) / (grid - 1);

        var vertices = new TerrainVertex[side * side];

        for (int row = 0; row < side; row++)
        {
            int gz = Math.Min(row * Stride, grid - 1);

            for (int column = 0; column < side; column++)
            {
                int gx = Math.Min(column * Stride, grid - 1);

                // Central differences on the full-resolution grid, so a vertex the
                // stride skipped still bends the normals of its neighbours.
                float left = heights[(gz * grid) + Math.Max(gx - 1, 0)];
                float right = heights[(gz * grid) + Math.Min(gx + 1, grid - 1)];
                float near = heights[(Math.Max(gz - 1, 0) * grid) + gx];
                float far = heights[(Math.Min(gz + 1, grid - 1) * grid) + gx];

                var normal = Vector3.Normalize(
                    new Vector3(left - right, 2f * step, near - far));

                vertices[(row * side) + column] = new TerrainVertex(
                    new Vector3(
                        (gx * step) - extent,
                        heights[(gz * grid) + gx],
                        (gz * step) - extent),
                    normal);
            }
        }

        uint[] indices = new uint[(side - 1) * (side - 1) * 6];
        int write = 0;

        for (int row = 0; row < side - 1; row++)
        {
            for (int column = 0; column < side - 1; column++)
            {
                uint a = (uint)((row * side) + column);
                uint b = a + 1;
                uint c = a + (uint)side;
                uint d = c + 1;

                indices[write++] = a;
                indices[write++] = c;
                indices[write++] = b;
                indices[write++] = b;
                indices[write++] = c;
                indices[write++] = d;
            }
        }

        _vertices = VulkanBuffer.CreateDeviceLocal<TerrainVertex>(
            _context, vertices, BufferUsageFlags.VertexBufferBit);
        _indices = VulkanBuffer.CreateDeviceLocal<uint>(
            _context, indices, BufferUsageFlags.IndexBufferBit);
        _indexCount = (uint)indices.Length;
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
                throw new VulkanException("Could not create the terrain shader module.");
            }

            return module;
        }
    }

    private void CreateDescriptors()
    {
        DescriptorSetLayoutBinding* bindings = stackalloc DescriptorSetLayoutBinding[6];

        for (uint i = 0; i < 6; i++)
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
            BindingCount = 6,
            PBindings = bindings,
        };

        if (_vk.CreateDescriptorSetLayout(_context.Device, in layoutInfo, null, out _setLayout)
            != Result.Success)
        {
            throw new VulkanException("Could not create the terrain descriptor layout.");
        }

        var size = new DescriptorPoolSize
        {
            Type = DescriptorType.CombinedImageSampler,
            DescriptorCount = 6,
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
            throw new VulkanException("Could not create the terrain descriptor pool.");
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
            throw new VulkanException("Could not allocate the terrain descriptor set.");
        }

        DescriptorImageInfo* images = stackalloc DescriptorImageInfo[6];
        WriteDescriptorSet* writes = stackalloc WriteDescriptorSet[6];

        for (int i = 0; i < 6; i++)
        {
            VulkanTexture texture = _textures[i]!;
            images[i] = new DescriptorImageInfo
            {
                ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
                ImageView = texture.View,
                Sampler = texture.Sampler,
            };

            writes[i] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = _set,
                DstBinding = (uint)i,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.CombinedImageSampler,
                PImageInfo = images + i,
            };
        }

        _vk.UpdateDescriptorSets(_context.Device, 6, writes, 0, null);
    }

    private void BuildPipeline(Format colorFormat, Format depthFormat)
    {
        DescriptorSetLayout setLayout = _setLayout;

        var pushConstants = new PushConstantRange
        {
            StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
            Offset = 0,
            Size = (uint)Marshal.SizeOf<TerrainPush>(),
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
            throw new VulkanException("Could not create the terrain pipeline layout.");
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
                Stride = (uint)Marshal.SizeOf<TerrainVertex>(),
                InputRate = VertexInputRate.Vertex,
            };

            VertexInputAttributeDescription* attributes =
                stackalloc VertexInputAttributeDescription[2];
            attributes[0] = new VertexInputAttributeDescription
            {
                Location = 0,
                Binding = 0,
                Format = Format.R32G32B32Sfloat,
                Offset = 0,
            };
            attributes[1] = new VertexInputAttributeDescription
            {
                Location = 1,
                Binding = 0,
                Format = Format.R32G32B32Sfloat,
                Offset = 12,
            };

            var vertexInput = new PipelineVertexInputStateCreateInfo
            {
                SType = StructureType.PipelineVertexInputStateCreateInfo,
                VertexBindingDescriptionCount = 1,
                PVertexBindingDescriptions = &binding,
                VertexAttributeDescriptionCount = 2,
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

            // No culling: whether the grid's winding survives the world's handedness is
            // exactly the kind of thing that would otherwise be diagnosed as a black
            // screen, and a heightfield seen from above has almost no back faces anyway.
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

            // Tested and written: the room has already claimed its pixels, the terrain
            // sorts against itself in the far tail, and the sky at 1.0 loses to it.
            var depth = new PipelineDepthStencilStateCreateInfo
            {
                SType = StructureType.PipelineDepthStencilStateCreateInfo,
                DepthTestEnable = true,
                DepthWriteEnable = true,
                DepthCompareOp = CompareOp.LessOrEqual,
            };

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
                throw new VulkanException($"Could not create the terrain pipeline: {created}.");
            }
        }
        finally
        {
            SilkMarshal.Free(entryPoint);
        }
    }
}
