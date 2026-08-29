using GK3Reborn.Formats.Rebarn;

namespace GK3Reborn.Content;

/// <summary>
/// The game's movies, from the packs or from the workspace beside them.
/// </summary>
/// <remarks>
/// <para>
/// GK3 refers to a movie by name and never by file: <c>PlayFullScreenMovie("212pBegin")</c>
/// and the disc holds <c>212pbegin.bik</c>. G-Engine's <c>VideoHelper</c> strips the
/// extension deliberately, because some locales ship AVI where others ship BIK — so the
/// name is the identity and the container is an implementation detail. Forty of them
/// survive the import as H.264 in MP4.
/// </para>
/// <para>
/// Two places to find one, and which of them is looked in is the player's decision.
/// <b><c>--rebarn</c> means the packs and nothing else</b>, which is the only way to
/// measure what the shipped form does; without it the loose <c>enhanced/video</c>
/// directory is read as well and <b>wins</b>, so a movie re-imported during a session
/// plays without the pack having to be rebuilt. That is the same way round as the
/// textures, and for the same reason: the looser and more recent thing wins while a set is
/// still moving.
/// </para>
/// <para>
/// What comes back is a <em>stream</em> rather than a file or an array. A movie in a pack
/// is a window onto a memory mapping and a long one is a hundred megabytes, so copying it
/// into the heap to play it would cost more than decoding it does.
/// </para>
/// </remarks>
public sealed class VideoLibrary
{
    /// <summary>
    /// Containers a movie may arrive in, most preferred first.
    /// </summary>
    /// <remarks>
    /// The import writes MP4. The others are here because the pipeline may be told to
    /// write Matroska instead — <c>Plan/02</c> allows either — and because a name is
    /// supposed to outlive the container it happens to be in.
    /// </remarks>
    private static readonly string[] Containers = [".mp4", ".mkv", ".webm", ".avi"];

    private readonly Dictionary<string, string> _loose = new(StringComparer.OrdinalIgnoreCase);
    private readonly RebarnContent? _packs;

    private VideoLibrary(string directory, RebarnContent? packs)
    {
        Directory = directory;
        _packs = packs;
    }

    /// <summary>Where the loose movies were looked for.</summary>
    public string Directory { get; }

    /// <summary>How many distinct movies can be played.</summary>
    public int Count => Names.Count;

    /// <summary>How many of them are loose files rather than packed.</summary>
    public int LooseCount => _loose.Count;

    /// <summary>How many are in the packs, whether or not a loose file covers them.</summary>
    public int PackedCount => _packs?.CountOf(RebarnKind.Video) ?? 0;

    /// <summary>Every movie's name, in a stable order and without extensions.</summary>
    public IReadOnlyList<string> Names =>
        [.. _loose.Keys
            .Concat(_packs?.Names(RebarnKind.Video) ?? [])
            .Select(Key)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)];

    /// <summary>Indexes the movies a run can play.</summary>
    /// <param name="directory">
    /// The workspace's <c>enhanced/video</c>, or empty for none. Empty is what
    /// <c>--rebarn</c> passes: the packs are then the whole of the answer.
    /// </param>
    /// <param name="packs">The packs beside the executable, or null for none.</param>
    /// <returns>The set, empty when neither has anything.</returns>
    public static VideoLibrary Open(string directory, RebarnContent? packs = null)
    {
        ArgumentNullException.ThrowIfNull(directory);

        var set = new VideoLibrary(directory, packs);

        if (directory.Length == 0 || !System.IO.Directory.Exists(directory))
        {
            return set;
        }

        foreach (string file in System.IO.Directory.EnumerateFiles(directory))
        {
            // By extension rather than by trying to open everything: the directory also
            // holds whatever the import left beside the movies.
            if (!Containers.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            string name = Key(file);

            // Two containers of the same movie is the import having been run twice with
            // different settings. The preferred container wins rather than the last one
            // the directory happened to list.
            if (!set._loose.TryGetValue(name, out string? held) || Better(file, held))
            {
                set._loose[name] = file;
            }
        }

        return set;
    }

    /// <summary>Whether a movie of that name can be played.</summary>
    /// <param name="name">Its name, with or without an extension.</param>
    /// <returns>True when one of the sources has it.</returns>
    public bool Has(string? name) =>
        name is { Length: > 0 } &&
        (_loose.ContainsKey(Key(name)) || (_packs?.Has(RebarnKind.Video, name) ?? false));

    /// <summary>Where a movie will be read from, for saying so out loud.</summary>
    /// <param name="name">Its name.</param>
    /// <returns>A description, or null when there is no such movie.</returns>
    public string? Source(string? name)
    {
        if (name is not { Length: > 0 })
        {
            return null;
        }

        if (_loose.TryGetValue(Key(name), out string? file))
        {
            return file;
        }

        return _packs?.SourceOf(RebarnKind.Video, name);
    }

    /// <summary>Opens a movie for reading.</summary>
    /// <param name="name">Its name, with or without an extension.</param>
    /// <returns>A seekable stream, or null when there is no such movie.</returns>
    /// <remarks>
    /// Seekable because a decoder needs to be: an MP4's index may sit at either end of the
    /// file, and although the import writes <c>+faststart</c> so that it sits at the
    /// front, a movie that came from somewhere else need not.
    /// </remarks>
    public Stream? Open(string? name)
    {
        if (name is not { Length: > 0 })
        {
            return null;
        }

        if (_loose.TryGetValue(Key(name), out string? file))
        {
            return File.OpenRead(file);
        }

        // A window onto the pack's mapping rather than a copy of it. The pack outlives the
        // playback — it is opened once for the session — so the window stays valid. An
        // override in front of it comes back as an ordinary file handle instead.
        return _packs?.OpenStream(RebarnKind.Video, name);
    }

    /// <summary>The name a movie is known by: no directory, no extension.</summary>
    private static string Key(string path) => Path.GetFileNameWithoutExtension(path);

    /// <summary>Whether one container is preferred over another.</summary>
    private static bool Better(string candidate, string held) =>
        Rank(candidate) < Rank(held);

    private static int Rank(string path)
    {
        int at = Array.FindIndex(
            Containers,
            c => string.Equals(c, Path.GetExtension(path), StringComparison.OrdinalIgnoreCase));

        return at < 0 ? Containers.Length : at;
    }
}
