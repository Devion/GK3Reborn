using GK3Reborn.Formats.Actions;
using GK3Reborn.Formats.Scenes;
using GK3Reborn.Foundation.Diagnostics;
using GK3Reborn.Game;
using GK3Reborn.Sheep;
using GK3Reborn.Tests.Sheep;
using Xunit;

namespace GK3Reborn.Tests.Game;

public sealed class DialogueRegressionTests
{
    private sealed class Timed : ISheepApi
    {
        public int Marks { get; private set; }
        public double AnimationTime { get; private set; }
        public SheepValue Invoke(string name, IReadOnlyList<SheepValue> arguments)
        {
            if (name == "Mark") { Marks++; }
            return SheepValue.FromInt(0);
        }
        public bool IsWaitable(string name) => name != "Mark";
        public double SecondsFor(string name, IReadOnlyList<SheepValue> arguments) =>
            SheepScheduler.IsDialogue(name) ? 10 : 4;
        public SheepWaitWork? CaptureWait(string name) => name == "StartAnimation"
            ? new SheepWaitWork(seconds => AnimationTime += seconds) : null;
    }

    private static ScriptBuilder Wait(ScriptBuilder builder, int import) => builder
        .Op(SheepOpcode.BeginWait).Op(SheepOpcode.PushI, 0)
        .Op(SheepOpcode.CallSysFunctionV, import).Op(SheepOpcode.Pop).Op(SheepOpcode.EndWait);

    [Theory]
    [InlineData("StartDialogue")]
    [InlineData("StartDialogueNoFidgets")]
    [InlineData("ContinueDialogue")]
    [InlineData("ContinueDialogueNoFidgets")]
    [InlineData("StartVoiceOver")]
    [InlineData("StartYak")]
    public void All_speech_calls_advance_their_companion_animation_but_not_background_work(string dialogue)
    {
        var builder = new ScriptBuilder().Import(dialogue).Import("StartAnimation");
        builder.Function("Main$").Op(SheepOpcode.BeginWait)
            .Op(SheepOpcode.PushI, 0).Op(SheepOpcode.CallSysFunctionV, 0).Op(SheepOpcode.Pop)
            .Op(SheepOpcode.PushI, 0).Op(SheepOpcode.CallSysFunctionV, 1).Op(SheepOpcode.Pop)
            .Op(SheepOpcode.EndWait).Op(SheepOpcode.ReturnV);
        Wait(builder.Function("Background$"), 1).Op(SheepOpcode.ReturnV);
        var api = new Timed();
        var vm = new SheepVirtualMachine(api);
        var scheduler = new SheepScheduler(vm);
        SheepScriptFile script = builder.Build("ANY_SCENE.SHP");
        SheepThread background = vm.Execute(script, "Background$");
        SheepThread speaking = vm.Execute(script, "Main$");
        scheduler.Park(background);
        scheduler.Park(speaking);
        scheduler.Advance(1);
        scheduler.SkipDialogue(9, speaking);
        scheduler.Advance(0);
        Assert.Equal(SheepThreadState.Completed, speaking.State);
        Assert.Equal(SheepThreadState.Blocked, background.State);
        Assert.Equal(3, api.AnimationTime);
    }

    [Theory]
    [InlineData("SetTimerSeconds")]
    [InlineData("WalkTo")]
    public void Explicit_non_animation_waits_are_preserved(string call)
    {
        var builder = new ScriptBuilder().Import("StartDialogue").Import(call);
        builder.Function("Main$").Op(SheepOpcode.BeginWait)
            .Op(SheepOpcode.PushI, 0).Op(SheepOpcode.CallSysFunctionV, 0).Op(SheepOpcode.Pop)
            .Op(SheepOpcode.PushI, 0).Op(SheepOpcode.CallSysFunctionV, 1).Op(SheepOpcode.Pop)
            .Op(SheepOpcode.EndWait).Op(SheepOpcode.ReturnV);
        var vm = new SheepVirtualMachine(new Timed());
        var scheduler = new SheepScheduler(vm);
        SheepThread thread = vm.Execute(builder.Build(), "Main$");
        scheduler.Park(thread);
        scheduler.Advance(1);
        scheduler.SkipDialogue(9, thread);
        scheduler.Advance(0);
        Assert.Equal(SheepThreadState.Blocked, thread.State);
        scheduler.Advance(3);
        Assert.Equal(SheepThreadState.Completed, thread.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Awaited_helpers_execute_their_completion_code_and_stop_at_the_next_speech(bool nextSpeech)
    {
        var builder = new ScriptBuilder().Import("StartDialogue").Import("StartAnimation").Import("Mark");
        Wait(builder.Function("Main$"), 0).Op(SheepOpcode.ReturnV);
        Wait(builder.Function("ArbitraryHelper$"), 1);
        Wait(builder, 1);
        Wait(builder, 1);
        builder.Op(SheepOpcode.PushI, 0).Op(SheepOpcode.CallSysFunctionV, 2).Op(SheepOpcode.Pop);
        if (nextSpeech) { Wait(builder, 0); }
        builder.Op(SheepOpcode.ReturnV);
        var api = new Timed();
        var vm = new SheepVirtualMachine(api);
        var scheduler = new SheepScheduler(vm);
        SheepScriptFile script = builder.Build("UNNAMED.SHP");
        SheepThread child = vm.Execute(script, "ArbitraryHelper$");
        SheepThread parent = vm.Execute(script, "Main$");
        scheduler.Park(child);
        scheduler.Park(parent, [child]);
        scheduler.SkipDialogue(10, parent);
        scheduler.Advance(0);
        Assert.Equal(1, api.Marks);
        Assert.Equal(12, api.AnimationTime);
        Assert.Equal(nextSpeech ? SheepThreadState.Blocked : SheepThreadState.Completed, child.State);
        Assert.Equal(nextSpeech ? SheepThreadState.Blocked : SheepThreadState.Completed, parent.State);
    }

    [Fact]
    public void Skipping_one_of_several_lines_does_not_complete_the_run_or_another_speakers_wait()
    {
        var builder = new ScriptBuilder().Import("StartDialogue");
        Wait(builder.Function("Main$"), 0).Op(SheepOpcode.ReturnV);
        var vm = new SheepVirtualMachine(new Timed());
        var scheduler = new SheepScheduler(vm);
        SheepScriptFile script = builder.Build();
        SheepThread first = vm.Execute(script, "Main$");
        SheepThread second = vm.Execute(script, "Main$");
        scheduler.Park(first);
        scheduler.Park(second);
        scheduler.SkipDialogue(4, first, lastLine: false);
        scheduler.Advance(0);
        Assert.Equal(SheepThreadState.Blocked, first.State);
        scheduler.SkipDialogue(6, first);
        scheduler.Advance(0);
        Assert.Equal(SheepThreadState.Completed, first.State);
        Assert.Equal(SheepThreadState.Blocked, second.State);
    }

    [Fact]
    public void A_nested_speaker_advances_the_animation_waiting_alongside_its_call()
    {
        var builder = new ScriptBuilder().Import("StartDialogue").Import("StartAnimation");
        Wait(builder.Function("Outer$"), 1).Op(SheepOpcode.ReturnV);
        Wait(builder.Function("Speaking$"), 0).Op(SheepOpcode.ReturnV);
        var api = new Timed();
        var vm = new SheepVirtualMachine(api);
        var scheduler = new SheepScheduler(vm);
        var script = builder.Build();
        SheepThread child = vm.Execute(script, "Speaking$");
        SheepThread parent = vm.Execute(script, "Outer$");
        scheduler.Park(child);
        scheduler.Park(parent, [child]);
        scheduler.SkipDialogue(10, child);
        scheduler.Advance(0);
        scheduler.Advance(0);
        Assert.Equal(SheepThreadState.Completed, parent.State);
        Assert.Equal(4, api.AnimationTime);
    }

    [Fact]
    public void A_helper_timer_is_not_spent_when_the_last_line_is_skipped()
    {
        var builder = new ScriptBuilder().Import("StartDialogue").Import("StartAnimation")
            .Import("SetTimerSeconds").Import("Mark");
        Wait(builder.Function("Main$"), 0).Op(SheepOpcode.ReturnV);
        Wait(builder.Function("Helper$"), 1);
        Wait(builder, 2);
        builder.Op(SheepOpcode.PushI, 0).Op(SheepOpcode.CallSysFunctionV, 3).Op(SheepOpcode.Pop)
            .Op(SheepOpcode.ReturnV);
        var api = new Timed();
        var vm = new SheepVirtualMachine(api);
        var scheduler = new SheepScheduler(vm);
        var script = builder.Build();
        SheepThread child = vm.Execute(script, "Helper$");
        SheepThread parent = vm.Execute(script, "Main$");
        scheduler.Park(child);
        scheduler.Park(parent, [child]);
        scheduler.SkipDialogue(10, parent);
        scheduler.Advance(0);
        Assert.Equal(SheepThreadState.Blocked, child.State);
        Assert.Equal(0, api.Marks);
        scheduler.Advance(4);
        Assert.Equal(1, api.Marks);
    }

    [Fact]
    public void A_direct_topic_runs_the_authored_talk_setup_once()
    {
        var state = new GameState();
        var api = new Gk3SheepApi(state);
        var rules = new ActionResolver(api);
        rules.Add(NvcFile.Parse("""
            ABBE, TALK, ALL, script={SetConversation("Seated");}
            ABBE, T_INTRODUCE, ALL, script={SetFlag("Introduced");}
            """, "TEST.NVC", new DiagnosticBag()));
        var scene = new LoadedScene("TEST", new SceneDefinition(SceneInitFile.Parse("[GENERAL]", "TEST.SIF")),
            null, null, 0, Actions: rules);
        int prepared = 0;
        api.Register("SetConversation", args =>
        {
            prepared++;
            state.Conversation = args[0].AsString();
            return SheepValue.FromInt(0);
        });
        var interaction = new SceneInteraction(scene, api);
        Assert.True(interaction.Do("ABBE", "T_INTRODUCE")!.Ran);
        Assert.Equal("Seated", state.Conversation);
        interaction.Do("ABBE", "T_INTRODUCE");
        Assert.Equal(1, prepared);
        state.Conversation = null;
        interaction.Do("ABBE", "T_INTRODUCE");
        Assert.Equal(2, prepared);
    }
}
