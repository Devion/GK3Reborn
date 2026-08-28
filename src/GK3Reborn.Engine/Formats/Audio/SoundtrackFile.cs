using System.Globalization;
using System.Numerics;
using GK3Reborn.Formats.Ini;
using GK3Reborn.Foundation.Diagnostics;

namespace GK3Reborn.Formats.Audio;

/// <summary>What a soundtrack is for, which decides which volume slider it obeys.</summary>
public enum SoundtrackKind
{
    /// <summary>Room tone: rain, traffic, a fountain.</summary>
    Ambient,

    /// <summary>Written music.</summary>
    Music,

    /// <summary>One-off effects.</summary>
    Effect,
}

/// <summary>What a sound does when the soundtrack is stopped while it is playing.</summary>
public enum SoundtrackStop
{
    /// <summary>Let it finish.</summary>
    PlayToEnd,

    /// <summary>Fade it out over its own fade time.</summary>
    FadeOut,

    /// <summary>Cut it.</summary>
    Immediate,
}

/// <summary>What kind of step a node is.</summary>
public enum SoundtrackStep
{
    /// <summary>Do nothing for a while.</summary>
    Wait,

    /// <summary>Play one sound.</summary>
    Sound,

    /// <summary>Play one of several, chosen at random.</summary>
    PickRandom,
}

/// <summary>One sound a node can play.</summary>
public sealed record SoundtrackSound
{
    /// <summary>Name of the audio asset.</summary>
    public required string Name { get; init; }

    /// <summary>How loud, from 0 to 100.</summary>
    public int Volume { get; init; } = 100;

    /// <summary>Whether it repeats until something stops it.</summary>
    /// <remarks>
    /// A looping sound stops the rest of the list running, which is how a soundtrack that
    /// is meant to be continuous is written: everything before it is an introduction.
    /// </remarks>
    public bool Loop { get; init; }

    /// <summary>How long it fades in over, in milliseconds.</summary>
    public int FadeInMs { get; init; }

    /// <summary>What happens to it if the soundtrack stops.</summary>
    public SoundtrackStop Stop { get; init; }

    /// <summary>How long it fades out over, in milliseconds.</summary>
    /// <remarks>
    /// Parsed and read by nothing. Its one consumer was the room-to-room crossfade, removed
    /// on 2026-08-27 because three seconds of two beds on one bus is audibly two rooms. Kept
    /// because it is in the file, and because anything that fades a bed out again will want
    /// the artists' own number rather than a new one.
    /// </remarks>
    public int FadeOutMs { get; init; }

    /// <summary>Whether it is positioned in the room rather than played flat.</summary>
    public bool Is3D { get; init; }

    /// <summary>Distance within which it is at full volume.</summary>
    public float MinDistance { get; init; }

    /// <summary>Distance beyond which it cannot be heard.</summary>
    public float MaxDistance { get; init; }

    /// <summary>Where it comes from, when it is positioned.</summary>
    public Vector3 Position { get; init; }

    /// <summary>An object in the scene it follows instead of standing still.</summary>
    public string? Follow { get; init; }
}

/// <summary>One step of a soundtrack.</summary>
public sealed record SoundtrackNode
{
    /// <summary>Which kind of step.</summary>
    public required SoundtrackStep Step { get; init; }

    /// <summary>
    /// How many times round the list it still runs, or zero for always.
    /// </summary>
    /// <remarks>
    /// Counted down whether or not the node actually did anything, so a node that failed
    /// its chance still uses up one of its turns.
    /// </remarks>
    public int Repeat { get; init; }

    /// <summary>Percentage chance it happens at all, from 1 to 100.</summary>
    public int Chance { get; init; } = 100;

    /// <summary>Shortest wait, in milliseconds.</summary>
    public int MinWaitMs { get; init; }

    /// <summary>Longest wait, in milliseconds; zero means exactly the shortest.</summary>
    public int MaxWaitMs { get; init; }

    /// <summary>The sound, or the sounds to choose between.</summary>
    public IReadOnlyList<SoundtrackSound> Sounds { get; init; } = [];
}

/// <summary>
/// Reader for GK3's soundtracks.
/// </summary>
/// <remarks>
/// <para>
/// A <c>.STK</c> is not a piece of music. It is a little script — an INI file of numbered
/// steps the game walks in order and then repeats — that builds room tone out of clips:
/// wait a second, play the room's theme, wait five to ten seconds, play a mood, wait one
/// to four, play another. Repeating that with different waits each time is what keeps a
/// hotel room from sounding like a loop. 269 of them, named by 554 scene files.
/// </para>
/// <para>
/// <c>[PRS]</c> is the one section that is not what it looks like. Consecutive ones are
/// <em>one</em> step — pick one of these at random — rather than several steps in a row, so
/// they accumulate until some other section ends the run. Reading each as its own step
/// would play all three of the vampire's hisses at once instead of one of them.
/// </para>
/// <para>
/// Documented from G-Engine's <c>Soundtrack::Load</c>. Unknown keys and sections are
/// reported and ignored, which is also what the original does, and there are four in the
/// corpus worth knowing about — see <c>docs/formats/soundtracks.md</c>.
/// </para>
/// </remarks>
public sealed class SoundtrackFile
{
    private SoundtrackFile(string name, SoundtrackKind kind, IReadOnlyList<SoundtrackNode> nodes)
    {
        Name = name;
        Kind = kind;
        Nodes = nodes;
    }

    /// <summary>Name this soundtrack was read under.</summary>
    public string Name { get; }

    /// <summary>What it is for.</summary>
    public SoundtrackKind Kind { get; }

    /// <summary>The steps, in the order they run.</summary>
    public IReadOnlyList<SoundtrackNode> Nodes { get; }

    /// <summary>Every sound it can play, without duplicates, in a stable order.</summary>
    public IReadOnlyList<string> Sounds =>
        [.. Nodes.SelectMany(n => n.Sounds)
            .Select(s => s.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)];

    /// <summary>Parses a soundtrack.</summary>
    /// <param name="text">The file's text.</param>
    /// <param name="name">Name used in diagnostics.</param>
    /// <param name="diagnostics">Receives warnings about keys and sections not understood.</param>
    /// <returns>The soundtrack.</returns>
    public static SoundtrackFile Parse(string text, string name, DiagnosticBag diagnostics)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(diagnostics);

        // One entry a line: a soundtrack's values are plain numbers and names, and nothing
        // in the format packs several onto a line the way a scene file's cameras do.
        IniDocument document = IniDocument.Parse(text, name, multipleEntriesPerLine: false);

        SoundtrackKind kind = SoundtrackKind.Ambient;
        List<SoundtrackNode> nodes = [];
        List<SoundtrackSound> pending = [];

        foreach (IniSection section in document.Sections)
        {
            // A run of PRS sections is one step. Anything else ends the run.
            if (pending.Count > 0 &&
                !section.Name.Equals("PRS", StringComparison.OrdinalIgnoreCase))
            {
                nodes.Add(new SoundtrackNode
                {
                    Step = SoundtrackStep.PickRandom,
                    Sounds = [.. pending],
                });

                pending.Clear();
            }

            switch (section.Name.ToUpperInvariant())
            {
                case "SOUNDTRACK":
                    kind = KindOf(section, name, diagnostics, kind);
                    break;

                case "WAIT":
                    nodes.Add(Wait(section, name, diagnostics));
                    break;

                case "SOUND":
                    nodes.Add(Sound(section, name, diagnostics));
                    break;

                case "PRS":
                    // Repeat and looping mean nothing to one of several alternatives, and
                    // the original drops them here too.
                    pending.Add(Sound(section, name, diagnostics).Sounds[0] with { Loop = false });
                    break;

                case "":
                    break;

                default:
                    diagnostics.Add(new Diagnostic(
                        "GK3R1100", DiagnosticSeverity.Warning,
                        $"A soundtrack section named '{section.Name}' means nothing here.",
                        name, null, "SOUNDTRACK, WAIT, SOUND or PRS", section.Name,
                        "The section is skipped, as it is by the original."));
                    break;
            }
        }

        if (pending.Count > 0)
        {
            nodes.Add(new SoundtrackNode
            {
                Step = SoundtrackStep.PickRandom,
                Sounds = [.. pending],
            });
        }

        return new SoundtrackFile(name, kind, nodes);
    }

    private static SoundtrackKind KindOf(
        IniSection section, string file, DiagnosticBag diagnostics, SoundtrackKind fallback)
    {
        foreach (IniLine line in section.Lines)
        {
            if (!line.Head.Key.Equals("SoundType", StringComparison.OrdinalIgnoreCase))
            {
                Unknown(section.Name, line.Head.Key, file, diagnostics);
                continue;
            }

            fallback = line.Head.Value.ToUpperInvariant() switch
            {
                "MUSIC" => SoundtrackKind.Music,
                "SFX" => SoundtrackKind.Effect,
                _ => SoundtrackKind.Ambient,
            };
        }

        return fallback;
    }

    private static SoundtrackNode Wait(IniSection section, string file, DiagnosticBag diagnostics)
    {
        int minimum = 0;
        int maximum = 0;
        int repeat = 0;
        int chance = 100;

        foreach (IniLine line in section.Lines)
        {
            switch (line.Head.Key.ToUpperInvariant())
            {
                case "MINWAITMS":
                    minimum = Number(line, minimum);
                    break;
                case "MAXWAITMS":
                    maximum = Number(line, maximum);
                    break;
                case "REPEAT":
                    repeat = Number(line, repeat);
                    break;
                case "RANDOM":
                    chance = Number(line, chance);
                    break;
                default:
                    Unknown(section.Name, line.Head.Key, file, diagnostics);
                    break;
            }
        }

        return new SoundtrackNode
        {
            Step = SoundtrackStep.Wait,
            MinWaitMs = minimum,
            MaxWaitMs = maximum,
            Repeat = repeat,
            Chance = chance,
        };
    }

    private static SoundtrackNode Sound(IniSection section, string file, DiagnosticBag diagnostics)
    {
        string sound = string.Empty;
        int volume = 100;
        int repeat = 0;
        int chance = 100;
        bool loop = false;
        int fadeIn = 0;
        int fadeOut = 0;
        SoundtrackStop stop = SoundtrackStop.PlayToEnd;
        bool positioned = false;
        float minimum = 0f;
        float maximum = 0f;
        float x = 0f;
        float y = 0f;
        float z = 0f;
        string? follow = null;

        foreach (IniLine line in section.Lines)
        {
            switch (line.Head.Key.ToUpperInvariant())
            {
                case "NAME":
                    sound = line.Head.Value;
                    break;
                case "VOLUME":
                    volume = Number(line, volume);
                    break;
                case "REPEAT":
                    repeat = Number(line, repeat);
                    break;
                case "RANDOM":
                    chance = Number(line, chance);
                    break;
                case "LOOP":
                    loop = Number(line, 0) != 0;
                    break;
                case "FADEINMS":
                    fadeIn = Number(line, fadeIn);
                    break;
                case "FADEOUTMS":
                    fadeOut = Number(line, fadeOut);
                    break;
                case "STOPMETHOD":
                    stop = Number(line, 0) switch
                    {
                        1 => SoundtrackStop.FadeOut,
                        2 => SoundtrackStop.Immediate,
                        _ => SoundtrackStop.PlayToEnd,
                    };
                    break;
                case "3D":
                    positioned = Number(line, 0) != 0;
                    break;
                case "MINDIST":
                    minimum = Real(line, minimum);
                    break;
                case "MAXDIST":
                    maximum = Real(line, maximum);
                    break;
                case "X":
                    x = Real(line, x);
                    break;
                case "Y":
                    y = Real(line, y);
                    break;
                case "Z":
                    z = Real(line, z);
                    break;
                case "FOLLOW":
                    follow = line.Head.Value;
                    break;
                default:
                    Unknown(section.Name, line.Head.Key, file, diagnostics);
                    break;
            }
        }

        return new SoundtrackNode
        {
            Step = SoundtrackStep.Sound,
            Repeat = repeat,
            Chance = chance,
            Sounds =
            [
                new SoundtrackSound
                {
                    Name = sound,
                    Volume = volume,
                    Loop = loop,
                    FadeInMs = fadeIn,
                    Stop = stop,
                    FadeOutMs = fadeOut,
                    Is3D = positioned,
                    MinDistance = minimum,
                    MaxDistance = maximum,
                    Position = new Vector3(x, y, z),
                    Follow = follow is { Length: > 0 } ? follow : null,
                },
            ],
        };
    }

    /// <summary>A whole number, however the file spelt it.</summary>
    /// <remarks>
    /// Volumes are written <c>80.0</c> as often as <c>80</c>, and the original reads both
    /// through an integer conversion that stops at the point.
    /// </remarks>
    private static int Number(IniLine line, int fallback) =>
        line.Head.AsNumber() is { } value ? (int)value : fallback;

    private static float Real(IniLine line, float fallback) =>
        line.Head.AsNumber() ?? fallback;

    private static void Unknown(
        string section, string key, string file, DiagnosticBag diagnostics) =>
        diagnostics.Add(new Diagnostic(
            "GK3R1101", DiagnosticSeverity.Warning,
            string.Create(
                CultureInfo.InvariantCulture,
                $"A soundtrack's [{section}] has a key '{key}' that means nothing here."),
            file, null, "a key the section defines", key,
            "The key is ignored, as it is by the original; check it for a typo."));
}
