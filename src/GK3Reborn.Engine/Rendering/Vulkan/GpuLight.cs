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

    /// <summary>How many lights the shader can hold.</summary>
    /// <remarks>
    /// Sized to the corpus rather than to a round number: the median lit scene declares
    /// six lights and only a handful exceed sixty-four, so this covers all but a few
    /// scenes outright without needing per-object culling yet. <c>TE2B</c>, with 148, is
    /// the case that will eventually force it.
    /// </remarks>
    public const int Capacity = 64;

    /// <summary>Converts an authored light.</summary>
    /// <param name="light">The light as the scene asset declares it.</param>
    /// <returns>Its packed form.</returns>
    public static GpuLight From(AuthoredLight light)
    {
        ArgumentNullException.ThrowIfNull(light);

        float end = RangeOf(light);
        float start = light.UsesAttenuation ? MathF.Min(light.AttenuationStart, end) : end;

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
            new Vector4(MathF.Cos(hot), MathF.Cos(falloff), spot ? 1f : 0f, MathF.Max(light.Radius, 0.01f)));
    }

    /// <summary>How far a light actually reaches.</summary>
    /// <param name="light">The light as the scene asset declares it.</param>
    /// <returns>The distance beyond which it contributes nothing.</returns>
    /// <remarks>
    /// A light that declares no attenuation has none: 3ds Max's far attenuation was
    /// switched off and the bake let it reach the whole map. The stored range is still
    /// there in the file and is meaningless when the switch is off — R25's key light for
    /// the afternoon is the sun, fifty thousand units away with a range of two hundred, so
    /// honouring that range deleted the daylight from every room with a window in it.
    /// </remarks>
    public static float RangeOf(AuthoredLight light)
    {
        ArgumentNullException.ThrowIfNull(light);

        if (!light.UsesAttenuation)
        {
            return Unlimited;
        }

        return light.AttenuationEnd > 0 ? light.AttenuationEnd : 500f;
    }

    /// <summary>Chooses which lights to upload when a scene declares more than fit.</summary>
    /// <param name="lights">Every light the scene declares.</param>
    /// <returns>At most <see cref="Capacity"/> of them.</returns>
    /// <remarks>
    /// <para>
    /// Brightest and longest-reaching first, which is the least bad order without knowing
    /// where the lit object is. Proper per-object culling replaces this once anything
    /// moves through a scene large enough to need it.
    /// </para>
    /// <para>
    /// Sorted even when everything fits, because the shader shadows the first few lights
    /// of the array rather than all of them. Leaving a short rig in file order would mean
    /// the shadowed ones were whichever the artist happened to place first.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<AuthoredLight> Choose(IReadOnlyList<AuthoredLight> lights)
    {
        ArgumentNullException.ThrowIfNull(lights);

        return lights
            .OrderByDescending(l => l.Intensity * MathF.Max(1f, RangeOf(l)))
            .Take(Capacity)
            .ToList();
    }
}
