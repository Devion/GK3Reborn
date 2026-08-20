using System.Text.Json.Serialization;

namespace GK3Reborn.Content.Manifests;

/// <summary>Whether a candidate texture may be used.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TextureVerdict>))]
public enum TextureVerdict
{
    /// <summary>
    /// Usable, and not yet looked at by a person.
    /// </summary>
    /// <remarks>
    /// The default and the honest one. <c>Plan/02</c> section 1 requires human review
    /// before anything derived from the originals is distributed, so a candidate that
    /// passes every check a machine can make is still a draft.
    /// </remarks>
    [JsonStringEnumMemberName("draft")]
    Draft,

    /// <summary>A person has looked at it and it may ship.</summary>
    [JsonStringEnumMemberName("approved")]
    Approved,

    /// <summary>Fails a check that would make the game look wrong. Not used.</summary>
    [JsonStringEnumMemberName("rejected")]
    Rejected,
}

/// <summary>One candidate texture and what was found out about it.</summary>
public sealed record EnhancedTexture
{
    /// <summary>Texture name, without extension, as the geometry refers to it.</summary>
    public required string Name { get; init; }

    /// <summary>The file it came from, relative to the workspace.</summary>
    public required string Candidate { get; init; }

    /// <summary>Whether it may be used.</summary>
    public required TextureVerdict Verdict { get; init; }

    /// <summary>Why not, when it may not be used.</summary>
    public IReadOnlyList<string> Rejections { get; init; } = [];

    /// <summary>Things worth a person's attention that do not disqualify it.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>Width of the original.</summary>
    public required int SourceWidth { get; init; }

    /// <summary>Height of the original.</summary>
    public required int SourceHeight { get; init; }

    /// <summary>Width of the candidate.</summary>
    public required int Width { get; init; }

    /// <summary>Height of the candidate.</summary>
    public required int Height { get; init; }

    /// <summary>How many times larger the candidate is, by width.</summary>
    public required int Scale { get; init; }

    /// <summary>The tier the texture plan put it in.</summary>
    public required int Tier { get; init; }

    /// <summary>The size the texture plan asked for.</summary>
    public required int PlannedSize { get; init; }

    /// <summary>Whether the original was alpha-tested.</summary>
    public required bool SourceHasAlpha { get; init; }

    /// <summary>Whether the candidate carries transparency.</summary>
    public required bool HasAlpha { get; init; }
}

/// <summary>
/// What an import of enhanced textures produced.
/// </summary>
/// <remarks>
/// <para>
/// The provenance record <c>Plan/02</c> section 1 asks for: which tool made these, from
/// what, under what settings, and what a machine could tell about the result. It is
/// deliberately a record of a <em>draft</em>. Nothing here approves anything; a verdict of
/// <see cref="TextureVerdict.Draft"/> means only that no automatic check found it broken.
/// </para>
/// <para>
/// Rejections are kept rather than dropped. A candidate that lost its alpha or changed its
/// aspect ratio is worth going back to the generator about, and a list of names that
/// simply vanished from the manifest would not tell anybody that.
/// </para>
/// </remarks>
public sealed record EnhancedTextureManifest
{
    /// <summary>Manifest schema version.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Which pipeline stage produced it.</summary>
    public string Stage { get; init; } = "C4.texture-import";

    /// <summary>What made the candidates.</summary>
    public required string Tool { get; init; }

    /// <summary>Where the candidates were read from, relative to the workspace.</summary>
    public required string CandidateRoot { get; init; }

    /// <summary>Which of each candidate's files was taken.</summary>
    public required string Variant { get; init; }

    /// <summary>How many candidates were considered.</summary>
    public required int Considered { get; init; }

    /// <summary>How many were written into the enhanced set.</summary>
    public required int Accepted { get; init; }

    /// <summary>How many were refused, and why, by reason.</summary>
    public required IReadOnlyDictionary<string, int> RejectedBy { get; init; }

    /// <summary>Every candidate, accepted or not.</summary>
    public required IReadOnlyList<EnhancedTexture> Textures { get; init; }
}
