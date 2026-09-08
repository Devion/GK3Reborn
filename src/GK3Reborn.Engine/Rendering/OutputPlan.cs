// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

namespace GK3Reborn.Rendering;

/// <summary>How the finished picture is encoded for the display.</summary>
public enum HdrTransfer
{
    /// <summary>Whichever of the two below the surface offers, PQ first.</summary>
    Automatic,

    /// <summary>HDR10: absolute luminance through the ST.2084 curve, in Rec.2020.</summary>
    PerceptualQuantiser,

    /// <summary>scRGB: linear light in sRGB primaries, with values above one and below nought.</summary>
    ExtendedLinear,
}

/// <summary>What the picture is put through before it is shown.</summary>
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
public sealed record OutputPlan
{
    /// <summary>The standard-range picture, which is what everything did before HDR.</summary>
    public static OutputPlan Standard { get; } = new();

    /// <summary>Whether the swapchain is asked for a high dynamic range colour space.</summary>
    public bool HighDynamicRange { get; init; }

    /// <summary>Which encoding to ask the surface for.</summary>
    public HdrTransfer Transfer { get; init; } = HdrTransfer.Automatic;

    /// <summary>What the SDR picture is put through. Ignored in HDR.</summary>
    public ToneMapping ToneMap { get; init; } = ToneMapping.Clip;

    /// <summary>Where diffuse white sits, in candelas per square metre.</summary>
    public float PaperWhiteNits { get; init; } = 200f;

    /// <summary>The brightest the display can go.</summary>
    public float PeakNits { get; init; } = 1000f;

    /// <summary>The darkest it can go, for the mastering metadata.</summary>
    public float BlackNits { get; init; } = 0.005f;

    /// <summary>Where a sunlit surface is allowed to reach.</summary>
    public float SunNits { get; init; } = 800f;

    /// <summary>Where a lamp, a bulb or a lit window is allowed to reach.</summary>
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
    public float Headroom => HighDynamicRange
        ? Math.Clamp(PeakNits / MathF.Max(PaperWhiteNits, 1f), 1f, 100f)
        : 1f;

    /// <summary>The same plan with every value inside its range.</summary>
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
