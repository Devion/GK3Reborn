using System.Numerics;
using GK3Reborn.Audio;
using GK3Reborn.Formats.Audio;
using GK3Reborn.Game;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Tests for which sounds belong in the room and which belong at the listener.
/// </summary>
/// <remarks>
/// A soundtrack either gives its sound a place or does not, and the difference is the whole
/// of whether a fountain sounds like it is across the square. Room tone has no place because
/// it comes from everywhere; a fountain has one because it does not.
/// </remarks>
public sealed class PositionedSoundTests
{
    [Fact]
    public void A_sound_with_a_place_keeps_the_distances_it_was_given()
    {
        // RC1's fountain, as its soundtrack writes it.
        var fountain = new SoundtrackSound
        {
            Name = "CSEFountain.wav",
            Is3D = true,
            MinDistance = 100f,
            MaxDistance = 1200f,
            Position = new Vector3(3113, 114, -2337),
        };

        AudioPlacement at = SceneAudio.PlacementOf(fountain)!.Value;

        Assert.Equal(new Vector3(3113, 114, -2337), at.Position);
        Assert.Equal(100f, at.Minimum);
        Assert.Equal(1200f, at.Maximum);
    }

    [Fact]
    public void A_sound_with_no_place_belongs_at_the_listener()
    {
        // The same fountain has a two-dimensional soundtrack as well, for the room it is
        // heard in rather than seen in. Room tone comes from everywhere and following the
        // player about is what it is for.
        var tone = new SoundtrackSound { Name = "CSEFountain.wav", Is3D = false };

        Assert.Null(SceneAudio.PlacementOf(tone));
    }

    [Fact]
    public void A_placed_sound_that_names_no_distances_gets_the_game_s_own()
    {
        // 200 and 2000 units, out of the original's audio configuration.
        var somewhere = new SoundtrackSound
        {
            Name = "x.wav",
            Is3D = true,
            Position = new Vector3(1, 2, 3),
        };

        AudioPlacement at = SceneAudio.PlacementOf(somewhere)!.Value;

        Assert.Equal(AudioPlacement.DefaultMinimum, at.Minimum);
        Assert.Equal(AudioPlacement.DefaultMaximum, at.Maximum);
    }

    [Fact]
    public void The_rolloff_is_the_inverse_clamped_one_the_original_uses()
    {
        // Full volume within the minimum, the reciprocal of distance after it, and level
        // again past the maximum. Written out because it is what the device is asked for
        // and what the numbers in the .STK files mean.
        var at = new AudioPlacement(Vector3.Zero, 100f, 1200f);

        static float Gain(AudioPlacement at, float away)
        {
            float d = Math.Clamp(away, at.Minimum, at.Maximum);
            return at.Minimum / (at.Minimum + (d - at.Minimum));
        }

        Assert.Equal(1f, Gain(at, 0f), 3);
        Assert.Equal(1f, Gain(at, 100f), 3);
        Assert.Equal(0.5f, Gain(at, 200f), 3);
        Assert.Equal(0.25f, Gain(at, 400f), 3);

        // And it stops falling, rather than tending to silence for ever.
        Assert.Equal(Gain(at, 1200f), Gain(at, 5000f), 5);
    }
}
