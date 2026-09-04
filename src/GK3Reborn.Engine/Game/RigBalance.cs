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
/// <remarks>
/// <para>
/// <b>These rigs were never meant to be run all at once.</b> They are baking rigs: the
/// artists lit each room for an offline renderer that had no global illumination, and the
/// way you did that in 1999 was to place the lamps and then layer <em>fills</em>,
/// <em>ambients</em> and <em>bounce</em> lights by hand until the corners stopped being
/// black. CS3's attic is fifty-eight lights, of which a good dozen are named
/// <c>cs3_ambient</c>, <c>back_room_fill</c>, <c>front_room_fill</c>,
/// <c>cs3_turret_window_ambient01</c> and so on, most reaching three or four hundred units
/// at full intensity.
/// </para>
/// <para>
/// <b>Evaluated live, that is the same light twice.</b> The tracer computes ambient
/// occlusion and carries an ambient floor of its own, which is what those hand-placed
/// fills were standing in for; running both gives a room brighter than the bake it
/// replaced and, worse, <em>flatter</em> — every surface lit from every direction at once,
/// so a bulb and the daylight through a window read as the same wash. Reported exactly
/// that way: "the whole scene is exceptionally lit, and no real distinction between light
/// from a glowing bulb versus outdoor light".
/// </para>
/// <para>
/// <b>The names are the evidence.</b> GK3's rigs use one vocabulary across all 229 assets
/// and 3,325 lights: <c>key</c>, <c>spot</c>, <c>omni</c> and <c>special</c> are sources —
/// a lamp, a window, a shaft — while <c>fill</c>, <c>ambient</c>, <c>bounce</c> and
/// <c>warmer</c> are the baking scaffolding. 429 lights carry one of the second set, about
/// one in eight. Nothing else in the data separates them, and the artists were consistent
/// enough that nothing else needs to.
/// </para>
/// <para>
/// <b>Only while tracing, and in proportion to it.</b> With no rays the bake <em>is</em>
/// the room's lighting and the rig reaches only the models standing in it — there is
/// nothing being counted twice, so nothing is touched. The more of the picture the rays
/// are paying for, the further the bake recedes and the more of the fills' job the
/// occlusion has taken over.
/// </para>
/// </remarks>
public static class RigBalance
{
    /// <summary>
    /// The words the artists used for a light that stands in for bounced light.
    /// </summary>
    /// <remarks>
    /// Matched anywhere in the name, because the corpus writes them every way round:
    /// <c>back_room_fill</c>, <c>cs3_ambient</c>, <c>sky_bounce01</c>,
    /// <c>cs3_turret_window_floor_warmer04</c>.
    /// </remarks>
    private static readonly string[] Indirect =
        ["fill", "ambient", "bounce", "warmer"];

    /// <summary>
    /// Whether a light is scaffolding rather than a source.
    /// </summary>
    /// <param name="light">The light as the scene asset declares it.</param>
    /// <returns>True for a fill, an ambient, a bounce or a warmer.</returns>
    /// <remarks>
    /// <b>A name is weak evidence and it is the only evidence there is.</b> Nothing in the
    /// file marks a light's purpose — position, colour, range and intensity are the same
    /// fields for a bulb and for a fill somebody put behind the camera. The alternative is
    /// to guess from the geometry, which would be a classifier over the thing the artists
    /// already wrote down.
    /// </remarks>
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
    /// <remarks>
    /// One with no rays — the rig is not lighting the room then, so there is nothing to
    /// correct. At High the bake is gone entirely and the occlusion has taken over the
    /// fills' whole job, so what is left is a sixth: enough that a corner the tracer
    /// over-darkens does not go black, and not enough to flood a room that is meant to be
    /// dim. Grace remarks that CS3's attic is too dark to read in, and it has to be.
    /// </remarks>
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
    /// <remarks>
    /// <para>
    /// <b>Nothing, when the player has asked for only real sources.</b> A sixth of a fill
    /// is a deliberate compromise — enough that a corner the tracer over-darkens does not
    /// go black — and somebody who has asked for the light in a room to come from the
    /// windows and the lamps has said they would rather have the corner. What is left is
    /// the sun, the sky through a window, a lamp, a fire and the tracer's own ambient floor
    /// shaped by occlusion.
    /// </para>
    /// <para>
    /// Still nothing at all with no rays, whatever the player asked for: the bake
    /// <em>is</em> the room's lighting there and the rig only reaches the people standing in
    /// it, so switching off the fills would darken the characters and leave the room they
    /// stand in exactly as bright. That is not realism, it is a bug with a switch on it.
    /// </para>
    /// </remarks>
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
    /// <remarks>
    /// The dimmed lights are kept in the list at nought intensity rather than removed. A
    /// light is addressed by its position in the rig — by the shadow budget, by anything
    /// that names one — and renumbering the room's lights because a preference changed is a
    /// way to make a scene look different depending on what the player did last.
    /// </remarks>
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
