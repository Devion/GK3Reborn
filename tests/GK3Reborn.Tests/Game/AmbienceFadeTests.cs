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
/// Tests for one room's music becoming the next room's.
/// </summary>
/// <remarks>
/// Leaving a room used to stop its bed and entering the next one used to start another, so
/// a door was two cuts with a gap between them where the game was silent. What it should be
/// is one sound becoming another, which needs both playing at once at different levels —
/// and that is the part worth pinning down, because by ear a fade that is slightly wrong
/// and a fade that is not happening at all are hard to tell apart.
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
    /// for as long as the player is in the room and that the next room's bed crossfades
    /// with. 83 of the corpus's 269 soundtracks have one; the rest are programs of
    /// occasional sounds and have nothing to crossfade, which is what these tests are not
    /// about. See <c>SoundtrackProgramTests</c> for those.
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
        // Nothing to fade from, so nothing is faded: the bed comes in at its own level
        // rather than rising out of silence over a second and a half.
        var device = new Recorder();
        SceneAudio audio = Audio(device, "R25THEME");

        audio.StartAmbience([Track("R25THEME")]);
        Settle(audio, device, "R25THEME");

        Assert.Equal(1f, device.Level("R25THEME"));
        Assert.Empty(device.Stopped);
    }

    [Fact]
    public void One_rooms_bed_becomes_the_next_rooms()
    {
        var device = new Recorder();
        SceneAudio audio = Audio(device, "R25THEME", "LBYTHEME");

        audio.StartAmbience([Track("R25THEME")]);
        Settle(audio, device, "R25THEME");

        // Through the door. What the room was saying stops; what it sounded like does not.
        audio.Leave();
        Assert.DoesNotContain(device.Voice("R25THEME"), device.Stopped);

        audio.StartAmbience([Track("LBYTHEME")]);
        Settle(audio, device, "LBYTHEME");

        // Both playing, and the new one is not yet audible.
        Assert.Equal(2, device.Playing);
        Assert.True(device.Level("LBYTHEME") < 0.5f, $"{device.Level("LBYTHEME")}");

        // Half way through the three seconds the soundtrack asks for: one going down, the
        // other coming up, and neither of them silent.
        audio.Update(1.5);

        Assert.InRange(device.Level("R25THEME"), 0.2f, 0.8f);
        Assert.InRange(device.Level("LBYTHEME"), 0.2f, 0.8f);

        // And done. The room that has been left stops rather than being held open for ever.
        audio.Update(2.0);

        Assert.Equal(1f, device.Level("LBYTHEME"));
        Assert.Contains(device.Voice("R25THEME"), device.Stopped);
        Assert.Equal(1, device.Playing);
    }

    [Fact]
    public void A_room_with_no_music_lets_the_last_one_fade_out()
    {
        // The same crossfade with nothing on the other side of it, which is a room going
        // quiet rather than the sound being cut off at the door.
        var device = new Recorder();
        SceneAudio audio = Audio(device, "R25THEME");

        audio.StartAmbience([Track("R25THEME")]);
        Settle(audio, device, "R25THEME");

        audio.Leave();
        Assert.Null(audio.StartAmbience([]));

        audio.Update(1.5);
        Assert.InRange(device.Level("R25THEME"), 0.2f, 0.8f);

        audio.Update(2.0);
        Assert.Contains(device.Voice("R25THEME"), device.Stopped);
        Assert.Equal(0, device.Playing);
    }

    [Theory]
    [InlineData(1000, 1.2, true)]
    [InlineData(3000, 1.2, false)]
    public void The_soundtrack_says_how_long_its_room_takes_to_stop(
        int fadeMs, double after, bool over)
    {
        // FadeOutMS is the artists' own answer to how long this room should take to stop
        // being the room you are in, and it is not the same everywhere. A second and a half
        // is only the fallback for the soundtracks that leave it out.
        var device = new Recorder();
        SceneAudio audio = Audio(device, "R25THEME");

        audio.StartAmbience([Track("R25THEME", fadeMs)]);
        Settle(audio, device, "R25THEME");

        audio.Leave();
        audio.Update(after);

        Assert.Equal(over, device.Stopped.Contains(device.Voice("R25THEME")));
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
