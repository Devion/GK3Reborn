using System.Numerics;
using GK3Reborn.Audio;
using GK3Reborn.Content;
using GK3Reborn.Formats.Audio;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Tests for a line of dialogue whose recording is not there and whose words are.
/// </summary>
/// <remarks>
/// <para>
/// A <c>.YAK</c> is the line: its <c>[SOUNDS]</c> names the recording and its <c>[GK3]</c>
/// carries the caption. When the developers cut a puzzle they deleted the recordings and
/// left the YAKs, so eighteen of the crow's-nest puzzle's nineteen lines are a caption with
/// no sound behind it, and the shipped game has a few of its own.
/// </para>
/// <para>
/// Those were dropped on the floor: the caption was read off the animation and cleared in
/// the same call, while the waited <c>StartVoiceOver</c> that asked for the line went on
/// spending its three seconds. Reported as looking at the crow's nest doing nothing at all.
/// </para>
/// </remarks>
public sealed class SilentLineTests
{
    /// <summary>A device that plays nothing and would say so if it were asked to.</summary>
    private sealed class Recorder : GK3Reborn.Audio.IAudioBackend
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

    /// <summary>
    /// An audio layer whose whole library is lines with a caption and no <c>[SOUNDS]</c>.
    /// </summary>
    /// <param name="device">Where a sound would go, if one of these had any.</param>
    /// <param name="frames">How long the animation is, at fifteen frames a second.</param>
    /// <param name="caption">The line's words, or empty for a YAK that says nothing.</param>
    private static GK3Reborn.Game.SceneAudio Audio(
        Recorder device, int frames = 45, string caption = "It's a black rug.")
    {
        // Nothing is on disc: a line that names no sound never asks for one, and one that
        // did would find nothing here either.
        var sounds = new SoundLibrary(_ => null, _ => false);

        string words = caption.Length > 0
            ? $"[GK3]\n3\n0,SPEAKER,UNKNOWN\n0,CAPTION,{caption}\n{frames - 3},DIALOGUECUE\n"
            : $"[GK3]\n1\n{frames - 3},DIALOGUECUE\n";

        var animations = new AnimationLibrary(name =>
            name.StartsWith("YAK", StringComparison.OrdinalIgnoreCase)
                ? $"[HEADER]\n{frames}\n\n{words}"
                : null);

        return new GK3Reborn.Game.SceneAudio(sounds, animations, device);
    }

    /// <summary>
    /// A line that was never recorded is still read out, for as long as it was cut to.
    /// </summary>
    [Fact]
    public void A_line_with_no_recording_still_shows_its_caption()
    {
        var device = new Recorder();
        GK3Reborn.Game.SceneAudio audio = Audio(device);

        audio.Speak("YAK1", 1);

        Assert.True(audio.Talking);
        Assert.Equal("It's a black rug.", audio.Caption);
        Assert.Empty(device.Started);

        // Forty-five frames at fifteen a second is three seconds, and it stands for all of
        // them rather than for the one frame it used to.
        audio.Update(1.0);
        Assert.Equal("It's a black rug.", audio.Caption);

        audio.Update(1.5);
        Assert.Equal("It's a black rug.", audio.Caption);

        audio.Update(0.6);
        Assert.Null(audio.Caption);
        Assert.False(audio.Talking);
    }

    /// <summary>And the run behind it goes on, one line at a time.</summary>
    [Fact]
    public void The_lines_behind_it_wait_their_turn()
    {
        var device = new Recorder();
        GK3Reborn.Game.SceneAudio audio = Audio(device);

        Assert.Equal(2, audio.Speak("YAK1", 2));

        Assert.True(audio.Talking);
        Assert.Equal(1, audio.Queued);

        audio.Update(3.1);

        // The second one is up, rather than both having gone past in the same frame.
        Assert.True(audio.Talking);
        Assert.Equal(0, audio.Queued);
        Assert.Equal("It's a black rug.", audio.Caption);

        audio.Update(3.1);
        Assert.False(audio.Talking);
    }

    /// <summary>A player who has read it can tap through it, as they can any line.</summary>
    [Fact]
    public void A_silent_line_can_be_tapped_through()
    {
        var device = new Recorder();
        GK3Reborn.Game.SceneAudio audio = Audio(device);

        audio.Speak("YAK1", 1);
        audio.Update(0.2);

        Assert.True(audio.Skip());
        Assert.False(audio.Talking);
        Assert.Null(audio.Caption);

        // And it stays gone: the hold does not run on underneath and clear the next line.
        audio.Update(3.0);
        Assert.False(audio.Talking);
    }

    /// <summary>
    /// A line with nothing to hear and nothing to read is still skipped outright.
    /// </summary>
    /// <remarks>
    /// Which is the case the old behaviour was written for: an animation with no audio and
    /// no words has nothing to hold the queue up for.
    /// </remarks>
    [Fact]
    public void A_line_with_no_words_either_is_still_skipped()
    {
        var device = new Recorder();
        GK3Reborn.Game.SceneAudio audio = Audio(device, caption: string.Empty);

        audio.Speak("YAK1", 1);

        Assert.False(audio.Talking);
        Assert.Null(audio.Caption);
    }
}
