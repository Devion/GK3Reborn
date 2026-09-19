using System.Numerics;
using GK3Reborn.Audio;
using GK3Reborn.Content;
using GK3Reborn.Formats.Audio;
using GK3Reborn.Game;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Tests for two people speaking at once, which the game asks for exactly once.
/// </summary>
public sealed class SimultaneousDialogueTests
{
    /// <summary>A device that remembers what was started and what was cut off.</summary>
    private sealed class Recorder : IAudioBackend
    {
        private readonly HashSet<int> _stopped = [];
        private int _next;

        public SpeakerLayout RequestedLayout => SpeakerLayout.Stereo;

        public SpeakerLayout ActualLayout => SpeakerLayout.Stereo;

        public int Playing => Started.Count - _stopped.Count;

        public List<string> Started { get; } = [];

        public List<string> Silenced { get; } = [];

        public AudioVoice Play(
            WavFile sound, AudioBus bus, bool repeat = false, AudioPlacement? at = null)
        {
            Started.Add(sound.Name);
            return new AudioVoice(++_next);
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

        public void Silence(AudioVoice voice)
        {
            if (_stopped.Add(voice.Id))
            {
                Silenced.Add(Started[voice.Id - 1]);
            }
        }

        public void StopBus(AudioBus bus)
        {
        }

        public bool IsPlaying(AudioVoice voice) => !_stopped.Contains(voice.Id);

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

    /// <summary>
    /// The two lines of R33310A, named as the script names them: nineteen frames of
    /// Gabriel with its <c>DIALOGUECUE</c> on frame one, and sixteen of Mosely.
    /// </summary>
    private static SceneAudio Audio(Recorder device)
    {
        byte[] wav = Wav();

        var sounds = new SoundLibrary(
            name => name.StartsWith("NAH", StringComparison.OrdinalIgnoreCase) ? wav : null,
            name => name.StartsWith("NAH", StringComparison.OrdinalIgnoreCase));

        // The library tries four names for every line — <name>.ANM, <name>.YAK and the
        // localised <E><name> of each — so this answers to the plate rather than to a file.
        var animations = new AnimationLibrary(name => Path.GetFileNameWithoutExtension(name) switch
        {
            "GABE1" =>
                "[HEADER]\n19\n\n[SOUNDS]\n1\n0,NAHGABE.WAV,100\n\n[GK3]\n3\n" +
                "0,SPEAKER,GABRIEL\n0,CAPTION,Nah.\n1,DIALOGUECUE\n",
            "MOSE1" =>
                "[HEADER]\n16\n\n[SOUNDS]\n1\n0,NAHMOSE.WAV,100\n\n[GK3]\n3\n" +
                "0,SPEAKER,MOSELY\n0,CAPTION,Nah.\n15,DIALOGUECUE\n",
            _ => null,
        });

        return new SceneAudio(sounds, animations, device);
    }

    [Fact]
    public void Two_lines_started_in_one_breath_are_both_heard()
    {
        // R33310A, day 3 at 310A: Mosely suggests Gabriel talk to Grace about his feelings
        // and the two of them answer "Nah." together. It is the only wait block in the
        // corpus holding two StartDialogue calls, and the port cut the first off the
        // instant the second arrived, so only Mosely's was ever heard. Reported as such.
        var device = new Recorder();
        SceneAudio audio = Audio(device);

        audio.Speak("GABE1", 1);
        audio.Speak("MOSE1", 1);

        Assert.Equal(["NAHGABE.WAV", "NAHMOSE.WAV"], device.Started);
        Assert.Empty(device.Silenced);
        Assert.Equal(1, audio.Chorus);
        Assert.Equal(2, device.Playing);
    }

    [Fact]
    public void A_line_started_a_frame_later_still_takes_over()
    {
        // The other half of the same rule, and the reason it is a frame rather than a
        // count: a player clicking a second noun while the first is still talking wants
        // the first line to stop, and cannot possibly click inside the frame it began in.
        var device = new Recorder();
        SceneAudio audio = Audio(device);

        audio.Speak("GABE1", 1);
        audio.Update(1.0 / 60);
        audio.Speak("MOSE1", 1);

        Assert.Equal(["NAHGABE.WAV"], device.Silenced);
        Assert.Equal(0, audio.Chorus);
        Assert.Equal(1, device.Playing);
    }

    [Fact]
    public void Leaving_the_room_silences_the_one_talking_over_as_well()
    {
        var device = new Recorder();
        SceneAudio audio = Audio(device);

        audio.Speak("GABE1", 1);
        audio.Speak("MOSE1", 1);
        audio.Hush();

        Assert.Equal(["NAHGABE.WAV", "NAHMOSE.WAV"], [.. device.Silenced.Order()]);
        Assert.Equal(0, audio.Chorus);
        Assert.Equal(0, device.Playing);
    }

    [Fact]
    public void A_finished_chorus_line_is_let_go_of()
    {
        var device = new Recorder();
        SceneAudio audio = Audio(device);

        audio.Speak("GABE1", 1);
        audio.Speak("MOSE1", 1);

        Assert.Equal(1, audio.Chorus);

        device.Silence(new AudioVoice(1));
        audio.Update(1.0 / 60);

        Assert.Equal(0, audio.Chorus);
    }
}
