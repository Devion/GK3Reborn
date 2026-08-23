namespace GK3Reborn.Content.Manifests;

/// <summary>One texture and what should happen to it.</summary>
public sealed record TexturePlanEntry
{
    /// <summary>Texture name, without extension.</summary>
    public required string Name { get; init; }

    /// <summary>Current pixel width.</summary>
    public required int Width { get; init; }

    /// <summary>Current pixel height.</summary>
    public required int Height { get; init; }

    /// <summary>Whether the texture carries transparency.</summary>
    public required bool HasAlpha { get; init; }

    /// <summary>Enhancement tier, 0 being the most visible.</summary>
    public required int Tier { get; init; }

    /// <summary>
    /// True when the texture is a single colour, or two. Upscaling one of these produces
    /// a larger flat colour; they belong in a material instead.
    /// </summary>
    public required bool IsFlatColor { get; init; }

    /// <summary>The texture's colour, when it is flat, as <c>#RRGGBB</c>.</summary>
    public string? FlatColor { get; init; }

    /// <summary>Target size for the largest dimension, or zero to leave alone.</summary>
    public required int TargetSize { get; init; }

    /// <summary>How many character models use it.</summary>
    public required int UsedByCharacters { get; init; }

    /// <summary>How many props use it.</summary>
    public required int UsedByProps { get; init; }

    /// <summary>How many rooms use it.</summary>
    public required int UsedByRooms { get; init; }

    /// <summary>A sample of what references it, for context when authoring.</summary>
    public required IReadOnlyList<string> Referrers { get; init; }
}

/// <summary>The texture enhancement plan.</summary>
public sealed record TexturePlanManifest
{
    /// <summary>Schema version.</summary>
    public required int SchemaVersion { get; init; }

    /// <summary>Pipeline stage that produced it.</summary>
    public required string Stage { get; init; }

    /// <summary>Directory the archives were read from.</summary>
    public required string SourceRoot { get; init; }

    /// <summary>Texture counts per tier.</summary>
    public required IReadOnlyDictionary<string, int> TierCounts { get; init; }

    /// <summary>Total megapixels across every texture, as a sense of the budget.</summary>
    public required double TotalMegapixels { get; init; }

    /// <summary>Every texture, most important first.</summary>
    public required IReadOnlyList<TexturePlanEntry> Textures { get; init; }
}
