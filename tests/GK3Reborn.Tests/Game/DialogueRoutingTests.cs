using System.Numerics;
using GK3Reborn.Audio;
using GK3Reborn.Content;
using GK3Reborn.Formats.Audio;
using GK3Reborn.Game;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Tests for where a spoken line comes from.
/// </summary>
/// <remarks>
/// Gabriel is centred because the player is him, and everybody else is where they are
/// standing. The policy is older than anything that read it: <c>DialogueRoutingOptions</c>
/// has been in the audio layer since it was written, and every line in the game came out of
/// the middle regardless — the person beside you and the person across the courtyard alike.
/// </remarks>
public sealed class DialogueRoutingTests
{
    /// <summary>A device that remembers which bus each line went to, and where.</summary>
    private sealed class Recorder : IAudioBackend
    {
        private int _next;

        public SpeakerLayout RequestedLayout => SpeakerLayout.Stereo;

        public SpeakerLayout ActualLayout => SpeakerLayout.Stereo;

        public int Playing => Started.Count;

        public List<(string Name, AudioBus Bus, AudioPlacement? At)> Started { get; } = [];

        public AudioVoice Play(
            WavFile sound, AudioBus bus, bool repeat = false, AudioPlacement? at = null)
        {
            Started.Add((sound.Name, bus, at));
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
        }

        public void StopBus(AudioBus bus)
        {
        }

        public bool IsPlaying(AudioVoice voice) => true;

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

    /// <summary>An audio layer with one line in it, spoken by whoever is named.</summary>
    private static SceneAudio Audio(Recorder device, string speaker)
    {
        byte[] wav = Wav();

        var sounds = new SoundLibrary(
            name => name.StartsWith("LINE", StringComparison.OrdinalIgnoreCase) ? wav : null,
            name => name.StartsWith("LINE", StringComparison.OrdinalIgnoreCase));

        var animations = new AnimationLibrary(name =>
            name.Contains("YAK", StringComparison.OrdinalIgnoreCase)
                ? "[HEADER]\n30\n\n[SOUNDS]\n1\n0,LINE.WAV,100\n\n[GK3]\n2\n" +
                  $"0,SPEAKER,{speaker}\n0,CAPTION,Something is said\n"
                : null);

        return new SceneAudio(sounds, animations, device);
    }

    [Fact]
    public void GabrielComesFromTheMiddleWhereverHeIsStanding()
    {
        // The player is him. A voice that swings across the field every time the camera
        // cuts is the one voice that must not.
        var device = new Recorder();
        SceneAudio audio = Audio(device, "GABRIEL");
        audio.Where = _ => new Vector3(500, 60, 500);

        audio.Speak("YAK", 1);

        Assert.Single(device.Started);
        Assert.Equal(AudioBus.DialogueCentered, device.Started[0].Bus);
        Assert.Null(device.Started[0].At);
    }

    [Fact]
    public void EverybodyElseComesFromWhereTheyAreStanding()
    {
        var device = new Recorder();
        SceneAudio audio = Audio(device, "GRACE");
        audio.Where = named => named == "GRACE" ? new Vector3(120, 60, -40) : null;

        audio.Speak("YAK", 1);

        Assert.Single(device.Started);
        Assert.Equal(AudioBus.DialogueInWorld, device.Started[0].Bus);
        Assert.Equal(new Vector3(120, 60, -40), device.Started[0].At!.Value.Position);
    }

    [Fact]
    public void ASpeakerTheRoomCannotFindIsCentredRatherThanDropped()
    {
        // An unattributed line, or somebody who is not in this room: a line nobody hears
        // is a worse answer than a line in the middle.
        var device = new Recorder();
        SceneAudio audio = Audio(device, "SOMEBODY_ELSEWHERE");
        audio.Where = _ => null;

        audio.Speak("YAK", 1);

        Assert.Single(device.Started);
        Assert.Equal(AudioBus.DialogueCentered, device.Started[0].Bus);
    }

    [Fact]
    public void TheAccessibilityOptionCentresEverybody()
    {
        var device = new Recorder();
        SceneAudio audio = Audio(device, "GRACE");
        audio.Where = _ => new Vector3(120, 60, -40);
        audio.Routing = new DialogueRoutingOptions { CenterAllDialogue = true };

        audio.Speak("YAK", 1);

        Assert.Single(device.Started);
        Assert.Equal(AudioBus.DialogueCentered, device.Started[0].Bus);
    }
}
