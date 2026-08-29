using System.Numerics;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;

namespace GK3Reborn.Rendering.Vulkan;

/// <summary>What the fade is told: the colour to draw, and what the display wants.</summary>
/// <param name="Color">The wash, straight alpha.</param>
/// <param name="Display">Which encoding, paper white, and the headroom above it.</param>
[System.Runtime.InteropServices.StructLayout(
    System.Runtime.InteropServices.LayoutKind.Sequential)]
internal readonly record struct FadeConstants(Vector4 Color, DisplayEncode Display);

/// <summary>
/// Draws a flat colour over the finished picture, at whatever opacity it is given.
/// </summary>
/// <remarks>
/// <para>
/// What a scene change looks like. Everything else the renderer draws is a thing in the
/// world or a thing on the interface; this is neither, and it goes over both — a fade that
/// left the inventory bar showing would be a fade of the room rather than of the picture.
/// </para>
/// <para>
/// No vertex buffer, no descriptors and no texture: one triangle covering the screen,
/// generated from the vertex index, and a push constant carrying the colour. That makes it
/// the cheapest pass in the renderer, which matters because it is recorded on every frame
/// of the game and does nothing on almost all of them.
/// </para>
/// <para>
/// The colour is written as it is given and the blend is straight, so an alpha of one
/// leaves the target exactly that colour and an alpha of a half leaves it halfway there.
/// See <see cref="OverlayPipeline"/> for why the interface's own colours have to be
/// converted first: this one writes the number it is handed, and the ramp it is driven
/// along is the caller's business.
/// </para>
/// </remarks>
public sealed unsafe class FadePipeline : IDisposable
{
    /// <remarks>
    /// GLSL rather than HLSL, for the reason <see cref="OverlayPipeline"/> gives: a push
    /// constant is one unambiguous declaration here and a coin toss through shaderc's HLSL
    /// front end, which fails by compiling and drawing nothing.
    /// </remarks>
    private const string VertexSource = @"#version 450

void main()
{
    // One oversized triangle rather than two, so there is no seam down the diagonal and
    // no vertex buffer to bind. Vertices 0, 1 and 2 land at (-1,-1), (3,-1) and (-1,3);
    // the part of each that falls outside the viewport is clipped away and what remains
    // covers it exactly.
    vec2 corner = vec2((gl_VertexIndex << 1) & 2, gl_VertexIndex & 2);
    gl_Position = vec4((corner * 2.0) - 1.0, 0.0, 1.0);
}
";

    /// <summary>The fragment stage, with the shared display encode spliced in.</summary>
    /// <remarks>See <see cref="DisplayEncoding"/>: one copy of ST.2084 rather than four.</remarks>
    private static readonly string FragmentSource =
        FragmentPrelude + "\n" + DisplayEncoding.Glsl + "\n" + FragmentBody;

    private const string FragmentPrelude = @"#version 450

layout(push_constant) uniform Fade
{
    vec4 color;

    // Which encoding the swapchain wants, where paper white sits, and how far above it
    // the display goes. All nought on an ordinary sRGB surface.
    vec4 display;
} fade;

layout(location = 0) out vec4 outColor;
";

    private const string FragmentBody = @"
void main()
{
    // The fade covers the interface as well as the room, so it is encoded the same way
    // both of them were. A fade written unencoded onto a PQ surface is a wash of the
    // wrong colour that gets *lighter* as it deepens.
    outColor = vec4(EncodeForDisplay(fade.color.rgb, fade.display.xyz), fade.color.a);
}
";

    private readonly Vk _vk;
    private readonly VulkanContext _context;

    private ShaderModule _vertexModule;
    private ShaderModule _fragmentModule;
    private PipelineLayout _layout;
    private Pipeline _pipeline;

    private FadePipeline(VulkanContext context)
    {
        _context = context;
        _vk = context.Api;
    }

    /// <summary>What the swapchain wants written into it.</summary>
    /// <remarks>
    /// Set by the renderer. Standard by default, which is the sRGB target the hardware
    /// encodes and where the wash is written exactly as it always was.
    /// </remarks>
    public DisplayEncode Display { get; set; } = DisplayEncode.Standard;

    /// <summary>Builds the pipeline.</summary>
    /// <param name="context">The device it belongs to.</param>
    /// <param name="colorFormat">Format of the target it draws onto.</param>
    /// <param name="depthFormat">Format of the depth target the pass carries.</param>
    /// <param name="compiler">What compiles the two shaders.</param>
    /// <returns>The pipeline.</returns>
    public static FadePipeline Create(
        VulkanContext context,
        Format colorFormat,
        Format depthFormat,
        ShaderCompiler compiler)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(compiler);

        var pipeline = new FadePipeline(context);

        try
        {
            pipeline._vertexModule = pipeline.CreateModule(compiler.Compile(
                VertexSource, ShaderStage.Vertex, "fade.vert", "main", ShaderLanguage.Glsl));

            pipeline._fragmentModule = pipeline.CreateModule(compiler.Compile(
                FragmentSource, ShaderStage.Fragment, "fade.frag", "main", ShaderLanguage.Glsl));

            pipeline.BuildPipeline(colorFormat, depthFormat);

            return pipeline;
        }
        catch
        {
            pipeline.Dispose();
            throw;
        }
    }

    /// <summary>Records the draw.</summary>
    /// <param name="command">Command buffer to record into.</param>
    /// <param name="width">Viewport width in pixels.</param>
    /// <param name="height">Viewport height in pixels.</param>
    /// <param name="color">
    /// What to draw over the picture, straight alpha. An alpha of zero records nothing.
    /// </param>
    public void Record(CommandBuffer command, int width, int height, Vector4 color)
    {
        if (color.W <= 0f)
        {
            return;
        }

        var viewport = new Viewport { Width = width, Height = height, MaxDepth = 1f };
        var scissor = new Rect2D { Extent = new Extent2D((uint)width, (uint)height) };

        _vk.CmdSetViewport(command, 0, 1, in viewport);
        _vk.CmdSetScissor(command, 0, 1, in scissor);
        _vk.CmdBindPipeline(command, PipelineBindPoint.Graphics, _pipeline);

        FadeConstants pushed = new(color, Display);

        _vk.CmdPushConstants(
            command,
            _layout,
            ShaderStageFlags.FragmentBit,
            0,
            (uint)sizeof(FadeConstants),
            &pushed);

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
                throw new VulkanException("Could not create the fade shader module.");
            }

            return module;
        }
    }

    private void BuildPipeline(Format colorFormat, Format depthFormat)
    {
        var pushed = new PushConstantRange
        {
            StageFlags = ShaderStageFlags.FragmentBit,
            Offset = 0,
            Size = (uint)sizeof(FadeConstants),
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
            throw new VulkanException("Could not create the fade pipeline layout.");
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

            // Nothing comes in: the triangle is built from the vertex index.
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
                DepthTestEnable = false,
                DepthWriteEnable = false,
                DepthCompareOp = CompareOp.Always,
            };

            // Four attachments described and three masked off, for the reason
            // OverlayPipeline gives: the frame this is recorded into has more than the
            // picture, and a pipeline must describe every one of them or the other targets
            // take whatever the shader happens to leave in them.
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
                throw new VulkanException($"Could not create the fade pipeline: {created}.");
            }
        }
        finally
        {
            SilkMarshal.Free(entryPoint);
        }
    }
}
