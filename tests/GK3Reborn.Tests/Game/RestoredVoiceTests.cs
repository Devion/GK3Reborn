using System.Numerics;
using GK3Reborn.Audio;
using GK3Reborn.Content;
using GK3Reborn.Formats.Audio;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Tests for the recording a licence plate implies when the YAK names none.
/// </summary>
/// <remarks>
/// <para>
/// A YAK called <c>E1395D0LCW1</c> carries <c>A1395D0L.CW1</c> — first seven, a stop, last
/// three — and 6,606 of the corpus's YAKs name exactly that. The ones whose recording was
/// deleted name nothing at all, so a line restored by putting audio back under the 1999
/// name had nobody asking for it: fourteen spoken lines sat in the pack and never played.
/// </para>
/// <para>
/// The derived name is only ever reached where the YAK names none, which is what keeps it
/// honest. 683 YAKs deliberately point at a different recording — a line said twice and
/// recorded once — and those must go on doing so.
/// </para>
/// </remarks>
public sealed class RestoredVoiceTests
{
    /// <summary>A device that plays nothing and remembers what it was handed.</summary>
    private sealed class Recorder : IAudioBackend
    {
        private int _next;

        public SpeakerLayout RequestedLayout => SpeakerLayout.Stereo;

        public SpeakerLayout ActualLayout => SpeakerLayout.Stereo;

        public int Playing => Started.Count;

        public List<string> Started { get; } = [];

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
    private static byte[] Wave()
    {
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
        body.AddRange(BitConverter.GetBytes(64));
        body.AddRange(new byte[64]);

        var file = new List<byte>();
        file.AddRange("RIFF"u8.ToArray());
        file.AddRange(BitConverter.GetBytes(4 + body.Count));
        file.AddRange("WAVE"u8.ToArray());
        file.AddRange(body);

        return [.. file];
    }

    /// <summary>
    /// An audio layer holding one line and one recording, each named as asked.
    /// </summary>
    /// <param name="device">The device.</param>
    /// <param name="held">The one asset name the sound library has, or none.</param>
    /// <param name="sounds">A <c>[SOUNDS]</c> section for the YAK, or empty for none.</param>
    private static GK3Reborn.Game.SceneAudio Audio(
        Recorder device, string? held, string sounds = "")
    {
        byte[] wave = Wave();

        var library = new SoundLibrary(
            name => string.Equals(name, held, StringComparison.OrdinalIgnoreCase)
                ? wave
                : null,
            name => string.Equals(name, held, StringComparison.OrdinalIgnoreCase));

        var animations = new AnimationLibrary(_ =>
            "[HEADER]\n45\n\n" + sounds +
            "[GK3]\n3\n0,SPEAKER,GABRIEL\n0,CAPTION,I ought to be able to use that hose.\n" +
            "42,DIALOGUECUE\n");

        return new GK3Reborn.Game.SceneAudio(library, animations, device);
    }

    /// <summary>
    /// A line whose YAK names no sound is spoken by the recording its plate implies.
    /// </summary>
    [Fact]
    public void A_plate_finds_the_recording_named_after_it()
    {
        var device = new Recorder();
        GK3Reborn.Game.SceneAudio audio = Audio(device, held: "A1395D0L.CW1");

        audio.Speak("1395D0LCW1", 1);

        Assert.Equal(["A1395D0L.CW1"], device.Started);
        Assert.True(audio.Talking);
        Assert.Equal("I ought to be able to use that hose.", audio.Caption);

        // Held by the device now, not by the caption timer: it does not end on its own
        // three seconds later the way a line with no recording does.
        audio.Update(5.0);
        Assert.True(audio.Talking);
    }

    /// <summary>And a stated recording is never displaced by the implied one.</summary>
    /// <remarks>
    /// <c>E01LIQ44QR1</c> plays <c>A01LED44.QR1</c> in the shipped game, which is a line
    /// written twice and recorded once. 683 YAKs do something of the sort.
    /// </remarks>
    [Fact]
    public void A_yak_that_names_its_own_recording_keeps_it()
    {
        var device = new Recorder();

        GK3Reborn.Game.SceneAudio audio = Audio(
            device, held: "A01LED44.QR1", sounds: "[SOUNDS]\n1\n0,A01LED44.QR1,100\n\n");

        audio.Speak("01LIQ44QR1", 1);

        Assert.Equal(["A01LED44.QR1"], device.Started);
    }

    /// <summary>
    /// A plate with no recording anywhere still falls back to its caption.
    /// </summary>
    [Fact]
    public void A_plate_with_nothing_behind_it_is_still_read_out()
    {
        var device = new Recorder();
        GK3Reborn.Game.SceneAudio audio = Audio(device, held: null);

        audio.Speak("1395D0LCW1", 1);

        Assert.Empty(device.Started);
        Assert.True(audio.Talking);
        Assert.Equal("I ought to be able to use that hose.", audio.Caption);

        // Forty-five frames at fifteen a second, and then it is over.
        audio.Update(3.1);
        Assert.False(audio.Talking);
    }

    /// <summary>A name that is not a licence plate implies nothing.</summary>
    /// <remarks>
    /// <c>StartYak</c> names an animation outright — <c>CIRCUSEMILIO</c> — and reaches the
    /// same queue. Deriving an asset from one of those would ask for a file that cannot
    /// exist, so the length is checked rather than assumed.
    /// </remarks>
    [Fact]
    public void A_name_that_is_not_a_plate_implies_no_recording()
    {
        var device = new Recorder();
        GK3Reborn.Game.SceneAudio audio = Audio(device, held: "ACIRCUSE.MIL");

        audio.Speak("CIRCUSEMILIO", 1);

        Assert.Empty(device.Started);
    }
}
