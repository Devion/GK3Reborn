namespace GK3Reborn.Content.Manifests;

/// <summary>One entry that could not be extracted.</summary>
public sealed record BarnFailure
{
    /// <summary>Asset name as spelled in the archive.</summary>
    public required string Name { get; init; }

    /// <summary>Compression type recorded for the entry.</summary>
    public required string Compression { get; init; }

    /// <summary>Offset from the start of the archive's data section.</summary>
    public required uint Offset { get; init; }

    /// <summary>Stored size in bytes.</summary>
    public required uint Size { get; init; }

    /// <summary>What went wrong, including offset and expectation where known.</summary>
    public required string Error { get; init; }
}

/// <summary>The outcome of extracting one archive.</summary>
public sealed record BarnArchiveRecord
{
    /// <summary>Archive file name.</summary>
    public required string File { get; init; }

    /// <summary>Archive size in bytes.</summary>
    public required long Bytes { get; init; }

    /// <summary>Number of entries in the directory.</summary>
    public required int EntryCount { get; init; }

    /// <summary>Entries successfully decompressed.</summary>
    public required int Extracted { get; init; }

    /// <summary>Entries that point at an asset held in a different archive.</summary>
    public required int Pointers { get; init; }

    /// <summary>Entries that failed.</summary>
    public required int Failed { get; init; }

    /// <summary>Total decompressed size of everything extracted.</summary>
    public long ExtractedBytes { get; init; }

    /// <summary>Every failure, so none is reduced to a count.</summary>
    public required IReadOnlyList<BarnFailure> Failures { get; init; }

    /// <summary>Entry counts by compression type.</summary>
    public required IReadOnlyDictionary<string, int> CompressionCounts { get; init; }
}

/// <summary>The C1 Barn extraction manifest.</summary>
public sealed record BarnManifest
{
    /// <summary>Schema version of this manifest.</summary>
    public required int SchemaVersion { get; init; }

    /// <summary>Pipeline stage that produced it.</summary>
    public required string Stage { get; init; }

    /// <summary>Extractor version that produced it.</summary>
    public required string ExtractorVersion { get; init; }

    /// <summary>Directory the archives were read from.</summary>
    public required string SourceRoot { get; init; }

    /// <summary>Directory entries were written to, or null when nothing was written.</summary>
    public string? OutputRoot { get; init; }

    /// <summary>Totals across every archive.</summary>
    public required IReadOnlyDictionary<string, int> Summary { get; init; }

    /// <summary>Per-archive results, in file-name order.</summary>
    public required IReadOnlyList<BarnArchiveRecord> Archives { get; init; }
}
