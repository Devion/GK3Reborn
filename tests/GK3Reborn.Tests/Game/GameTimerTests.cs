using GK3Reborn.Game;
using GK3Reborn.Sheep;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Tests for actions the story asks for later.
/// </summary>
/// <remarks>
/// A timer holds a noun and a verb rather than a piece of work, so the rule that runs is
/// the one that applies when the time comes rather than the one that applied when the
/// timer was set. That is the property everything else depends on.
/// </remarks>
public sealed class GameTimerTests
{
    [Fact]
    public void Nothing_is_due_before_its_time()
    {
        var timers = new GameTimers();
        timers.Set("PHONE", "RING", 60);

        Assert.Empty(timers.Advance(59));
        Assert.Equal(1, timers.Count);

        GameTimer due = Assert.Single(timers.Advance(1));

        Assert.Equal("PHONE", due.Noun);
        Assert.Equal("RING", due.Verb);
        Assert.Equal(0, timers.Count);
    }

    [Fact]
    public void A_long_step_cannot_lose_a_timer()
    {
        // Everything due comes back together rather than one per call.
        var timers = new GameTimers();
        timers.Set("PHONE", "RING", 10);
        timers.Set("KETTLE", "BOIL", 20);
        timers.Set("CLOCK", "CHIME", 3600);

        Assert.Equal(
            [("PHONE", "RING"), ("KETTLE", "BOIL")],
            timers.Advance(60).Select(t => (t.Noun, t.Verb)));

        Assert.Equal(1, timers.Count);
    }

    [Fact]
    public void They_come_back_in_the_order_the_story_asked_for_them()
    {
        // Two runs of the same story have to fire them the same way round.
        var timers = new GameTimers();
        timers.Set("FIRST", "DO", 5);
        timers.Set("SECOND", "DO", 5);
        timers.Set("THIRD", "DO", 5);

        Assert.Equal(["FIRST", "SECOND", "THIRD"], timers.Advance(5).Select(t => t.Noun));
    }

    [Fact]
    public void A_wait_of_nothing_is_due_at_the_next_opportunity()
    {
        // The original fires such a timer where it stands, which it can because setting one
        // and running an action happen in the same place. Here they do not, so it waits a
        // step rather than being thrown away.
        var timers = new GameTimers();
        timers.Set("DOOR", "OPEN", 0);
        timers.Set("WINDOW", "OPEN", -5);

        Assert.Equal(2, timers.Count);
        Assert.Equal(["DOOR", "WINDOW"], timers.Advance(0).Select(t => t.Noun));
    }

    [Fact]
    public void A_script_sets_one_in_milliseconds()
    {
        var state = new GameState();

        SheepExpression.Evaluate(
            """SetGameTimer("MOSELY", "LEAVE", 90000)""", new Gk3SheepApi(state));

        GameTimer timer = Assert.Single(state.Timers.Pending);

        Assert.Equal("MOSELY", timer.Noun);
        Assert.Equal("LEAVE", timer.Verb);
        Assert.Equal(90, timer.SecondsRemaining, 3);
    }

    [Fact]
    public void What_is_waiting_is_part_of_the_state_two_runs_are_compared_on()
    {
        // The original saves them, because a minute set in the lobby is still counting in
        // the hall. Two runs that disagree about what is pending have diverged.
        var state = new GameState();
        string before = state.ComputeHash();

        state.Timers.Set("PHONE", "RING", 60);
        Assert.NotEqual(before, state.ComputeHash());

        state.Timers.Advance(60);
        Assert.Equal(before, state.ComputeHash());
    }

    [Fact]
    public void A_timer_names_the_action_rather_than_holding_it()
    {
        // Set while the door is shut, fired after it is opened: the rule that runs is the
        // one that applies then. That is the whole reason for keeping a name.
        var state = new GameState();
        var api = new Gk3SheepApi(state);
        var resolver = new ActionResolver(api);

        resolver.Add(GK3Reborn.Formats.Actions.NvcFile.Parse(
            """
            DOOR, KNOCK, 1ST_TIME, script={SetFlag("first")}
            DOOR, KNOCK, OTR_TIME, script={SetFlag("again")}
            """,
            "test.nvc",
            new GK3Reborn.Foundation.Diagnostics.DiagnosticBag()));

        state.Timers.Set("DOOR", "KNOCK", 5);

        // The story moves on before the timer comes due.
        state.IncrementNounVerbCount("DOOR", "KNOCK");

        GameTimer due = Assert.Single(state.Timers.Advance(5));
        var runner = new ActionRunner(api);
        runner.Run(resolver.Find(due.Noun, due.Verb)!);

        Assert.True(state.GetFlag("again"));
        Assert.False(state.GetFlag("first"));
    }
}
