// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Numerics;
using GK3Reborn.Formats.Scenes;
using GK3Reborn.Rendering.Geometry;

namespace GK3Reborn.Game;

/// <summary>
/// Lights the room by the things in it that glow.
/// </summary>
/// <remarks>
/// <para>
/// <b>A self-lit surface lights nothing.</b> The flag means "draw this at full brightness
/// and skip shading" and no more, so GK3's lamp shades, lit bulbs, stained glass and the
/// painted views through its windows have always been bright objects standing in rooms
/// they did not light. This is the gather that fixes that: every glowing surface the room
/// has is a candidate light source, and the ones nobody has already lit get one.
/// </para>
/// <para>
/// <b>Gathering into lights rather than tracing at the hit.</b> The alternative — a ray
/// that returns the emission of whatever it struck — needs the acceleration structure to
/// carry per-triangle materials, and this one is built one instance per <em>model</em>
/// with no material data at all. Turning the emitters into lights instead gets the same
/// picture through machinery the renderer already has: they are shadowed by the same rays,
/// gathered by the same grid, and cost the same as any other lamp. What it cannot do is
/// bounce a second time, which for a 1999 adventure game is not the missing part.
/// </para>
/// <para>
/// <b>The rule is the one the fires already use.</b> A lamp the artists put a light inside
/// needs nothing added, and most of them did: doubling every practical in the game would
/// blow out every room that has one. So an emitter with a light already standing in it is
/// left alone, and only the ones nobody lit are given one. See <see cref="FlameLighting"/>,
/// which is the same shape for the same reason.
/// </para>
/// </remarks>
public static class EmissiveLighting
{
    /// <summary>
    /// How near a light has to be to count as already lighting an emitter.
    /// </summary>
    /// <remarks>
    /// Larger than the flames' thirteen, and it has to be: a fire is a card a few units
    /// across with its light in the middle of it, while an emitter here is a whole fitting
    /// — a chandelier, a bank of stained glass — whose centre may be a good way from the
    /// bulb the artists put inside it. Scaled by the emitter's own size for that reason,
    /// with this as the floor.
    /// </remarks>
    public const float Reach = 25f;

    /// <summary>How far an emitter's own size widens that.</summary>
    private const float ReachPerRadius = 1.5f;

    /// <summary>How far a synthesized light reaches, as a multiple of the emitter's size.</summary>
    /// <remarks>
    /// A lamp shade is about ten units across and lights a corner of a room; the ratio is
    /// what makes a bank of windows light more of one than a bulb does, which is the whole
    /// point of measuring the surface rather than counting it.
    /// </remarks>
    private const float ReachPerSize = 9f;

    /// <summary>The least a synthesized light reaches.</summary>
    private const float LeastReach = 40f;

    /// <summary>And the most, so a wall of glass does not light the next room.</summary>
    private const float MostReach = 400f;

    /// <summary>
    /// How bright a synthesized light is.
    /// </summary>
    /// <remarks>
    /// Under the practicals the artists placed, which run from 0.5 to 3. These are the
    /// lights nobody thought were needed, and a room where they arrive brighter than the
    /// lamps somebody did place is a room this has taken over rather than filled in.
    /// </remarks>
    private const float Intensity = 0.55f;

    /// <summary>
    /// Adds a light for every glowing thing in the room that has none.
    /// </summary>
    /// <param name="rig">The room's lights, as the artists placed them.</param>
    /// <param name="emitters">What glows; see <see cref="EmissiveSurface"/>.</param>
    /// <param name="added">How many lights this put in.</param>
    /// <returns>The rig, with the synthesized lights after it.</returns>
    public static IReadOnlyList<AuthoredLight> Rig(
        IReadOnlyList<AuthoredLight> rig,
        IReadOnlyList<EmissiveSurface> emitters,
        out int added)
    {
        ArgumentNullException.ThrowIfNull(rig);
        ArgumentNullException.ThrowIfNull(emitters);

        added = 0;

        if (emitters.Count == 0)
        {
            return rig;
        }

        List<AuthoredLight> result = [.. rig];

        foreach (EmissiveSurface emitter in emitters)
        {
            if (Lit(rig, emitter))
            {
                continue;
            }

            result.Add(Synthesize(emitter));
            added++;
        }

        return result;
    }

    /// <summary>Whether the artists already put a light in this thing.</summary>
    /// <remarks>
    /// Measured against the rig the room was authored with rather than against what this
    /// has added, so two emitters close together each get their own — a pair of wall
    /// sconces is two lights, not one and a shadow.
    /// </remarks>
    private static bool Lit(IReadOnlyList<AuthoredLight> rig, EmissiveSurface emitter)
    {
        float reach = Reach + (emitter.Radius * ReachPerRadius);

        foreach (AuthoredLight light in rig)
        {
            if (Vector3.DistanceSquared(light.Position, emitter.Centre) <= reach * reach)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The light a glowing thing nobody lit gets.</summary>
    private static AuthoredLight Synthesize(EmissiveSurface emitter)
    {
        float reach = Math.Clamp(
            emitter.Radius * ReachPerSize, LeastReach, MostReach);

        // The emission normalised to a colour, with its strength folded into the intensity.
        // The library writes emissive as a colour whose magnitude is how bright the picture
        // is, and a light wants those two apart: a dim yellow shade and a bright one are the
        // same yellow.
        float strength = MathF.Max(
            MathF.Max(emitter.Emission.X, emitter.Emission.Y), emitter.Emission.Z);

        Vector3 colour = strength > 0.0001f ? emitter.Emission / strength : Vector3.One;

        return new AuthoredLight(
            "emissive:" + emitter.Owner,
            AuthoredLightKind.Point,
            emitter.Centre,
            -Vector3.UnitY,
            colour,

            // Cone angles a point light has no use for, and a stored range that is used:
            // the falloff has to start somewhere inside the reach or the light ends at a
            // hard circle on the floor. See GpuLight.From.
            0f,
            0f,
            reach * 0.15f,
            reach,
            UsesAttenuation: true,

            // <b>Not shadowed.</b> The ray budget is eight shadowed lights in a whole room
            // and it belongs to the lamps that shape it; a glow filling in behind them that
            // spent it would make the room darker overall, which is the opposite of the
            // point. The emitter is also its own occluder — the shade the light is inside —
            // and tracing that seals it in, which is the same trap SurfaceFinish.Occludes
            // exists to avoid.
            CastsShadows: false,
            Intensity * Math.Clamp(strength, 0f, 2f),

            // The surface's own size, which is what soft shadows would jitter across and
            // what keeps a wall of glass from reading as a point.
            MathF.Max(emitter.Radius, 1f));
    }
}
