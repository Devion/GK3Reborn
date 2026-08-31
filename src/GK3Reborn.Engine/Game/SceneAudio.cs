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

    /// <summary>A soundtrack the room is running, and the sound it has going.</summary>
    /// <param name="program">The list being walked.</param>
    /// <remarks>
    /// A program that is not holding a handle to what it started cannot stop it, and a
    /// sound nothing can stop is a sound that outlives its room: the theme of the room
    /// just left, still playing under the theme of the room just entered. The reference
    /// keeps the same handle for the same reason — <c>PlayingSoundtrack</c> holds the
    /// sound its current node started, and the scene forces every one of them to stop
    /// as it unloads.
    /// </remarks>
    private sealed class Playing(SoundtrackProgram program)
    {
        /// <summary>The list being walked.</summary>
        public SoundtrackProgram Program { get; } = program;

        /// <summary>The one-shots it has going, oldest first.</summary>
        /// <remarks>
        /// More than one at a time is ordinary: a node's sound is what times the next
        /// step, and a step that decides to play something the moment the last sound was
        /// due to end will overlap it by whatever the decode rounded off.
        /// </remarks>
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
    /// <remarks>
    /// Its own generator rather than the game's, so that how often a room creaks does not
    /// depend on how many times anybody has clicked on anything — and seeded, so that two
    /// runs of the same scene make the same noises at the same moments. ADR 0004.
    /// </remarks>
    private readonly Foundation.DeterministicRandom _chance = new(0x51A7C0DE51A7C0DE);

    /// <summary>A bed started by name rather than by a soundtrack, and what it is.</summary>
    private AudioVoice _ambience;
    private string? _looping;
    private AudioPlacement? _loopingAt;

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
    /// <remarks>
    /// A room may have several going at once — 62 of the game's 493 timeblocks name more
    /// than one looping soundtrack, and CSE's afternoon means its room tone and its
    /// fountain to be heard together — so there is no single bed to hold in a field. The
    /// most recent one is what gets reported, and the rest are running underneath it.
    /// </remarks>
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
            _programs.Add(new Playing(new SoundtrackProgram(soundtrack, _chance)));
        }

        // One step of each, now, so that a room is not silent for the length of its first
        // wait — and so that the caller has something to report. Time zero rather than a
        // frame's worth: a wait of a second is a second after the room appears.
        foreach (Playing playing in _programs)
        {
            playing.Program.Advance(0, sound => Sound(playing, sound));
        }

        return Ambience ?? _programs.Find(p => p.Waiting is { Length: > 0 })?.Waiting;
    }

    /// <summary>Starts one sound of a soundtrack, and says how long it lasts.</summary>
    /// <param name="playing">The soundtrack it belongs to.</param>
    /// <param name="sound">The sound, as the file describes it.</param>
    /// <returns>Its length in seconds, or zero when it could not be played.</returns>
    /// <remarks>
    /// <para>
    /// A sound that loops is the room's bed: it is what the room sounds like for as long
    /// as the player is in it, and it stops when the player leaves the room.
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
    /// <remarks>
    /// What the line was going to do to the music is forgotten with it. A caller that means
    /// to replace the line rather than to end the room says so — see <see cref="Ended"/>.
    /// </remarks>
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

        Ended(performed);

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

    /// <summary>Stops everything that is sounding, leaving the soundtracks running.</summary>
    /// <remarks>
    /// Every voice, whichever bus it is on, including the beds — but not the programs
    /// that started them, which go on walking their lists and will play whatever their
    /// next node says. A soundtrack holding at a looping node has nothing left to play,
    /// so a room silenced this way stays silent; the reference behaves the same way, for
    /// the same reason.
    /// </remarks>
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
    /// <remarks>
    /// The bed used to be handed to a crossfade and left playing while the next room's came
    /// up underneath it. Two beds on one bus is two beds you can hear, and a room whose
    /// soundtrack asks for a long <c>FadeOutMS</c> carried its own sound well into the next
    /// room; between them the overlap was loud enough to be the wrong room. So the bed stops
    /// where the room does.
    /// </remarks>
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
    /// <remarks>
    /// At its own level from the first sample. The room it replaced stopped when the player
    /// left it, so there is nothing underneath for this to come up over.
    /// </remarks>
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
    /// <remarks>
    /// A script's soundtrack is another program running beside the room's rather than
    /// instead of it: <c>PlaySoundTrack</c> in the middle of a scene is a car arriving or
    /// a storm getting up, and the room is still the room underneath it. Playing one
    /// twice does nothing, which is what the original does — the same list started twice
    /// would be the same sound at two different points in its own walk.
    /// </remarks>
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
    /// <remarks>
    /// A soundtrack stops with everything it had going: its bed, if it had reached one,
    /// and the sounds it has playing. Stopping the storm a script started should not
    /// leave the last thunderclap ringing on into the room.
    /// </remarks>
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
    /// <remarks>
    /// <para>
    /// The one place a <c>PLAYSOUNDTRACK</c>, a <c>STOPSOUNDTRACK</c> or a
    /// <c>STOPALLSOUNDTRACKS</c> is performed, whichever kind of animation carried it: a
    /// line of dialogue's own schedule reaches it from inside this class, and a moment's
    /// reaches it through <c>SceneUpdate.Music</c>. Both mean the same thing and neither
    /// should mean it differently.
    /// </para>
    /// <para>
    /// A stop names the soundtrack it stops; stopping one that is not running is nothing
    /// happening rather than a fault, and the corpus does it — <c>MontUpstaris.STK</c> is a
    /// stop for a soundtrack whose real name is spelled the other way, so the original
    /// missed it too.
    /// </para>
    /// </remarks>
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
    /// <remarks>
    /// A room may be running several — RC1 at ten in the morning names a fountain, a room
    /// tone and birdsong — and most of them are silent at any given moment, because a
    /// soundtrack is mostly waiting. So this says what is <em>running</em>, where
    /// <see cref="Ambience"/> says what is sounding.
    /// </remarks>
    public IReadOnlyList<string> Running =>
        [.. _programs.Select(p => p.Program.Track.Name)];

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
    /// Straight-line in gain. A sound whose fade has finished is dropped from the list rather than kept and set to the same
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

    /// <summary>Drops the soundtrack sounds that have finished on their own.</summary>
    /// <remarks>
    /// A soundtrack's sounds are held so that leaving the room can stop them, and a room
    /// stood in for ten minutes starts a great many. The device is the clock here as it is
    /// for dialogue: a sound is over when its source stops, and the handle goes then.
    /// </remarks>
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
    /// <remarks>
    /// Called once a frame. The device is the clock: a line is over when its source stops,
    /// not when a timer says it should have, so the two never drift apart.
    /// </remarks>
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
                    Opening(animation);
                    Speaking?.Invoke(animation);
                    return;
                }
            }

            // The animation is there but its audio is not, so the line is skipped rather
            // than holding up the ones behind it. What it was going to do to the music
            // still happens, all at once: the line contributes no time, and a fight whose
            // first sentence is missing should still get its music.
            Opening(animation);
            Ended(performed: true);

            Saying = null;
            Caption = null;
            Speaker = null;
        }
    }

    /// <summary>Takes on the schedule the line about to be spoken carries.</summary>
    /// <remarks>
    /// Sorted by frame rather than taken in file order, because the files are not written in
    /// order — <c>E0SB2J3H7B1</c> stops one soundtrack at frame 9 on the line after the one
    /// that starts another at frame 10 — and a cursor over file order would perform them the
    /// wrong way round.
    /// </remarks>
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
    /// <remarks>
    /// The line is the clock. 79 of the corpus's 81 soundtrack changes are written inside
    /// one, on a frame chosen against the words — <c>E01KED3S4U6</c> cuts the lobby's music
    /// at frame 40 of "But I'm afraid I have bad news" and brings the fight's up at 50 —
    /// so performing them anywhere but against the recording's own clock puts the music on
    /// the wrong side of the sentence.
    /// </remarks>
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
