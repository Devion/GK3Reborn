using System.Text.Json;
using System.Text.RegularExpressions;
using GK3Reborn.Formats;
using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Formats.Rebarn;
using GK3Reborn.Foundation.Diagnostics;

namespace GK3Reborn.Content;

/// <summary>
/// One language's pack, standing between the overrides and the game's own archives.
/// </summary>
/// <remarks>
/// <para>
/// GK3 was localised the most awkward way available: Sierra re-cut every archive per
/// language, so a French disc is not an English disc with a French patch on it — it is a
/// whole second copy of the game in which about fifteen thousand of the forty thousand
/// assets happen to differ. Nothing in the 1999 data says which fifteen thousand.
/// </para>
/// <para>
/// <b>So the port works out the difference once and ships it.</b> A language pack —
/// <c>Reborn_FR.rebarn</c> beside the executable — holds exactly the assets that language
/// spells or records differently, under their 1999 names, and nothing else. Reading it in
/// front of the archives turns any installation into any language: an English install with
/// the French pack plays in French, and a French install with the English pack plays in
/// English. See <c>docs/localization.md</c> for how the set is derived.
/// </para>
/// <para>
/// <b>It is a layer, not a replacement.</b> A name the pack does not hold falls through to
/// the installation, which is what makes a partial pack a perfectly good pack — the same
/// rule <see cref="EnhancedTextures"/> and <see cref="RebarnContent"/> follow, and the
/// reason a missing pack is not an error. What a player loses by not having one is that
/// language, not the game.
/// </para>
/// <para>
/// Three doors, because a localised asset is three different things depending on who is
/// asking. <see cref="Read(string?)"/> answers the 1999 name — the door
/// <see cref="GameArchives"/> puts in front of every script, bitmap, font and recording.
/// <see cref="ReadTexture"/> answers the enhanced texture stack,
/// for the pictures with words painted into them that had to be redone per language.
/// <see cref="OpenMovie"/> and <see cref="OpenMovieSound"/> answer the video, where the
/// picture is usually shared and only the soundtrack is not.
/// </para>
/// </remarks>
public sealed partial class LocalizedContent : IDisposable
{
    /// <summary>The manifest a pack has to carry to be taken for a language pack.</summary>
    /// <remarks>
    /// A file name is a weak claim. <c>Reborn_HD.rebarn</c> matches the pattern and is
    /// somebody's texture mod; a pack that declares itself is one that meant to. The
    /// manifest is written by <c>extract-localized</c> and read on open, and a pack without
    /// one is skipped rather than misread.
    /// </remarks>
    public const string ManifestName = "localization";

    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

    private readonly RebarnArchive _pack;
    private readonly Dictionary<string, RebarnEntry> _entries = new(StringComparer.Ordinal);

    private LocalizedContent(GameLanguage language, RebarnArchive pack)
    {
        Language = language;
        _pack = pack;

        foreach (RebarnEntry entry in pack.Entries)
        {
            _entries[entry.Key] = entry;
        }
    }

    /// <summary>Which language this is.</summary>
    public GameLanguage Language { get; }

    /// <summary>Where the pack was read from.</summary>
    public string Path => _pack.Path;

    /// <summary>How many entries it holds, of every kind.</summary>
    public int Count => _entries.Count;

    /// <summary>How many of them are assets of the 1999 game.</summary>
    public int AssetCount => CountOf(RebarnKind.Localized);

    /// <summary>Whether the pack holds nothing at all.</summary>
    public bool IsEmpty => _entries.Count == 0;

    /// <summary>The file name a language's pack carries.</summary>
    /// <param name="language">The language.</param>
    /// <returns>Its file name, extension included.</returns>
    public static string FileNameOf(GameLanguage language)
    {
        ArgumentNullException.ThrowIfNull(language);
        return $"Reborn_{language.FileCode}{RebarnFormat.Extension}";
    }

    /// <summary>
    /// Which languages there are packs for in a directory.
    /// </summary>
    /// <param name="directory">Where the packs are; usually beside the executable.</param>
    /// <returns>
    /// The languages, English first and then in <see cref="GameLanguage.Known"/> order.
    /// English is always among them whether or not a pack exists for it.
    /// </returns>
    /// <remarks>
    /// <para>
    /// English is offered unconditionally because it is what every installation can already
    /// read: the archives answer to the English spellings under every locale Sierra
    /// shipped, so a player with no packs at all still has one language rather than none.
    /// </para>
    /// <para>
    /// The name is matched and the pack is then <em>opened</em> to see whether it declares
    /// itself — a listing that offered a language the pack turns out not to hold would put
    /// a row in the menu that does nothing when chosen.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<GameLanguage> Available(string directory)
    {
        ArgumentNullException.ThrowIfNull(directory);

        List<GameLanguage> found = [GameLanguage.Default];

        if (!System.IO.Directory.Exists(directory))
        {
            return found;
        }

        foreach (GameLanguage language in GameLanguage.Known)
        {
            if (language == GameLanguage.Default)
            {
                continue;
            }

            string file = System.IO.Path.Combine(directory, FileNameOf(language));

            if (!File.Exists(file))
            {
                continue;
            }

            try
            {
                using RebarnArchive pack = RebarnArchive.Open(file);

                if (Declares(pack, language))
                {
                    found.Add(language);
                }
            }
            catch (Exception ex) when (ex is FormatParseException or IOException)
            {
                // A pack that will not open is a pack that is not offered. Reported when it
                // is actually asked for, in Open below, rather than here: a listing runs
                // every time the menu is drawn.
            }
        }

        return found;
    }

    /// <summary>Opens one language's pack.</summary>
    /// <param name="directory">Where the packs are; usually beside the executable.</param>
    /// <param name="language">Which language to read.</param>
    /// <param name="diagnostics">Receives a diagnostic when the pack will not open.</param>
    /// <returns>The layer, or null when there is no pack for that language.</returns>
    /// <remarks>
    /// Null rather than an empty layer, and null all the way down: every reader below tests
    /// this to decide whether the localisation door exists at all, so an empty one handed
    /// out instead would have each of them consulting a dictionary that can never answer,
    /// on the path of every asset the game reads.
    /// </remarks>
    public static LocalizedContent? Open(
        string directory, GameLanguage language, DiagnosticBag? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(language);

        string file = System.IO.Path.Combine(directory, FileNameOf(language));

        if (!File.Exists(file))
        {
            return null;
        }

        RebarnArchive? pack = null;

        try
        {
            pack = RebarnArchive.Open(file);

            if (!Declares(pack, language))
            {
                diagnostics?.Add(new Diagnostic(
                    "GK3R1190",
                    DiagnosticSeverity.Warning,
                    $"{System.IO.Path.GetFileName(file)} does not declare itself a "
                    + $"{language.Name} pack, so it is not read as one.",
                    file,
                    null,
                    $"a {ManifestName} manifest naming {language.Code}",
                    "none, or another language",
                    "Produce it again with `extract-localized`, or take it out of the "
                    + "directory if it is somebody else's pack."));

                pack.Dispose();
                return null;
            }

            var content = new LocalizedContent(language, pack);

            return content.IsEmpty ? null : content;
        }
        catch (Exception ex) when (ex is FormatParseException or IOException)
        {
            pack?.Dispose();

            diagnostics?.Add(new Diagnostic(
                "GK3R1191",
                DiagnosticSeverity.Warning,
                $"The {language.Name} pack will not open, so the game is read in the "
                + $"language the installation holds: {ex.Message}",
                file,
                null,
                "a readable ReBarn pack",
                ex.GetType().Name,
                "Produce it again with `extract-localized` and `pack-content`."));

            return null;
        }
    }

    /// <summary>Whether a pack says it is this language's.</summary>
    private static bool Declares(RebarnArchive pack, GameLanguage language)
    {
        if (pack.Read(RebarnKind.Manifest, ManifestName) is not { } bytes)
        {
            return false;
        }

        try
        {
            LocalizationManifest? manifest =
                JsonSerializer.Deserialize<LocalizationManifest>(bytes, Json);

            return string.Equals(
                manifest?.Language, language.Code, StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Whether the pack holds an asset of the 1999 game.</summary>
    /// <param name="name">Its whole name, extension included.</param>
    /// <returns>True when it does.</returns>
    public bool HasArchive(string? name) =>
        name is { Length: > 0 } &&
        _entries.ContainsKey(RebarnFormat.Key(RebarnKind.Localized, name));

    /// <summary>Reads an asset of the 1999 game.</summary>
    /// <param name="name">Its whole name, extension included.</param>
    /// <returns>Its bytes, or null when the pack does not hold it.</returns>
    /// <remarks>
    /// The whole name, because that is how the game asks. <c>FSTRINGS.TXT</c> and
    /// <c>ESTRINGS.TXT</c> are different files, <c>A0NQIB44.QR1</c> and <c>.QR2</c> are
    /// different recordings of different lines, and <c>GAB_FACE.BMP</c> must not answer a
    /// question about <c>GAB_FACE.MOD</c>.
    /// </remarks>
    public byte[]? Read(string? name) =>
        name is { Length: > 0 } &&
        _entries.TryGetValue(RebarnFormat.Key(RebarnKind.Localized, name), out RebarnEntry? entry)
            ? _pack.Read(entry)
            : null;

    /// <summary>Every 1999 name the pack holds, in a stable order.</summary>
    public IReadOnlyList<string> ArchiveNames => Names(RebarnKind.Localized);

    /// <summary>How many entries of one kind there are.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>The count.</returns>
    public int CountOf(RebarnKind kind) => _entries.Values.Count(e => e.Kind == kind);

    /// <summary>Every name of one kind, in a stable order.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>
    /// The names, keeping their extension for the kinds whose key does.
    /// </returns>
    public IReadOnlyList<string> Names(RebarnKind kind) =>
        [.. _entries.Values
            .Where(e => e.Kind == kind)
            .Select(e => kind is RebarnKind.Localized or RebarnKind.Audio
                ? System.IO.Path.GetFileName(e.Name)
                : System.IO.Path.GetFileNameWithoutExtension(e.Name))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)];

    /// <summary>Whether the pack holds an entry of some other kind.</summary>
    /// <param name="kind">What it is for.</param>
    /// <param name="name">Its name.</param>
    /// <returns>True when it does.</returns>
    public bool Has(RebarnKind kind, string? name) =>
        name is { Length: > 0 } && _entries.ContainsKey(RebarnFormat.Key(kind, name));

    /// <summary>
    /// Reads a block-compressed texture this language needs a different picture for.
    /// </summary>
    /// <param name="kind">Which set to read from; colour by default.</param>
    /// <param name="name">The colour texture's name, which every set is keyed by.</param>
    /// <param name="diagnostics">Receives a diagnostic when one will not read.</param>
    /// <returns>The texture, or null when this language does not redo it.</returns>
    /// <remarks>
    /// The words painted into a picture are the reason this exists: a road sign, a
    /// newspaper, the labels on Sidney's buttons. Most of GK3's textures carry no words and
    /// are shared, so this set is small and every name in it is a decision somebody made.
    /// <para>
    /// Blocks point into the memory-mapped pack, exactly as
    /// <see cref="RebarnContent.ReadTexture"/>'s do, and stay valid for the life of this
    /// object — which for the game is the life of the process.
    /// </para>
    /// </remarks>
    public CompressedImage? ReadTexture(
        RebarnKind kind, string name, DiagnosticBag? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (!_entries.TryGetValue(RebarnFormat.Key(kind, name), out RebarnEntry? entry))
        {
            return null;
        }

        try
        {
            return DdsFile.Read(_pack.ReadMapped(entry), entry.Name);
        }
        catch (Exception ex) when (ex is FormatParseException or IOException)
        {
            diagnostics?.Add(new Diagnostic(
                "GK3R1192",
                DiagnosticSeverity.Warning,
                $"The {Language.Name} {kind} for {name} will not load, so the shared one is "
                + $"used instead: {ex.Message}",
                _pack.Path,
                entry.Offset,
                "a readable DDS",
                ex.GetType().Name,
                "Produce the pack again with `pack-content`."));

            return null;
        }
    }

    /// <summary>Opens a movie this language has its own picture for.</summary>
    /// <param name="name">Its name, with or without an extension.</param>
    /// <returns>A seekable stream, or null when the shared picture serves.</returns>
    /// <remarks>
    /// Rare and deliberate. Four of GK3's sixteen spoken movies are a different cut in
    /// French — <c>day3-3</c> runs 430 seconds in English and 153 in French — so a
    /// soundtrack laid over the shared picture would drift apart within seconds. Where the
    /// two are the same length the picture is shared and only
    /// <see cref="OpenMovieSound"/> answers.
    /// </remarks>
    public Stream? OpenMovie(string? name) => OpenEntry(RebarnKind.Video, name);

    /// <summary>Opens this language's soundtrack for a shared movie.</summary>
    /// <param name="name">The movie's name, with or without an extension.</param>
    /// <returns>A seekable stream, or null when the movie's own sound serves.</returns>
    public Stream? OpenMovieSound(string? name) => OpenEntry(RebarnKind.MovieAudio, name);

    /// <summary>Whether this language redoes a movie's sound.</summary>
    /// <param name="name">The movie's name.</param>
    /// <returns>True when it does.</returns>
    public bool HasMovieSound(string? name) => Has(RebarnKind.MovieAudio, name);

    private MappedStream? OpenEntry(RebarnKind kind, string? name)
    {
        if (name is not { Length: > 0 } ||
            !_entries.TryGetValue(RebarnFormat.Key(kind, name), out RebarnEntry? entry))
        {
            return null;
        }

        return new MappedStream(_pack.ReadMapped(entry));
    }

    /// <summary>Where an entry would be read from, for saying so out loud.</summary>
    /// <param name="kind">What it is for.</param>
    /// <param name="name">Its name.</param>
    /// <returns>A description, or null when the pack does not hold it.</returns>
    public string? SourceOf(RebarnKind kind, string? name) =>
        name is { Length: > 0 } &&
        _entries.TryGetValue(RebarnFormat.Key(kind, name), out RebarnEntry? entry)
            ? $"{System.IO.Path.GetFileName(_pack.Path)}:{entry.Name}"
            : null;

    /// <summary>A one-line summary of what is open, for a startup report.</summary>
    /// <returns>The summary.</returns>
    public string Describe()
    {
        IEnumerable<string> parts = _entries.Values
            .GroupBy(e => e.Kind)
            .OrderBy(g => g.Key)
            .Select(g => $"{g.Count()} {RebarnFormat.DirectoryOf(g.Key)}");

        return $"{Language.Name} ({Language.Code}), {_entries.Count} entries "
            + $"({_pack.Length / (1024.0 * 1024):F0} MB): {string.Join(", ", parts)}";
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _pack.Dispose();
        _entries.Clear();
    }

    /// <summary>Matches the file name a language pack carries.</summary>
    /// <remarks>
    /// Only used to say what a directory looks like it holds; whether a file <em>is</em> a
    /// language pack is decided by its manifest, not by its name.
    /// </remarks>
    [GeneratedRegex(@"^Reborn_(?<code>[A-Za-z]{2})\.rebarn$", RegexOptions.IgnoreCase)]
    public static partial Regex FileNamePattern();
}

/// <summary>What a language pack says about itself.</summary>
/// <param name="Language">The ISO 639-1 code, lower case.</param>
/// <param name="Prefix">The letter its spoken assets carry.</param>
/// <param name="Name">What the language is called in English.</param>
/// <param name="Assets">How many 1999 assets the pack replaces.</param>
/// <param name="Source">Where the set was derived from, for a person reading the pack.</param>
/// <param name="BuiltUtc">When it was derived, in round-trip form.</param>
/// <remarks>
/// Small on purpose. It exists to answer one question on open — "is this a pack for the
/// language I asked for" — and everything past that is for somebody looking at the file
/// with <c>pack-extract</c> rather than for the loader.
/// </remarks>
public sealed record LocalizationManifest(
    string Language,
    char Prefix,
    string Name,
    int Assets,
    string? Source = null,
    string? BuiltUtc = null);
