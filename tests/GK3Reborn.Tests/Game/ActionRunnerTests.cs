using GK3Reborn.Formats.Actions;
using GK3Reborn.Foundation.Diagnostics;
using GK3Reborn.Game;
using GK3Reborn.Sheep;
using GK3Reborn.Tests.Sheep;
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

    /// <summary>
    /// A script whose one function waits on a timer and then sets a flag.
    /// </summary>
    /// <remarks>
    /// The shape of every cutscene an action calls into: a wait block over something that
    /// takes real time, and the rest of the function on the other side of it.
    /// </remarks>
    private static SheepScriptFile Cutscene(string flag) =>
        TestScripts.Build("CS6_ALL.SHP", builder =>
        {
            builder.Import("SetTimerSeconds", 0, 2);
            builder.Import("SetFlag", 0, 3);
            int name = builder.String(flag);

            builder.Function("Old_GRACE$")
                .Op(SheepOpcode.BeginWait)
                .OpF(SheepOpcode.PushF, 3f)
                .Op(SheepOpcode.PushI, 1)
                .Op(SheepOpcode.CallSysFunctionV, 0)
                .Op(SheepOpcode.Pop)
                .Op(SheepOpcode.EndWait)
                .Op(SheepOpcode.PushS, name)
                .Op(SheepOpcode.GetString)
                .Op(SheepOpcode.PushI, 1)
                .Op(SheepOpcode.CallSysFunctionV, 1)
                .Op(SheepOpcode.Pop)
                .Op(SheepOpcode.ReturnV);
        });

    /// <summary>The room's half of a wait on a script, without a room.</summary>
    /// <remarks>
    /// <see cref="SceneUpdate.Until"/> keeps the work beside the threads and asks the
    /// scheduler on every tick whether any of them is still parked. That is the whole of
    /// it, and this is the same two lines with the tick made explicit.
    /// </remarks>
    private sealed class Room(SheepScheduler scheduler)
    {
        private readonly List<(IReadOnlyList<SheepThread> Until, Action Work)> _held = [];

        public bool Until(IReadOnlyList<SheepThread> scripts, Action work)
        {
            if (!scheduler.Outstanding(scripts))
            {
                return false;
            }

            _held.Add((scripts, work));
            return true;
        }

        public void Advance(double seconds)
        {
            scheduler.Advance(seconds);

            for (int i = _held.Count - 1; i >= 0; i--)
            {
                if (scheduler.Outstanding(_held[i].Until))
                {
                    continue;
                }

                Action work = _held[i].Work;
                _held.RemoveAt(i);
                work();
            }
        }
    }

    [Fact]
    public void A_waited_call_into_a_script_holds_the_rest_of_the_action_until_it_is_over()
    {
        // CS6's old lady, and the whole of what was reported: OLD_LADY, TALK reads
        // wait CallSheep("cs6_all", "Old_Grace$"); ... setlocation("cse"), and the called
        // function is forty seconds of camera cuts, animation and dialogue. Its length is
        // not a number any host can answer for, so the statement was worth no time at all
        // and the courtyard arrived in the frame the cutscene started.
        var state = new GameState();
        var api = new Gk3SheepApi(state);
        var host = new ScriptHost(api);
        var scheduler = new SheepScheduler(host.Machine);

        host.Scheduler = scheduler;
        host.Add(Cutscene("CutsceneOver"));

        var room = new Room(scheduler);
        api.DefersUntil = room.Until;

        ActionOutcome outcome = new ActionRunner(api).Run(Action(
            """wait CallSheep("cs6_all", "Old_Grace$"); SetLocation("cse")""",
            "TALK",
            "OLD_LADY"));

        // Committed, and not finished: the cutscene is running and the room has not moved.
        Assert.True(outcome.Ran);
        Assert.True(outcome.Deferred);
        Assert.False(state.GetFlag("CutsceneOver"));
        Assert.NotEqual("cse", state.Location, StringComparer.OrdinalIgnoreCase);

        room.Advance(1.0);

        Assert.False(state.GetFlag("CutsceneOver"));
        Assert.NotEqual("cse", state.Location, StringComparer.OrdinalIgnoreCase);

        // The wait inside the called function is over, so the rest of it runs — and then
        // the rest of the action does.
        room.Advance(2.5);

        Assert.True(state.GetFlag("CutsceneOver"));
        Assert.Equal("cse", state.Location, ignoreCase: true);
    }

    [Fact]
    public void An_unwaited_call_into_a_script_does_not_hold_anything_up()
    {
        // The script left it running behind itself on purpose, which is what an unwaited
        // call is for. Holding the rest of the action back for one would stop a room that
        // starts a background script and carries on.
        var state = new GameState();
        var api = new Gk3SheepApi(state);
        var host = new ScriptHost(api);
        var scheduler = new SheepScheduler(host.Machine);

        host.Scheduler = scheduler;
        host.Add(Cutscene("CutsceneOver"));

        var room = new Room(scheduler);
        api.DefersUntil = room.Until;

        ActionOutcome outcome = new ActionRunner(api).Run(Action(
            """CallSheep("cs6_all", "Old_Grace$"); SetLocation("cse")"""));

        Assert.False(outcome.Deferred);
        Assert.Equal("cse", state.Location, ignoreCase: true);
        Assert.False(state.GetFlag("CutsceneOver"));
    }

    [Fact]
    public void A_call_into_a_script_that_finished_leaves_the_action_running_in_one_frame()
    {
        // The ordinary CallSheep: nothing in the called function waits, so there is
        // nothing outstanding when it returns and the statement after it is this frame's.
        var state = new GameState();
        var api = new Gk3SheepApi(state);
        var host = new ScriptHost(api);
        var scheduler = new SheepScheduler(host.Machine);

        host.Scheduler = scheduler;
        host.Add(TestScripts.Build("CS6_ALL.SHP", builder =>
        {
            builder.Import("SetFlag", 0, 3);
            int name = builder.String("Straight");

            builder.Function("Old_GRACE$")
                .Op(SheepOpcode.PushS, name)
                .Op(SheepOpcode.GetString)
                .Op(SheepOpcode.PushI, 1)
                .Op(SheepOpcode.CallSysFunctionV, 0)
                .Op(SheepOpcode.Pop)
                .Op(SheepOpcode.ReturnV);
        }));

        api.DefersUntil = new Room(scheduler).Until;

        ActionOutcome outcome = new ActionRunner(api).Run(Action(
            """wait CallSheep("cs6_all", "Old_Grace$"); SetLocation("cse")"""));

        Assert.False(outcome.Deferred);
        Assert.True(state.GetFlag("Straight"));
        Assert.Equal("cse", state.Location, ignoreCase: true);
    }

    [Fact]
    public void A_tool_with_nothing_to_wait_on_a_script_with_runs_the_action_straight_through()
    {
        // Every sweep of the corpus. There is no scheduler, so the called function runs
        // inline to completion and the statement after it follows immediately, exactly as
        // it always did.
        var state = new GameState();
        var api = new Gk3SheepApi(state);
        var host = new ScriptHost(api);

        host.Add(Cutscene("CutsceneOver"));

        Assert.Null(api.DefersUntil);

        ActionOutcome outcome = new ActionRunner(api).Run(Action(
            """wait CallSheep("cs6_all", "Old_Grace$"); SetLocation("cse")"""));

        Assert.False(outcome.Deferred);
        Assert.True(state.GetFlag("CutsceneOver"));
        Assert.Equal("cse", state.Location, ignoreCase: true);
    }
}
