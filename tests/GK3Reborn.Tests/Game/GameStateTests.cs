using GK3Reborn.Game;
using GK3Reborn.Sheep;
using Xunit;

namespace GK3Reborn.Tests.Game;

public sealed class GameStateTests
{
    [Fact]
    public void Names_are_case_insensitive_throughout()
    {
        // The language specification says upper and lower case are the same, and scripts
        // spell the same flag several ways.
        var state = new GameState();

        state.SetFlag("TalkedToJean");
        Assert.True(state.GetFlag("talkedtojean"));
        Assert.True(state.GetFlag("TALKEDTOJEAN"));

        state.SetVariable("Score_Bonus", 5);
        Assert.Equal(5, state.GetVariable("score_bonus"));

        state.SetNounVerbCount("Window", "Look", 2);
        Assert.Equal(2, state.GetNounVerbCount("WINDOW", "look"));
    }

    [Fact]
    public void Unset_values_read_as_zero_or_empty()
    {
        var state = new GameState();

        Assert.Equal(0, state.GetVariable("never_set"));
        Assert.False(state.GetFlag("never_set"));
        Assert.Equal(0, state.GetNounVerbCount("a", "b"));
        Assert.Equal(string.Empty, state.GetActorLocation("nobody"));
    }

    [Fact]
    public void The_state_hash_is_stable_across_instances()
    {
        // The differential harness compares this between runs and between engines, so a
        // hash that varied with dictionary ordering would be useless.
        static GameState Build()
        {
            var s = new GameState { Location = "LBY" };
            s.SetFlag("zebra");
            s.SetFlag("alpha");
            s.SetVariable("second", 2);
            s.SetVariable("first", 1);
            s.SetNounVerbCount("door", "open", 3);
            s.ChangeScore(15);
            return s;
        }

        Assert.Equal(Build().ComputeHash(), Build().ComputeHash());
    }

    [Fact]
    public void The_state_hash_changes_when_state_does()
    {
        var state = new GameState();
        string before = state.ComputeHash();

        state.SetFlag("something");
        Assert.NotEqual(before, state.ComputeHash());
    }

    [Fact]
    public void Insertion_order_does_not_affect_the_hash()
    {
        var first = new GameState();
        first.SetVariable("a", 1);
        first.SetVariable("b", 2);

        var second = new GameState();
        second.SetVariable("b", 2);
        second.SetVariable("a", 1);

        Assert.Equal(first.ComputeHash(), second.ComputeHash());
    }

    [Fact]
    public void Score_accumulates()
    {
        var state = new GameState();
        state.ChangeScore(10);
        state.ChangeScore(-3);
        Assert.Equal(7, state.Score);
    }
}

public sealed class Gk3SheepApiTests
{
    private static SheepValue Str(string value) => SheepValue.FromString(value);

    private static SheepValue Num(int value) => SheepValue.FromInt(value);

    [Fact]
    public void State_functions_read_and_write_the_game_state()
    {
        var state = new GameState();
        var api = new Gk3SheepApi(state);

        api.Invoke("SetGameVariableInt", [Str("chapter"), Num(3)]);
        Assert.Equal(3, state.GetVariable("chapter"));
        Assert.Equal(3, api.Invoke("GetGameVariableInt", [Str("chapter")]).AsInt());

        api.Invoke("SetFlag", [Str("metJean")]);
        Assert.Equal(1, api.Invoke("GetFlag", [Str("metjean")]).AsInt());

        api.Invoke("ChangeScore", [Num(25)]);
        Assert.Equal(25, state.Score);
    }

    [Fact]
    public void Timeblock_and_location_answer_from_state()
    {
        var state = new GameState { Location = "LBY", Timeblock = new Timeblock(1, 10, IsAfternoon: false) };
        var api = new Gk3SheepApi(state);

        Assert.Equal(1, api.Invoke("IsCurrentTime", [Str("110A")]).AsInt());
        Assert.Equal(0, api.Invoke("IsCurrentTime", [Str("202P")]).AsInt());
        Assert.Equal(1, api.Invoke("IsCurrentLocation", [Str("lby")]).AsInt());
        Assert.Equal(0, api.Invoke("IsCurrentLocation", [Str("din")]).AsInt());
    }

    [Fact]
    public void Presentation_calls_are_recorded_rather_than_performed()
    {
        var api = new Gk3SheepApi(new GameState());

        api.Invoke("CutToCameraAngle", [Str("lby_wide")]);
        api.Invoke("StartAnimation", [Str("gabIdle")]);

        Assert.Equal(["CutToCameraAngle", "StartAnimation"], api.Events.Select(e => e.Name));
        Assert.Equal("lby_wide", api.Events[0].Arguments[0]);
        Assert.Empty(api.UnknownFunctions);
    }

    [Fact]
    public void An_unimplemented_function_is_reported_once_and_still_recorded()
    {
        // Silence here would let a missing function look like a working one.
        var api = new Gk3SheepApi(new GameState());

        api.Invoke("SomeMissingFunction", [Str("x")]);
        api.Invoke("SomeMissingFunction", [Str("y")]);

        Assert.Equal(["SomeMissingFunction"], api.UnknownFunctions);
        Assert.Single(api.Diagnostics.Items);
        Assert.Equal("GK3R3200", api.Diagnostics.Items[0].Code);
        Assert.Equal(2, api.Events.Count);
    }

    [Fact]
    public void Waitable_functions_are_the_ones_the_specification_classifies_as_such()
    {
        var api = new Gk3SheepApi(new GameState());

        Assert.True(api.IsWaitable("WalkTo"));
        Assert.True(api.IsWaitable("StartAnimation"));
        Assert.True(api.IsWaitable("ContinueDialogue"));

        Assert.False(api.IsWaitable("SetFlag"));
        Assert.False(api.IsWaitable("CutToCameraAngle"));
    }

    [Fact]
    public void A_registered_function_overrides_a_recorded_one()
    {
        var api = new Gk3SheepApi(new GameState());
        api.Register("CutToCameraAngle", _ => SheepValue.FromInt(99));

        Assert.Equal(99, api.Invoke("CutToCameraAngle", [Str("x")]).AsInt());
        Assert.Empty(api.Events);
    }
}
