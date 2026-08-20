namespace GK3Reborn.Content.Manifests;

/// <summary>One classified asset.</summary>
public sealed record CorpusAsset
{
    /// <summary>Asset name as spelled in the archive.</summary>
    public required string Name { get; init; }

    /// <summary>Archive the asset was read from.</summary>
    public required string Archive { get; init; }

    /// <summary>Extension, uppercased, or null when the name has none.</summary>
    public string? Extension { get; init; }

    /// <summary>What the contents say this is.</summary>
    public required string Kind { get; init; }

    /// <summary>How the classification was reached.</summary>
    public required string Basis { get; init; }

    /// <summary>Printable rendering of the leading bytes.</summary>
    public required string Magic { get; init; }

    /// <summary>Decompressed size in bytes.</summary>
    public required int Bytes { get; init; }
}

/// <summary>A reference from a text asset that does not resolve.</summary>
public sealed record CorpusDanglingReference
{
    /// <summary>Asset containing the reference.</summary>
    public required string From { get; init; }

    /// <summary>Archive that asset came from.</summary>
    public required string FromArchive { get; init; }

    /// <summary>The name that could not be resolved.</summary>
    public required string Reference { get; init; }
}

/// <summary>Corpus-wide totals.</summary>
public sealed record CorpusSummary
{
    /// <summary>Number of assets classified.</summary>
    public required int Assets { get; init; }

    /// <summary>Total decompressed size.</summary>
    public required long TotalBytes { get; init; }

    /// <summary>Distinct extensions seen across the corpus.</summary>
    public required int DistinctExtensions { get; init; }

    /// <summary>References that resolved to a known asset.</summary>
    public required int ReferencesResolved { get; init; }

    /// <summary>References that did not resolve.</summary>
    public required int ReferencesDangling { get; init; }
}

/// <summary>The C2 corpus inventory.</summary>
public sealed record CorpusManifest
{
    /// <summary>Schema version.</summary>
    public required int SchemaVersion { get; init; }

    /// <summary>Pipeline stage that produced it.</summary>
    public required string Stage { get; init; }

    /// <summary>Directory the archives were read from.</summary>
    public required string SourceRoot { get; init; }

    /// <summary>Corpus-wide totals.</summary>
    public required CorpusSummary Summary { get; init; }

    /// <summary>Asset counts by kind, most common first.</summary>
    public required IReadOnlyDictionary<string, int> KindCounts { get; init; }

    /// <summary>Total bytes by kind, largest first.</summary>
    public required IReadOnlyDictionary<string, long> KindBytes { get; init; }

    /// <summary>
    /// How many distinct extensions each kind appears under.
    /// </summary>
    /// <remarks>
    /// The number that shows why classification cannot go by name.
    /// </remarks>
    public required IReadOnlyDictionary<string, int> ExtensionsByKind { get; init; }

    /// <summary>Unclassified assets, capped for readability.</summary>
    public required IReadOnlyList<CorpusAsset> Unknown { get; init; }

    /// <summary>References that did not resolve, capped for readability.</summary>
    public required IReadOnlyList<CorpusDanglingReference> DanglingReferences { get; init; }

    /// <summary>Every asset, ordered by name.</summary>
    public required IReadOnlyList<CorpusAsset> Assets { get; init; }
}
