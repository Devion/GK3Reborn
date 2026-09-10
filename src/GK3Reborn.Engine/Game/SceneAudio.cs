using System.Numerics;
using GK3Reborn.Audio;
using GK3Reborn.Content;
using GK3Reborn.Formats.Animation;
using GK3Reborn.Formats.Audio;

namespace GK3Reborn.Game;

/// <summary>
/// What the room sounds like.
/// </summary>
public sealed class SceneAudio
{
    private readonly SoundLibrary _sounds;
    private readonly AnimationLibrary _animations;
    private readonly IAudioBackend _backend;
    private readonly Queue<string> _speaking = new();

    /// <summary>A soundtrack the room is running, and the sound it has going.</summary>
    /// <param name="program">The list being walked.</param>
    private sealed class Playing(SoundtrackProgram program)
    {
        /// <summary>The list being walked.</summary>
        public SoundtrackProgram Program { get; } = program;

        /// <summary>The one-shots it has going, oldest first.</summary>
        public List<AudioVoice> Voices { get; } = [];

        /// <summary>Its looping bed, when it has reached one.</summary>
        public AudioVoice Bed;

        /// <summary>What the bed is, for saying what the room sounds like.</summary>
        public string? BedName;

        /// <summary>And where it is.</summary>
        public AudioPlacement? BedAt;

        /// <summary>A bed being decoded off the thread.</summary>
        public Task<WavFile?>? Pending;

        /// <summary>What that decode is, and where it goes when it arrives.</summary>
        public string? Waiting;

        /// <summary>Where the bed being decoded belongs.</summary>
        public AudioPlacement? Where;

        /// <summary>
        /// The one-shot the list opened with, for saying what the room sounds like when
        /// it has no bed: a theme that plays through and then waits is what most of the
        /// game's music is.
        /// </summary>
        public string? Opened;
    }

    /// <summary>The soundtracks the room is running, one program each.</summary>
    private readonly List<Playing> _programs = [];

    /// <summary>Sounds that move with something, and what they move with.</summary>
    private readonly List<(AudioVoice Voice, string Model)> _following = [];

    /// <summary>Sounds still fading in, with how far through the fade they are.</summary>
    private readonly List<(AudioVoice Voice, double Length, double Gain, double At)> _rising = [];

    /// <summary>
    /// Where the waits and the choices are drawn from.
    /// </summary>
    private readonly Foundation.DeterministicRandom _chance = new(0x51A7C0DE51A7C0DE);

    /// <summary>A bed started by name rather than by a soundtrack, and what it is.</summary>
    private AudioVoice _ambience;
    private string? _looping;
    private AudioPlacement? _loopingAt;

    private AudioVoice _line;

    /// <summary>
    /// How much longer a line with a caption and no recording is held for.
    /// </summary>
    private double _silent;

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
    public Action<AnimationFile?>? Speaking { get; set; }

    /// <summary>The caption for what is being said, if the line carries one.</summary>
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
    public bool Loop(string? name, AudioPlacement? at)
    {
        if (_ambience.Exists)
        {
            _backend.Silence(_ambience);
            _ambience = AudioVoice.None;
        }

        _looping = null;
        _loopingAt = null;

        if (name is not null && _sounds.Read(name) is { } sound)
        {
            _ambience = _backend.Play(sound, AudioBus.Ambience, repeat: true, at);

            if (_ambience.Exists)
            {
                _looping = name;
                _loopingAt = at;
            }
        }

        Reported();

        return _ambience.Exists;
    }

    /// <summary>Says which bed the room is to report as its own.</summary>
    private void Reported()
    {
        for (int i = _programs.Count - 1; i >= 0; i--)
        {
            if (!_programs[i].Bed.Exists)
            {
                continue;
            }

            Ambience = _programs[i].BedName;
            AmbienceAt = _programs[i].BedAt;
            return;
        }

        Ambience = _looping;
        AmbienceAt = _loopingAt;
    }

    /// <summary>Where the ambience is in the room, or null when it plays at the head.</summary>
    public AudioPlacement? AmbienceAt { get; private set; }

    // Where a run of lines has got to, so that ContinueDialogue knows what "the next two"
    // means without the script repeating the plate.
    private string? _stem;
    private int _next;

    // The line being spoken, how far into it we are, and how many of its own soundtrack
    // changes have been performed. A line is a schedule as well as a recording:
    // 79 of the corpus's 81 soundtrack changes are written inside one, because the sentence
    // is the clock the score is cut against. See Cueing.
    private AnimationFile? _sounding;
    private double _spoken;
    private int _cued;
    private IReadOnlyList<AnimationMusic> _changes = [];

    /// <summary>Starts the room's ambience from the soundtracks the scene names.</summary>
    /// <param name="soundtracks">What the scene listed.</param>
    /// <returns>What is playing, or null when none of it could be.</returns>
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
            _programs.Add(new Playing(new SoundtrackProgram(soundtrack, _chance)));
        }

        // One step of each, now, so that a room is not silent for the length of its first
        // wait — and so that the caller has something to report. Time zero rather than a
        // frame's worth: a wait of a second is a second after the room appears.
        foreach (Playing playing in _programs)
        {
            playing.Program.Advance(0, sound => Sound(playing, sound));
        }

        return Ambience
            ?? _programs.Find(p => p.Waiting is { Length: > 0 })?.Waiting
            ?? _programs.Find(p => p.Opened is { Length: > 0 })?.Opened;
    }

    /// <summary>Starts one sound of a soundtrack, and says how long it lasts.</summary>
    /// <param name="playing">The soundtrack it belongs to.</param>
    /// <param name="sound">The sound, as the file describes it.</param>
    /// <returns>Its length in seconds, or zero when it could not be played.</returns>
    private double Sound(Playing playing, SoundtrackSound sound)
    {
        AudioPlacement? at = PlacementOf(sound);

        if (sound.Loop)
        {
            if (!_sounds.Has(sound.Name))
            {
                return 0;
            }

            // This soundtrack's own decode, not the room's: a room with a fountain and a
            // room tone reaches two looping nodes, and one field for the pair meant the
            // second overwrote the first before it had arrived. The fountain was never
            // heard, and once it was — a soundtrack that waits before it loops — it was
            // heard for ever, because the field that could have stopped it had moved on.
            playing.Waiting = sound.Name;
            playing.Where = at;
            playing.Pending = Task.Run(() => _sounds.Read(sound.Name));

            return 0;
        }

        if (_sounds.Read(sound.Name) is not { } wav)
        {
            return 0;
        }

        AudioVoice voice = _backend.Play(wav, Bus(playing.Program.Kind), repeat: false, at);

        if (!voice.Exists)
        {
            return 0;
        }

        // Held, so that leaving the room can stop it. A theme is a minute long and a room
        // is often left in the middle of one; on the Effects bus that was covered by the
        // bus being stopped, but a soundtrack saying Music or Ambient is not on that bus
        // and nothing else was holding it.
        playing.Voices.Add(voice);
        playing.Opened ??= sound.Name;

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
    private static AudioBus Bus(SoundtrackKind kind) => kind switch
    {
        SoundtrackKind.Music => AudioBus.Music,
        SoundtrackKind.Effect => AudioBus.Effects,
        _ => AudioBus.Ambience,
    };

    /// <summary>Where a soundtrack's sound is, if it says.</summary>
    /// <param name="sound">The sound, as its soundtrack describes it.</param>
    /// <returns>Its placement, or null when it belongs at the listener.</returns>
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
    public int Speak(string plate, int lines)
    {
        ArgumentNullException.ThrowIfNull(plate);

        // Replacing the line, not ending the room: whatever it had left to do to the music
        // still happens. The reference does not stop the outgoing line's animation at all,
        // so its nodes go on firing there; this is the nearest thing with one voice.
        Hush(performed: true);

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

        _silent = 0;

        // Tapping through the words does not tap through what they do to the room. The
        // player skipping "But I'm afraid I have bad news" would otherwise skip the fight
        // music that comes up under its last few frames, and the room would be wrong for
        // the rest of the scene.
        Ended(performed: true);

        Saying = null;
        Caption = null;
        Speaker = null;
        Speaking?.Invoke(null);

        Next();
        return true;
    }

    /// <summary>Stops whatever is being said and forgets the rest of it.</summary>
    public void Hush() => Hush(performed: false);

    /// <summary>Stops whatever is being said, saying what becomes of its schedule.</summary>
    /// <param name="performed">
    /// Whether the soundtrack changes the line had not reached yet still happen.
    /// </param>
    private void Hush(bool performed)
    {
        if (_line.Exists)
        {
            _backend.Silence(_line);
            _line = AudioVoice.None;
        }

        _silent = 0;

        Ended(performed);

        _speaking.Clear();
        Saying = null;
        Caption = null;
        Speaker = null;
        Speaking?.Invoke(null);
    }

    /// <summary>Stops the one-shot sounds, leaving the ambience and the dialogue.</summary>
    public void Quiet() => _backend.StopBus(AudioBus.Effects);

    /// <summary>Stops everything that is sounding, leaving the soundtracks running.</summary>
    public void Silence()
    {
        Hush();
        Loop(null);
        Quiet();

        foreach (Playing playing in _programs)
        {
            Silence(playing);
        }

        Reported();
    }

    /// <summary>Stops everything one soundtrack has going, and forgets its decode.</summary>
    /// <param name="playing">The soundtrack.</param>
    private void Silence(Playing playing)
    {
        foreach (AudioVoice voice in playing.Voices)
        {
            _backend.Silence(voice);
            Forget(voice);
        }

        playing.Voices.Clear();

        if (playing.Bed.Exists)
        {
            _backend.Silence(playing.Bed);
            Forget(playing.Bed);
        }

        playing.Bed = AudioVoice.None;
        playing.BedName = null;
        playing.BedAt = null;

        // A bed still being decoded is dropped rather than started: the room it was going
        // to sound like is the room being left.
        playing.Pending = null;
        playing.Waiting = null;
        playing.Where = null;
    }

    /// <summary>Drops a stopped voice from the lists that would still be moving it.</summary>
    /// <param name="voice">The voice that has just been silenced.</param>
    private void Forget(AudioVoice voice)
    {
        _following.RemoveAll(f => f.Voice.Id == voice.Id);
        _rising.RemoveAll(r => r.Voice.Id == voice.Id);
    }

    /// <summary>
    /// Ends the room, and everything it was saying and sounding like with it.
    /// </summary>
    public void Leave()
    {
        Hush();
        Quiet();

        // A soundtrack says how its sound stops: play to the end, fade, or cut. Leaving
        // the room is the forced kind, so even "play to the end" stops — the reference
        // does the same, and a creak carried into the next room is a creak in the wrong
        // room. Every sound each soundtrack has going, not just the one it is timing off
        // and not just the ones on the effects bus: a theme is a minute of music on the
        // music bus, and one left playing is heard under the next room's.
        foreach (Playing playing in _programs)
        {
            Silence(playing);
        }

        _programs.Clear();
        _following.Clear();
        _rising.Clear();

        if (_ambience.Exists)
        {
            _backend.Silence(_ambience);
        }

        _ambience = AudioVoice.None;
        _looping = null;
        _loopingAt = null;
        Ambience = null;
        AmbienceAt = null;
    }

    /// <summary>Starts the room's bed, once it has finished decoding.</summary>
    private void Begin(Playing playing, string? name, AudioPlacement? at)
    {
        if (name is null || _sounds.Read(name) is not { } sound)
        {
            return;
        }

        // On the bus its soundtrack asks for. A bed is usually ambience, but a looping
        // soundtrack that says Music is music and belongs under that slider.
        playing.Bed = _backend.Play(sound, Bus(playing.Program.Kind), repeat: true, at);

        if (!playing.Bed.Exists)
        {
            return;
        }

        _backend.SetVoiceGain(playing.Bed, 1f);

        playing.BedName = name;
        playing.BedAt = at;

        Reported();
    }

    /// <summary>Starts a soundtrack a script named, on top of the room's own.</summary>
    /// <param name="track">The file.</param>
    /// <returns>True if it was not already playing.</returns>
    public bool Play(SoundtrackFile track) => Play(track, looping: true);

    /// <summary>Starts a soundtrack, saying whether it walks its list more than once.</summary>
    /// <param name="track">The file.</param>
    /// <param name="looping">
    /// Whether it goes round again when its list is spent. <c>PLAYSOUNDTRACKTBS</c> is the
    /// once-through form; nothing in the corpus asks for it, and it is here because the
    /// program already knows how and the alternative is a flag read and then dropped.
    /// </param>
    /// <returns>True if it was not already playing.</returns>
    public bool Play(SoundtrackFile track, bool looping)
    {
        ArgumentNullException.ThrowIfNull(track);

        foreach (Playing running in _programs)
        {
            if (running.Program.Track.Name.Equals(track.Name, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        var playing = new Playing(new SoundtrackProgram(track, _chance, looping));
        _programs.Add(playing);

        // One step now, so a soundtrack started by a script is heard on the frame it was
        // asked for rather than on the next.
        playing.Program.Advance(0, sound => Sound(playing, sound));

        return true;
    }

    /// <summary>Stops one soundtrack, or every soundtrack.</summary>
    /// <param name="name">Which one, or null for all of them.</param>
    /// <returns>How many were stopped.</returns>
    public int StopSoundtrack(string? name = null)
    {
        int stopped = 0;

        for (int i = _programs.Count - 1; i >= 0; i--)
        {
            // Compared without extensions on either side. Whether a caller writes
            // "FightDrone" or "FightDrone.STK" is down to who typed the line — the scripts
            // always write it and the animation nodes are split about half and half — and
            // the two mean the same soundtrack.
            if (name is { Length: > 0 } &&
                !_programs[i].Program.Track.Name.Equals(name, StringComparison.OrdinalIgnoreCase) &&
                !Path.GetFileNameWithoutExtension(_programs[i].Program.Track.Name)
                    .Equals(
                        Path.GetFileNameWithoutExtension(name),
                        StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Silence(_programs[i]);
            _programs.RemoveAt(i);
            stopped++;
        }

        Reported();

        return stopped;
    }

    /// <summary>Does what an animation's soundtrack node says.</summary>
    /// <param name="change">The node.</param>
    /// <returns>True if anything started or stopped.</returns>
    public bool Cue(AnimationMusic change)
    {
        if (change.Stop)
        {
            return StopSoundtrack(change.Track) > 0;
        }

        return change.Track is { Length: > 0 } named &&
               Soundtracks?.Invoke(named) is { } track &&
               Play(track, change.Looping);
    }

    /// <summary>The soundtracks the room is running, by name.</summary>
    public IReadOnlyList<string> Running =>
        [.. _programs.Select(p => p.Program.Track.Name)];

    /// <summary>Where a line is spoken from, or null when it belongs at the head.</summary>
    /// <param name="line">The line's animation, which names its speaker in its caption.</param>
    /// <returns>The placement, or null to centre it.</returns>
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
    private const float DialogueNear = 300f;

    /// <summary>And how far away it is as quiet as it gets.</summary>
    private const float DialogueFar = 2000f;

    /// <summary>Which speakers are centred and which are placed in the room.</summary>
    public DialogueRoutingOptions Routing { get; set; } = new();

    /// <summary>Reads a soundtrack by name, for the calls that name one.</summary>
    public Func<string, SoundtrackFile?>? Soundtracks { get; set; }

    /// <summary>Where a model in the room is, for a sound that moves with it.</summary>
    public Func<string, Vector3?>? Where { get; set; }

    /// <summary>Brings the sounds that are fading in up to their own level.</summary>
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

    /// <summary>Drops the soundtrack sounds that have finished on their own.</summary>
    private void Spent()
    {
        foreach (Playing playing in _programs)
        {
            for (int i = playing.Voices.Count - 1; i >= 0; i--)
            {
                if (!_backend.IsPlaying(playing.Voices[i]))
                {
                    playing.Voices.RemoveAt(i);
                }
            }
        }
    }

    /// <summary>Starts the next line when the last one has finished.</summary>
    /// <param name="seconds">How long since the last frame, for the fades and the waits.</param>
    public void Update(double seconds = 0)
    {
        _backend.Update();

        // What the line being spoken does to the music, first: a soundtrack it starts this
        // frame should be walked by the loop below on this frame rather than the next.
        Cueing(seconds);

        // The room's own soundtracks, each a list being walked. Before the decode below
        // rather than after it, so a bed a program asks for this frame is picked up this
        // frame rather than the next.
        foreach (Playing playing in _programs)
        {
            playing.Program.Advance(seconds, sound => Sound(playing, sound));
        }

        Following();
        Rising(seconds);
        Spent();

        // A soundtrack is a five-minute MP3 and decoding one is a quarter of a second, which
        // used to sit between a room being ready and the player seeing it. It is decoded
        // beside the first frames instead and started on whichever one it is ready for. The
        // device work stays here, on the thread that owns the device.
        foreach (Playing playing in _programs)
        {
            if (playing.Pending is not { IsCompleted: true } finished)
            {
                continue;
            }

            string? name = playing.Waiting;
            AudioPlacement? at = playing.Where;

            playing.Pending = null;
            playing.Waiting = null;
            playing.Where = null;

            if (finished.IsCompletedSuccessfully && finished.Result is not null)
            {
                // A room's bed is a five-minute MP3 and takes a moment to decode, so the
                // room has been standing silent for that moment. That is the cost of not
                // carrying the last room's sound into this one.
                Begin(playing, name, at);
            }
        }

        // A line that is only words runs down here rather than on the device. Before the
        // check below and not inside it: it has no voice for that to look at.
        if (_silent > 0)
        {
            _silent -= seconds;

            if (_silent <= 0)
            {
                _silent = 0;
                Ended(performed: true);

                Saying = null;
                Caption = null;
                Speaker = null;

                Next();
            }
        }

        if (_line.Exists && !_backend.IsPlaying(_line))
        {
            _line = AudioVoice.None;

            // A YAK is a few frames longer than its recording — the mouth closes before the
            // last frame and the DIALOGUECUE sits after it — so anything the line had left
            // is performed here rather than lost to the difference.
            Ended(performed: true);

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
        // Whatever was standing on the screen belongs to the line that is over. Cleared
        // here rather than only where a hold runs out, because a run can be continued out
        // from under one.
        _silent = 0;

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

            // A line's animation names its own audio, and where it names none the licence
            // plate does. See Asset.
            foreach (string named in Named(animation, yak))
            {
                if (_sounds.Read(named) is not { } sound)
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
                    Opening(animation);
                    Speaking?.Invoke(animation);
                    return;
                }
            }

            // The animation is there, its audio is not, and it still has something to say.
            // A YAK whose recording was deleted keeps its [GK3] caption and simply names no
            // sound, which is how eighteen of the crow's-nest puzzle's nineteen lines
            // survive; the shipped game has a handful more. Dropping those left the noun
            // silent *and* wordless while the waited StartVoiceOver went on spending the
            // animation's three seconds — a click that visibly does nothing, which is
            // exactly how it was reported. So a line with words holds for as long as the
            // animation is, and the caption stands for that long.
            if (animation.Captions.Count > 0 && animation.Duration > 0)
            {
                _silent = animation.Duration;
                Opening(animation);
                return;
            }

            // Nothing to hear and nothing to read, so the line is skipped rather than
            // holding up the ones behind it. What it was going to do to the music still
            // happens, all at once: the line contributes no time, and a fight whose first
            // sentence is missing should still get its music.
            Opening(animation);
            Ended(performed: true);

            Saying = null;
            Caption = null;
            Speaker = null;
        }
    }

    /// <summary>The sounds to try for a line, in order.</summary>
    /// <param name="line">The line's animation.</param>
    /// <param name="plate">The licence plate it was read under.</param>
    /// <returns>What to ask the sound library for.</returns>
    private static IEnumerable<string> Named(AnimationFile line, string plate) =>
        line.Sounds.Count > 0
            ? line.Sounds.Select(cue => cue.Name)
            : Asset(plate) is { } implied ? [implied] : [];

    /// <summary>The recording a licence plate implies, by the game's own naming.</summary>
    /// <param name="plate">Ten characters, as <c>StartVoiceOver</c> takes them.</param>
    /// <returns>The asset name, or null when the name is not a plate.</returns>
    private static string? Asset(string plate) =>
        plate.Length == 10 ? $"A{plate[..7]}.{plate[7..]}" : null;

    /// <summary>Takes on the schedule the line about to be spoken carries.</summary>
    private void Opening(AnimationFile line)
    {
        _sounding = line;
        _spoken = 0;
        _cued = 0;
        _changes = line.Music.Count > 1 ? [.. line.Music.OrderBy(m => m.Frame)] : line.Music;

        // Frame zero is now rather than in a frame's time, as it is everywhere else a
        // schedule is taken on.
        Cueing(0);
    }

    /// <summary>Performs whatever the line being spoken has reached.</summary>
    /// <param name="seconds">How long since the last frame.</param>
    private void Cueing(double seconds)
    {
        if (_sounding is not { } line)
        {
            return;
        }

        _spoken += seconds;
        double rate = Math.Max(1, line.Rate);

        while (_cued < _changes.Count && _changes[_cued].Frame / rate <= _spoken)
        {
            Cue(_changes[_cued++]);
        }
    }

    /// <summary>Lets go of the schedule the line being spoken carried.</summary>
    /// <param name="performed">
    /// Whether what it had not reached yet still happens. It does wherever the line ends
    /// because it is over, is cut short by the next one, or is tapped through by the
    /// player — a soundtrack change is a statement about the room and outlives the sentence
    /// it was timed against, and every one of them in the corpus sits before its line's
    /// <c>DIALOGUECUE</c>, so this is a safety net rather than the usual path. It does not
    /// where the room itself is being left or silenced: starting music on the way out is
    /// music in the wrong room.
    /// </param>
    private void Ended(bool performed)
    {
        if (performed)
        {
            while (_cued < _changes.Count)
            {
                Cue(_changes[_cued++]);
            }
        }

        _sounding = null;
        _changes = [];
        _cued = 0;
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
