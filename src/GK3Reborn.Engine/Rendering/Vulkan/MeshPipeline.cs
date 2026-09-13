using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;

using GK3Reborn.Rendering.Shaders;

using GK3Reborn.Rendering.Geometry;

namespace GK3Reborn.Rendering.Vulkan;

/// <summary>
/// A textured, lit mesh pipeline, optionally with ray tracing compiled in.
/// </summary>
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
    private Pipeline _culled;
    private Pipeline _culledMirror;
    private Pipeline _decal;

    private MeshPipeline(Vk vk, Device device, bool rayTracing)
    {
        _vk = vk;
        _device = device;
        RayTracing = rayTracing;
    }

    /// <summary>Whether this variant can trace rays.</summary>
    public bool RayTracing { get; }

    /// <summary>The pipeline handle: both faces of every triangle.</summary>
    public Pipeline Handle => _pipeline;

    /// <summary>The same pipeline with back faces discarded.</summary>
    public Pipeline CulledHandle => _culled;

    /// <summary>That one again for the mirror pass, whose view reverses every winding.</summary>
    public Pipeline CulledMirrorHandle => _culledMirror;

    /// <summary>
    /// The pipeline a stain on the room is drawn with: its picture multiplied into the
    /// first attachment, nothing written to the other three and nothing written to depth.
    /// See <c>SceneDraw.Decal</c>.
    /// </summary>
    public Pipeline DecalHandle => _decal;

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

    /// <summary>Issues the draws a scene worked out.</summary>
    /// <param name="vk">Vulkan API.</param>
    /// <param name="command">Command buffer, inside an active rendering scope.</param>
    /// <param name="pipeline">The pipeline currently bound.</param>
    /// <param name="draws">What to draw, from <c>SceneGeometry.Draws</c>.</param>
    /// <param name="reflection">
    /// Whether this is the mirror's pass. Its view is reflected, so a culled draw within it
    /// wants the opposite front face; nothing else about the pass changes.
    /// </param>
    public static void Record(
        Vk vk,
        CommandBuffer command,
        MeshPipeline pipeline,
        IEnumerable<SceneDraw> draws,
        bool reflection = false)
    {
        ArgumentNullException.ThrowIfNull(vk);
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(draws);

        // Reused for every draw: two vertex streams, both from the start of their buffer.
        Silk.NET.Vulkan.Buffer* streams = stackalloc Silk.NET.Vulkan.Buffer[2];
        ulong* offsets = stackalloc ulong[2] { 0, 0 };
        int? bound = null;

        foreach (SceneDraw draw in draws)
        {
            // One bind per run of draws that agree rather than one per draw: the room's own
            // batches are built before any model is placed, so a frame normally switches
            // once. Nothing here sorts them, because a sort would be a decision and this
            // method makes none.
            int wanted = draw.Decal ? 2 : draw.DoubleSided ? 0 : 1;

            if (bound != wanted)
            {
                bound = wanted;
                vk.CmdBindPipeline(
                    command,
                    PipelineBindPoint.Graphics,
                    wanted switch
                    {
                        2 => pipeline.DecalHandle,
                        0 => pipeline.Handle,
                        _ => reflection ? pipeline.CulledMirrorHandle : pipeline.CulledHandle,
                    });
            }

            DescriptorSet material = VulkanGeometry.Set(draw.Material);
            vk.CmdBindDescriptorSets(
                command, PipelineBindPoint.Graphics, pipeline.Layout, 1, 1, in material, 0, null);

            pipeline.PushConstants(command, draw.Constants);

            streams[0] = VulkanGeometry.Handle(draw.Vertices);
            streams[1] = VulkanGeometry.Handle(draw.Previous);

            vk.CmdBindVertexBuffers(command, 0, 2, streams, offsets);
            vk.CmdBindIndexBuffer(
                command,
                VulkanGeometry.Handle(draw.Indices),
                0,
                draw.ShortIndices ? IndexType.Uint16 : IndexType.Uint32);

            vk.CmdDrawIndexed(command, draw.IndexCount, 1, 0, 0, 0);

            // Everything else about the draw is already bound, so a shell is one push and
            // one draw. That is what makes twelve of them affordable on a model.
            foreach (DrawConstants shell in draw.Shells)
            {
                pipeline.PushConstants(command, shell);
                vk.CmdDrawIndexed(command, draw.IndexCount, 1, 0, 0, 0);
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_decal.Handle != 0)
        {
            _vk.DestroyPipeline(_device, _decal, null);
        }

        if (_culledMirror.Handle != 0)
        {
            _vk.DestroyPipeline(_device, _culledMirror, null);
        }

        if (_culled.Handle != 0)
        {
            _vk.DestroyPipeline(_device, _culled, null);
        }

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
        DescriptorSetLayoutBinding* frameBindings = stackalloc DescriptorSetLayoutBinding[6];
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

        // The room as a mirror sees it, drawn a moment ago from the reflected camera. Set 0
        // because there is one of these for the whole frame; a device with no mirror in the
        // room still binds it, as every other always-bound texture in this renderer is.
        frameBindings[4] = new DescriptorSetLayoutBinding
        {
            Binding = 5,
            DescriptorType = DescriptorType.CombinedImageSampler,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.FragmentBit,
        };

        // Last in the array, so that the count can leave it off on a device that cannot
        // trace. A binding is not added by writing to it — the count is what the driver
        // reads, which is why its binding number is 4 while it sits at index 5: the array's
        // order is what the count truncates, and the numbers are free to be in any order.
        frameBindings[5] = new DescriptorSetLayoutBinding
        {
            Binding = 4,
            DescriptorType = DescriptorType.AccelerationStructureKhr,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.FragmentBit,
        };

        var frameInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = RayTracing ? 6u : 5u,
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

                // Both overwritten below, once per variant.
                //
                // <b>Clockwise, the same as the Direct3D path asks for.</b> An older comment
                // here said the two APIs needed opposite spellings because they disagree
                // about which way up a framebuffer is. They do not: both put the origin at
                // the top left, and this renderer hands them the same left-handed
                // projection, so the same triangle comes out wound the same way in each. The
                // claim was never tested, because until there was a culled variant nothing
                // read this field at all - and it was wrong. RayTracingTests, which draw a
                // floor from straight above through the Vulkan backend, are what says so.
                CullMode = CullModeFlags.None,
                FrontFace = FrontFace.Clockwise,
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

            // Four pipelines over one layout and one pair of modules, differing in nothing
            // but which faces survive the rasteriser and — for the fourth — how the result
            // reaches the attachments, and built in one call.
            //
            // A draw says which it wants. Placed models take the first, because a grown
            // tree's leaf is a single sheet with no back; the room takes the second, because
            // a GK3 room is a shell of inward-facing surfaces and drawing their backs paints
            // over what is meant to be seen through them - R25's dumbwaiter, where the
            // shaft's room-side face is a solid sheet of lath with no hole cut for the door.
            // The third is that one again for the mirror pass, whose reflected view turns
            // every triangle the other way. The fourth is the room's stains: see below.
            PipelineRasterizationStateCreateInfo* rasterizers =
                stackalloc PipelineRasterizationStateCreateInfo[4];

            for (int i = 0; i < 4; i++)
            {
                rasterizers[i] = rasterization;

                // The fourth is both faces again: a shadow decal is a card laid on the
                // ground, and the original turns culling off for its whole translucent pass.
                rasterizers[i].CullMode =
                    i is 0 or 3 ? CullModeFlags.None : CullModeFlags.BackBit;

                rasterizers[i].FrontFace =
                    i == 2 ? FrontFace.CounterClockwise : FrontFace.Clockwise;
            }

            // And the fourth's own blending: `dst * src`, the factor pair the original uses
            // for its whole translucent pass, into every attachment and no depth written.
            // The shader writes white to the three a stain must not disturb, which is what
            // lets one blend state serve all four — a per-attachment write mask would need
            // `independentBlend`, which Vulkan does not promise. See SceneDraw.Decal.
            PipelineColorBlendAttachmentState* stainAttachments =
                stackalloc PipelineColorBlendAttachmentState[(int)GBuffer.Targets];

            for (int i = 0; i < (int)GBuffer.Targets; i++)
            {
                stainAttachments[i] = new PipelineColorBlendAttachmentState
                {
                    BlendEnable = true,
                    SrcColorBlendFactor = BlendFactor.DstColor,
                    DstColorBlendFactor = BlendFactor.Zero,
                    ColorBlendOp = BlendOp.Add,
                    SrcAlphaBlendFactor = BlendFactor.Zero,
                    DstAlphaBlendFactor = BlendFactor.One,
                    AlphaBlendOp = BlendOp.Add,
                    ColorWriteMask = All,
                };
            }

            var stainBlend = new PipelineColorBlendStateCreateInfo
            {
                SType = StructureType.PipelineColorBlendStateCreateInfo,
                AttachmentCount = GBuffer.Targets,
                PAttachments = stainAttachments,
            };

            var stainDepth = depth;
            stainDepth.DepthWriteEnable = false;

            GraphicsPipelineCreateInfo* infos = stackalloc GraphicsPipelineCreateInfo[4];
            Pipeline* built = stackalloc Pipeline[4];

            for (int i = 0; i < 4; i++)
            {
                infos[i] = createInfo;
                infos[i].PRasterizationState = &rasterizers[i];

                if (i == 3)
                {
                    infos[i].PColorBlendState = &stainBlend;
                    infos[i].PDepthStencilState = &stainDepth;
                }
            }

            if (_vk.CreateGraphicsPipelines(_device, default, 4, infos, null, built)
                != Result.Success)
            {
                throw new VulkanException("Could not create the mesh pipeline.");
            }

            _pipeline = built[0];
            _culled = built[1];
            _culledMirror = built[2];
            _decal = built[3];
        }
        finally
        {
            SilkMarshal.Free(entryPoint);
        }
    }
}
