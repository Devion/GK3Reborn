using System.Numerics;
using System.Runtime.InteropServices;
using GK3Reborn.Formats.Scenes;

namespace GK3Reborn.Rendering.Geometry;

/// <summary>
/// One of the artists' lights, in the form the shader reads.
/// </summary>
/// <param name="PositionAndStart">Position in world space, and where falloff begins.</param>
/// <param name="ColorAndIntensity">Colour, and the multiplier on it.</param>
/// <param name="DirectionAndEnd">Direction it points, and where falloff reaches zero.</param>
/// <param name="Cone">
/// Cosine of the fully lit half-angle, cosine of the outer half-angle, whether it is a
/// spot, and the emitter's radius.
/// </param>
/// <param name="Flicker">
/// How far this light's brightness swings, what it settles at, how fast, and a number of
/// its own that spreads it against its neighbours. <c>(0, 1, 0, 0)</c> for a light that
/// stands still, which multiplies it by exactly one for ever. See
/// <see cref="Formats.Scenes.FlameFlicker"/>.
/// </param>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct GpuLight(
    Vector4 PositionAndStart,
    Vector4 ColorAndIntensity,
    Vector4 DirectionAndEnd,
    Vector4 Cone,
    Vector4 Flicker)
{
    /// <summary>
    /// The range given to a light that declares no attenuation.
    /// </summary>
    public const float Unlimited = 1e6f;

    /// <summary>How many lights a scene may upload.</summary>
    public const int Capacity = 1024;

    /// <summary>Converts an authored light.</summary>
    /// <param name="light">The light as the scene asset declares it.</param>
    /// <param name="scene">What the geometry occupies; default decides nothing.</param>
    /// <param name="sunGain">
    /// How much brighter a distant key is than the artists made it. One everywhere except
    /// on an HDR display, where a sun that comes out at the same brightness as the wall it
    /// lights is the thing that makes an exterior look flat. See
    /// <see cref="OutputPlan.SunNits"/>.
    /// </param>
    /// <returns>Its packed form.</returns>
    public static GpuLight From(AuthoredLight light, SceneExtent scene = default, float sunGain = 1f)
    {
        ArgumentNullException.ThrowIfNull(light);

        float end = RangeOf(light);
        bool directional = IsDistantKey(light, scene);

        float intensity = light.Intensity *
            (directional && float.IsFinite(sunGain) ? MathF.Max(sunGain, 0f) : 1f);

        // The near range too, whatever the switch says. A light whose start equals its end
        // has no ramp at all — it is full brightness to a hard edge and then nothing — and
        // that edge is a visible circle on a floor.
        //
        // Except where there is no range to ramp across: a light that states no reach has
        // no falloff either, and spreading a ramp over the unlimited range would invent a
        // falloff nobody asked for and dim a sun by a tenth for being far away.
        float start = end >= Unlimited ? end : MathF.Min(light.AttenuationStart, end);

        bool spot = light.Kind == AuthoredLightKind.Spot;

        // Negative cone angles appear on point lights, where they mean nothing; clamping
        // keeps the cosines ordered so the falloff between them stays monotonic.
        float hot = Math.Clamp(light.HotSpot, 0f, MathF.PI);
        float falloff = Math.Clamp(MathF.Max(light.Falloff, hot + 0.01f), 0f, MathF.PI);

        return new GpuLight(
            new Vector4(light.Position, start),
            new Vector4(light.Color, intensity),
            new Vector4(light.Direction, MathF.Max(end, start + 1f)),
            // The emitter radius rides in the spare component: soft shadows jitter their
            // rays across it, so a two-unit bulb and a twenty-unit window behave
            // differently without needing another buffer.
            //
            // Two flags in one number, the same way DrawConstants packs its two: 1 for a
            // spot, 2 for a light whose attenuation switch was off when the scene was
            // baked. The second is what lets a character stand in the sun. See RangeOf
            // for why the range is honoured anyway, and why that answer only works for
            // surfaces that have a lightmap to fall back on.
            new Vector4(
                MathF.Cos(hot),
                MathF.Cos(falloff),
                (spot ? 1f : 0f) + (directional ? 2f : 0f),
                MathF.Max(light.Radius, 0.01f)),

            // A fire, or a light that stands still. The steady form is (0, 1, 0, 0), whose
            // multiplier is one at every instant — so a rig with no fire in it is shaded
            // by arithmetic that cannot change what it used to draw.
            light.Flicker is { } flicker
                ? new Vector4(flicker.Swing, flicker.Bias, flicker.Rate, flicker.Seed)
                : Steady);
    }

    /// <summary>The flicker of a light that does not flicker.</summary>
    public static Vector4 Steady => new(0f, 1f, 0f, 0f);

    /// <summary>How far a light actually reaches.</summary>
    /// <param name="light">The light as the scene asset declares it.</param>
    /// <returns>The distance beyond which it contributes nothing.</returns>
    public static float RangeOf(AuthoredLight light)
    {
        ArgumentNullException.ThrowIfNull(light);

        if (light.AttenuationEnd > 0)
        {
            return light.AttenuationEnd;
        }

        // Attenuated and yet no range: the switch is on and the number is missing, so
        // something has to be chosen. Unattenuated and no range is a light that genuinely
        // says nothing, and gets nothing imposed on it.
        return light.UsesAttenuation ? 500f : Unlimited;
    }

    /// <summary>Puts a scene's lights in the order the passes want them.</summary>
    /// <param name="lights">Every light the scene declares.</param>
    /// <param name="scene">What the geometry occupies; default decides nothing.</param>
    /// <returns>At most <see cref="Capacity"/> of them, brightest first.</returns>
    public static IReadOnlyList<AuthoredLight> Choose(
        IReadOnlyList<AuthoredLight> lights, SceneExtent scene = default)
    {
        ArgumentNullException.ThrowIfNull(lights);

        // A distant key sorts by the reach it actually has, not by the two hundred units
        // left in the file. Sorted low it would be the first light dropped from a crowded
        // rig and the last to be given a shadow ray — and it is the sun.
        return lights
            .OrderByDescending(l => l.Intensity *
                (IsDistantKey(l, scene) ? Unlimited : MathF.Max(1f, RangeOf(l))))
            .Take(Capacity)
            .ToList();
    }

    /// <summary>
    /// Whether this light is a distant source whose stored range is leftover data rather
    /// than an authored falloff.
    /// </summary>
    /// <param name="light">The light as the scene asset declares it.</param>
    /// <param name="scene">What the scene occupies, or default to decide nothing.</param>
    /// <returns>True to shade it with no distance falloff at all.</returns>
    public static bool IsDistantKey(AuthoredLight light, SceneExtent scene)
    {
        ArgumentNullException.ThrowIfNull(light);

        if (light.UsesAttenuation || !scene.IsKnown)
        {
            return false;
        }

        return scene.DistanceTo(light.Position) > RangeOf(light);
    }

    /// <summary>Describes packed lights to the grid builder.</summary>
    /// <param name="lights">The rig, as the shader will read it.</param>
    /// <returns>What the grid needs of each: where it is and how far it reaches.</returns>
    public static GridLight[] Describe(IReadOnlyList<GpuLight> lights)
    {
        ArgumentNullException.ThrowIfNull(lights);

        var described = new GridLight[lights.Count];

        for (int i = 0; i < lights.Count; i++)
        {
            GpuLight light = lights[i];

            bool everywhere = light.Cone.Z >= 1.5f;
            float reach = light.DirectionAndEnd.W;

            described[i] = new GridLight(
                new Vector3(light.PositionAndStart.X, light.PositionAndStart.Y, light.PositionAndStart.Z),
                reach,
                everywhere,
                light.ColorAndIntensity.W * MathF.Max(1f, reach));
        }

        return described;
    }
}
