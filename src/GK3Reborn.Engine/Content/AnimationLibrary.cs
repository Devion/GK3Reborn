using GK3Reborn.Formats.Animation;
using GK3Reborn.Foundation.Diagnostics;

namespace GK3Reborn.Content;

/// <summary>
/// The game's animations, read on demand.
/// </summary>
public sealed class AnimationLibrary
{
    private readonly Func<string, string?> _open;
    private readonly Dictionary<string, AnimationFile?> _read =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates a library over a set of archives.</summary>
    /// <param name="archives">Where the animations are.</param>
    public AnimationLibrary(GameArchives archives)
        : this(NotNull(archives).ReadText)
    {
    }

    /// <summary>Creates a library over anything that can produce a file's text.</summary>
    /// <param name="open">
    /// Given a full file name, returns its text or null. Separate from the archives so that
    /// the naming — which extension, which language — can be exercised without a copy of
    /// the game, since that naming is the whole difficulty.
    /// </param>
    public AnimationLibrary(Func<string, string?> open)
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

    /// <summary>How many distinct names have been asked for.</summary>
    public int Count => _read.Count;

    /// <summary>The language whose dialogue is loaded.</summary>
    public char Language { get; set; } = 'E';

    /// <summary>Reads an animation, or returns what was read before.</summary>
    /// <param name="name">Its name, with or without an extension.</param>
    /// <returns>The animation, or null when there is no such file.</returns>
    public AnimationFile? Read(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (_read.TryGetValue(name, out AnimationFile? cached))
        {
            return cached;
        }

        string bare = Path.GetFileNameWithoutExtension(name);
        string spoken = Language + bare;

        string? text =
            _open($"{bare}.ANM") ??
            _open($"{bare}.YAK") ??
            _open($"{spoken}.YAK") ??
            _open($"{spoken}.ANM") ??
            _open($"{bare}.MOM") ??
            _open($"{spoken}.MOM");

        AnimationFile? animation = text is null
            ? null
            : AnimationFile.Parse(text, bare, Diagnostics);

        _read[name] = animation;
        return animation;
    }

    /// <summary>How long an animation lasts.</summary>
    /// <param name="name">Its name.</param>
    /// <returns>Seconds, or zero when there is no such animation.</returns>
    public double SecondsOf(string name) => Read(name)?.Duration ?? 0;

    /// <summary>How long a voice-over lasts.</summary>
    /// <param name="plate">The licence plate the script gave.</param>
    /// <param name="lines">How many lines follow it, itself included.</param>
    /// <returns>Seconds, or zero when none of them could be found.</returns>
    public double SecondsOfVoiceOver(string plate, int lines)
    {
        ArgumentNullException.ThrowIfNull(plate);

        if (plate.Length == 0)
        {
            return 0;
        }

        string stem = plate[..^1];
        int first = Sequence(plate[^1]);
        double total = 0;

        for (int i = 0; i < Math.Max(1, lines); i++)
        {
            total += SecondsOf(stem + Digit(first + i));
        }

        return total;
    }

    /// <summary>Reads the sequence number a plate ends with.</summary>
    private static int Sequence(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'A' and <= 'Z' => c - 'A' + 10,
        >= 'a' and <= 'z' => c - 'a' + 10,
        _ => 0,
    };

    /// <summary>Writes a sequence number back into a plate.</summary>
    private static char Digit(int value) => value switch
    {
        >= 0 and <= 9 => (char)('0' + value),
        >= 10 and <= 35 => (char)('A' + value - 10),
        _ => '0',
    };
}
