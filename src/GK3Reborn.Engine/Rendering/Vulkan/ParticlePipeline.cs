// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Runtime.InteropServices;
using GK3Reborn.Rendering.Shaders;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;

namespace GK3Reborn.Rendering.Vulkan;

/// <summary>
/// Draws a room's smoke and embers over the finished picture, on Vulkan.
/// </summary>
/// <remarks>
/// <para>
/// The one blended pass in the renderer. Everything else the room draws is opaque or cut
/// out against a hard alpha test — see <see cref="MeshPipeline"/> — and smoke cannot be
/// either. It runs after the picture is composed, with the depth the room left still bound
/// so that a puff behind a wall is hidden by it, and it writes no depth of its own so that
/// two puffs do not occlude one another.
/// </para>
/// <para>
/// One colour attachment rather than the room's four. It is recorded in a scope of its own
/// against the lit target, because by this point in the frame the normals and the motion
/// vectors have been read and are finished with, and a pass that declared them would have
/// to say what a wisp of smoke's normal is.
/// </para>
/// </remarks>
public sealed unsafe class ParticlePipeline : IDisposable
{
    private readonly Vk _vk;
    private readonly VulkanContext _context;

    private ShaderModule _vertexModule;
    private ShaderModule _fragmentModule;
    private PipelineLayout _layout;
    private Pipeline _pipeline;
    private VulkanBuffer? _vertices;
    private int _count;

    private ParticlePipeline(VulkanContext context)
    {
        _context = context;
        _vk = context.Api;
    }

    /// <summary>How many particles were written for this frame.</summary>
    public int Count => _count / ParticleVertex.Corners;

    /// <summary>Builds the pipeline.</summary>
    /// <param name="context">The device it belongs to.</param>
    /// <param name="colorFormat">Format of the lit target it draws onto.</param>
    /// <param name="depthFormat">Format of the depth target it tests against.</param>
    /// <param name="compiler">What compiles the two shaders.</param>
    /// <returns>The pipeline.</returns>
    public static ParticlePipeline Create(
        VulkanContext context,
        Format colorFormat,
        Format depthFormat,
        ShaderCompiler compiler)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(compiler);

        var pipeline = new ParticlePipeline(context);

        try
        {
            pipeline._vertexModule = pipeline.CreateModule(compiler.Compile(
                ParticleShaders.Vertex, ShaderStage.Vertex, "particle.vert", "main",
                ShaderLanguage.Glsl));

            pipeline._fragmentModule = pipeline.CreateModule(compiler.Compile(
                ParticleShaders.Fragment, ShaderStage.Fragment, "particle.frag", "main",
                ShaderLanguage.Glsl));

            pipeline._vertices = VulkanBuffer.CreateHostVisible(
                context,
                (ulong)(ParticleVertex.Capacity * ParticleVertex.Corners *
                        Marshal.SizeOf<ParticleVertex>()),
                BufferUsageFlags.VertexBufferBit);

            pipeline.BuildPipeline(colorFormat, depthFormat);

            return pipeline;
        }
        catch
        {
            pipeline.Dispose();
            throw;
        }
    }

    /// <summary>Turns a frame's particles into vertices, ready to draw.</summary>
    /// <param name="particles">The particles, furthest from the eye first.</param>
    /// <remarks>
    /// The order is the caller's: smoke is blended over what is behind it, so two puffs
    /// that overlap have to arrive in depth order. See <see cref="Game.FlameParticles"/>.
    /// </remarks>
    public void Prepare(IReadOnlyList<Particle> particles)
    {
        ArgumentNullException.ThrowIfNull(particles);

        _count = 0;

        if (_vertices is null || particles.Count == 0)
        {
            return;
        }

        var vertices = new ParticleVertex[ParticleVertex.Capacity * ParticleVertex.Corners];
        int written = ParticleVertex.Build(particles, vertices);

        if (written == 0)
        {
            return;
        }

        _vertices.Write<ParticleVertex>(vertices.AsSpan(0, written));
        _count = written;
    }

    /// <summary>Records the draw.</summary>
    /// <param name="command">Command buffer to record into.</param>
    /// <param name="width">Viewport width in pixels.</param>
    /// <param name="height">Its height.</param>
    /// <param name="constants">The camera, as <see cref="ParticleShaders.Describe"/> gives it.</param>
    public void Record(
        CommandBuffer command, int width, int height, ParticleConstants constants)
    {
        if (_count == 0 || _vertices is null)
        {
            return;
        }

        var viewport = new Viewport { Width = width, Height = height, MaxDepth = 1f };
        var scissor = new Rect2D { Extent = new Extent2D((uint)width, (uint)height) };

        _vk.CmdSetViewport(command, 0, 1, in viewport);
        _vk.CmdSetScissor(command, 0, 1, in scissor);
        _vk.CmdBindPipeline(command, PipelineBindPoint.Graphics, _pipeline);

        _vk.CmdPushConstants(
            command,
            _layout,
            ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
            0,
            (uint)sizeof(ParticleConstants),
            &constants);

        Silk.NET.Vulkan.Buffer buffer = _vertices.Handle;
        ulong offset = 0;

        _vk.CmdBindVertexBuffers(command, 0, 1, in buffer, in offset);
        _vk.CmdDraw(command, (uint)_count, 1, 0, 0);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _vertices?.Dispose();
        _vertices = null;

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

        GC.SuppressFinalize(this);
    }

    private ShaderModule CreateModule(byte[] code)
    {
        fixed (byte* bytes = code)
        {
            var info = new ShaderModuleCreateInfo
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)code.Length,
                PCode = (uint*)bytes,
            };

            if (_vk.CreateShaderModule(_context.Device, in info, null, out ShaderModule module)
                != Result.Success)
            {
                throw new VulkanException("Could not create the particle shader module.");
            }

            return module;
        }
    }

    private void BuildPipeline(Format colorFormat, Format depthFormat)
    {
        var pushed = new PushConstantRange
        {
            StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
            Offset = 0,
            Size = (uint)sizeof(ParticleConstants),
        };

        var layoutInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 0,
            PushConstantRangeCount = 1,
            PPushConstantRanges = &pushed,
        };

        if (_vk.CreatePipelineLayout(_context.Device, in layoutInfo, null, out _layout)
            != Result.Success)
        {
            throw new VulkanException("Could not create the particle pipeline layout.");
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
                Stride = (uint)Marshal.SizeOf<ParticleVertex>(),
                InputRate = VertexInputRate.Vertex,
            };

            VertexInputAttributeDescription* attributes =
                stackalloc VertexInputAttributeDescription[3];

            attributes[0] = new VertexInputAttributeDescription
            {
                Location = 0, Binding = 0, Format = Format.R32G32B32A32Sfloat, Offset = 0,
            };
            attributes[1] = new VertexInputAttributeDescription
            {
                Location = 1, Binding = 0, Format = Format.R32G32B32A32Sfloat, Offset = 16,
            };
            attributes[2] = new VertexInputAttributeDescription
            {
                Location = 2, Binding = 0, Format = Format.R32G32B32A32Sfloat, Offset = 32,
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

                // A sprite is turned to face the camera and has no back, so which way its
                // two triangles happen to be wound decides nothing.
                CullMode = CullModeFlags.None,
                FrontFace = FrontFace.CounterClockwise,
            };

            var multisample = new PipelineMultisampleStateCreateInfo
            {
                SType = StructureType.PipelineMultisampleStateCreateInfo,
                RasterizationSamples = SampleCountFlags.Count1Bit,
            };

            // Tested and never written. A puff of smoke behind a wall is hidden by it; two
            // puffs in front of one another both draw, which is the whole point of blending
            // them, and a sprite that wrote depth would delete every sprite behind it.
            var depth = new PipelineDepthStencilStateCreateInfo
            {
                SType = StructureType.PipelineDepthStencilStateCreateInfo,
                DepthTestEnable = depthFormat != Format.Undefined,
                DepthWriteEnable = false,
                DepthCompareOp = CompareOp.LessOrEqual,
            };

            // Premultiplied, so that one blend serves both kinds: an ember writes zero
            // alpha and is added to what is behind it, smoke writes its coverage and hides
            // it. See ParticleShaders.
            var attachment = new PipelineColorBlendAttachmentState
            {
                BlendEnable = true,
                SrcColorBlendFactor = BlendFactor.One,
                DstColorBlendFactor = BlendFactor.OneMinusSrcAlpha,
                ColorBlendOp = BlendOp.Add,
                SrcAlphaBlendFactor = BlendFactor.One,
                DstAlphaBlendFactor = BlendFactor.OneMinusSrcAlpha,
                AlphaBlendOp = BlendOp.Add,
                ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit |
                                 ColorComponentFlags.BBit | ColorComponentFlags.ABit,
            };

            var blend = new PipelineColorBlendStateCreateInfo
            {
                SType = StructureType.PipelineColorBlendStateCreateInfo,
                AttachmentCount = 1,
                PAttachments = &attachment,
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
                throw new VulkanException($"Could not create the particle pipeline: {created}.");
            }
        }
        finally
        {
            SilkMarshal.Free(entryPoint);
        }
    }
}
