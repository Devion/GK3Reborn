using System.Globalization;
using System.Text.Json;
using GK3Reborn.Content;
using GK3Reborn.Content.Manifests;
using GK3Reborn.Formats;
using GK3Reborn.Formats.Barn;
using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Formats.Rebarn;
using GK3Reborn.Foundation;
using GK3Reborn.Foundation.Diagnostics;

namespace GK3Reborn.Tools.Stages;

/// <summary>
/// Works out which of GK3's assets differ between languages, and writes each language's
/// own copies into the workspace for packing.
/// </summary>
/// <remarks>
/// <para>
/// <b>Sierra localised GK3 by re-cutting every archive.</b> A French disc is not an English
/// disc with a French patch on it; it is a whole second copy of the game, and nothing in
/// the data says which of its forty thousand assets are actually different. That is what
/// this stage is for: given one directory per language, it compares them and writes out
/// only what differs, so a language ships as a few hundred megabytes rather than as a
/// second installation.
/// </para>
/// <para>
/// <b>Two ways a language differs, and they need different treatment.</b> Most assets keep
/// their 1999 name and change their contents — the same <c>27KASHAF.BMP</c> with different
/// words painted on it, the same <c>A014ED3S.6J1</c> with a different actor saying a
/// different sentence. A few change their <em>name</em> instead: the string table is
/// <c>ESTRINGS.TXT</c> in English and <c>FSTRINGS.TXT</c> in French, and every line of
/// dialogue's lip-sync and every scripted moment carries the language's letter in front of
/// it. Those families are listed in <see cref="PrefixedExtensions"/>, and they are the
/// reason the set is worked out per <em>canonical</em> name rather than per file name: the
/// English pack has to hold <c>E014ED3S6J1.YAK</c> exactly where the French one holds
/// <c>F014ED3S6J1.YAK</c>, and a comparison by file name would see two unrelated files.
/// </para>
/// <para>
/// <b>Bitmaps are compared as pictures, not as bytes.</b> GK3's own container is a raw
/// RGB565 bitmap with an eight-byte header, and a dumped localisation may well have been
/// written back out as an ordinary Windows bitmap. Those two files never compare equal and
/// always decode to the same picture, so a byte comparison would declare every bitmap in
/// the game localised and the packs would be six times the size they need to be.
/// </para>
/// <para>
/// <b>What it will not do is guess.</b> An asset present in one language and absent from
/// every other is reported rather than packed silently: it is usually a dump that was taken
/// with a different filter, occasionally a genuine difference between two builds of the
/// game, and the two are not distinguishable from here. See <c>docs/localization.md</c>.
/// </para>
/// </remarks>
public sealed class LocalizationExtractStage
{
    /// <summary>The manifest schema this stage writes.</summary>
    public const int SchemaVersion = 1;

    /// <summary>
    /// The asset families whose <em>name</em> carries the language's letter.
    /// </summary>
    /// <remarks>
    /// <c>.YAK</c> is a line of dialogue's lip-sync and there are about 7,400 of them;
    /// <c>.MOM</c> is a scripted moment and there are 38. The string table is the third and
    /// is handled by name rather than by extension, because <c>.TXT</c> as a whole is not a
    /// prefixed family — <c>ESIDNEY.TXT</c> keeps its <c>E</c> in the French release and
    /// changes its contents, exactly like a bitmap.
    /// </remarks>
    public static readonly string[] PrefixedExtensions = [".YAK", ".MOM"];

    /// <summary>The string table's name, without the language's letter.</summary>
    public const string StringTableSuffix = "STRINGS.TXT";

    /// <summary>The character standing in for a language's letter in a canonical name.</summary>
    private const char AnyPrefix = '*';

    /// <summary>
    /// Below this many differing assets, a release is not a translation of anything.
    /// </summary>
    /// <remarks>
    /// The smallest real localisation in the corpus changes 8,150 assets - Spanish, which
    /// re-recorded nothing and only retranslated its text and its pictures. Two hundred is
    /// far below anything a translation could be and far above the handful of incidental
    /// differences between two pressings of the same disc.
    /// </remarks>
    private const int Threadbare = 200;

    private readonly Action<string> _log;

    /// <summary>Creates the stage.</summary>
    /// <param name="log">Where progress is written.</param>
    public LocalizationExtractStage(Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
    }

    /// <summary>Compares the languages and writes each one's own assets out.</summary>
    /// <param name="localizedDirectory">
    /// A directory holding one subdirectory per language, named for its ISO 639-1 code:
    /// <c>EN</c>, <c>FR</c>. Each may hold <c>*.brn</c> archives, or a dumped tree of loose
    /// files — both are read, and a tree may be nested to any depth.
    /// </param>
    /// <param name="sourceDirectory">
    /// The installed game's <c>Data</c> directory, read and never written. It is the
    /// fallback for a name the baseline language's own directory does not hold.
    /// </param>
    /// <param name="workspaceDirectory">The content workspace root.</param>
    /// <param name="only">Only this language, by code, or null for all of them.</param>
    /// <param name="video">
    /// The pass that separates each language's cinematic sound from the shared picture, or
    /// null to leave the movies alone. Null when FFmpeg was not found, and null is the
    /// right answer there rather than a failure: the assets are the bulk of a localisation
    /// and they need no media tools at all.
    /// </param>
    /// <param name="force">Write every asset again rather than skipping identical ones.</param>
    /// <param name="dryRun">Report what would happen and write nothing.</param>
    /// <param name="diagnostics">Receives stage-level diagnostics.</param>
    /// <returns>The manifest, or null when there was nothing to compare.</returns>
    public LocalizationSetManifest? Run(
        string localizedDirectory,
        string sourceDirectory,
        string workspaceDirectory,
        string? only,
        LocalizationVideoStage? video,
        bool force,
        bool dryRun,
        DiagnosticBag diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentException.ThrowIfNullOrWhiteSpace(localizedDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceDirectory);

        if (!Directory.Exists(localizedDirectory))
        {
            diagnostics.Add(new Diagnostic(
                "GK3R2300", DiagnosticSeverity.Error,
                "No localisation sources were found.",
                localizedDirectory, null,
                "a directory with one subdirectory per language (EN, FR, ...)",
                "no such directory",
                "Point --localized at the directory holding the per-language releases."));

            return null;
        }

        List<LocaleSource> sources = Discover(localizedDirectory, diagnostics);

        if (sources.Count == 0)
        {
            diagnostics.Add(new Diagnostic(
                "GK3R2301", DiagnosticSeverity.Error,
                "None of the subdirectories names a language GK3 was published in.",
                localizedDirectory, null,
                "subdirectories named EN, FR, DE, IT, ES, PT, RU or PL",
                string.Join(", ", Directory.EnumerateDirectories(localizedDirectory)
                    .Select(Path.GetFileName)),
                "Rename them to their ISO 639-1 codes."));

            return null;
        }

        // The installation, which is the reference every language is measured against for
        // the names its own directory happens not to hold. Opened read-only; the stage
        // never writes to it.
        using GameArchives? installed =
            sourceDirectory is { Length: > 0 } && Directory.Exists(sourceDirectory)
                ? GameArchives.Open(sourceDirectory)
                : null;

        if (installed is null)
        {
            diagnostics.Add(new Diagnostic(
                "GK3R2302", DiagnosticSeverity.Warning,
                "No installation was given, so a language is compared only against the "
                + "other languages' own directories.",
                sourceDirectory, null,
                "the game's Data directory",
                sourceDirectory is { Length: > 0 } ? "no such directory" : "not given",
                "Pass --source to compare against the game you have installed as well."));
        }

        _log($"Languages: {string.Join(", ", sources.Select(s => $"{s.Language.Code} ({s.Count})"))}");

        // English is the baseline because it is the release every other one was made from,
        // and because it is the language the installed game most likely is. When it is not
        // among the directories the first one given stands in for it, which still gives a
        // usable answer -- "what French has that Italian does not" -- and says so.
        LocaleSource baseline =
            sources.FirstOrDefault(s => s.Language == GameLanguage.Default) ?? sources[0];

        if (baseline.Language != GameLanguage.Default)
        {
            diagnostics.Add(new Diagnostic(
                "GK3R2303", DiagnosticSeverity.Warning,
                $"There is no English directory, so {baseline.Language.Name} is used as the "
                + "reference every other language is compared against.",
                localizedDirectory, null, "an EN directory", "none",
                "Add the English release so the comparison is against the one the others "
                + "were made from."));
        }

        // Which lines the restoration already supplies. Those are left out of the baseline
        // language's set so they fall through to the restored master rather than being
        // shadowed by the 1999 recording of the same line -- see SoundLibrary.ReadLayered.
        HashSet<string> restored = RestoredAudio(workspaceDirectory);

        if (restored.Count > 0)
        {
            _log($"Restored audio: {restored.Count} recording(s) left to enhanced/audio");
        }

        var byLanguage = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var canonical = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (LocaleSource source in sources)
        {
            byLanguage[source.Language.Code] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        // Pass one: what each non-baseline language spells or records differently.
        foreach (LocaleSource source in sources.Where(s => s != baseline))
        {
            HashSet<string> mine = byLanguage[source.Language.Code];
            int same = 0;

            foreach (string name in source.Names)
            {
                byte[]? theirs = Reference(name, source.Language, baseline, installed);

                if (theirs is not null && Same(name, source.Read(name), theirs))
                {
                    same++;
                    continue;
                }

                mine.Add(name);
                canonical.Add(Canonical(name, source.Language));
            }

            _log($"{source.Language.Code}: {mine.Count} localised, {same} shared with "
                + $"{baseline.Language.Code}");

            // A release that differs on a handful of files out of forty thousand is not a
            // translation of anything - it is the same game under a different label, or a
            // download whose localised half is somewhere else. Packing it would produce a
            // language the menu offers, the player chooses, and nothing whatever happens.
            //
            // The Chinese release found on 2026-09-05 was exactly this: eight archives
            // byte-identical to the English installation, differing on four wood textures
            // that are not text at all.
            if (mine.Count < Threadbare && same > 0)
            {
                diagnostics.Add(new Diagnostic(
                    "GK3R2309", DiagnosticSeverity.Warning,
                    $"The {source.Language.Name} release differs from "
                    + $"{baseline.Language.Name} in only {mine.Count} of {source.Count} "
                    + "assets, which is not a translation of anything. Its archives are "
                    + "very likely the same game under a different label.",
                    source.Root, null,
                    "thousands of assets that differ",
                    $"{mine.Count}",
                    "Check that the release is the localised one, and that whatever makes "
                    + "it localised is inside its archives rather than in a patched "
                    + "executable beside them. It is packed either way; look at "
                    + "reports/localization.md to see what it actually holds."));
            }
        }

        // Pass two: the baseline's own spelling of everything the others localised. It is
        // derived rather than compared, because the baseline has nothing to be compared
        // against -- it *is* the comparison -- and because a French game and an English one
        // must answer the same question with the same asset under two different names.
        HashSet<string> baseNames = byLanguage[baseline.Language.Code];
        List<string> missing = [];

        foreach (string key in canonical)
        {
            string name = Spell(key, baseline.Language);

            if (baseline.Has(name) || installed?.Exists(name) == true)
            {
                baseNames.Add(name);
            }
            else
            {
                missing.Add(name);
            }
        }

        _log($"{baseline.Language.Code}: {baseNames.Count} localised (derived from the others)");

        if (missing.Count > 0)
        {
            // Not an error. Seven thousand of these names are lip-sync files and a release
            // may simply have a line the other does not, but it is worth saying how many
            // and which: a large number means the prefix rule above matched something it
            // should not have.
            diagnostics.Add(new Diagnostic(
                "GK3R2304", DiagnosticSeverity.Warning,
                $"{missing.Count} asset(s) another language localises have no "
                + $"{baseline.Language.Name} counterpart, so that language falls back to "
                + "the installation for them.",
                localizedDirectory, null,
                $"a {baseline.Language.Name} spelling of each",
                string.Join(", ", missing.Take(8)) + (missing.Count > 8 ? ", ..." : string.Empty),
                "Check reports/localization.md; a large number means a family is being "
                + "treated as name-prefixed when it is not."));
        }

        // And the restoration's own lines, taken back out of the baseline set only.
        int handedBack = baseNames.RemoveWhere(restored.Contains);

        if (handedBack > 0)
        {
            _log($"{baseline.Language.Code}: {handedBack} left to the restored masters");
        }

        List<LocalizationLanguageEntry> entries = [];

        foreach (LocaleSource source in sources)
        {
            LocalizationLanguageEntry entry = Write(
                source, byLanguage[source.Language.Code], installed, workspaceDirectory,
                only, force, dryRun, diagnostics);

            bool wanted = only is not { Length: > 0 } ||
                string.Equals(only, source.Language.Code, StringComparison.OrdinalIgnoreCase);

            if (video is not null && wanted)
            {
                _log($"{source.Language.FileCode}: movies");

                // The installation first, because it is the release the shared pictures
                // were imported from and it carries all twenty-eight of them; the baseline
                // language's own directory second, for a run given no installation. A
                // language compared against itself finds every picture and every soundtrack
                // identical and needs nothing, which is the right answer without a special
                // case for it.
                entry = entry with
                {
                    Movies = video.Run(
                        source.MediaRoot,
                        [sourceDirectory, baseline.MediaRoot],
                        workspaceDirectory,
                        source.Language,
                        force,
                        dryRun),
                };
            }

            entries.Add(entry);
        }

        // Said once, plainly, because it is the one thing about a localised run that cannot
        // be seen: a cutscene with the wrong words over it looks exactly like one with the
        // right words, and the count is what says whether the pass did anything.
        foreach (LocalizationLanguageEntry entry in entries.Where(
                     e => e.Movies.Any(m => m.Disposition == LocalizationMovieDisposition.Unmatched)))
        {
            diagnostics.Add(new Diagnostic(
                "GK3R2307", DiagnosticSeverity.Warning,
                $"{entry.Name} has movies with no shared cut to put a soundtrack over, so "
                + "they are heard in whatever language the shared picture was imported from.",
                localizedDirectory, null,
                "a shared import of each",
                string.Join(", ", entry.Movies
                    .Where(m => m.Disposition == LocalizationMovieDisposition.Unmatched)
                    .Select(m => m.Name)),
                "Run `import-video` over the installation first, or accept that those "
                + "cutscenes are not localised."));
        }

        var manifest = new LocalizationSetManifest
        {
            SchemaVersion = SchemaVersion,
            Stage = "C3.localization",
            Baseline = baseline.Language.Code,
            SourceRoot = Normalize(localizedDirectory),
            InstallationRoot = installed is null ? null : Normalize(sourceDirectory),
            Languages = [.. entries.OrderBy(e => e.Language, StringComparer.Ordinal)],
        };

        if (!dryRun)
        {
            string manifestDirectory = Path.Combine(workspaceDirectory, "manifests");
            Directory.CreateDirectory(manifestDirectory);

            string path = Path.Combine(manifestDirectory, "localization.json");
            AtomicFile.WriteAllText(
                path, JsonSerializer.Serialize(manifest, ManifestJson.Options) + "\n");
            _log($"manifest: {path}");

            Report(manifest, workspaceDirectory);
        }

        return manifest;
    }

    /// <summary>
    /// Writes one language's assets into the workspace, and describes what it wrote.
    /// </summary>
    /// <remarks>
    /// Into <c>enhanced/localized/&lt;CODE&gt;/localized</c>, which is the directory the
    /// packer takes <see cref="RebarnKind.Localized"/> from — the same
    /// kind-named layout <c>pack-extract</c> writes and <c>overrides/</c> reads, so a set
    /// can be moved between the three without anything being renamed.
    /// </remarks>
    private LocalizationLanguageEntry Write(
        LocaleSource source,
        HashSet<string> names,
        GameArchives? installed,
        string workspace,
        string? only,
        bool force,
        bool dryRun,
        DiagnosticBag diagnostics)
    {
        string code = source.Language.FileCode;
        string root = Path.Combine(workspace, "enhanced", "localized", code);
        string assets = Path.Combine(root, RebarnFormat.DirectoryOf(RebarnKind.Localized));

        var byExtension = new SortedDictionary<string, int>(StringComparer.Ordinal);

        foreach (string name in names)
        {
            string family = Family(name);
            byExtension[family] = byExtension.GetValueOrDefault(family) + 1;
        }

        var entry = new LocalizationLanguageEntry
        {
            Language = source.Language.Code,
            Prefix = source.Language.Prefix,
            Name = source.Language.Name,
            CodePage = source.Language.CodePage,
            Source = Normalize(source.Root),
            Assets = names.Count,
            ByExtension = byExtension,
            Textures = [.. names
                .Where(n => Path.GetExtension(n).Equals(".BMP", StringComparison.OrdinalIgnoreCase))
                .Select(Path.GetFileNameWithoutExtension)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)!],
        };

        // Of those, the ones that are actually painted onto something in the world. See
        // Surfaces below for why the other six hundred are not worth anybody's afternoon.
        entry = entry with { Surfaces = Surfaces(workspace, entry.Textures) };

        if (only is { Length: > 0 } &&
            !string.Equals(only, source.Language.Code, StringComparison.OrdinalIgnoreCase))
        {
            _log($"{code}: skipped (--language {only})");
            return entry with { Written = 0, Skipped = names.Count };
        }

        if (dryRun)
        {
            _log($"{code}: would write {names.Count} asset(s) to {assets}");
            _log($"{code}: {entry.Surfaces.Count} of its {entry.Textures.Count} bitmaps are "
                + "painted onto something in the world");

            return entry;
        }

        Directory.CreateDirectory(assets);

        int written = 0;
        int unchanged = 0;
        int absent = 0;

        foreach (string name in names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            // The language's own copy where it has one, and the installation's otherwise.
            // The second case is ordinary and is what makes the baseline's set work at all:
            // most of English's localised assets are simply the ones the installed game
            // already holds, gathered under the names the other languages disagree about.
            byte[]? bytes = source.Read(name) ?? installed?.Read(name);

            if (bytes is null)
            {
                absent++;
                continue;
            }

            string path = Path.Combine(assets, name);

            if (!force && File.Exists(path) && new FileInfo(path).Length == bytes.Length &&
                File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
            {
                unchanged++;
                continue;
            }

            File.WriteAllBytes(path, bytes);
            written++;
        }

        // Anything left from a previous run that this one no longer claims. Taken away
        // rather than left, because a stale asset in this directory is packed and read: it
        // would stand in front of the installation under a name nothing now believes is
        // localised, and there would be nothing to say why.
        int removed = 0;

        foreach (string stale in Directory.EnumerateFiles(assets))
        {
            if (!names.Contains(Path.GetFileName(stale)))
            {
                File.Delete(stale);
                removed++;
            }
        }

        // What the pack says about itself, which is what LocalizedContent.Open reads to
        // decide whether a file called Reborn_FR.rebarn really is the French pack. Written
        // into the language's own manifests directory so the packer picks it up with
        // everything else rather than needing a special case.
        string manifests = Path.Combine(root, "manifests");
        Directory.CreateDirectory(manifests);

        AtomicFile.WriteAllText(
            Path.Combine(manifests, LocalizedContent.ManifestName + ".json"),
            JsonSerializer.Serialize(
                new LocalizationManifest(
                    source.Language.Code,
                    source.Language.Prefix,
                    source.Language.Name,
                    written + unchanged,
                    Normalize(source.Root),
                    DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)),
                ManifestJson.Options) + "\n");

        _log($"{code}: {written} written, {unchanged} unchanged, {removed} removed -> {assets}");

        if (absent > 0)
        {
            diagnostics.Add(new Diagnostic(
                "GK3R2305", DiagnosticSeverity.Warning,
                $"{absent} of {source.Language.Name}'s localised assets could not be read "
                + "from either its own directory or the installation, so they are left out.",
                source.Root, null, "readable source files", $"{absent} unreadable or absent",
                "Check the release the directory was dumped from."));
        }

        // enhanced/localtextures/<CODE> is not touched, and that is deliberate.
        //
        // It is where a person prunes the hundred candidates down to the ones they actually
        // mean to repaint and then repaints them, so the shape of their decision *is which
        // files are there*. This stage once copied the candidates in when they were missing,
        // which read as helpful and was not: a run put back thirty-four pictures somebody
        // had deliberately deleted, and nothing said so. There is no flag for it now,
        // because a flag is something that gets passed by a script somebody else wrote.
        //
        // The work list is reported instead - `surfaces` in manifests/localization.json -
        // and the packer reads the directory without writing to it.
        return entry with { Written = written, Unchanged = unchanged, Removed = removed };
    }

    /// <summary>
    /// Every extension GK3 uses for something that is not a line of dialogue.
    /// </summary>
    /// <remarks>
    /// The list exists so that everything <em>else</em> can be recognised. GK3 puts a
    /// recording's sequence number where a type would go — <c>A0NQIB44.QR1</c> and
    /// <c>.QR2</c> are two takes of two different lines — so a localisation has about six
    /// and a half thousand distinct "extensions", one or two files each. A report that
    /// listed them would be four thousand rows long and would say nothing.
    /// </remarks>
    private static readonly HashSet<string> KnownExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".BMP", ".MOD", ".ACT", ".ANM", ".YAK", ".MOM", ".MUL", ".BSP", ".SIF", ".SCN",
        ".NVC", ".SHP", ".GAS", ".SEQ", ".FON", ".CUR", ".STK", ".TXT", ".HTM", ".HTML",
        ".WAV", ".CFG", ".TIP", ".DOC", ".MD1", ".EXE", ".DLL", ".ZIP", ".INI",
    };

    /// <summary>Which family of asset a name belongs to, for the report.</summary>
    private static string Family(string name)
    {
        string extension = Path.GetExtension(name).ToUpperInvariant();

        if (extension.Length == 0)
        {
            return "(none)";
        }

        return KnownExtensions.Contains(extension) ? extension : "(dialogue)";
    }

    /// <summary>
    /// Which of a language's bitmaps are painted onto something in the world.
    /// </summary>
    /// <param name="workspace">The content workspace root.</param>
    /// <param name="bitmaps">Every bitmap this language differs on.</param>
    /// <returns>The ones a room, a prop or a character refers to, in a stable order.</returns>
    /// <remarks>
    /// <para>
    /// <b>Six hundred and fifty of the seven hundred and fifty are the 1999 interface.</b>
    /// Sidney's buttons, the options screens, the binocular controls, the toolbar - every one
    /// of them was a picture with a word painted on it, every one of them was localised, and
    /// the port draws none of them: it renders its own interface, with its own text, at the
    /// size of the window. Repainting those would be somebody's month spent on pictures
    /// nothing displays.
    /// </para>
    /// <para>
    /// What is left is about a hundred, and those are the ones that matter: a road sign, a
    /// shop front, a note on a table, a label on a bottle. They are painted onto geometry and
    /// there is no other way to change what they say.
    /// </para>
    /// <para>
    /// The test is the texture plan's own reference count - whether any room, prop or
    /// character names this texture. That is a fact the plan already has, derived from the
    /// whole corpus, and it is a far better answer than any list of name prefixes: nothing
    /// about <c>BLUEAPPLE</c> or <c>ABBEPRNT3</c> says "interface" except that no piece of
    /// geometry has ever asked for it.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<string> Surfaces(
        string workspace, IReadOnlyList<string> bitmaps)
    {
        string path = Path.Combine(workspace, "manifests", "texture-plan.json");

        if (!File.Exists(path))
        {
            // Without the plan there is no way to tell one from the other, and calling them
            // all surfaces would seed a directory with six hundred pictures nobody wants.
            return [];
        }

        TexturePlanManifest? plan;

        try
        {
            plan = JsonSerializer.Deserialize<TexturePlanManifest>(
                File.ReadAllText(path), ManifestJson.Options);
        }
        catch (JsonException)
        {
            return [];
        }

        if (plan is null)
        {
            return [];
        }

        HashSet<string> drawn = new(
            plan.Textures.Where(t => t.Referrers.Count > 0).Select(t => t.Name),
            StringComparer.OrdinalIgnoreCase);

        return [.. bitmaps.Where(drawn.Contains).OrderBy(n => n, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Writes the report a person reads instead of the manifest.</summary>
    private void Report(LocalizationSetManifest manifest, string workspace)
    {
        string directory = Path.Combine(workspace, "reports");
        Directory.CreateDirectory(directory);

        var text = new System.Text.StringBuilder();
        text.AppendLine("# Localisation");
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture,
            $"Compared against **{manifest.Baseline}**, from `{manifest.SourceRoot}`.");
        text.AppendLine();
        text.AppendLine(
            "| Language | Assets | Written | Unchanged | Removed | Bitmaps | On geometry |");
        text.AppendLine("|---|---:|---:|---:|---:|---:|---:|");

        foreach (LocalizationLanguageEntry entry in manifest.Languages)
        {
            text.AppendLine(CultureInfo.InvariantCulture,
                $"| {entry.Name} ({entry.Language}) | {entry.Assets} | {entry.Written} | "
                + $"{entry.Unchanged} | {entry.Removed} | {entry.Textures.Count} | "
                + $"{entry.Surfaces.Count} |");
        }

        foreach (LocalizationLanguageEntry entry in manifest.Languages)
        {
            text.AppendLine();
            text.AppendLine(CultureInfo.InvariantCulture, $"## {entry.Name} ({entry.Language})");
            text.AppendLine();
            text.AppendLine(CultureInfo.InvariantCulture,
                $"Prefix `{entry.Prefix}`, code page {entry.CodePage}, from `{entry.Source}`.");
            text.AppendLine();
            text.AppendLine("| Kind | Count |");
            text.AppendLine("|---|---:|");

            foreach ((string extension, int count) in entry.ByExtension)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"| `{extension}` | {count} |");
            }

            if (entry.Movies.Count > 0)
            {
                text.AppendLine();
                text.AppendLine("| Movie | This language |");
                text.AppendLine("|---|---|");

                foreach (LocalizationMovieEntry movie in entry.Movies)
                {
                    string lengths = movie.SharedSeconds is { } shared
                        ? string.Create(
                            CultureInfo.InvariantCulture,
                            $" ({movie.Seconds:F1}s against {shared:F1}s shared)")
                        : string.Empty;

                    text.AppendLine(CultureInfo.InvariantCulture,
                        $"| {movie.Name} | {movie.Disposition}{lengths} |");
                }
            }
        }

        string path = Path.Combine(directory, "localization.md");
        AtomicFile.WriteAllText(path, text.ToString());
        _log($"report: {path}");
    }

    /// <summary>Which recordings the enhanced audio set already restores.</summary>
    /// <remarks>
    /// Named without their <c>.wav</c> wrapper, because that is how the packer names them
    /// and how a script asks for one: <c>A0NQIB44.QR1.wav</c> on disk is
    /// <c>A0NQIB44.QR1</c> to the game.
    /// </remarks>
    private static HashSet<string> RestoredAudio(string workspace)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string directory = Path.Combine(workspace, "enhanced", "audio");

        if (!Directory.Exists(directory))
        {
            return names;
        }

        foreach (string file in Directory.EnumerateFiles(
                     directory, "*.wav", SearchOption.AllDirectories))
        {
            string name = Path.GetFileName(file);
            names.Add(name[..^".wav".Length]);
        }

        return names;
    }

    /// <summary>The reference bytes a language's asset is measured against.</summary>
    private static byte[]? Reference(
        string name, GameLanguage language, LocaleSource baseline, GameArchives? installed)
    {
        // The baseline's own spelling of the same asset, which for the prefixed families is
        // a different file name entirely: French F014ED3S6J1.YAK is measured against
        // English E014ED3S6J1.YAK and not against a file that does not exist.
        string counterpart = Spell(Canonical(name, language), baseline.Language);

        return baseline.Read(counterpart) ?? installed?.Read(counterpart);
    }

    /// <summary>Whether two copies of an asset are the same asset.</summary>
    /// <remarks>
    /// By bytes for everything but bitmaps, and by pixels for those. GK3's own container is
    /// a raw RGB565 bitmap with an eight-byte header; a dumped localisation may have been
    /// written back out as a 24-bit Windows bitmap, and the two forms never compare equal
    /// while always showing the same picture. Comparing bytes there would declare all 6,657
    /// of the game's bitmaps localised.
    /// </remarks>
    private static bool Same(string name, byte[]? mine, byte[] theirs)
    {
        if (mine is null)
        {
            return false;
        }

        if (mine.AsSpan().SequenceEqual(theirs))
        {
            return true;
        }

        if (!Path.GetExtension(name).Equals(".BMP", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            if (!BitmapDecoder.CanDecode(mine) || !BitmapDecoder.CanDecode(theirs))
            {
                return false;
            }

            DecodedImage a = BitmapDecoder.Decode(mine, name);
            DecodedImage b = BitmapDecoder.Decode(theirs, name);

            return a.Width == b.Width && a.Height == b.Height &&
                a.Pixels.AsSpan().SequenceEqual(b.Pixels);
        }
        catch (FormatParseException)
        {
            // One of them will not decode, which is a difference worth keeping rather than
            // a reason to fail: the language gets its own copy and the picture is whatever
            // its own release shipped.
            return false;
        }
    }

    /// <summary>
    /// The name an asset answers to with its language's letter taken out.
    /// </summary>
    /// <remarks>
    /// <c>F014ED3S6J1.YAK</c> in French and <c>E014ED3S6J1.YAK</c> in English are one
    /// asset, and this is its name: <c>*014ED3S6J1.YAK</c>. Everything else is its own
    /// canonical name — <c>27KASHAF.BMP</c> is <c>27KASHAF.BMP</c> in every language and
    /// differs in its contents.
    /// <para>
    /// The letter is only taken out when it <em>is</em> this language's letter. GK3's
    /// cutscene lip-sync files are named for the scene rather than for a line —
    /// <c>205PEND.YAK</c> — and stripping their first character would make five different
    /// assets collide.
    /// </para>
    /// </remarks>
    public static string Canonical(string name, GameLanguage language)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(language);

        string upper = name.ToUpperInvariant();

        if (upper.Length > StringTableSuffix.Length &&
            upper[0] == language.Prefix &&
            upper.AsSpan(1).SequenceEqual(StringTableSuffix))
        {
            return AnyPrefix + StringTableSuffix;
        }

        return upper.Length > 1 &&
               upper[0] == language.Prefix &&
               PrefixedExtensions.Contains(Path.GetExtension(upper), StringComparer.Ordinal)
            ? AnyPrefix + upper[1..]
            : upper;
    }

    /// <summary>How a language spells a canonical name.</summary>
    public static string Spell(string canonical, GameLanguage language)
    {
        ArgumentNullException.ThrowIfNull(canonical);
        ArgumentNullException.ThrowIfNull(language);

        return canonical.Length > 0 && canonical[0] == AnyPrefix
            ? language.Prefix + canonical[1..]
            : canonical;
    }

    /// <summary>Finds the language directories under a root.</summary>
    private static List<LocaleSource> Discover(string root, DiagnosticBag diagnostics)
    {
        List<LocaleSource> sources = [];

        foreach (string directory in Directory
                     .EnumerateDirectories(root)
                     .OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            string name = Path.GetFileName(directory);

            if (GameLanguage.Find(name) is not { } language)
            {
                // Said rather than passed over. A release nobody can name is a release
                // nobody gets, and the failure is completely silent otherwise: the
                // directory is there, the pack is not, and nothing anywhere connects the
                // two.
                diagnostics.Add(new Diagnostic(
                    "GK3R2308", DiagnosticSeverity.Warning,
                    $"The directory {name} names no language GK3 was published in, so it is "
                    + "not read.",
                    directory, null,
                    "a directory named for its language: "
                    + string.Join(", ", GameLanguage.Known.Select(l => l.FileCode)),
                    name,
                    "Rename it to that language's code. Its English name and its three-letter "
                    + "code are accepted too."));

                continue;
            }

            try
            {
                sources.Add(LocaleSource.Open(language, directory));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatParseException)
            {
                diagnostics.Add(new Diagnostic(
                    "GK3R2306", DiagnosticSeverity.Warning,
                    $"The {language.Name} directory cannot be read, so that language is "
                    + $"left out: {ex.Message}",
                    directory, null, "a readable directory", ex.GetType().Name,
                    "Check the permissions on it, or take it away."));
            }
        }

        return sources;
    }

    private static string Normalize(string path) => path.Replace('\\', '/');
}

/// <summary>
/// One language's release, however it happens to be laid out.
/// </summary>
/// <remarks>
/// Two forms, because both turn up. A language sourced properly is a set of <c>*.brn</c>
/// archives and is read with the engine's own reader in the engine's own search order; a
/// language somebody dumped is a tree of loose files, which may be flat, may be one
/// directory per archive, and may be both at once with the same name in each. Either way
/// what this offers is a set of 1999 names and their bytes, which is all the comparison
/// needs to know.
/// <para>
/// Where a name appears more than once in a dumped tree the shallowest copy wins, and then
/// the alphabetically first. That is arbitrary and it has to be: a dump has thrown away
/// which archive an entry came from, which is the only thing that could decide it.
/// </para>
/// </remarks>
internal sealed class LocaleSource : IDisposable
{
    private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly GameArchives? _archives;

    private LocaleSource(GameLanguage language, string root, string media, GameArchives? archives)
    {
        Language = language;
        Root = root;
        MediaRoot = media;
        _archives = archives;
    }

    /// <summary>Which language this is.</summary>
    public GameLanguage Language { get; }

    /// <summary>Where its assets were read from.</summary>
    /// <remarks>
    /// The directory holding the archives when it is a release, and the language directory
    /// itself when it is a dumped tree.
    /// </remarks>
    public string Root { get; }

    /// <summary>Where its movies are.</summary>
    /// <remarks>
    /// The same directory as <see cref="Root"/>. A GK3 release keeps its BIKs beside its
    /// archives in <c>Data</c>, and a dump keeps them beside whatever was dumped, so one
    /// answer serves both — but it is named separately because the two are different
    /// questions and one of them is asked by a different stage.
    /// </remarks>
    public string MediaRoot { get; }

    /// <summary>How many distinct names it holds.</summary>
    public int Count => _archives?.Names().Count ?? _files.Count;

    /// <summary>Every name it holds.</summary>
    public IReadOnlyCollection<string> Names =>
        _archives is not null ? _archives.Names() : _files.Keys;

    /// <summary>Opens a language directory, however it is laid out.</summary>
    /// <remarks>
    /// <para>
    /// <b>Archives first.</b> A directory with real <c>.brn</c> files in it is a release,
    /// and reading it with the game's own reader gets the game's own search order for free —
    /// which matters, because several archives hold the same name and only that order says
    /// which one the game would have read.
    /// </para>
    /// <para>
    /// They are looked for in the release's own shape rather than only at the top: an
    /// unpacked GK3 is <c>GK3.exe</c> beside a <c>Data</c> directory, and insisting the
    /// archives be loose in the language directory would silently take the whole release
    /// for a dump and index <c>GK3.EXE</c> and eight <c>.BRN</c> files as though they were
    /// assets. That is exactly what it did, and it reported ten localised assets for a
    /// complete German release.
    /// </para>
    /// </remarks>
    public static LocaleSource Open(GameLanguage language, string root)
    {
        if (Archives(root) is { } data)
        {
            return new LocaleSource(language, data, data, GameArchives.Open(data));
        }

        var source = new LocaleSource(language, root, root, null);

        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .OrderBy(Depth)
                     .ThenBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            string name = Path.GetFileName(file);

            // The movies sit at the top of a dumped release beside the archives' contents,
            // and they are not archive assets: they are handled by the video pass, which
            // reads them from the directory itself.
            if (IsMovie(name))
            {
                continue;
            }

            source._files.TryAdd(name, file);
        }

        return source;
    }

    /// <summary>
    /// The directory holding a release's archives, wherever the person who unpacked it put
    /// them.
    /// </summary>
    /// <returns>That directory, or null when this is not a release.</returns>
    /// <remarks>
    /// <c>core.brn</c> is the test rather than "any .brn", because it is the one archive
    /// every GK3 installation has and the one G-Engine's own <c>DataHelper</c> looks for.
    /// A directory with a stray <c>pl.brn</c> in it and nothing else is not a release.
    /// </remarks>
    public static string? Archives(string root)
    {
        if (!Directory.Exists(root))
        {
            return null;
        }

        if (File.Exists(Path.Combine(root, "core.brn")))
        {
            return root;
        }

        // Data first by name, then any child that has one, so an unpacked release works
        // whether its directory is called Data, DATA or something else again.
        string data = Path.Combine(root, "Data");

        if (File.Exists(Path.Combine(data, "core.brn")))
        {
            return data;
        }

        return Directory.EnumerateDirectories(root)
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(d => File.Exists(Path.Combine(d, "core.brn")));
    }

    /// <summary>Whether a name is one of the release's movies rather than an asset.</summary>
    public static bool IsMovie(string name) =>
        Path.GetExtension(name) is { Length: > 0 } extension &&
        (extension.Equals(".bik", StringComparison.OrdinalIgnoreCase) ||
         extension.Equals(".avi", StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether it holds an asset.</summary>
    public bool Has(string name) =>
        _archives?.Exists(name) ?? _files.ContainsKey(name);

    /// <summary>Reads an asset.</summary>
    public byte[]? Read(string name)
    {
        if (_archives is not null)
        {
            return _archives.Read(name);
        }

        return _files.TryGetValue(name, out string? file) ? File.ReadAllBytes(file) : null;
    }

    private static int Depth(string path) => path.Count(c => c is '/' or '\\');

    /// <inheritdoc/>
    public void Dispose() => _archives?.Dispose();
}
