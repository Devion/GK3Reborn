using System.Numerics;
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
/// <param name="Placement">
/// Where in the room to play it, or null to play it wherever the model already is.
/// </param>
public readonly record struct AnimationAction(
    int Frame, string Name, AnimationPlacement? Placement = null);

/// <summary>Where an absolute animation puts the thing it moves.</summary>
/// <param name="Position">The spot, in world space.</param>
/// <param name="Heading">Which way it faces there, in radians about the vertical.</param>
/// <remarks>
/// <para>
/// An <c>[ACTIONS]</c> line may carry eight numbers after the clip's name: an offset and
/// heading from the actor to the model, then a second pair from the world to the model. The
/// spot is the second offset plus the first <em>rotated</em> by the second heading, and the
/// facing is the difference of the two headings. That is as strange as it sounds and it is
/// what the original computes.
/// </para>
/// <para>
/// 502 of the corpus's 6,040 action lines carry them — 8.3%. The other 92% place nothing
/// and mean "play this where the model is standing".
/// </para>
/// </remarks>
public readonly record struct AnimationPlacement(Vector3 Position, float Heading);

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
                        (int)(line.Entries[0].AsNumber() ?? 0),
                        line.Entries[1].Key,
                        Placement(line))));
                    break;

                case "SOUNDS":
                    Read(section, line => sounds.Add(new AnimationSound(
                        (int)(line.Entries[0].AsNumber() ?? 0),
                        line.Entries[1].Key,
                        line.Entries.Count > 2 ? (int)(line.Entries[2].AsNumber() ?? 100) : 100)));
                    break;

                case "GK3":
                    Spoken(section, captions);
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
    /// Reads an action line's absolute placement, if it has one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>&lt;frame&gt;,&lt;clip&gt;,x1,y1,z1,angle1,x2,y2,z2,angle2</c>. The first offset
    /// goes actor-to-model and is wanted the other way round, so it is <b>negated</b>; and
    /// <b>y and z are swapped</b> in both, because the assets came out of Maya. Both quirks
    /// are the original's and reproducing them is the difference between a character
    /// standing on the floor and one standing in a wall.
    /// </para>
    /// <para>
    /// A line whose numbers are all zero is not a placement. Nearly two thirds of the
    /// corpus's action lines are written that way and they mean the same as writing nothing.
    /// </para>
    /// </remarks>
    private static AnimationPlacement? Placement(IniLine line)
    {
        if (line.Entries.Count < 10)
        {
            return null;
        }

        float At(int index) => line.Entries[index].AsNumber() ?? 0;

        // Actor to model, wanted as model to actor, with y and z as Maya left them.
        var modelToActor = new Vector3(-At(2), -At(4), -At(3));
        float modelToActorHeading = At(5);

        var worldToModel = new Vector3(At(6), At(8), At(7));
        float worldToModelHeading = At(9);

        if (modelToActor == Vector3.Zero &&
            worldToModel == Vector3.Zero &&
            modelToActorHeading == 0 &&
            worldToModelHeading == 0)
        {
            return null;
        }

        Vector3 position = worldToModel + Vector3.Transform(
            modelToActor,
            Matrix4x4.CreateRotationY(worldToModelHeading * MathF.PI / 180f));

        return new AnimationPlacement(
            position, (worldToModelHeading - modelToActorHeading) * MathF.PI / 180f);
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
    /// Reads the spoken lines.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two forms, and the rarer one is the one that reads like documentation.
    /// <c>SpeakerCaption</c> carries everything on one line — end frame, noun, text — and
    /// occurs 211 times, in the long cutscenes. The ordinary form is a <c>SPEAKER</c> node
    /// naming who is talking followed by a <c>CAPTION</c> node with what they say, and that
    /// is 7,380 of the game's lines. A reader that handles only the first understands three
    /// percent of the dialogue.
    /// </para>
    /// <para>
    /// A caption is a sentence and contains commas, so everything past the fixed fields is
    /// put back together rather than taken as more fields.
    /// </para>
    /// <para>
    /// <c>LIPSYNCH</c> is skipped, and it is 98,153 of the corpus's nodes — a mouth shape
    /// per frame per line. Reading it needs a face with shapes to put them into.
    /// </para>
    /// </remarks>
    private static void Spoken(IniSection section, List<AnimationCaption> captions)
    {
        string speaker = string.Empty;

        for (int i = 1; i < section.Lines.Count; i++)
        {
            IniLine line = section.Lines[i];

            if (line.Entries.Count < 2)
            {
                continue;
            }

            int frame = (int)(line.Entries[0].AsNumber() ?? 0);

            switch (line.Entries[1].Key.ToUpperInvariant())
            {
                case "SPEAKER":
                    speaker = line.Entries.Count > 2 ? line.Entries[2].Key : string.Empty;
                    break;

                case "CAPTION":
                    if (line.Entries.Count > 2)
                    {
                        captions.Add(new AnimationCaption(frame, 0, speaker, Rest(line, 2)));
                    }

                    break;

                case "SPEAKERCAPTION":
                    if (line.Entries.Count > 4)
                    {
                        captions.Add(new AnimationCaption(
                            frame,
                            (int)(line.Entries[2].AsNumber() ?? 0),
                            line.Entries[3].Key,
                            Rest(line, 4)));
                    }

                    break;

                default:
                    break;
            }
        }
    }

    /// <summary>
    /// Puts the fields from an index onwards back together as one string.
    /// </summary>
    /// <remarks>
    /// The reader repeats a bare keyword as its own value, because the files that need it
    /// rely on the value never being empty. A caption is a sentence rather than a keyword,
    /// so putting it back means writing the key alone unless the two actually differ —
    /// otherwise every line of dialogue in the game is said twice.
    /// </remarks>
    private static string Rest(IniLine line, int from) =>
        string.Join(
            ",",
            line.Entries
                .Skip(from)
                .Select(e => string.Equals(e.Key, e.Value, StringComparison.Ordinal)
                    ? e.Key
                    : $"{e.Key}={e.Value}"));
}
