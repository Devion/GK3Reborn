using System.Text.Json.Serialization;

namespace GK3Reborn.Content.Manifests;

/// <summary>What became of one of a language's movies.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<LocalizationMovieDisposition>))]
public enum LocalizationMovieDisposition
{
    /// <summary>
    /// The picture is the shared cut and this language supplies only the words.
    /// </summary>
    [JsonStringEnumMemberName("soundtrack")]
    Soundtrack,

    /// <summary>
    /// This language re-cut the footage, so it ships the whole movie.
    /// </summary>
    [JsonStringEnumMemberName("recut")]
    Recut,

    /// <summary>The shared cut already carries this language's sound.</summary>
    [JsonStringEnumMemberName("shared")]
    Shared,

    /// <summary>There is no shared cut of this movie to compare against.</summary>
    [JsonStringEnumMemberName("unmatched")]
    Unmatched,

    /// <summary>It would not convert.</summary>
    [JsonStringEnumMemberName("failed")]
    Failed,
}

/// <summary>One of a language's movies, and what was done with it.</summary>
public sealed record LocalizationMovieEntry
{
    /// <summary>The name the game asks for it by: no directory, no extension.</summary>
    public required string Name { get; init; }

    /// <summary>What became of it.</summary>
    public required LocalizationMovieDisposition Disposition { get; init; }

    /// <summary>How long this language's cut runs.</summary>
    public double Seconds { get; init; }

    /// <summary>How long the shared cut runs, or null when there is none.</summary>
    public double? SharedSeconds { get; init; }

    /// <summary>What was written, relative to the language's directory.</summary>
    public string? Output { get; init; }

    /// <summary>Why it failed, when it did.</summary>
    public string? Error { get; init; }
}

/// <summary>What one language contributes.</summary>
public sealed record LocalizationLanguageEntry
{
    /// <summary>Its ISO 639-1 code, lower case.</summary>
    public required string Language { get; init; }

    /// <summary>The letter its spoken assets carry.</summary>
    public required char Prefix { get; init; }

    /// <summary>What it is called in English.</summary>
    public required string Name { get; init; }

    /// <summary>The code page its text assets are one byte a character in.</summary>
    public int CodePage { get; init; } = 1252;

    /// <summary>Where the release was read from.</summary>
    public required string Source { get; init; }

    /// <summary>How many 1999 assets this language spells or records differently.</summary>
    public int Assets { get; init; }

    /// <summary>How many of them this run wrote.</summary>
    public int Written { get; init; }

    /// <summary>How many were already there, byte for byte.</summary>
    public int Unchanged { get; init; }

    /// <summary>How many stale files a previous run had left, now taken away.</summary>
    public int Removed { get; init; }

    /// <summary>How many were skipped because another language was asked for.</summary>
    public int Skipped { get; init; }

    /// <summary>How many of each extension, for reading at a glance.</summary>
    public IReadOnlyDictionary<string, int> ByExtension { get; init; } =
        new Dictionary<string, int>(StringComparer.Ordinal);

    /// <summary>
    /// The bitmaps this language differs on, without extensions.
    /// </summary>
    public IReadOnlyList<string> Textures { get; init; } = [];

    /// <summary>
    /// The ones of those that are painted onto something in the world.
    /// </summary>
    public IReadOnlyList<string> Surfaces { get; init; } = [];

    /// <summary>Its movies, and what was done with each.</summary>
    public IReadOnlyList<LocalizationMovieEntry> Movies { get; init; } = [];
}

/// <summary>
/// What every language holds that the others do not.
/// </summary>
public sealed record LocalizationSetManifest
{
    /// <summary>Schema version.</summary>
    public required int SchemaVersion { get; init; }

    /// <summary>Which pipeline stage wrote it.</summary>
    public required string Stage { get; init; }

    /// <summary>The language every other one was compared against.</summary>
    public required string Baseline { get; init; }

    /// <summary>Where the per-language releases were read from.</summary>
    public required string SourceRoot { get; init; }

    /// <summary>The installation used as the fallback reference, when there was one.</summary>
    public string? InstallationRoot { get; init; }

    /// <summary>The languages, by code.</summary>
    public IReadOnlyList<LocalizationLanguageEntry> Languages { get; init; } = [];
}
