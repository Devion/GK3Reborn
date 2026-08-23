using System.Numerics;

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
    /// <remarks>
    /// <para>
    /// Green-leaning and very dim where the bake is still in play: there it only has to keep
    /// a corner no lamp reaches from going to black, which is the job the original's own
    /// ambient floor did.
    /// </para>
    /// <para>
    /// Where the bake is gone it is doing a great deal more, because it is now the only
    /// thing standing in for light that has bounced. Measured on <c>RC1</c> and <c>LBY</c>:
    /// the rig alone lands about a third below the bake, and this is what closes it. It is
    /// modulated by traced ambient occlusion at those tiers, which is what keeps it from
    /// reading as the flat wash a constant would be — a corner still darkens, it just
    /// darkens because a ray said so rather than because a lightmap was painted that way.
    /// </para>
    /// </remarks>
    public Vector3 Ambient => UsesBake
        ? new Vector3(0.06f, 0.08f, 0.06f)
        : new Vector3(0.15f, 0.16f, 0.17f);

    /// <summary>How much of the traced ambient occlusion to believe, from zero to one.</summary>
    /// <remarks>
    /// <para>
    /// Never all of it: whole, it drives a surface to black outright, because enough of the
    /// hemisphere above a shoulder is that person's own head that the shoulder disappears.
    /// </para>
    /// <para>
    /// Where the bake is still in play there is a second reason to hold it back — those
    /// lightmaps were baked with occlusion already in them, so a hemisphere of rays is
    /// measuring something the bake has largely accounted for and applying it whole counts
    /// it twice. Medium and High have no bake to count twice against, so they believe a good
    /// deal more of it, and that is what puts a chair leg on the floor rather than above it.
    /// </para>
    /// </remarks>
    public float OcclusionStrength => UsesBake ? 0.55f : 0.85f;

    /// <summary>How much the baked lightmaps shape the ambient floor, from zero to one.</summary>
    /// <remarks>
    /// <para>
    /// A bake is not allowed to be the lighting at these tiers, and it is still the best map
    /// anybody has of where the light in a room goes. The artists decided in 1999 that the
    /// wall beside the sconce is warm and the corner behind the screen is not, and dropping
    /// all of it flattened rooms that are full of lamps: the dining room's sconces went dark,
    /// its tablecloths turned from cream to grey, and almost nothing in it cast a readable
    /// shadow because there was nothing for a shadow to be darker than.
    /// </para>
    /// <para>
    /// So the bake modulates the ambient term instead of adding to it. That is not the same
    /// thing as lighting from it — the term stays ambient, stays subject to traced occlusion,
    /// and is never subtracted against — and what it buys is the room's shape and colour
    /// back. Nothing at None and Low, where the bake is already doing the lighting outright
    /// and shaping it twice would only deepen it.
    /// </para>
    /// </remarks>
    public float LightmapHint => UsesBake ? 0f : 1f;

    /// <summary>Whether any rays are traced at all.</summary>
    public bool TracesRays => ShadowLights > 0 || AmbientOcclusionRays > 0;

    /// <summary>Whether the baked lightmaps still light scene geometry outright.</summary>
    /// <remarks>
    /// True only at <see cref="RayTracingQuality.None"/>. Above it the rig lights
    /// everything and the bake, where it is still used, contributes bounce rather than
    /// the whole result.
    /// </remarks>
    public bool BakedOnly => Quality == RayTracingQuality.None;

    /// <summary>Whether the bake contributes anything at all.</summary>
    /// <remarks>
    /// False at Medium and High, where the room is lit outright. It is a separate question
    /// from <see cref="BakedOnly"/>: one asks whether the bake is the whole answer, this
    /// asks whether it is any of it.
    /// </remarks>
    public bool UsesBake => LightmapIndirect > 0f;

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
    /// <b>Medium and High light the room outright and use no bake at all</b>, which is
    /// what <c>Plan/04</c> P10 asks for — "the RT and enhanced tiers light scenes from the
    /// rig, the compatibility tier keeps baked lightmaps" — and what ADR 0006 means by
    /// re-lighting for modern range rather than matching 1999 output. A baked lightmap is
    /// light that was computed once, for a room with nobody in it, and cannot know about
    /// anything that has happened since; keeping it is what made a character's shadow so
    /// faint, because a shadow can only take away the share of a surface the rig accounts
    /// for and the bake was holding the rest.
    /// </para>
    /// <para>
    /// What replaces it is not nothing. The artists' own rig is not only key lights: 125
    /// <c>sky_bounce</c> and 169 <c>ground_bounce</c> entries across the corpus are their
    /// answer to what the walls and floor are throwing back, and <c>ground_bounce</c> is
    /// the most common light name in the game. Evaluating the rig in full is evaluating
    /// their bounce approximation along with their key light — which is why dropping the
    /// bake costs far less than the raw numbers suggest.
    /// </para>
    /// <para>
    /// Low keeps most of the bake. It is the tier for hardware that can trace a few shadow
    /// rays and nothing else, and a room lit by a handful of shadowed lights with no
    /// occlusion is exactly the case the bake is still the better answer for.
    /// </para>
    /// </remarks>
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
