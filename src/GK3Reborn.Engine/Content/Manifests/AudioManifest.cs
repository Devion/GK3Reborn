namespace GK3Reborn.Content.Manifests;

/// <summary>One original sound prepared for restoration.</summary>
public sealed record AudioAssetRecord
{
    /// <summary>The complete archive identity, including dialogue sequence suffix.</summary>
    public required string Name { get; init; }

    /// <summary>The Barn volume that supplies the effective entry.</summary>
    public required string Archive { get; init; }

    /// <summary><c>dialogue</c> for a YAK voice-over; otherwise <c>sfx</c>.</summary>
    public required string Lane { get; init; }

    /// <summary>Untouched RIFF path, relative to the workspace.</summary>
    public required string RawPath { get; init; }

    /// <summary>Decoded PCM path, relative to the workspace, or null on failure.</summary>
    public string? NormalizedPath { get; init; }

    /// <summary>SHA-256 of the untouched archive bytes.</summary>
    public required string SourceHash { get; init; }

    /// <summary>SHA-256 of the normalized PCM WAV, or null on failure.</summary>
    public string? NormalizedHash { get; init; }

    /// <summary>Channel count after decode.</summary>
    public int Channels { get; init; }

    /// <summary>Source sample rate after decode.</summary>
    public int SampleRate { get; init; }

    /// <summary>Decoded frames, with a stereo pair counted once.</summary>
    public int Frames { get; init; }

    /// <summary>Duration in seconds.</summary>
    public double Seconds { get; init; }

    /// <summary>YAK files which identify this recording as dialogue.</summary>
    public required IReadOnlyList<string> Yaks { get; init; }

    /// <summary>Speaker nouns derived from those YAK captions.</summary>
    public required IReadOnlyList<string> Speakers { get; init; }

    /// <summary>Caption text derived from those YAK files.</summary>
    public required IReadOnlyList<string> Captions { get; init; }

    /// <summary>Why normalization failed, or null when it succeeded.</summary>
    public string? Error { get; init; }
}

/// <summary>The audio extraction and normalization manifest.</summary>
public sealed record AudioManifest
{
    /// <summary>Schema version.</summary>
    public required int SchemaVersion { get; init; }

    /// <summary>Pipeline stage.</summary>
    public required string Stage { get; init; }

    /// <summary>Implementation version.</summary>
    public required string StageVersion { get; init; }

    /// <summary>Original game Data directory.</summary>
    public required string SourceRoot { get; init; }

    /// <summary>Counts by disposition.</summary>
    public required IReadOnlyDictionary<string, int> Summary { get; init; }

    /// <summary>YAK sound names which do not resolve to audio in the effective archives.</summary>
    public required IReadOnlyList<string> UnresolvedDialogueReferences { get; init; }

    /// <summary>Every effective audio asset in stable name order.</summary>
    public required IReadOnlyList<AudioAssetRecord> Assets { get; init; }
}
