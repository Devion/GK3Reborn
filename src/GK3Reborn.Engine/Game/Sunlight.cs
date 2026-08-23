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
/// <remarks>
/// <para>
/// Most exteriors ship a <c>scenekey</c> — the artists' own sun — but it carries the two
/// hundred unit range 3ds Max left in the file with its attenuation switched off, and that
/// range is honoured on lightmapped surfaces (see <c>GpuLight.RangeOf</c> for why it must
/// be). So the authored sun reaches the characters and nothing else: on the ray-traced
/// tiers the ground under them had no key light at all, nothing outdoors cast a shadow,
/// and ground cut with real cobble relief read as flat, because relief is legible only
/// under grazing directional light.
/// </para>
/// <para>
/// So outdoors the scenekey is replaced with this: a single warm sun far enough away to be
/// effectively directional and unattenuated for real, so it reaches the ground, aimed by
/// the timeblock's own hour rather than by where one morning's artist parked it. It
/// subtends about half a degree — the real sun's size — so its ray-traced shadows have the
/// real penumbra. The sky-bounce fills stay, interiors are left exactly alone, and so is
/// any exterior at night.
/// </para>
/// </remarks>
public static class Sunlight
{
    /// <summary>How far away the sun stands, in scene units.</summary>
    /// <remarks>
    /// Far enough that its direction is the same across the largest exterior — RC1 spans
    /// about three thousand units — and its light does not measurably fall off across one.
    /// </remarks>
    private const float Distance = 60_000f;

    /// <summary>The emitter's radius, sized so the disc subtends half a degree.</summary>
    private const float Disc = 260f;

    /// <summary>Whether an authored light is the artists' own sun.</summary>
    /// <param name="light">The light as the scene asset declares it.</param>
    /// <param name="minimum">One corner of the loaded geometry.</param>
    /// <param name="maximum">The other.</param>
    /// <returns>True for a scenekey: distant, unattenuated, and shadow-casting.</returns>
    /// <remarks>
    /// Recognised by shape rather than name, the same shape <c>GpuLight.IsDistantKey</c>
    /// keys on: the attenuation switch off, and a stored range that cannot reach the
    /// geometry. Measured from the bounding box, not its centre, and for the same reason
    /// the renderer measures it that way: RC1's evening square is ringed by street lamps
    /// with the switch off and a couple of hundred units of range, and against the centre
    /// of a three-thousand-unit town the far corner's lamps read as distant. Against the
    /// box a lamp standing in the scene is at distance zero, always. The sky-bounce lights
    /// share the sun's shape but not its shadows, and stay: they are the blue of the sky
    /// on whatever faces up, which the replacement does not provide.
    /// </remarks>
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

    /// <summary>
    /// The sun for a point in the story, or null where there should not be one.
    /// </summary>
    /// <param name="timeblock">When it is.</param>
    /// <param name="centre">The middle of the scene, which the sun is placed relative to.</param>
    /// <returns>The light, or null at night.</returns>
    /// <remarks>
    /// The elevation follows the hour: low and warm in the morning, high and near white
    /// before noon, sinking and warming again through the afternoon. The azimuth swings
    /// from one side of the map to the other across the day. None of it is astronomy — the
    /// game's world has no agreed compass — but a morning in the village now looks like a
    /// morning, and by evening the shadows have crossed the square.
    /// </remarks>
    public static AuthoredLight? For(Timeblock timeblock, Vector3 centre)
    {
        if (Arc(timeblock) is not { } arc)
        {
            return null;
        }

        (float azimuth, float elevation, Vector3 colour, float intensity) = arc;

        // From the scene toward the sun: east of the map in the morning, overhead-south at
        // midday, west by evening.
        var toward = new Vector3(
            MathF.Cos(elevation) * MathF.Sin(azimuth),
            MathF.Sin(elevation),
            MathF.Cos(elevation) * MathF.Cos(azimuth));

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
            Intensity: intensity,
            Radius: Disc);
    }

    /// <summary>Where the sun stands at each of the game's hours.</summary>
    /// <returns>Azimuth and elevation in radians, colour, and intensity — or null at night.</returns>
    private static (float, float, Vector3, float)? Arc(Timeblock timeblock)
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
        float elevation = MathF.Sin(day * MathF.PI) * (62f * MathF.PI / 180f);
        float azimuth = float.Lerp(80f, 280f, day) * MathF.PI / 180f;

        // Warmer the lower it stands.
        float warm = 1f - (elevation / (MathF.PI / 2f));
        var colour = new Vector3(1f, 0.97f - (0.12f * warm), 0.92f - (0.28f * warm));

        return (azimuth, elevation, colour, 1.15f);
    }
}
