using GK3Reborn.Game;
using GK3Reborn.Sheep;
using Xunit;

namespace GK3Reborn.Tests.Sheep;

/// <summary>
/// Tests for a script that is waiting for something.
/// </summary>
/// <remarks>
/// The machine has always parked a thread properly and nothing ever kept it parked, so
/// every wait was over as soon as it began. What matters here is that a script now takes
/// as long as the calls it waited on, and that a host with nothing to wait against still
/// runs it straight through.
/// </remarks>
public sealed class SheepSchedulerTests
{
    /// <summary>A host where one call takes time and the rest do not.</summary>
    private sealed class Slow : ISheepApi
    {
        public List<string> Calls { get; } = [];

        public SheepValue Invoke(string name, IReadOnlyList<SheepValue> arguments)
        {
            Calls.Add(name);
            return SheepValue.FromInt(0);
        }

        public bool IsWaitable(string name) => true;

        public double SecondsFor(string name, IReadOnlyList<SheepValue> arguments) =>
            name.Equals("Sleep", StringComparison.OrdinalIgnoreCase) ? 2.0 : 0;
    }

    /// <summary>A script that waits on one call, then makes another.</summary>
    private static SheepScriptFile Script()
    {
        var builder = new ScriptBuilder().Import("Sleep").Import("Then");

        return builder
            .Function("Main$")
            .Op(SheepOpcode.BeginWait)
            .Op(SheepOpcode.PushI, 0)
            .Op(SheepOpcode.CallSysFunctionV, 0)
            .Op(SheepOpcode.Pop)
            .Op(SheepOpcode.EndWait)
            .Op(SheepOpcode.PushI, 0)
            .Op(SheepOpcode.CallSysFunctionV, 1)
            .Op(SheepOpcode.Pop)
            .Op(SheepOpcode.ReturnV)
            .Build();
    }

    [Fact]
    public void A_parked_script_waits_as_long_as_the_call_it_waited_on()
    {
        var api = new Slow();
        var vm = new SheepVirtualMachine(api);
        var scheduler = new SheepScheduler(vm);

        SheepThread thread = vm.Execute(Script(), "Main$");

        Assert.Equal(SheepThreadState.Blocked, thread.State);
        Assert.True(scheduler.Park(thread));
        Assert.Equal(["Sleep"], api.Calls);

        // Nowhere near two seconds, so it is still waiting.
        Assert.Empty(scheduler.Advance(1.0));
        Assert.Equal(["Sleep"], api.Calls);
        Assert.Equal(1, scheduler.Count);

        Assert.Single(scheduler.Advance(1.5));
        Assert.Equal(["Sleep", "Then"], api.Calls);
        Assert.Equal(0, scheduler.Count);
        Assert.Equal(SheepThreadState.Completed, thread.State);
    }

    [Fact]
    public void A_script_that_is_not_waiting_is_not_parked()
    {
        var vm = new SheepVirtualMachine(new Slow());
        var scheduler = new SheepScheduler(vm);

        SheepScriptFile straight = new ScriptBuilder()
            .Function("Main$")
            .Op(SheepOpcode.ReturnV)
            .Build();

        Assert.False(scheduler.Park(vm.Execute(straight, "Main$")));
        Assert.Equal(0, scheduler.Count);
    }

    [Fact]
    public void A_host_with_nothing_to_wait_against_runs_a_script_straight_through()
    {
        // Which is what every tool needs: a sweep of the corpus has no clock and no reason
        // to want one.
        var api = new Slow();
        var host = new ScriptHost(new Gk3SheepApi(new GameState()));

        SheepVirtualMachine vm = new(api);
        SheepThread thread = vm.Execute(Script(), "Main$");

        Assert.Equal(SheepThreadState.Blocked, thread.State);

        SheepVirtualMachine.NotifyWaitsCompleted(thread);
        vm.Resume(thread);

        Assert.Equal(["Sleep", "Then"], api.Calls);
        Assert.Null(host.Scheduler);
    }

    [Fact]
    public void A_wait_block_is_over_when_its_slowest_call_is()
    {
        var api = new Slow();
        var vm = new SheepVirtualMachine(api);

        // Waits on a quick call and then a slow one, in that order.
        SheepScriptFile both = new ScriptBuilder()
            .Import("Then")
            .Import("Sleep")
            .Function("Main$")
            .Op(SheepOpcode.BeginWait)
            .Op(SheepOpcode.PushI, 0)
            .Op(SheepOpcode.CallSysFunctionV, 0)
            .Op(SheepOpcode.Pop)
            .Op(SheepOpcode.PushI, 0)
            .Op(SheepOpcode.CallSysFunctionV, 1)
            .Op(SheepOpcode.Pop)
            .Op(SheepOpcode.EndWait)
            .Op(SheepOpcode.ReturnV)
            .Build();

        SheepThread thread = vm.Execute(both, "Main$");

        Assert.Equal(2.0, thread.WaitSeconds, 3);
    }

    [Fact]
    public void Changing_room_gives_up_on_whatever_was_waiting()
    {
        var vm = new SheepVirtualMachine(new Slow());
        var scheduler = new SheepScheduler(vm);

        scheduler.Park(vm.Execute(Script(), "Main$"));
        Assert.Equal(1, scheduler.Count);

        scheduler.Clear();

        Assert.Equal(0, scheduler.Count);
        Assert.Empty(scheduler.Advance(10));
    }
}
