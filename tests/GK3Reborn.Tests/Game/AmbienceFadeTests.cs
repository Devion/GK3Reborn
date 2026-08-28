using System.Numerics;
using System.Text;
using GK3Reborn.Audio;
using GK3Reborn.Content;
using GK3Reborn.Formats.Audio;
using GK3Reborn.Foundation.Diagnostics;
using GK3Reborn.Game;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Tests for a room's bed starting and stopping with the room.
/// </summary>
/// <remarks>
/// These once pinned a crossfade: leaving a room handed its bed on, and the next room's came
/// up underneath it over the <c>FadeOutMS</c> the soundtrack asked for. Two beds on one bus
/// is two beds you can hear, and with R25's three seconds the overlap was long enough to be
/// audibly the wrong room. So what is pinned now is the opposite, and it is worth pinning
/// because it is exactly what regressed: <em>one bed at a time</em>.
/// </remarks>
public sealed class AmbienceFadeTests
{
    /// <summary>An audio device that plays nothing and remembers everything.</summary>
    private sealed class Recorder : IAudioBackend
    {
        private int _next;

        public SpeakerLayout RequestedLayout => SpeakerLayout.Stereo;

        public SpeakerLayout ActualLayout => SpeakerLayout.Stereo;

        public int Playing => Started.Count - Stopped.Count;

        /// <summary>Every sound started, in order.</summary>
        public List<(AudioVoice Voice, string Name)> Started { get; } = [];

        /// <summary>Every voice stopped.</summary>
        public List<AudioVoice> Stopped { get; } = [];

        /// <summary>Where each voice's level was last put.</summary>
        public Dictionary<int, float> Levels { get; } = [];

        public AudioVoice Play(
            WavFile sound, AudioBus bus, bool repeat = false, AudioPlacement? at = null)
        {
            var voice = new AudioVoice(++_next);
            Started.Add((voice, sound.Name));
            Levels[voice.Id] = 1f;
            return voice;
        }

        public void SetBusGain(AudioBus bus, float gain)
        {
        }

        public void SetVoiceGain(AudioVoice voice, float gain) => Levels[voice.Id] = gain;

        public void Move(AudioVoice voice, Vector3 position)
        {
        }

        public void Listen(Vector3 position, Vector3 forward, Vector3 up)
        {
        }

        public void Silence(AudioVoice voice) => Stopped.Add(voice);

        public void StopBus(AudioBus bus)
        {
        }

        public bool IsPlaying(AudioVoice voice) => !Stopped.Contains(voice);

        public void Update()
        {
        }

        public void Dispose()
        {
        }

        /// <summary>The voice a named sound is playing on, or none.</summary>
        public AudioVoice Voice(string name) =>
            Started.Find(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Voice;

        public float Level(string name) => Levels.GetValueOrDefault(Voice(name).Id, -1f);
    }

    /// <summary>The shortest thing that will read back as a sound.</summary>
    private static byte[] Wav()
    {
        byte[] samples = new byte[64];
        var body = new List<byte>();

        body.AddRange("fmt "u8.ToArray());
        body.AddRange(BitConverter.GetBytes(16));
        body.AddRange(BitConverter.GetBytes((short)1));      // PCM
        body.AddRange(BitConverter.GetBytes((short)1));      // mono
        body.AddRange(BitConverter.GetBytes(22050));
        body.AddRange(BitConverter.GetBytes(44100));         // bytes a second
        body.AddRange(BitConverter.GetBytes((short)2));      // block align
        body.AddRange(BitConverter.GetBytes((short)16));     // bits
        body.AddRange("data"u8.ToArray());
        body.AddRange(BitConverter.GetBytes(samples.Length));
        body.AddRange(samples);

        var file = new List<byte>();
        file.AddRange("RIFF"u8.ToArray());
        file.AddRange(BitConverter.GetBytes(4 + body.Count));
        file.AddRange("WAVE"u8.ToArray());
        file.AddRange(body);

        return [.. file];
    }

    private static SoundLibrary Sounds(params string[] names)
    {
        HashSet<string> held = new(
            names.Select(n => n + ".WAV").Concat(names), StringComparer.OrdinalIgnoreCase);

        byte[] wav = Wav();

        return new SoundLibrary(name => held.Contains(name) ? wav : null, held.Contains);
    }

    /// <summary>A soundtrack naming one looping sound, with the fade its room asks for.</summary>
    /// <remarks>
    /// <c>Loop=1</c> is what makes a sound the room's <em>bed</em> — the thing that plays
    /// for as long as the player is in the room and stops when they leave it. 83 of the
    /// corpus's 269 soundtracks have one; the rest are programs of occasional sounds and
    /// have no bed at all, which is what these tests are not about. See
    /// <c>SoundtrackProgramTests</c> for those.
    /// </remarks>
    private static SoundtrackFile Track(string sound, int fadeMs = 3000) => SoundtrackFile.Parse(
        $"""
        [SOUND]
        Name={sound}
        Volume=80.0
        Loop=1
        StopMethod=1
        FadeOutMS={fadeMs}
        """,
        sound + ".STK",
        new DiagnosticBag());

    private static SceneAudio Audio(Recorder device, params string[] names) =>
        new(Sounds(names), new AnimationLibrary(_ => null), device);

    /// <summary>Pumps frames until the room's bed has been decoded and started.</summary>
    private static void Settle(SceneAudio audio, Recorder device, string name)
    {
        for (int i = 0; i < 2000 && device.Voice(name).Id == 0; i++)
        {
            audio.Update();
            Thread.Sleep(1);
        }

        Assert.NotEqual(0, device.Voice(name).Id);
    }

    [Fact]
    public void The_first_room_of_the_game_just_starts()
    {
        // The bed comes in at its own level rather than rising out of silence.
        var device = new Recorder();
        SceneAudio audio = Audio(device, "R25THEME");

        audio.StartAmbience([Track("R25THEME")]);
        Settle(audio, device, "R25THEME");

        Assert.Equal(1f, device.Level("R25THEME"));
        Assert.Empty(device.Stopped);
    }

    [Fact]
    public void One_rooms_bed_stops_before_the_next_rooms_starts()
    {
        var device = new Recorder();
        SceneAudio audio = Audio(device, "R25THEME", "LBYTHEME");

        audio.StartAmbience([Track("R25THEME")]);
        Settle(audio, device, "R25THEME");

        // Through the door. The bed goes with the room, at the door rather than three
        // seconds into the next room.
        audio.Leave();

        Assert.Contains(device.Voice("R25THEME"), device.Stopped);
        Assert.Equal(0, device.Playing);

        audio.StartAmbience([Track("LBYTHEME")]);
        Settle(audio, device, "LBYTHEME");

        // One bed, at its own level from the first sample. This is the assertion the
        // crossfade broke: `Playing` was 2 for as long as the fade lasted.
        Assert.Equal(1, device.Playing);
        Assert.Equal(1f, device.Level("LBYTHEME"));
    }

    [Fact]
    public void A_room_with_no_music_is_silent_rather_than_still_playing_the_last_one()
    {
        var device = new Recorder();
        SceneAudio audio = Audio(device, "R25THEME");

        audio.StartAmbience([Track("R25THEME")]);
        Settle(audio, device, "R25THEME");

        audio.Leave();
        Assert.Null(audio.StartAmbience([]));

        // Nothing carries over into a room that names no ambience, and no amount of
        // pumping frames brings the last room's sound back.
        Assert.Contains(device.Voice("R25THEME"), device.Stopped);
        Assert.Equal(0, device.Playing);

        audio.Update(2.0);
        Assert.Equal(0, device.Playing);
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(3000)]
    public void How_long_the_soundtrack_asks_to_fade_for_no_longer_holds_the_bed_open(int fadeMs)
    {
        // `FadeOutMS` decided how long the outgoing bed stayed audible, and R25's three
        // seconds is most of a walk through a door. It has no consumer now, and the room
        // it belongs to stops at the same moment whatever it says.
        var device = new Recorder();
        SceneAudio audio = Audio(device, "R25THEME");

        audio.StartAmbience([Track("R25THEME", fadeMs)]);
        Settle(audio, device, "R25THEME");

        audio.Leave();

        Assert.Contains(device.Voice("R25THEME"), device.Stopped);
    }

    [Fact]
    public void Leaving_twice_before_a_bed_has_decoded_leaves_nothing_playing()
    {
        // The crossfade held the outgoing voice in a field of its own, and a second
        // departure overwrote that field with the voice the first one had already cleared.
        // The first room's bed was then playing, owned by nothing, and no later room could
        // stop it — a bed per hurried door, all of them audible at once. There is no such
        // field now, and this is what says so.
        var device = new Recorder();
        SceneAudio audio = Audio(device, "R25THEME", "LBYTHEME", "MCBTHEME");

        audio.StartAmbience([Track("R25THEME")]);
        Settle(audio, device, "R25THEME");

        audio.Leave();
        audio.StartAmbience([Track("LBYTHEME")]);

        // Straight out again, before the second bed has finished decoding.
        audio.Leave();
        audio.StartAmbience([Track("MCBTHEME")]);
        Settle(audio, device, "MCBTHEME");

        Assert.Equal(1, device.Playing);
        Assert.Equal("MCBTHEME", audio.Ambience);
    }

    [Fact]
    public void A_rooms_soundtrack_plays_its_moods_rather_than_one_sound_for_ever()
    {
        // R25's afternoon, as the game ships it: a wait, the room's theme once, then
        // moods with gaps between them. What this pins is that the whole list is walked —
        // before it was, the hotel room played R25Theme1 on a loop for the length of the
        // afternoon and none of its four moods at all.
        var device = new Recorder();
        SceneAudio audio = Audio(device, "R25THEME1", "R25MOOD1", "R25MOOD2");

        SoundtrackFile track = SoundtrackFile.Parse(
            """
            [WAIT]
            MinWaitMS=1000
            Repeat=1

            [SOUND]
            Name=R25Theme1
            Volume=80.0
            Repeat=1

            [WAIT]
            MinWaitMS=2000
            MaxWaitMS=4000

            [SOUND]
            Name=R25Mood1

            [WAIT]
            MinWaitMS=1000
            MaxWaitMS=2000

            [SOUND]
            Name=R25Mood2
            """,
            "R25SNDTRKL.STK",
            new DiagnosticBag());

        audio.StartAmbience([track]);

        Assert.Equal(["R25SNDTRKL.STK"], audio.Running);

        // A minute of room, a twentieth of a second at a time.
        for (int i = 0; i < 1200; i++)
        {
            audio.Update(0.05);
        }

        Assert.True(device.Started.Count >= 4, $"{device.Started.Count} sound(s) in a minute");

        // The theme once, because its node says Repeat=1, and the moods over and over.
        Assert.Equal(1, device.Started.Count(s => s.Name.Equals("R25THEME1", StringComparison.OrdinalIgnoreCase)));
        Assert.True(device.Started.Count(s => s.Name.Equals("R25MOOD2", StringComparison.OrdinalIgnoreCase)) > 1);
    }
}
