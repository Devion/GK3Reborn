// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System;
using Silk.NET.Vulkan;

namespace GK3Reborn.Rendering.Vulkan;

/// <summary>Puts the room back together from its parts.</summary>
/// <remarks>
/// The mesh pass writes the two halves of the lighting separately and shadows neither of
/// them: what a lamp would give a pixel with nothing in the way, and what the bake and the
/// ambient give it with nothing above it. Both occlusion terms are traced and filtered
/// afterwards, by which time the geometry is long gone, so the multiply happens here — one
/// triangle over the whole frame, four samples a pixel.
/// </remarks>
internal sealed unsafe class CompositePipeline : IDisposable
{
    private const string Vertex = """
        #version 460

        layout(location = 0) out vec2 outUv;

        void main()
        {
            // One triangle covering the frame, from nothing but the vertex index.
            outUv = vec2((gl_VertexIndex << 1) & 2, gl_VertexIndex & 2);
            gl_Position = vec4((outUv * 2.0) - 1.0, 0.0, 1.0);
        }
        """;

    private const string Fragment = """
        #version 460

        layout(location = 0) in vec2 inUv;
        layout(location = 0) out vec4 outColor;

        layout(set = 0, binding = 0) uniform sampler2D indirectTarget;
        layout(set = 0, binding = 1) uniform sampler2D directTarget;
        layout(set = 0, binding = 2) uniform sampler2D shadowTarget;
        layout(set = 0, binding = 3) uniform sampler2D occlusionTarget;
        layout(set = 0, binding = 4) uniform sampler2D reflectionTarget;

        // How much of the rig's light survives the things standing in the room, as opposed
        // to the room itself. One where nobody is in the way.
        layout(set = 0, binding = 5) uniform sampler2D dynamicTarget;

        // How much of the traced occlusion to believe. Not all of it: the lightmaps these
        // rooms ship with were baked with occlusion already in them, so a hemisphere of
        // rays is measuring something the bake has largely accounted for, and applying it
        // whole counts it twice. Whole, it also drives a surface to black outright —
        // enough of the hemisphere above a shoulder is that person's own head that the
        // shoulder disappears, which is not a shadow anybody would draw.
        //
        // What is worth having is the near contact the bake is too coarse to hold: the
        // seam where an arm meets a body, the line under a table.
        const float kOcclusionStrength = 0.55;

        // Reflections arrive already weighted: by how much of the ray the marcher could
        // follow, by how much the surface reflects at the angle it is seen from, and by
        // how rough it is. There is nothing left to scale here.
        const float kReflectionStrength = 1.0;

        void main()
        {
            ivec2 pixel = ivec2(gl_FragCoord.xy);

            vec4 indirect = texelFetch(indirectTarget, pixel, 0);
            vec3 direct = texelFetch(directTarget, pixel, 0).rgb;
            float shadow = clamp(texelFetch(shadowTarget, pixel, 0).r, 0.0, 1.0);
            float open = clamp(texelFetch(occlusionTarget, pixel, 0).r, 0.0, 1.0);

            // Alpha carries what the indirect term is: zero for a surface that carries
            // its own brightness, a half for the ambient floor, one for a bake.
            //
            // A bulb is not dimmed by the shade around it, so occlusion applies to the
            // other two and not to it.
            float lightmapped = step(0.75, indirect.a);
            float shaded = step(0.25, indirect.a);

            float occlusion = mix(1.0, open, kOcclusionStrength * shaded);

            // How much of the rig's light a moving thing takes away, kept apart from the
            // room's own shadowing above because the two are subtracted at different
            // points. See below.
            float unblocked = clamp(texelFetch(dynamicTarget, pixel, 0).r, 0.0, 1.0);

            // The rig's light, as much of it as the *room* lets through. This is the term
            // the bake is comparable to, because the bake was made with the room and
            // nothing else in it.
            vec3 accounted = direct * shadow;

            // And what actually arrives, once whoever is standing there is counted too.
            vec3 arrived = accounted * unblocked;

            // And what the bake holds that the rig has not just accounted for.
            //
            // A bake contains two things: the light these same lamps threw in 1999, which
            // is being computed afresh and would otherwise be counted twice, and light
            // from sources the rig has not got — daylight through a window, sky, bounce
            // off a wall. Scaling the whole bake down, which is what this used to do,
            // throws the second away with the first: R25's window fell from a mean of 71
            // to 50 and the room lost the daylight the artists painted into it.
            //
            // Subtracting what the rig delivered keeps the two apart, and needs no weight
            // to be chosen. Where the rig explains the bake this falls to nothing and the
            // picture is ray traced outright; where it explains none of it — a window with
            // no light behind it, a corner lit only by bounce — the bake survives whole.
            // And it is the light that got past the *room* that is subtracted, not the
            // light the rig would give with nothing in the way: a rig this size has lamps
            // in the rooms next door, which contribute on paper and are stopped by a wall
            // in fact.
            //
            // What is emphatically not subtracted is the light a character is standing in
            // front of. Subtracting the fully occluded term is what made characters cast
            // no shadow for as long as this pass has existed: block a light and `arrived`
            // falls, `residual` rises by exactly as much, and the sum is unchanged. The
            // bake refilled every shadow the moment it was drawn. The bake cannot know
            // about somebody who walked into the room after 1999, so its light is credited
            // against the room's occlusion only, and the shadow is taken off the result.
            // Only against the bake. The ambient floor is not a second copy of anything
            // the rig computes, so nothing is taken off it — it is simply light that is
            // there, and it survives to be occluded below.
            vec3 residual = max(indirect.rgb - (accounted * lightmapped), vec3(0.0));

            vec3 lit = (residual * occlusion) + arrived;

            // What the surface reflects, and how much of it was found. Added rather than
            // mixed in: a floor that reflects a lamp is brighter for it, not less itself.
            // Alpha is the marcher's own confidence — off the edge of the screen there is
            // nothing to reflect and it says so.
            vec4 mirrored = texelFetch(reflectionTarget, pixel, 0);

            outColor = vec4(lit + (mirrored.rgb * mirrored.a * kReflectionStrength), 1.0);
        }
        """;

    private readonly Vk _vk;
    private readonly Device _device;
    private readonly ShaderModule _vertexModule;
    private readonly ShaderModule _fragmentModule;
    private readonly DescriptorSetLayout _setLayout;
    private readonly DescriptorPool _pool;
    private readonly Sampler _sampler;

    private readonly DescriptorSet[] _sets = new DescriptorSet[2];

    private CompositePipeline(
        Vk vk,
        Device device,
        ShaderModule vertexModule,
        ShaderModule fragmentModule,
        DescriptorSetLayout setLayout,
        PipelineLayout layout,
        Pipeline handle,
        DescriptorPool pool,
        Sampler sampler)
    {
        _vk = vk;
        _device = device;
        _vertexModule = vertexModule;
        _fragmentModule = fragmentModule;
        _setLayout = setLayout;
        _pool = pool;
        _sampler = sampler;
        Layout = layout;
        Handle = handle;
    }

    /// <summary>The pipeline.</summary>
    public Pipeline Handle { get; }

    /// <summary>Its layout.</summary>
    public PipelineLayout Layout { get; }

    /// <summary>Builds the pass.</summary>
    /// <param name="context">The device.</param>
    /// <param name="compiler">Compiler for the two stages.</param>
    /// <param name="colorFormat">Format of the image being composited into.</param>
    /// <returns>The pass.</returns>
    public static CompositePipeline Create(
        VulkanContext context, ShaderCompiler compiler, Format colorFormat)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(compiler);

        Vk vk = context.Api;
        Device device = context.Device;

        ShaderModule vertexModule = Module(vk, device, compiler, Vertex, ShaderStage.Vertex);
        ShaderModule fragmentModule = Module(vk, device, compiler, Fragment, ShaderStage.Fragment);

        const uint inputs = 6;

        DescriptorSetLayoutBinding* bindings =
            stackalloc DescriptorSetLayoutBinding[(int)inputs];

        for (uint i = 0; i < inputs; i++)
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
            BindingCount = inputs,
            PBindings = bindings,
        };

        vk.CreateDescriptorSetLayout(device, in layoutInfo, null, out DescriptorSetLayout setLayout);

        DescriptorSetLayout local = setLayout;

        var pipelineLayoutInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1,
            PSetLayouts = &local,
        };

        vk.CreatePipelineLayout(device, in pipelineLayoutInfo, null, out PipelineLayout layout);

        var poolSize = new DescriptorPoolSize(DescriptorType.CombinedImageSampler, 48);

        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            PoolSizeCount = 1,
            PPoolSizes = &poolSize,
            MaxSets = 8,
        };

        vk.CreateDescriptorPool(device, in poolInfo, null, out DescriptorPool pool);

        var samplerInfo = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Nearest,
            MinFilter = Filter.Nearest,
            AddressModeU = SamplerAddressMode.ClampToEdge,
            AddressModeV = SamplerAddressMode.ClampToEdge,
            AddressModeW = SamplerAddressMode.ClampToEdge,
        };

        vk.CreateSampler(device, in samplerInfo, null, out Sampler sampler);

        byte* entryPoint = stackalloc byte[] { (byte)'m', (byte)'a', (byte)'i', (byte)'n', 0 };

        PipelineShaderStageCreateInfo* stages = stackalloc PipelineShaderStageCreateInfo[2];
        stages[0] = new PipelineShaderStageCreateInfo
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.VertexBit,
            Module = vertexModule,
            PName = entryPoint,
        };
        stages[1] = new PipelineShaderStageCreateInfo
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.FragmentBit,
            Module = fragmentModule,
            PName = entryPoint,
        };

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

        // No depth at all: this covers the frame and everything in it has already been
        // depth-tested once.
        var depthStencil = new PipelineDepthStencilStateCreateInfo
        {
            SType = StructureType.PipelineDepthStencilStateCreateInfo,
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
            Layout = layout,
        };

        if (vk.CreateGraphicsPipelines(device, default, 1, in createInfo, null, out Pipeline handle) !=
            Result.Success)
        {
            throw new VulkanException("Could not create the compositing pipeline.");
        }

        return new CompositePipeline(
            vk, device, vertexModule, fragmentModule, setLayout, layout, handle, pool, sampler);
    }

    /// <summary>Points the pass at the six things it reads.</summary>
    /// <param name="indirect">Ambient and baked light, before occlusion.</param>
    /// <param name="direct">The rig's light, before shadowing.</param>
    /// <param name="shadow">The denoised fraction of that light which arrives.</param>
    /// <param name="occlusion">The denoised fraction of the hemisphere that is open.</param>
    /// <param name="dynamicShadow">
    /// The denoised fraction of the rig's light that the characters and props standing in
    /// the room leave alone. One everywhere in a scene with nobody in it.
    /// </param>
    /// <param name="reflections">
    /// The two buffers reflections alternate between, most recent first each frame.
    /// </param>
    /// <remarks>
    /// Two sets, because the reflection pass writes its answer into whichever of its two
    /// buffers was not the last frame's. Everything else is the same in both.
    /// </remarks>
    public void Bind(
        ImageView indirect,
        ImageView direct,
        ImageView shadow,
        ImageView occlusion,
        ImageView dynamicShadow,
        ReadOnlySpan<ImageView> reflections)
    {
        DescriptorSetLayout* layouts = stackalloc DescriptorSetLayout[2]
        {
            _setLayout,
            _setLayout,
        };

        var info = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _pool,
            DescriptorSetCount = 2,
            PSetLayouts = layouts,
        };

        fixed (DescriptorSet* sets = _sets)
        {
            if (_vk.AllocateDescriptorSets(_device, in info, sets) != Result.Success)
            {
                throw new VulkanException("Could not allocate the compositing descriptor sets.");
            }
        }

        DescriptorImageInfo* images = stackalloc DescriptorImageInfo[12];
        WriteDescriptorSet* writes = stackalloc WriteDescriptorSet[12];

        for (uint which = 0; which < 2; which++)
        {
            ImageView[] views =
            [
                indirect,
                direct,
                shadow,
                occlusion,
                reflections.Length > which ? reflections[(int)which] : occlusion,
                dynamicShadow,
            ];

            for (uint i = 0; i < 6; i++)
            {
                uint at = (which * 6) + i;

                images[at] = new DescriptorImageInfo
                {
                    Sampler = _sampler,
                    ImageView = views[i],

                    // The three computed terms are storage images and stay in General; the
                    // two colour targets are read after being written as attachments.
                    ImageLayout = i < 2
                        ? ImageLayout.ShaderReadOnlyOptimal
                        : ImageLayout.General,
                };

                writes[at] = new WriteDescriptorSet
                {
                    SType = StructureType.WriteDescriptorSet,
                    DstSet = _sets[which],
                    DstBinding = i,
                    DescriptorCount = 1,
                    DescriptorType = DescriptorType.CombinedImageSampler,
                    PImageInfo = &images[at],
                };
            }
        }

        _vk.UpdateDescriptorSets(_device, 12, writes, 0, null);
    }

    /// <summary>Draws the frame.</summary>
    /// <param name="command">Command buffer, inside an active rendering scope.</param>
    /// <param name="width">Viewport width.</param>
    /// <param name="height">Viewport height.</param>
    /// <param name="parity">Which of the two reflection buffers holds this frame's.</param>
    public void Record(CommandBuffer command, int width, int height, int parity)
    {
        var viewport = new Viewport { Width = width, Height = height, MaxDepth = 1f };
        var scissor = new Rect2D { Extent = new Extent2D((uint)width, (uint)height) };

        _vk.CmdSetViewport(command, 0, 1, in viewport);
        _vk.CmdSetScissor(command, 0, 1, in scissor);
        _vk.CmdBindPipeline(command, PipelineBindPoint.Graphics, Handle);
        DescriptorSet set = _sets[parity & 1];

        _vk.CmdBindDescriptorSets(
            command, PipelineBindPoint.Graphics, Layout, 0, 1, in set, 0, null);

        _vk.CmdDraw(command, 3, 1, 0, 0);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _vk.DestroyPipeline(_device, Handle, null);
        _vk.DestroyPipelineLayout(_device, Layout, null);
        _vk.DestroyDescriptorPool(_device, _pool, null);
        _vk.DestroyDescriptorSetLayout(_device, _setLayout, null);
        _vk.DestroySampler(_device, _sampler, null);
        _vk.DestroyShaderModule(_device, _fragmentModule, null);
        _vk.DestroyShaderModule(_device, _vertexModule, null);
    }

    private static ShaderModule Module(
        Vk vk, Device device, ShaderCompiler compiler, string source, ShaderStage stage)
    {
        byte[] code = compiler.Compile(source, stage, "composite", "main", ShaderLanguage.Glsl);

        fixed (byte* spirv = code)
        {
            var info = new ShaderModuleCreateInfo
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)code.Length,
                PCode = (uint*)spirv,
            };

            if (vk.CreateShaderModule(device, in info, null, out ShaderModule module) !=
                Result.Success)
            {
                throw new VulkanException("Could not create a compositing shader module.");
            }

            return module;
        }
    }
}
