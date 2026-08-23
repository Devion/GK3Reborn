using System.Text.Json.Serialization;

namespace GK3Reborn.Content.Manifests;

/// <summary>Outcome of converting one source video.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<VideoEntryStatus>))]
public enum VideoEntryStatus
{
    /// <summary>Converted and every validation check passed.</summary>
    [JsonStringEnumMemberName("converted")]
    Converted,

    /// <summary>Converted, but at least one validation check failed.</summary>
    [JsonStringEnumMemberName("converted-with-warnings")]
    ConvertedWithWarnings,

    /// <summary>The source could not be identified as a decodable container.</summary>
    [JsonStringEnumMemberName("unreadable-source")]
    UnreadableSource,

    /// <summary>The source decoded but the encoder failed.</summary>
    [JsonStringEnumMemberName("conversion-failed")]
    ConversionFailed,
}

/// <summary>Media properties of one video file.</summary>
public sealed record VideoMedia
{
    /// <summary>Path relative to the manifest's root, or the source file name.</summary>
    public required string File { get; init; }

    /// <summary>Size in bytes.</summary>
    public required long Bytes { get; init; }

    /// <summary>SHA-256 of the file.</summary>
    public required string Sha256 { get; init; }

    /// <summary>Container format reported by the prober.</summary>
    public string? Container { get; init; }

    /// <summary>Video codec name.</summary>
    public string? VideoCodec { get; init; }

    /// <summary>Frame width in pixels.</summary>
    public int? Width { get; init; }

    /// <summary>Frame height in pixels.</summary>
    public int? Height { get; init; }

    /// <summary>Frame rate as an exact rational string, e.g. "30/1".</summary>
    public string? FrameRate { get; init; }

    /// <summary>Duration in seconds.</summary>
    public double? DurationSeconds { get; init; }

    /// <summary>Audio codec name, or null when there is no audio stream.</summary>
    public string? AudioCodec { get; init; }

    /// <summary>Audio sample rate in Hz.</summary>
    public int? AudioSampleRate { get; init; }

    /// <summary>Audio channel count.</summary>
    public int? AudioChannels { get; init; }
}

/// <summary>Validation results comparing an output against its source.</summary>
public sealed record VideoValidation
{
    /// <summary>Output frame size equals the source frame size.</summary>
    public required bool DimensionsMatch { get; init; }

    /// <summary>Output frame rate equals the source frame rate exactly.</summary>
    public required bool FrameRateMatch { get; init; }

    /// <summary>Output duration minus source duration, in seconds.</summary>
    public required double DurationDriftSeconds { get; init; }

    /// <summary>Drift is within the accepted tolerance.</summary>
    public required bool DurationWithinTolerance { get; init; }

    /// <summary>An audio stream exists in the output exactly when one existed in the source.</summary>
    public required bool AudioPreserved { get; init; }

    /// <summary>True when every boolean check passed.</summary>
    [JsonIgnore]
    public bool AllPassed =>
        DimensionsMatch && FrameRateMatch && DurationWithinTolerance && AudioPreserved;
}

/// <summary>The exact command used to produce an output, for reproducibility.</summary>
public sealed record VideoRecipe
{
    /// <summary>Converter identifier.</summary>
    public required string Converter { get; init; }

    /// <summary>Converter version; bumping it invalidates cached outputs.</summary>
    public required string ConverterVersion { get; init; }

    /// <summary>Full argument list, with the output path elided.</summary>
    public required IReadOnlyList<string> Arguments { get; init; }
}

/// <summary>Why an entry could not be produced.</summary>
public sealed record VideoDiagnostic
{
    /// <summary>Error text from the prober, if probing failed.</summary>
    public string? ProbeError { get; init; }

    /// <summary>Error text from the encoder, if encoding failed.</summary>
    public string? FfmpegError { get; init; }

    /// <summary>What the user should do about it.</summary>
    public string? Remediation { get; init; }
}

/// <summary>One video, addressed by its logical id.</summary>
public sealed record VideoEntry
{
    /// <summary>
    /// Uppercase base name with no extension. GK3 data references videos without an
    /// extension - GEngine's <c>VideoHelper</c> strips it deliberately - so the
    /// logical id is the bare name.
    /// </summary>
    public required string LogicalId { get; init; }

    /// <summary>Conversion outcome.</summary>
    public required VideoEntryStatus Status { get; init; }

    /// <summary>The original file this entry came from.</summary>
    public required VideoMedia Source { get; init; }

    /// <summary>The converted file, when one was produced.</summary>
    public VideoMedia? Output { get; init; }

    /// <summary>Validation results, when conversion ran.</summary>
    public VideoValidation? Validation { get; init; }

    /// <summary>The recipe used, when conversion ran.</summary>
    public VideoRecipe? Recipe { get; init; }

    /// <summary>Failure detail, when the entry did not convert.</summary>
    public VideoDiagnostic? Diagnostic { get; init; }

    /// <summary>True when this entry produced a usable runtime file.</summary>
    [JsonIgnore]
    public bool IsPlayable =>
        Output is not null &&
        Status is VideoEntryStatus.Converted or VideoEntryStatus.ConvertedWithWarnings;
}

/// <summary>The C7 video stage manifest.</summary>
public sealed record VideoManifest
{
    /// <summary>Schema version of this manifest.</summary>
    public required int SchemaVersion { get; init; }

    /// <summary>Pipeline stage that produced it.</summary>
    public required string Stage { get; init; }

    /// <summary>Converter version that produced it.</summary>
    public required string ConverterVersion { get; init; }

    /// <summary>Directory the sources were read from.</summary>
    public required string SourceRoot { get; init; }

    /// <summary>Directory the outputs were written to.</summary>
    public required string OutputRoot { get; init; }

    /// <summary>Counts by status, plus a total.</summary>
    public required IReadOnlyDictionary<string, int> Summary { get; init; }

    /// <summary>All entries, ordered by logical id.</summary>
    public required IReadOnlyList<VideoEntry> Entries { get; init; }
}
