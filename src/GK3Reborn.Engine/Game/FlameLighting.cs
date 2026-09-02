// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Numerics;
using GK3Reborn.Formats.Scenes;

namespace GK3Reborn.Game;

/// <summary>
/// Makes the light in a room with a fire in it move.
/// </summary>
/// <remarks>
/// <para>
/// The artists lit these rooms twice: a flame card that animates, and, usually, a light
/// standing inside it. In CS5 the eight hanging lanterns each carry
/// <c>cs5_lantern_light01</c> a third of a unit from the flame; in the dining room each
/// chafing dish has a <c>chafing_dish_special</c> in the sterno; the chapel's fire has
/// <c>firelight_omni</c> eight units away and the tomb's fourteen candles have a
/// <c>candleside_glow_special</c> apiece. Every one of those lights is perfectly steady,
/// which is what makes a room lit by fire read as a room lit by a bulb with a picture of a
/// fire in it.
/// </para>
/// <para>
/// So this pairs each light with the fire it stands in and marks it to waver. <b>The
/// pairing is by distance and the threshold is not a guess.</b> Over every scene asset of
/// the nine rooms that have a fire in them, 161 lights stand within reach of one and the
/// furthest of those is 10.3 units away — <c>candle_omni02</c>, which is a candle's light.
/// The nearest light that is plainly something else is 16.7, <c>omni04</c> in MA1 and in
/// CS6. Thirteen sits between them.
/// </para>
/// <para>
/// <b>One fire in the game has no light of its own.</b> The temple's bowl of fire is lit
/// entirely by the bake, with the nearest rig light 68 units away and lighting a lantern
/// across the room. Every other fire is lit once the right asset is loaded: the bar's
/// fireplace looks unlit under RL2's morning rig and has two lights five units into it
/// under the evening one, which is the only rig its fire is ever placed with.
/// </para>
/// <para>
/// A fire that has none gets a light synthesized here, and it is a light whose <em>mean is
/// zero</em>: it contributes the waver and nothing else, so the room is exactly as bright
/// as it has always been on average and a fire that used to be a still picture now moves
/// the wall beside it.
/// </para>
/// </remarks>
public static class FlameLighting
{
    /// <summary>How near a light has to be to a fire to be that fire's light.</summary>
    /// <remarks>
    /// See the type's remarks: the corpus separates at 10.3 against 16.7, and this sits
    /// between them. Raising it far enough to sweep in a room's fill lighting would make
    /// the whole room pulse rather than the wall beside the fire, which reads as the
    /// lighting being switched.
    /// </remarks>
    public const float Reach = 13f;

    /// <summary>
    /// The colour of a synthesized flame light, as linear RGB.
    /// </summary>
    /// <remarks>
    /// About 1900 K, which is a wood fire. It tints the waver rather than the room, because
    /// a synthesized light averages to nothing — so getting it a little wrong warms and
    /// cools the wall by the fire slightly out of step, and cannot shift a room's colour.
    /// </remarks>
    private static readonly Vector3 Firelight = new(1f, 0.55f, 0.22f);

    /// <summary>How far a synthesized flame light reaches, as a multiple of the flame's height.</summary>
    private const float ReachPerHeight = 14f;

    /// <summary>The narrowest and widest a synthesized flame light may reach.</summary>
    private const float LeastReach = 40f;
    private const float MostReach = 260f;

    /// <summary>
    /// How bright a synthesized flame light is before its waver is applied.
    /// </summary>
    /// <remarks>
    /// Below the practicals the artists placed, which run from 0.5 to 3. It is never seen
    /// at this brightness — the waver is what multiplies it, and the waver averages to
    /// nought — so this is the size of the swing rather than the brightness of a light.
    /// </remarks>
    private const float SynthesizedIntensity = 1.1f;

    /// <summary>Marks the lights that stand in a fire, and lights the fires that have none.</summary>
    /// <param name="rig">The room's lights, as the artists placed them.</param>
    /// <param name="flames">The room's fires; see <see cref="Flames.In"/>.</param>
    /// <returns>
    /// The rig with its flame lights marked, followed by one synthesized light per fire
    /// that had none. The same list back, unchanged, when the room has no fire in it.
    /// </returns>
    public static IReadOnlyList<AuthoredLight> Rig(
        IReadOnlyList<AuthoredLight> rig, IReadOnlyList<Flame> flames)
    {
        ArgumentNullException.ThrowIfNull(rig);
        ArgumentNullException.ThrowIfNull(flames);

        if (flames.Count == 0)
        {
            return rig;
        }

        var lit = new bool[flames.Count];
        List<AuthoredLight> result = new(rig.Count + flames.Count);

        foreach (AuthoredLight light in rig)
        {
            int nearest = Nearest(flames, light.Position, out float distance);

            if (nearest < 0 || distance > Reach)
            {
                result.Add(light);
                continue;
            }

            lit[nearest] = true;

            Flame flame = flames[nearest];

            result.Add(light with
            {
                Flicker = new FlameFlicker(
                    flame.Swing, 1f, flame.Rate, Spread(light.Position)),
            });
        }

        for (int i = 0; i < flames.Count; i++)
        {
            // A fire the artists lit needs nothing added, and one the scene is not drawing
            // is not burning: TE6 keeps its candles hidden until a script lights them, and
            // a light standing in an unlit candle would be a glow with no source.
            if (lit[i] || !flames[i].Visible)
            {
                continue;
            }

            result.Add(Synthesize(flames[i]));
        }

        return result;
    }

    /// <summary>The light a fire the artists left dark gets.</summary>
    private static AuthoredLight Synthesize(Flame flame)
    {
        float reach = Math.Clamp(
            flame.Height * ReachPerHeight, LeastReach, MostReach);

        return new AuthoredLight(
            "flame:" + flame.Model,
            AuthoredLightKind.Point,
            flame.Position,
            -Vector3.UnitY,
            Firelight,

            // Cone angles a point light has no use for, and a stored range that is used:
            // the falloff has to start somewhere inside the reach or the light ends at a
            // hard circle on the floor. See GpuLight.From.
            0f,
            0f,
            reach * 0.15f,
            reach,
            UsesAttenuation: true,
            CastsShadows: false,
            SynthesizedIntensity,

            // The emitter's own size, which is what the soft-shadow sampling jitters
            // across: a bowl of fire a foot across does not cast the shadow of a point.
            MathF.Max(flame.Width * 0.5f, 1f))
        {
            Flicker = new FlameFlicker(
                flame.Swing, 0f, flame.Rate, Spread(flame.Position)),
        };
    }

    /// <summary>Which flame is nearest a point, and how far.</summary>
    private static int Nearest(
        IReadOnlyList<Flame> flames, Vector3 position, out float distance)
    {
        int nearest = -1;
        distance = float.MaxValue;

        for (int i = 0; i < flames.Count; i++)
        {
            float apart = Vector3.Distance(flames[i].Position, position);

            if (apart < distance)
            {
                distance = apart;
                nearest = i;
            }
        }

        return nearest;
    }

    /// <summary>
    /// A number in [0, 1) that is this light's own, from where it stands.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It spreads the flicker rates so that fourteen candles around one tomb do not surge
    /// in unison, which reads as the room's lighting being switched rather than as
    /// fourteen candles. From the position rather than from a counter because it must be
    /// the same on every run and in every backend: two renderers that disagree about which
    /// candle is which cannot be compared.
    /// </para>
    /// <para>
    /// A poor hash by any standard and entirely sufficient — the requirement is that
    /// neighbours differ and that the answer is stable, not that it is uniform.
    /// </para>
    /// </remarks>
    private static float Spread(Vector3 position)
    {
        float mixed =
            (position.X * 12.9898f) + (position.Y * 78.233f) + (position.Z * 37.719f);

        float wave = MathF.Sin(mixed) * 43758.5453f;

        return wave - MathF.Floor(wave);
    }
}
