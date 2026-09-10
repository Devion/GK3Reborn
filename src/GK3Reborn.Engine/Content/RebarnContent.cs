using GK3Reborn.Formats;
using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Formats.Rebarn;
using GK3Reborn.Foundation.Diagnostics;

namespace GK3Reborn.Content;

/// <summary>
/// Every ReBarn pack beside the executable, searched as one.
/// </summary>
public sealed class RebarnContent : IDisposable
{
    private readonly List<RebarnArchive> _packs = [];
    private readonly Dictionary<string, (RebarnArchive Pack, RebarnEntry Entry)> _entries =
        new(StringComparer.Ordinal);

    private RebarnContent(string directory) => Directory = directory;

    /// <summary>Where the packs were opened from.</summary>
    public string Directory { get; }

    /// <summary>
    /// Files a player has dropped into <c>overrides/</c>, which outrank every pack.
    /// </summary>
    public ContentOverrides? Overrides { get; set; }

    /// <summary>How many packs are open.</summary>
    public int VolumeCount => _packs.Count;

    /// <summary>How many entries there are across all of them, after overrides.</summary>
    public int Count => _entries.Count;

    /// <summary>The packs, in the order they are searched.</summary>
    public IReadOnlyList<RebarnArchive> Volumes => _packs;

    /// <summary>Every entry after overrides, each with the pack that holds it.</summary>
    public IReadOnlyCollection<(RebarnArchive Pack, RebarnEntry Entry)> Entries =>
        (IReadOnlyCollection<(RebarnArchive, RebarnEntry)>)_entries.Values;

    /// <summary>Every pack in a directory.</summary>
    /// <param name="directory">Where to look; usually the directory the executable is in.</param>
    /// <param name="diagnostics">Receives a diagnostic for any pack that will not open.</param>
    /// <returns>The set, empty when there is nothing to open.</returns>
    public static RebarnContent Open(string directory, DiagnosticBag? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(directory);

        var content = new RebarnContent(directory);

        if (!System.IO.Directory.Exists(directory))
        {
            return content;
        }

        IEnumerable<string> files = System.IO.Directory
            .EnumerateFiles(directory, "*" + RebarnFormat.Extension)

            // The language packs are not part of this set. The game opens exactly one of
            // them — the one the player chose — through LocalizedContent, and merging every
            // language it happens to have installed into the shared namespace would put the
            // last one alphabetically in front of the archives for everybody.
            .Where(f => !LocalizedContent.FileNamePattern().IsMatch(Path.GetFileName(f)))
            .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase);

        foreach (string file in files)
        {
            try
            {
                content.Add(RebarnArchive.Open(file));
            }
            catch (Exception ex) when (ex is FormatParseException or IOException)
            {
                diagnostics?.Add(new Diagnostic(
                    "GK3R1176",
                    DiagnosticSeverity.Warning,
                    $"The pack {Path.GetFileName(file)} will not open, so it is skipped: {ex.Message}",
                    file,
                    null,
                    "a readable ReBarn pack",
                    ex.GetType().Name,
                    "Produce it again with `pack-content`, or take it out of the directory."));
            }
        }

        return content;
    }

    /// <summary>Opens a named set of packs, in the order given.</summary>
    /// <param name="paths">The pack files. Later ones override earlier ones.</param>
    /// <returns>The set.</returns>
    public static RebarnContent OpenFiles(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        string[] list = [.. paths];
        var content = new RebarnContent(
            list.Length > 0 ? Path.GetDirectoryName(Path.GetFullPath(list[0])) ?? "." : ".");

        try
        {
            foreach (string path in list)
            {
                content.Add(RebarnArchive.Open(path));
            }

            return content;
        }
        catch
        {
            content.Dispose();
            throw;
        }
    }

    private void Add(RebarnArchive pack)
    {
        _packs.Add(pack);

        foreach (RebarnEntry entry in pack.Entries)
        {
            // Later packs win, which is what makes a patch pack a patch pack.
            _entries[entry.Key] = (pack, entry);
        }
    }

    /// <summary>Whether the packs hold something.</summary>
    /// <param name="kind">What it is for.</param>
    /// <param name="name">Its name, with or without an extension.</param>
    /// <returns>True when one of them does.</returns>
    public bool Has(RebarnKind kind, string name) =>
        Overrides?.Has(kind, name) == true ||
        _entries.ContainsKey(RebarnFormat.Key(kind, name));

    /// <summary>How many entries of one kind there are.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>The count.</returns>
    public int CountOf(RebarnKind kind) => Names(kind).Count;

    /// <summary>Every name of one kind, in a stable order.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>The names.</returns>
    public IReadOnlyList<string> Names(RebarnKind kind) =>
        [.. _entries.Values
            .Where(e => e.Entry.Kind == kind)
            .Select(e => e.Entry.Kind is RebarnKind.Audio or RebarnKind.Room
                ? Path.GetFileName(e.Entry.Name)
                : Path.GetFileNameWithoutExtension(e.Entry.Name))
            .Concat(Overrides?.Names(kind) ?? [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)];

    /// <summary>Reads an entry into a new array.</summary>
    /// <param name="kind">What it is for.</param>
    /// <param name="name">Its name.</param>
    /// <returns>Its bytes, or null when no pack holds it.</returns>
    public byte[]? Read(RebarnKind kind, string name) =>
        Overrides?.Read(kind, name)
        ?? (_entries.TryGetValue(RebarnFormat.Key(kind, name), out (RebarnArchive Pack, RebarnEntry Entry) found)
            ? found.Pack.Read(found.Entry)
            : null);

    /// <summary>Finds which pack holds an entry, and what the index says about it.</summary>
    /// <param name="kind">What it is for.</param>
    /// <param name="name">Its name.</param>
    /// <returns>The pack and the entry, or null when no pack holds it.</returns>
    public (RebarnArchive Pack, RebarnEntry Entry)? Find(RebarnKind kind, string name) =>
        _entries.TryGetValue(RebarnFormat.Key(kind, name), out (RebarnArchive Pack, RebarnEntry Entry) found)
            ? found
            : null;

    /// <summary>Opens an entry as a seekable stream, override first.</summary>
    /// <param name="kind">What it is for.</param>
    /// <param name="name">Its name.</param>
    /// <returns>The stream, or null when nothing holds it.</returns>
    public Stream? OpenStream(RebarnKind kind, string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (Overrides?.PathOf(kind, name) is { } loose)
        {
            return File.OpenRead(loose);
        }

        return Find(kind, name) is { } found
            ? new MappedStream(found.Pack.ReadMapped(found.Entry))
            : null;
    }

    /// <summary>Where an entry would be read from, for saying so out loud.</summary>
    /// <param name="kind">What it is for.</param>
    /// <param name="name">Its name.</param>
    /// <returns>A description, or null when nothing holds it.</returns>
    public string? SourceOf(RebarnKind kind, string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (Overrides?.PathOf(kind, name) is { } loose)
        {
            return loose;
        }

        return Find(kind, name) is { } found
            ? $"{Path.GetFileName(found.Pack.Path)}:{found.Entry.Name}"
            : null;
    }

    /// <summary>Reads a block-compressed texture without copying it out of the pack.</summary>
    /// <param name="kind">Which set to read from.</param>
    /// <param name="name">The colour texture's name, which every set is keyed by.</param>
    /// <param name="diagnostics">Receives a diagnostic when one will not read.</param>
    /// <returns>The texture, or null when no pack holds it or it is unreadable.</returns>
    public CompressedImage? ReadTexture(
        RebarnKind kind, string name, DiagnosticBag? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(name);

        // An override standing in for this entry. Read into the heap rather than mapped,
        // which is the right way round for a file somebody is editing between runs: the
        // pack is open for the life of the process and one loose texture is not worth a
        // second mapping to save a copy of.
        if (Overrides?.Blocks(kind).TryGetValue(
                Path.GetFileNameWithoutExtension(name), out string? loose) == true)
        {
            try
            {
                return DdsFile.Read(File.ReadAllBytes(loose), loose);
            }
            catch (Exception ex) when (ex is FormatParseException or IOException)
            {
                diagnostics?.Add(new Diagnostic(
                    "GK3R1182",
                    DiagnosticSeverity.Warning,
                    $"The override {Path.GetFileName(loose)} will not load, so what it "
                    + $"stands in for is used instead: {ex.Message}",
                    loose,
                    null,
                    "a readable DDS",
                    ex.GetType().Name,
                    "Produce it again, or take it out of the overrides directory."));
            }
        }

        if (Find(kind, name) is not { } found)
        {
            return null;
        }

        try
        {
            return DdsFile.Read(found.Pack.ReadMapped(found.Entry), found.Entry.Name);
        }
        catch (Exception ex) when (ex is FormatParseException or IOException)
        {
            diagnostics?.Add(new Diagnostic(
                "GK3R1177",
                DiagnosticSeverity.Warning,
                $"The packed {kind} for {name} will not load, so it is skipped: {ex.Message}",
                found.Pack.Path,
                found.Entry.Offset,
                "a readable DDS",
                ex.GetType().Name,
                "Produce the pack again with `pack-content`."));

            return null;
        }
    }

    /// <summary>A one-line summary of what is open, for a startup report.</summary>
    /// <returns>The summary, or null when no pack is open.</returns>
    public string? Describe()
    {
        if (_packs.Count == 0)
        {
            return null;
        }

        long bytes = _packs.Sum(p => p.Length);

        IEnumerable<string> parts = _entries.Values
            .GroupBy(e => e.Entry.Kind)
            .OrderBy(g => g.Key)
            .Select(g => $"{g.Count()} {RebarnFormat.DirectoryOf(g.Key)}");

        return $"{_packs.Count} pack(s), {_entries.Count} entries "
            + $"({bytes / (1024.0 * 1024 * 1024):F1} GB): {string.Join(", ", parts)}";
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (RebarnArchive pack in _packs)
        {
            pack.Dispose();
        }

        _packs.Clear();
        _entries.Clear();
    }
}
