using GK3Reborn.Game;
using GK3Reborn.Sheep;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Tests for the state the action files ask about beyond flags and counts.
/// </summary>
/// <remarks>
/// These six functions were found by sweeping the corpus rather than by reading it: an
/// unimplemented function returns zero and warns once, so a condition that depends on one
/// silently reads as false and the action it guards leaves the game.
/// </remarks>
public sealed class SheepApiStateTests
{
    private static int Eval(GameState state, string expression) =>
        SheepExpression.Evaluate(expression, new Gk3SheepApi(state)).AsInt();

    [Fact]
    public void Nothing_the_action_files_ask_for_goes_unanswered()
    {
        var api = new Gk3SheepApi(new GameState());

        foreach (string name in (string[])
                 [
                     "DoesSidneyFileExist", "GetNounVerbCountInt", "GetRandomInt",
                     "GetTopicCountInt", "IsActiveInvItem", "IsTopLayerInventory",
                 ])
        {
            api.Invoke(name, [SheepValue.FromString("x"), SheepValue.FromString("y")]);
        }

        Assert.Empty(api.UnknownFunctions);
    }

    [Fact]
    public void The_active_inventory_item_is_the_one_in_hand_not_the_ones_in_the_bag()
    {
        var state = new GameState();
        state.Inventory.Add("GABRIEL", "TAPE_RECORDER");
        state.Inventory.Add("GABRIEL", "CANDY");

        Assert.Equal(0, Eval(state, "IsActiveInvItem(\"CANDY\")"));

        state.Inventory.SetActive("GABRIEL", "CANDY");
        Assert.Equal(1, Eval(state, "IsActiveInvItem(\"CANDY\")"));
        Assert.Equal(0, Eval(state, "IsActiveInvItem(\"TAPE_RECORDER\")"));

        // It follows whoever is being played, like DoesEgoHaveInvItem.
        state.Ego = "GRACE";
        Assert.Equal(0, Eval(state, "IsActiveInvItem(\"CANDY\")"));
    }

    [Fact]
    public void An_item_that_is_taken_away_stops_being_the_one_in_hand()
    {
        var state = new GameState();
        state.Inventory.Add("GABRIEL", "CANDY");
        state.Inventory.SetActive("GABRIEL", "CANDY");

        state.Inventory.Remove("GABRIEL", "CANDY");

        Assert.Null(state.Inventory.ActiveItemOf("GABRIEL"));
        Assert.Equal(0, Eval(state, "IsActiveInvItem(\"CANDY\")"));
    }

    [Fact]
    public void A_script_can_put_an_item_in_egos_hand()
    {
        var state = new GameState();
        state.Inventory.Add("GABRIEL", "CANDY");

        Assert.Equal(0, Eval(state, "SetEgoActiveInvItem(\"CANDY\")"));
        Assert.Equal("CANDY", state.Inventory.ActiveItemOf("GABRIEL"));
    }

    [Fact]
    public void Sidney_holds_the_evidence_the_player_has_gathered()
    {
        var state = new GameState();

        Assert.Equal(0, Eval(state, "DoesSidneyFileExist(\"ARCADIA_TEXT\")"));

        state.AddSidneyFile("Arcadia_Text");

        // Named, and case-insensitive like every other name in this data — which is why
        // the list gives back the normalised spelling rather than the one that was added.
        Assert.Equal(1, Eval(state, "DoesSidneyFileExist(\"ARCADIA_TEXT\")"));
        Assert.Equal(["ARCADIA_TEXT"], state.SidneyFiles);
    }

    [Fact]
    public void The_int_forms_of_the_counts_ask_the_same_question_as_the_named_ones()
    {
        // A case's n$ and v$ carry the names here rather than numbers, so the Int suffix
        // is only history — but the files use both spellings and both have to work.
        var state = new GameState();
        state.SetNounVerbCount("BLOOD_POOL", "LOOK", 3);
        state.SetTopicCount("MOSELY", "T_THE_BODY", 2);

        Assert.Equal(3, Eval(state, "GetNounVerbCountInt(\"BLOOD_POOL\", \"LOOK\")"));
        Assert.Equal(3, Eval(state, "GetNounVerbCount(\"BLOOD_POOL\", \"LOOK\")"));
        Assert.Equal(2, Eval(state, "GetTopicCountInt(\"MOSELY\", \"T_THE_BODY\")"));
    }

    [Fact]
    public void What_one_character_has_already_done_says_nothing_about_the_other()
    {
        // Gabriel and Grace investigate the same places, so 1ST_TIME means the first time
        // for whoever is being played. The game has a function whose only purpose is to
        // set both counts at once, which is what gives the distinction away.
        var state = new GameState();

        state.IncrementNounVerbCount("WINDOW", "OPEN");

        Assert.Equal(1, state.GetNounVerbCount("WINDOW", "OPEN"));
        Assert.Equal(0, state.GetNounVerbCount("GRACE", "WINDOW", "OPEN"));

        state.Ego = "GRACE";
        Assert.Equal(0, state.GetNounVerbCount("WINDOW", "OPEN"));
    }

    [Fact]
    public void A_door_opened_is_open_for_whoever_walks_in_next()
    {
        var state = new GameState();

        Assert.Equal(0, Eval(state, "SetNounVerbCountBoth(\"WINDOW\", \"OPEN\", 1)"));

        Assert.Equal(1, state.GetNounVerbCount("GABRIEL", "WINDOW", "OPEN"));
        Assert.Equal(1, state.GetNounVerbCount("GRACE", "WINDOW", "OPEN"));
    }

    [Fact]
    public void A_random_number_falls_inside_the_range_at_both_ends()
    {
        var state = new GameState();

        for (int i = 0; i < 200; i++)
        {
            int value = state.NextRandom(3, 5);
            Assert.InRange(value, 3, 5);
        }

        // Both ends are reachable: the original's documentation says the range is
        // inclusive, and a generator that never returns the top is a subtle way to make a
        // puzzle unsolvable.
        Assert.Contains(Enumerable.Range(0, 50).Select(_ => state.NextRandom(0, 1)), v => v == 1);
        Assert.Equal(1, state.NextRandom(1, 1));
    }

    [Fact]
    public void The_same_story_draws_the_same_numbers()
    {
        // No ambient nondeterminism in engine code, ADR 0004: two runs of the same build
        // have to agree, or the differential harness compares noise.
        int[] first = [.. Enumerable.Range(0, 10).Select(_ => new GameState().NextRandom(1, 1000))];
        int[] again = [.. Enumerable.Range(0, 10).Select(_ => new GameState().NextRandom(1, 1000))];

        Assert.Equal(first, again);

        var state = new GameState();
        int[] sequence = [.. Enumerable.Range(0, 10).Select(_ => state.NextRandom(1, 1000))];

        // A sequence, not the same number over and over.
        Assert.True(sequence.Distinct().Count() > 1);
    }

    [Fact]
    public void Drawing_a_number_is_part_of_the_state_two_runs_are_compared_on()
    {
        var state = new GameState();
        string before = state.ComputeHash();

        state.NextRandom(1, 100);

        Assert.NotEqual(before, state.ComputeHash());
        Assert.Equal(1, state.RandomDraws);
    }

    [Fact]
    public void What_is_in_hand_and_what_Sidney_holds_are_part_of_that_state_too()
    {
        var state = new GameState();
        state.Inventory.Add("GABRIEL", "CANDY");

        string carried = state.ComputeHash();

        state.Inventory.SetActive("GABRIEL", "CANDY");
        string inHand = state.ComputeHash();
        Assert.NotEqual(carried, inHand);

        state.AddSidneyFile("ARCADIA_TEXT");
        Assert.NotEqual(inHand, state.ComputeHash());
    }
}
