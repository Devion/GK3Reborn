using GK3Reborn.Formats;
using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Formats.Rebarn;
using GK3Reborn.Foundation.Diagnostics;

namespace GK3Reborn.Content;

/// <summary>
/// Block-compressed textures, standing in front of everything else.
/// </summary>
/// <remarks>
/// <para>
/// The same enhanced textures as <see cref="EnhancedTextures"/>, compressed to BC7 and
/// their normal maps to BC5 by the content pipeline. It is the cheapest form there is:
/// the file goes to the device without being decoded, the mip chain is already built, and
/// it takes a quarter of the video memory. `PbrLab` measures the pilot set at 13.71 GiB
/// uncompressed against 3.43 GiB compressed, at 45.5–47.0 dB, which nobody can see.
/// </para>
/// <para>
/// One thing it cannot do is carry a colour key. <see cref="Rendering.TextureKeying"/>
/// works on texels and these are blocks, so a texture whose original uses GK3's magenta
/// has to take the decoded path — three of the 324 in the pilot set do. Deciding that is
/// the loader's business, because only the loader has the original to look at.
/// </para>
/// <para>
/// Names are matched without their extension and without regard to case, the same as every
/// other texture layer: a surface refers to <c>R25WALLS</c>, the archive holds
/// <c>R25WALLS.BMP</c>, and this holds <c>R25WALLS.dds</c>.
/// </para>
/// </remarks>
public sealed class CompressedTextures
{
    private readonly Dictionary<string, string> _colour = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _normal = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _orm = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _height = new(StringComparer.OrdinalIgnoreCase);

    private RebarnContent? _packs;

    private int _fromPacks;
    private int _fromFiles;

    private CompressedTextures(string directory) => Directory = directory;

    /// <summary>Where the textures were read from.</summary>
    public string Directory { get; }

    /// <summary>How many colour textures are available.</summary>
    public int Count => _colour.Count;

    /// <summary>How many normal maps are available.</summary>
    public int NormalCount => _normal.Count;

    /// <summary>How many packed occlusion/roughness/metalness maps are available.</summary>
    public int OrmCount => _orm.Count;

    /// <summary>How many height maps are available.</summary>
    public int HeightCount => _height.Count;

    /// <summary>How many reads this set has served out of a ReBarn pack.</summary>
    public int FromPacks => Volatile.Read(ref _fromPacks);

    /// <summary>How many reads this set has served out of a loose <c>.dds</c> file.</summary>
    public int FromFiles => Volatile.Read(ref _fromFiles);

    /// <summary>
    /// Where each set's entries come from, for a startup report.
    /// </summary>
    /// <returns>One line, or null when there is nothing at all.</returns>
    /// <remarks>
    /// Worth saying out loud because the two sources are indistinguishable once a texture is
    /// on screen, and a run that silently used a stale <c>build/</c> directory instead of the
    /// pack looks exactly like a run that used the pack.
    /// </remarks>
    public string? Describe()
    {
        if (_colour.Count == 0 && _normal.Count == 0 && _orm.Count == 0 && _height.Count == 0)
        {
            return null;
        }

        return string.Join(", ", new[]
        {
            Part("colour", _colour),
            Part("normal", _normal),
            Part("ORM", _orm),
            Part("height", _height),
        }.Where(p => p is not null));

        static string? Part(string what, Dictionary<string, string> from)
        {
            if (from.Count == 0)
            {
                return null;
            }

            int packed = from.Count(e => e.Value.Length == 0);

            return packed == from.Count ? $"{from.Count} {what} packed"
                : packed == 0 ? $"{from.Count} {what} loose"
                : $"{from.Count} {what} ({packed} packed, {from.Count - packed} loose)";
        }
    }

    /// <summary>Indexes a build directory.</summary>
    /// <param name="directory">
    /// The content workspace's <c>build</c> directory, which holds <c>textures</c>,
    /// <c>normals</c> and <c>orm</c> beside each other.
    /// </param>
    /// <returns>The set, empty when the directory does not exist.</returns>
    /// <remarks>
    /// A missing directory is not an error, the same as the enhanced set: the game runs
    /// from a legally obtained installation and this is an addition to it.
    /// </remarks>
    public static CompressedTextures Open(string directory) => Open(directory, null);

    /// <summary>Indexes a build directory, a set of ReBarn packs, or both.</summary>
    /// <param name="directory">
    /// The content workspace's <c>build</c> directory, which holds <c>textures</c>,
    /// <c>normals</c> and <c>orm</c> beside each other. May be empty or missing.
    /// </param>
    /// <param name="packs">
    /// Packs beside the executable, or null for none.
    /// </param>
    /// <returns>The set, empty when neither has anything.</returns>
    /// <remarks>
    /// Packs are indexed first and loose files overwrite them, so a texture recompressed
    /// into <c>build/</c> during a session is what gets drawn without the pack having to be
    /// rebuilt. That is the same way round as PNG beating DDS, and for the same reason: the
    /// looser and more recent thing wins while a set is still moving.
    /// </remarks>
    public static CompressedTextures Open(string directory, RebarnContent? packs)
    {
        ArgumentNullException.ThrowIfNull(directory);

        var set = new CompressedTextures(directory) { _packs = packs };

        if (packs is not null)
        {
            IndexPack(packs, RebarnKind.Texture, set._colour);
            IndexPack(packs, RebarnKind.Normal, set._normal);
            IndexPack(packs, RebarnKind.Orm, set._orm);
            IndexPack(packs, RebarnKind.Height, set._height);
        }

        // An empty directory means the packs and nothing else — what --rebarn asks for.
        // Combining it with "textures" would produce a relative path and index whatever
        // happened to be beside the working directory, which is worse than indexing nothing.
        if (directory.Length > 0)
        {
            Index(Path.Combine(directory, "textures"), set._colour);
            Index(Path.Combine(directory, "normals"), set._normal);
            Index(Path.Combine(directory, "orm"), set._orm);
            Index(Path.Combine(directory, "height"), set._height);
        }

        return set;
    }

    /// <summary>Registers a pack's names, with no path, so a read falls through to the pack.</summary>
    private static void IndexPack(
        RebarnContent packs, RebarnKind kind, Dictionary<string, string> into)
    {
        foreach (string name in packs.Names(kind))
        {
            into[name] = string.Empty;
        }
    }

    private static void Index(string directory, Dictionary<string, string> into)
    {
        if (!System.IO.Directory.Exists(directory))
        {
            return;
        }

        foreach (string file in System.IO.Directory.EnumerateFiles(directory, "*.dds"))
        {
            into[Path.GetFileNameWithoutExtension(file)] = file;
        }
    }

    /// <summary>Whether there is a compressed version of a texture.</summary>
    /// <param name="name">Texture name, with or without an extension.</param>
    /// <returns>True when there is one.</returns>
    public bool Has(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _colour.ContainsKey(Path.GetFileNameWithoutExtension(name));
    }

    /// <summary>Whether there is a compressed normal map for a texture.</summary>
    /// <param name="name">The <em>colour</em> texture's name.</param>
    /// <returns>True when there is one.</returns>
    public bool HasNormal(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _normal.ContainsKey(Path.GetFileNameWithoutExtension(name));
    }

    /// <summary>Whether there is a compressed ORM map for a texture.</summary>
    /// <param name="name">The <em>colour</em> texture's name.</param>
    /// <returns>True when there is one.</returns>
    public bool HasOrm(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _orm.ContainsKey(Path.GetFileNameWithoutExtension(name));
    }

    /// <summary>Whether there is a compressed height map for a texture.</summary>
    /// <param name="name">The <em>colour</em> texture's name.</param>
    /// <returns>True when there is one.</returns>
    public bool HasHeight(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _height.ContainsKey(Path.GetFileNameWithoutExtension(name));
    }

    /// <summary>Reads a compressed texture.</summary>
    /// <param name="name">Texture name, with or without an extension.</param>
    /// <param name="diagnostics">Receives a diagnostic when one will not read.</param>
    /// <returns>The texture, or null when there is none or it is unreadable.</returns>
    public CompressedImage? Read(string name, DiagnosticBag? diagnostics = null) =>
        Read(_colour, RebarnKind.Texture, name, "texture", diagnostics);

    /// <summary>Reads a compressed normal map.</summary>
    /// <param name="name">The colour texture's name.</param>
    /// <param name="diagnostics">Receives a diagnostic when one will not read.</param>
    /// <returns>The map, or null when there is none or it is unreadable.</returns>
    public CompressedImage? ReadNormal(string name, DiagnosticBag? diagnostics = null) =>
        Read(_normal, RebarnKind.Normal, name, "normal map", diagnostics);

    /// <summary>Reads a compressed occlusion/roughness/metalness map.</summary>
    /// <param name="name">The colour texture's name.</param>
    /// <param name="diagnostics">Receives a diagnostic when one will not read.</param>
    /// <returns>The map, or null when there is none or it is unreadable.</returns>
    /// <remarks>
    /// Three channels rather than two, so BC7 rather than BC5 — and linear either way. An
    /// ORM uploaded through the sRGB path comes back with every roughness pulled towards
    /// one end of its range, which reads as a material problem rather than as the colour
    /// space bug it is.
    /// </remarks>
    public CompressedImage? ReadOrm(string name, DiagnosticBag? diagnostics = null) =>
        Read(_orm, RebarnKind.Orm, name, "ORM map", diagnostics);

    /// <summary>Reads a compressed height map.</summary>
    /// <param name="name">The colour texture's name.</param>
    /// <param name="diagnostics">Receives a diagnostic when one will not read.</param>
    /// <returns>The map, or null when there is none or it is unreadable.</returns>
    public CompressedImage? ReadHeight(string name, DiagnosticBag? diagnostics = null) =>
        Read(_height, RebarnKind.Height, name, "height map", diagnostics);

    /// <remarks>
    /// A file that will not read falls back rather than failing the load, exactly as the
    /// enhanced set does: generated content is a draft until somebody has looked at it, and
    /// one bad file in a set of hundreds should cost that texture and nothing else.
    /// </remarks>
    private CompressedImage? Read(
        Dictionary<string, string> from,
        RebarnKind kind,
        string name,
        string what,
        DiagnosticBag? diagnostics)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (!from.TryGetValue(Path.GetFileNameWithoutExtension(name), out string? file))
        {
            return null;
        }

        // An empty path is a name that only a pack holds. The pack hands back a window onto
        // its own mapping rather than a copy, which is what makes this the cheapest path
        // there is: nothing is read, decoded or allocated between the file and the device.
        if (file.Length == 0)
        {
            Interlocked.Increment(ref _fromPacks);
            return _packs?.ReadTexture(kind, name, diagnostics);
        }

        Interlocked.Increment(ref _fromFiles);

        try
        {
            return DdsFile.Read(File.ReadAllBytes(file), file);
        }
        catch (Exception ex) when (ex is FormatParseException or IOException)
        {
            diagnostics?.Add(new Diagnostic(
                "GK3R1095",
                DiagnosticSeverity.Warning,
                $"The compressed {what} for {name} will not load, so it is skipped: {ex.Message}",
                file,
                null,
                "a readable DDS",
                ex.GetType().Name,
                "Produce it again, or take it out of the build directory."));

            return null;
        }
    }
}
