using System.Numerics;
using GK3Reborn.Content;
using GK3Reborn.Formats.Animation;
using GK3Reborn.Formats.Scenes;
using GK3Reborn.Foundation.Diagnostics;
using GK3Reborn.Game;
using GK3Reborn.Game.Actors;
using GK3Reborn.Rendering;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Tests for the noises an animation makes.
/// </summary>
/// <remarks>
/// An <c>.ANM</c>'s <c>[SOUNDS]</c> section is how most of the game makes any sound at all:
/// a door, a match, a yawn. Only dialogue was ever played, so everything else was silent —
/// and silence is indistinguishable from a sound file that is merely missing, which is why
/// this is checked rather than listened for.
/// </remarks>
public sealed class AnimationSoundTests
{
    /// <summary>A room with one animation in it, and a note of what it played.</summary>
    private static (SceneUpdate World, List<(string Name, Vector3? At, float Gain)> Heard) World(
        string animation, string body)
    {
        var scene = new LoadedScene(
            "TEST",
            new SceneDefinition(SceneInitFile.Parse(
                "[ROOM_CAMERAS]\nA, angle={0,0}, pos={0,0,0}, Default", "T.SIF")),
            Asset: null,
            Lightmaps: null,
            ModelsPlaced: 0,
            Placed: []);

        var heard = new List<(string, Vector3?, float)>();

        var update = new SceneUpdate(
            scene, new Gk3SheepApi(new GameState()), new Glances(), new HeadlessSceneSink())
        {
            Animations = new AnimationLibrary(n =>
                n.Equals($"{animation}.ANM", StringComparison.OrdinalIgnoreCase) ? body : null),

            Clips = new ClipLibrary(_ => null),
        };

        update.Sound = (cue, at) =>
        {
            heard.Add((cue.Name, at, cue.Gain));
            return true;
        };

        return (update, heard);
    }

    [Fact]
    public void A_sound_is_read_with_the_model_it_comes_from()
    {
        AnimationFile file = AnimationFile.Parse(
            "[HEADER]\n51\n\n[SOUNDS]\n1\n1,GabYawn1.wav,100,gab\n",
            "GABYAWN.ANM",
            new DiagnosticBag());

        AnimationSound cue = Assert.Single(file.Sounds);

        Assert.Equal(1, cue.Frame);
        Assert.Equal("GabYawn1.wav", cue.Name);
        Assert.Equal(100, cue.Volume);

        // The fourth field, which was being dropped. Without it Gabriel's yawn comes from
        // nowhere in particular rather than from Gabriel.
        Assert.Equal("gab", cue.Model);
        Assert.Equal(1f, cue.Gain, 3);
    }

    [Fact]
    public void An_animation_that_moves_nothing_still_makes_its_noise()
    {
        // No [ACTIONS] at all: a door closing somewhere, a match struck. Nothing about it
        // is visible and it is a third of the sound in the game.
        (SceneUpdate world, var heard) = World(
            "DOORSHUT", "[HEADER]\n15\n\n[SOUNDS]\n1\n0,DoorShut.WAV,100\n");

        // The animation is a second long even though it poses nothing, because a script
        // that waits on it is waiting for the sound.
        Assert.Equal(1.0, world.Play("DOORSHUT"), 2);

        world.Advance(0.1);

        (string name, Vector3? at, float gain) = Assert.Single(heard);

        Assert.Equal("DoorShut.WAV", name);
        Assert.Null(at);
        Assert.Equal(1f, gain, 3);
    }

    [Fact]
    public void A_sound_waits_for_the_frame_it_is_written_on()
    {
        // Frame 30 of a 45-frame animation: two seconds in, at fifteen frames a second.
        (SceneUpdate world, var heard) = World(
            "LATER", "[HEADER]\n45\n\n[SOUNDS]\n1\n30,Late.WAV,50\n");

        world.Play("LATER");

        world.Advance(1.0);
        Assert.Empty(heard);

        world.Advance(0.9);
        Assert.Empty(heard);

        world.Advance(0.2);

        // And its volume is the file's, as a gain rather than a percentage: a cue written
        // at 50 is half as loud, not silent and not full.
        Assert.Equal(0.5f, Assert.Single(heard).Gain, 3);
    }

    [Fact]
    public void A_sound_is_played_once_and_not_every_frame_after_it()
    {
        (SceneUpdate world, var heard) = World(
            "ONCE", "[HEADER]\n30\n\n[SOUNDS]\n1\n0,Once.WAV,100\n");

        world.Play("ONCE");

        for (int i = 0; i < 60; i++)
        {
            world.Advance(1 / 60.0);
        }

        Assert.Single(heard);
    }

    [Fact]
    public void A_looping_animation_makes_its_noise_every_time_round()
    {
        // A fan, a clock, a dripping tap: the sound belongs to the loop and not to the
        // moment the script started it.
        (SceneUpdate world, var heard) = World(
            "TICK", "[HEADER]\n15\n\n[SOUNDS]\n1\n0,Tick.WAV,100\n");

        world.Play("TICK", repeat: true);

        for (int i = 0; i < 30; i++)
        {
            world.Advance(0.1);
        }

        // Three seconds of a one-second loop.
        Assert.Equal(3, heard.Count);
    }

    [Fact]
    public void A_sound_the_archives_have_not_got_is_reported_rather_than_thrown()
    {
        (SceneUpdate world, _) = World(
            "MISSING", "[HEADER]\n15\n\n[SOUNDS]\n1\n0,Gone.WAV,100\n");

        world.Sound = (_, _) => false;

        world.Play("MISSING");
        world.Advance(0.2);

        Assert.Contains(world.Diagnostics.Items, d => d.Code == "GK3R3316");
    }

    [Fact]
    public void No_device_is_not_an_error()
    {
        (SceneUpdate world, _) = World(
            "QUIET", "[HEADER]\n15\n\n[SOUNDS]\n1\n0,Quiet.WAV,100\n");

        world.Sound = null;

        world.Play("QUIET");
        world.Advance(0.2);

        // Nothing said, because nothing is wrong: the game runs silent on a machine with
        // no sound device and that is not a fault in the animation.
        Assert.DoesNotContain(world.Diagnostics.Items, d => d.Code == "GK3R3316");
    }
}
