// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using GK3Reborn.Rendering.Upscaling;
using Silk.NET.Vulkan;

namespace GK3Reborn.Rendering.Vulkan;

/// <summary>
/// The engine's own upscaler: one frame in, edge-directed, no history.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why there is one at all.</b> The two good upscalers are somebody else's binaries and
/// neither ships with this game. Without a built-in one, "render at three quarters and
/// upscale" would be a setting that does nothing until the player has downloaded a DLL,
/// and the settings page would be advertising a feature the game does not have. This is
/// the floor: it works on every device the renderer runs on, it is a few hundred lines,
/// and it is honestly worse than either vendor's.
/// </para>
/// <para>
/// <b>What it does.</b> For each output pixel it takes the sixteen source pixels around
/// where it lands and weights them with a windowed sinc — but not a round one. The
/// three-by-three luminance around the point gives a gradient; the kernel is squeezed
/// across that gradient and left alone along it, so a taut diagonal is resampled along its
/// own direction rather than across it. That is the difference between an upscaled edge
/// that looks like an edge and one that looks like a staircase with grey on it. The result
/// is then clamped to the range of the four nearest source pixels, which is what stops a
/// negative-lobed kernel ringing along the hard edges this game is full of — door numbers,
/// Sidney's screen, the inventory's line art.
/// </para>
/// <para>
/// <b>What it cannot do.</b> Nothing here recovers detail that was never drawn, because
/// there is only one frame to look at. That is the whole reason the temporal upscalers
/// exist and the reason this one is not the default. Its compensation is that it has no
/// history to be wrong about: nothing it produces can ghost, smear or shimmer over time,
/// which for a game played largely in fixed camera angles is worth more than it sounds.
/// </para>
/// <para>
/// The sharpening that normally follows an upscale is not here. It is in
/// <see cref="OutputPipeline"/>, which is the last pass either way and can therefore
/// sharpen a picture that was not upscaled at all.
/// </para>
/// </remarks>
internal sealed unsafe class SpatialUpscaler : IUpscaler
{
    /// <summary>How many pixels one invocation group covers each way.</summary>
    private const uint Group = 8;

    private const string Source = """
        #version 460

        layout(local_size_x = 8, local_size_y = 8) in;

        layout(set = 0, binding = 0) uniform sampler2D sourcePicture;
        layout(set = 0, binding = 1, rgba16f) uniform writeonly image2D destination;

        layout(push_constant) uniform Sizes
        {
            // xy the source in pixels, zw the destination
            vec4 extents;
        } sizes;

        float Luminance(vec3 c)
        {
            return dot(c, vec3(0.2126, 0.7152, 0.0722));
        }

        // A two-lobe Lanczos window, evaluated on the squared distance so the common case
        // never takes a square root. Zero past two pixels, which is where the sixteen taps
        // stop.
        float Weight(float distanceSquared)
        {
            if (distanceSquared >= 4.0)
            {
                return 0.0;
            }

            float d = sqrt(distanceSquared);

            if (d < 1e-5)
            {
                return 1.0;
            }

            float pd = 3.14159265 * d;

            return (sin(pd) / pd) * (sin(pd * 0.5) / (pd * 0.5));
        }

        void main()
        {
            ivec2 pixel = ivec2(gl_GlobalInvocationID.xy);
            ivec2 target = ivec2(sizes.extents.zw);

            if (pixel.x >= target.x || pixel.y >= target.y)
            {
                return;
            }

            vec2 sourceSize = sizes.extents.xy;

            // Where this output pixel's centre lands in the source, in source pixel
            // coordinates with the half-pixel offset taken out so that whole numbers are
            // pixel centres.
            vec2 at = ((vec2(pixel) + 0.5) / vec2(target) * sourceSize) - 0.5;
            vec2 base = floor(at);
            vec2 offset = at - base;

            vec3 taps[16];
            float luma[16];

            for (int y = 0; y < 4; y++)
            {
                for (int x = 0; x < 4; x++)
                {
                    ivec2 coord = ivec2(base) + ivec2(x - 1, y - 1);
                    coord = clamp(coord, ivec2(0), ivec2(sourceSize) - 1);

                    vec3 c = texelFetch(sourcePicture, coord, 0).rgb;

                    taps[(y * 4) + x] = c;
                    luma[(y * 4) + x] = Luminance(c);
                }
            }

            // The gradient of the three by three around the tap nearest the sample point,
            // by Sobel. This is the direction the picture changes fastest in, which is
            // across whatever edge runs through here.
            //
            // Indices are row-major over the four by four: the middle of that window is
            // tap 5, and the three by three around it is columns 0 to 2 of rows 0 to 2.
            float gx =
                (luma[0] + (2.0 * luma[4]) + luma[8]) -
                (luma[2] + (2.0 * luma[6]) + luma[10]);

            float gy =
                (luma[0] + (2.0 * luma[1]) + luma[2]) -
                (luma[8] + (2.0 * luma[9]) + luma[10]);

            vec2 gradient = vec2(gx, gy);
            float steepness = length(gradient);

            // Below this there is no edge worth steering by and the kernel stays round. The
            // threshold is in luminance across three pixels, so it is a real contrast rather
            // than a fraction of one.
            vec2 across = steepness > 0.02 ? gradient / steepness : vec2(0.0);
            vec2 along = vec2(-across.y, across.x);

            // How much to squeeze the kernel across the edge. Capped, because a kernel
            // narrowed without limit is a nearest-neighbour sample, and the staircase that
            // produces is the thing being avoided.
            float squeeze = 1.0 + min(steepness * 6.0, 2.0);

            vec3 sum = vec3(0.0);
            float total = 0.0;

            for (int y = 0; y < 4; y++)
            {
                for (int x = 0; x < 4; x++)
                {
                    vec2 delta = vec2(float(x - 1), float(y - 1)) - offset;

                    // Round where there is no edge, and stretched along one where there is.
                    vec2 shaped = across.x == 0.0 && across.y == 0.0
                        ? delta
                        : vec2(dot(delta, along), dot(delta, across) * squeeze);

                    float w = Weight(dot(shaped, shaped));

                    sum += taps[(y * 4) + x] * w;
                    total += w;
                }
            }

            vec3 colour = total > 1e-5 ? sum / total : taps[5];

            // The four pixels this output actually sits between. A windowed sinc has
            // negative lobes and will overshoot both ways along a hard edge; clamping to
            // the neighbourhood is what turns that overshoot into acuity instead of a halo.
            vec3 lowest = min(min(taps[5], taps[6]), min(taps[9], taps[10]));
            vec3 highest = max(max(taps[5], taps[6]), max(taps[9], taps[10]));

            imageStore(
                destination,
                pixel,
                vec4(clamp(colour, lowest, highest), 1.0));
        }
        """;

    private readonly VulkanContext _context;
    private readonly ComputePipeline _pipeline;
    private readonly Sampler _sampler;
    private readonly Extent2D _render;
    private readonly Extent2D _display;

    /// <summary>
    /// Which upscaler this is standing in for, which is usually itself.
    /// </summary>
    /// <remarks>
    /// It is also what runs when FSR or DLSS was chosen and its runtime is not installed,
    /// and it has to know: a stand-in that reported itself as merely spatial would be
    /// judged not to serve a plan asking for DLSS, and torn down and rebuilt on every
    /// single frame. Which is exactly what it did.
    /// </remarks>
    private readonly UpscalerKind _serving;

    private DescriptorPool _pool;
    private DescriptorSet _set;
    private ImageView _boundSource;
    private ImageView _boundDestination;

    private SpatialUpscaler(
        VulkanContext context,
        ComputePipeline pipeline,
        Sampler sampler,
        DescriptorPool pool,
        Extent2D render,
        Extent2D display,
        UpscalerKind serving)
    {
        _context = context;
        _pipeline = pipeline;
        _sampler = sampler;
        _pool = pool;
        _render = render;
        _display = display;
        _serving = serving;
    }

    /// <inheritdoc/>
    public UpscalerKind Kind => UpscalerKind.Spatial;

    /// <inheritdoc/>
    public string Describe() => string.Create(
        CultureInfo.InvariantCulture,
        $"edge-directed{(_serving == UpscalerKind.Spatial ? string.Empty : $", standing in for {_serving}")}, " +
        $"{_render.Width}x{_render.Height} to {_display.Width}x{_display.Height}");

    /// <summary>Builds it for one pair of sizes.</summary>
    /// <param name="context">The device.</param>
    /// <param name="compiler">Compiler for the stage.</param>
    /// <param name="render">The size the room is drawn at.</param>
    /// <param name="display">The size it is shown at.</param>
    /// <param name="serving">
    /// Which upscaler this is standing in for, when it is not itself. See
    /// <see cref="_serving"/>.
    /// </param>
    /// <returns>The upscaler.</returns>
    public static SpatialUpscaler Create(
        VulkanContext context,
        ShaderCompiler compiler,
        Extent2D render,
        Extent2D display,
        UpscalerKind serving = UpscalerKind.Spatial)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(compiler);

        DescriptorSetLayoutBinding* bindings = stackalloc DescriptorSetLayoutBinding[2];

        bindings[0] = new DescriptorSetLayoutBinding
        {
            Binding = 0,
            DescriptorType = DescriptorType.CombinedImageSampler,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.ComputeBit,
        };

        bindings[1] = new DescriptorSetLayoutBinding
        {
            Binding = 1,
            DescriptorType = DescriptorType.StorageImage,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.ComputeBit,
        };

        ComputePipeline pipeline = ComputePipeline.Create(
            context, compiler, Source, new ReadOnlySpan<DescriptorSetLayoutBinding>(bindings, 2),
            (uint)Marshal.SizeOf<Vector4>());

        Vk vk = context.Api;

        // Nearest, because every read in the shader is a texelFetch. A linear sampler here
        // would be a filter nothing asks for and a lie about what the taps are.
        var samplerInfo = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Nearest,
            MinFilter = Filter.Nearest,
            AddressModeU = SamplerAddressMode.ClampToEdge,
            AddressModeV = SamplerAddressMode.ClampToEdge,
            AddressModeW = SamplerAddressMode.ClampToEdge,
        };

        vk.CreateSampler(context.Device, in samplerInfo, null, out Sampler sampler);

        DescriptorPoolSize* sizes = stackalloc DescriptorPoolSize[2];
        sizes[0] = new DescriptorPoolSize(DescriptorType.CombinedImageSampler, 4);
        sizes[1] = new DescriptorPoolSize(DescriptorType.StorageImage, 4);

        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            PoolSizeCount = 2,
            PPoolSizes = sizes,
            MaxSets = 4,
            Flags = DescriptorPoolCreateFlags.FreeDescriptorSetBit,
        };

        vk.CreateDescriptorPool(context.Device, in poolInfo, null, out DescriptorPool pool);

        return new SpatialUpscaler(context, pipeline, sampler, pool, render, display, serving);
    }

    /// <inheritdoc/>
    public bool Serves(UpscalePlan plan, Extent2D render, Extent2D display) =>
        plan.Kind == _serving &&
        render.Width == _render.Width && render.Height == _render.Height &&
        display.Width == _display.Width && display.Height == _display.Height;

    /// <inheritdoc/>
    public bool Record(CommandBuffer command, in UpscaleFrame frame)
    {
        if (!frame.Colour.Exists || !frame.Output.Exists)
        {
            return false;
        }

        Bind(frame.Colour.View, frame.Output.View);

        Vk vk = _context.Api;

        vk.CmdBindPipeline(command, PipelineBindPoint.Compute, _pipeline.Handle);

        DescriptorSet set = _set;

        vk.CmdBindDescriptorSets(
            command, PipelineBindPoint.Compute, _pipeline.Layout, 0, 1, in set, 0, null);

        var extents = new Vector4(
            frame.Colour.Extent.Width,
            frame.Colour.Extent.Height,
            frame.Output.Extent.Width,
            frame.Output.Extent.Height);

        vk.CmdPushConstants(
            command,
            _pipeline.Layout,
            ShaderStageFlags.ComputeBit,
            0,
            (uint)Marshal.SizeOf<Vector4>(),
            &extents);

        vk.CmdDispatch(
            command,
            (frame.Output.Extent.Width + Group - 1) / Group,
            (frame.Output.Extent.Height + Group - 1) / Group,
            1);

        return true;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Vk vk = _context.Api;

        if (_pool.Handle != 0)
        {
            vk.DestroyDescriptorPool(_context.Device, _pool, null);
            _pool = default;
        }

        vk.DestroySampler(_context.Device, _sampler, null);
        _pipeline.Dispose();
    }

    /// <summary>Points the stage at this frame's two images, when they have changed.</summary>
    /// <remarks>
    /// The two do not change from frame to frame in the ordinary case, so this is a
    /// comparison and a return. It is written as a per-frame call rather than a separate
    /// setup step because the renderer may swap the images underneath — a resize, a
    /// different quality setting — and a descriptor pointing at a destroyed view is a class
    /// of bug that only shows up on somebody else's machine.
    /// </remarks>
    private void Bind(ImageView source, ImageView destination)
    {
        if (_set.Handle != 0 &&
            _boundSource.Handle == source.Handle &&
            _boundDestination.Handle == destination.Handle)
        {
            return;
        }

        Vk vk = _context.Api;

        if (_set.Handle != 0)
        {
            DescriptorSet previous = _set;
            vk.FreeDescriptorSets(_context.Device, _pool, 1, in previous);
            _set = default;
        }

        DescriptorSetLayout layout = _pipeline.SetLayout;

        var info = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _pool,
            DescriptorSetCount = 1,
            PSetLayouts = &layout,
        };

        if (vk.AllocateDescriptorSets(_context.Device, in info, out _set) != Result.Success)
        {
            throw new VulkanException("Could not allocate the spatial upscaler's descriptor set.");
        }

        var sourceImage = new DescriptorImageInfo
        {
            Sampler = _sampler,
            ImageView = source,
            ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
        };

        var destinationImage = new DescriptorImageInfo
        {
            ImageView = destination,
            ImageLayout = ImageLayout.General,
        };

        WriteDescriptorSet* writes = stackalloc WriteDescriptorSet[2];

        writes[0] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = _set,
            DstBinding = 0,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.CombinedImageSampler,
            PImageInfo = &sourceImage,
        };

        writes[1] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = _set,
            DstBinding = 1,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.StorageImage,
            PImageInfo = &destinationImage,
        };

        vk.UpdateDescriptorSets(_context.Device, 2, writes, 0, null);

        _boundSource = source;
        _boundDestination = destination;
    }
}
