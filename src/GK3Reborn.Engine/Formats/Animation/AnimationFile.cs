using GK3Reborn.Formats.Ini;
using GK3Reborn.Foundation.Diagnostics;

namespace GK3Reborn.Formats.Animation;

/// <summary>A sound an animation plays as it runs.</summary>
/// <param name="Frame">Which frame it starts on.</param>
/// <param name="Name">The audio asset.</param>
/// <param name="Volume">How loud, from 0 to 100.</param>
public readonly record struct AnimationSound(int Frame, string Name, int Volume);

/// <summary>A line of dialogue an animation shows and speaks.</summary>
/// <param name="Frame">Which frame it appears on.</param>
/// <param name="EndFrame">Which frame it goes away on.</param>
/// <param name="Speaker">The noun of whoever is talking.</param>
/// <param name="Text">What they say.</param>
public readonly record struct AnimationCaption(int Frame, int EndFrame, string Speaker, string Text);

/// <summary>A vertex animation an animation starts on a frame.</summary>
/// <param name="Frame">Which frame it starts on.</param>
/// <param name="Name">The <c>.ACT</c> asset.</param>
public readonly record struct AnimationAction(int Frame, string Name);

/// <summary>
/// Reader for GK3's animations.
/// </summary>
/// <remarks>
/// <para>
/// 6,831 <c>.ANM</c> and 7,403 <c>.YAK</c> files, the largest asset family in the game and
/// the same format: an INI file whose <c>[HEADER]</c> is a frame count, and whose sections
/// list what happens on which frame — vertex animations to start, sounds to play, lines to
/// speak.
/// </para>
/// <para>
/// A YAK is a line of dialogue. <c>StartVoiceOver("1LLJ644QR1", 1)</c> names one: the last
/// character is a sequence number and the rest is the plate, so a voice-over of several
/// lines is several YAKs in a row. That makes this the thing that decides how long a
/// conversation takes — 20,709 of the corpus's 26,552 action-script statements are a
/// waited <c>StartVoiceOver</c>, and until something could say how long one lasts, every
/// one of them was over the instant it began.
/// </para>
/// <para>
/// Reading is not playing. The frames here are a schedule; running one needs the vertex
/// animation format and an audio device, neither of which exists. What it does give is
/// duration, which is what a script waiting on one needs to know.
/// </para>
/// </remarks>
public sealed class AnimationFile
{
    /// <summary>How many frames a second an animation runs at.</summary>
    /// <remarks>
    /// Fifteen, from G-Engine's <c>Animation::mFramesPerSecond</c>. Nothing in the files
    /// says so, which is worth knowing: a reader that assumed thirty would make every
    /// line of dialogue in the game half as long as it is.
    /// </remarks>
    public const int FramesPerSecond = 15;

    private AnimationFile(
        string name,
        int frames,
        IReadOnlyList<AnimationAction> actions,
        IReadOnlyList<AnimationSound> sounds,
        IReadOnlyList<AnimationCaption> captions)
    {
        Name = name;
        FrameCount = frames;
        Actions = actions;
        Sounds = sounds;
        Captions = captions;
    }

    /// <summary>Name this animation was read under.</summary>
    public string Name { get; }

    /// <summary>How many frames long it is.</summary>
    public int FrameCount { get; }

    /// <summary>How long it lasts, in seconds.</summary>
    public double Duration => (double)FrameCount / FramesPerSecond;

    /// <summary>The vertex animations it starts, in file order.</summary>
    public IReadOnlyList<AnimationAction> Actions { get; }

    /// <summary>The sounds it plays, in file order.</summary>
    public IReadOnlyList<AnimationSound> Sounds { get; }

    /// <summary>The lines it speaks, in file order.</summary>
    public IReadOnlyList<AnimationCaption> Captions { get; }

    /// <summary>Parses an animation.</summary>
    /// <param name="text">The file's text.</param>
    /// <param name="name">Name used in diagnostics.</param>
    /// <param name="diagnostics">Receives warnings about lines that could not be read.</param>
    /// <returns>The animation.</returns>
    public static AnimationFile Parse(string text, string name, DiagnosticBag diagnostics)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(diagnostics);

        IniDocument document = IniDocument.Parse(text, name);

        int frames = 0;
        List<AnimationAction> actions = [];
        List<AnimationSound> sounds = [];
        List<AnimationCaption> captions = [];

        foreach (IniSection section in document.Sections)
        {
            switch (section.Name.ToUpperInvariant())
            {
                case "HEADER":
                    frames = section.Lines.Count > 0
                        ? (int)(section.Lines[0].Head.AsNumber() ?? 0)
                        : 0;
                    break;

                case "ACTIONS":
                    Read(section, line => actions.Add(new AnimationAction(
                        (int)(line.Entries[0].AsNumber() ?? 0), line.Entries[1].Key)));
                    break;

                case "SOUNDS":
                    Read(section, line => sounds.Add(new AnimationSound(
                        (int)(line.Entries[0].AsNumber() ?? 0),
                        line.Entries[1].Key,
                        line.Entries.Count > 2 ? (int)(line.Entries[2].AsNumber() ?? 100) : 100)));
                    break;

                case "GK3":
                    Read(section, line => Caption(line, captions));
                    break;

                default:
                    break;
            }
        }

        if (frames <= 0)
        {
            diagnostics.Add(new Diagnostic(
                "GK3R1110", DiagnosticSeverity.Warning,
                "An animation gives no frame count, so it has no length.",
                name, null, "a [HEADER] with a number in it",
                frames.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "Anything waiting on it will not wait."));
        }

        return new AnimationFile(name, Math.Max(0, frames), actions, sounds, captions);
    }

    /// <summary>
    /// Walks the lines of a node section, skipping the count they open with.
    /// </summary>
    /// <remarks>
    /// Every section states how many entries follow and then lists them. The count is
    /// ignored in favour of the lines actually present, which is what the original does and
    /// what survives a file whose count is wrong.
    /// </remarks>
    private static void Read(IniSection section, Action<IniLine> line)
    {
        for (int i = 1; i < section.Lines.Count; i++)
        {
            if (section.Lines[i].Entries.Count >= 2)
            {
                line(section.Lines[i]);
            }
        }
    }

    /// <summary>
    /// Reads a spoken line.
    /// </summary>
    /// <remarks>
    /// <c>&lt;frame&gt;,SpeakerCaption,&lt;end frame&gt;,&lt;noun&gt;,&lt;caption&gt;</c>,
    /// and the caption itself contains commas as often as not — it is a sentence — so
    /// everything past the fourth field is put back together rather than taken as fields.
    /// </remarks>
    private static void Caption(IniLine line, List<AnimationCaption> captions)
    {
        if (line.Entries.Count < 4 ||
            !line.Entries[1].Key.Equals("SpeakerCaption", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        captions.Add(new AnimationCaption(
            (int)(line.Entries[0].AsNumber() ?? 0),
            (int)(line.Entries[2].AsNumber() ?? 0),
            line.Entries[3].Key,
            string.Join(
                ",",
                line.Entries.Skip(4).Select(e => e.Value.Length > 0 ? $"{e.Key}={e.Value}" : e.Key))));
    }
}
