// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Globalization;

namespace GK3Reborn.Rendering.Upscaling;

/// <summary>
/// What the player asked the upscaler for, in the form the renderer acts on.
/// </summary>
public sealed record UpscalePlan
{
    /// <summary>Nothing: draw at the size of the window and show it.</summary>
    public static UpscalePlan None { get; } = new();

    /// <summary>Which upscaler.</summary>
    public UpscalerKind Kind { get; init; } = UpscalerKind.Off;

    /// <summary>How much of the picture to draw.</summary>
    public UpscalerQuality Quality { get; init; } = UpscalerQuality.Quality;

    /// <summary>Whether the upscaler is asked to sharpen what it produces.</summary>
    public bool Sharpen { get; init; } = true;

    /// <summary>How hard, from nothing to as much as the pass will do.</summary>
    public float Sharpness { get; init; } = 0.5f;

    /// <summary>Whether frames are interpolated between the drawn ones, and how many.</summary>
    public FrameGeneration FrameGeneration { get; init; } = FrameGeneration.Off;

    /// <summary>How hard to work at keeping latency down.</summary>
    public LatencyMode Latency { get; init; } = LatencyMode.On;

    /// <summary>
    /// Whether DLSS is asked to denoise the traced terms as well as upscale them.
    /// </summary>
    public bool RayReconstruction { get; init; } = true;

    /// <summary>
    /// Which of DLSS's trained models to ask for, or nought for whatever the runtime
    /// thinks best.
    /// </summary>
    public int DlssPreset { get; init; }

    /// <summary>
    /// What the neural rendering network is asked to do, where the player has one.
    /// </summary>
    public NeuralUplift Neural { get; init; } = NeuralUplift.None;

    /// <summary>Whether the colour handed to the upscaler is high dynamic range.</summary>
    public bool HighDynamicRange { get; init; }

    /// <summary>Whether anything is being upscaled at all.</summary>
    public bool Active => Kind != UpscalerKind.Off;

    /// <summary>Whether this backend accumulates across frames.</summary>
    public bool Temporal => Kind is UpscalerKind.Fsr or UpscalerKind.Dlss;

    /// <summary>How many pixels across the window there are for each one drawn.</summary>
    public float Ratio => Quality switch
    {
        UpscalerQuality.Native => 1.0f,
        UpscalerQuality.UltraQuality => 1.3f,
        UpscalerQuality.Quality => 1.5f,
        UpscalerQuality.Balanced => 1.7f,
        UpscalerQuality.Performance => 2.0f,
        _ => 3.0f,
    };

    /// <summary>What size to draw the room at, for a window of a given size.</summary>
    /// <param name="width">Window width in pixels.</param>
    /// <param name="height">Window height in pixels.</param>
    /// <returns>The render size, never smaller than 32 by 32.</returns>
    public (int Width, int Height) RenderSize(int width, int height)
    {
        if (!Active)
        {
            return (Math.Max(1, width), Math.Max(1, height));
        }

        float ratio = Ratio;

        return (
            Math.Max(32, (int)MathF.Round(Math.Max(1, width) / ratio)),
            Math.Max(32, (int)MathF.Round(Math.Max(1, height) / ratio)));
    }

    /// <summary>The same plan with every value inside its range.</summary>
    public UpscalePlan Sane() => this with
    {
        Kind = Enum.IsDefined(Kind) ? Kind : UpscalerKind.Off,
        // The network runs one-to-one and only one-to-one, for two reasons that both hold.
        // It is handed depth and motion, and those are the size the room was drawn at, so a
        // picture shown at any other size would have it reading its guides off the edge. And
        // the plugin sets no scaling ratio for it in any case — the parameter names for one
        // are not in the plugin at all — so asked to scale it refuses every frame. Pinning
        // the rung is what makes the setting mean what it says.
        //
        // Native is not nothing: with DLSS selected the upscaler still runs as DLAA, and the
        // uplift reworks what it resolved.
        Quality = Neural is { Enabled: true }
            ? UpscalerQuality.Native
            : Enum.IsDefined(Quality) ? Quality : UpscalerQuality.Quality,
        Sharpness = float.IsFinite(Sharpness) ? Math.Clamp(Sharpness, 0f, 1f) : 0.5f,
        FrameGeneration = Enum.IsDefined(FrameGeneration) ? FrameGeneration : FrameGeneration.Off,
        Latency = Enum.IsDefined(Latency) ? Latency : LatencyMode.On,
        DlssPreset = Math.Clamp(DlssPreset, 0, DlssPresets.Highest),
        Neural = Neural?.Sane() ?? NeuralUplift.None,
    };

    /// <summary>How the ratio reads on the settings page.</summary>
    /// <param name="width">Window width in pixels.</param>
    /// <param name="height">Window height in pixels.</param>
    /// <returns>Something like "1280x720 to 1920x1080".</returns>
    public string Describe(int width, int height)
    {
        if (!Active)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{width}x{height}");
        }

        (int drawn, int tall) = RenderSize(width, height);

        return string.Create(
            CultureInfo.InvariantCulture, $"{drawn}x{tall} to {width}x{height}");
    }
}

/// <summary>What the numbers in <see cref="UpscalePlan.DlssPreset"/> mean.</summary>
public static class DlssPresets
{
    /// <summary>The highest ordinal the setting will hold, which is Z.</summary>
    public const int Highest = 26;

    /// <summary>How a preset reads on the page.</summary>
    /// <param name="preset">Nought for the runtime's own choice, else 1 for A and up.</param>
    /// <returns>The label.</returns>
    public static string Describe(int preset) => preset is <= 0 or > Highest
        ? "Whatever the runtime prefers"
        : string.Create(
            CultureInfo.InvariantCulture,
            $"Preset {(char)('A' + preset - 1)}{Note(preset)}");

    /// <summary>What the runtime's own notes say about a preset worth knowing about.</summary>
    private static string Note(int preset) => preset switch
    {
        10 => " (transformer)",
        11 => " (transformer, best picture)",
        12 => " (transformer, steadiest)",
        13 => " (transformer, fastest)",
        _ => string.Empty,
    };
}
