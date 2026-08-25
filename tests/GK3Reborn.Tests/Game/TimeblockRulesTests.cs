using GK3Reborn.Game;
using GK3Reborn.Game.Story;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Tests for the rules that decide when a point in the story is over.
/// </summary>
/// <remarks>
/// The rules themselves are <see cref="TimeblockRules"/>, carried by the engine because the
/// original kept them in its executable — no script in the game's own archives calls
/// <c>SetTime</c> at all. What is checked here is that every timeblock has a rule, and that
/// the clock moves only when everything a timeblock asks for is done.
/// </remarks>
public sealed class TimeblockRulesTests
{
    /// <summary>Everything 110A requires, as noun/verb and topic counts.</summary>
    private static readonly (string Noun, string Verb, int Count)[] Morning =
    [
        ("COFFEE_POT", "POUR", 1),
        ("PHONE", "PRINCE_JAMES_CARD", 1),
        ("REGISTER", "READ", 1),
        ("EMILIO", "T_INTRODUCE", 1),
        ("BUTHANE", "T_CHECK_IN", 2),
        ("SAN_GREAL_WORDS", "LOOK", 1),
        ("GIRARD", "T_HOLY_GRAIL", 1),
        ("LADY_H_ESTELLE", "T_INTRODUCE", 1),
    ];

    private static GameState Morning110A() =>
        new() { Timeblock = new Timeblock(1, 10, false), Location = "RC1" };

    private static void Did(GameState state, (string Noun, string Verb, int Count) done)
    {
        state.SetNounVerbCount(done.Noun, done.Verb, done.Count);
        state.SetTopicCount(done.Noun, done.Verb, done.Count);
    }

    [Fact]
    public void Every_timeblock_the_game_has_rules_for_is_answered()
    {
        // The dispatch is a switch on the timeblock's own code, and a code that falls
        // through it is a point in the story that can never end. Sixteen of them; 309P is
        // where the story stops and has no rule of its own.
        foreach (string code in TimeblockRules.Known)
        {
            Assert.True(Timeblock.TryParse(code, out Timeblock block), $"{code} does not parse");
            Assert.Equal(code, block.ToString());

            // Nothing done and nowhere the rules want the player, so the answer is null.
            var state = new GameState { Timeblock = block, Location = "NOWHERE" };

            Assert.Null(TimeblockRules.Check(state));
        }
    }

    [Fact]
    public void A_timeblock_with_no_rules_is_simply_not_over()
    {
        // 309P is the end of the story, and any other unrecognised code is a save from a
        // future version or a broken one. Neither should throw.
        var state = new GameState { Timeblock = new Timeblock(3, 9, true), Location = "R25" };

        Assert.Null(TimeblockRules.Check(state));
    }

    [Fact]
    public void A_timeblock_with_nothing_done_does_not_end() =>
        Assert.Null(TimeblockRules.Check(Morning110A()));

    [Fact]
    public void Everything_but_one_thing_is_not_enough()
    {
        // Each rule is a guard clause and any one of them stops the check, so the
        // interesting case is not "none of them" but "all but one" — which is where a
        // condition read the wrong way round would show.
        for (int missing = 0; missing < Morning.Length; missing++)
        {
            GameState state = Morning110A();

            for (int i = 0; i < Morning.Length; i++)
            {
                if (i != missing)
                {
                    Did(state, Morning[i]);
                }
            }

            Assert.Null(TimeblockRules.Check(state));
        }
    }

    [Fact]
    public void The_morning_ends_once_it_is_all_done()
    {
        GameState state = Morning110A();

        foreach ((string Noun, string Verb, int Count) done in Morning)
        {
            Did(state, done);
        }

        TimeblockCompletion? completion = TimeblockRules.Check(state);

        Assert.NotNull(completion);
        Assert.Equal(new Timeblock(1, 12, true), completion.Value.Next);

        // 110A ends where it began, so it names no room of its own.
        Assert.Null(completion.Value.Location);
    }

    [Fact]
    public void And_only_where_the_player_is_meant_to_be()
    {
        // "Must be at RC1 to complete timeblock" is 110A's first line, and it is why the
        // check runs on a change of location and after the new one is current: the morning
        // ends as you walk into the square, not the moment you finish the last errand.
        GameState state = Morning110A();
        state.Location = "LBY";

        foreach ((string Noun, string Verb, int Count) done in Morning)
        {
            Did(state, done);
        }

        Assert.Null(TimeblockRules.Check(state));
    }

    [Fact]
    public void The_rules_ask_where_the_player_is_case_insensitively()
    {
        // Locations arrive from scene files, save games and the command line in whatever
        // case they were written in, and the Sheep function these rules replace compared
        // them with OrdinalIgnoreCase.
        GameState state = Morning110A();
        state.Location = "rc1";

        foreach ((string Noun, string Verb, int Count) done in Morning)
        {
            Did(state, done);
        }

        Assert.NotNull(TimeblockRules.Check(state));
    }

    [Fact]
    public void One_timeblock_says_where_the_player_ends_up()
    {
        // 210A is the only one of the sixteen that moves the player as well as the clock:
        // lunch at the Chateau de Serras. Everything it needs is checked by the lobby's own
        // action file, which leaves this single count as the trace of it.
        var state = new GameState { Timeblock = new Timeblock(2, 10, false), Location = "LBY" };

        Assert.Null(TimeblockRules.Check(state));

        state.SetNounVerbCount("MAID", "FOLLOW", 1);

        TimeblockCompletion? completion = TimeblockRules.Check(state);

        Assert.NotNull(completion);
        Assert.Equal(new Timeblock(2, 12, true), completion.Value.Next);
        Assert.Equal("CSE", completion.Value.Location);
    }

    [Fact]
    public void Deciding_does_not_move_the_story()
    {
        // Check is asked on every change of location. It answers; Application acts, and
        // keeping those apart is what lets a rule be asked without consequences.
        GameState state = Morning110A();

        foreach ((string Noun, string Verb, int Count) done in Morning)
        {
            Did(state, done);
        }

        Assert.NotNull(TimeblockRules.Check(state));

        Assert.False(state.ChangingTimeblock);
        Assert.Equal(new Timeblock(1, 10, false), state.Timeblock);
    }

    [Fact]
    public void Asking_for_the_timeblock_the_game_is_already_in_does_nothing()
    {
        // Otherwise a rule that fires twice plays the closing film twice.
        var state = new GameState { Timeblock = new Timeblock(1, 10, false) };

        Assert.False(state.ChangeTimeblock(new Timeblock(1, 10, false)));
        Assert.False(state.ChangingTimeblock);
    }

    [Fact]
    public void A_timeblock_change_can_say_where_the_player_ends_up()
    {
        // Several of them do, and it outranks the door the player walked through.
        var state = new GameState { Timeblock = new Timeblock(1, 10, false), Location = "LBY" };

        Assert.True(state.ChangeTimeblock(new Timeblock(1, 12, true), "rc1"));

        state.StartedTimeblock();

        Assert.Equal("RC1", state.Location);
        Assert.Equal(new Timeblock(1, 12, true), state.Timeblock);
    }
}
