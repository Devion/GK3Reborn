// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System;
using System.Globalization;
using GK3Reborn.Foundation.Diagnostics;
using GK3Reborn.Rendering.Upscaling;
using Silk.NET.Vulkan;

namespace GK3Reborn.Rendering.Vulkan;

/// <summary>
/// NVIDIA DLSS, driven through Streamline.
/// </summary>
/// <remarks>
/// <para>
/// Streamline has to be started before the Vulkan device exists, because the features it
/// loads ask for device extensions and for queues of their own, and both have to be in the
/// <c>vkCreateDevice</c> call. So the object this talks to — <see cref="Streamline"/> — is
/// made by the host at startup and handed to the renderer, and this class is only the
/// per-frame half: tag the four images, hand over the camera, evaluate the feature.
/// </para>
/// <para>
/// <b>Ray reconstruction instead of the engine's denoiser.</b> When the picture is being
/// traced and the player has the ray-reconstruction runtime installed, the feature
/// evaluated is DLSS-D rather than DLSS: it denoises and upscales in one pass, and running
/// it after this engine's own spatiotemporal filter would be two temporal filters over one
/// signal, which is how a picture ends up smeared. See <c>VulkanRenderer.PrepareDeferred</c>,
/// which leaves its own denoiser unbuilt in that case.
/// </para>
/// <para>
/// This project is GPL-3.0. Loading a separately-installed proprietary upscaler at runtime
/// is a deliberate exception, taken because the alternative is a worse picture for every
/// player who has the hardware for a better one; see <c>NOTICE</c>. Nothing of NVIDIA's is
/// redistributed here and the game runs without it.
/// </para>
/// </remarks>
internal sealed class DlssUpscaler : IUpscaler
{
    private readonly Streamline _streamline;
    private readonly Extent2D _render;
    private readonly Extent2D _display;
    private readonly UpscalerQuality _quality;
    private readonly int _preset;
    private readonly bool _highDynamicRange;
    private readonly bool _rayReconstruction;

    private DlssUpscaler(
        Streamline streamline,
        Extent2D render,
        Extent2D display,
        UpscalerQuality quality,
        int preset,
        bool highDynamicRange,
        bool rayReconstruction)
    {
        _streamline = streamline;
        _render = render;
        _display = display;
        _quality = quality;
        _preset = preset;
        _highDynamicRange = highDynamicRange;
        _rayReconstruction = rayReconstruction;
    }

    /// <inheritdoc/>
    public UpscalerKind Kind => UpscalerKind.Dlss;

    /// <inheritdoc/>
    public string Describe() => string.Create(
        CultureInfo.InvariantCulture,
        $"{_streamline.SuperResolutionVersion}, {(_rayReconstruction ? "ray reconstruction, " : string.Empty)}" +
        $"{_render.Width}x{_render.Height} to {_display.Width}x{_display.Height}");

    /// <summary>Turns the feature on for these sizes, or returns null.</summary>
    /// <param name="context">The device, for its queue.</param>
    /// <param name="runtimes">
    /// Where the player's runtimes were found. Not used to load anything — Streamline did
    /// that at startup — and taken so that every backend is created the same way.
    /// </param>
    /// <param name="streamline">Streamline as the host started it, or null.</param>
    /// <param name="plan">What was asked for.</param>
    /// <param name="render">The size the room is drawn at.</param>
    /// <param name="display">The size it is shown at.</param>
    /// <param name="tracing">Whether the picture is being ray traced.</param>
    /// <returns>The upscaler, or null when DLSS is not available.</returns>
    public static DlssUpscaler? TryCreate(
        VulkanContext context,
        UpscalerRuntimes? runtimes,
        UpscalePlan plan,
        Extent2D render,
        Extent2D display,
        Streamline? streamline = null,
        bool tracing = false)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(plan);

        _ = runtimes;

        if (streamline is not { Ready: true })
        {
            return null;
        }

        // Ray reconstruction only. The neural uplift used to be run from here too, and that
        // was the wrong place for it: it reworks a finished picture and wants one that has
        // been tone-mapped, where this stage hands over linear light with lamps and windows
        // hundreds of times over one. It runs in the renderer now, after the tone map.
        bool reconstruction = !streamline.NeuralRenderingLoaded &&
                              plan.RayReconstruction &&
                              streamline.CanReconstruct(plan.Quality) &&
                              tracing;

        if (!streamline.SetDlssOptions(
                plan.Quality,
                plan.DlssPreset,
                (display.Width, display.Height),
                plan.HighDynamicRange,
                reconstruction,
                plan.Neural))
        {
            Log.Warning("WARNING GK3R3436: DLSS refused the options it was given.");
            return null;
        }

        return new DlssUpscaler(
            streamline,
            render,
            display,
            plan.Quality,
            plan.DlssPreset,
            plan.HighDynamicRange,
            reconstruction);
    }

    /// <inheritdoc/>
    public bool Serves(UpscalePlan plan, Extent2D render, Extent2D display) =>
        plan.Kind == UpscalerKind.Dlss &&
        plan.Quality == _quality &&
        plan.DlssPreset == _preset &&
        plan.HighDynamicRange == _highDynamicRange &&
        render.Width == _render.Width && render.Height == _render.Height &&
        display.Width == _display.Width && display.Height == _display.Height;

    /// <inheritdoc/>
    public bool Record(CommandBuffer command, in UpscaleFrame frame) =>
        _streamline.Evaluate(command.Handle, Describe(in frame), _rayReconstruction);

    /// <summary>Says what this frame is in the terms Streamline asks for.</summary>
    /// <param name="frame">The frame.</param>
    /// <returns>The same frame, described without naming Vulkan.</returns>
    /// <remarks>
    /// Streamline takes a handle, a size, a format and a layout, and keeps the last two as
    /// numbers it never interprets — it knows which API it was given a device for. So the
    /// runtime is neutral and this is the one place that knows an image layout is a Vulkan
    /// image layout.
    /// </remarks>
    private static StreamlineFrame Describe(in UpscaleFrame frame) => new(
        Surface(frame.Colour, ImageLayout.ShaderReadOnlyOptimal),
        Surface(frame.Depth, ImageLayout.ShaderReadOnlyOptimal),
        Surface(frame.Motion, ImageLayout.ShaderReadOnlyOptimal),
        Surface(frame.Output, ImageLayout.General),
        frame.JitterPixels,
        frame.DeltaSeconds,
        frame.Reset,
        frame.Camera,
        frame.Aspect,
        frame.Sharpen,
        frame.Sharpness,
        frame.HighDynamicRange);

    private static UpscaleSurface Surface(UpscaleImage image, ImageLayout layout) => new(
        (nint)image.Image.Handle,
        (nint)image.View.Handle,
        (uint)layout,
        image.Extent.Width,
        image.Extent.Height,
        (uint)image.Format,
        (uint)image.Usage);

    /// <inheritdoc/>
    public void Dispose() => _streamline.ReleaseDlss();
}
