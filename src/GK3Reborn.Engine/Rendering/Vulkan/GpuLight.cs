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

        // A 1999 rig with attenuation switched off reached the whole map, which was
        // affordable when the result was baked once and not when it is evaluated every
        // frame. The stored range is used as a soft limit regardless, widened where the
        // light declares no attenuation; see ADR 0006 on re-lighting for modern range.
        float end = light.AttenuationEnd > 0 ? light.AttenuationEnd : 500f;
        float start = light.UsesAttenuation ? light.AttenuationStart : end;

        if (!light.UsesAttenuation)
        {
            end *= 2f;
        }

        bool spot = light.Kind == AuthoredLightKind.Spot;

        // Negative cone angles appear on point lights, where they mean nothing; clamping
        // keeps the cosines ordered so the falloff between them stays monotonic.
        float hot = Math.Clamp(light.HotSpot, 0f, MathF.PI);
        float falloff = Math.Clamp(MathF.Max(light.Falloff, hot + 0.01f), 0f, MathF.PI);

        return new GpuLight(
            new Vector4(light.Position, start),
            new Vector4(light.Color, light.Intensity),
            new Vector4(light.Direction, MathF.Max(end, start + 1f)),
            new Vector4(MathF.Cos(hot), MathF.Cos(falloff), spot ? 1f : 0f, 0f));
    }

    /// <summary>Chooses which lights to upload when a scene declares more than fit.</summary>
    /// <param name="lights">Every light the scene declares.</param>
    /// <returns>At most <see cref="Capacity"/> of them.</returns>
    /// <remarks>
    /// Brightest and longest-reaching first, which is the least bad choice without knowing
    /// where the lit object is. Proper per-object culling replaces this once anything
    /// moves through a scene large enough to need it.
    /// </remarks>
    public static IReadOnlyList<AuthoredLight> Choose(IReadOnlyList<AuthoredLight> lights)
    {
        ArgumentNullException.ThrowIfNull(lights);

        if (lights.Count <= Capacity)
        {
            return lights;
        }

        return lights
            .OrderByDescending(l => l.Intensity * MathF.Max(1f, l.AttenuationEnd))
            .Take(Capacity)
            .ToList();
    }
}
