using System.Numerics;
using System.Runtime.InteropServices;
using GK3Reborn.Formats.Scenes;

namespace GK3Reborn.Rendering.Vulkan;

/// <summary>
/// One of the artists' lights, in the form the shader reads.
/// </summary>
/// <param name="PositionAndStart">Position in world space, and where falloff begins.</param>
/// <param name="ColorAndIntensity">Colour, and the multiplier on it.</param>
/// <param name="DirectionAndEnd">Direction it points, and where falloff reaches zero.</param>
/// <param name="Cone">
/// Cosine of the fully lit half-angle, cosine of the outer half-angle, whether it is a
/// spot, and an unused fourth component.
/// </param>
/// <remarks>
/// Packed as four <c>float4</c>s so the layout is the same on both sides without any
/// padding rules to get wrong. Cone angles are converted to cosines here rather than in
/// the shader, because they are constant for the life of the scene and the shader would
/// otherwise recompute them per pixel per light.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct GpuLight(
    Vector4 PositionAndStart,
    Vector4 ColorAndIntensity,
    Vector4 DirectionAndEnd,
    Vector4 Cone)
{
    /// <summary>
    /// The range given to a light that declares no attenuation.
    /// </summary>
    /// <remarks>
    /// Finite so that the falloff arithmetic stays in range, and far enough out that
    /// nothing in the corpus reaches it: the most distant light is a sun some fifty
    /// thousand units from the room it lights.
    /// </remarks>
    public const float Unlimited = 1e6f;

    /// <summary>How many lights a scene may upload.</summary>
    /// <remarks>
    /// <para>
    /// A thousand, which is an allocation rather than a limit anything reaches: the whole
    /// corpus's busiest scene declares 148, and the three rooms of the hotel together want
    /// 195. It was sixty-four, because the rig lived in a uniform block and the standard
    /// guarantees only 16 KB of one; it is now a storage buffer, and what used to make the
    /// number matter — a shading loop that ran over every light in the room — is gone.
    /// A fragment loops the cell it stands in. See <see cref="SceneLightGrid"/>.
    /// </para>
    /// <para>
    /// Sixty-four kilobytes at this size, uploaded once a room.
    /// </para>
    /// </remarks>
    public const int Capacity = 1024;

    /// <summary>Converts an authored light.</summary>
    /// <param name="light">The light as the scene asset declares it.</param>
    /// <param name="scene">What the geometry occupies; default decides nothing.</param>
    /// <returns>Its packed form.</returns>
    public static GpuLight From(AuthoredLight light, SceneExtent scene = default)
    {
        ArgumentNullException.ThrowIfNull(light);

        float end = RangeOf(light);
        bool directional = IsDistantKey(light, scene);

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
            new Vector4(light.Color, light.Intensity),
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
                MathF.Max(light.Radius, 0.01f)));
    }

    /// <summary>How far a light actually reaches.</summary>
    /// <param name="light">The light as the scene asset declares it.</param>
    /// <returns>The distance beyond which it contributes nothing.</returns>
    /// <remarks>
    /// <para>
    /// <b>A stored range is honoured whether or not the switch is on.</b> 3ds Max's far
    /// attenuation being off means the light had no decay while the scene was being baked,
    /// and reproducing that at runtime is faithful and unusable: a light with no falloff
    /// lights every surface it can see equally, so a rig's fill lights become a flat wash
    /// with no source anywhere in the room. The lobby is the case that showed it — 82% of
    /// the light arriving at the middle of its floor came from lights with the switch off,
    /// one of them 842 units outside the room — and it reads exactly as it is: a floor lit
    /// from nowhere.
    /// </para>
    /// <para>
    /// The ranges are in the file and they are the artists' own. Every one of the lobby's
    /// fourteen switched-off lights carries a full near and far pair — 10 to 77, 33 to 66,
    /// 164 to 221 — set by hand and then disabled, which is a normal way to work in Max
    /// and leaves the intent behind in the file.
    /// </para>
    /// <para>
    /// This used to return an unlimited range for them, because honouring the range
    /// switched off R25's afternoon sun: fifty thousand units away with a range of two
    /// hundred. That no longer costs anything, and the reason is the compositing pass —
    /// the bake carries the daylight now and the rig only has to explain what it can, so
    /// R25 measures 0.237 either way and the brightest tenth of its window view is
    /// identical to the pixel. Across ten scenes six do not change at all; the lobby falls
    /// 20%, the dining room 42%, and both stop being washed and start looking lamp-lit.
    /// </para>
    /// <para>
    /// A light with no stored range at all still has none. There is nothing to honour, and
    /// unlimited is the only honest reading of a light that says nothing about its reach.
    /// </para>
    /// </remarks>
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
    /// <remarks>
    /// <para>
    /// Brightest and longest-reaching first. This used to be a *choice* — the rig held
    /// sixty-four and a scene declaring more had the rest dropped — and it is now only an
    /// order, because the buffer holds a thousand and the shading loop walks a grid cell
    /// rather than the array.
    /// </para>
    /// <para>
    /// The order still matters and is not decoration. The denoiser spends its ray budget on
    /// the front of the rig, and each cell of the light grid keeps its own heaviest first
    /// for the same reason. Leaving a rig in file order would mean the shadowed lights were
    /// whichever the artist happened to place first.
    /// </para>
    /// </remarks>
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
    /// <remarks>
    /// <para>
    /// Two conditions, and the second is what makes the first safe. The attenuation switch
    /// is off, so the artists said this light does not decay; and its stored far range
    /// cannot reach the scene's own bounding box, so honouring that range does not dim the
    /// light — it deletes it, everywhere, for every surface. A light that reaches nothing
    /// at all is not what anybody meant by placing it and setting CastShadows.
    /// </para>
    /// <para>
    /// The corpus separates cleanly on this, which is why there is no threshold to tune.
    /// Every one of the 111 <c>scenekey</c> lights and all 125 <c>sky_bounce</c> lights
    /// qualify: switch off, median range 200, median distance 53,885 and 27,995. None of
    /// the 169 <c>ground_bounce</c> lights do — every one has its switch on and a range in
    /// the thousands that genuinely reaches. Neither do the lobby's fourteen switched-off
    /// fill lights, which sit in the room they light with ranges of 10 to 77: those keep
    /// the falloff the artists gave them, and the flat wash they used to cause stays fixed.
    /// </para>
    /// <para>
    /// Rays are left pointing at the light's actual position rather than made parallel. At
    /// fifty thousand units the direction varies by about three degrees across a scene,
    /// which is below what a shadow edge shows, and tracing towards a position is what
    /// every other light already does.
    /// </para>
    /// </remarks>
    public static bool IsDistantKey(AuthoredLight light, SceneExtent scene)
    {
        ArgumentNullException.ThrowIfNull(light);

        if (light.UsesAttenuation || !scene.IsKnown)
        {
            return false;
        }

        return scene.DistanceTo(light.Position) > RangeOf(light);
    }
}
