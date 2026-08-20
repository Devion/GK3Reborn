using GK3Reborn.Formats.Audio;
using GK3Reborn.Foundation.Diagnostics;

namespace GK3Reborn.Content;

/// <summary>
/// The game's sounds, decoded and read on demand.
/// </summary>
/// <remarks>
/// <para>
/// Straight out of the archives. 97.5% of the game's sounds are an MP3 inside a RIFF
/// header, and <see cref="WavFile"/> decodes those where it finds them, so there is nothing
/// to import and no second place to look. A decoded copy of the corpus used to sit in the
/// content workspace and cost 3.7 GB to save about eight milliseconds a sound.
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
    private readonly GameArchives _archives;
    private readonly Dictionary<string, WavFile?> _read = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates a library.</summary>
    /// <param name="archives">The game's archives.</param>
    public SoundLibrary(GameArchives archives)
    {
        ArgumentNullException.ThrowIfNull(archives);

        _archives = archives;
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
                if (_archives.Read(candidate) is { } bytes)
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
            if (_archives.Exists(candidate))
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
