// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using GK3Reborn.Formats.Scenes;
using GK3Reborn.Rendering;

namespace GK3Reborn.Game;

/// <summary>
/// Turns a rig built for a 1999 lightmap into one that can be evaluated live.
/// </summary>
public static class RigBalance
{
    /// <summary>
    /// The words the artists used for a light that stands in for bounced light.
    /// </summary>
    private static readonly string[] Indirect =
        ["fill", "ambient", "bounce", "warmer"];

    /// <summary>
    /// Whether a light is scaffolding rather than a source.
    /// </summary>
    /// <param name="light">The light as the scene asset declares it.</param>
    /// <returns>True for a fill, an ambient, a bounce or a warmer.</returns>
    public static bool IsIndirect(AuthoredLight light)
    {
        ArgumentNullException.ThrowIfNull(light);

        foreach (string word in Indirect)
        {
            if (light.Name.Contains(word, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// How much of an indirect light is kept at a given amount of tracing.
    /// </summary>
    /// <param name="quality">How much of the picture is being paid for.</param>
    /// <returns>A multiplier for the intensity of the fills.</returns>
    public static float Keep(RayTracingQuality quality) => quality switch
    {
        RayTracingQuality.None => 1f,
        RayTracingQuality.Low => 0.7f,
        RayTracingQuality.Medium => 0.5f,
        _ => 0.15f,
    };

    /// <summary>
    /// How much of an indirect light is kept, with the player's own answer taken into
    /// account.
    /// </summary>
    /// <param name="quality">How much of the picture is being paid for.</param>
    /// <param name="realistic">Whether only real sources may light the room.</param>
    /// <returns>A multiplier for the intensity of the fills.</returns>
    public static float Keep(RayTracingQuality quality, bool realistic) =>
        realistic && quality != RayTracingQuality.None ? 0f : Keep(quality);

    /// <summary>
    /// Balances a rig for the amount of tracing it is about to be evaluated under.
    /// </summary>
    /// <param name="rig">The room's lights.</param>
    /// <param name="quality">How much of the picture is being paid for.</param>
    /// <param name="dimmed">How many lights were turned down.</param>
    /// <param name="realistic">
    /// Whether the player has asked for only real sources, which takes the scaffolding out
    /// altogether rather than turning it down.
    /// </param>
    /// <returns>The rig, with its baking scaffolding turned down.</returns>
    public static IReadOnlyList<AuthoredLight> For(
        IReadOnlyList<AuthoredLight> rig,
        RayTracingQuality quality,
        out int dimmed,
        bool realistic = false)
    {
        ArgumentNullException.ThrowIfNull(rig);

        dimmed = 0;

        float keep = Keep(quality, realistic);

        if (keep >= 1f)
        {
            return rig;
        }

        var balanced = new List<AuthoredLight>(rig.Count);

        foreach (AuthoredLight light in rig)
        {
            if (!IsIndirect(light))
            {
                balanced.Add(light);
                continue;
            }

            balanced.Add(light with { Intensity = light.Intensity * keep });
            dimmed++;
        }

        return balanced;
    }
}
