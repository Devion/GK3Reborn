// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Numerics;
using GK3Reborn.Formats.Scenes;

namespace GK3Reborn.Rendering;

/// <summary>
/// The sun's rays through a room's air: what the renderer is told about the sun so that it
/// can draw the light streaming from it past whatever stands in the way.
/// </summary>
/// <param name="Toward">A unit vector from the room toward the sun.</param>
/// <param name="Colour">What the sun's light is, in linear light.</param>
/// <param name="Strength">
/// How strongly the rays are drawn, from nought for none to one for the pass's own tuning.
/// Nought is the switch: a room with the sun over it and the rays turned off is this with
/// nought here, and the pass is not run.
/// </param>
/// <param name="Shafts">
/// The shafts of daylight at the room's windows, indoors, or none. Drawn by the same pass
/// as volumes: see <see cref="LightShaft"/>.
/// </param>
public readonly record struct SunRays(
    Vector3 Toward, Vector3 Colour, float Strength, IReadOnlyList<LightShaft>? Shafts = null)
{
    /// <summary>No rays: a room with no sun, an hour with none, or the rays turned off.</summary>
    public static SunRays None { get; }

    /// <summary>Whether the sky's part of the pass has anything to draw.</summary>
    public bool Sky => Toward.LengthSquared() > 0.5f;

    /// <summary>The shafts, never null.</summary>
    public IReadOnlyList<LightShaft> Windows => Shafts ?? [];

    /// <summary>Whether the pass has anything to draw.</summary>
    public bool Any => Strength > 0f && (Sky || Windows.Count > 0);

    /// <summary>The rays for a room's sun.</summary>
    /// <param name="sun">The sun the room is lit by, from <see cref="Game.Sunlight"/>, or null at night or indoors.</param>
    /// <param name="strength">How strongly to draw them; one is the pass's own tuning.</param>
    /// <returns>The rays, or <see cref="None"/> where there is no sun.</returns>
    public static SunRays For(AuthoredLight? sun, float strength = 1f)
    {
        if (sun is null || sun.Direction.LengthSquared() < 1e-6f)
        {
            return None;
        }

        // The light's direction is the way it travels; the rays want the way to the sun.
        Vector3 toward = Vector3.Normalize(-sun.Direction);

        // Below the horizon is no sun at all, whatever the rig says.
        if (toward.Y <= 0.01f)
        {
            return None;
        }

        return new SunRays(toward, sun.Color, Math.Clamp(strength, 0f, 4f));
    }

    /// <summary>These rays, with the daylight at the windows added.</summary>
    /// <param name="shafts">The shafts, from <see cref="Game.SceneShafts.For"/>.</param>
    /// <returns>The same rays carrying the shafts. A room with no sun keeps none.</returns>
    public SunRays Through(IReadOnlyList<LightShaft> shafts)
    {
        ArgumentNullException.ThrowIfNull(shafts);

        return this with { Shafts = shafts.Count > 0 ? shafts : null };
    }

    /// <summary>These rays, drawn or not.</summary>
    /// <param name="on">Whether to draw them.</param>
    /// <returns>The same rays, or the same rays at nought.</returns>
    public SunRays Lit(bool on) => on ? this : this with { Strength = 0f };
}
