// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Numerics;
using GK3Reborn.Formats.Scenes;

namespace GK3Reborn.Game;

/// <summary>
/// The sun, for the scenes the artists lit without one.
/// </summary>
public static class Sunlight
{
    /// <summary>How far away the sun stands, in scene units.</summary>
    private const float Distance = 60_000f;

    /// <summary>The emitter's radius, sized so the disc subtends half a degree.</summary>
    private const float Disc = 260f;

    /// <summary>Whether an authored light is the artists' own sun.</summary>
    /// <param name="light">The light as the scene asset declares it.</param>
    /// <param name="minimum">One corner of the loaded geometry.</param>
    /// <param name="maximum">The other.</param>
    /// <returns>True for a scenekey: distant, unattenuated, and shadow-casting.</returns>
    public static bool IsAuthoredSun(AuthoredLight light, Vector3 minimum, Vector3 maximum)
    {
        ArgumentNullException.ThrowIfNull(light);

        if (light is not { UsesAttenuation: false, CastsShadows: true, AttenuationEnd: > 0 })
        {
            return false;
        }

        Vector3 nearest = Vector3.Clamp(light.Position, minimum, maximum);

        return Vector3.Distance(light.Position, nearest) > light.AttenuationEnd;
    }

    /// <summary>The artists' own sun among a scene's lights, if it ships one.</summary>
    /// <param name="lights">Every light the scene asset declares.</param>
    /// <param name="minimum">One corner of the loaded geometry.</param>
    /// <param name="maximum">The other.</param>
    /// <returns>The scenekey, or null where there is none.</returns>
    public static AuthoredLight? AuthoredSun(
        IReadOnlyList<AuthoredLight>? lights, Vector3 minimum, Vector3 maximum) =>
        lights?
            .Where(light => IsAuthoredSun(light, minimum, maximum))
            .MaxBy(light => light.Intensity);

    /// <summary>
    /// The sun for a point in the story, or null where there should not be one.
    /// </summary>
    /// <param name="timeblock">When it is.</param>
    /// <param name="centre">The middle of the scene, which the sun is placed relative to.</param>
    /// <param name="authored">
    /// The scenekey this stands in for, from <see cref="AuthoredSun"/>, or null where the
    /// asset ships none.
    /// </param>
    /// <returns>The light, or null at night.</returns>
    public static AuthoredLight? For(
        Timeblock timeblock, Vector3 centre, AuthoredLight? authored = null)
    {
        if (Arc(timeblock) is not { } arc)
        {
            return null;
        }

        (float azimuth, float hourly) = arc;

        // From the scene toward the sun. The scenekey's own bearing where the asset has
        // one; otherwise east of the map in the morning, overhead-south at midday, west by
        // evening.
        Vector3 toward = Aim(authored, centre) ?? new Vector3(
            MathF.Cos(hourly) * MathF.Sin(azimuth),
            MathF.Sin(hourly),
            MathF.Cos(hourly) * MathF.Cos(azimuth));

        // Measured off whichever bearing was chosen rather than off the hour, so a scenekey
        // standing low over a morning square is warm for standing low rather than for the
        // clock saying so.
        float elevation = MathF.Asin(Math.Clamp(toward.Y, -1f, 1f));

        // Warmer the lower it stands.
        float warm = 1f - (elevation / (MathF.PI / 2f));
        var colour = new Vector3(1f, 0.97f - (0.12f * warm), 0.92f - (0.28f * warm));

        return new AuthoredLight(
            "sun",
            AuthoredLightKind.Point,
            centre + (toward * Distance),
            -toward,
            colour,
            HotSpot: 0,
            Falloff: 0,
            AttenuationStart: 0,
            AttenuationEnd: 0,

            // No decay: the difference in distance between one end of a scene and the
            // other is a fraction of a percent, and the artists' own no-attenuation lights
            // are read exactly this way.
            UsesAttenuation: false,
            CastsShadows: true,
            Intensity: Strength,
            Radius: Disc);
    }

    /// <summary>How bright the replacement stands, against the rig it joins.</summary>
    private const float Strength = 1.15f;

    /// <summary>
    /// Which way the artists' scenekey says the light comes from, or null to fall back to
    /// the hour.
    /// </summary>
    /// <param name="authored">The scenekey, or null.</param>
    /// <param name="centre">The middle of the room it lights.</param>
    /// <returns>A unit vector from the scene toward the sun, or null.</returns>
    private static Vector3? Aim(AuthoredLight? authored, Vector3 centre)
    {
        if (authored is null)
        {
            return null;
        }

        Vector3 toward = authored.Position - centre;
        float distance = toward.Length();

        if (distance < 1f)
        {
            return null;
        }

        toward /= distance;

        return toward.Y > 0.05f ? toward : null;
    }

    /// <summary>Where the sun stands at each of the game's hours.</summary>
    /// <returns>Azimuth and elevation in radians, or null at night.</returns>
    private static (float Azimuth, float Elevation)? Arc(Timeblock timeblock)
    {
        int hour = (timeblock.IsAfternoon && timeblock.Hour != 12
            ? timeblock.Hour + 12
            : timeblock.Hour) % 24;

        // Night: the 2am dig, and everything from six in the evening on. The evening
        // blocks' art — skybox, lamps lit, the bake — is painted as dusk, and a sun over
        // a night sky argues with all of it. Those scenes keep their authored rigs whole.
        if (hour is < 7 or >= 18)
        {
            return null;
        }

        // Sunrise about six, sunset just after the last daytime block ends, the noon peak
        // a little above sixty degrees — a southern French summer, near enough.
        float day = (hour - 6f) / 13f;

        return (
            float.Lerp(80f, 280f, day) * MathF.PI / 180f,
            MathF.Sin(day * MathF.PI) * (62f * MathF.PI / 180f));
    }
}
