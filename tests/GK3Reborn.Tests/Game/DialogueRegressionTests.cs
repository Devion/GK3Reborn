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
        public SheepValue Invoke(string name, IReadOnlyList<SheepValue> arguments) => SheepValue.FromInt(0);
        public bool IsWaitable(string name) => true;
        public double SecondsFor(string name, IReadOnlyList<SheepValue> arguments) =>
            name == "StartDialogue" ? 10 : 4;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Skipping_speech_preserves_other_calls_in_the_same_wait_block(bool animation)
    {
        var builder = new ScriptBuilder().Import("StartDialogue").Import("StartAnimation");
        builder.Function("Main$").Op(SheepOpcode.BeginWait)
            .Op(SheepOpcode.PushI, 0).Op(SheepOpcode.CallSysFunctionV, 0).Op(SheepOpcode.Pop);
        if (animation)
        {
            builder.Op(SheepOpcode.PushI, 0).Op(SheepOpcode.CallSysFunctionV, 1).Op(SheepOpcode.Pop);
        }
        builder.Op(SheepOpcode.EndWait).Op(SheepOpcode.ReturnV);
        var vm = new SheepVirtualMachine(new Timed());
        var scheduler = new SheepScheduler(vm);
        SheepThread thread = vm.Execute(builder.Build(), "Main$");
        scheduler.Park(thread);
        scheduler.Advance(1);
        scheduler.SkipDialogue(9);
        scheduler.Advance(0);
        Assert.Equal(animation ? SheepThreadState.Blocked : SheepThreadState.Completed, thread.State);
        scheduler.Advance(3);
        Assert.Equal(SheepThreadState.Completed, thread.State);
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
