using System.Buffers.Binary;
using System.Text;
using GK3Reborn.Sheep;
using Xunit;

namespace GK3Reborn.Tests.Sheep;

public sealed class SheepVirtualMachineTests
{
    /// <summary>An API that answers with whatever the test tells it to.</summary>
    private sealed class StubApi(int result = 0, params string[] waitable) : ISheepApi
    {
        public List<SheepCall> Calls { get; } = [];

        public SheepValue Invoke(string name, IReadOnlyList<SheepValue> arguments)
        {
            Calls.Add(new SheepCall(name, arguments, false));
            return SheepValue.FromInt(result);
        }

        public bool IsWaitable(string name) => waitable.Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_call_receives_its_arguments_in_order()
    {
        var builder = new ScriptBuilder().Import("SetFlag", 0, 3, 1);
        int offset = builder.String("noon");

        SheepScriptFile script = builder
            .Function("Main$")
            .Op(SheepOpcode.PushS, offset)
            .Op(SheepOpcode.GetString)
            .Op(SheepOpcode.PushI, 42)
            .Op(SheepOpcode.PushI, 2)          // argument count
            .Op(SheepOpcode.CallSysFunctionV, 0)
            .Op(SheepOpcode.Pop)
            .Op(SheepOpcode.ReturnV)
            .Build();

        var api = new StubApi();
        SheepThread thread = new SheepVirtualMachine(api).Execute(script, "Main$");

        Assert.Equal(SheepThreadState.Completed, thread.State);
        SheepCall call = Assert.Single(api.Calls);
        Assert.Equal("SetFlag", call.Name);
        Assert.Equal("noon", call.Arguments[0].AsString());
        Assert.Equal(42, call.Arguments[1].AsInt());
    }

    [Fact]
    public void A_void_call_still_pushes_a_result()
    {
        // The original compiler emits a Pop after every void call, so a VM that pushed
        // nothing would drift the stack by one on each call.
        SheepScriptFile script = new ScriptBuilder()
            .Import("DoThing")
            .Function("Main$")
            .Op(SheepOpcode.PushI, 0)
            .Op(SheepOpcode.CallSysFunctionV, 0)
            .Op(SheepOpcode.Pop)
            .Op(SheepOpcode.PushI, 0)
            .Op(SheepOpcode.CallSysFunctionV, 0)
            .Op(SheepOpcode.Pop)
            .Op(SheepOpcode.ReturnV)
            .Build();

        var api = new StubApi();
        SheepThread thread = new SheepVirtualMachine(api).Execute(script, "Main$");

        Assert.Equal(SheepThreadState.Completed, thread.State);
        Assert.Equal(2, api.Calls.Count);
    }

    [Fact]
    public void Arithmetic_and_comparison_work()
    {
        var builder = new ScriptBuilder().Import("Report", 0, 1);

        SheepScriptFile script = builder
            .Function("Main$")
            .Op(SheepOpcode.PushI, 7)
            .Op(SheepOpcode.PushI, 3)
            .Op(SheepOpcode.SubtractI)     // 4
            .Op(SheepOpcode.PushI, 5)
            .Op(SheepOpcode.MultiplyI)     // 20
            .Op(SheepOpcode.PushI, 1)
            .Op(SheepOpcode.CallSysFunctionV, 0)
            .Op(SheepOpcode.Pop)
            .Op(SheepOpcode.ReturnV)
            .Build();

        var api = new StubApi();
        new SheepVirtualMachine(api).Execute(script, "Main$");

        Assert.Equal(20, Assert.Single(api.Calls).Arguments[0].AsInt());
    }

    [Fact]
    public void Division_by_zero_yields_zero_rather_than_taking_the_engine_down()
    {
        // Scripts come from data the project does not control.
        var builder = new ScriptBuilder().Import("Report", 0, 1);

        SheepScriptFile script = builder
            .Function("Main$")
            .Op(SheepOpcode.PushI, 10)
            .Op(SheepOpcode.PushI, 0)
            .Op(SheepOpcode.DivideI)
            .Op(SheepOpcode.PushI, 1)
            .Op(SheepOpcode.CallSysFunctionV, 0)
            .Op(SheepOpcode.Pop)
            .Op(SheepOpcode.ReturnV)
            .Build();

        var api = new StubApi();
        SheepThread thread = new SheepVirtualMachine(api).Execute(script, "Main$");

        Assert.Equal(SheepThreadState.Completed, thread.State);
        Assert.Equal(0, Assert.Single(api.Calls).Arguments[0].AsInt());
    }

    [Fact]
    public void BranchIfZero_takes_the_branch_only_when_the_test_is_false()
    {
        var builder = new ScriptBuilder().Import("Taken").Import("Skipped");

        builder.Function("Main$").Op(SheepOpcode.PushI, 0);
        int branch = builder.Here;
        builder.Op(SheepOpcode.BranchIfZero, 0);
        builder.Op(SheepOpcode.PushI, 0).Op(SheepOpcode.CallSysFunctionV, 1).Op(SheepOpcode.Pop);
        int target = builder.Here;
        builder.Op(SheepOpcode.PushI, 0).Op(SheepOpcode.CallSysFunctionV, 0).Op(SheepOpcode.Pop);
        builder.Op(SheepOpcode.ReturnV);
        builder.Patch(branch, target);

        var api = new StubApi();
        new SheepVirtualMachine(api).Execute(builder.Build(), "Main$");

        Assert.Equal(["Taken"], api.Calls.Select(c => c.Name));
    }

    [Fact]
    public void Variables_start_at_their_declared_values_and_round_trip()
    {
        var builder = new ScriptBuilder()
            .Import("Report", 0, 1)
            .Variable("count$", SheepValueKind.Int, intValue: 5);

        SheepScriptFile script = builder
            .Function("Main$")
            .Op(SheepOpcode.LoadI, 0)
            .Op(SheepOpcode.PushI, 3)
            .Op(SheepOpcode.AddI)
            .Op(SheepOpcode.StoreI, 0)
            .Op(SheepOpcode.LoadI, 0)
            .Op(SheepOpcode.PushI, 1)
            .Op(SheepOpcode.CallSysFunctionV, 0)
            .Op(SheepOpcode.Pop)
            .Op(SheepOpcode.ReturnV)
            .Build();

        var api = new StubApi();
        new SheepVirtualMachine(api).Execute(script, "Main$");

        Assert.Equal(8, Assert.Single(api.Calls).Arguments[0].AsInt());
    }

    [Fact]
    public void A_wait_block_suspends_only_when_it_called_something_waitable()
    {
        var builder = new ScriptBuilder().Import("WalkTo").Import("SetFlag");

        SheepScriptFile script = builder
            .Function("Main$")
            .Op(SheepOpcode.BeginWait)
            .Op(SheepOpcode.PushI, 0)
            .Op(SheepOpcode.CallSysFunctionV, 0)
            .Op(SheepOpcode.Pop)
            .Op(SheepOpcode.EndWait)
            .Op(SheepOpcode.ReturnV)
            .Build();

        SheepThread waiting = new SheepVirtualMachine(new StubApi(0, "WalkTo")).Execute(script, "Main$");
        Assert.Equal(SheepThreadState.Blocked, waiting.State);
        Assert.True(Assert.Single(waiting.Calls).Waited);

        SheepThread notWaiting = new SheepVirtualMachine(new StubApi()).Execute(script, "Main$");
        Assert.Equal(SheepThreadState.Completed, notWaiting.State);
    }

    [Fact]
    public void A_blocked_thread_resumes_where_it_stopped()
    {
        var builder = new ScriptBuilder().Import("WalkTo").Import("Arrived");

        SheepScriptFile script = builder
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

        var api = new StubApi(0, "WalkTo");
        var vm = new SheepVirtualMachine(api);

        SheepThread thread = vm.Execute(script, "Main$");
        Assert.Equal(SheepThreadState.Blocked, thread.State);
        Assert.Equal(["WalkTo"], api.Calls.Select(c => c.Name));

        vm.Resume(thread);
        Assert.Equal(SheepThreadState.Completed, thread.State);
        Assert.Equal(["WalkTo", "Arrived"], api.Calls.Select(c => c.Name));
    }

    [Fact]
    public void SitnSpin_halts_deliberately()
    {
        SheepScriptFile script = new ScriptBuilder()
            .Function("Main$")
            .Op(SheepOpcode.SitnSpin)
            .Build();

        SheepThread thread = new SheepVirtualMachine(new StubApi()).Execute(script, "Main$");
        Assert.Equal(SheepThreadState.Halted, thread.State);
    }

    [Fact]
    public void An_endless_loop_faults_instead_of_hanging()
    {
        var builder = new ScriptBuilder();
        builder.Function("Main$");
        int top = builder.Here;
        builder.Op(SheepOpcode.Branch, top);

        SheepThread thread = new SheepVirtualMachine(new StubApi(), instructionLimit: 500)
            .Execute(builder.Build(), "Main$");

        Assert.Equal(SheepThreadState.Faulted, thread.State);
        Assert.Equal("GK3R3101", thread.Diagnostics.Items[0].Code);
    }

    [Fact]
    public void A_branch_past_the_end_faults_rather_than_reading_wild_memory()
    {
        SheepScriptFile script = new ScriptBuilder()
            .Function("Main$")
            .Op(SheepOpcode.Branch, 9999)
            .Build();

        SheepThread thread = new SheepVirtualMachine(new StubApi()).Execute(script, "Main$");

        Assert.Equal(SheepThreadState.Faulted, thread.State);
        Assert.Equal("GK3R3102", thread.Diagnostics.Items[0].Code);
    }

    [Fact]
    public void Function_names_are_matched_case_insensitively()
    {
        // The language specification says upper and lower case are the same.
        SheepScriptFile script = new ScriptBuilder()
            .Function("JeanTalk$")
            .Op(SheepOpcode.ReturnV)
            .Build();

        var vm = new SheepVirtualMachine(new StubApi());
        Assert.Equal(SheepThreadState.Completed, vm.Execute(script, "jeantalk$").State);
        Assert.Equal(SheepThreadState.Completed, vm.Execute(script, "JEANTALK$").State);
    }

    [Fact]
    public void Calling_a_function_that_does_not_exist_is_reported()
    {
        SheepScriptFile script = new ScriptBuilder()
            .Function("Main$")
            .Op(SheepOpcode.ReturnV)
            .Build();

        SheepThread thread = new SheepVirtualMachine(new StubApi()).Execute(script, "Missing$");

        Assert.Equal(SheepThreadState.Faulted, thread.State);
        Assert.Equal("GK3R3100", thread.Diagnostics.Items[0].Code);
    }

    [Fact]
    public void IToF_converts_the_value_the_operand_points_at()
    {
        // The operand says how far down the stack to reach, not that the top is meant.
        var builder = new ScriptBuilder().Import("Report", 0, 2, 1);

        SheepScriptFile script = builder
            .Function("Main$")
            .Op(SheepOpcode.PushI, 7)
            .Op(SheepOpcode.PushI, 9)
            .Op(SheepOpcode.IToF, 1)      // convert the 7, one below the top
            .Op(SheepOpcode.PushI, 2)
            .Op(SheepOpcode.CallSysFunctionV, 0)
            .Op(SheepOpcode.Pop)
            .Op(SheepOpcode.ReturnV)
            .Build();

        var api = new StubApi();
        new SheepVirtualMachine(api).Execute(script, "Main$");

        SheepCall call = Assert.Single(api.Calls);
        Assert.Equal(SheepValueKind.Float, call.Arguments[0].Kind);
        Assert.Equal(SheepValueKind.Int, call.Arguments[1].Kind);
    }
}
