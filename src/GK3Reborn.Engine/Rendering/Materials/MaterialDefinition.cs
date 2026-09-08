using System.Numerics;
using GK3Reborn.Content.Authoring;
using GK3Reborn.Foundation.Diagnostics;

namespace GK3Reborn.Rendering.Materials;

/// <summary>A sparse change to a material. Null fields are left alone.</summary>
public sealed record MaterialPatch
{
    /// <summary>New base color tint, or null to keep.</summary>
    public Vector3? BaseColorTint { get; init; }

    /// <summary>New roughness, or null to keep.</summary>
    public float? Roughness { get; init; }

    /// <summary>New metallic value, or null to keep.</summary>
    public float? Metallic { get; init; }

    /// <summary>New specular reflectance at normal incidence, or null to keep.</summary>
    public float? SpecularReflectance { get; init; }

    /// <summary>New normal-map strength, or null to keep.</summary>
    public float? NormalStrength { get; init; }

    /// <summary>A different normal map, or empty to go back to having none.</summary>
    public string? NormalTexture { get; init; }

    /// <summary>A different packed occlusion/roughness/metalness map.</summary>
    public string? OrmTexture { get; init; }

    /// <summary>A different height map, or empty to go back to having none.</summary>
    public string? HeightTexture { get; init; }

    /// <summary>New height-map depth in world units, or null to keep.</summary>
    public float? HeightDepth { get; init; }

    /// <summary>Whether the height map becomes geometry, or null to keep.</summary>
    public bool? Displaced { get; init; }

    /// <summary>Whether the surface is a mirror, or null to keep.</summary>
    public bool? Mirror { get; init; }

    /// <summary>How much of each edge is drawn frame rather than glass, or null to keep.</summary>
    public float? MirrorInset { get; init; }

    /// <summary>Whether the surface is a lit CRT screen, or null to keep.</summary>
    public bool? Screen { get; init; }

    /// <summary>Where the glass is within the texture, or null to keep.</summary>
    public Vector4? ScreenGlass { get; init; }

    /// <summary>New emissive color, or null to keep.</summary>
    public Vector3? Emissive { get; init; }

    /// <summary>New alpha-test cutoff, or null to keep.</summary>
    public float? AlphaCutoff { get; init; }

    /// <summary>New double-sided flag, or null to keep.</summary>
    public bool? DoubleSided { get; init; }

    /// <summary>How many fur shells to draw over this surface, or null to keep.</summary>
    public int? Shells { get; init; }

    /// <summary>How far the fur stands off the surface in world units, or null to keep.</summary>
    public float? ShellDepth { get; init; }

    /// <summary>How many strands across one turn of the texture, or null to keep.</summary>
    public float? ShellDensity { get; init; }

    /// <summary>Note explaining the correction.</summary>
    public string? ReviewNote { get; init; }
}

/// <summary>
/// The PBR description of one original material.
/// </summary>
public sealed record MaterialDefinition : IAuthorable<MaterialDefinition, MaterialPatch>
{
    /// <summary>Stable identifier; normally the original material or texture name.</summary>
    public required string Id { get; init; }

    /// <summary>Logical id of the base color texture.</summary>
    public required string BaseColorTexture { get; init; }

    /// <summary>Multiplier over the base color texture.</summary>
    public Vector3 BaseColorTint { get; init; } = Vector3.One;

    /// <summary>Roughness, 0 (mirror) to 1 (fully diffuse).</summary>
    public required float Roughness { get; init; }

    /// <summary>Metalness, 0 (dielectric) to 1 (conductor).</summary>
    public required float Metallic { get; init; }

    /// <summary>Specular reflectance at normal incidence for dielectrics. 0.5 is the neutral default.</summary>
    public float SpecularReflectance { get; init; } = 0.5f;

    /// <summary>Strength of the normal map, where one exists.</summary>
    public float NormalStrength { get; init; } = 1.0f;

    /// <summary>
    /// The surface's normal map, named for the colour texture it belongs to.
    /// </summary>
    public string? NormalTexture { get; init; }

    /// <summary>
    /// The surface's packed occlusion, roughness and metalness.
    /// </summary>
    public string? OrmTexture { get; init; }

    /// <summary>
    /// The surface's height field, for parallax and for displacement.
    /// </summary>
    public string? HeightTexture { get; init; }

    /// <summary>
    /// How deep the height map goes, in <em>world</em> units from its floor to its ceiling.
    /// </summary>
    public float HeightDepth { get; init; } = 1.5f;

    /// <summary>
    /// Whether this surface's relief is cut into the geometry as well as marched by the
    /// shader.
    /// </summary>
    public bool Displaced { get; init; }

    /// <summary>
    /// Whether this surface is a mirror, and its reflection is to be rendered rather than
    /// taken from the texture.
    /// </summary>
    public bool Mirror { get; init; }

    /// <summary>
    /// How much of the texture, as a share of each edge, is frame rather than glass.
    /// </summary>
    public float MirrorInset { get; init; }

    /// <summary>
    /// Whether this texture is a lit CRT screen, whose raster the shader draws.
    /// </summary>
    public bool Screen { get; init; }

    /// <summary>
    /// Where the lit glass is inside the texture: u and v of one corner, then the other.
    /// </summary>
    public Vector4 ScreenGlass { get; init; }

    /// <summary>Linear emissive color. Zero for non-emissive surfaces.</summary>
    public Vector3 Emissive { get; init; }

    /// <summary>Alpha-test cutoff, where the surface is alpha tested.</summary>
    public float? AlphaCutoff { get; init; }

    /// <summary>Whether the surface renders from both sides.</summary>
    public bool DoubleSided { get; init; }

    /// <summary>How many fur shells stand over this surface. Zero, for all but a few.</summary>
    public int Shells { get; init; }

    /// <summary>How far the outermost shell stands off the surface, in model units.</summary>
    public float ShellDepth { get; init; } = 1f;

    /// <summary>How many strands stand across one turn of the texture.</summary>
    public float ShellDensity { get; init; } = 160f;

    /// <summary>How this material's values were arrived at.</summary>
    public required AuthoringProvenance Provenance { get; init; }

    /// <summary>Confidence in the inference, from 0 to 1. Meaningless once corrected.</summary>
    public required float Confidence { get; init; }

    /// <summary>What the inference was based on, or why a human changed it.</summary>
    public string? ReviewNote { get; init; }

    /// <inheritdoc/>
    public MaterialDefinition ApplyPatch(MaterialPatch patch)
    {
        ArgumentNullException.ThrowIfNull(patch);

        return this with
        {
            BaseColorTint = patch.BaseColorTint ?? BaseColorTint,
            Roughness = patch.Roughness ?? Roughness,
            Metallic = patch.Metallic ?? Metallic,
            SpecularReflectance = patch.SpecularReflectance ?? SpecularReflectance,
            NormalStrength = patch.NormalStrength ?? NormalStrength,

            // An empty string means "go back to having none", which a null cannot say.
            NormalTexture = patch.NormalTexture is null
                ? NormalTexture
                : patch.NormalTexture.Length > 0 ? patch.NormalTexture : null,
            OrmTexture = patch.OrmTexture is null
                ? OrmTexture
                : patch.OrmTexture.Length > 0 ? patch.OrmTexture : null,
            HeightTexture = patch.HeightTexture is null
                ? HeightTexture
                : patch.HeightTexture.Length > 0 ? patch.HeightTexture : null,
            HeightDepth = patch.HeightDepth ?? HeightDepth,
            Displaced = patch.Displaced ?? Displaced,
            Mirror = patch.Mirror ?? Mirror,
            MirrorInset = patch.MirrorInset ?? MirrorInset,
            Screen = patch.Screen ?? Screen,
            ScreenGlass = patch.ScreenGlass ?? ScreenGlass,
            Emissive = patch.Emissive ?? Emissive,
            AlphaCutoff = patch.AlphaCutoff ?? AlphaCutoff,
            DoubleSided = patch.DoubleSided ?? DoubleSided,
            Shells = patch.Shells ?? Shells,
            ShellDepth = patch.ShellDepth ?? ShellDepth,
            ShellDensity = patch.ShellDensity ?? ShellDensity,
            ReviewNote = patch.ReviewNote ?? ReviewNote,
        };
    }

    /// <inheritdoc/>
    public MaterialDefinition MarkEdited() =>
        Provenance == AuthoringProvenance.Authored ? this : this with { Provenance = AuthoringProvenance.Edited };
}

/// <summary>A library of inferred materials, as generated before edits are applied.</summary>
public sealed record MaterialLibrary
{
    /// <summary>Schema version.</summary>
    public required int SchemaVersion { get; init; }

    /// <summary>What this library covers; normally a scene or asset group.</summary>
    public required string LibraryId { get; init; }

    /// <summary>The materials.</summary>
    public required IReadOnlyList<MaterialDefinition> Materials { get; init; }

    /// <summary>
    /// Applies hand-authored corrections, returning the library the renderer should use.
    /// </summary>
    /// <param name="edits">Corrections, or null when none exist.</param>
    /// <param name="diagnostics">Receives warnings about corrections that no longer apply.</param>
    /// <returns>The effective library.</returns>
    public MaterialLibrary WithEdits(MaterialEdits? edits, DiagnosticBag diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);

        if (edits is null || edits.Edits.Count == 0)
        {
            return this;
        }

        return this with
        {
            Materials = EditLayer.Compose(Materials, edits.Edits, $"{LibraryId}.materials", diagnostics),
        };
    }
}

/// <summary>
/// Hand-authored corrections to inferred materials.
/// </summary>
public sealed record MaterialEdits
{
    /// <summary>Schema version.</summary>
    public required int SchemaVersion { get; init; }

    /// <summary>Library these corrections apply to.</summary>
    public required string LibraryId { get; init; }

    /// <summary>Corrections, applied in order.</summary>
    public required IReadOnlyList<Edit<MaterialDefinition, MaterialPatch>> Edits { get; init; }
}
