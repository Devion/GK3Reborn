using GK3Reborn.Formats.Audio;
using GK3Reborn.Foundation.Diagnostics;

namespace GK3Reborn.Content;

/// <summary>
/// The game's sounds, decoded and read on demand.
/// </summary>
/// <remarks>
/// <para>
/// Restored masters come from ReBarn when one exists; the original archive remains the
/// complete fallback. 97.5% of the originals are an MP3 inside a RIFF header, and
/// <see cref="WavFile"/> decodes those where it finds them.
/// </para>
/// <para>
/// Names keep their extension, and a script may not give one. A line of dialogue is
/// <c>A0NQIB44.QR1</c> — where the last characters are a sequence number rather than a
/// type, so two sounds can differ only in them — while a soundtrack asks for
/// <c>R25Theme1</c> and means <c>R25THEME1.WAV</c>. Both spellings have to reach the same
/// file, which is why an extensionless name is tried again with <c>.WAV</c>.
/// </para>
/// </remarks>
public sealed class SoundLibrary
{
    private readonly Func<string, byte[]?> _open;
    private readonly Func<string, bool> _exists;
    private readonly Dictionary<string, WavFile?> _read = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Forgets every sound it has decoded.
    /// </summary>
    /// <remarks>
    /// For the one thing that changes what a name means: the language. Every line in the
    /// game is a different recording in French, and a cache keyed on the name alone would
    /// go on answering with the English one for the rest of the session.
    /// </remarks>
    public void Forget() => _read.Clear();

    /// <summary>Creates a library.</summary>
    /// <param name="archives">The game's archives.</param>
    public SoundLibrary(GameArchives archives)
        : this(NotNull(archives).Read, NotNull(archives).Exists)
    {
    }

    /// <summary>Creates a library over overrides, restored packs, then original barns.</summary>
    /// <param name="archives">The original game's complete fallback.</param>
    /// <param name="packs">The remake's optional restored content.</param>
    public SoundLibrary(GameArchives archives, RebarnContent? packs)
        : this(
            name => ReadLayered(NotNull(archives), packs, name),
            name => HasLayered(NotNull(archives), packs, name))
    {
    }

    /// <summary>Creates a library over anything that can produce a file's bytes.</summary>
    /// <param name="open">Given a full file name, returns its bytes or null.</param>
    /// <param name="exists">
    /// Whether a file is there, without reading it. Kept apart from
    /// <paramref name="open"/> because a soundtrack is a five-minute MP3 and choosing
    /// between them must not cost a decode each.
    /// </param>
    public SoundLibrary(Func<string, byte[]?> open, Func<string, bool>? exists = null)
    {
        ArgumentNullException.ThrowIfNull(open);

        _open = open;
        _exists = exists ?? (name => open(name) is not null);
    }

    private static GameArchives NotNull(GameArchives archives)
    {
        ArgumentNullException.ThrowIfNull(archives);
        return archives;
    }

    /// <summary>
    /// Overrides, then the language, then the restored masters, then the archives.
    /// </summary>
    /// <remarks>
    /// <b>The language sits above the restorations, and that is the whole of the rule.</b>
    /// A restored master is a cleaned-up copy of a recording in one language; a language
    /// pack holds a different actor saying a different sentence. Handing a French game an
    /// English master because the English one had been remastered would be the loudest
    /// possible way to get this wrong, so the language wins wherever it has an answer.
    /// <para>
    /// Which leaves the English case, and it is handled where it belongs: the English pack
    /// simply leaves out the lines <c>enhanced/audio</c> restores, so those fall through to
    /// the restored master rather than being shadowed by the 1999 recording of the same
    /// line. See <c>docs/localization.md</c>.
    /// </para>
    /// </remarks>
    private static byte[]? ReadLayered(
        GameArchives archives, RebarnContent? packs, string name)
    {
        ContentOverrides? overrides = archives.Overrides ?? packs?.Overrides;

        return overrides?.ReadArchive(name)
            ?? overrides?.Read(Formats.Rebarn.RebarnKind.Audio, name)
            ?? archives.Localization?.Read(name)
            ?? packs?.Read(Formats.Rebarn.RebarnKind.Audio, name)
            ?? archives.Read(name);
    }

    private static bool HasLayered(
        GameArchives archives, RebarnContent? packs, string name)
    {
        ContentOverrides? overrides = archives.Overrides ?? packs?.Overrides;

        return overrides?.HasArchive(name) == true
            || overrides?.Has(Formats.Rebarn.RebarnKind.Audio, name) == true
            || archives.Localization?.HasArchive(name) == true
            || packs?.Has(Formats.Rebarn.RebarnKind.Audio, name) == true
            || archives.Exists(name);
    }

    /// <summary>Diagnostics raised while reading.</summary>
    public DiagnosticBag Diagnostics { get; } = new();

    /// <summary>Reads a sound, or returns what was read before.</summary>
    /// <param name="name">Its name, extension and all.</param>
    /// <returns>The sound, or null when there is none that can be played.</returns>
    public WavFile? Read(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        // Locked, because a room decodes its ambience on another thread while the player is
        // being shown the first frames of it. Held across the decode rather than only across
        // the dictionary: two threads asking for the same sound should decode it once, and a
        // soundtrack is a quarter of a second of work.
        lock (_read)
        {
            if (_read.TryGetValue(name, out WavFile? cached))
            {
                return cached;
            }

            WavFile? sound = null;

            foreach (string candidate in Spellings(name))
            {
                if (_open(candidate) is { } bytes)
                {
                    sound = WavFile.Read(bytes, name, Diagnostics);
                }

                if (sound is not null)
                {
                    break;
                }
            }

            _read[name] = sound;
            return sound;
        }
    }

    /// <summary>Whether a sound exists, without reading or decoding it.</summary>
    /// <param name="name">Its name, with or without an extension.</param>
    /// <returns>True when an archive holds it under either spelling.</returns>
    public bool Has(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        foreach (string candidate in Spellings(name))
        {
            if (_exists(candidate))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The names a sound might be stored under.</summary>
    /// <remarks>
    /// As given first, because a name whose extension is a sequence number is already exact
    /// and appending to it would find nothing. Then with <c>.WAV</c>, which is what the
    /// original appends when an asset is named without a type.
    /// </remarks>
    private static IEnumerable<string> Spellings(string name)
    {
        yield return name;

        if (Path.GetExtension(name).Length == 0)
        {
            yield return name + ".WAV";
        }
    }

    /// <summary>How long a sound lasts.</summary>
    /// <param name="name">Its name.</param>
    /// <returns>Seconds, or zero when there is no such sound.</returns>
    public double SecondsOf(string name) => Read(name)?.Duration ?? 0;
}
