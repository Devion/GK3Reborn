using System.Text;
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

        foreach (BarnArchive archive in _archives)
        {
            BarnEntry? entry = archive.Find(name);
            if (entry is not null && !entry.IsPointer)
            {
                return archive.Extract(entry);
            }
        }

        return null;
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

        if (Overrides?.HasArchive(name) == true)
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

        return false;
    }

    /// <summary>Reads a text asset by name.</summary>
    /// <param name="name">Asset name, with extension.</param>
    /// <returns>Its text, or null if no archive holds it.</returns>
    /// <remarks>
    /// The text assets are Windows-1252 rather than UTF-8: they were authored in 1999 and
    /// contain accented characters in French names. Decoding them as UTF-8 corrupts those
    /// and can throw on otherwise valid files.
    /// </remarks>
    public string? ReadText(string name)
    {
        byte[]? bytes = Read(name);
        return bytes is null ? null : Encoding.Latin1.GetString(bytes);
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
