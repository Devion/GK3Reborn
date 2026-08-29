using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;

namespace GK3Reborn.Rendering.Vulkan;

/// <summary>A vertex as the mesh pipeline expects it.</summary>
/// <param name="Position">Model-space position.</param>
/// <param name="Normal">Model-space normal.</param>
/// <param name="TexCoord">Diffuse texture coordinate.</param>
/// <param name="LightmapCoord">Coordinate into the lightmap atlas.</param>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct MeshVertex(
    Vector3 Position, Vector3 Normal, Vector2 TexCoord, Vector2 LightmapCoord);

/// <summary>Constants shared by every draw of a frame.</summary>
/// <param name="ViewProjection">World to clip space.</param>
/// <param name="PreviousViewProjection">
/// The same, as it was last frame. Half of what a motion vector is: where a point that is
/// here now would have been on the screen a frame ago.
/// </param>
/// <param name="LightDirection">Direction the fallback key light travels.</param>
/// <param name="CameraPosition">Where the eye is, in world space.</param>
/// <param name="Rays">
/// Shadowed light count, occlusion rays, rays per shadow, and how much the bake counts.
/// </param>
/// <param name="Tuning">
/// Occlusion radius, and three components nothing reads. The second used to be a frame
/// counter that seeded the sampling noise; it made the grain change every frame, which
/// with no temporal filter to average it is a pattern crawling across the picture.
/// </param>
/// <param name="GridOrigin">
/// The corner the light grid starts at, and how wide one of its cells is. See
/// <see cref="SceneLightGrid"/>.
/// </param>
/// <param name="GridCounts">
/// How many cells the grid has along each axis, and how many lights the rig holds in all.
/// </param>
/// <param name="Ambient">
/// The ambient floor in rgb, and in w how much the baked lightmaps shape it. It is tier data rather than a constant
/// because what it has to stand in for changes: where the baked lightmaps still light the
/// room it only keeps an unreached corner off black, and where they are gone it is the
/// whole of what the walls and floor bounce back.
/// </param>
/// <param name="Exposure">
/// This frame's jitter in pixels in xy, how much brighter a surface that carries its own
/// light is drawn in z, and nothing in w.
/// <para>
/// The jitter is here because the fragment stage has to take it back out of the motion
/// vectors. <c>gl_FragCoord</c> comes from the jittered projection and the previous clip
/// position comes from an unjittered one, so the difference between them is the movement
/// plus this frame's offset; adding the offset back leaves the movement.
/// </para>
/// <para>
/// The brightness is the HDR path's, and it is one in SDR. A bulb and a diffuse white wall
/// both come out of the shading at about one, which is the only answer an 8-bit target can
/// hold; on a display with somewhere above white to go, they should not be the same
/// brightness at all. See <see cref="Rendering.OutputPlan"/>.
/// </para>
/// </param>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct FrameUniforms(
    Matrix4x4 ViewProjection,
    Matrix4x4 PreviousViewProjection,
    Vector4 LightDirection,
    Vector4 CameraPosition,
    Vector4 Rays,
    Vector4 Tuning,
    Vector4 GridOrigin,
    Vector4 GridCounts,
    Vector4 Ambient,
    Vector4 Exposure);

/// <summary>Constants that change per draw, delivered as push constants.</summary>
/// <param name="Model">Model to world space.</param>
/// <param name="PreviousModel">
/// The same, as it was last frame. The other half of a motion vector: without it a walking
/// character reports the movement of the camera and none of his own, which is exactly the
/// case a temporal filter has to get right.
/// </param>
/// <param name="Shading">
/// How to shade: x selects the lightmap over the rig, y scales the lightmap, z marks a
/// surface that carries its own brightness, w is how deep its height map goes.
/// </param>
/// <param name="Material">
/// The surface's finish where no map says otherwise: x roughness, y metalness,
/// z specular reflectance at normal incidence, w how much of the normal map to believe.
/// </param>
/// <param name="Wind">
/// How this batch moves: x how far a leaf at the top of it travels, as a fraction of the
/// model's own height, y how fast, z the clock as it stood a frame ago. All zero for
/// everything that is not foliage, which is almost everything in the game.
/// </param>
/// <remarks>
/// A hundred and seventy-six bytes, which is past the hundred and twenty-eight Vulkan
/// guarantees. Every desktop driver this renderer has run on offers 256, and the two
/// matrices alone were already past the floor — but it is the number to look at first if
/// a device ever refuses the pipeline layout, and the fix is a uniform buffer rather than
/// a smaller struct.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct DrawConstants(
    Matrix4x4 Model,
    Matrix4x4 PreviousModel,
    Vector4 Shading,
    Vector4 Material,
    Vector4 Wind);

/// <summary>
/// A textured, lit mesh pipeline, optionally with ray tracing compiled in.
/// </summary>
/// <remarks>
/// <para>
/// Resources are split by how often they change. Set 0 holds the camera, the light rig
/// and — in the ray-traced variant — the acceleration structure, and is bound once a
/// frame. Set 1 holds a batch's two textures and never changes at all. What is left, the
/// model transform and the shading mode, travels as push constants, which need no buffer,
/// no descriptor and no synchronisation between frames in flight.
/// </para>
/// <para>
/// The two variants exist rather than one shader that branches, because Vulkan requires
/// every statically used binding to point at something valid whether its branch runs or
/// not. A device with no ray-tracing extensions cannot supply an acceleration structure,
/// so its shader must not mention one.
/// </para>
/// </remarks>
public sealed unsafe class MeshPipeline : IDisposable
{
    private readonly Vk _vk;
    private readonly Device _device;

    private ShaderModule _vertexModule;
    private ShaderModule _fragmentModule;
    private DescriptorSetLayout _frameLayout;
    private DescriptorSetLayout _materialLayout;
    private PipelineLayout _layout;
    private Pipeline _pipeline;

    private MeshPipeline(Vk vk, Device device, bool rayTracing)
    {
        _vk = vk;
        _device = device;
        RayTracing = rayTracing;
    }

    /// <summary>Whether this variant can trace rays.</summary>
    public bool RayTracing { get; }

    /// <summary>The pipeline handle.</summary>
    public Pipeline Handle => _pipeline;

    /// <summary>The pipeline layout, for binding descriptor sets and push constants.</summary>
    public PipelineLayout Layout => _layout;

    /// <summary>Layout of set 0: the camera, the rig, and the scene rays see.</summary>
    public DescriptorSetLayout FrameLayout => _frameLayout;

    /// <summary>Layout of set 1: a batch's textures.</summary>
    public DescriptorSetLayout MaterialLayout => _materialLayout;

    /// <summary>Builds the pipeline.</summary>
    /// <param name="context">Device context.</param>
    /// <param name="colorFormat">Colour target format.</param>
    /// <param name="depthFormat">Depth target format.</param>
    /// <param name="compiler">Shader compiler.</param>
    /// <param name="rayTracing">Whether to compile the ray-tracing paths in.</param>
    /// <returns>The pipeline.</returns>
    public static MeshPipeline Create(
        VulkanContext context,
        Format colorFormat,
        Format depthFormat,
        ShaderCompiler compiler,
        bool rayTracing = false)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(compiler);

        var pipeline = new MeshPipeline(context.Api, context.Device, rayTracing);

        try
        {
            pipeline._vertexModule = pipeline.CreateModule(compiler.Compile(
                MeshShaders.Compose(fragment: false, rayTracing),
                ShaderStage.Vertex,
                "mesh.vert",
                "main",
                ShaderLanguage.Glsl));

            pipeline._fragmentModule = pipeline.CreateModule(compiler.Compile(
                MeshShaders.Compose(fragment: true, rayTracing),
                ShaderStage.Fragment,
                "mesh.frag",
                "main",
                ShaderLanguage.Glsl));

            pipeline.CreateDescriptorLayouts();
            pipeline.BuildPipeline(colorFormat, depthFormat);
            return pipeline;
        }
        catch
        {
            pipeline.Dispose();
            throw;
        }
    }

    /// <summary>Sets the per-draw constants.</summary>
    /// <param name="command">Command buffer to record into.</param>
    /// <param name="constants">The constants.</param>
    public void PushConstants(CommandBuffer command, DrawConstants constants)
    {
        DrawConstants value = constants;

        _vk.CmdPushConstants(
            command,
            _layout,
            ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
            0,
            (uint)Marshal.SizeOf<DrawConstants>(),
            &value);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_pipeline.Handle != 0)
        {
            _vk.DestroyPipeline(_device, _pipeline, null);
        }

        if (_layout.Handle != 0)
        {
            _vk.DestroyPipelineLayout(_device, _layout, null);
        }

        if (_materialLayout.Handle != 0)
        {
            _vk.DestroyDescriptorSetLayout(_device, _materialLayout, null);
        }

        if (_frameLayout.Handle != 0)
        {
            _vk.DestroyDescriptorSetLayout(_device, _frameLayout, null);
        }

        if (_vertexModule.Handle != 0)
        {
            _vk.DestroyShaderModule(_device, _vertexModule, null);
        }

        if (_fragmentModule.Handle != 0)
        {
            _vk.DestroyShaderModule(_device, _fragmentModule, null);
        }
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

            if (_vk.CreateShaderModule(_device, in createInfo, null, out ShaderModule module) != Result.Success)
            {
                throw new VulkanException("Could not create a shader module.");
            }

            return module;
        }
    }

    private void CreateDescriptorLayouts()
    {
        DescriptorSetLayoutBinding* frameBindings = stackalloc DescriptorSetLayoutBinding[5];
        frameBindings[0] = new DescriptorSetLayoutBinding
        {
            Binding = 0,
            DescriptorType = DescriptorType.UniformBuffer,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
        };
        // The rig, and the grid that says which of it reaches where. Storage buffers
        // rather than uniform ones: a uniform block has to be sized at compile time and
        // the standard only guarantees 16 KB of it, which is what put a limit of sixty-four
        // lights on a scene. A storage buffer is unsized on both sides and the loop is
        // bounded by the cell rather than by the array. See SceneLightGrid.
        frameBindings[1] = new DescriptorSetLayoutBinding
        {
            Binding = 1,
            DescriptorType = DescriptorType.StorageBuffer,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.FragmentBit,
        };
        frameBindings[2] = new DescriptorSetLayoutBinding
        {
            Binding = 2,
            DescriptorType = DescriptorType.StorageBuffer,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.FragmentBit,
        };
        frameBindings[3] = new DescriptorSetLayoutBinding
        {
            Binding = 3,
            DescriptorType = DescriptorType.StorageBuffer,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.FragmentBit,
        };

        // Last, so that the count can leave it off on a device that cannot trace. A
        // binding is not added by writing to it — the count is what the driver reads.
        frameBindings[4] = new DescriptorSetLayoutBinding
        {
            Binding = 4,
            DescriptorType = DescriptorType.AccelerationStructureKhr,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.FragmentBit,
        };

        var frameInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = RayTracing ? 5u : 4u,
            PBindings = frameBindings,
        };

        if (_vk.CreateDescriptorSetLayout(_device, in frameInfo, null, out _frameLayout) != Result.Success)
        {
            throw new VulkanException("Could not create the frame descriptor set layout.");
        }

        DescriptorSetLayoutBinding* materialBindings = stackalloc DescriptorSetLayoutBinding[5];
        materialBindings[0] = new DescriptorSetLayoutBinding
        {
            Binding = 0,
            DescriptorType = DescriptorType.CombinedImageSampler,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.FragmentBit,
        };
        materialBindings[1] = new DescriptorSetLayoutBinding
        {
            Binding = 1,
            DescriptorType = DescriptorType.CombinedImageSampler,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.FragmentBit,
        };

        // A surface's normal map. Every batch binds one — a flat map where there is none —
        // so a partial set of enhanced materials stays a perfectly good set.
        materialBindings[2] = new DescriptorSetLayoutBinding
        {
            Binding = 2,
            DescriptorType = DescriptorType.CombinedImageSampler,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.FragmentBit,
        };

        // The surface's packed occlusion, roughness and metalness. Every batch binds one —
        // a neutral map where there is none — so switching the specular lobe on before the
        // maps exist changes nothing about what is drawn.
        materialBindings[3] = new DescriptorSetLayoutBinding
        {
            Binding = 3,
            DescriptorType = DescriptorType.CombinedImageSampler,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.FragmentBit,
        };

        // The surface's height field, for parallax. Bound the same way and for the same
        // reason: a level map where there is none, and a height scale of zero to go with it.
        materialBindings[4] = new DescriptorSetLayoutBinding
        {
            Binding = 4,
            DescriptorType = DescriptorType.CombinedImageSampler,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.FragmentBit,
        };

        var materialInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,

            // Raised with the array above and not after it. A binding written into the
            // array without this count moving is not a binding: the driver does not
            // complain, it corrupts binding 0, and every surface draws the fallback
            // checkerboard. That cost a debugging round the first time.
            BindingCount = 5,
            PBindings = materialBindings,
        };

        if (_vk.CreateDescriptorSetLayout(_device, in materialInfo, null, out _materialLayout) != Result.Success)
        {
            throw new VulkanException("Could not create the material descriptor set layout.");
        }
    }

    private void BuildPipeline(Format colorFormat, Format depthFormat)
    {
        DescriptorSetLayout* layouts = stackalloc DescriptorSetLayout[2] { _frameLayout, _materialLayout };

        var pushConstants = new PushConstantRange
        {
            StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
            Offset = 0,
            Size = (uint)Marshal.SizeOf<DrawConstants>(),
        };

        var layoutInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 2,
            PSetLayouts = layouts,
            PushConstantRangeCount = 1,
            PPushConstantRanges = &pushConstants,
        };

        if (_vk.CreatePipelineLayout(_device, in layoutInfo, null, out _layout) != Result.Success)
        {
            throw new VulkanException("Could not create a pipeline layout.");
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

            // Two bindings over the same kind of vertex: this frame's pose and the last
            // one. Both are whole MeshVertex streams, and only the position is read from
            // the second.
            VertexInputBindingDescription* bindings = stackalloc VertexInputBindingDescription[2];

            for (int i = 0; i < 2; i++)
            {
                bindings[i] = new VertexInputBindingDescription
                {
                    Binding = (uint)i,
                    Stride = (uint)Marshal.SizeOf<MeshVertex>(),
                    InputRate = VertexInputRate.Vertex,
                };
            }

            VertexInputAttributeDescription* attributes = stackalloc VertexInputAttributeDescription[5];
            attributes[0] = new VertexInputAttributeDescription
            {
                Location = 0, Binding = 0, Format = Format.R32G32B32Sfloat, Offset = 0,
            };
            attributes[1] = new VertexInputAttributeDescription
            {
                Location = 1, Binding = 0, Format = Format.R32G32B32Sfloat, Offset = 12,
            };
            attributes[2] = new VertexInputAttributeDescription
            {
                Location = 2, Binding = 0, Format = Format.R32G32Sfloat, Offset = 24,
            };
            attributes[3] = new VertexInputAttributeDescription
            {
                Location = 3, Binding = 0, Format = Format.R32G32Sfloat, Offset = 32,
            };
            attributes[4] = new VertexInputAttributeDescription
            {
                Location = 4, Binding = 1, Format = Format.R32G32B32Sfloat, Offset = 0,
            };

            var vertexInput = new PipelineVertexInputStateCreateInfo
            {
                SType = StructureType.PipelineVertexInputStateCreateInfo,
                VertexBindingDescriptionCount = 2,
                PVertexBindingDescriptions = bindings,
                VertexAttributeDescriptionCount = 5,
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

                // Culling stays off: GK3's winding is not consistently counter-clockwise,
                // which is also why the exported glTF marks its materials double-sided.
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
                DepthTestEnable = true,
                DepthWriteEnable = true,
                DepthCompareOp = CompareOp.Less,
            };

            // Three targets: the picture, the surface normal and how far each pixel moved
            // since the last frame. A pipeline drawing into a set of attachments has to
            // describe all of them whether it writes to them or not, which is why the sky
            // and the interface describe three as well and mask two of them off.
            const ColorComponentFlags All =
                ColorComponentFlags.RBit | ColorComponentFlags.GBit |
                ColorComponentFlags.BBit | ColorComponentFlags.ABit;

            PipelineColorBlendAttachmentState* blendAttachments =
                stackalloc PipelineColorBlendAttachmentState[(int)GBuffer.Targets];

            for (int i = 0; i < (int)GBuffer.Targets; i++)
            {
                blendAttachments[i] = new PipelineColorBlendAttachmentState { ColorWriteMask = All };
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

            if (_vk.CreateGraphicsPipelines(_device, default, 1, in createInfo, null, out _pipeline)
                != Result.Success)
            {
                throw new VulkanException("Could not create the mesh pipeline.");
            }
        }
        finally
        {
            SilkMarshal.Free(entryPoint);
        }
    }
}
