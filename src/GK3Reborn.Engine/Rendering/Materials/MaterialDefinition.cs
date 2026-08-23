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

    /// <summary>New emissive color, or null to keep.</summary>
    public Vector3? Emissive { get; init; }

    /// <summary>New alpha-test cutoff, or null to keep.</summary>
    public float? AlphaCutoff { get; init; }

    /// <summary>New double-sided flag, or null to keep.</summary>
    public bool? DoubleSided { get; init; }

    /// <summary>Note explaining the correction.</summary>
    public string? ReviewNote { get; init; }
}

/// <summary>
/// The PBR description of one original material.
/// </summary>
/// <remarks>
/// <para>
/// The 1999 assets carry a diffuse texture and little else. Everything a physically
/// based renderer needs — roughness, metalness, specular response, normal detail —
/// has to be inferred from the texture, the surface's name and its role in the scene.
/// Those inferences are guesses, and some will be wrong in ways only visible in
/// motion under real lighting: a stone floor that reads as wet, a brass fitting with
/// no highlight at all.
/// </para>
/// <para>
/// So every channel is correctable through the same edit layer the lighting rigs use.
/// Fix the value in the material's edits file, and the correction survives every
/// future rerun of the inference pass. See ADR 0006.
/// </para>
/// </remarks>
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
    /// <remarks>
    /// Null where there is none, which is most of them: 324 of the game's 6,657 textures
    /// have one so far. A surface without one is given a flat map and looks exactly as it
    /// did, which is how a partial set stays a perfectly good set.
    /// </remarks>
    public string? NormalTexture { get; init; }

    /// <summary>
    /// The surface's packed occlusion, roughness and metalness.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Red is ambient occlusion, green is roughness, blue is metalness — the glTF packing,
    /// which is what every generator and every authoring tool already writes. Read from
    /// <c>enhanced/orm</c>, named for the colour texture it belongs to.
    /// </para>
    /// <para>
    /// The map multiplies <see cref="Roughness"/> and <see cref="Metallic"/> rather than
    /// replacing them, which is what keeps a corrected value in the edit layer meaningful
    /// once a generated map arrives for the same surface.
    /// </para>
    /// </remarks>
    public string? OrmTexture { get; init; }

    /// <summary>
    /// The surface's height field, for parallax and for displacement.
    /// </summary>
    /// <remarks>
    /// Read from <c>enhanced/height</c>, named for the colour texture it belongs to. Mid
    /// grey is the modelled surface and the channel runs either side of it. Two things
    /// consume it: a marched texture-coordinate offset, which deepens mortar courses and
    /// cobbles and does nothing at all to a silhouette, and — on a floor, where the
    /// silhouette is what gives a street away — real geometry. See
    /// <see cref="ReliefPlan"/>.
    /// </remarks>
    public string? HeightTexture { get; init; }

    /// <summary>
    /// How deep the height map goes, in <em>world</em> units from its floor to its ceiling.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A GK3 unit is roughly two and a half centimetres, so the default is a relief of
    /// about four: a cobble's crown over its gutter, a floorboard's chamfer, the depth of a
    /// mortar course. Like <see cref="NormalStrength"/> this is a decision recorded per
    /// material rather than a constant in the shader, because how much of a generated
    /// field to believe differs by surface.
    /// </para>
    /// <para>
    /// <b>World units, not texture coordinates.</b> It was the latter until the corpus was
    /// measured: the game tiles one road texture over 232 units of street and one lobby
    /// floor over 32, so a single number in texture coordinates was seven times as deep on
    /// the second as on the first, and nobody had chosen that. The shader converts through
    /// the surface's own tiling, which it can read off the tangent frame it already builds.
    /// </para>
    /// </remarks>
    public float HeightDepth { get; init; } = 1.5f;

    /// <summary>
    /// Whether this surface's relief is cut into the geometry as well as marched by the
    /// shader.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Off by default, and it has to be. Every texture in the game has a generated height
    /// field, and for most of them the field is not relief at all: a grass texture's is
    /// blades, a rug's is pile, a painted backdrop's is whatever the model made of a
    /// picture of a hillside. Marching those does no harm — the offset is a few texels and
    /// it reads as texture. Moving vertices by them makes a lawn out of corrugated iron.
    /// </para>
    /// <para>
    /// <b>And it is what the triangle budget is spent on.</b> CSE's floor object is
    /// nineteen million square units of village, of which the road is one; displacing all
    /// of it buys a cell so coarse that the high-passed field averages to nothing inside
    /// one, which is the worst of both — every triangle paid for and no relief to show for
    /// them. Turned on for the paved fifth of it, the same budget buys four units a cell.
    /// </para>
    /// <para>
    /// The set that has it on is derived from the material classifier and then reviewed:
    /// stone, brick, tile, concrete, marble and wood, plus the surfaces it calls ground
    /// whose names say road, cobble, path or pavement rather than soil or sand.
    /// </para>
    /// </remarks>
    public bool Displaced { get; init; }

    /// <summary>Linear emissive color. Zero for non-emissive surfaces.</summary>
    public Vector3 Emissive { get; init; }

    /// <summary>Alpha-test cutoff, where the surface is alpha tested.</summary>
    public float? AlphaCutoff { get; init; }

    /// <summary>Whether the surface renders from both sides.</summary>
    public bool DoubleSided { get; init; }

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
            Emissive = patch.Emissive ?? Emissive,
            AlphaCutoff = patch.AlphaCutoff ?? AlphaCutoff,
            DoubleSided = patch.DoubleSided ?? DoubleSided,
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
/// <remarks>
/// Stored beside the generated library as <c>&lt;LIBRARY&gt;.materials.edits.json</c> and
/// never written by the inference pass.
/// </remarks>
public sealed record MaterialEdits
{
    /// <summary>Schema version.</summary>
    public required int SchemaVersion { get; init; }

    /// <summary>Library these corrections apply to.</summary>
    public required string LibraryId { get; init; }

    /// <summary>Corrections, applied in order.</summary>
    public required IReadOnlyList<Edit<MaterialDefinition, MaterialPatch>> Edits { get; init; }
}
