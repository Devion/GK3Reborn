namespace GK3Reborn.Rendering.Lighting;

/// <summary>Kind of light a rig entry describes.</summary>
public enum SceneLightKind
{
    /// <summary>Omnidirectional point light.</summary>
    Point,

    /// <summary>Cone-shaped spot light.</summary>
    Spot,

    /// <summary>Rectangular area light, typically a window or practical fixture.</summary>
    Area,

    /// <summary>Uniform ambient or sky contribution.</summary>
    Ambient,
}

/// <summary>Where a light in a rig came from.</summary>
public enum LightProvenance
{
    /// <summary>Derived automatically from the scene's baked lightmaps.</summary>
    Derived,

    /// <summary>Derived, then adjusted by hand.</summary>
    Edited,

    /// <summary>Authored from scratch.</summary>
    Authored,
}

/// <summary>One light in a scene's rig.</summary>
/// <remarks>
/// Produced by pipeline stage C4b (Plan/02-content-pipeline.md). The original game
/// bakes all scene lighting into MUL lightmaps, which nothing dynamic or ray traced
/// can use. C4b treats the lightmaps as evidence: it back-projects luminance maxima
/// into world space to propose lights, and a human confirms or replaces them. The
/// baked lightmaps stay as the compatibility tier's lighting.
/// </remarks>
public sealed record SceneLight
{
    /// <summary>Stable identifier within the scene.</summary>
    public required string Id { get; init; }

    /// <summary>What kind of light this is.</summary>
    public required SceneLightKind Kind { get; init; }

    /// <summary>World-space position.</summary>
    public required System.Numerics.Vector3 Position { get; init; }

    /// <summary>World-space direction. Ignored for point and ambient lights.</summary>
    public System.Numerics.Vector3 Direction { get; init; }

    /// <summary>Linear RGB color, un-tinted by surface albedo where albedo was known.</summary>
    public required System.Numerics.Vector3 Color { get; init; }

    /// <summary>Intensity in the renderer's photometric units.</summary>
    public required float Intensity { get; init; }

    /// <summary>Approximate influence radius in world units.</summary>
    public required float Radius { get; init; }

    /// <summary>Cone half-angle in radians. Spot lights only.</summary>
    public float ConeAngleRadians { get; init; }

    /// <summary>How this light entered the rig.</summary>
    public required LightProvenance Provenance { get; init; }

    /// <summary>
    /// Confidence in a derived light, from 0 to 1. Low-confidence lights are review
    /// candidates, not shippable content.
    /// </summary>
    public required float Confidence { get; init; }

    /// <summary>Free-form note from whoever reviewed this light.</summary>
    public string? ReviewNote { get; init; }
}

/// <summary>A scene's complete dynamic lighting rig.</summary>
public sealed record SceneLightRig
{
    /// <summary>Schema version.</summary>
    public required int SchemaVersion { get; init; }

    /// <summary>Scene this rig lights.</summary>
    public required string SceneId { get; init; }

    /// <summary>The lights.</summary>
    public required IReadOnlyList<SceneLight> Lights { get; init; }

    /// <summary>
    /// Perceptual difference between re-baking this rig and the original lightmap.
    /// Lower is closer to the 1999 lighting. Null until the comparison has run.
    /// </summary>
    public double? RebakeDelta { get; init; }

    /// <summary>Whether a human has signed this rig off for shipping.</summary>
    public required bool SignedOff { get; init; }
}
