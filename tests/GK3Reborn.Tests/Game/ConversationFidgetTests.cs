using System.Numerics;
using System.Text;
using GK3Reborn.Content;
using GK3Reborn.Formats.Animation;
using GK3Reborn.Formats.Models;
using GK3Reborn.Formats.Scenes;
using GK3Reborn.Game;
using GK3Reborn.Game.Actors;
using GK3Reborn.Rendering;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Tests for what a scene's <c>[LISTENERS]</c> section does to the people in it.
/// </summary>
/// <remarks>
/// An actor's own <c>talk</c> and <c>listen</c> scripts are what they do in any
/// conversation; a scene's listener lines are what they do in <em>one</em> of them, and 237
/// lines across 75 rooms say so. The pairing that matters is enter and exit: the enter
/// animation is what leans Mosely on the Armorer's counter, and without its exit he is
/// still leaning on it for the rest of the afternoon.
/// </remarks>
public sealed class ConversationFidgetTests
{
    private static List<string> Played(SceneUpdate update) =>
        [.. update.Diagnostics.Items
            .Where(d => string.Equals(d.Code, "GK3R3313", StringComparison.Ordinal))
            .Select(d => d.File ?? string.Empty)];

    private static GasFile Script(string text) => GasFile.Parse(Encoding.Latin1.GetBytes(text));

    private static ModFile Person(string name) => ModFile.FromMeshes(
        name,
        [
            new ModMesh
            {
                MeshToLocal = Matrix4x4.Identity,
                BoundsMin = Vector3.Zero,
                BoundsMax = Vector3.One,
                Submeshes =
                [
                    new ModSubmesh
                    {
                        TextureName = name + "_FACE",
                        Color = (255, 255, 255),
                        Positions = [Vector3.Zero],
                        Normals = [Vector3.UnitY],
                        TexCoords = [Vector2.Zero],
                        Indices = [0, 0, 0],
                    },
                ],
            },
        ]);

    private const string Listeners = """
        [ROOM_CAMERAS]
        A, angle={0,0}, pos={0,0,0}, Default

        [LISTENERS]
        dialogue=MOSELYCHAT, actor=MOSELY, talk=mosArmTalk.gas, listen=mosArmListen.gas, enter=MosArmLeanB, exit=MosArmLeanA
        dialogue=SOMEBODY_ELSE, actor=MOSELY, talk=other.gas, listen=other.gas
        """;

    private static (SceneUpdate World, GameState State, Gk3SheepApi Api, PlacedModel Mosely, LoadedScene Scene) World()
    {
        var mosely = new PlacedModel(
            "mos", "MOSELY", null, Person("mos"), Matrix4x4.Identity,
            PlacedModelKind.Actor, new ModelPlacement(0))
        {
            Talk = Script("ANIM mosTalkOrdinary\nloop\n"),
            Listen = Script("ANIM mosListenOrdinary\nloop\n"),
            Idle = Script("ANIM mosIdle\nloop\n"),
        };

        var scene = new LoadedScene(
            "TEST",
            new SceneDefinition(SceneInitFile.Parse(Listeners, "T.SIF")),
            Asset: null,
            Lightmaps: null,
            ModelsPlaced: 1,
            Placed: [mosely]);

        var state = new GameState();
        var api = new Gk3SheepApi(state);

        var update = new SceneUpdate(scene, api, new Glances(), new HeadlessSceneSink())
        {
            Animations = new AnimationLibrary(_ => "[HEADER]\n30\n"),
            Clips = new ClipLibrary(_ => null),

            // What reads a named script. The conversation's own scripts are named rather
            // than carried, so without this a listener line can change nothing.
            Behaviours = named => Script($"ANIM {named.Replace(".gas", string.Empty, StringComparison.OrdinalIgnoreCase)}Fidget\nloop\n"),
        };

        update.StartScenery();

        return (update, state, api, mosely, scene);
    }

    [Fact]
    public void TheSceneKnowsWhichActorsAConversationChanges()
    {
        (SceneUpdate world, _, _, _, LoadedScene scene) = World();

        Assert.Equal(2, scene.Definition.Conversations().Count);

        SceneConversation first = scene.Definition.Conversations()[0];

        Assert.Equal("MOSELYCHAT", first.Conversation);
        Assert.Equal("MOSELY", first.Actor);
        Assert.Equal("mosArmTalk.gas", first.Talk);
        Assert.Equal("MosArmLeanB", first.Enter);
        Assert.Equal("MosArmLeanA", first.Exit);
    }

    [Fact]
    public void EnteringAConversationHandsTheActorItsOwnScripts()
    {
        (SceneUpdate world, _, _, PlacedModel mosely, _) = World();

        world.EnterConversation("MOSELYCHAT");

        // The scripts came from the conversation, not from the actor's own line.
        Assert.Contains("mosArmTalkFidget", Names(mosely.Talk));
        Assert.Contains("mosArmListenFidget", Names(mosely.Listen));

        static IEnumerable<string> Names(GasFile? script) =>
            script?.Steps.Select(s => s.Name ?? string.Empty) ?? [];
    }

    [Fact]
    public void AndPlaysTheAnimationThatPutsThemIntoIt()
    {
        (SceneUpdate world, _, _, _, LoadedScene scene) = World();

        world.EnterConversation("MOSELYCHAT");

        Assert.Contains("MosArmLeanB", Played(world));
    }

    [Fact]
    public void LeavingItPlaysTheOneThatUndoesIt()
    {
        (SceneUpdate world, _, _, _, LoadedScene scene) = World();

        world.EnterConversation("MOSELYCHAT");
        world.LeaveConversation();

        Assert.Contains("MosArmLeanA", Played(world));
    }

    [Fact]
    public void AndGivesTheActorTheirOwnScriptsBack()
    {
        (SceneUpdate world, _, _, PlacedModel mosely, _) = World();

        GasFile? talk = mosely.Talk;
        GasFile? listen = mosely.Listen;

        world.EnterConversation("MOSELYCHAT");
        world.LeaveConversation();

        Assert.Same(talk, mosely.Talk);
        Assert.Same(listen, mosely.Listen);
    }

    [Fact]
    public void ASecondConversationEndsTheFirstRatherThanStackingOnIt()
    {
        // Otherwise the first conversation's pose is still on everybody it named, and its
        // exit animation has nothing to undo.
        (SceneUpdate world, _, _, PlacedModel mosely, _) = World();

        GasFile? talk = mosely.Talk;

        world.EnterConversation("MOSELYCHAT");
        world.EnterConversation("SOMEBODY_ELSE");

        Assert.Contains("MosArmLeanA", Played(world));
        Assert.Equal("SOMEBODY_ELSE", world.Conversation);

        world.LeaveConversation();

        Assert.Same(talk, mosely.Talk);
        Assert.Null(world.Conversation);
    }

    [Fact]
    public void AConversationNobodyIsNamedInChangesNothing()
    {
        (SceneUpdate world, _, _, PlacedModel mosely, _) = World();

        GasFile? talk = mosely.Talk;

        world.EnterConversation("A_CONVERSATION_THE_SCENE_NEVER_HEARD_OF");

        Assert.Same(talk, mosely.Talk);
        Assert.Equal("A_CONVERSATION_THE_SCENE_NEVER_HEARD_OF", world.Conversation);
    }

    [Fact]
    public void TheStoryAndTheRoomAgreeAboutWhichConversationIsOn()
    {
        // Both halves are set through the same call: the story's record survives a save
        // and the room's is what the actors are doing, and a save reloaded into a room
        // where nobody is leaning on anything is the failure this pins.
        (SceneUpdate world, GameState state, Gk3SheepApi api, _, LoadedScene scene) = World();

        SceneScripting.Attach(
            api,
            scene,
            world: world,
            behaviours: named => Script($"ANIM {named}\nloop\n"));

        api.Invoke("SetConversation", [GK3Reborn.Sheep.SheepValue.FromString("MOSELYCHAT")]);

        Assert.Equal("MOSELYCHAT", state.Conversation);
        Assert.Equal("MOSELYCHAT", world.Conversation);

        api.Invoke("EndConversation", []);

        Assert.Null(state.Conversation);
        Assert.Null(world.Conversation);
    }
}
