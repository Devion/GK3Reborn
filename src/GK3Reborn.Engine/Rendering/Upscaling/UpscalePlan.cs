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
/// <remarks>
/// <para>
/// Deliberately not <c>Game.Settings</c>. The renderer has no business reading a record
/// full of volume levels and easter eggs, and the settings record has no business knowing
/// which of these the device could actually build. This is the whole of what passes
/// between them, and it is a value — so handing the renderer a new one is the only way
/// anything here changes, and comparing two of them is the whole of the test for whether
/// the frame's plumbing has to be rebuilt.
/// </para>
/// <para>
/// <b>Everything is clamped by <see cref="Sane"/>.</b> These arrive from a JSON file
/// somebody may have edited.
/// </para>
/// </remarks>
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
    /// <remarks>
    /// <para>
    /// Here rather than on the output plan because of what it is bound to: frame generation
    /// cannot run without it, and the two are stepped together often enough that separating
    /// them across two structures would mean a caller that set one and forgot the other.
    /// </para>
    /// <para>
    /// On by default. It costs a little throughput on a machine that has throughput to
    /// spare, and this game is not one that runs short of it — what it buys is a mouse that
    /// answers sooner, which is the whole of how an adventure game feels to use.
    /// </para>
    /// </remarks>
    public LatencyMode Latency { get; init; } = LatencyMode.On;

    /// <summary>
    /// Whether DLSS is asked to denoise the traced terms as well as upscale them.
    /// </summary>
    /// <remarks>
    /// Ray reconstruction replaces the engine's own denoiser rather than running after it:
    /// the two are the same job, and filtering a signal twice across frames is how a
    /// picture ends up smeared. Ignored unless <see cref="Kind"/> is
    /// <see cref="UpscalerKind.Dlss"/> and the picture is being traced at all.
    /// </remarks>
    public bool RayReconstruction { get; init; } = true;

    /// <summary>
    /// Which of DLSS's trained models to ask for, or nought for whatever the runtime
    /// thinks best.
    /// </summary>
    /// <remarks>
    /// A number rather than an enumeration on purpose. NVIDIA names its presets by letter
    /// and keeps adding letters — the transformer models arrived as J and K long after the
    /// convolutional ones ran from A to F, and L and M followed — so a build of this game
    /// from before a preset existed should still be able to ask for it once the player
    /// drops in a newer <c>nvngx_dlss.dll</c>. One is A. See <see cref="DlssPresets"/>.
    /// </remarks>
    public int DlssPreset { get; init; }

    /// <summary>Whether the colour handed to the upscaler is high dynamic range.</summary>
    /// <remarks>
    /// Set by the renderer from the output chain rather than by the player: it is a fact
    /// about what the frame holds, and getting it wrong makes an upscaler tone-map twice.
    /// </remarks>
    public bool HighDynamicRange { get; init; }

    /// <summary>Whether anything is being upscaled at all.</summary>
    /// <remarks>
    /// A temporal upscaler at a ratio of one is still doing something — it is anti-aliasing
    /// across frames, which is what DLAA is — so this is about the backend and not about
    /// the ratio. The spatial one at a ratio of one is only its sharpening pass, which is
    /// still worth running if it was asked for.
    /// </remarks>
    public bool Active => Kind != UpscalerKind.Off;

    /// <summary>Whether this backend accumulates across frames.</summary>
    /// <remarks>
    /// The whole of what the renderer has to know to decide whether to jitter the camera
    /// and hand over a depth buffer. The spatial one wants none of it: jittering a frame
    /// nothing accumulates just makes the picture wobble.
    /// </remarks>
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
    /// <remarks>
    /// Rounded rather than floored, and floored at 32: a window dragged down to a sliver
    /// still has to produce a render target Vulkan will accept, and a zero-extent image is
    /// a device loss rather than a small picture.
    /// </remarks>
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
        Quality = Enum.IsDefined(Quality) ? Quality : UpscalerQuality.Quality,
        Sharpness = float.IsFinite(Sharpness) ? Math.Clamp(Sharpness, 0f, 1f) : 0.5f,
        FrameGeneration = Enum.IsDefined(FrameGeneration) ? FrameGeneration : FrameGeneration.Off,
        Latency = Enum.IsDefined(Latency) ? Latency : LatencyMode.On,
        DlssPreset = Math.Clamp(DlssPreset, 0, DlssPresets.Highest),
    };

    /// <summary>How the ratio reads on the settings page.</summary>
    /// <param name="width">Window width in pixels.</param>
    /// <param name="height">Window height in pixels.</param>
    /// <returns>Something like "1280x720 to 1920x1080".</returns>
    /// <remarks>
    /// The numbers rather than the ratio. "Quality" and "1.5x" are both abstractions a
    /// player has to convert before they mean anything; two resolutions are the thing
    /// itself, and somebody who knows their monitor knows at a glance whether the first one
    /// is a size their card will manage.
    /// </remarks>
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
/// <remarks>
/// NVIDIA's presets are letters and the list grows. Rather than an enumeration that has to
/// be edited every time a driver adds one, the setting is the letter's ordinal and this
/// turns it back into a letter for the menu. Anything past the ones with a note still has a
/// name — "Preset Q" — so a future runtime's model can be selected by somebody who has read
/// its release notes, without waiting for this file to catch up.
/// </remarks>
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
    /// <remarks>
    /// Only the ones a player would choose deliberately. The convolutional presets A to F
    /// are gone or deprecated in the 310 runtimes, and G through I revert to the default,
    /// so naming them would be advertising rows that do nothing.
    /// </remarks>
    private static string Note(int preset) => preset switch
    {
        10 => " (transformer)",
        11 => " (transformer, best picture)",
        12 => " (transformer, steadiest)",
        13 => " (transformer, fastest)",
        _ => string.Empty,
    };
}
