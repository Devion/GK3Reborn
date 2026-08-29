// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

namespace GK3Reborn.Rendering;

/// <summary>How the finished picture is encoded for the display.</summary>
/// <remarks>
/// Which of these a surface actually offers is a fact about the machine, so the choice the
/// player makes is a preference and the renderer takes the nearest thing it can get. See
/// <c>VulkanRenderer.ChooseFormat</c>.
/// </remarks>
public enum HdrTransfer
{
    /// <summary>Whichever of the two below the surface offers, PQ first.</summary>
    /// <remarks>
    /// The right answer nearly always, and the only one that can be given without asking
    /// the player what their monitor is. PQ first because it is what a television and most
    /// HDR monitors want, and because its ten bits carry a great deal further up the
    /// luminance range than ten bits of anything linear would.
    /// </remarks>
    Automatic,

    /// <summary>HDR10: absolute luminance through the ST.2084 curve, in Rec.2020.</summary>
    PerceptualQuantiser,

    /// <summary>scRGB: linear light in sRGB primaries, with values above one and below nought.</summary>
    ExtendedLinear,
}

/// <summary>What the picture is put through before it is shown.</summary>
/// <remarks>
/// <para>
/// <see cref="Clip"/> is what this game has always done, and it stays the default in
/// standard range for exactly that reason: every reference image in the corpus was taken
/// with it, and a tone curve is not something to change underneath a regression suite as
/// a side effect of adding HDR.
/// </para>
/// <para>
/// It is also not as bad a choice here as it sounds. GK3's rooms were lit for an 8-bit
/// target in 1999, and outside the ray-traced highlights almost nothing in them ever
/// exceeds white.
/// </para>
/// </remarks>
public enum ToneMapping
{
    /// <summary>Anything above white is white.</summary>
    Clip,

    /// <summary>A gentle roll-off that never quite reaches white.</summary>
    Reinhard,

    /// <summary>The filmic shoulder, which keeps highlight colour rather than desaturating to white.</summary>
    Filmic,
}

/// <summary>
/// What the end of the frame does: how bright the display is, and how the picture is
/// encoded for it.
/// </summary>
/// <remarks>
/// <para>
/// Everything here is a number about the <em>display</em> rather than about the scene, and
/// none of it can be discovered reliably. A monitor's EDID routinely claims a peak
/// luminance it cannot hold, and Windows' own HDR calibration exists because of it. So
/// these are settings, with defaults that are safe on almost anything and a page that says
/// what each one does.
/// </para>
/// <para>
/// <b>Paper white is the one that matters.</b> It is where a sheet of white paper sits, and
/// therefore where the interface sits and where a plainly lit wall sits. Too low and the
/// game looks dim next to the desktop; too high and there is no headroom left for anything
/// to be brighter than a wall, which is the whole point of the exercise.
/// </para>
/// </remarks>
public sealed record OutputPlan
{
    /// <summary>The standard-range picture, which is what everything did before HDR.</summary>
    public static OutputPlan Standard { get; } = new();

    /// <summary>Whether the swapchain is asked for a high dynamic range colour space.</summary>
    /// <remarks>
    /// A request rather than a fact. A surface that offers no HDR colour space leaves this
    /// on and changes nothing, and the renderer says so rather than pretending.
    /// </remarks>
    public bool HighDynamicRange { get; init; }

    /// <summary>Which encoding to ask the surface for.</summary>
    public HdrTransfer Transfer { get; init; } = HdrTransfer.Automatic;

    /// <summary>What the SDR picture is put through. Ignored in HDR.</summary>
    public ToneMapping ToneMap { get; init; } = ToneMapping.Clip;

    /// <summary>Where diffuse white sits, in candelas per square metre.</summary>
    /// <remarks>
    /// Two hundred, which is roughly where Windows puts SDR content on an HDR display and
    /// therefore what makes the game match the desktop it was launched from.
    /// </remarks>
    public float PaperWhiteNits { get; init; } = 200f;

    /// <summary>The brightest the display can go.</summary>
    /// <remarks>
    /// A thousand is the figure most HDR monitors are sold against and few sustain. Setting
    /// it above what the panel can do does not break anything — the panel clips — but it
    /// wastes the top of the range, which is why there is a row for it.
    /// </remarks>
    public float PeakNits { get; init; } = 1000f;

    /// <summary>The darkest it can go, for the mastering metadata.</summary>
    public float BlackNits { get; init; } = 0.005f;

    /// <summary>Where a sunlit surface is allowed to reach.</summary>
    /// <remarks>
    /// <para>
    /// Four times paper white by default. Real daylight is thousands of times a lit
    /// interior wall and no display can show that, but a sun that comes out at exactly the
    /// same brightness as the wall it lights is the thing that makes an SDR exterior look
    /// flat, and it is the single change that most makes an HDR one look like daylight.
    /// </para>
    /// <para>
    /// It reaches the picture through the rig rather than through a curve: the synthesised
    /// sun's intensity is multiplied on the way to the shader. See
    /// <c>GpuLight.From</c> and <see cref="SunGain"/>.
    /// </para>
    /// </remarks>
    public float SunNits { get; init; } = 800f;

    /// <summary>Where a lamp, a bulb or a lit window is allowed to reach.</summary>
    /// <remarks>
    /// The emitters themselves, not what they light. GK3 marks these surfaces in its own
    /// data — the original binds a white lightmap and a multiplier of one to them, which is
    /// its way of saying "this is its own light source" — so the game already knows exactly
    /// which pixels these are, and they are the ones with somewhere to go on an HDR
    /// display. Five times paper white by default.
    /// </remarks>
    public float LightNits { get; init; } = 1000f;

    /// <summary>How much brighter than white a self-lit surface is drawn.</summary>
    public float EmissiveGain => HighDynamicRange
        ? Math.Clamp(LightNits / MathF.Max(PaperWhiteNits, 1f), 1f, 64f)
        : 1f;

    /// <summary>How much brighter than it was authored the sun burns.</summary>
    public float SunGain => HighDynamicRange
        ? Math.Clamp(SunNits / MathF.Max(PaperWhiteNits, 1f), 1f, 64f)
        : 1f;

    /// <summary>How far above white the picture may go before the display clips.</summary>
    /// <remarks>
    /// The number the encoding pass scales by. In HDR a shading value of one is paper white
    /// and the range above it runs to this; in SDR there is nothing above one at all.
    /// </remarks>
    public float Headroom => HighDynamicRange
        ? Math.Clamp(PeakNits / MathF.Max(PaperWhiteNits, 1f), 1f, 100f)
        : 1f;

    /// <summary>The same plan with every value inside its range.</summary>
    /// <remarks>
    /// The ordering matters as much as the bounds: a peak below paper white would ask the
    /// encoder for negative headroom, and a black level above paper white is not a black
    /// level. Both are clamped rather than rejected, because a settings file is a text file
    /// somebody may edit and none of this is worth failing to start over.
    /// </remarks>
    public OutputPlan Sane()
    {
        float paper = float.IsFinite(PaperWhiteNits)
            ? Math.Clamp(PaperWhiteNits, 40f, 1000f)
            : 200f;

        float peak = float.IsFinite(PeakNits) ? Math.Clamp(PeakNits, paper, 10_000f) : 1000f;

        return this with
        {
            Transfer = Enum.IsDefined(Transfer) ? Transfer : HdrTransfer.Automatic,
            ToneMap = Enum.IsDefined(ToneMap) ? ToneMap : ToneMapping.Clip,
            PaperWhiteNits = paper,
            PeakNits = peak,
            BlackNits = float.IsFinite(BlackNits)
                ? Math.Clamp(BlackNits, 0f, MathF.Min(1f, paper))
                : 0.005f,
            SunNits = float.IsFinite(SunNits) ? Math.Clamp(SunNits, paper, 10_000f) : 800f,
            LightNits = float.IsFinite(LightNits) ? Math.Clamp(LightNits, paper, 10_000f) : 1000f,
        };
    }
}
