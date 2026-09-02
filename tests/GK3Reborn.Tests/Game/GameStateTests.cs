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

        // ChangeScore takes the *name* of a score event, not a number: reading it as one
        // awards nothing, which is what it did for every one of the corpus's 321 calls.
        api.Scores = ScoreEvents.Parse("[SCORES]" + Environment.NewLine + "e_test_event = 25");

        api.Invoke("ChangeScore", [Str("e_test_event")]);
        Assert.Equal(25, state.Score);

        // And once. The same call is made every time the player does the thing.
        api.Invoke("ChangeScore", [Str("e_test_event")]);
        Assert.Equal(25, state.Score);

        // A name the table does not have scores nothing rather than guessing.
        api.Invoke("ChangeScore", [Str("e_no_such_event")]);
        Assert.Equal(25, state.Score);

        // IncreaseScore is the one that takes a number.
        api.Invoke("IncreaseScore", [Num(5)]);
        Assert.Equal(30, state.Score);
    }

    [Theory]
    [InlineData("110A", 1, 10, false)]
    [InlineData("102P", 1, 2, true)]
    [InlineData("202p", 2, 2, true)]
    [InlineData("312P", 3, 12, true)]
    public void A_timeblock_code_survives_a_round_trip(string code, int day, int hour, bool afternoon)
    {
        Assert.True(Timeblock.TryParse(code, out Timeblock timeblock));

        Assert.Equal(new Timeblock(day, hour, afternoon), timeblock);

        // The hour is two digits. Scripts compare against this string, so an unpadded
        // "22P" makes every IsCurrentTime("202p") false and silently loads the wrong
        // state of every scene.
        Assert.Equal(code.ToUpperInvariant(), timeblock.ToString());
    }

    [Fact]
    public void Visits_are_counted_per_actor_per_location_per_timeblock()
    {
        var state = new GameState { Timeblock = new Timeblock(2, 2, IsAfternoon: true), Ego = "GABRIEL" };

        state.EnterLocation("GABRIEL", "R25");
        state.EnterLocation("GABRIEL", "HAL");
        state.EnterLocation("GABRIEL", "R25");

        Assert.Equal(2, state.GetLocationCount("GABRIEL", "R25"));
        Assert.Equal(0, state.GetLocationCount("GRACE", "R25"));

        // A different timeblock is a different count, which is what the scene files ask
        // about: "first time here this afternoon", not "first time here at all".
        state.Timeblock = new Timeblock(3, 3, IsAfternoon: true);
        Assert.Equal(0, state.GetLocationCount("GABRIEL", "R25"));
        Assert.True(state.WasEverInLocation("GABRIEL", "R25"));
        Assert.False(state.WasEverInLocation("GABRIEL", "CHU"));
    }

    [Fact]
    public void Arriving_somewhere_makes_the_place_left_behind_the_last_location()
    {
        var state = new GameState { Ego = "GABRIEL" };

        state.EnterLocation("GABRIEL", "R25");
        Assert.Equal("R25", state.Location);
        Assert.Equal(string.Empty, state.LastLocation);

        state.EnterLocation("GABRIEL", "HAL");
        Assert.Equal("HAL", state.Location);
        Assert.Equal("R25", state.LastLocation);

        // Arriving where you already are is not a move; the original goes out of its way
        // to keep location and last location distinct.
        state.EnterLocation("GABRIEL", "HAL");
        Assert.Equal("R25", state.LastLocation);
        Assert.Equal(2, state.GetLocationCount("GABRIEL", "HAL"));
    }

    [Fact]
    public void An_actor_who_is_not_ego_moves_without_moving_the_player()
    {
        var state = new GameState { Ego = "GABRIEL" };
        state.EnterLocation("GABRIEL", "R25");

        state.EnterLocation("GRACE", "CHU");

        Assert.Equal("R25", state.Location);
        Assert.Equal("CHU", state.GetActorLocation("GRACE"));
        Assert.Equal(1, state.GetLocationCount("GRACE", "CHU"));
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

        // The whole walking family, because this one was left out and nothing noticed. A
        // wait the machine does not know is a wait at all is no wait, and the 165 calls
        // written `wait WalkToAnimation(who, clip); StartMoveAnimation(clip);` played the
        // clip the moment the walk set off — CS3's attic, where Grace pushed the robes
        // aside from across the room and Montreaux acted out his arrival on the stairs.
        Assert.True(api.IsWaitable("WalkToAnimation"));
        Assert.True(api.IsWaitable("WalkToSeeModel"));

        Assert.False(api.IsWaitable("SetFlag"));
        Assert.False(api.IsWaitable("CutToCameraAngle"));
    }

    [Fact]
    public void A_walk_to_an_animations_start_is_priced_by_the_walk_it_actually_makes()
    {
        // The second argument is a clip, not a place, so the ordinary walk cannot price it:
        // it goes looking for a spot of that name, finds none and answers nothing at all.
        // A wait of nothing is a script that plays the clip while the actor is still
        // crossing the room, which is what CS3's attic looked like.
        var api = new Gk3SheepApi(new GameState());

        api.Walks = (_, _, _, _, _) => 0;
        api.WalksToAnimationStart = (actor, animation, _) =>
            actor == "GRACE" && animation == "GraCs3WrdbOpen" ? 3.5 : 0;

        Assert.Equal(
            3.5,
            api.SecondsFor("WalkToAnimation", [Str("GRACE"), Str("GraCs3WrdbOpen")]),
            3);

        // And a room with nowhere to walk to still answers, rather than throwing.
        Assert.Equal(0, new Gk3SheepApi(new GameState()).SecondsFor(
            "WalkToAnimation", [Str("GRACE"), Str("GraCs3WrdbOpen")]), 3);
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
