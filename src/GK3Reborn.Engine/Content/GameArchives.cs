using GK3Reborn.Foundation;
using GK3Reborn.Foundation.Diagnostics;
using GK3Reborn.Formats.Barn;

namespace GK3Reborn.Content;

/// <summary>
/// Every barn archive of an installation, searched as one.
/// </summary>
/// <remarks>
/// The game does not record which archive holds a given asset, and several archives can
/// hold the same name, so the only reliable way to open one by name is to search them all
/// in a fixed order. Doing that in one place keeps the order identical between the
/// toolchain and the game, which matters because a name resolving differently in the two
/// would make a tool's output describe something the game never loads.
/// </remarks>
public sealed class GameArchives : IDisposable
{
    private readonly List<BarnArchive> _archives = [];

    private GameArchives()
    {
    }

    /// <summary>How many archives were opened.</summary>
    public int Count => _archives.Count;

    /// <summary>
    /// Files a player has dropped into <c>overrides/</c>, which outrank every archive.
    /// </summary>
    /// <remarks>
    /// Set here rather than consulted by each caller because this is the one door every
    /// 1999 asset comes through — scripts, room definitions, sounds, models, bitmaps, the
    /// text files that configure the game. A caller that had to remember to ask the
    /// override layer first is a caller that will one day forget, and the asset it forgot
    /// about would be the one nobody could work out why they could not replace.
    /// </remarks>
    public ContentOverrides? Overrides { get; set; }

    /// <summary>
    /// The language the game is being read in, when it is not the installation's own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Set here for the same reason <see cref="Overrides"/> is: this is the one door every
    /// 1999 asset comes through, and localisation touches nearly every family of them —
    /// the string table, the fonts, Sidney's documents, the bitmaps with words painted into
    /// them, every line of recorded dialogue and every <c>.YAK</c> that lip-syncs one. A
    /// layer each of those callers had to remember to consult is a layer that would be
    /// French everywhere except the one place somebody forgot.
    /// </para>
    /// <para>
    /// <b>Under the overrides and over the archives.</b> A file a player put in
    /// <c>overrides/</c> is theirs and stays theirs whatever language the game is in;
    /// everything the language pack does not hold falls through to the installation, which
    /// is what makes an incomplete pack harmless. Null when the player is reading the game
    /// in whatever language they installed, and null all the way down — see
    /// <see cref="LocalizedContent"/>.
    /// </para>
    /// </remarks>
    public LocalizedContent? Localization { get; set; }

    /// <summary>
    /// Content the game shipped with and cannot reach, put back on the way past.
    /// </summary>
    /// <remarks>
    /// Null unless the player asked for it, and null all the way down, so that a game
    /// nobody has asked to restore anything in does one null test per read.
    /// <para>
    /// It edits what an archive holds; it never edits an override. A file the player put
    /// in <c>overrides/</c> is theirs, and a table quietly rewriting it would be the one
    /// thing an override exists to prevent.
    /// </para>
    /// </remarks>
    public CutContent? Restoration { get; set; }

    /// <summary>Where restorations that did not apply are reported.</summary>
    public DiagnosticBag? RestorationDiagnostics { get; set; }

    /// <summary>
    /// Assets the remake adds, which no barn has and none can.
    /// </summary>
    /// <remarks>
    /// Consulted last, after every archive, so it can only ever answer for a name the game
    /// does not know. See <see cref="AddedAssets"/>.
    /// </remarks>
    public AddedAssets? Added { get; set; }

    /// <summary>Opens every archive in a directory.</summary>
    /// <param name="directory">The game's <c>Data</c> directory.</param>
    /// <returns>The set.</returns>
    public static GameArchives Open(string directory)
    {
        ArgumentNullException.ThrowIfNull(directory);

        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"No such content directory: {directory}");
        }

        var set = new GameArchives();

        try
        {
            foreach (FileInfo file in new DirectoryInfo(directory)
                         .EnumerateFiles("*.brn")
                         .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
            {
                set._archives.Add(BarnArchive.Open(file.FullName));
            }

            return set;
        }
        catch
        {
            set.Dispose();
            throw;
        }
    }

    /// <summary>Every asset name the archives hold, without duplicates.</summary>
    /// <param name="extension">
    /// Only names ending in this, with or without the dot, or null for all of them.
    /// </param>
    /// <returns>The names, in the order the archives are searched.</returns>
    /// <remarks>
    /// Pointer entries are left out: an archive naming an asset it does not contain is a
    /// cross-reference to another archive, and counting it would report the same asset
    /// twice under a name nothing can read.
    /// </remarks>
    public IReadOnlyList<string> Names(string? extension = null)
    {
        string? suffix = extension is null
            ? null
            : extension.StartsWith('.') ? extension : "." + extension;

        List<string> names = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        // First, because they are what would be read. A name that only an override has is
        // a name the game can now open, and leaving it out would make a listing disagree
        // with a read.
        foreach (string name in Overrides?.ArchiveNames ?? [])
        {
            if (suffix is not null && !name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (seen.Add(name))
            {
                names.Add(name);
            }
        }

        // Then the language, in the order it is read. Its names are mostly the archives'
        // own, so this adds few — but the ones it does add are the ones that matter:
        // FSTRINGS.TXT and seven thousand F-prefixed YAKs exist in no English archive, and
        // a listing without them would say French had no dialogue at all.
        foreach (string name in Localization?.ArchiveNames ?? [])
        {
            if (suffix is not null && !name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (seen.Add(name))
            {
                names.Add(name);
            }
        }

        foreach (BarnArchive archive in _archives)
        {
            foreach (BarnEntry entry in archive.Entries)
            {
                if (entry.IsPointer)
                {
                    continue;
                }

                if (suffix is not null &&
                    !entry.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (seen.Add(entry.Name))
                {
                    names.Add(entry.Name);
                }
            }
        }

        // Last, because that is where they are read from: a listing that put them earlier
        // would disagree with a read for any name an archive also has.
        foreach (string added in Added?.Names ?? [])
        {
            if (suffix is not null && !added.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (seen.Add(added))
            {
                names.Add(added);
            }
        }

        return names;
    }

    /// <summary>Reads an asset by name.</summary>
    /// <param name="name">Asset name, with extension.</param>
    /// <returns>Its bytes, or null if no archive holds it.</returns>
    public byte[]? Read(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (Overrides?.ReadArchive(name) is { } replaced)
        {
            return replaced;
        }

        // Before the archives and before the restoration table, because an entry here is
        // not an improvement on what the archive holds — it is what the archive would hold
        // if the disc had been pressed in this language. The restoration edits GK3's own
        // English text and would be editing the wrong language's bytes here.
        if (Localization?.Read(name) is { } localised)
        {
            return localised;
        }

        foreach (BarnArchive archive in _archives)
        {
            BarnEntry? entry = archive.Find(name);
            if (entry is not null && !entry.IsPointer)
            {
                byte[] bytes = archive.Extract(entry);

                return Restoration is { } restoration && restoration.Handles(name)
                    ? restoration.Apply(name, bytes, RestorationDiagnostics)
                    : bytes;
            }
        }

        return Added?.Read(name);
    }

    /// <summary>Whether any archive holds an asset.</summary>
    /// <param name="name">Asset name, with extension.</param>
    /// <returns>True when one does.</returns>
    /// <remarks>
    /// A directory lookup and nothing more — no extraction, no decompression. It is what
    /// lets a caller choose between candidates before committing to reading one.
    /// </remarks>
    public bool Exists(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (Overrides?.HasArchive(name) == true || Localization?.HasArchive(name) == true)
        {
            return true;
        }

        foreach (BarnArchive archive in _archives)
        {
            if (archive.Find(name) is { IsPointer: false })
            {
                return true;
            }
        }

        return Added?.Has(name) == true;
    }

    /// <summary>Reads a text asset by name.</summary>
    /// <param name="name">Asset name, with extension.</param>
    /// <returns>Its text, or null if no archive holds it.</returns>
    /// <remarks>
    /// <para>
    /// The text assets are one byte a character rather than UTF-8: they were authored in
    /// 1999 and contain accented characters in French names. Decoding them as UTF-8
    /// corrupts those and can throw on otherwise valid files.
    /// </para>
    /// <para>
    /// <b>Which code page depends on the language being read.</b> Nothing in the file says
    /// — no mark, no header, only bytes — so the only thing that can know is whoever chose
    /// the language, which is why this asks <see cref="Localization"/> and falls back to
    /// Windows-1252 when no language pack is open. See <see cref="Gk3Encoding"/>.
    /// </para>
    /// </remarks>
    public string? ReadText(string name)
    {
        byte[]? bytes = Read(name);

        return bytes is null
            ? null
            : Gk3Encoding.GetString(bytes, Localization?.Language.CodePage ?? 1252);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (BarnArchive archive in _archives)
        {
            archive.Dispose();
        }

        _archives.Clear();
    }
}
