using System.Numerics;

namespace GK3Reborn.Rendering;

/// <summary>How much ray tracing to do.</summary>
public enum RayTracingQuality
{
    /// <summary>
    /// No rays. Scene geometry is lit by the original baked lightmaps, exactly as the
    /// 1999 renderer did, and props by the authored rig with no shadows.
    /// </summary>
    None,

    /// <summary>Ray-traced shadows from the strongest lights.</summary>
    Low,

    /// <summary>Shadows from more lights, plus ray-traced ambient occlusion.</summary>
    Medium,

    /// <summary>
    /// Shadows from most of the rig, softened across each light's own emitter size, and
    /// the most occlusion rays. The bake is reduced to a faint bounce term.
    /// </summary>
    High,
}

/// <summary>
/// What a quality level actually costs, in rays.
/// </summary>
/// <param name="Quality">The level these settings came from.</param>
/// <param name="ShadowLights">
/// How many of the rig's lights get a shadow ray. Lights beyond this still contribute,
/// unshadowed, so turning the setting down dims a scene rather than changing which lights
/// exist in it.
/// </param>
/// <param name="AmbientOcclusionRays">Hemisphere rays per pixel for occlusion; zero disables it.</param>
/// <param name="ShadowSamples">
/// Rays per shadowed light. One gives a hard edge; more sample across the light's own
/// emitter radius for a soft one.
/// </param>
/// <param name="LightmapIndirect">
/// How much the baked lightmap contributes as an indirect term, from zero to one.
/// </param>
/// <param name="AmbientOcclusionRadius">How far occlusion rays reach, in scene units.</param>
/// <param name="OcclusionSamples">
/// How many rays a pixel spends on each of the two occlusion signals every frame.
/// </param>
public readonly record struct RayTracingSettings(
    RayTracingQuality Quality,
    int ShadowLights,
    int AmbientOcclusionRays,
    int ShadowSamples,
    float LightmapIndirect,
    float AmbientOcclusionRadius,
    int OcclusionSamples = 8)
{
    /// <summary>The light a surface receives from everywhere at once.</summary>
    public Vector3 Ambient => UsesBake
        ? new Vector3(0.06f, 0.08f, 0.06f)
        : new Vector3(0.15f, 0.16f, 0.17f);

    /// <summary>How much of the traced ambient occlusion to believe, from zero to one.</summary>
    public float OcclusionStrength => UsesBake ? 0.55f : 0.85f;

    /// <summary>How much the baked lightmaps shape the ambient floor, from zero to one.</summary>
    public float LightmapHint => UsesBake ? 0f : 1f;

    /// <summary>Whether any rays are traced at all.</summary>
    public bool TracesRays => ShadowLights > 0 || AmbientOcclusionRays > 0;

    /// <summary>Whether the baked lightmaps still light scene geometry outright.</summary>
    public bool BakedOnly => Quality == RayTracingQuality.None;

    /// <summary>Whether the bake contributes anything at all.</summary>
    public bool UsesBake => LightmapIndirect > 0f;

    /// <summary>
    /// How far an occlusion ray reaches, in scene units.
    /// </summary>
    private const float OcclusionRadius = 45f;

    /// <summary>The settings for a quality level.</summary>
    /// <param name="quality">The level.</param>
    /// <returns>Its ray budget.</returns>
    public static RayTracingSettings For(RayTracingQuality quality) => quality switch
    {
        // The last number is what every quality level needs most, and the one that used
        // to be missing from Low: how many rays a pixel spends on occlusion each frame.
        // It had been taken from the ambient-occlusion budget, which is nought at Low
        // because Low has no ambient occlusion — so Low was estimating every shadow from
        // a single ray and looked far worse than the level above it.
        RayTracingQuality.Low => new(quality, 8, 0, 1, 0.6f, 0f, 4),
        RayTracingQuality.Medium => new(quality, 16, 4, 1, 0f, OcclusionRadius, 6),
        RayTracingQuality.High => new(quality, 32, 8, 2, 0f, OcclusionRadius, 8),
        _ => new(RayTracingQuality.None, 0, 0, 1, 1f, 0f, 0),
    };

    /// <summary>Parses a quality level from a command line or configuration value.</summary>
    /// <param name="text">The value, such as <c>medium</c> or <c>med</c>.</param>
    /// <returns>The level, or null if it is not one.</returns>
    public static RayTracingQuality? Parse(string? text) => text?.ToUpperInvariant() switch
    {
        "NONE" or "OFF" => RayTracingQuality.None,
        "LOW" => RayTracingQuality.Low,
        "MED" or "MEDIUM" => RayTracingQuality.Medium,
        "HIGH" => RayTracingQuality.High,
        _ => null,
    };
}
