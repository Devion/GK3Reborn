using GK3Reborn.Formats.Audio;
using GK3Reborn.Foundation.Diagnostics;

namespace GK3Reborn.Content;

/// <summary>
/// The game's sounds, decoded and read on demand.
/// </summary>
/// <remarks>
/// <para>
/// Looks in the content workspace's <c>normalized/audio-pcm</c> first and the archives
/// second. That order is the whole point: 97.5% of the archives' sounds are an MP3 inside
/// a RIFF header and the runtime does not decode, so the workspace copy that
/// <c>import-audio</c> produced is the only playable one. The archives still answer for
/// the 196 that were always PCM, which means a player who has not run the import gets
/// footsteps and silence rather than a crash.
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
    private readonly string? _decoded;
    private readonly Dictionary<string, WavFile?> _read = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates a library.</summary>
    /// <param name="archives">The game's archives.</param>
    /// <param name="decodedDirectory">
    /// Where <c>import-audio</c> put its output, or null to use the archives alone.
    /// </param>
    public SoundLibrary(GameArchives archives, string? decodedDirectory)
    {
        ArgumentNullException.ThrowIfNull(archives);

        _archives = archives;
        _decoded = decodedDirectory is { Length: > 0 } d && Directory.Exists(d) ? d : null;
    }

    /// <summary>Diagnostics raised while reading.</summary>
    public DiagnosticBag Diagnostics { get; } = new();

    /// <summary>Whether the decoded store is there.</summary>
    public bool HasDecoded => _decoded is not null;

    /// <summary>How many decoded sounds are available.</summary>
    public int DecodedCount =>
        _decoded is null ? 0 : Directory.EnumerateFiles(_decoded, "*.wav").Count();

    /// <summary>Reads a sound, or returns what was read before.</summary>
    /// <param name="name">Its name, extension and all.</param>
    /// <returns>The sound, or null when there is none that can be played.</returns>
    public WavFile? Read(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (_read.TryGetValue(name, out WavFile? cached))
        {
            return cached;
        }

        WavFile? sound = null;

        foreach (string candidate in Spellings(name))
        {
            if (_decoded is not null)
            {
                string path = Path.Combine(_decoded, candidate + ".wav");

                if (File.Exists(path))
                {
                    sound = WavFile.Read(File.ReadAllBytes(path), name, Diagnostics);
                }
            }

            if (sound is null && _archives.Read(candidate) is { } bytes)
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
