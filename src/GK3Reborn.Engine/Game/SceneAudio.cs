using System.Numerics;
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

    /// <summary>
    /// How long a bed takes to fade when its own soundtrack does not say.
    /// </summary>
    /// <remarks>
    /// Most of them do say. A <c>.STK</c> gives each sound a <c>FadeOutMS</c> — R25's theme
    /// asks for three seconds — and that is the artists' own answer to how long this room
    /// should take to stop being the room you are in. This is only for the ones that leave
    /// it out: long enough to hear as a change of place rather than a cut, and about as
    /// long as walking through a door takes.
    /// </remarks>
    private const double FadeSeconds = 1.5;

    /// <summary>The soundtracks the room is running, one program each.</summary>
    private readonly List<SoundtrackProgram> _programs = [];

    /// <summary>Sounds that move with something, and what they move with.</summary>
    private readonly List<(AudioVoice Voice, string Model)> _following = [];

    /// <summary>Sounds still fading in, with how far through the fade they are.</summary>
    private readonly List<(AudioVoice Voice, double Length, double Gain, double At)> _rising = [];

    /// <summary>
    /// Where the waits and the choices are drawn from.
    /// </summary>
    /// <remarks>
    /// Its own generator rather than the game's, so that how often a room creaks does not
    /// depend on how many times anybody has clicked on anything — and seeded, so that two
    /// runs of the same scene make the same noises at the same moments. ADR 0004.
    /// </remarks>
    private readonly Foundation.DeterministicRandom _chance = new(0x51A7C0DE51A7C0DE);

    private AudioVoice _ambience;
    private AudioVoice _line;

    /// <summary>The bed being faded out, if a room change is under way.</summary>
    private AudioVoice _leaving;
    private double _faded = FadeSeconds;
    private double _fadeLength = FadeSeconds;

    /// <summary>What the playing bed's soundtrack asks for when it stops, in milliseconds.</summary>
    private int _bedFadeMs;

    /// <summary>And what the one being decoded will ask for.</summary>
    private int _nextFadeMs;

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

    /// <summary>
    /// Told whenever a line starts or stops, so that faces can follow it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A line of dialogue is one animation: its <c>[SOUNDS]</c> is the recording and its
    /// <c>[GK3]</c> is the mouth shapes, against the same frame numbers. So whatever moves
    /// mouths needs the animation and not just the name of it, and it needs it at the
    /// moment the sound actually starts rather than when the script asked for it — a
    /// queued line may be several seconds behind the call that queued it.
    /// </para>
    /// <para>
    /// Null when nothing is being said, which is how a mouth learns to close. Optional:
    /// the room may be running without faces, or without anything to draw them on.
    /// </para>
    /// </remarks>
    public Action<AnimationFile?>? Speaking { get; set; }

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

    /// <summary>Starts a one-shot sound somewhere in the room, at its own level.</summary>
    /// <param name="name">Its name, extension and all.</param>
    /// <param name="at">Where it comes from, or null for the listener's own head.</param>
    /// <param name="gain">How loud, from zero to one, on top of its bus.</param>
    /// <param name="bus">Which bus to mix it on.</param>
    /// <returns>True when something played.</returns>
    /// <remarks>
    /// What an animation's <c>[SOUNDS]</c> cue needs: the file names a model and a volume,
    /// and both are lost by playing it flat at full level. Gabriel's yawn comes from where
    /// Gabriel is.
    /// </remarks>
    public bool PlayAt(
        string name, Vector3? at, float gain = 1f, AudioBus bus = AudioBus.Effects)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (_sounds.Read(name) is not { } sound)
        {
            return false;
        }

        AudioVoice voice = _backend.Play(
            sound, bus, repeat: false, at is { } spot ? AudioPlacement.At(spot) : null);

        if (!voice.Exists)
        {
            return false;
        }

        if (gain < 1f)
        {
            _backend.SetVoiceGain(voice, Math.Clamp(gain, 0f, 1f));
        }

        return true;
    }

    /// <summary>Starts the room's looping bed, replacing whatever was there.</summary>
    /// <param name="name">Its name, or null to stop.</param>
    /// <returns>True when something is now playing.</returns>
    public bool Loop(string? name) => Loop(name, null);

    /// <summary>Starts a looping ambience, somewhere in the room.</summary>
    /// <param name="name">The sound, or null to stop whatever is playing.</param>
    /// <param name="at">Where it is, or null to play it at the listener's head.</param>
    /// <returns>True when something is playing.</returns>
    /// <remarks>
    /// A soundtrack says where its sound is and how far it carries — RC1's fountain is at
    /// (3113, 114, −2337) and reaches 1,200 units. Played at the head instead, it is as loud
    /// across the square as it is standing in it.
    /// </remarks>
    public bool Loop(string? name, AudioPlacement? at)
    {
        _pending = null;
        _waiting = null;
        _where = null;

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

        _ambience = _backend.Play(sound, AudioBus.Ambience, repeat: true, at);
        Ambience = _ambience.Exists ? name : null;
        AmbienceAt = _ambience.Exists ? at : null;

        return _ambience.Exists;
    }

    private Task<WavFile?>? _pending;
    private string? _waiting;
    private AudioPlacement? _where;

    /// <summary>Where the ambience is in the room, or null when it plays at the head.</summary>
    public AudioPlacement? AmbienceAt { get; private set; }

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

        // Whatever was playing is already on its way out — see Leave — so nothing here
        // stops anything. A room that names no soundtrack leaves that fade to finish on
        // its own, which is a room going quiet rather than the sound being cut off.
        _programs.Clear();

        // All of them, not the first: RC1 at ten in the morning names a fountain, a room
        // tone and birdsong, and they are meant to be heard together.
        foreach (SoundtrackFile soundtrack in soundtracks)
        {
            _programs.Add(new SoundtrackProgram(soundtrack, _chance));
        }

        // One step of each, now, so that a room is not silent for the length of its first
        // wait — and so that the caller has something to report. Time zero rather than a
        // frame's worth: a wait of a second is a second after the room appears.
        foreach (SoundtrackProgram program in _programs)
        {
            program.Advance(0, sound => Sound(program, sound));
        }

        return Ambience ?? _waiting;
    }

    /// <summary>Starts one sound of a soundtrack, and says how long it lasts.</summary>
    /// <param name="program">The soundtrack it belongs to.</param>
    /// <param name="sound">The sound, as the file describes it.</param>
    /// <returns>Its length in seconds, or zero when it could not be played.</returns>
    /// <remarks>
    /// <para>
    /// A sound that loops is the room's bed: it is what the room sounds like for as long
    /// as the player is in it, and it is what the crossfade into the next room fades out.
    /// It goes through the same decode-off-the-thread path as before, because a bed is
    /// often a five-minute MP3 and decoding one where the room appears is a quarter of a
    /// second of nothing happening.
    /// </para>
    /// <para>
    /// Everything else is a moment — a creak, a bell, a car going past — and is played
    /// outright. Those are short, already decoded by the time a room has been in for a
    /// minute, and waiting a frame for one would put it after the step that follows it.
    /// </para>
    /// </remarks>
    private double Sound(SoundtrackProgram program, SoundtrackSound sound)
    {
        AudioPlacement? at = PlacementOf(sound);

        if (sound.Loop)
        {
            if (!_sounds.Has(sound.Name))
            {
                return 0;
            }

            _waiting = sound.Name;
            _nextFadeMs = sound.FadeOutMs;
            _where = at;
            AmbienceAt = at;
            _pending = Task.Run(() => _sounds.Read(sound.Name));

            return 0;
        }

        if (_sounds.Read(sound.Name) is not { } wav)
        {
            return 0;
        }

        AudioVoice voice = _backend.Play(wav, Bus(program.Kind), repeat: false, at);

        if (!voice.Exists)
        {
            return 0;
        }

        float gain = Math.Clamp(sound.Volume / 100f, 0f, 1f);

        // A sound that fades in starts at nothing and is brought up by Update. 52 of the
        // corpus's soundtracks ask for one, and they are the ones where the sound is meant
        // to arrive rather than to start — weather, a crowd, an engine coming closer.
        if (sound.FadeInMs > 0)
        {
            _backend.SetVoiceGain(voice, 0f);
            _rising.Add((voice, sound.FadeInMs / 1000.0, gain, 0));
        }
        else
        {
            _backend.SetVoiceGain(voice, gain);
        }

        // Kept only while it needs following. Everything else the backend reclaims on its
        // own, and holding a handle to a sound that has finished is how a list of voices
        // grows for as long as a room is stood in.
        if (sound.Follow is { Length: > 0 })
        {
            _following.Add((voice, sound.Follow));
        }

        return wav.Duration;
    }

    /// <summary>Which bus a soundtrack's sounds are mixed on.</summary>
    /// <remarks>
    /// The file says: <c>SoundType=Music</c>, <c>Ambient</c> or <c>SFX</c>, which is which
    /// of the player's own volume sliders it obeys.
    /// </remarks>
    private static AudioBus Bus(SoundtrackKind kind) => kind switch
    {
        SoundtrackKind.Music => AudioBus.Music,
        SoundtrackKind.Effect => AudioBus.Effects,
        _ => AudioBus.Ambience,
    };

    /// <summary>Where a soundtrack's sound is, if it says.</summary>
    /// <param name="sound">The sound, as its soundtrack describes it.</param>
    /// <returns>Its placement, or null when it belongs at the listener.</returns>
    /// <remarks>
    /// <para>
    /// A <c>.STK</c> either gives a sound a place in the room or does not. Those that do
    /// carry <c>3D=1</c> with a position and a pair of distances — CSE's fountain is 85 to
    /// 1,000 units, a passing car is 250 to 2,500 — and those that do not are room tone,
    /// which belongs at the head because it comes from everywhere.
    /// </para>
    /// <para>
    /// A sound that follows something is not placed here. <c>Follow=blk_sedan</c> means the
    /// emitter moves with a model, and where that model is at any moment is the room's
    /// business rather than this file's; until something asks the room, following sounds
    /// play at their authored spot, which is where the model starts.
    /// </para>
    /// </remarks>
    public static AudioPlacement? PlacementOf(SoundtrackSound sound) =>
        sound.Is3D
            ? new AudioPlacement(
                sound.Position,
                sound.MinDistance > 0 ? sound.MinDistance : AudioPlacement.DefaultMinimum,
                sound.MaxDistance > 0 ? sound.MaxDistance : AudioPlacement.DefaultMaximum)
            : null;

    /// <summary>Puts the listener where the player is looking from.</summary>
    /// <param name="position">The camera's position.</param>
    /// <param name="forward">Which way it looks.</param>
    /// <param name="up">Which way is up for it.</param>
    public void Listen(Vector3 position, Vector3 forward, Vector3 up) =>
        _backend.Listen(position, forward, up);

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

    /// <summary>Whether anybody is speaking at this moment.</summary>
    public bool Talking => Saying is { Length: > 0 };

    /// <summary>
    /// How long the next lines of the run in progress take.
    /// </summary>
    /// <param name="lines">How many more.</param>
    /// <returns>Seconds, or nought when nothing has been started.</returns>
    /// <remarks>
    /// What a waited <c>ContinueDialogue</c> is worth. The run's licence plate was given
    /// once, when it started, and this is the only thing that still knows it — so the script
    /// host asks rather than working it out, and a continuation of nothing answers nought.
    /// </remarks>
    public double SecondsOfNext(int lines)
    {
        if (_stem is not { Length: > 0 } stem)
        {
            return 0;
        }

        double total = 0;

        for (int i = 0; i < Math.Max(1, lines); i++)
        {
            total += _animations.SecondsOf(stem + Digit(_next + i));
        }

        return total;
    }

    /// <summary>
    /// Cuts the line being spoken short and starts the next one.
    /// </summary>
    /// <returns>True when there was a line to cut short.</returns>
    /// <remarks>
    /// <para>
    /// What a click during dialogue means. A player who has read the caption should not have
    /// to sit through the rest of the recording, and every adventure game of this kind lets
    /// them tap through — the original does not, which is a limitation of 1999 rather than a
    /// design anybody would choose.
    /// </para>
    /// <para>
    /// The rest of the run is kept, because a conversation is a queue and skipping a line is
    /// not abandoning the exchange. Skipping the last line stops cleanly and lets whatever
    /// was waiting on the dialogue carry on, which is the same path the line finishing on
    /// its own takes.
    /// </para>
    /// </remarks>
    public bool Skip()
    {
        if (Saying is not { Length: > 0 })
        {
            return false;
        }

        if (_line.Exists)
        {
            _backend.Silence(_line);
            _line = AudioVoice.None;
        }

        Saying = null;
        Caption = null;
        Speaker = null;
        Speaking?.Invoke(null);

        Next();
        return true;
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
        Speaking?.Invoke(null);
    }

    /// <summary>Stops the one-shot sounds, leaving the ambience and the dialogue.</summary>
    /// <remarks>
    /// What a script means by <c>StopAllSounds</c>. The room goes on sounding like the room
    /// and whoever is speaking goes on speaking; what stops is the door that was closing
    /// and the glass that was breaking.
    /// </remarks>
    public void Quiet() => _backend.StopBus(AudioBus.Effects);

    /// <summary>Stops everything, for leaving a room.</summary>
    public void Silence()
    {
        Hush();
        Loop(null);
        Quiet();
        Drop();
    }

    /// <summary>
    /// Ends the room without ending what it sounds like.
    /// </summary>
    /// <remarks>
    /// Everything the room being left was <em>saying</em> belongs to that room and stops.
    /// What it sounded like does not: the next room's bed fades in over it, so leaving one
    /// place for another is heard as a change of place rather than as two cuts. If the next
    /// room names no ambience at all, this one fades out on its own — which is the same
    /// crossfade with nothing on the other side of it.
    /// </remarks>
    public void Leave()
    {
        Hush();
        Quiet();

        Drop();

        // A soundtrack says how its sound stops: play to the end, fade, or cut. Leaving
        // the room is the forced kind, so even "play to the end" stops — the reference
        // does the same, and a creak carried into the next room is a creak in the wrong
        // room. The bed is not stopped here: it is handed to the crossfade below.
        foreach (SoundtrackProgram program in _programs)
        {
            if (program.Sounding is { Loop: false } sounding)
            {
                Stop(sounding);
            }
        }

        _programs.Clear();
        _following.Clear();
        _rising.Clear();

        _pending = null;
        _waiting = null;

        _leaving = _ambience;
        _ambience = AudioVoice.None;
        Ambience = null;
        AmbienceAt = null;

        // The outgoing sound's own fade, because that is whose stopping this is.
        _fadeLength = _bedFadeMs > 0 ? _bedFadeMs / 1000.0 : FadeSeconds;
        _faded = _leaving.Exists ? 0 : _fadeLength;
        _bedFadeMs = 0;
    }

    /// <summary>Starts a bed under whatever is fading out.</summary>
    private void Fade(string? name, AudioPlacement? at)
    {
        if (name is null || _sounds.Read(name) is not { } sound)
        {
            return;
        }

        // Silent to begin with, and brought up by Update. Starting it at full and turning
        // the other one down would be a cut with a tail rather than a crossfade.
        _ambience = _backend.Play(sound, AudioBus.Ambience, repeat: true, at);

        if (!_ambience.Exists)
        {
            return;
        }

        _backend.SetVoiceGain(_ambience, _leaving.Exists ? 0f : 1f);

        Ambience = name;
        AmbienceAt = at;
        _bedFadeMs = _nextFadeMs;
    }

    /// <summary>Moves a crossfade along, and ends it when it is over.</summary>
    private void Crossfade(double seconds)
    {
        if (_faded >= _fadeLength)
        {
            return;
        }

        _faded += Math.Max(0, seconds);
        float part = (float)Math.Clamp(_faded / Math.Max(0.001, _fadeLength), 0, 1);

        if (_leaving.Exists)
        {
            _backend.SetVoiceGain(_leaving, 1f - part);
        }

        if (_ambience.Exists)
        {
            _backend.SetVoiceGain(_ambience, part);
        }

        if (part >= 1f)
        {
            Drop();
        }
    }

    /// <summary>Stops whatever was fading out.</summary>
    /// <summary>Stops a soundtrack's sound the way the soundtrack says to.</summary>
    /// <param name="sound">The sound being stopped.</param>
    /// <remarks>
    /// Only the voices this still holds — the ones that follow something. Everything else
    /// is a moment already over or nearly so, and the backend reclaims it.
    /// </remarks>
    private void Stop(SoundtrackSound sound)
    {
        for (int i = _following.Count - 1; i >= 0; i--)
        {
            if (!string.Equals(_following[i].Model, sound.Follow, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            _backend.Silence(_following[i].Voice);
            _following.RemoveAt(i);
        }
    }

    private void Drop()
    {
        if (_leaving.Exists)
        {
            _backend.Silence(_leaving);
            _leaving = AudioVoice.None;
        }
    }

    /// <summary>Starts a soundtrack a script named, on top of the room's own.</summary>
    /// <param name="track">The file.</param>
    /// <returns>True if it was not already playing.</returns>
    /// <remarks>
    /// A script's soundtrack is another program running beside the room's rather than
    /// instead of it: <c>PlaySoundTrack</c> in the middle of a scene is a car arriving or
    /// a storm getting up, and the room is still the room underneath it. Playing one
    /// twice does nothing, which is what the original does — the same list started twice
    /// would be the same sound at two different points in its own walk.
    /// </remarks>
    public bool Play(SoundtrackFile track)
    {
        ArgumentNullException.ThrowIfNull(track);

        foreach (SoundtrackProgram running in _programs)
        {
            if (running.Track.Name.Equals(track.Name, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        var program = new SoundtrackProgram(track, _chance);
        _programs.Add(program);

        // One step now, so a soundtrack started by a script is heard on the frame it was
        // asked for rather than on the next.
        program.Advance(0, sound => Sound(program, sound));

        return true;
    }

    /// <summary>Stops one soundtrack, or every soundtrack.</summary>
    /// <param name="name">Which one, or null for all of them.</param>
    /// <returns>How many were stopped.</returns>
    /// <remarks>
    /// The room's bed is left alone by name and stopped by "all", which is the difference
    /// between a script ending the storm it started and a script asking for silence.
    /// </remarks>
    public int StopSoundtrack(string? name = null)
    {
        int stopped = 0;

        for (int i = _programs.Count - 1; i >= 0; i--)
        {
            if (name is { Length: > 0 } &&
                !_programs[i].Track.Name.Equals(name, StringComparison.OrdinalIgnoreCase) &&
                !Path.GetFileNameWithoutExtension(_programs[i].Track.Name)
                    .Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (_programs[i].Sounding is { } sounding)
            {
                Stop(sounding);

                // A looping sound is the room's bed, and stopping the soundtrack that owns
                // it is what silences the room.
                if (sounding.Loop)
                {
                    Loop(null);
                }
            }

            _programs.RemoveAt(i);
            stopped++;
        }

        return stopped;
    }

    /// <summary>The soundtracks the room is running, by name.</summary>
    /// <remarks>
    /// A room may be running several — RC1 at ten in the morning names a fountain, a room
    /// tone and birdsong — and most of them are silent at any given moment, because a
    /// soundtrack is mostly waiting. So this says what is <em>running</em>, where
    /// <see cref="Ambience"/> says what is sounding.
    /// </remarks>
    public IReadOnlyList<string> Running =>
        [.. _programs.Select(p => p.Track.Name)];

    /// <summary>Where a line is spoken from, or null when it belongs at the head.</summary>
    /// <param name="line">The line's animation, which names its speaker in its caption.</param>
    /// <returns>The placement, or null to centre it.</returns>
    /// <remarks>
    /// This line's own speaker rather than <see cref="Speaker"/>, which is the last one
    /// known: an animation with no caption names nobody, and taking the previous line's
    /// speaker would put an unattributed line wherever the last person to talk is standing.
    /// </remarks>
    private AudioPlacement? Placed(AnimationFile line)
    {
        if (line.Captions.Count == 0 ||
            line.Captions[0].Speaker is not { Length: > 0 } speaker ||
            Routing.Resolve(speaker) == DialogueRouting.Centered)
        {
            return null;
        }

        return Where?.Invoke(speaker) is { } standing
            ? new AudioPlacement(standing, DialogueNear, DialogueFar)
            : null;
    }

    /// <summary>How near a speaker has to be before their voice stops getting louder.</summary>
    /// <remarks>
    /// A person talking is not a fountain: the useful range is a conversation's worth of
    /// room rather than a square's, so a line placed in the world is at full level for the
    /// few feet a conversation is held across and falls away beyond that. Wider than the
    /// game's own default for a sound, because a line the player cannot make out is worse
    /// than a line that is not quite placed right.
    /// </remarks>
    private const float DialogueNear = 300f;

    /// <summary>And how far away it is as quiet as it gets.</summary>
    private const float DialogueFar = 2000f;

    /// <summary>Which speakers are centred and which are placed in the room.</summary>
    /// <remarks>
    /// Gabriel by default, and everybody when the player turns on centred dialogue — which
    /// is an accessibility option, not a mixing preference (Plan/03 section 8).
    /// </remarks>
    public DialogueRoutingOptions Routing { get; set; } = new();

    /// <summary>Reads a soundtrack by name, for the calls that name one.</summary>
    /// <remarks>
    /// A hook rather than an archive reference: a <c>.STK</c> is text in a barn, and the
    /// audio layer knowing how to open one would put the archives behind the mixer.
    /// </remarks>
    public Func<string, SoundtrackFile?>? Soundtracks { get; set; }

    /// <summary>Where a model in the room is, for a sound that moves with it.</summary>
    /// <remarks>
    /// <c>Follow=blk_sedan</c> on a soundtrack's sound means the emitter travels with that
    /// model. Where the model is at any moment is the room's business rather than the
    /// audio's, so this is a hook: without one, a following sound stays where the file
    /// authored it, which is where the model starts.
    /// </remarks>
    public Func<string, Vector3?>? Where { get; set; }

    /// <summary>Brings the sounds that are fading in up to their own level.</summary>
    /// <remarks>
    /// Straight-line in gain, like the room-to-room crossfade beside it. A sound whose
    /// fade has finished is dropped from the list rather than kept and set to the same
    /// level every frame for as long as it plays.
    /// </remarks>
    private void Rising(double seconds)
    {
        for (int i = _rising.Count - 1; i >= 0; i--)
        {
            (AudioVoice voice, double length, double gain, double at) = _rising[i];

            at += seconds;

            if (at >= length || length <= 0)
            {
                _backend.SetVoiceGain(voice, (float)gain);
                _rising.RemoveAt(i);
                continue;
            }

            _backend.SetVoiceGain(voice, (float)(gain * (at / length)));
            _rising[i] = (voice, length, gain, at);
        }
    }

    /// <summary>Moves the sounds that travel with something.</summary>
    /// <remarks>
    /// Voices that have finished are dropped here rather than kept, because a room stood
    /// in for ten minutes starts a great many sounds and only a handful of them follow
    /// anything.
    /// </remarks>
    private void Following()
    {
        for (int i = _following.Count - 1; i >= 0; i--)
        {
            (AudioVoice voice, string model) = _following[i];

            if (!_backend.IsPlaying(voice))
            {
                _following.RemoveAt(i);
                continue;
            }

            if (Where?.Invoke(model) is { } position)
            {
                _backend.Move(voice, position);
            }
        }
    }

    /// <summary>Starts the next line when the last one has finished.</summary>
    /// <remarks>
    /// Called once a frame. The device is the clock: a line is over when its source stops,
    /// not when a timer says it should have, so the two never drift apart.
    /// </remarks>
    /// <param name="seconds">How long since the last frame, for the crossfade.</param>
    public void Update(double seconds = 0)
    {
        _backend.Update();

        Crossfade(seconds);

        // The room's own soundtracks, each a list being walked. Before the decode below
        // rather than after it, so a bed a program asks for this frame is picked up this
        // frame rather than the next.
        foreach (SoundtrackProgram program in _programs)
        {
            program.Advance(seconds, sound => Sound(program, sound));
        }

        Following();
        Rising(seconds);

        // A soundtrack is a five-minute MP3 and decoding one is a quarter of a second, which
        // used to sit between a room being ready and the player seeing it. It is decoded
        // beside the first frames instead and started on whichever one it is ready for. The
        // device work stays here, on the thread that owns the device.
        if (_pending is { IsCompleted: true } finished)
        {
            string? name = _waiting;

            _pending = null;
            _waiting = null;

            AudioPlacement? at = _where;

            if (finished.IsCompletedSuccessfully && finished.Result is not null)
            {
                // Under whatever is on its way out, rather than in place of it. A room's
                // bed is a five-minute MP3 and takes a moment to decode, so by the time
                // this lands the fade is already part of the way through — which is right:
                // the old room has been fading since the player left it.
                Fade(name, at);
            }
        }

        if (_line.Exists && !_backend.IsPlaying(_line))
        {
            _line = AudioVoice.None;
            Saying = null;
            Caption = null;
            Speaker = null;

            // The mouth closes the moment the sound stops, whether or not there is another
            // line behind it. Next() will say so again if there is.
            Speaking?.Invoke(null);

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

                // Where the line is heard from. Gabriel is always centred — the player is
                // him, and a voice that swings across the room every time the camera cuts
                // is the one voice that must not — and everybody else is placed where they
                // are standing, unless the player has asked for all dialogue centred. The
                // policy has existed since the audio layer was written with nothing
                // reading it, so every line in the game came out of the middle.
                _line = Placed(animation) is { } placed
                    ? _backend.Play(sound, AudioBus.DialogueInWorld, repeat: false, placed)
                    : AudioVoice.None;

                if (!_line.Exists)
                {
                    _line = _backend.Play(sound, AudioBus.DialogueCentered);
                }

                if (_line.Exists)
                {
                    Speaking?.Invoke(animation);
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
