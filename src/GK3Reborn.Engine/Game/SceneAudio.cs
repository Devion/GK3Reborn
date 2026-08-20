using GK3Reborn.Audio;
using GK3Reborn.Content;
using GK3Reborn.Formats.Animation;
using GK3Reborn.Formats.Audio;

namespace GK3Reborn.Game;

/// <summary>
/// What the room sounds like.
/// </summary>
/// <remarks>
/// <para>
/// Three things put sound in a GK3 scene and they behave differently enough to be worth
/// separating. <b>Ambience</b> is one looping bed that lasts as long as the room does.
/// <b>Effects</b> are one-shots a script fires and forgets. <b>Dialogue</b> is a queue: a
/// voice-over of three lines is three files played one after another, and the second must
/// not start until the first has finished.
/// </para>
/// <para>
/// Which file a spoken line actually is comes out of the animation that carries it. A
/// script says <c>StartVoiceOver("0NQIB44QR1", 2)</c>; the plate resolves to
/// <c>E0NQIB44QR1.YAK</c>, whose <c>[SOUNDS]</c> names <c>A0NQIB44.QR1</c>, and that is the
/// audio. Nothing in the script mentions any of those names, which is why the library and
/// the animations both have to be here for anybody to say a word.
/// </para>
/// </remarks>
public sealed class SceneAudio
{
    private readonly SoundLibrary _sounds;
    private readonly AnimationLibrary _animations;
    private readonly IAudioBackend _backend;
    private readonly Queue<string> _speaking = new();

    private AudioVoice _ambience;
    private AudioVoice _line;

    /// <summary>Creates the scene's audio.</summary>
    /// <param name="sounds">Where the decoded sounds are.</param>
    /// <param name="animations">Where the animations that name them are.</param>
    /// <param name="backend">The device.</param>
    public SceneAudio(SoundLibrary sounds, AnimationLibrary animations, IAudioBackend backend)
    {
        ArgumentNullException.ThrowIfNull(sounds);
        ArgumentNullException.ThrowIfNull(animations);
        ArgumentNullException.ThrowIfNull(backend);

        _sounds = sounds;
        _animations = animations;
        _backend = backend;
    }

    /// <summary>What is being said now, if anything.</summary>
    public string? Saying { get; private set; }

    /// <summary>The caption for what is being said, if the line carries one.</summary>
    /// <remarks>
    /// Read from the animation rather than a subtitle file, because that is where GK3 keeps
    /// it: a <c>[GK3]</c> section of speaker and text against frame numbers. It is what a
    /// subtitle track will be drawn from.
    /// </remarks>
    public string? Caption { get; private set; }

    /// <summary>Who is saying it.</summary>
    public string? Speaker { get; private set; }

    /// <summary>How many lines are still queued behind this one.</summary>
    public int Queued => _speaking.Count;

    /// <summary>What the room is playing under everything, if anything.</summary>
    public string? Ambience { get; private set; }

    /// <summary>Starts a one-shot sound.</summary>
    /// <param name="name">Its name, extension and all.</param>
    /// <param name="bus">Which bus to mix it on.</param>
    /// <returns>True when something played.</returns>
    public bool Play(string name, AudioBus bus = AudioBus.Effects)
    {
        ArgumentNullException.ThrowIfNull(name);

        return _sounds.Read(name) is { } sound && _backend.Play(sound, bus).Exists;
    }

    /// <summary>Starts the room's looping bed, replacing whatever was there.</summary>
    /// <param name="name">Its name, or null to stop.</param>
    /// <returns>True when something is now playing.</returns>
    public bool Loop(string? name)
    {
        _pending = null;
        _waiting = null;

        if (_ambience.Exists)
        {
            _backend.Silence(_ambience);
            _ambience = AudioVoice.None;
        }

        Ambience = null;

        if (name is null || _sounds.Read(name) is not { } sound)
        {
            return false;
        }

        _ambience = _backend.Play(sound, AudioBus.Ambience, repeat: true);
        Ambience = _ambience.Exists ? name : null;

        return _ambience.Exists;
    }

    private Task<WavFile?>? _pending;
    private string? _waiting;

    // Where a run of lines has got to, so that ContinueDialogue knows what "the next two"
    // means without the script repeating the plate.
    private string? _stem;
    private int _next;

    /// <summary>Starts the room's ambience from the soundtracks the scene names.</summary>
    /// <param name="soundtracks">What the scene listed.</param>
    /// <returns>What is playing, or null when none of it could be.</returns>
    /// <remarks>
    /// <para>
    /// A soundtrack is a little program — pick one of these at random, wait between four
    /// and nine seconds, repeat twice — and running it properly is a scheduler of its own.
    /// This takes the first sound of the first ambient track and loops it, which gives a
    /// room its tone but not its variety.
    /// </para>
    /// <para>
    /// Deliberately the simple half. The difference is audible over minutes rather than
    /// seconds, and a room that hums is much closer to right than a room that is silent.
    /// </para>
    /// </remarks>
    public string? StartAmbience(IReadOnlyList<SoundtrackFile> soundtracks)
    {
        ArgumentNullException.ThrowIfNull(soundtracks);

        // In the order the file lists them, not the sorted set: a soundtrack opens with the
        // sound that establishes the room, and taking them alphabetically starts R25 on its
        // mood rather than its theme.
        foreach (SoundtrackFile soundtrack in soundtracks)
        {
            foreach (SoundtrackNode node in soundtrack.Nodes)
            {
                foreach (SoundtrackSound sound in node.Sounds)
                {
                    // Chosen by whether the archives hold it, which costs a directory
                    // lookup, rather than by whether it decodes, which costs the decode.
                    if (_sounds.Has(sound.Name))
                    {
                        Silence();

                        _waiting = sound.Name;
                        _pending = Task.Run(() => _sounds.Read(sound.Name));

                        return sound.Name;
                    }
                }
            }
        }

        return null;
    }

    /// <summary>Says a run of lines, one after another.</summary>
    /// <param name="plate">The licence plate the script gave.</param>
    /// <param name="lines">How many lines, itself included.</param>
    /// <returns>How many of them were found.</returns>
    /// <remarks>
    /// Anything already being said is abandoned. That is what the original does — a script
    /// that starts a conversation while one is running means to replace it — and it is also
    /// the only behaviour that cannot deadlock.
    /// </remarks>
    public int Speak(string plate, int lines)
    {
        ArgumentNullException.ThrowIfNull(plate);

        Hush();

        if (plate.Length == 0)
        {
            return 0;
        }

        _stem = plate[..^1];
        _next = Sequence(plate[^1]);

        return Continue(lines);
    }

    /// <summary>Says the next lines of whatever was last started.</summary>
    /// <param name="lines">How many more to say.</param>
    /// <returns>How many of them were found.</returns>
    /// <remarks>
    /// <para>
    /// A conversation is written as one plate and then a series of continuations: the
    /// script says <c>StartDialogue("1E4CU4OCZ1", 1)</c> and later <c>ContinueDialogue(2)</c>,
    /// which means the next two in the same sequence. The plate is not repeated, so
    /// somebody has to remember where the run had got to.
    /// </para>
    /// <para>
    /// Continuing when nothing was started says nothing, which is what a script calling it
    /// out of order deserves — and is better than guessing at a plate.
    /// </para>
    /// </remarks>
    public int Continue(int lines)
    {
        if (_stem is not { Length: > 0 } stem)
        {
            return 0;
        }

        int found = 0;

        for (int i = 0; i < Math.Max(1, lines); i++)
        {
            string yak = stem + Digit(_next);

            _next++;

            if (_animations.Read(yak) is not null)
            {
                _speaking.Enqueue(yak);
                found++;
            }
        }

        Next();
        return found;
    }

    /// <summary>Stops whatever is being said and forgets the rest of it.</summary>
    public void Hush()
    {
        if (_line.Exists)
        {
            _backend.Silence(_line);
            _line = AudioVoice.None;
        }

        _speaking.Clear();
        Saying = null;
        Caption = null;
        Speaker = null;
    }

    /// <summary>Stops everything, for leaving a room.</summary>
    public void Silence()
    {
        Hush();
        Loop(null);
        _backend.StopBus(AudioBus.Effects);
    }

    /// <summary>Starts the next line when the last one has finished.</summary>
    /// <remarks>
    /// Called once a frame. The device is the clock: a line is over when its source stops,
    /// not when a timer says it should have, so the two never drift apart.
    /// </remarks>
    public void Update()
    {
        _backend.Update();

        // A soundtrack is a five-minute MP3 and decoding one is a quarter of a second, which
        // used to sit between a room being ready and the player seeing it. It is decoded
        // beside the first frames instead and started on whichever one it is ready for. The
        // device work stays here, on the thread that owns the device.
        if (_pending is { IsCompleted: true } finished)
        {
            string? name = _waiting;

            _pending = null;
            _waiting = null;

            if (finished.IsCompletedSuccessfully && finished.Result is not null)
            {
                Loop(name);
            }
        }

        if (_line.Exists && !_backend.IsPlaying(_line))
        {
            _line = AudioVoice.None;
            Saying = null;
            Caption = null;
            Speaker = null;

            Next();
        }
    }

    /// <summary>Starts the line at the head of the queue.</summary>
    private void Next()
    {
        while (_speaking.Count > 0)
        {
            string yak = _speaking.Dequeue();
            AnimationFile? animation = _animations.Read(yak);

            if (animation is null)
            {
                continue;
            }

            Saying = yak;

            if (animation.Captions.Count > 0)
            {
                Caption = animation.Captions[0].Text;
                Speaker = animation.Captions[0].Speaker;
            }

            // A line's animation names its own audio; without that there is no way from a
            // licence plate to a file.
            foreach (AnimationSound cue in animation.Sounds)
            {
                if (_sounds.Read(cue.Name) is not { } sound)
                {
                    continue;
                }

                _line = _backend.Play(sound, AudioBus.DialogueCentered);

                if (_line.Exists)
                {
                    return;
                }
            }

            // The animation is there but its audio is not, so the line is skipped rather
            // than holding up the ones behind it.
            Saying = null;
            Caption = null;
            Speaker = null;
        }
    }

    private static int Sequence(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'A' and <= 'Z' => c - 'A' + 10,
        >= 'a' and <= 'z' => c - 'a' + 10,
        _ => 0,
    };

    private static char Digit(int value) => value switch
    {
        >= 0 and <= 9 => (char)('0' + value),
        >= 10 and <= 35 => (char)('A' + value - 10),
        _ => '0',
    };
}
