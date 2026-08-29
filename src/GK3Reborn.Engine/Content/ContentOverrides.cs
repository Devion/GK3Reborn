using GK3Reborn.Formats.Rebarn;
using GK3Reborn.Foundation.Diagnostics;

namespace GK3Reborn.Content;

/// <summary>
/// Loose files a player has dropped in, standing in front of everything else.
/// </summary>
/// <remarks>
/// <para>
/// One directory — <c>overrides/</c> beside the executable — whose contents outrank both
/// the remake's <see cref="RebarnContent">packs</see> and the original game's
/// <see cref="GameArchives">archives</see>. Somebody who wants a different wallpaper, a
/// different normal map or a different script puts a file there under the name the game
/// already uses, and nothing else has to change: no repack, no reinstall, no patch pack.
/// </para>
/// <para>
/// <strong>It is the top of every stack, not another layer in the middle.</strong> That is
/// the whole difference between this and a <c>RebornPatch.rebarn</c>, which only outranks
/// the packs that sort before it. A texture has four sources — the archive's bitmap, an
/// enhanced PNG, a loose <c>build/</c> DDS and a pack — and an override that beat only one
/// of them would appear to do nothing on the machines where a different one happened to
/// win. So an override is registered into every one of those layers.
/// </para>
/// <para>
/// A missing directory is not an error, the same rule <see cref="EnhancedTextures"/> and
/// <see cref="RebarnContent"/> follow. Nothing here is required to play the game.
/// </para>
///
/// <para>
/// <strong>What decides where a file lands.</strong> Two independent questions, answered from the path and from the extension, so that
/// neither has to be guessed at:
/// </para>
/// <list type="bullet">
///   <item>
///     <description>
///     <strong>The extension says which layer.</strong> The forms the remake's own content
///     takes — <c>.png .dds .bmp .glb .mp4 .json</c> — go in front of the packs.
///     Everything else is an asset of the 1999 game, so it goes in front of the archives
///     under its own file name: <c>R25.SIF</c>, <c>R25.NVC</c>, <c>R25THEME1.WAV</c>.
///     </description>
///   </item>
///   <item>
///     <description>
///     <strong>A directory says which kind.</strong> The last path segment that names one
///     — <c>textures normals orm height emissive models scene-geometry video manifests raw</c>
///     — decides, exactly as <c>enhanced/</c> and <c>pack-extract</c> lay them out, so
///     <c>--extract</c> writes a tree this reads back without anything being moved. Any
///     other directory is the player's own filing and is ignored: <c>overrides/my mod/
///     textures/R25WALLS.png</c> is a colour texture. With no kind directory at all, an
///     image is a colour texture and the rest go by extension.
///     </description>
///   </item>
/// </list>
/// <para>
/// <strong>Every file is registered in front of the archives as well</strong>, under its
/// full name with its extension. It costs one dictionary entry and it is what makes a
/// dropped <c>GAB_FACE.BMP</c> reach the seventeen places that ask an archive for a bitmap
/// by name, rather than only the one that asks the texture stack for <c>GAB_FACE</c>.
/// </para>
/// </remarks>
public sealed class ContentOverrides
{
    /// <summary>The directory the game looks in, relative to the executable.</summary>
    public const string DirectoryName = "overrides";

    /// <summary>Extensions that go in front of the packs rather than the archives.</summary>
    /// <remarks>
    /// The forms the remake's own content takes. Anything else in the directory is an
    /// asset of the original game and is matched by its whole file name instead.
    /// </remarks>
    private static readonly string[] PackForms =
        [".PNG", ".DDS", ".BMP", ".GLB", ".GLTF", ".MP4", ".M4V", ".JSON"];

    /// <summary>Extensions that decode to pixels rather than to blocks.</summary>
    /// <remarks>
    /// No JPEG. Nothing in the engine decodes one, and a form advertised here that then
    /// falls back to what it was meant to replace is worse than one that was never offered
    /// — the file is there, the picture is not, and nothing says why.
    /// </remarks>
    private static readonly string[] ImageForms = [".PNG", ".BMP"];

    private readonly Dictionary<string, string> _archive = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<RebarnKind, Dictionary<string, string>> _images = [];
    private readonly Dictionary<RebarnKind, Dictionary<string, string>> _blocks = [];
    private readonly Dictionary<RebarnKind, Dictionary<string, string>> _packed = [];

    private ContentOverrides(string directory) => Directory = directory;

    /// <summary>Where the files were read from.</summary>
    public string Directory { get; }

    /// <summary>How many files were found, counted once each.</summary>
    public int Count { get; private set; }

    /// <summary>How many of them are assets of the 1999 game rather than packed content.</summary>
    private int _assets;

    /// <summary>Whether there is anything at all to override.</summary>
    /// <remarks>
    /// Worth asking before the layers are built. A game with no overrides directory should
    /// behave to the byte as it did before this existed, and the cheapest way to promise
    /// that is for every consumer to skip the layer entirely rather than to consult an
    /// empty one.
    /// </remarks>
    public bool IsEmpty => Count == 0;

    /// <summary>Reads a directory of overrides, subdirectories included.</summary>
    /// <param name="directory">Where they are; usually <c>overrides</c> beside the game.</param>
    /// <param name="diagnostics">Receives a diagnostic when the directory cannot be read.</param>
    /// <returns>The set, empty when the directory is not there.</returns>
    public static ContentOverrides Open(string directory, DiagnosticBag? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(directory);

        var set = new ContentOverrides(directory);

        if (directory.Length == 0 || !System.IO.Directory.Exists(directory))
        {
            return set;
        }

        List<string> files;

        try
        {
            files = [.. System.IO.Directory.EnumerateFiles(
                directory, "*", SearchOption.AllDirectories)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diagnostics?.Add(new Diagnostic(
                "GK3R1180",
                DiagnosticSeverity.Warning,
                $"The overrides directory cannot be read, so nothing is overridden: {ex.Message}",
                directory,
                null,
                "a readable directory",
                ex.GetType().Name,
                "Check the permissions on it, or take it away."));

            return set;
        }

        // In a fixed order so that two files claiming the same name resolve the same way on
        // every machine. EnumerateFiles is filesystem order, which is not an order.
        files.Sort(StringComparer.OrdinalIgnoreCase);

        foreach (string file in files)
        {
            set.Add(directory, file);
        }

        return set;
    }

    private void Add(string root, string file)
    {
        string name = Path.GetFileName(file);

        // Whatever a Mac or an editor left behind. Never an asset, and a .DS_Store
        // registered as an archive override is a name nothing asks for — harmless, but it
        // would be counted and reported, and a count that says 41 when the player put 40
        // files there is a count nobody trusts again.
        if (name.StartsWith('.') || name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string extension = Path.GetExtension(name).ToUpperInvariant();
        string bare = Path.GetFileNameWithoutExtension(name);

        Count++;

        // In front of the archives under its whole name, whatever else it is. This is the
        // door every 1999 asset comes through — scripts, room definitions, sounds, models,
        // the text files that configure the game — and it is keyed by name-with-extension
        // because that is how the archives themselves are keyed.
        _archive[name] = file;

        if (!PackForms.Contains(extension))
        {
            _assets++;
            return;
        }

        RebarnKind kind = KindOf(root, file, extension);

        if (ImageForms.Contains(extension))
        {
            Into(_images, kind)[bare] = file;
        }
        else if (extension == ".DDS")
        {
            Into(_blocks, kind)[bare] = file;
        }

        // Every packable form answers as bytes too, which is what the model, video,
        // manifest and terrain paths want. A texture is in here as well: a caller that
        // asks a pack for a name and gets bytes should get the override's bytes, and it is
        // the caller's business whether it then decodes them or hands them to the device.
        Into(_packed, kind)[bare] = file;
    }

    /// <summary>Which kind a file under the overrides directory belongs to.</summary>
    /// <remarks>
    /// The last directory in its path that names one, so <c>overrides/my mod/normals/X.dds</c>
    /// is a normal map and <c>overrides/normals/experiments/X.dds</c> is one too. With no
    /// such directory the extension decides, and an image with nothing else said about it is
    /// a colour texture — which is what somebody who dropped a PNG in meant.
    /// </remarks>
    private static RebarnKind KindOf(string root, string file, string extension)
    {
        string? relative = Path.GetDirectoryName(Path.GetRelativePath(root, file));

        if (relative is { Length: > 0 })
        {
            string[] segments = relative.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);

            for (int i = segments.Length - 1; i >= 0; i--)
            {
                if (RebarnFormat.KindOf(segments[i]) is { } named)
                {
                    return named;
                }
            }
        }

        return extension switch
        {
            ".GLB" or ".GLTF" => RebarnKind.Model,
            ".MP4" or ".M4V" => RebarnKind.Video,
            ".JSON" => RebarnKind.Manifest,
            _ => RebarnKind.Texture,
        };
    }

    private static Dictionary<string, string> Into(
        Dictionary<RebarnKind, Dictionary<string, string>> map, RebarnKind kind)
    {
        if (!map.TryGetValue(kind, out Dictionary<string, string>? set))
        {
            set = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            map[kind] = set;
        }

        return set;
    }

    /// <summary>The set for a kind, or the shared empty one when there is none.</summary>
    /// <remarks>
    /// Shared rather than freshly allocated, because this is asked once per texture per
    /// room load for four kinds and the answer for most of them is nothing.
    /// </remarks>
    private static Dictionary<string, string> Of(
        Dictionary<RebarnKind, Dictionary<string, string>> map, RebarnKind kind) =>
        map.TryGetValue(kind, out Dictionary<string, string>? set) ? set : Nothing;

    private static readonly Dictionary<string, string> Nothing =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The images of one kind, by the name the game uses.</summary>
    /// <param name="kind">Which set.</param>
    /// <returns>Name without extension, to the file on disk.</returns>
    /// <remarks>
    /// PNG, BMP and JPEG: the forms that have to be decoded to pixels. Handed to
    /// <see cref="EnhancedTextures"/>, which is the layer already asked before the
    /// compressed one everywhere in the loader.
    /// </remarks>
    public IReadOnlyDictionary<string, string> Images(RebarnKind kind) => Of(_images, kind);

    /// <summary>The block-compressed textures of one kind, by the name the game uses.</summary>
    /// <param name="kind">Which set.</param>
    /// <returns>Name without extension, to the file on disk.</returns>
    public IReadOnlyDictionary<string, string> Blocks(RebarnKind kind) => Of(_blocks, kind);

    /// <summary>Whether an override stands in for something a pack would hold.</summary>
    /// <param name="kind">What it is for.</param>
    /// <param name="name">Its name, with or without an extension.</param>
    /// <returns>True when one does.</returns>
    public bool Has(RebarnKind kind, string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return Of(_packed, kind).ContainsKey(Path.GetFileNameWithoutExtension(name));
    }

    /// <summary>Where an override that stands in for a pack entry is on disk.</summary>
    /// <param name="kind">What it is for.</param>
    /// <param name="name">Its name, with or without an extension.</param>
    /// <returns>The path, or null when there is no such override.</returns>
    public string? PathOf(RebarnKind kind, string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return Of(_packed, kind).TryGetValue(
            Path.GetFileNameWithoutExtension(name), out string? file) ? file : null;
    }

    /// <summary>Every overridden name of one kind, in a stable order.</summary>
    /// <param name="kind">Which set.</param>
    /// <returns>The names, without extensions.</returns>
    public IReadOnlyList<string> Names(RebarnKind kind) =>
        [.. Of(_packed, kind).Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase)];

    /// <summary>How many overrides there are of one kind.</summary>
    /// <param name="kind">Which set.</param>
    /// <returns>The count.</returns>
    public int CountOf(RebarnKind kind) => Of(_packed, kind).Count;

    /// <summary>Reads an override that stands in for a pack entry.</summary>
    /// <param name="kind">What it is for.</param>
    /// <param name="name">Its name, with or without an extension.</param>
    /// <param name="diagnostics">Receives a diagnostic when the file will not read.</param>
    /// <returns>Its bytes, or null when there is no such override or it will not read.</returns>
    /// <remarks>
    /// A file that will not read falls through to whatever was underneath it rather than
    /// failing the load, which is the rule every optional layer here follows: one bad file
    /// out of forty costs that one asset and nothing else.
    /// </remarks>
    public byte[]? Read(RebarnKind kind, string name, DiagnosticBag? diagnostics = null) =>
        ReadFile(PathOf(kind, name), diagnostics);

    /// <summary>Whether a file stands in front of the game's own archives.</summary>
    /// <param name="name">The asset's name, extension included.</param>
    /// <returns>True when one does.</returns>
    public bool HasArchive(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return _archive.ContainsKey(name);
    }

    /// <summary>Reads a file standing in front of the game's own archives.</summary>
    /// <param name="name">The asset's name, extension included.</param>
    /// <param name="diagnostics">Receives a diagnostic when the file will not read.</param>
    /// <returns>Its bytes, or null when there is no such override or it will not read.</returns>
    public byte[]? ReadArchive(string name, DiagnosticBag? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(name);

        return _archive.TryGetValue(name, out string? file)
            ? ReadFile(file, diagnostics)
            : null;
    }

    /// <summary>Every name standing in front of the archives, in a stable order.</summary>
    public IReadOnlyList<string> ArchiveNames =>
        [.. _archive.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase)];

    /// <summary>Where one file came from, for saying so out loud.</summary>
    /// <param name="kind">What it is for.</param>
    /// <param name="name">Its name.</param>
    /// <returns>The path, or null when nothing overrides it.</returns>
    public string? SourceOf(RebarnKind kind, string name) => PathOf(kind, name);

    /// <summary>A one-line summary of what is being overridden, for a startup report.</summary>
    /// <returns>The summary, or null when there is nothing.</returns>
    /// <remarks>
    /// Said out loud on purpose. An override is invisible once it is on screen — that is
    /// the point of it — so a run in which a stale file in <c>overrides/</c> is quietly
    /// standing in for the shipped one looks exactly like a run without it, and somebody
    /// chasing a rendering fault would have no way to tell.
    /// </remarks>
    public string? Describe()
    {
        if (Count == 0)
        {
            return null;
        }

        List<string> parts = [];

        foreach (RebarnKind kind in _packed.Keys.OrderBy(k => k))
        {
            parts.Add($"{_packed[kind].Count} {RebarnFormat.DirectoryOf(kind)}");
        }

        // Counted as they were added rather than subtracted from the archive layer, which
        // holds both sorts and can collide: textures/X.png and normals/X.png are two
        // overrides and one file name.
        if (_assets > 0)
        {
            parts.Add($"{_assets} game asset(s)");
        }

        return $"{Count} file(s) in {Directory}: {string.Join(", ", parts)}";
    }

    private static byte[]? ReadFile(string? file, DiagnosticBag? diagnostics)
    {
        if (file is null)
        {
            return null;
        }

        try
        {
            return File.ReadAllBytes(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diagnostics?.Add(new Diagnostic(
                "GK3R1181",
                DiagnosticSeverity.Warning,
                $"The override {Path.GetFileName(file)} will not read, so what it stands in "
                + $"for is used instead: {ex.Message}",
                file,
                null,
                "a readable file",
                ex.GetType().Name,
                "Check the file, or take it out of the overrides directory."));

            return null;
        }
    }
}
