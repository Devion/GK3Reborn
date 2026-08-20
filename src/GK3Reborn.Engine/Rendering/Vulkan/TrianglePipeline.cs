using Silk.NET.Core.Native;
using Silk.NET.Vulkan;

namespace GK3Reborn.Rendering.Vulkan;

/// <summary>
/// A graphics pipeline drawing a single coloured triangle.
/// </summary>
/// <remarks>
/// <para>
/// The smallest thing that proves the whole chain works: HLSL compiles, SPIR-V loads, a
/// pipeline builds against the swapchain's format, and a draw reaches the screen. Every
/// later pass is this plus vertex buffers, descriptors and more interesting shaders.
/// </para>
/// <para>
/// Vertices are generated in the vertex shader from <c>SV_VertexID</c> rather than read
/// from a buffer. That is deliberate for this step: it means a failure here is a shader,
/// pipeline or render-target problem and cannot be a buffer or memory problem, which
/// makes the first bring-up debuggable.
/// </para>
/// </remarks>
public sealed unsafe class TrianglePipeline : IDisposable
{
    private const string Source = """
        struct VertexOutput
        {
            float4 position : SV_Position;
            float3 color    : COLOR0;
        };

        // A full triangle from the vertex index alone, so this stage needs no buffers.
        VertexOutput VertexMain(uint vertexId : SV_VertexID)
        {
            float2 positions[3] =
            {
                float2( 0.0, -0.6),
                float2( 0.6,  0.6),
                float2(-0.6,  0.6)
            };

            float3 colors[3] =
            {
                float3(0.90, 0.32, 0.28),
                float3(0.36, 0.72, 0.45),
                float3(0.35, 0.55, 0.92)
            };

            VertexOutput output;
            output.position = float4(positions[vertexId], 0.0, 1.0);
            output.color = colors[vertexId];
            return output;
        }

        float4 FragmentMain(VertexOutput input) : SV_Target
        {
            return float4(input.color, 1.0);
        }
        """;

    private readonly Vk _vk;
    private readonly Device _device;

    private ShaderModule _vertexModule;
    private ShaderModule _fragmentModule;
    private PipelineLayout _layout;
    private Pipeline _pipeline;

    private TrianglePipeline(Vk vk, Device device)
    {
        _vk = vk;
        _device = device;
    }

    /// <summary>The pipeline handle, for binding.</summary>
    public Pipeline Handle => _pipeline;

    /// <summary>Builds the pipeline.</summary>
    /// <param name="vk">Vulkan API.</param>
    /// <param name="device">Logical device.</param>
    /// <param name="colorFormat">Format of the target this pipeline renders to.</param>
    /// <param name="compiler">Shader compiler.</param>
    /// <returns>The pipeline.</returns>
    public static TrianglePipeline Create(Vk vk, Device device, Format colorFormat, ShaderCompiler compiler)
    {
        ArgumentNullException.ThrowIfNull(vk);
        ArgumentNullException.ThrowIfNull(compiler);

        var pipeline = new TrianglePipeline(vk, device);

        try
        {
            pipeline._vertexModule = pipeline.CreateModule(
                compiler.Compile(Source, ShaderStage.Vertex, "triangle.vert", "VertexMain"));

            pipeline._fragmentModule = pipeline.CreateModule(
                compiler.Compile(Source, ShaderStage.Fragment, "triangle.frag", "FragmentMain"));

            pipeline.BuildPipeline(colorFormat);
            return pipeline;
        }
        catch
        {
            pipeline.Dispose();
            throw;
        }
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

    private void BuildPipeline(Format colorFormat)
    {
        var layoutInfo = new PipelineLayoutCreateInfo { SType = StructureType.PipelineLayoutCreateInfo };
        if (_vk.CreatePipelineLayout(_device, in layoutInfo, null, out _layout) != Result.Success)
        {
            throw new VulkanException("Could not create a pipeline layout.");
        }

        nint vertexEntry = SilkMarshal.StringToPtr("VertexMain");
        nint fragmentEntry = SilkMarshal.StringToPtr("FragmentMain");

        try
        {
            PipelineShaderStageCreateInfo* stages = stackalloc PipelineShaderStageCreateInfo[2];
            stages[0] = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.VertexBit,
                Module = _vertexModule,
                PName = (byte*)vertexEntry,
            };
            stages[1] = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.FragmentBit,
                Module = _fragmentModule,
                PName = (byte*)fragmentEntry,
            };

            // No vertex input at all: the vertex shader builds its own positions.
            var vertexInput = new PipelineVertexInputStateCreateInfo
            {
                SType = StructureType.PipelineVertexInputStateCreateInfo,
            };

            var inputAssembly = new PipelineInputAssemblyStateCreateInfo
            {
                SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                Topology = PrimitiveTopology.TriangleList,
            };

            // Viewport and scissor are dynamic so a window resize does not require
            // rebuilding the pipeline, only re-recording the command buffer.
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

                // Culling stays off here. GK3's winding is not consistently
                // counter-clockwise, and a first bring-up should not fail invisibly
                // because a triangle faced away.
                CullMode = CullModeFlags.None,
                FrontFace = FrontFace.CounterClockwise,
            };

            var multisample = new PipelineMultisampleStateCreateInfo
            {
                SType = StructureType.PipelineMultisampleStateCreateInfo,
                RasterizationSamples = SampleCountFlags.Count1Bit,
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
                BlendEnable = false,
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

            // Dynamic rendering means the pipeline is told its target formats directly
            // rather than being tied to a render pass object.
            Format* formats = stackalloc Format[(int)GBuffer.Targets]
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
                PColorAttachmentFormats = formats,
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
                PColorBlendState = &blend,
                PDynamicState = &dynamic,
                Layout = _layout,
            };

            if (_vk.CreateGraphicsPipelines(_device, default, 1, in createInfo, null, out _pipeline)
                != Result.Success)
            {
                throw new VulkanException("Could not create the graphics pipeline.");
            }
        }
        finally
        {
            SilkMarshal.Free(vertexEntry);
            SilkMarshal.Free(fragmentEntry);
        }
    }
}
