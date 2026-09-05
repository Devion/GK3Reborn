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

    /// <summary>
    /// Containers a loose per-language soundtrack may arrive in, most preferred first.
    /// </summary>
    /// <remarks>
    /// The import writes <c>.m4a</c> — the movie's own AAC track copied out of the MP4
    /// without re-encoding it, which costs a second and loses nothing. The others are here
    /// because a soundtrack somebody produced by hand may be anything, and because the
    /// decoder reads a bare MP4 with no video track exactly as it reads one with.
    /// </remarks>
    private static readonly string[] SoundContainers = [".m4a", ".mp4", ".wav"];

    private readonly Dictionary<string, string> _loose = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _looseLocal = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _looseSound = new(StringComparer.OrdinalIgnoreCase);
    private readonly RebarnContent? _packs;
    private readonly LocalizedContent? _localized;

    private VideoLibrary(string directory, RebarnContent? packs, LocalizedContent? localized)
    {
        Directory = directory;
        _packs = packs;
        _localized = localized;
    }

    /// <summary>Where the loose movies were looked for.</summary>
    public string Directory { get; }

    /// <summary>How many distinct movies can be played.</summary>
    public int Count => Names.Count;

    /// <summary>How many of them are loose files rather than packed.</summary>
    public int LooseCount => _loose.Count;

    /// <summary>How many are in the packs, whether or not a loose file covers them.</summary>
    public int PackedCount => _packs?.CountOf(RebarnKind.Video) ?? 0;

    /// <summary>How many of them this language has a picture of its own for.</summary>
    public int LocalizedCount =>
        _looseLocal.Count + (_localized?.CountOf(RebarnKind.Video) ?? 0);

    /// <summary>How many of them this language has a soundtrack of its own for.</summary>
    public int LocalizedSoundCount =>
        _looseSound.Count + (_localized?.CountOf(RebarnKind.MovieAudio) ?? 0);

    /// <summary>Every movie's name, in a stable order and without extensions.</summary>
    public IReadOnlyList<string> Names =>
        [.. _loose.Keys
            .Concat(_looseLocal.Keys)
            .Concat(_packs?.Names(RebarnKind.Video) ?? [])
            .Concat(_localized?.Names(RebarnKind.Video) ?? [])
            .Select(Key)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)];

    /// <summary>Indexes the movies a run can play.</summary>
    /// <param name="directory">
    /// The workspace's <c>enhanced/video</c>, or empty for none. Empty is what
    /// <c>--rebarn</c> passes: the packs are then the whole of the answer.
    /// </param>
    /// <param name="packs">The packs beside the executable, or null for none.</param>
    /// <param name="localized">The language pack, or null when the shared cut serves.</param>
    /// <param name="localizedDirectory">
    /// The workspace's <c>enhanced/localized/&lt;CODE&gt;</c>, or empty for none. Its
    /// <c>video</c> directory holds whole movies this language re-cut and its
    /// <c>movie-audio</c> directory holds soundtracks for the ones it did not — the two
    /// directories the packer takes those kinds from, so a loose set and a packed one are
    /// laid out the same way. Preferred over the pack, for the same reason the loose
    /// shared directory is preferred over the shared pack.
    /// </param>
    /// <returns>The set, empty when none of them has anything.</returns>
    /// <remarks>
    /// <b>The language outranks the shared cut, loose or packed.</b> That is the one place
    /// this differs from the texture stack, and it has to: a shared picture where the
    /// language has its own is not a stale picture, it is the wrong one. Four of GK3's
    /// sixteen spoken movies are a different length in French — <c>day3-3</c> is 430
    /// seconds in English and 153 in French — so playing the shared cut under a French
    /// soundtrack would drift apart within seconds and end three minutes early.
    /// </remarks>
    public static VideoLibrary Open(
        string directory,
        RebarnContent? packs = null,
        LocalizedContent? localized = null,
        string localizedDirectory = "")
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(localizedDirectory);

        var set = new VideoLibrary(directory, packs, localized);

        Index(directory, Containers, set._loose);

        if (localizedDirectory.Length > 0)
        {
            Index(
                Path.Combine(localizedDirectory, RebarnFormat.DirectoryOf(RebarnKind.Video)),
                Containers,
                set._looseLocal);

            Index(
                Path.Combine(localizedDirectory, RebarnFormat.DirectoryOf(RebarnKind.MovieAudio)),
                SoundContainers,
                set._looseSound);
        }

        return set;
    }

    /// <summary>Indexes one directory's files of the containers a kind arrives in.</summary>
    private static void Index(
        string directory, string[] containers, Dictionary<string, string> into)
    {
        if (directory.Length == 0 || !System.IO.Directory.Exists(directory))
        {
            return;
        }

        foreach (string file in System.IO.Directory.EnumerateFiles(directory))
        {
            // By extension rather than by trying to open everything: the directory also
            // holds whatever the import left beside the movies.
            if (!containers.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            string name = Key(file);

            // Two containers of the same movie is the import having been run twice with
            // different settings. The preferred container wins rather than the last one
            // the directory happened to list.
            if (!into.TryGetValue(name, out string? held) || Better(containers, file, held))
            {
                into[name] = file;
            }
        }
    }

    /// <summary>Whether a movie of that name can be played.</summary>
    /// <param name="name">Its name, with or without an extension.</param>
    /// <returns>True when one of the sources has it.</returns>
    public bool Has(string? name) =>
        name is { Length: > 0 } &&
        (_looseLocal.ContainsKey(Key(name)) ||
         (_localized?.Has(RebarnKind.Video, name) ?? false) ||
         _loose.ContainsKey(Key(name)) ||
         (_packs?.Has(RebarnKind.Video, name) ?? false));

    /// <summary>Where a movie will be read from, for saying so out loud.</summary>
    /// <param name="name">Its name.</param>
    /// <returns>A description, or null when there is no such movie.</returns>
    public string? Source(string? name)
    {
        if (name is not { Length: > 0 })
        {
            return null;
        }

        if (_looseLocal.TryGetValue(Key(name), out string? localised))
        {
            return localised;
        }

        if (_localized?.SourceOf(RebarnKind.Video, name) is { } packed)
        {
            return packed;
        }

        if (_loose.TryGetValue(Key(name), out string? file))
        {
            return file;
        }

        return _packs?.SourceOf(RebarnKind.Video, name);
    }

    /// <summary>Where a movie's soundtrack will come from, when it is not the movie.</summary>
    /// <param name="name">The movie's name.</param>
    /// <returns>A description, or null when the movie's own sound is used.</returns>
    public string? SoundSource(string? name)
    {
        if (name is not { Length: > 0 })
        {
            return null;
        }

        return _looseSound.TryGetValue(Key(name), out string? file)
            ? file
            : _localized?.SourceOf(RebarnKind.MovieAudio, name);
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

        if (_looseLocal.TryGetValue(Key(name), out string? localised))
        {
            return File.OpenRead(localised);
        }

        if (_localized?.OpenMovie(name) is { } packedLocal)
        {
            return packedLocal;
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

    /// <summary>Opens the soundtrack to play over a movie instead of its own.</summary>
    /// <param name="name">The movie's name, with or without an extension.</param>
    /// <returns>A seekable stream, or null when the movie's own sound is what to play.</returns>
    /// <remarks>
    /// <para>
    /// Null is the ordinary answer and means "the picture and the sound are one file", which
    /// is true of every movie in a game running in the language its footage was cut for.
    /// A language that shares the picture and not the words has an entry here instead, and
    /// it costs a few megabytes rather than the hundred a second copy of the picture would.
    /// </para>
    /// <para>
    /// Never consulted for a movie <see cref="Open(string?)"/> answered out of the language's own
    /// <c>video</c> — that file already carries its own sound. <see cref="Movie"/> asks in
    /// that order for exactly that reason.
    /// </para>
    /// </remarks>
    public Stream? OpenSound(string? name)
    {
        if (name is not { Length: > 0 })
        {
            return null;
        }

        return _looseSound.TryGetValue(Key(name), out string? file)
            ? File.OpenRead(file)
            : _localized?.OpenMovieSound(name);
    }

    /// <summary>Whether a movie's picture comes from the language rather than the shared cut.</summary>
    /// <param name="name">The movie's name.</param>
    /// <returns>True when this language re-cut it.</returns>
    public bool IsLocalized(string? name) =>
        name is { Length: > 0 } &&
        (_looseLocal.ContainsKey(Key(name)) || (_localized?.Has(RebarnKind.Video, name) ?? false));

    /// <summary>The name a movie is known by: no directory, no extension.</summary>
    private static string Key(string path) => Path.GetFileNameWithoutExtension(path);

    /// <summary>Whether one container is preferred over another.</summary>
    private static bool Better(string[] containers, string candidate, string held) =>
        Rank(containers, candidate) < Rank(containers, held);

    private static int Rank(string[] containers, string path)
    {
        int at = Array.FindIndex(
            containers,
            c => string.Equals(c, Path.GetExtension(path), StringComparison.OrdinalIgnoreCase));

        return at < 0 ? containers.Length : at;
    }
}
