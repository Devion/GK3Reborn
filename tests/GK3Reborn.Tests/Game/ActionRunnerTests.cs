using GK3Reborn.Formats.Actions;
using GK3Reborn.Foundation.Diagnostics;
using GK3Reborn.Game;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Tests for carrying out an action.
/// </summary>
/// <remarks>
/// An action's script is a much smaller language than Sheep — across the corpus every one
/// of its 6,842 statements is a function call — so the runner reads statements rather than
/// compiling. What matters is that it performs exactly what the file says, in order, and
/// refuses anything else out loud instead of guessing at it.
/// </remarks>
public sealed class ActionRunnerTests
{
    private static NvcAction Action(string script, string verb = "LOOK", string noun = "PAINTING") =>
        new()
        {
            Noun = noun,
            Verb = verb,
            Case = "ALL",
            Script = script,
            Source = "test.nvc:1",
        };

    private static (ActionRunner Runner, Gk3SheepApi Api, GameState State) Host()
    {
        var state = new GameState();
        var api = new Gk3SheepApi(state);
        return (new ActionRunner(api), api, state);
    }

    private static NvcAction Reached(string script, string how, string target) =>
        new()
        {
            Noun = "BUTHANE",
            Verb = "TALK",
            Case = "ALL",
            Script = script,
            Source = "test.nvc:1",
            Approach = how,
            Target = target,
        };

    [Fact]
    public void The_player_walks_to_the_thing_before_the_script_runs()
    {
        // approach=WalkTo is not part of the script — it is what has to be true before the
        // script runs. Running the two together is what had Gabriel talking to somebody he
        // was still crossing the square towards, and opening a door from the far side of a
        // room.
        (ActionRunner runner, Gk3SheepApi api, GameState state) = Host();

        List<Action> held = [];

        api.Walks = (_, _, _, _, _) => 4.0;
        api.Defers = (_, work) =>
        {
            held.Add(work);
            return true;
        };

        ActionOutcome outcome = runner.Run(
            Reached(@"SetFlag(""Talked"")", "WalkTo", "TALK_BUTHANE"));

        Assert.True(outcome.Deferred);
        Assert.Equal(4.0, outcome.Approaching, 3);
        Assert.False(state.GetFlag("Talked"));

        Assert.Single(held)();
        Assert.True(state.GetFlag("Talked"));
    }

    [Fact]
    public void An_action_with_nowhere_to_walk_runs_where_it_was_asked_for()
    {
        // A walk of no length is not a walk. Queuing one would hold an ordinary action back
        // for a frame for nothing.
        (ActionRunner runner, Gk3SheepApi api, GameState state) = Host();

        api.Walks = (_, _, _, _, _) => 0;
        api.Defers = (_, _) => throw new InvalidOperationException("nothing to wait for");

        ActionOutcome outcome = runner.Run(
            Reached(@"SetFlag(""Talked"")", "WalkTo", "TALK_BUTHANE"));

        Assert.False(outcome.Deferred);
        Assert.True(outcome.Ran);
        Assert.True(state.GetFlag("Talked"));
    }

    [Fact]
    public void A_tool_with_nothing_to_wait_with_runs_the_action_as_it_always_did()
    {
        (ActionRunner runner, Gk3SheepApi api, GameState state) = Host();

        api.Walks = (_, _, _, _, _) => 4.0;

        ActionOutcome outcome = runner.Run(
            Reached(@"SetFlag(""Talked"")", "WalkTo", "TALK_BUTHANE"));

        Assert.False(outcome.Deferred);
        Assert.Equal(4.0, outcome.Approaching, 3);
        Assert.True(state.GetFlag("Talked"));
    }

    [Fact]
    public void A_turn_is_an_approach_too_and_the_script_waits_for_it()
    {
        // 394 of the corpus's 3,617 approaches are turns. Walking to the thing instead
        // puts the player on top of whatever they meant to look at.
        (ActionRunner runner, Gk3SheepApi api, _) = Host();

        Approaching? asked = null;

        api.Walks = (_, _, how, _, _) =>
        {
            asked = how;
            return 1.5;
        };

        api.Defers = (_, _) => true;

        ActionOutcome outcome = runner.Run(
            Reached(@"SetFlag(""Looked"")", "TurnToModel", "rc1_hotel"));

        Assert.Equal(Approaching.Turn, asked);
        Assert.True(outcome.Deferred);
    }

    [Fact]
    public void The_statements_run_in_the_order_the_file_wrote_them()
    {
        (ActionRunner runner, Gk3SheepApi api, GameState state) = Host();

        ActionOutcome outcome = runner.Run(Action(
            """wait StartVoiceOver("1LLJ644QR1",1); SetFlag("SawPainting"); IncNounVerbCount("PAINTING","LOOK")"""));

        Assert.True(outcome.Ran);
        Assert.Equal(
            [("StartVoiceOver", true), ("SetFlag", false), ("IncNounVerbCount", false)],
            outcome.Statements.Select(s => (s.Call, s.Waited)));

        // The registered ones did their work; the presentation call was recorded.
        Assert.True(state.GetFlag("SawPainting"));
        Assert.Equal(1, state.GetNounVerbCount("PAINTING", "LOOK"));
        Assert.Contains(api.Events, e => e.Name == "StartVoiceOver");
    }

    [Fact]
    public void A_semicolon_inside_a_string_or_an_argument_list_is_not_a_separator()
    {
        (ActionRunner runner, _, GameState state) = Host();

        ActionOutcome outcome = runner.Run(Action(
            """SetGameVariableInt("a;b", 1); SetGameVariableInt("c", 2)"""));

        Assert.True(outcome.Ran);
        Assert.Equal(2, outcome.Statements.Count);
        Assert.Equal(1, state.GetVariable("a;b"));
        Assert.Equal(2, state.GetVariable("c"));
    }

    [Fact]
    public void A_trailing_semicolon_does_not_make_an_empty_statement()
    {
        (ActionRunner runner, _, _) = Host();

        Assert.Single(runner.Run(Action("""SetFlag("x");""")).Statements);
    }

    [Fact]
    public void A_statement_that_is_not_a_call_refuses_the_whole_action()
    {
        // Refused whole, the way a compiler refuses a file: half an action is worse than
        // none, because the half that ran has already changed the story.
        (ActionRunner runner, _, GameState state) = Host();

        ActionOutcome outcome = runner.Run(Action("""SetFlag("first"); 1 + 1"""));

        Assert.False(outcome.Ran);
        Assert.False(state.GetFlag("first"));
        Assert.Contains(runner.Diagnostics.Items, d => d.Code == "GK3R3302");
    }

    [Fact]
    public void A_bare_name_is_a_call_that_takes_nothing()
    {
        // One script in the corpus says Yield and nothing else.
        (ActionRunner runner, Gk3SheepApi api, _) = Host();

        ActionOutcome outcome = runner.Run(Action("Yield"));

        Assert.True(outcome.Ran);
        Assert.Equal("Yield", Assert.Single(outcome.Statements).Call);
        Assert.Contains(api.Events, e => e.Name == "Yield");
    }

    [Fact]
    public void A_topic_counts_itself_and_an_ordinary_verb_does_not()
    {
        // The original's asymmetry: a topic is used up by being raised, so the engine
        // records it, while an ordinary action increments its own count only if its script
        // says so — which 260 of the corpus's scripts do.
        (ActionRunner runner, _, GameState state) = Host();

        runner.Run(Action("""SetFlag("x")""", verb: "T_THE_BODY", noun: "MOSELY"));
        Assert.Equal(1, state.GetTopicCount("MOSELY", "T_THE_BODY"));

        runner.Run(Action("""SetFlag("y")""", verb: "LOOK", noun: "MOSELY"));
        Assert.Equal(0, state.GetNounVerbCount("MOSELY", "LOOK"));

        runner.Run(Action("""SetFlag("z")""", verb: "Z_CHAT", noun: "MOSELY"));
        Assert.Equal(1, state.GetChatCount("MOSELY"));
    }

    [Fact]
    public void An_empty_script_runs_and_does_nothing()
    {
        (ActionRunner runner, _, GameState state) = Host();
        string before = state.ComputeHash();

        ActionOutcome outcome = runner.Run(Action(string.Empty));

        Assert.True(outcome.Ran);
        Assert.Empty(outcome.Statements);
        Assert.Equal(before, state.ComputeHash());
    }

    [Fact]
    public void Reading_a_script_performs_none_of_it()
    {
        (ActionRunner runner, _, GameState state) = Host();
        string before = state.ComputeHash();

        Assert.NotNull(runner.Read(Action("""SetFlag("x"); SetFlag("y")""")));
        Assert.Equal(before, state.ComputeHash());
        Assert.Null(runner.Read(Action("1 + 1")));
    }

    [Fact]
    public void The_resolver_finds_the_rule_a_verb_would_run()
    {
        var diagnostics = new DiagnosticBag();
        var resolver = new ActionResolver(new Gk3SheepApi(new GameState()));

        resolver.Add(NvcFile.Parse(
            """
            WINDOW, OPEN, GABE_ALL, script={wait CallSheep("R25_ALL","WINDOW_OPEN");}
            WINDOW, OPEN, GRACE_ALL, script={wait CallSheep("R25_ALL","WINDOW_OPEN_Grace");}
            WINDOW, CLOSE, EGG, script={}
            """,
            "r25_all.nvc",
            diagnostics));

        Assert.Contains("WINDOW_OPEN\"", resolver.Find("WINDOW", "OPEN")!.Script);
        Assert.Contains("_Grace", resolver.Find("WINDOW", "OPEN", "GRACE")!.Script);

        // Its case does not hold, so there is no rule to run even though one is written.
        Assert.Null(resolver.Find("WINDOW", "CLOSE"));
        Assert.Null(resolver.Find("WINDOW", "SMASH"));
    }
}
