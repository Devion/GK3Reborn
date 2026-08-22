using System.Numerics;
using GK3Reborn.Audio;
using GK3Reborn.Rendering;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Tests that a sound on your left is heard on your left.
/// </summary>
/// <remarks>
/// <para>
/// GK3's world is left-handed and OpenAL's is right-handed, so world coordinates handed
/// straight to the device put every sound in the game on the wrong side of the player. It
/// is a defect nobody can see, only hear, and one that sounds like a stereo cable in
/// backwards rather than like a bug — the fountain is still a fountain, and still gets
/// louder as you walk towards it.
/// </para>
/// <para>
/// So these check the whole chain against OpenAL's own rule for where a listener's right
/// ear points, rather than against the conversion agreeing with itself.
/// </para>
/// </remarks>
public sealed class AudioSpaceTests
{
    /// <summary>Where the camera looks in GK3's world, and which way is up there.</summary>
    private static readonly Vector3 Up = Vector3.UnitY;

    [Fact]
    public void The_worlds_disagree_about_which_way_is_right()
    {
        // Facing along positive Z. The renderer works its right out as cross(up, forward),
        // which is the left-handed order; the device uses the other one.
        Vector3 forward = Vector3.UnitZ;

        Vector3 game = Vector3.Cross(Up, forward);
        Vector3 device = AudioSpace.RightOfListener(forward, Up);

        Assert.Equal(Vector3.UnitX, game);
        Assert.Equal(-Vector3.UnitX, device);

        // Which is the whole defect: pass the world over unchanged and the device's idea
        // of right is the game's idea of left.
        Assert.Equal(-game, device);
    }

    [Theory]

    // Facing each way along both horizontal axes, with the sound on the listener's left.
    [InlineData(0f, 0f, 1f, -1f, 0f, 0f)]
    [InlineData(0f, 0f, -1f, 1f, 0f, 0f)]
    [InlineData(1f, 0f, 0f, 0f, 0f, 1f)]
    [InlineData(-1f, 0f, 0f, 0f, 0f, -1f)]
    public void A_sound_on_the_left_is_heard_on_the_left(
        float fx, float fy, float fz, float sx, float sy, float sz)
    {
        var forward = new Vector3(fx, fy, fz);
        var source = new Vector3(sx, sy, sz);

        // The left of a left-handed world: the opposite of cross(up, forward), which is
        // what the camera and the picker both use.
        Assert.Equal(-Vector3.Cross(Up, forward), Vector3.Normalize(source));

        Assert.True(
            AudioSpace.Panning(Vector3.Zero, forward, Up, source) < -0.9f,
            "a sound on the listener's left was not heard on the left");

        // And the mirror of it on the right, so this cannot pass by putting everything on
        // one side.
        Assert.True(
            AudioSpace.Panning(Vector3.Zero, forward, Up, -source) > 0.9f,
            "a sound on the listener's right was not heard on the right");
    }

    [Fact]
    public void Straight_ahead_is_neither_side()
    {
        Assert.Equal(0f, AudioSpace.Panning(Vector3.Zero, Vector3.UnitZ, Up, Vector3.UnitZ), 3);
        Assert.Equal(0f, AudioSpace.Panning(Vector3.Zero, Vector3.UnitZ, Up, -Vector3.UnitZ), 3);

        // Above and below are neither side either, which is what says the up vector went
        // over with the same mirror as everything else.
        Assert.Equal(0f, AudioSpace.Panning(Vector3.Zero, Vector3.UnitZ, Up, Vector3.UnitY), 3);
    }

    [Fact]
    public void Turning_round_swaps_the_sides()
    {
        var fountain = new Vector3(-300, 0, 0);

        float facing = AudioSpace.Panning(Vector3.Zero, Vector3.UnitZ, Up, fountain);
        float away = AudioSpace.Panning(Vector3.Zero, -Vector3.UnitZ, Up, fountain);

        Assert.True(facing < 0, "it was not on the left to begin with");
        Assert.Equal(-facing, away, 3);
    }

    [Fact]
    public void Walking_past_a_sound_moves_it_across_the_head()
    {
        // A fountain ahead and to the left, walked past: it should swing round to the
        // right rather than jump.
        var fountain = new Vector3(-100, 0, 300);

        float before = AudioSpace.Panning(Vector3.Zero, Vector3.UnitZ, Up, fountain);
        float beside = AudioSpace.Panning(new Vector3(0, 0, 300), Vector3.UnitZ, Up, fountain);
        float after = AudioSpace.Panning(new Vector3(0, 0, 600), Vector3.UnitZ, Up, fountain);

        Assert.True(before < 0, $"it began on the right ({before:F2})");
        Assert.True(beside < before, "it did not move further left as the listener drew level");
        Assert.True(after < 0, "it ended up on the wrong side");

        // Level with it, it is directly to the left and nowhere else.
        Assert.Equal(-1f, beside, 2);
    }

    [Fact]
    public void The_mirror_is_its_own_inverse_and_keeps_distances()
    {
        var point = new Vector3(3, -5, 7);

        Assert.Equal(point, AudioSpace.Device(AudioSpace.Device(point)));

        // A mirror is a rigid motion: what is measured in world coordinates and what is
        // measured in the device's agree about how far apart things are, which is why the
        // muffling and the rolloff need no conversion at all.
        var other = new Vector3(-11, 2, 40);

        Assert.Equal(
            Vector3.Distance(point, other),
            Vector3.Distance(AudioSpace.Device(point), AudioSpace.Device(other)),
            3);
    }

    [Fact]
    public void The_camera_and_the_ears_agree_about_which_way_is_right()
    {
        // The listener is handed the camera's own vectors, so this is the real chain: a
        // point the renderer puts on the left of the screen has to be heard on the left.
        var camera = new Camera
        {
            Position = new Vector3(100, 50, 0),
            Target = new Vector3(100, 50, 400),
            Up = Up,
        };

        Vector3 forward = Vector3.Normalize(camera.Target - camera.Position);
        Vector3 right = Vector3.Normalize(Vector3.Cross(camera.Up, forward));

        Vector3 onTheRight = camera.Position + (right * 200);
        Vector3 onTheLeft = camera.Position - (right * 200);

        Assert.True(
            AudioSpace.Panning(camera.Position, forward, camera.Up, onTheLeft) < -0.9f,
            "what the camera shows on the left is heard on the right");

        Assert.True(
            AudioSpace.Panning(camera.Position, forward, camera.Up, onTheRight) > 0.9f,
            "what the camera shows on the right is heard on the left");
    }
}
