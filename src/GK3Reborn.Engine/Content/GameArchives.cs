using GK3Reborn.Foundation;
using GK3Reborn.Foundation.Diagnostics;
using GK3Reborn.Formats.Barn;

namespace GK3Reborn.Content;

/// <summary>
/// Every barn archive of an installation, searched as one.
/// </summary>
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
    public ContentOverrides? Overrides { get; set; }

    /// <summary>
    /// The language the game is being read in, when it is not the installation's own.
    /// </summary>
    public LocalizedContent? Localization { get; set; }

    /// <summary>
    /// Content the game shipped with and cannot reach, put back on the way past.
    /// </summary>
    public CutContent? Restoration { get; set; }

    /// <summary>Where restorations that did not apply are reported.</summary>
    public DiagnosticBag? RestorationDiagnostics { get; set; }

    /// <summary>
    /// Assets the remake adds, which no barn has and none can.
    /// </summary>
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
