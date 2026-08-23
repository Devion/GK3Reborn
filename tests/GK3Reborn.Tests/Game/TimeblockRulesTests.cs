using GK3Reborn.Game;
using GK3Reborn.Sheep;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Tests for the rules that decide when a point in the story is over.
/// </summary>
/// <remarks>
/// The rules themselves are <c>Assets/Story/Timeblocks.shp</c>, carried by the engine
/// because the original kept them in its executable — no script in the game's own archives
/// calls <c>SetTime</c> at all. What is checked here is that they compile, that they are
/// reachable, and that the clock moves only when everything a timeblock asks for is done.
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

    private static (ScriptHost Host, Gk3SheepApi Api) Rules()
    {
        var state = new GameState { Timeblock = new Timeblock(1, 10, false), Location = "RC1" };
        var api = new Gk3SheepApi(state);
        var host = new ScriptHost(api);

        host.Add(SheepCompiler.Compile(Script(), "Timeblocks.shp"));

        return (host, api);
    }

    /// <summary>The engine's own copy of the rules.</summary>
    private static string Script()
    {
        using Stream? carried = typeof(GameState).Assembly
            .GetManifestResourceStream("GK3Reborn.Assets.Story.Timeblocks.shp");

        Assert.NotNull(carried);

        using var reader = new StreamReader(carried);

        return reader.ReadToEnd();
    }

    private static void Did(GameState state, (string Noun, string Verb, int Count) done)
    {
        state.SetNounVerbCount(done.Noun, done.Verb, done.Count);
        state.SetTopicCount(done.Noun, done.Verb, done.Count);
    }

    [Fact]
    public void The_rules_the_engine_carries_compile()
    {
        // They are source rather than bytecode, so they are compiled at startup by the
        // engine's own compiler. A rule set that will not compile is a story that cannot
        // advance, and nothing else would say so.
        (ScriptHost host, _) = Rules();

        Assert.Contains(
            host.LoadedScripts,
            id => id.ToString().Contains("TIMEBLOCKS", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_timeblock_with_nothing_done_does_not_end()
    {
        (ScriptHost host, Gk3SheepApi api) = Rules();

        host.Run("Timeblocks.shp", "CheckTimeblockComplete$");

        Assert.False(api.State.ChangingTimeblock);
        Assert.Equal(new Timeblock(1, 10, false), api.State.Timeblock);
    }

    [Fact]
    public void Everything_but_one_thing_is_not_enough()
    {
        // Each rule is a guard clause and any one of them stops the check, so the
        // interesting case is not "none of them" but "all but one" — which is where a
        // condition read the wrong way round would show.
        for (int missing = 0; missing < Morning.Length; missing++)
        {
            (ScriptHost host, Gk3SheepApi api) = Rules();

            for (int i = 0; i < Morning.Length; i++)
            {
                if (i != missing)
                {
                    Did(api.State, Morning[i]);
                }
            }

            host.Run("Timeblocks.shp", "CheckTimeblockComplete$");

            Assert.False(
                api.State.ChangingTimeblock,
                $"the morning ended without {Morning[missing].Noun}:{Morning[missing].Verb}");
        }
    }

    [Fact]
    public void The_morning_ends_once_it_is_all_done()
    {
        (ScriptHost host, Gk3SheepApi api) = Rules();

        foreach ((string Noun, string Verb, int Count) done in Morning)
        {
            Did(api.State, done);
        }

        host.Run("Timeblocks.shp", "CheckTimeblockComplete$");

        Assert.True(api.State.ChangingTimeblock);

        api.State.StartedTimeblock();

        Assert.Equal(new Timeblock(1, 12, true), api.State.Timeblock);
    }

    [Fact]
    public void And_only_where_the_player_is_meant_to_be()
    {
        // "Must be at RC1 to complete timeblock" is 110A's first line, and it is why the
        // check runs on a change of location and after the new one is current: the morning
        // ends as you walk into the square, not the moment you finish the last errand.
        (ScriptHost host, Gk3SheepApi api) = Rules();

        api.State.Location = "LBY";

        foreach ((string Noun, string Verb, int Count) done in Morning)
        {
            Did(api.State, done);
        }

        host.Run("Timeblocks.shp", "CheckTimeblockComplete$");

        Assert.False(api.State.ChangingTimeblock);
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
