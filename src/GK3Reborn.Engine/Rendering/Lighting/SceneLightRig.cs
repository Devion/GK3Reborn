using System.Numerics;
using System.Text.Json.Serialization;
using GK3Reborn.Content.Authoring;
using GK3Reborn.Foundation.Diagnostics;

namespace GK3Reborn.Rendering.Lighting;

/// <summary>Kind of light a rig entry describes.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SceneLightKind>))]
public enum SceneLightKind
{
    /// <summary>Omnidirectional point light.</summary>
    [JsonStringEnumMemberName("point")]
    Point,

    /// <summary>Cone-shaped spot light.</summary>
    [JsonStringEnumMemberName("spot")]
    Spot,

    /// <summary>Rectangular area light, typically a window or practical fixture.</summary>
    [JsonStringEnumMemberName("area")]
    Area,

    /// <summary>Uniform ambient or sky contribution.</summary>
    [JsonStringEnumMemberName("ambient")]
    Ambient,
}

/// <summary>A sparse change to a light. Null fields are left alone.</summary>
public sealed record SceneLightPatch
{
    /// <summary>New kind, or null to keep.</summary>
    public SceneLightKind? Kind { get; init; }

    /// <summary>New world-space position, or null to keep.</summary>
    public Vector3? Position { get; init; }

    /// <summary>New world-space direction, or null to keep.</summary>
    public Vector3? Direction { get; init; }

    /// <summary>New linear RGB color, or null to keep.</summary>
    public Vector3? Color { get; init; }

    /// <summary>New intensity, or null to keep.</summary>
    public float? Intensity { get; init; }

    /// <summary>New influence radius, or null to keep.</summary>
    public float? Radius { get; init; }

    /// <summary>New cone half-angle in radians, or null to keep.</summary>
    public float? ConeAngleRadians { get; init; }

    /// <summary>Note explaining the correction.</summary>
    public string? ReviewNote { get; init; }
}

/// <summary>One light in a scene's rig.</summary>
/// <remarks>
/// Derived lights are produced by pipeline stage C4b, which back-projects lightmap
/// luminance into world space to guess where the 1999 artists put their lights. That
/// guess is a starting point: every field can be corrected, and lights can be added
/// or deleted outright, through the edit layer. See ADR 0002 and ADR 0006.
/// </remarks>
public sealed record SceneLight : IAuthorable<SceneLight, SceneLightPatch>
{
    /// <summary>Stable identifier within the scene.</summary>
    public required string Id { get; init; }

    /// <summary>What kind of light this is.</summary>
    public required SceneLightKind Kind { get; init; }

    /// <summary>World-space position.</summary>
    public required Vector3 Position { get; init; }

    /// <summary>World-space direction. Ignored for point and ambient lights.</summary>
    public Vector3 Direction { get; init; }

    /// <summary>Linear RGB color, un-tinted by surface albedo where albedo was known.</summary>
    public required Vector3 Color { get; init; }

    /// <summary>Intensity in the renderer's photometric units.</summary>
    public required float Intensity { get; init; }

    /// <summary>Approximate influence radius in world units.</summary>
    public required float Radius { get; init; }

    /// <summary>Cone half-angle in radians. Spot lights only.</summary>
    public float ConeAngleRadians { get; init; }

    /// <summary>How this light entered the rig.</summary>
    public required AuthoringProvenance Provenance { get; init; }

    /// <summary>
    /// Confidence in a derived light, from 0 to 1. Low-confidence lights are review
    /// candidates, not shippable content. Meaningless once a human has touched it.
    /// </summary>
    public required float Confidence { get; init; }

    /// <summary>How the derivation reached this guess, or why a human changed it.</summary>
    public string? ReviewNote { get; init; }

    /// <inheritdoc/>
    public SceneLight ApplyPatch(SceneLightPatch patch)
    {
        ArgumentNullException.ThrowIfNull(patch);

        return this with
        {
            Kind = patch.Kind ?? Kind,
            Position = patch.Position ?? Position,
            Direction = patch.Direction ?? Direction,
            Color = patch.Color ?? Color,
            Intensity = patch.Intensity ?? Intensity,
            Radius = patch.Radius ?? Radius,
            ConeAngleRadians = patch.ConeAngleRadians ?? ConeAngleRadians,
            ReviewNote = patch.ReviewNote ?? ReviewNote,
        };
    }

    /// <inheritdoc/>
    public SceneLight MarkEdited() =>
        Provenance == AuthoringProvenance.Authored ? this : this with { Provenance = AuthoringProvenance.Edited };
}

/// <summary>A scene's dynamic lighting rig, as generated before edits are applied.</summary>
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
    /// A diagnostic signal, not an acceptance gate: the project re-lights for modern
    /// range rather than reproducing 1999 exactly (ADR 0006). A large delta means the
    /// derivation may have missed a source, which is worth a look before signing off.
    /// </summary>
    public double? RebakeDelta { get; init; }

    /// <summary>Whether a human has signed this rig off for shipping.</summary>
    public required bool SignedOff { get; init; }

    /// <summary>
    /// Applies hand-authored corrections, returning the rig the renderer should use.
    /// </summary>
    /// <param name="edits">Corrections, or null when none exist.</param>
    /// <param name="diagnostics">Receives warnings about corrections that no longer apply.</param>
    /// <returns>The effective rig.</returns>
    public SceneLightRig WithEdits(SceneLightEdits? edits, DiagnosticBag diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);

        if (edits is null || edits.Edits.Count == 0)
        {
            return this;
        }

        return this with
        {
            Lights = EditLayer.Compose(Lights, edits.Edits, $"{SceneId}.lighting", diagnostics),
        };
    }
}

/// <summary>
/// Hand-authored corrections to a scene's derived lighting rig.
/// </summary>
/// <remarks>
/// Stored beside the generated rig as <c>&lt;SCENE&gt;.lighting.edits.json</c> and never
/// written by the generator, so re-running C4b cannot destroy an artist's work.
/// </remarks>
public sealed record SceneLightEdits
{
    /// <summary>Schema version.</summary>
    public required int SchemaVersion { get; init; }

    /// <summary>Scene these corrections apply to.</summary>
    public required string SceneId { get; init; }

    /// <summary>Corrections, applied in order.</summary>
    public required IReadOnlyList<Edit<SceneLight, SceneLightPatch>> Edits { get; init; }
}
