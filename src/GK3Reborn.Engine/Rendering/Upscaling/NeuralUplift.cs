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
/// <remarks>
/// <para>
/// The network is <c>nvngx_dlssnr.dll</c>, driven through <see cref="Ngx"/> rather than
/// through Streamline. It takes the same three pictures super resolution does — colour,
/// depth and motion — and hands back a frame at the display size that has been reworked as
/// well as scaled: local contrast lifted, fine structure rebuilt, skin treated separately
/// from everything else.
/// </para>
/// <para>
/// <b>Every number here is a taste rather than a correctness.</b> Nothing in this record can
/// make the picture wrong in the way a mistaken depth or a mistaken motion vector can; the
/// worst any of them does is overdo the effect. So they are all exposed, and the ones whose
/// effect is not certain say so on the page rather than being hidden.
/// </para>
/// <para>
/// Off by default. It is a change to how the game looks rather than a fix for how it looks,
/// and a port whose whole business is the 1999 picture should not quietly restyle it.
/// </para>
/// </remarks>
public sealed record NeuralUplift
{
    /// <summary>The highest ordinal a preset or a style will hold.</summary>
    /// <remarks>
    /// A number rather than an enumeration for the reason <see cref="DlssPresets"/> gives:
    /// what a network ships is a table inside somebody else's DLL, and a build of this game
    /// from before an entry existed should still be able to ask for it once the player drops
    /// in a newer one. The network reports anything it does not have as unavailable and falls
    /// back, so a number past the end is quiet rather than fatal.
    /// </remarks>
    public const int Highest = 15;

    /// <summary>Nothing: the network is not run at all.</summary>
    public static NeuralUplift None { get; } = new();

    /// <summary>Whether to run the network.</summary>
    public bool Enabled { get; init; }

    /// <summary>How much of the whole effect to apply.</summary>
    /// <remarks>
    /// The master strength, and the first thing to pull down when the picture looks worked
    /// on. One is the scale's full end rather than an excess: the plugin NVIDIA ships
    /// supplies no default for this and reads whatever the caller set, so nought is not
    /// "leave the picture alone" — it is asking the network to do none of what it does, which
    /// it does not answer by passing the frame through untouched.
    /// </remarks>
    public float Intensity { get; init; } = 1f;

    /// <summary>How hard local contrast is lifted.</summary>
    /// <remarks>What makes textures and the edges of lit areas read more strongly.</remarks>
    public float LocalTone { get; init; } = 1f;

    /// <summary>How hard the picture's overall tone is reworked.</summary>
    /// <remarks>
    /// The one control the ReShade add-in this integration was checked against does not
    /// expose, and it is exposed here because the network plainly reads it: NVIDIA's own
    /// plugin sets it beside the other three from a field of its own. Left at one, which is
    /// what that plugin sends when its caller says nothing.
    /// </remarks>
    public float GlobalTone { get; init; } = 1f;

    /// <summary>How much fine structure and micro-detail is rebuilt.</summary>
    /// <remarks>
    /// The control most worth being careful with. Too much is not blur or noise but invented
    /// detail — a surface that grows a texture the artist never painted — which is the one
    /// failure of this network that a player will read as the game being wrong rather than as
    /// a setting being high.
    /// </remarks>
    public float LocalStructure { get; init; } = 1f;

    /// <summary>Whether skin takes <see cref="LocalStructure"/> rather than its own strength.</summary>
    /// <remarks>
    /// The network keeps a separate strength for skin, and a negative value in it means
    /// "whatever the general one is". That sentinel is a toggle here rather than the bottom
    /// of a slider, because "follow the other setting" and "none at all" are different
    /// answers and a slider cannot say both.
    /// </remarks>
    public bool SkinFollowsStructure { get; init; } = true;

    /// <summary>How much detail skin takes, when it is not following.</summary>
    /// <remarks>
    /// Worth turning down on its own. Faces are what a player looks at, and structure a
    /// landscape carries well is structure a face carries as pores that were never modelled.
    /// </remarks>
    public float SkinStructure { get; init; } = 0.5f;

    /// <summary>Whether the network finds skin for itself.</summary>
    /// <remarks>
    /// The alternative is handing it a mask saying which pixels are skin, which this engine
    /// has nothing to build from. On, therefore — and it is the second switch to try when the
    /// picture is wrong in a way that follows the people in it rather than the room.
    /// </remarks>
    public bool AutoSkinMask { get; init; } = true;

    /// <summary>Which of the network's trained weights, or nought for its own choice.</summary>
    public int Preset { get; init; }

    /// <summary>Which of the network's looks, or nought for its own choice.</summary>
    public int Style { get; init; }

    /// <summary>What the skin strength reaches the network as.</summary>
    /// <remarks>
    /// Negative one is the network's own way of saying "use the general structure strength",
    /// which is why the toggle and the slider collapse into one number here rather than at
    /// the call site.
    /// </remarks>
    public float SkinStrength => SkinFollowsStructure ? -1f : SkinStructure;

    /// <summary>The same settings with every value inside its range.</summary>
    /// <returns>A record nothing downstream has to re-check.</returns>
    /// <remarks>
    /// These arrive from a JSON file somebody may have edited, and a strength that is not a
    /// number reaches a network as one and comes back as a frame of nothing.
    /// </remarks>
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
