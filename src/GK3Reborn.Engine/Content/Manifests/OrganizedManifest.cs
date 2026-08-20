namespace GK3Reborn.Content.Manifests;

/// <summary>Where one asset ended up, and what happened to it.</summary>
public sealed record OrganizedAsset
{
    /// <summary>Asset name as spelled in the archive.</summary>
    public required string Name { get; init; }

    /// <summary>Path relative to the normalized root.</summary>
    public required string Path { get; init; }

    /// <summary>What the asset is.</summary>
    public required string Kind { get; init; }

    /// <summary>Size of the original bytes.</summary>
    public required int SourceBytes { get; init; }

    /// <summary>Size after conversion, or the same as the source when unconverted.</summary>
    public required int OutputBytes { get; init; }

    /// <summary>What conversion was applied, or null when the bytes passed through.</summary>
    public string? Conversion { get; init; }
}

/// <summary>Totals for an organize run.</summary>
public sealed record OrganizedSummary
{
    /// <summary>Assets placed.</summary>
    public required int Assets { get; init; }

    /// <summary>Assets converted to a modern format.</summary>
    public required int Converted { get; init; }

    /// <summary>Assets that could not be placed or converted.</summary>
    public required int Failed { get; init; }

    /// <summary>Total original size.</summary>
    public required long SourceBytes { get; init; }

    /// <summary>Total size written.</summary>
    public required long OutputBytes { get; init; }
}

/// <summary>The C3 normalized-layout manifest.</summary>
public sealed record OrganizedManifest
{
    /// <summary>Schema version.</summary>
    public required int SchemaVersion { get; init; }

    /// <summary>Pipeline stage that produced it.</summary>
    public required string Stage { get; init; }

    /// <summary>Stage version.</summary>
    public required string StageVersion { get; init; }

    /// <summary>Directory the archives were read from.</summary>
    public required string SourceRoot { get; init; }

    /// <summary>Directory the tree was written to.</summary>
    public required string OutputRoot { get; init; }

    /// <summary>Totals.</summary>
    public required OrganizedSummary Summary { get; init; }

    /// <summary>Asset counts per top-level directory.</summary>
    public required IReadOnlyDictionary<string, int> DirectoryCounts { get; init; }

    /// <summary>Every asset, ordered by path.</summary>
    public required IReadOnlyList<OrganizedAsset> Assets { get; init; }
}
