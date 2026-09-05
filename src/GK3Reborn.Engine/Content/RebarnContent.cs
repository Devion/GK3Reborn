using GK3Reborn.Formats;
using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Formats.Rebarn;
using GK3Reborn.Foundation.Diagnostics;

namespace GK3Reborn.Content;

/// <summary>
/// Every ReBarn pack beside the executable, searched as one.
/// </summary>
/// <remarks>
/// <para>
/// The remake's own content — enhanced colour, normal, ORM and height maps, modernised
/// models, imported video — in one or two files that ship with the game rather than in
/// forty thousand loose ones. <see cref="GameArchives"/> is the equivalent for the
/// original installation, and the two are separate on purpose: GK3's archives are read
/// from wherever the player installed the game, and these are read from wherever the
/// executable is.
/// </para>
/// <para>
/// Packs are opened in file-name order and <strong>the last one wins</strong>, so a pack
/// dropped in later overrides one shipped earlier: <c>Reborn.rebarn</c>, then
/// <c>RebornMaterials.rebarn</c>, then a <c>RebornPatch.rebarn</c> somebody adds. That is
/// the whole mod story, and it needs no support beyond a name that sorts last.
/// </para>
/// <para>
/// A missing directory, or a directory with no packs in it, is not an error. The game runs
/// from a legally obtained installation and all of this is an addition to it — exactly the
/// rule <see cref="EnhancedTextures"/> follows.
/// </para>
/// </remarks>
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
    /// <remarks>
    /// Set here for the same reason <see cref="GameArchives.Overrides"/> is set there:
    /// this is the one door the remake's own content comes through. The trees, the
    /// improved room geometry, the video, the material library and every block-compressed
    /// texture reach a pack through this class, so an override registered here reaches all
    /// of them and none of those callers has to know the layer exists.
    /// </remarks>
    public ContentOverrides? Overrides { get; set; }

    /// <summary>How many packs are open.</summary>
    public int VolumeCount => _packs.Count;

    /// <summary>How many entries there are across all of them, after overrides.</summary>
    public int Count => _entries.Count;

    /// <summary>The packs, in the order they are searched.</summary>
    public IReadOnlyList<RebarnArchive> Volumes => _packs;

    /// <summary>Every entry after overrides, each with the pack that holds it.</summary>
    /// <remarks>
    /// The effective set rather than the union: where two volumes hold the same key the
    /// later one has already won, which is what a reader would get. A tool that wants to
    /// see each volume as itself should open the files rather than ask this.
    /// </remarks>
    public IReadOnlyCollection<(RebarnArchive Pack, RebarnEntry Entry)> Entries =>
        (IReadOnlyCollection<(RebarnArchive, RebarnEntry)>)_entries.Values;

    /// <summary>Every pack in a directory.</summary>
    /// <param name="directory">Where to look; usually the directory the executable is in.</param>
    /// <param name="diagnostics">Receives a diagnostic for any pack that will not open.</param>
    /// <returns>The set, empty when there is nothing to open.</returns>
    /// <remarks>
    /// A pack that will not open costs that pack and nothing else. One damaged volume out
    /// of two should leave the game running on what the other one holds, exactly as one
    /// unreadable texture leaves the rest of a scene alone.
    /// </remarks>
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
            .Select(e => e.Entry.Kind == RebarnKind.Audio
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
    /// <remarks>
    /// For the things that are read as they play rather than loaded whole — a movie, whose
    /// index may sit at either end of the container. A packed entry comes back as a window
    /// onto the pack's own mapping, which stays valid because the pack is open for the life
    /// of the process; an override comes back as an ordinary file handle.
    /// </remarks>
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
    /// <remarks>
    /// The blocks point into the memory-mapped pack, which is what makes this the cheapest
    /// texture path there is: no decode, no copy, a mip chain already built. They are valid
    /// while this <see cref="RebarnContent"/> is open, which for the game is the life of the
    /// process — see <see cref="RebarnArchive.ReadMapped(RebarnEntry)"/>.
    /// </remarks>
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
