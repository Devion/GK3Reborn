// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using GK3Reborn.Rendering.Geometry;
using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;

using GK3Reborn.Rendering.Shaders;

namespace GK3Reborn.Rendering.Vulkan;

/// <summary>What the output pass is told about the display, per frame.</summary>
/// <param name="Tuning">
/// x which transfer function, y where paper white sits, z how far above it the display can
/// go, w which tone curve.
/// </param>
/// <param name="Sharpen">
/// x how hard to sharpen — nought for not at all — and yz the size of one source pixel.
/// </param>
[StructLayout(LayoutKind.Sequential)]
internal readonly record struct OutputConstants(Vector4 Tuning, Vector4 Sharpen);

/// <summary>
/// The last thing that happens to a frame: a tone curve, a sharpen, and whatever encoding
/// the display's colour space wants.
/// </summary>
/// <remarks>
/// <para>
/// Everything before this writes linear light into a floating-point target, where a value
/// of one means "diffuse white" and values above it are allowed. That is the only form in
/// which upscaling, ray tracing and HDR all work; it is also not a picture any display can
/// accept. This pass turns it into one.
/// </para>
/// <para>
/// It exists in the standard-range path as well, doing almost nothing — a copy with an
/// optional sharpen — and that is deliberate. Having one place where the frame becomes a
/// picture is what makes the HDR path a different set of push constants rather than a
/// different renderer, and what lets the interface keep being drawn afterwards onto the
/// swapchain in exactly the way it always was.
/// </para>
/// <para>
/// The sharpen is contrast-adaptive: it takes the five-tap cross around a pixel, works out
/// how much local contrast there is to spend, and sharpens by an amount that cannot
/// overshoot the neighbourhood. Run over an already-upscaled picture it is what puts back
/// the acuity a resample costs, and unlike an unsharp mask it will not ring along a hard
/// edge — which in this game means the hotel's door numbers and Sidney's screen text.
/// </para>
/// </remarks>
internal sealed unsafe class OutputPipeline : IDisposable
{
    /// <summary>The hardware encodes: write linear and let the sRGB target do the curve.</summary>
    public const float TransferHardware = 0f;

    /// <summary>ST.2084, in Rec.2020 primaries, with luminance in absolute nits.</summary>
    public const float TransferPerceptualQuantiser = 1f;

    /// <summary>scRGB: linear light in sRGB primaries, where 1.0 is 80 nits.</summary>
    public const float TransferExtendedLinear = 2f;

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

        layout(set = 0, binding = 0) uniform sampler2D picture;

        layout(push_constant) uniform Display
        {
            // x transfer function, y paper white, z headroom above it, w tone curve
            vec4 tuning;

            // x sharpness, yz one source pixel in texture coordinates
            vec4 sharpen;
        } display;

        // Rec.709 to Rec.2020, which is what ST.2084 signalling is carried in. Written out
        // rather than looked up: it is nine constants and a matrix constructor, and a
        // texture lookup for a fixed 3x3 would be worse in every way.
        const mat3 kRec709ToRec2020 = mat3(
            0.6274040, 0.0690970, 0.0163916,
            0.3292820, 0.9195400, 0.0880132,
            0.0433136, 0.0113612, 0.8955950);

        // scRGB is defined with 1.0 at 80 candelas, which is the sRGB reference white the
        // standard was written against.
        const float kScRgbReferenceNits = 80.0;

        // ST.2084 is defined against a peak of ten thousand candelas.
        const float kPqPeakNits = 10000.0;

        float Luminance(vec3 c)
        {
            return dot(c, vec3(0.2126, 0.7152, 0.0722));
        }

        // Reinhard, applied to luminance rather than to each channel. Per channel it
        // desaturates everything bright towards white, which is exactly what a fire or a
        // stained-glass window must not do.
        vec3 Reinhard(vec3 c)
        {
            float l = Luminance(c);
            return l > 0.0001 ? c * ((l / (1.0 + l)) / l) : c;
        }

        // The filmic shoulder, in the form Hable published: a rational curve with a toe and
        // a shoulder, normalised so that the white point comes out at one.
        vec3 FilmicCurve(vec3 x)
        {
            const float a = 0.15, b = 0.50, c = 0.10, d = 0.20, e = 0.02, f = 0.30;
            return (((x * ((a * x) + (c * b))) + (d * e)) / ((x * ((a * x) + b)) + (d * f))) -
                   (e / f);
        }

        vec3 Filmic(vec3 colour)
        {
            const float white = 11.2;
            return FilmicCurve(colour * 2.0) / FilmicCurve(vec3(white));
        }

        float PerceptualQuantiser(float nits)
        {
            const float m1 = 0.1593017578125;
            const float m2 = 78.84375;
            const float c1 = 0.8359375;
            const float c2 = 18.8515625;
            const float c3 = 18.6875;

            float y = clamp(nits / kPqPeakNits, 0.0, 1.0);
            float p = pow(y, m1);

            return pow((c1 + (c2 * p)) / (1.0 + (c3 * p)), m2);
        }

        // Contrast-adaptive sharpening over the five-tap cross. The amount is derived from
        // how much room the neighbourhood has left in both directions, so a pixel already
        // near black or near white is sharpened hardly at all and cannot be pushed past
        // either end — which is the whole difference between this and an unsharp mask.
        vec3 Sharpened(vec2 uv)
        {
            vec2 step = display.sharpen.yz;

            vec3 c = texture(picture, uv).rgb;

            if (display.sharpen.x <= 0.0)
            {
                return c;
            }

            vec3 n = texture(picture, uv + vec2(0.0, -step.y)).rgb;
            vec3 s = texture(picture, uv + vec2(0.0, step.y)).rgb;
            vec3 w = texture(picture, uv + vec2(-step.x, 0.0)).rgb;
            vec3 e = texture(picture, uv + vec2(step.x, 0.0)).rgb;

            vec3 lowest = min(min(min(n, s), min(w, e)), c);
            vec3 highest = max(max(max(n, s), max(w, e)), c);

            // How much headroom there is either way, whichever is the smaller: how far the
            // darkest tap is above black, and how far the brightest is below white. A pixel
            // with nothing left in either direction is sharpened by nothing, which is what
            // stops the filter from ringing. The square root makes the response perceptual.
            //
            // Above white there is no answer to "how far below white", so the second term
            // falls to nought and highlights are left alone. That is the right behaviour in
            // high dynamic range as well as the only defined one: a lamp at four times
            // paper white has no detail in it to recover.
            vec3 room = sqrt(clamp(
                min(lowest, max(vec3(0.0), vec3(2.0) - highest)) / max(highest, vec3(1e-4)),
                0.0, 1.0));

            // The strongest ratio the filter will use, interpolated by the setting. Eight
            // is barely visible and five is as far as this can go without the ring it
            // exists to avoid.
            float peak = -1.0 / mix(8.0, 5.0, clamp(display.sharpen.x, 0.0, 1.0));

            vec3 weight = room * peak;
            vec3 sum = ((n + s + w + e) * weight) + c;

            return clamp(sum / ((4.0 * weight) + 1.0), lowest, highest);
        }

        void main()
        {
            vec3 colour = max(Sharpened(inUv), vec3(0.0));

            float transfer = display.tuning.x;

            if (transfer < 0.5)
            {
                // Standard range. The target is an sRGB format and the hardware does the
                // encode on write, so all that is left is the tone curve — and the default
                // curve is the clip this game has always had, because every reference
                // image in the corpus was taken through it.
                float curve = display.tuning.w;

                if (curve > 1.5)
                {
                    colour = Filmic(colour);
                }
                else if (curve > 0.5)
                {
                    colour = Reinhard(colour);
                }

                outColor = vec4(clamp(colour, 0.0, 1.0), 1.0);
                return;
            }

            // High dynamic range. No tone curve at all below the headroom the display
            // actually has: the point of the exercise is that a lamp is brighter than a
            // wall, and a curve that pulls it back down is the thing being escaped from.
            // Above the headroom there is nowhere left to go and it is clamped, which is
            // what the display would do anyway and at least keeps hue.
            float paperWhite = max(display.tuning.y, 1.0);
            float headroom = max(display.tuning.z, 1.0);

            float luminance = Luminance(colour);

            if (luminance > headroom)
            {
                colour *= headroom / luminance;
            }

            if (transfer > 1.5)
            {
                // scRGB. Linear light, sRGB primaries, and one unit is 80 candelas.
                outColor = vec4(colour * (paperWhite / kScRgbReferenceNits), 1.0);
                return;
            }

            vec3 wide = kRec709ToRec2020 * (colour * paperWhite);

            outColor = vec4(
                PerceptualQuantiser(wide.r),
                PerceptualQuantiser(wide.g),
                PerceptualQuantiser(wide.b),
                1.0);
        }
        """;

    private readonly Vk _vk;
    private readonly Device _device;
    private readonly ShaderModule _vertexModule;
    private readonly ShaderModule _fragmentModule;
    private readonly DescriptorSetLayout _setLayout;
    private readonly DescriptorPool _pool;
    private readonly Sampler _sampler;

    private DescriptorSet _set;

    private OutputPipeline(
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

    /// <summary>The format this was built to write into.</summary>
    /// <remarks>
    /// Kept so the renderer can tell whether a swapchain rebuild invalidated it. A pipeline
    /// carries the attachment format it was created with, so one built for an 8-bit sRGB
    /// swapchain cannot be used to write a 10-bit HDR one.
    /// </remarks>
    public Format ColorFormat { get; private set; }

    /// <summary>Builds the pass.</summary>
    /// <param name="context">The device.</param>
    /// <param name="compiler">Compiler for the two stages.</param>
    /// <param name="colorFormat">Format of the swapchain image being written.</param>
    /// <returns>The pass.</returns>
    public static OutputPipeline Create(
        VulkanContext context, ShaderCompiler compiler, Format colorFormat)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(compiler);

        Vk vk = context.Api;
        Device device = context.Device;

        ShaderModule vertexModule = Module(vk, device, compiler, Vertex, ShaderStage.Vertex);
        ShaderModule fragmentModule = Module(vk, device, compiler, Fragment, ShaderStage.Fragment);

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

        vk.CreateDescriptorSetLayout(device, in layoutInfo, null, out DescriptorSetLayout setLayout);

        DescriptorSetLayout local = setLayout;

        var range = new PushConstantRange
        {
            StageFlags = ShaderStageFlags.FragmentBit,
            Offset = 0,
            Size = (uint)Marshal.SizeOf<OutputConstants>(),
        };

        var pipelineLayoutInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1,
            PSetLayouts = &local,
            PushConstantRangeCount = 1,
            PPushConstantRanges = &range,
        };

        vk.CreatePipelineLayout(device, in pipelineLayoutInfo, null, out PipelineLayout layout);

        var poolSize = new DescriptorPoolSize(DescriptorType.CombinedImageSampler, 8);

        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            PoolSizeCount = 1,
            PPoolSizes = &poolSize,
            MaxSets = 8,

            // Rebound whenever the source changes, which is every time the upscaler is
            // switched on or off from the settings page.
            Flags = DescriptorPoolCreateFlags.FreeDescriptorSetBit,
        };

        vk.CreateDescriptorPool(device, in poolInfo, null, out DescriptorPool pool);

        // Linear, because the sharpen samples between pixels and because a source that is
        // not quite the size of the target — which a driver can hand back after a resize —
        // should stretch rather than alias.
        var samplerInfo = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Linear,
            MinFilter = Filter.Linear,
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
            throw new VulkanException("Could not create the output pipeline.");
        }

        return new OutputPipeline(
            vk, device, vertexModule, fragmentModule, setLayout, layout, handle, pool, sampler)
        {
            ColorFormat = colorFormat,
        };
    }

    /// <summary>Points the pass at the finished picture.</summary>
    /// <param name="picture">The linear frame, at the size it will be shown.</param>
    /// <remarks>
    /// Called whenever that image changes, which is on every resize and every time the
    /// upscaler is switched — the source is the upscaled image when there is one and the
    /// rendered one when there is not.
    /// </remarks>
    public void Bind(ImageView picture)
    {
        if (_set.Handle != 0)
        {
            DescriptorSet previous = _set;
            _vk.FreeDescriptorSets(_device, _pool, 1, in previous);
            _set = default;
        }

        DescriptorSetLayout layout = _setLayout;

        var info = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _pool,
            DescriptorSetCount = 1,
            PSetLayouts = &layout,
        };

        if (_vk.AllocateDescriptorSets(_device, in info, out _set) != Result.Success)
        {
            throw new VulkanException("Could not allocate the output descriptor set.");
        }

        var image = new DescriptorImageInfo
        {
            Sampler = _sampler,
            ImageView = picture,
            ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
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

        _vk.UpdateDescriptorSets(_device, 1, in write, 0, null);
    }

    /// <summary>Whether anything has been bound to draw.</summary>
    public bool Ready => _set.Handle != 0;

    /// <summary>Draws the frame onto the swapchain.</summary>
    /// <param name="command">Command buffer, inside an active rendering scope.</param>
    /// <param name="width">Swapchain width.</param>
    /// <param name="height">Swapchain height.</param>
    /// <param name="constants">What to tell the shader about the display.</param>
    public void Record(CommandBuffer command, int width, int height, OutputConstants constants)
    {
        if (_set.Handle == 0)
        {
            return;
        }

        var viewport = new Viewport { Width = width, Height = height, MaxDepth = 1f };
        var scissor = new Rect2D { Extent = new Extent2D((uint)width, (uint)height) };

        _vk.CmdSetViewport(command, 0, 1, in viewport);
        _vk.CmdSetScissor(command, 0, 1, in scissor);
        _vk.CmdBindPipeline(command, PipelineBindPoint.Graphics, Handle);

        DescriptorSet set = _set;

        _vk.CmdBindDescriptorSets(
            command, PipelineBindPoint.Graphics, Layout, 0, 1, in set, 0, null);

        OutputConstants pushed = constants;

        _vk.CmdPushConstants(
            command,
            Layout,
            ShaderStageFlags.FragmentBit,
            0,
            (uint)Marshal.SizeOf<OutputConstants>(),
            &pushed);

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
        byte[] code = compiler.Compile(source, stage, "output", "main", ShaderLanguage.Glsl);

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
                throw new VulkanException("Could not create an output shader module.");
            }

            return module;
        }
    }
}
