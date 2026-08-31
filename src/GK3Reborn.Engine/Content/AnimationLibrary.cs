using GK3Reborn.Formats.Animation;
using GK3Reborn.Foundation.Diagnostics;

namespace GK3Reborn.Content;

/// <summary>
/// The game's animations, read on demand.
/// </summary>
/// <remarks>
/// <para>
/// There are 14,234 of them across <c>.ANM</c> and <c>.YAK</c>, so they are read when
/// something asks and kept afterwards. A name that is not there is remembered as not
/// there, because the thing most likely to ask twice is a script in a loop.
/// </para>
/// <para>
/// What asks is <see cref="Game.Gk3SheepApi.SecondsFor"/>, on behalf of a script that said
/// <c>wait</c>. Until this existed the answer was always zero and every waited call in the
/// game was over in the frame it started.
/// </para>
/// </remarks>
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
    /// <remarks>
    /// <para>
    /// Spoken assets are localised by prefixing the language's letter to the name, so the
    /// English recording of the line <c>0NQIB44QR1</c> is <c>E0NQIB44QR1.YAK</c>. Scripts
    /// never write the prefix; the engine adds it, which is why a plate taken straight from
    /// an action file matches nothing on disk.
    /// </para>
    /// <para>
    /// It made the difference between none of the game's 4,642 voice-overs having a length
    /// and 99% of them having one.
    /// </para>
    /// </remarks>
    public char Language { get; set; } = 'E';

    /// <summary>Reads an animation, or returns what was read before.</summary>
    /// <param name="name">Its name, with or without an extension.</param>
    /// <returns>The animation, or null when there is no such file.</returns>
    /// <remarks>
    /// <para>
    /// A script names an animation without saying which kind it is or what language it is
    /// in, so six names are tried: the plain <c>.ANM</c> and <c>.YAK</c> that ordinary
    /// animations use, then the localised pair that recorded dialogue uses, then
    /// <c>.MOM</c>. The plain names come first because they are the ones that exist for
    /// everything that is not speech.
    /// </para>
    /// <para>
    /// <b><c>.MOM</c> is the same file in a different directory, and leaving it out meant
    /// <c>StartMom</c> silently played nothing.</b> A moment is a scripted beat that is not
    /// tied to one character - a spit take, a handshake, a toast - and there are 39 of them.
    /// The reference registers the extension for the same asset type the other two use and
    /// tags it, so only a moment's own lookup consults it (<c>GEngine.cpp</c>'s
    /// <c>AddTypeExtension&lt;Animation&gt;(".MOM", "mom")</c>); it is last here instead,
    /// which comes to the same thing on this corpus - <c>DEFAULT</c> is the one name that
    /// exists as both, and the <c>.ANM</c> wins there in the reference too.
    /// </para>
    /// <para>
    /// The dining room is what it cost. <c>StartMom("coffeepot")</c> is the beat where
    /// Gabriel spits his coffee out and Mosely folds his newspaper onto the table; without
    /// the extension the asset was never found, so Gabriel drank in silence, five sounds
    /// never played, and Mosely held the paper up through the whole conversation that
    /// followed.
    /// </para>
    /// <para>
    /// Opening the file was half of it. Its two lines and its cut to <c>VIEW_OF_SPIT</c>
    /// are <c>[GK3]</c> nodes that the animation reader was then dropping — see
    /// <see cref="Formats.Animation.AnimationDialogue"/>.
    /// </para>
    /// </remarks>
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
    /// <remarks>
    /// <para>
    /// <c>StartVoiceOver("1LLJ644QR1", 3)</c> does not name three assets. The last character
    /// of the plate is a sequence number — digits are themselves, letters carry on from ten
    /// — and each line is the plate with the next number in that place. So three lines are
    /// three YAKs, and the wait is over when the last of them is.
    /// </para>
    /// <para>
    /// A line that is missing contributes nothing rather than aborting the sum, because a
    /// conversation with one unreadable line should still take roughly as long as it takes.
    /// </para>
    /// </remarks>
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
    /// <remarks>Zero through nine, then A through Z carrying on from ten.</remarks>
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
