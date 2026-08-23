using System.Text.Json.Serialization;

namespace GK3Reborn.Content.Manifests;

/// <summary>What the enhancement pipeline should do with a model.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ModelDisposition>))]
public enum ModelDisposition
{
    /// <summary>An animated character. Full pipeline, treated as skinned.</summary>
    [JsonStringEnumMemberName("character")]
    Character,

    /// <summary>A prop. Full pipeline, treated as static.</summary>
    [JsonStringEnumMemberName("prop")]
    Prop,

    /// <summary>Static level geometry. Enhanced conservatively; silhouettes must hold.</summary>
    [JsonStringEnumMemberName("scene-geometry")]
    SceneGeometry,

    /// <summary>
    /// Collision, bounds or hit-test volume. Never drawn, and must survive untouched:
    /// the plan requires original navigation and collision to be preserved even where
    /// visible geometry is replaced.
    /// </summary>
    [JsonStringEnumMemberName("collision")]
    Collision,

    /// <summary>Declared only as collision yet carrying textured art. Needs a human.</summary>
    [JsonStringEnumMemberName("review")]
    Review,
}

/// <summary>One model and what is known about it.</summary>
public sealed record ModelRole
{
    /// <summary>Model name, without extension.</summary>
    public required string Name { get; init; }

    /// <summary>Every role a scene file declares for it.</summary>
    public required IReadOnlyList<string> Roles { get; init; }

    /// <summary>Whether animation data exists for it.</summary>
    public required bool Animated { get; init; }

    /// <summary>Whether any submesh names a texture.</summary>
    public required bool Textured { get; init; }

    /// <summary>Mesh count.</summary>
    public required int MeshCount { get; init; }

    /// <summary>Vertex count.</summary>
    public required int VertexCount { get; init; }

    /// <summary>Triangle count.</summary>
    public required int TriangleCount { get; init; }

    /// <summary>The recommended treatment.</summary>
    public required ModelDisposition Disposition { get; init; }
}

/// <summary>The model role manifest.</summary>
public sealed record ModelRoleManifest
{
    /// <summary>Schema version.</summary>
    public required int SchemaVersion { get; init; }

    /// <summary>Pipeline stage that produced it.</summary>
    public required string Stage { get; init; }

    /// <summary>Directory the archives were read from.</summary>
    public required string SourceRoot { get; init; }

    /// <summary>Model counts by disposition.</summary>
    public required IReadOnlyDictionary<string, int> DispositionCounts { get; init; }

    /// <summary>Every model, ordered by name.</summary>
    public required IReadOnlyList<ModelRole> Models { get; init; }
}
