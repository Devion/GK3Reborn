using System.Numerics;
using GK3Reborn.Audio;
using GK3Reborn.Content;
using GK3Reborn.Formats.Audio;
using GK3Reborn.Foundation.Diagnostics;
using GK3Reborn.Game;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Tests for the music an animation changes under itself.
/// </summary>
/// <remarks>
/// <para>
/// Seventy-nine of the corpus's 81 soundtrack nodes are inside a line of dialogue's own
/// <c>.YAK</c>, on a frame chosen against the words rather than against the script that
/// started the line: <c>E01KED3S4U6</c> — "But I'm afraid I have bad news" — cuts the
/// lobby's music at frame 40 and brings the fight's up at 50, part-way through the
/// sentence. So the line has to be the clock, which is what these check.
/// </para>
/// <para>
/// They were all read past. A YAK reaches the audio layer, which had no per-frame schedule
/// to hang anything on, so the music never changed and every fight in the game was scored
/// with whatever the room had been playing beforehand.
/// </para>
/// </remarks>
public sealed class AnimationSoundtrackTests
{
    /// <summary>A device that plays nothing and keeps count.</summary>
    private sealed class Recorder : IAudioBackend
    {
        private int _next;

        public SpeakerLayout RequestedLayout => SpeakerLayout.Stereo;

        public SpeakerLayout ActualLayout => SpeakerLayout.Stereo;

        public int Playing => Started.Count - Stopped.Count;

        public List<(AudioVoice Voice, string Name)> Started { get; } = [];

        public List<AudioVoice> Stopped { get; } = [];

        /// <summary>Whether the line being spoken has been made to stop early.</summary>
        public bool LineOver { get; set; }

        public AudioVoice Play(
            WavFile sound, AudioBus bus, bool repeat = false, AudioPlacement? at = null)
        {
            var voice = new AudioVoice(++_next);
            Started.Add((voice, sound.Name));
            return voice;
        }

        public void SetBusGain(AudioBus bus, float gain)
        {
        }

        public void SetVoiceGain(AudioVoice voice, float gain)
        {
        }

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

        public bool IsPlaying(AudioVoice voice) => !LineOver && !Stopped.Contains(voice);

        public void Update()
        {
        }

        public void Dispose()
        {
        }
    }

    /// <summary>Sixteen-bit mono silence, which is all the device here needs.</summary>
    private static byte[] Wav()
    {
        byte[] samples = new byte[2205 * 2];

        var body = new List<byte>();
        body.AddRange("fmt "u8.ToArray());
        body.AddRange(BitConverter.GetBytes(16));
        body.AddRange(BitConverter.GetBytes((short)1));
        body.AddRange(BitConverter.GetBytes((short)1));
        body.AddRange(BitConverter.GetBytes(22050));
        body.AddRange(BitConverter.GetBytes(44100));
        body.AddRange(BitConverter.GetBytes((short)2));
        body.AddRange(BitConverter.GetBytes((short)16));
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

    /// <summary>A soundtrack of one occasional sound, which is enough to be running.</summary>
    private static SoundtrackFile Track(string name) => SoundtrackFile.Parse(
        $"""
        [SOUND]
        Name=NOISE.WAV
        Volume=80.0
        """,
        name,
        new DiagnosticBag());

    /// <summary>
    /// An audio layer with one line in it, carrying the <c>[GK3]</c> nodes given.
    /// </summary>
    /// <remarks>
    /// Fifteen frames a second, so a node's frame number is fifteenths of a second in.
    /// </remarks>
    private static SceneAudio Audio(Recorder device, string nodes, int frames = 60)
    {
        byte[] wav = Wav();

        var sounds = new SoundLibrary(
            name => name.StartsWith("LINE", StringComparison.OrdinalIgnoreCase) ? wav : null,
            name => name.StartsWith("LINE", StringComparison.OrdinalIgnoreCase));

        int count = nodes.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length + 2;

        var animations = new AnimationLibrary(name =>
            name.StartsWith("YAK", StringComparison.OrdinalIgnoreCase)
                ? $"[HEADER]\n{frames}\n\n[SOUNDS]\n1\n0,LINE.WAV,100\n\n[GK3]\n{count}\n" +
                  "0,SPEAKER,GABRIEL\n0,CAPTION,Something is said\n" + nodes
                : null);

        return new SceneAudio(sounds, animations, device)
        {
            Soundtracks = Track,
        };
    }

    [Fact]
    public void A_line_changes_the_music_on_its_own_frames_and_not_before()
    {
        var device = new Recorder();

        SceneAudio audio = Audio(
            device,
            "40,StopAllSoundTracks\n50,PlaySoundTrack,FightDrone.STK\n");

        audio.Play(Track("LBYSNDTRKL1.STK"));
        audio.Speak("YAK1", 1);

        Assert.Equal(["LBYSNDTRKL1.STK"], audio.Running);

        // Frame 40 of a fifteen-frame-a-second line is 2.67 seconds in.
        audio.Update(2.5);
        Assert.Equal(["LBYSNDTRKL1.STK"], audio.Running);

        audio.Update(0.3);
        Assert.Empty(audio.Running);

        // And the fight's comes up ten frames later, in the middle of the sentence.
        audio.Update(0.7);
        Assert.Equal(["FightDrone.STK"], audio.Running);
    }

    [Fact]
    public void The_nodes_are_performed_in_frame_order_rather_than_file_order()
    {
        // E0SB2J3H7B1 writes the stop at frame 9 on the line after the play at frame 10.
        // Taken in file order the stop would silence the soundtrack the play had just
        // started, which on that line is Grace's climb replacing her sneak.
        var device = new Recorder();

        SceneAudio audio = Audio(
            device,
            "10,PlaySoundTrack,GraceClimbSerras\n9,StopSoundTrack,GraceSneakSerras\n");

        audio.Play(Track("GraceSneakSerras"));
        audio.Speak("YAK1", 1);
        audio.Update(1.0);

        Assert.Equal(["GraceClimbSerras"], audio.Running);
    }

    [Fact]
    public void A_soundtrack_is_stopped_whichever_way_its_name_is_spelled()
    {
        // Half the nodes leave the extension off and every script writes it. They mean the
        // same soundtrack, and a stop that misses is music that never goes away.
        var device = new Recorder();
        SceneAudio audio = Audio(device, "5,StopSoundTrack,EmlGlassOnR33\n");

        audio.Play(Track("EmlGlassOnR33.STK"));
        audio.Speak("YAK1", 1);
        audio.Update(0.5);

        Assert.Empty(audio.Running);
    }

    [Fact]
    public void What_a_line_had_left_to_do_still_happens_when_it_is_tapped_through()
    {
        // A soundtrack change is a statement about the room and outlives the sentence it
        // was timed against. A player skipping the line that brings the fight music up
        // would otherwise play the rest of the scene to the wrong music.
        var device = new Recorder();
        SceneAudio audio = Audio(device, "50,PlaySoundTrack,FightDrone.STK\n");

        audio.Speak("YAK1", 1);
        audio.Update(0.5);

        Assert.Empty(audio.Running);
        Assert.True(audio.Skip());
        Assert.Equal(["FightDrone.STK"], audio.Running);
    }

    [Fact]
    public void What_a_line_had_left_to_do_still_happens_when_the_line_ends()
    {
        // A YAK is a few frames longer than its recording, so a node close to the end can
        // fall past the moment the sound stops.
        var device = new Recorder();
        SceneAudio audio = Audio(device, "58,PlaySoundTrack,FightExit.STK\n");

        audio.Speak("YAK1", 1);
        audio.Update(0.5);
        Assert.Empty(audio.Running);

        device.LineOver = true;
        audio.Update(1.0 / 60);

        Assert.Equal(["FightExit.STK"], audio.Running);
    }

    [Fact]
    public void Leaving_the_room_does_not_start_music_the_line_had_not_reached()
    {
        // The other direction, and the reason the two are told apart: a soundtrack started
        // on the way out is music playing in the wrong room.
        var device = new Recorder();
        SceneAudio audio = Audio(device, "50,PlaySoundTrack,FightDrone.STK\n");

        audio.Speak("YAK1", 1);
        audio.Update(0.5);
        audio.Leave();

        Assert.Empty(audio.Running);
    }

}
