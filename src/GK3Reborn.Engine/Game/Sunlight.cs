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
/// effectively directional and unattenuated for real, so it reaches the ground. It subtends
/// about half a degree — the real sun's size — so its ray-traced shadows have the real
/// penumbra. The sky-bounce fills stay, interiors are left exactly alone, and so is any
/// exterior at night.
/// </para>
/// <para>
/// <b>It is aimed by the scenekey it replaces.</b> The scenekey was replaced for its reach,
/// not for its aim: its two hundred unit range cannot touch the geometry, and its azimuth
/// and elevation were never the problem. They are the artists' own statement of where the
/// light was coming from when they baked the room and painted the sky over it, and 749 of
/// the corpus's 817 sky-lit pairs ship one — none of them below the horizon. Where a room
/// has a bake for each time of day it has a scenekey for each, so the light still moves
/// through the day; where it has one asset for the whole game the sun stands still, and so
/// does everything else in that room.
/// </para>
/// <para>
/// This used to be an arc computed from the hour alone, which knew nothing about the scene
/// and disagreed with it: measured against the artists' keys across the corpus, the median
/// pair was 42 degrees apart, the worst 107, and 262 of 573 daytime pairs more than 45.
/// The arc is still the answer for the 68 sky-lit pairs that ship no scenekey at all — the
/// case this class was written for — and it still decides, alone, whether there is a sun to
/// place: the evening blocks' art is painted as dusk and a sun over a night sky argues with
/// all of it.
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

    /// <summary>The artists' own sun among a scene's lights, if it ships one.</summary>
    /// <param name="lights">Every light the scene asset declares.</param>
    /// <param name="minimum">One corner of the loaded geometry.</param>
    /// <param name="maximum">The other.</param>
    /// <returns>The scenekey, or null where there is none.</returns>
    /// <remarks>
    /// The brightest, on the rare asset that declares two shadow-casting distant keys — a
    /// sun and a moon over the same room. <see cref="LoadedScene.Lights"/> takes every one
    /// of them out and puts this back, so the one that is kept had better be the sun.
    /// </remarks>
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
    /// <remarks>
    /// Aimed by the scenekey wherever there is one, so the light agrees with the bake it is
    /// replacing and with the sky painted over it. Without one the elevation follows the
    /// hour: low and warm in the morning, high and near white before noon, sinking and
    /// warming again through the afternoon, with the azimuth swinging from one side of the
    /// map to the other. That is not astronomy — a scene with no key light has no compass
    /// either — but it is a morning that looks like a morning.
    /// </remarks>
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
    /// <remarks>
    /// Above the scenekey's own, which is typically 1.0 at a colour around
    /// (0.53, 0.48, 0.40). The room it lights has no bake at Medium and High, and this is
    /// most of what stands in for one.
    /// </remarks>
    private const float Strength = 1.15f;

    /// <summary>
    /// Which way the artists' scenekey says the light comes from, or null to fall back to
    /// the hour.
    /// </summary>
    /// <param name="authored">The scenekey, or null.</param>
    /// <param name="centre">The middle of the room it lights.</param>
    /// <returns>A unit vector from the scene toward the sun, or null.</returns>
    /// <remarks>
    /// Refused below the horizon, and refused for a key standing on top of the room's own
    /// centre. Neither happens in the corpus — all 749 scenekeys stand between 24 and 62
    /// degrees up, the lowest of them over an evening — and a rig is a text file that
    /// anybody may edit, so a light underground has to mean "no answer" rather than a scene
    /// lit from below.
    /// </remarks>
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
