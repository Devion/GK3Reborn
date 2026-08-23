using System.Text;
using GK3Reborn.Formats.Animation;
using Xunit;

namespace GK3Reborn.Tests.Formats;

/// <summary>
/// Tests for the scripts that drive a room, and everybody in it, when nobody is asking.
/// </summary>
/// <remarks>
/// 502 scripts and 25 keywords. The one that matters most is the smallest: <c>ONEOF</c> is
/// 1,559 of the corpus's instructions, and a <em>run</em> of them is one choice rather than
/// several — which is the whole difference between an idle that reads as a person and one
/// that plays every fidget a character has, in order, for ever.
/// </remarks>
public sealed class GasFileTests
{
    private static GasFile Parse(string text) => GasFile.Parse(Encoding.Latin1.GetBytes(text));

    [Fact]
    public void An_animation_and_a_loop_is_a_thing_that_simply_turns()
    {
        // LBYFAN.GAS, and the shape of nearly every piece of scenery in the game: the
        // ceiling fans, the fountains, the fires, the flashing clock.
        GasFile fan = Parse("ANIM lbyfan_spin\nloop\n");

        Assert.True(fan.Complete);
        Assert.True(fan.Continuous);
        Assert.Equal(GasAction.Animate, fan.Steps[0].Action);
        Assert.Equal("lbyfan_spin", fan.Steps[0].Name);
    }

    [Fact]
    public void A_script_that_waits_between_animations_is_not_one()
    {
        // It has a rhythm of its own and has to be stepped by the script rather than
        // handed to the clip as a loop.
        GasFile blinking = Parse(
            "LABEL START\nANIM cs5light01\nWAIT 3\nANIM cs5light02\nGOTO START\n");

        Assert.False(blinking.Continuous);
    }

    [Fact]
    public void An_animation_with_choices_beside_it_is_not_a_thing_that_simply_turns()
    {
        // EMLIDLE.GAS: one ANIM and four ONEOFs. It has a single animation and no wait,
        // which used to be the whole test — and playing it as a looping clip would run
        // Emilio's first breath for ever and never reach the choices.
        GasFile idle = Parse(
            """
            ANIM emlFigBreath1
            ONEOF emlFigBreath1
            ONEOF emlFigTalk1, 10
            ONEOF emlFigSway, 10
            LOOP
            """);

        Assert.True(idle.Complete);
        Assert.False(idle.Continuous);
    }

    [Fact]
    public void A_run_of_choices_carries_its_weights()
    {
        GasFile talk = Parse(
            """
            LABEL START
            ONEOF GabRTalk2Subtle, 100
            ONEOF GabRTalk2Talk1, 50
            GOTO START
            """);

        Assert.True(talk.Complete);
        Assert.Equal(
            [("GabRTalk2Subtle", 100), ("GabRTalk2Talk1", 50)],
            talk.Steps.Where(s => s.Action == GasAction.OneOf).Select(s => (s.Name, s.Weight)));

        // A choice written without one still counts for something.
        Assert.Equal(100, Parse("ONEOF emlFigBreath1\n").Steps[0].Weight);
    }

    [Fact]
    public void An_animation_may_carry_a_flag_and_a_chance_in_either_order_of_separator()
    {
        // The content writes commas and spaces interchangeably, sometimes in one line:
        // "ANIM AbeHe1FightFidget, FALSE 50". Both separate and neither is significant.
        GasFile script = Parse(
            """
            ANIM BarRL2BrethElbow, TRUE
            ANIM AbeHe1FightAbeTalk, FALSE ,20
            ANIM AbeHe1FightFidget, FALSE 50
            ANIM plain
            """);

        Assert.True(script.Complete);
        Assert.Equal([100, 20, 50, 100], script.Steps.Select(s => s.Chance));
        Assert.Equal([true, false, false, true], script.Steps.Select(s => s.Relative));
    }

    [Fact]
    public void Registers_and_branches_are_read()
    {
        // BIGIDLE.GAS counts its wipes and does a longer one every other time. The IF is
        // written with commas there and without them in CHICKEN.GAS; splitting on either
        // makes the two one shape.
        GasFile counted = Parse(
            """
            SET X, 0
            LABEL START
            INC X
            IF X,  = , 2, TWO
            IF A > 4 CHOOSER
            GOTO START
            LABEL TWO
            LABEL CHOOSER
            """);

        Assert.True(counted.Complete);

        Assert.Equal(GasAction.Set, counted.Steps[0].Action);
        Assert.Equal(0, counted.Steps[0].Value);
        Assert.Equal(GasAction.Increment, counted.Steps[2].Action);

        GasStep first = counted.Steps[3];
        Assert.Equal(GasAction.If, first.Action);
        Assert.Equal("X", first.Name);
        Assert.Equal("=", first.Comparison);
        Assert.Equal(2, first.Value);
        Assert.Equal("TWO", first.Other);

        GasStep second = counted.Steps[4];
        Assert.Equal(">", second.Comparison);
        Assert.Equal(4, second.Value);
        Assert.Equal("CHOOSER", second.Other);
    }

    [Fact]
    public void A_cleanup_says_what_to_play_when_an_animation_is_cut_short()
    {
        // If the Abbé is interrupted while breathing through his binoculars he lowers them,
        // rather than snapping to standing with them still raised.
        GasFile binoculars = Parse(
            """
            USE CLEANUP abebinocbreath, abebinocdown
            USE CLEANUP  Abebinocfocus1, abebinocdown
            ANIM abebinocup
            """);

        Assert.True(binoculars.Complete);
        Assert.Equal("abebinocdown", binoculars.CleanupFor("ABEBINOCBREATH"));
        Assert.Null(binoculars.CleanupFor("abebinocup"));
    }

    [Fact]
    public void A_walk_may_be_one_of_several_places_and_the_brackets_are_decoration()
    {
        GasFile wandering = Parse(
            """
            CHOOSEWALK ( ABE_MOVE_1 ,ABE_MOVE_2 ,ABE_MOVE_3, ABE_MOVE_4 )
            WALKTO MIDDLE
            """);

        Assert.True(wandering.Complete);
        Assert.Equal(
            ["ABE_MOVE_1", "ABE_MOVE_2", "ABE_MOVE_3", "ABE_MOVE_4"],
            wandering.Steps[0].Names!);

        Assert.Equal(GasAction.WalkTo, wandering.Steps[1].Action);
        Assert.Equal("MIDDLE", wandering.Steps[1].Name);
    }

    [Fact]
    public void The_rest_of_the_language_is_read_even_where_it_is_not_run()
    {
        GasFile watching = Parse(
            """
            WHENNEAR GABRIEL, 110, MOVE_1, CHICKEN
            WHENNOLONGERNEAR GABRIEL, 110, AWAY
            LOOKAT GABRIEL EH 5
            NEWIDLE MosIdle.gas
            DLG 1A96L1IOO1
            LOCATION CHU
            RESETIPOS
            """);

        Assert.True(watching.Complete);
        Assert.Equal(110, watching.Steps[0].Value);
        Assert.Equal("MOVE_1", watching.Steps[0].Other);
        Assert.Equal(5, watching.Steps[2].Seconds, 3);
        Assert.Equal("MosIdle.gas", watching.Steps[3].Name);
        Assert.Equal(GasAction.Speak, watching.Steps[4].Action);
    }

    [Fact]
    public void A_wait_may_be_a_range_with_a_chance_of_happening_at_all()
    {
        GasFile script = Parse("WAIT .5\nWAIT 1, 3, 100\n");

        Assert.Equal(0.5, script.Steps[0].Seconds, 3);
        Assert.Equal(1, script.Steps[1].Seconds, 3);
        Assert.Equal(3, script.Steps[1].To, 3);
    }

    [Fact]
    public void A_keyword_nothing_reads_is_named_rather_than_swallowed()
    {
        GasFile script = Parse("ANIM a\nWIBBLE 3\nLOOP\n");

        Assert.False(script.Complete);
        Assert.Equal("WIBBLE", Assert.Single(script.Unsupported));

        // And everything around it still runs.
        Assert.Equal(2, script.Steps.Count);
    }

    [Fact]
    public void A_label_can_be_jumped_back_to()
    {
        GasFile script = Parse("LABEL start\nANIM a\nWAIT 1\nGOTO start\n");

        Assert.Equal(1, script.LabelAt("START"));
        Assert.Null(script.LabelAt("nowhere"));
    }
}
