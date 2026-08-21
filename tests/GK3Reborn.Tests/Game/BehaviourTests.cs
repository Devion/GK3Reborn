using System.Numerics;
using System.Text;
using GK3Reborn.Content;
using GK3Reborn.Formats.Animation;
using GK3Reborn.Formats.Models;
using GK3Reborn.Formats.Scenes;
using GK3Reborn.Game;
using GK3Reborn.Game.Actors;
using GK3Reborn.Rendering;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Tests for running behaviour scripts: scenery, and what a character does when nobody is
/// telling them to do anything.
/// </summary>
/// <remarks>
/// Everything here is observed through <em>which animation was asked for</em>, because that
/// is the only thing a behaviour script actually produces. The animation library is a
/// function, so a test can record every name the player asks it for and read the script's
/// decisions off that.
/// </remarks>
public sealed class BehaviourTests
{
    /// <summary>
    /// Every animation the player has started, in order.
    /// </summary>
    /// <remarks>
    /// Read out of the diagnostics rather than out of the animation library, and the
    /// difference matters: the library caches, so a script that plays the same fidget forty
    /// times reads it once. Every play of an animation that names no clip reports itself,
    /// which for these scripts is every play.
    /// </remarks>
    private static List<string> Played(SceneUpdate update) =>
        [.. update.Diagnostics.Items
            .Where(d => string.Equals(d.Code, "GK3R3313", StringComparison.Ordinal))
            .Select(d => d.File ?? string.Empty)];

    private static GasFile Script(string text) => GasFile.Parse(Encoding.Latin1.GetBytes(text));

    private static readonly string[] Choices =
        ["gabBreath1", "gabFidget1", "gabFidget2", "gabSway"];

    private static ModFile Person(string name) => ModFile.FromMeshes(
        name,
        [
            new ModMesh
            {
                MeshToLocal = Matrix4x4.Identity,
                BoundsMin = Vector3.Zero,
                BoundsMax = Vector3.One,
                Submeshes =
                [
                    new ModSubmesh
                    {
                        TextureName = name + "_FACE",
                        Color = (255, 255, 255),
                        Positions = [Vector3.Zero],
                        Normals = [Vector3.UnitY],
                        TexCoords = [Vector2.Zero],
                        Indices = [0, 0, 0],
                    },
                ],
            },
        ]);

    private static PlacedModel Actor(
        string name, string noun, int placement,
        string? idle = null, string? talk = null, string? listen = null) =>
        new(name, noun, null, Person(name), Matrix4x4.Identity,
            PlacedModelKind.Actor, new ModelPlacement(placement))
        {
            Idle = idle is null ? null : Script(idle),
            Talk = talk is null ? null : Script(talk),
            Listen = listen is null ? null : Script(listen),
        };

    private static SceneUpdate World(params PlacedModel[] models)
    {
        var scene = new LoadedScene(
            "TEST",
            new SceneDefinition(SceneInitFile.Parse(
                "[ROOM_CAMERAS]\nA, angle={0,0}, pos={0,0,0}, Default\n", "T.SIF")),
            Asset: null,
            Lightmaps: null,
            ModelsPlaced: models.Length,
            Placed: models);

        var update = new SceneUpdate(
            scene, new Gk3SheepApi(new GameState()), new Glances(), new HeadlessSceneSink())
        {
            // A frame count and nothing else. An animation that names no clip takes no
            // time and reports itself, which is exactly what a test of a script's
            // decisions wants.
            Animations = new AnimationLibrary(_ => "[HEADER]\n30\n"),

            // The player wants both libraries before it will play anything.
            Clips = new ClipLibrary(_ => null),
        };

        update.StartScenery();
        return update;
    }

    [Fact]
    public void A_prop_with_a_script_of_its_own_runs_it()
    {
        SceneUpdate update = World(
            new PlacedModel(
                "fanblades", null, null, Person("fan"), Matrix4x4.Identity,
                PlacedModelKind.Prop, new ModelPlacement(0))
            {
                Idle = Script("ANIM lbyfan_spin\nloop\n"),
            });

        Assert.Equal(1, update.Scenic);
        Assert.Equal(0, update.Fidgeting);

        update.Advance(0.1);

        Assert.Equal("lbyfan_spin", Assert.Single(Played(update)));
    }

    [Fact]
    public void A_run_of_choices_plays_exactly_one_of_them()
    {
        // The whole point of ONEOF, and the thing that makes an idle read as a person: a
        // run of four is one decision, not four animations in a row.
        SceneUpdate update = World(
            Actor("gab", "GABRIEL", 0, idle:
                """
                ONEOF gabBreath1
                ONEOF gabFidget1
                ONEOF gabFidget2
                ONEOF gabSway
                WAIT 10
                LOOP
                """));

        Assert.Equal(1, update.Fidgeting);

        update.Advance(0.1);

        Assert.Contains(Assert.Single(Played(update)), Choices);
    }

    [Fact]
    public void Both_choices_come_up_when_they_are_weighted_the_same()
    {
        // One world stepped many times, not many worlds stepped once: the generator is
        // fixed on purpose, so a fresh one always draws the same first number and sixty of
        // them would agree with each other rather than say anything.
        SceneUpdate update = World(
            Actor("gab", "GABRIEL", 0, idle:
                """
                LABEL TOP
                ONEOF gabBreath1, 100
                ONEOF gabFidget1, 100
                WAIT 5
                GOTO TOP
                """));

        for (int i = 0; i < 60; i++)
        {
            update.Advance(5.1);
        }

        Assert.Contains("gabBreath1", Played(update));
        Assert.Contains("gabFidget1", Played(update));
    }

    [Fact]
    public void A_script_counts_and_branches()
    {
        // BIGIDLE.GAS counts its wipes and does a longer one every other time.
        SceneUpdate update = World(
            Actor("big", "BIG", 0, idle:
                """
                SET X, 0
                LABEL START
                ANIM wipeA
                INC X
                IF X , = , 2 , TWICE
                WAIT 10
                GOTO START
                LABEL TWICE
                ANIM wipeLong
                SET X, 0
                WAIT 10
                GOTO START
                """));

        // Stepped the way the game steps it, small amounts at a time, for long enough to
        // come round several times.
        for (int i = 0; i < 4000; i++)
        {
            update.Advance(1.0 / 60);
        }

        Assert.Contains("wipeA", Played(update));
        Assert.Contains("wipeLong", Played(update));

        // The long wipe happens every other time round, not every time.
        Assert.True(
            Played(update).Count(n => n == "wipeA") > Played(update).Count(n => n == "wipeLong"),
            $"expected fewer long wipes than short ones, got {string.Join(", ", Played(update))}");
    }

    [Fact]
    public void Who_is_speaking_decides_which_of_the_three_scripts_runs()
    {
        // The half of talking that lip sync does not cover. A scene names all three per
        // actor and the speaker decides which is which.
        SceneUpdate update = World(
            Actor("gab", "GABRIEL", 0,
                idle: "ANIM gabBreath\nWAIT 10\nLOOP\n",
                talk: "ANIM gabTalk\nWAIT 10\nLOOP\n",
                listen: "ANIM gabListen\nWAIT 10\nLOOP\n"));

        string? speaker = null;
        update.Speaking = () => speaker;

        update.Advance(0.1);
        Assert.Equal("gabBreath", Played(update)[^1]);

        speaker = "gab";
        update.Advance(0.1);
        Assert.Equal("gabTalk", Played(update)[^1]);

        speaker = "jea";
        update.Advance(0.1);
        Assert.Equal("gabListen", Played(update)[^1]);

        speaker = null;
        update.Advance(0.1);

        Assert.Equal(
            "gabBreath",
            Played(update)[^1]);
    }

    [Fact]
    public void A_speaker_is_recognised_by_either_of_their_names()
    {
        // A scene places gab and calls him GABRIEL, and which one a caller uses is not
        // something to depend on.
        SceneUpdate update = World(
            Actor("gab", "GABRIEL", 0,
                idle: "ANIM gabBreath\nWAIT 10\nLOOP\n",
                talk: "ANIM gabTalk\nWAIT 10\nLOOP\n"));

        update.Speaking = () => "GABRIEL";
        update.Advance(0.1);

        Assert.Equal("gabTalk", Played(update)[^1]);
    }

    [Fact]
    public void A_character_with_no_script_for_a_mode_falls_back_to_their_idle()
    {
        // Most of the cast have an idle and nothing else. Standing perfectly still while
        // speaking would be worse than gesturing the way they do when they wait.
        SceneUpdate update = World(
            Actor("jea", "JEAN", 0, idle: "ANIM jeaBreath\nWAIT 10\nLOOP\n"));

        update.Speaking = () => "jea";
        update.Advance(0.1);

        Assert.Equal("jeaBreath", Assert.Single(Played(update)));
    }

    [Fact]
    public void A_character_told_to_stand_still_does()
    {
        SceneUpdate update = World(
            Actor("gab", "GABRIEL", 0, idle: "ANIM gabBreath\nWAIT 1\nLOOP\n"));

        update.StopFidget("GABRIEL");
        update.Advance(5.0);

        Assert.Empty(Played(update));

        // And starts again when told to.
        update.StartFidget("gab", FidgetKind.Idle);
        update.Advance(0.1);

        Assert.Equal("gabBreath", Assert.Single(Played(update)));
    }

    [Fact]
    public void A_fidget_asked_for_by_name_overrides_who_is_speaking()
    {
        SceneUpdate update = World(
            Actor("gab", "GABRIEL", 0,
                idle: "ANIM gabBreath\nWAIT 10\nLOOP\n",
                listen: "ANIM gabListen\nWAIT 10\nLOOP\n"));

        update.Speaking = () => "somebody_else";
        update.StartFidget("gab", FidgetKind.Idle);
        update.Advance(0.1);

        Assert.Equal("gabBreath", Played(update)[^1]);
    }

    [Fact]
    public void A_script_can_be_replaced_while_it_is_running()
    {
        // SetIdleGAS, and NEWIDLE from inside a script. Started from the top rather than
        // merely stored: a script that hands somebody a new idle means it to take effect.
        SceneUpdate update = World(
            Actor("mos", "MOSELY", 0, idle: "ANIM mosStand\nWAIT 10\nLOOP\n"));

        update.Advance(0.1);
        Assert.Equal("mosStand", Assert.Single(Played(update)));

        Assert.True(update.SetBehaviour(
            "MOSELY", FidgetKind.Idle, Script("ANIM mosPace\nWAIT 10\nLOOP\n")));

        update.Advance(0.1);
        Assert.Equal("mosPace", Played(update)[^1]);
    }

    [Fact]
    public void Replacing_a_script_for_somebody_who_is_not_here_says_so()
    {
        SceneUpdate update = World(Actor("gab", "GABRIEL", 0, idle: "ANIM a\nLOOP\n"));

        Assert.False(update.SetBehaviour("mosely", FidgetKind.Idle, Script("ANIM b\nLOOP\n")));
    }

    [Fact]
    public void A_script_that_never_waits_is_bounded_rather_than_spinning()
    {
        // The corpus has several. Without a bound, one of them takes the frame with it.
        SceneUpdate update = World(
            Actor("gab", "GABRIEL", 0, idle: "LABEL TOP\nSET X, 1\nGOTO TOP\n"));

        update.Advance(0.1);

        Assert.Empty(Played(update));
    }
}
