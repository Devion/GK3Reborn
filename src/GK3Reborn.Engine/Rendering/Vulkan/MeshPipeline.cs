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
/// <param name="LightDirection">Direction the fallback key light travels.</param>
/// <param name="CameraPosition">Where the eye is, in world space.</param>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct FrameUniforms(
    Matrix4x4 ViewProjection, Vector4 LightDirection, Vector4 CameraPosition);

/// <summary>Constants that change per draw, delivered as push constants.</summary>
/// <param name="Model">Model to world space.</param>
/// <param name="Shading">
/// How to shade: x selects the lightmap over the directional term, y scales the lightmap.
/// </param>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct DrawConstants(Matrix4x4 Model, Vector4 Shading);

/// <summary>
/// A textured, lit mesh pipeline.
/// </summary>
/// <remarks>
/// <para>
/// Resources are split by how often they change. Set 0 holds the camera and is bound once
/// a frame. Set 1 holds a batch's two textures and never changes at all, so it is built
/// when the geometry is loaded and simply rebound. What is left — the model transform and
/// the shading mode — travels as push constants, which need no buffer, no descriptor and
/// no synchronisation between frames in flight.
/// </para>
/// <para>
/// That last point is why this shape was worth the trouble. Per-draw uniform buffers have
/// to be either allocated per frame or written while a previous frame may still be reading
/// them, and getting that wrong produces flickering that is very hard to attribute.
/// </para>
/// <para>
/// Depth testing is on. GK3's models are solid objects with self-occluding parts, and
/// without a depth buffer a character renders as an unreadable tangle of surfaces in draw
/// order.
/// </para>
/// </remarks>
public sealed unsafe class MeshPipeline : IDisposable
{
    private const string Source = """
        struct Frame
        {
            float4x4 viewProjection;
            float4   lightDirection;
            float4   cameraPosition;
        };

        struct Draw
        {
            float4x4 model;
            float4   shading;
        };

        [[vk::binding(0, 0)]] ConstantBuffer<Frame> frame;

        [[vk::binding(0, 1)]] Texture2D    baseColor;
        [[vk::binding(0, 1)]] SamplerState baseColorSampler;
        [[vk::binding(1, 1)]] Texture2D    lightmap;
        [[vk::binding(1, 1)]] SamplerState lightmapSampler;

        [[vk::push_constant]] ConstantBuffer<Draw> draw;

        struct VertexInput
        {
            float3 position      : POSITION;
            float3 normal        : NORMAL;
            float2 texCoord      : TEXCOORD0;
            float2 lightmapCoord : TEXCOORD1;
        };

        struct VertexOutput
        {
            float4 position      : SV_Position;
            float3 normal        : NORMAL;
            float2 texCoord      : TEXCOORD0;
            float2 lightmapCoord : TEXCOORD1;
        };

        VertexOutput VertexMain(VertexInput input)
        {
            VertexOutput output;

            float4 world = mul(draw.model, float4(input.position, 1.0));

            output.position = mul(frame.viewProjection, world);
            output.normal = normalize(mul((float3x3)draw.model, input.normal));
            output.texCoord = input.texCoord;
            output.lightmapCoord = input.lightmapCoord;

            return output;
        }

        float4 FragmentMain(VertexOutput input) : SV_Target
        {
            float4 sampled = baseColor.Sample(baseColorSampler, input.texCoord);
            float3 albedo = sampled.rgb;

            // GK3 keys transparency on magenta. It is converted to alpha before upload, so
            // the test here is on alpha — which filters and mips gracefully — with the
            // colour test kept as a backstop for anything the conversion missed.
            if (sampled.a < 0.5 || distance(albedo, float3(1.0, 0.0, 1.0)) < 0.1)
            {
                discard;
            }

            // A single directional term plus an ambient floor, so surfaces facing away
            // stay readable rather than going black.
            float lambert = saturate(dot(normalize(input.normal), -frame.lightDirection.xyz));
            float3 directional = albedo * (0.35 + 0.65 * lambert);

            // Baked lighting multiplies into the texture, as the original does.
            float3 baked = albedo * lightmap.Sample(lightmapSampler, input.lightmapCoord).rgb *
                           draw.shading.y;

            float3 lit = lerp(directional, baked, draw.shading.x);

            return float4(lit, 1.0);
        }
        """;

    private readonly Vk _vk;
    private readonly Device _device;

    private ShaderModule _vertexModule;
    private ShaderModule _fragmentModule;
    private DescriptorSetLayout _frameLayout;
    private DescriptorSetLayout _materialLayout;
    private PipelineLayout _layout;
    private Pipeline _pipeline;

    private MeshPipeline(Vk vk, Device device)
    {
        _vk = vk;
        _device = device;
    }

    /// <summary>The pipeline handle.</summary>
    public Pipeline Handle => _pipeline;

    /// <summary>The pipeline layout, for binding descriptor sets and push constants.</summary>
    public PipelineLayout Layout => _layout;

    /// <summary>Layout of set 0: the camera.</summary>
    public DescriptorSetLayout FrameLayout => _frameLayout;

    /// <summary>Layout of set 1: a batch's textures.</summary>
    public DescriptorSetLayout MaterialLayout => _materialLayout;

    /// <summary>Builds the pipeline.</summary>
    /// <param name="context">Device context.</param>
    /// <param name="colorFormat">Colour target format.</param>
    /// <param name="depthFormat">Depth target format.</param>
    /// <param name="compiler">Shader compiler.</param>
    /// <returns>The pipeline.</returns>
    public static MeshPipeline Create(
        VulkanContext context, Format colorFormat, Format depthFormat, ShaderCompiler compiler)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(compiler);

        var pipeline = new MeshPipeline(context.Api, context.Device);

        try
        {
            pipeline._vertexModule = pipeline.CreateModule(
                compiler.Compile(Source, ShaderStage.Vertex, "mesh.vert", "VertexMain"));

            pipeline._fragmentModule = pipeline.CreateModule(
                compiler.Compile(Source, ShaderStage.Fragment, "mesh.frag", "FragmentMain"));

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
        var frameBinding = new DescriptorSetLayoutBinding
        {
            Binding = 0,
            DescriptorType = DescriptorType.UniformBuffer,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
        };

        var frameInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 1,
            PBindings = &frameBinding,
        };

        if (_vk.CreateDescriptorSetLayout(_device, in frameInfo, null, out _frameLayout) != Result.Success)
        {
            throw new VulkanException("Could not create the frame descriptor set layout.");
        }

        DescriptorSetLayoutBinding* materialBindings = stackalloc DescriptorSetLayoutBinding[2];
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

        var materialInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 2,
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

            var binding = new VertexInputBindingDescription
            {
                Binding = 0,
                Stride = (uint)Marshal.SizeOf<MeshVertex>(),
                InputRate = VertexInputRate.Vertex,
            };

            VertexInputAttributeDescription* attributes = stackalloc VertexInputAttributeDescription[4];
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

            var vertexInput = new PipelineVertexInputStateCreateInfo
            {
                SType = StructureType.PipelineVertexInputStateCreateInfo,
                VertexBindingDescriptionCount = 1,
                PVertexBindingDescriptions = &binding,
                VertexAttributeDescriptionCount = 4,
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

            if (_vk.CreateGraphicsPipelines(_device, default, 1, in createInfo, null, out _pipeline)
                != Result.Success)
            {
                throw new VulkanException("Could not create the mesh pipeline.");
            }
        }
        finally
        {
            SilkMarshal.Free(vertexEntry);
            SilkMarshal.Free(fragmentEntry);
        }
    }
}
