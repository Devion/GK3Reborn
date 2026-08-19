namespace GK3Reborn.Rendering;

/// <summary>How much ray tracing to do.</summary>
/// <remarks>
/// The ladder the settings screen will expose. Each step is a superset of the one below
/// it, so a scene never loses a lighting effect by being turned up.
/// </remarks>
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
public readonly record struct RayTracingSettings(
    RayTracingQuality Quality,
    int ShadowLights,
    int AmbientOcclusionRays,
    int ShadowSamples,
    float LightmapIndirect,
    float AmbientOcclusionRadius)
{
    /// <summary>Whether any rays are traced at all.</summary>
    public bool TracesRays => ShadowLights > 0 || AmbientOcclusionRays > 0;

    /// <summary>Whether the baked lightmaps still light scene geometry outright.</summary>
    /// <remarks>
    /// True only at <see cref="RayTracingQuality.None"/>. Above it the rig lights
    /// everything and the bake, where it is still used, contributes bounce rather than
    /// the whole result.
    /// </remarks>
    public bool BakedOnly => Quality == RayTracingQuality.None;

    /// <summary>
    /// How far an occlusion ray reaches, in scene units.
    /// </summary>
    /// <remarks>
    /// The same at every quality level, because it describes the effect rather than the
    /// budget: it is the scale at which a surface counts as being in a corner, and the
    /// ray count is what changes with quality. Forty-five units is a little over a metre.
    /// The value was ninety at Medium and a hundred and forty at High, and R25 is only
    /// three hundred across, so a hemisphere that size reached a wall from anywhere in the
    /// room; occlusion sat low over every surface instead of gathering where two of them
    /// meet, and since it multiplies the whole indirect term the room went dark with it.
    /// </remarks>
    private const float OcclusionRadius = 45f;

    /// <summary>The settings for a quality level.</summary>
    /// <param name="quality">The level.</param>
    /// <returns>Its ray budget.</returns>
    /// <remarks>
    /// <para>
    /// The counts are sized to GK3 rather than to a generic scene. A lit scene declares
    /// six lights at the median, so eight shadowed lights already covers most rooms
    /// outright at Low; the scenes with dozens are corridors and exteriors where the
    /// distant ones contribute little.
    /// </para>
    /// <para>
    /// Every level above None keeps some of the bake as its indirect term, because there
    /// is no gathered bounce yet and a room lit by direct light alone reads as harsh and
    /// far too dark — the bake is the artists' own answer for what the walls are
    /// bouncing. It double counts the direct light it also contains, which is why it is
    /// scaled down rather than used whole, and why the weight falls as quality rises.
    /// Removing it entirely waits on real indirect light, which needs material data at
    /// the hit point and so a good deal more than a ray query.
    /// </para>
    /// </remarks>
    public static RayTracingSettings For(RayTracingQuality quality) => quality switch
    {
        RayTracingQuality.Low => new(quality, 8, 0, 1, 0.6f, 0f),
        RayTracingQuality.Medium => new(quality, 16, 4, 1, 0.5f, OcclusionRadius),
        RayTracingQuality.High => new(quality, 32, 8, 2, 0.35f, OcclusionRadius),
        _ => new(RayTracingQuality.None, 0, 0, 1, 1f, 0f),
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
