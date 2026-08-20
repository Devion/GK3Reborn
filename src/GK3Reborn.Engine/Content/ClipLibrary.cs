using GK3Reborn.Formats.Animation;
using GK3Reborn.Foundation.Diagnostics;

namespace GK3Reborn.Content;

/// <summary>
/// The game's vertex animation clips, read on demand.
/// </summary>
/// <remarks>
/// <para>
/// 5,796 of them and 399 MB, so they are read when something asks and kept afterwards —
/// and read <b>without their vertex data</b> by default. The shapes are 92 million samples
/// across the corpus and only skinning needs them; a door swinging needs its mesh
/// transforms, which are a rounding error beside the rest.
/// </para>
/// <para>
/// A clip names the model it targets in its header, and the filename is not reliable: 12.9%
/// of the corpus is named for something other than what it animates. Anything pairing a
/// clip to a model must use <see cref="ActFile.ModelName"/>.
/// </para>
/// </remarks>
public sealed class ClipLibrary
{
    private readonly Func<string, byte[]?> _open;
    private readonly Dictionary<string, ActFile?> _read = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates a library over a set of archives.</summary>
    /// <param name="archives">Where the clips are.</param>
    public ClipLibrary(GameArchives archives)
        : this(NotNull(archives).Read)
    {
    }

    /// <summary>Creates a library over anything that can produce a file's bytes.</summary>
    /// <param name="open">Given a full file name, returns its bytes or null.</param>
    public ClipLibrary(Func<string, byte[]?> open)
    {
        ArgumentNullException.ThrowIfNull(open);
        _open = open;
    }

    private static GameArchives NotNull(GameArchives archives)
    {
        ArgumentNullException.ThrowIfNull(archives);
        return archives;
    }

    /// <summary>Diagnostics raised while reading.</summary>
    public DiagnosticBag Diagnostics { get; } = new();

    /// <summary>Whether to keep vertex poses, which is most of what a clip is.</summary>
    /// <remarks>
    /// Off. Nothing plays deformation yet, and turning it on for <c>gab</c> alone would be
    /// 50.2 million vertex samples resident.
    /// </remarks>
    public bool KeepVertices { get; set; }

    /// <summary>How many distinct names have been asked for.</summary>
    public int Count => _read.Count;

    /// <summary>Reads a clip, or returns what was read before.</summary>
    /// <param name="name">Its name, with or without the extension.</param>
    /// <returns>The clip, or null when there is no such file.</returns>
    public ActFile? Read(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (_read.TryGetValue(name, out ActFile? cached))
        {
            return cached;
        }

        string bare = Path.GetFileNameWithoutExtension(name);

        ActFile? clip = _open($"{bare}.ACT") is { } bytes
            ? ActFile.Read(bytes, bare, Diagnostics, KeepVertices)
            : null;

        _read[name] = clip;
        return clip;
    }
}
