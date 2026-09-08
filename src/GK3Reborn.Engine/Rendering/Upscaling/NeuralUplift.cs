// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Globalization;

namespace GK3Reborn.Rendering.Upscaling;

/// <summary>
/// What the neural rendering network is asked to do, on top of upscaling the frame.
/// </summary>
public sealed record NeuralUplift
{
    /// <summary>The highest ordinal a preset or a style will hold.</summary>
    public const int Highest = 15;

    /// <summary>Nothing: the network is not run at all.</summary>
    public static NeuralUplift None { get; } = new();

    /// <summary>Whether to run the network.</summary>
    public bool Enabled { get; init; }

    /// <summary>How much of the whole effect to apply.</summary>
    public float Intensity { get; init; } = 1f;

    /// <summary>How hard local contrast is lifted.</summary>
    public float LocalTone { get; init; } = 1f;

    /// <summary>How hard the picture's overall tone is reworked.</summary>
    public float GlobalTone { get; init; } = 1f;

    /// <summary>How much fine structure and micro-detail is rebuilt.</summary>
    public float LocalStructure { get; init; } = 1f;

    /// <summary>Whether skin takes <see cref="LocalStructure"/> rather than its own strength.</summary>
    public bool SkinFollowsStructure { get; init; } = true;

    /// <summary>How much detail skin takes, when it is not following.</summary>
    public float SkinStructure { get; init; } = 0.5f;

    /// <summary>Whether the network finds skin for itself.</summary>
    public bool AutoSkinMask { get; init; } = true;

    /// <summary>Which of the network's trained weights, or nought for its own choice.</summary>
    public int Preset { get; init; }

    /// <summary>Which of the network's looks, or nought for its own choice.</summary>
    public int Style { get; init; }

    /// <summary>What the skin strength reaches the network as.</summary>
    public float SkinStrength => SkinFollowsStructure ? -1f : SkinStructure;

    /// <summary>The same settings with every value inside its range.</summary>
    /// <returns>A record nothing downstream has to re-check.</returns>
    public NeuralUplift Sane() => this with
    {
        Intensity = Strength(Intensity),
        LocalTone = Strength(LocalTone),
        GlobalTone = Strength(GlobalTone),
        LocalStructure = Strength(LocalStructure),
        SkinStructure = Strength(SkinStructure),
        Preset = Math.Clamp(Preset, 0, Highest),
        Style = Math.Clamp(Style, 0, Highest),
    };

    /// <summary>How a preset or a style reads on the page.</summary>
    /// <param name="ordinal">Nought for the network's own choice, else the number.</param>
    /// <returns>The label.</returns>
    public static string Describe(int ordinal) => ordinal <= 0
        ? "Whatever the network prefers"
        : string.Create(CultureInfo.InvariantCulture, $"Number {ordinal}");

    /// <summary>What this is doing, for the startup line.</summary>
    /// <returns>A short phrase, or an empty string when it is off.</returns>
    public string Summarise() => Enabled
        ? string.Create(
            CultureInfo.InvariantCulture,
            $"neural uplift at {Intensity:0.##}" +
            $"{(AutoSkinMask ? ", auto skin" : string.Empty)}")
        : string.Empty;

    private static float Strength(float value) =>
        float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : 1f;
}
