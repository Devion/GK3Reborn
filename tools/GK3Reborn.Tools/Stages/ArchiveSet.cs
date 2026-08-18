using System.Text;
using GK3Reborn.Formats.Barn;

namespace GK3Reborn.Tools.Stages;

/// <summary>
/// Every barn archive of an installation, searched as one.
/// </summary>
/// <remarks>
/// The game does not record which archive holds a given asset, and several archives can
/// hold the same name, so the only reliable way to open one by name is to search them all
/// in a fixed order. Doing that in one place keeps the order the same everywhere.
/// </remarks>
public sealed class ArchiveSet : IDisposable
{
    private readonly List<BarnArchive> _archives = [];

    private ArchiveSet()
    {
    }

    /// <summary>How many archives were opened.</summary>
    public int Count => _archives.Count;

    /// <summary>Opens every archive in a directory.</summary>
    /// <param name="directory">The game's <c>Data</c> directory.</param>
    /// <returns>The set.</returns>
    public static ArchiveSet Open(string directory)
    {
        ArgumentNullException.ThrowIfNull(directory);

        var set = new ArchiveSet();

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

    /// <summary>Reads an asset by name.</summary>
    /// <param name="name">Asset name, with extension.</param>
    /// <returns>Its bytes, or null if no archive holds it.</returns>
    public byte[]? Read(string name)
    {
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

    /// <summary>Reads a text asset by name.</summary>
    /// <param name="name">Asset name, with extension.</param>
    /// <returns>Its text, or null if no archive holds it.</returns>
    /// <remarks>
    /// The text assets are Windows-1252 rather than UTF-8: they were authored in 1999 and
    /// contain accented characters in French names. Decoding them as UTF-8 corrupts those
    /// and, worse, can throw on otherwise valid files.
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
