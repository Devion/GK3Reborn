using System.Numerics;
using GK3Reborn.Formats.Models;
using GK3Reborn.Formats.Scenes;
using GK3Reborn.Game;
using GK3Reborn.Game.Actors;
using GK3Reborn.Rendering;
using GK3Reborn.Sheep;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Tests for the calls the game makes that were being met with nothing.
/// </summary>
/// <remarks>
/// <para>
/// P4's exit criterion is that every API function is implemented or carries a recorded
/// exception. An unanswered <em>question</em> is the worse half of that: a script branches
/// on the answer, so a silent zero sends it down the wrong path and everything after is
/// wrong for a reason nothing records.
/// </para>
/// <para>
/// These are written in Sheep source and compiled, which is what the front end is for.
/// Hand-assembling bytecode to test a host was always the wrong way round.
/// </para>
/// </remarks>
public sealed class SheepApiSurfaceTests
{
    private static SheepSignatures Signatures(params (string Name, sbyte Returns, sbyte[] Args)[] functions)
    {
        var catalogue = new SheepSignatures();

        foreach ((string name, sbyte returns, sbyte[] args) in functions)
        {
            catalogue.Add(new SheepImport(name, returns, args));
        }

        return catalogue;
    }

    [Fact]
    public void Call_runs_a_function_of_the_script_that_asked()
    {
        // 190 of the game's calls are these, and the host is given a name and no context.
        // ARM202P cuts between Gabe_CU$, Mose_CU$, TwoShot$ and Overview$ that way, so a
        // recorded Call is a scene that plays with its camera never moving.
        var state = new GameState();
        var host = new ScriptHost(new Gk3SheepApi(state));

        host.Add(SheepCompiler.Compile(
            """
            code
            {
                Main$()   { Call("TwoShot"); }
                TwoShot$() { SetFlag("cut"); }
            }
            """,
            "ARM202P.SHP",
            Signatures(
                ("Call", SheepSignatures.Void, [SheepSignatures.String]),
                ("SetFlag", SheepSignatures.Void, [SheepSignatures.String]))));

        host.Run("ARM202P.SHP", "Main$");

        Assert.True(state.GetFlag("cut"));

        // The trace records the name as it was called. Scripts spell their own functions
        // with and without the dollar, and the machine matches either.
        Assert.Equal(["ARM202P:Main$", "ARM202P:TwoShot"], host.CallStackTrace);
    }

    [Fact]
    public void Call_reaches_the_script_that_is_running_rather_than_the_one_that_started()
    {
        // A called script's own Call has to stay inside it. Taking the outermost script
        // instead finds a function of the wrong name or the wrong one of the right name.
        var state = new GameState();
        var host = new ScriptHost(new Gk3SheepApi(state));

        SheepSignatures signatures = Signatures(
            ("Call", SheepSignatures.Void, [SheepSignatures.String]),
            ("CallSheep", SheepSignatures.Void, [SheepSignatures.String, SheepSignatures.String]),
            ("SetFlag", SheepSignatures.Void, [SheepSignatures.String]));

        host.Add(SheepCompiler.Compile(
            """
            code
            {
                Main$()  { CallSheep("INNER", "Enter$"); }
                Shot$()   { SetFlag("outer"); }
            }
            """,
            "OUTER.SHP", signatures));

        host.Add(SheepCompiler.Compile(
            """
            code
            {
                Enter$() { Call("Shot"); }
                Shot$()  { SetFlag("inner"); }
            }
            """,
            "INNER.SHP", signatures));

        host.Run("OUTER.SHP", "Main$");

        Assert.True(state.GetFlag("inner"));
        Assert.False(state.GetFlag("outer"));
    }

    private static ModFile Thing(string name, string texture) => ModFile.FromMeshes(
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
                        TextureName = texture,
                        Color = (255, 255, 255),
                        Positions = [Vector3.Zero],
                        Normals = [Vector3.UnitY],
                        TexCoords = [Vector2.Zero],
                        Indices = [0, 0, 0],
                    },
                ],
            },
        ]);

    private static (Gk3SheepApi Api, LoadedScene Scene) Room()
    {
        var scene = new LoadedScene(
            "TEST",
            new SceneDefinition(SceneInitFile.Parse("[MODELS]\nmodel=lamp, type=prop\n", "T.SIF")),
            Asset: null,
            Lightmaps: null,
            ModelsPlaced: 2,
            Placed:
            [
                new PlacedModel(
                    "gab", "GABRIEL", null, Thing("gab", "GAB_FACE"), Matrix4x4.Identity,
                    PlacedModelKind.Actor, new ModelPlacement(0)),
                new PlacedModel(
                    "lamp", "LAMP", null, Thing("lamp", "BRASS"), Matrix4x4.Identity,
                    PlacedModelKind.Prop, new ModelPlacement(1)),
            ]);

        var api = new Gk3SheepApi(new GameState());
        SceneScripting.Attach(api, scene);

        return (api, scene);
    }

    private static int Ask(Gk3SheepApi api, string function, string argument) =>
        api.Invoke(function, [SheepValue.FromString(argument)]).AsInt();

    [Fact]
    public void A_script_can_ask_whether_a_model_is_in_the_room()
    {
        (Gk3SheepApi api, _) = Room();

        Assert.Equal(1, Ask(api, "DoesModelExist", "lamp"));
        Assert.Equal(1, Ask(api, "DoesModelExist", "LAMP"));      // by noun, and case-blind
        Assert.Equal(0, Ask(api, "DoesModelExist", "wmo"));
    }

    [Fact]
    public void An_actor_is_a_model_but_a_model_is_not_an_actor()
    {
        (Gk3SheepApi api, _) = Room();

        Assert.Equal(1, Ask(api, "DoesActorExist", "gab"));
        Assert.Equal(1, Ask(api, "DoesActorExist", "GABRIEL"));
        Assert.Equal(0, Ask(api, "DoesActorExist", "lamp"));
        Assert.Equal(1, Ask(api, "DoesModelExist", "gab"));
    }

    [Fact]
    public void A_hit_test_can_be_switched_off_and_on_again()
    {
        var state = new GameState();
        var api = new Gk3SheepApi(state);

        api.Invoke("DisableHitTestModel", [SheepValue.FromString("lby_stairs")]);
        Assert.Contains("LBY_STAIRS", state.BlockedHitTests);

        api.Invoke("EnableHitTestModel", [SheepValue.FromString("LBY_STAIRS")]);
        Assert.Empty(state.BlockedHitTests);
    }

    [Fact]
    public void A_dialogue_camera_can_be_set_for_one_conversation_and_cleared_after_it()
    {
        var state = new GameState();
        var api = new Gk3SheepApi(state);

        api.Invoke("SetDefaultDialogueCamera", [SheepValue.FromString("GabMadWide")]);
        Assert.Equal("GabMadWide", state.DefaultDialogueCamera);

        // Cleared rather than left, or the next conversation opens on a shot framed for
        // two other people.
        api.Invoke("ClearDefaultDialogueCamera", []);
        Assert.Null(state.DefaultDialogueCamera);
    }

    [Fact]
    public void A_field_of_view_is_taken_in_degrees_and_an_impossible_one_is_refused()
    {
        (Gk3SheepApi api, _) = Room();

        api.Invoke("SetCameraFOV", [SheepValue.FromFloat(20f)]);
        Assert.Equal(20f * MathF.PI / 180f, api.State.CameraFieldOfView!.Value, 4);

        // Zero means the scene's own, not a pinhole.
        api.Invoke("SetCameraFOV", [SheepValue.FromFloat(0f)]);
        Assert.Null(api.State.CameraFieldOfView);
    }

    [Fact]
    public void A_count_can_be_raised_for_both_characters_at_once()
    {
        // The counts are per character, and a door that has been opened is open for
        // whoever walks in next.
        var state = new GameState();
        var api = new Gk3SheepApi(state);

        api.Invoke("IncNounVerbCountBoth",
            [SheepValue.FromString("DOOR"), SheepValue.FromString("OPEN")]);

        Assert.Equal(1, state.GetNounVerbCount("GABRIEL", "DOOR", "OPEN"));
        Assert.Equal(1, state.GetNounVerbCount("GRACE", "DOOR", "OPEN"));
    }

    [Fact]
    public void Every_function_the_specification_calls_gameplay_is_answered()
    {
        // The list is the 130 IMMEDIATE and WAIT entries of SHEEP ENGINE.DOC's function
        // reference, which is the conformance surface for completing the game; the other
        // 174 are DEVELOPMENT and belong to the console. Named here rather than read from
        // the workspace index, because that index is derived from copyrighted
        // documentation and does not ship.
        //
        // With a room standing, which is the state the criterion is about: a tool with no
        // scene deliberately leaves the calls that move things recorded, and always has.
        (Gk3SheepApi api, LoadedScene scene) = Room();

        _ = new ScriptHost(api);

        SceneScripting.Attach(
            api,
            scene,
            new Glances(),
            world: new SceneUpdate(scene, api, new Glances(), new HeadlessSceneSink()));

        string[] unanswered = [.. GameplayFunctions.Where(f => !api.Implements(f))];

        Assert.Empty(unanswered);
    }

    /// <summary>
    /// The specification's gameplay surface: everything it classifies IMMEDIATE or WAIT.
    /// </summary>
    private static readonly string[] GameplayFunctions =
    [
        "ActionWaitClearRegion", "AddCaptionVoiceOver", "AddModel", "AddActor", "AddPosition",
        "Blink", "BlinkX", "Call", "CallDefaultSheep", "CallGlobal", "CallGlobalSheep",
        "CallSceneFunction", "CallSheep", "CameraBoundaryBlockModel",
        "CameraBoundaryUnblockModel", "ClearDefaultDialogueCamera", "ClearFlag",
        "ClearModelShadowTexture", "ClearMood", "ClearPropGas", "ContinueDialogue",
        "ContinueDialogueNoFidgets", "CutToCameraAngle", "DefaultInspect",
        "DisableCameraBoundaries", "DisableCinematics", "DisableEyeJitter",
        "DisableHitTestModel", "DisableModelShadow", "DoesActorExist", "DoesEgoHaveInvItem",
        "DoesGabeHaveInvItem", "DoesGraceHaveInvItem", "DoesModelExist",
        "DoesSceneModelExist", "EnableCameraBoundaries", "EnableCinematics",
        "EnableEyeJitter", "EnableHitTestModel", "EnableModelShadow", "EyeJitter",
        "Expression", "FinishedScreen", "ForceCutToCameraAngle", "GetChatCount",
        "GetChatCountInt", "GetEgoCurrentLocationCount", "GetEgoLocationCount", "GetFlag",
        "GetGameVariableInt", "GetNounVerbCount", "GetRandomInt", "GetTopicCount",
        "Glance", "GlanceX", "GlideToCameraAngle", "HideInset", "HideModel",
        "HideModelGroup", "HidePlate", "HideSceneModel", "IncNounVerbCount",
        "IncNounVerbCountBoth", "IncreaseScore", "InitEgoPosition", "InventoryInspect",
        "IsActorAtLocation", "IsActorNear", "IsCameraGlideEnabled", "IsCurrentEgo",
        "IsCurrentTime", "LookitLock", "LookitModel", "LookitModelQuick",
        "LookitModelQuickX", "LookitModelX", "LookitNoun", "LookitNounQuick", "LookitPoint",
        "LookitUnlock", "LoopAnimation", "PlayFullScreenMovie", "PlayMovie", "PlaySound",
        "PlaySoundTrack", "ResetCaseLogic", "ScreenShot", "SetActorOffstage",
        "SetActorPosition", "SetBoundaryMap", "SetCameraAngleType", "SetCameraFOV",
        "SetCameraGlide", "SetDefaultDialogueCamera", "SetEgoLocationCount", "SetFlag",
        "SetGameVariableInt", "SetGlobalSheep", "SetIdleGAS", "SetListenGAS",
        "SetLocationTime", "SetModelShadowTexture", "SetMood", "SetNounVerbCount",
        "SetPamphletPage", "SetScene", "SetSceneNoPreloadTextures", "SetTalkGAS",
        "SetTime", "SetTimerMs", "SetTimerSeconds", "SetTopSheep", "SetWalkAnim",
        "ShowInset", "ShowModel", "ShowModelGroup", "ShowPlate", "ShowSceneModel",
        "ShowSidney", "StartDialogue", "StartDialogueNoFidgets", "StartDialogueX",
        "StartIdleFidget", "StartListenFidget", "StartMom", "StartMorphAnimation",
        "StartMoveAnimation", "StartPropFidget", "StartTalkFidget", "StartVoiceOver",
        "StopAllSounds", "StopAllSoundTracks", "StopAnimation", "StopFidget",
        "StopMorphAnimation", "StopPropFidget", "StopSound", "StopSoundTrack",
        "TurnHead", "TurnToModel", "UploadSceneLightmaps", "WalkNear", "WalkNearModel",
        "WalkTo", "WalkToSeeModel", "Warp",
    ];
}
