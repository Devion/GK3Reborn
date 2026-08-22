using GK3Reborn.Formats.Scenes;
using GK3Reborn.Game;
using GK3Reborn.Sheep;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Tests for pointing the camera at one of the angles a scene names.
/// </summary>
/// <remarks>
/// A camera angle is a name the scene gives — <c>OPEN_WARDROBE</c> — and means nothing in
/// the next room, so this is scene scripting rather than story state. The three ways to
/// ask differ by whose decision they respect, which is the part worth pinning: a player
/// who has turned cinematics off should stay where they are unless the story insists.
/// </remarks>
public sealed class SceneCameraTests
{
    private static LoadedScene Scene() =>
        new(
            "CS3",
            new SceneDefinition(SceneInitFile.Parse(
                """
                [ROOM_CAMERAS]
                WARD, angle={-16.81, 0.00}, pos={-141.53, 60.00, 108.03}, Default

                [CINEMATIC_CAMERAS]
                OPEN_WARDROBE, angle={121.00, 6.00}, pos={-93.87, 66.10, 266.55}

                [DIALOGUE_CAMERAS]
                VIEW_LH, angle={-53.78, 6.75}, pos={11.73, 50.96, 96.32}, fov=20, dialogue=GabeLHE

                [INSPECT_CAMERAS]
                //noun=ARCHWAY, angle={0.76,-3.43}, pos={258.52,81.96,579.78}
                noun=WINDOW, angle={180.94,0.00}, pos={387.65,60.00,46.45}
                model=cs3_wardrobe, angle={-5.59,29.25}, pos={130.26,-19.87,23.35}
                """,
                "CS3.SIF")),
            Asset: null,
            Lightmaps: null,
            ModelsPlaced: 0);

    private static (GameState State, Gk3SheepApi Api) Host()
    {
        var state = new GameState();
        var api = new Gk3SheepApi(state);
        SceneScripting.Attach(api, Scene());
        return (state, api);
    }

    [Fact]
    public void A_cut_moves_the_view_to_the_named_angle()
    {
        (GameState state, Gk3SheepApi api) = Host();

        Assert.Equal(string.Empty, state.CameraAngle);

        SheepExpression.Evaluate("""CutToCameraAngle("OPEN_WARDROBE")""", api);

        Assert.Equal("OPEN_WARDROBE", state.CameraAngle);
    }

    [Fact]
    public void A_player_who_has_turned_cinematics_off_stays_where_they_are()
    {
        (GameState state, Gk3SheepApi api) = Host();
        state.CinematicsEnabled = false;

        SheepExpression.Evaluate("""CutToCameraAngle("OPEN_WARDROBE")""", api);
        Assert.Equal(string.Empty, state.CameraAngle);

        // Unless a script insists for a moment, because some things the story has to show.
        SheepExpression.Evaluate("SetForcedCameraCuts(1)", api);
        SheepExpression.Evaluate("""CutToCameraAngle("OPEN_WARDROBE")""", api);
        Assert.Equal("OPEN_WARDROBE", state.CameraAngle);

        SheepExpression.Evaluate("ClearForcedCameraCuts()", api);
        SheepExpression.Evaluate("""CutToCameraAngle("WARD")""", api);
        Assert.Equal("OPEN_WARDROBE", state.CameraAngle);
    }

    [Fact]
    public void A_forced_cut_ignores_both_the_player_and_the_flag()
    {
        (GameState state, Gk3SheepApi api) = Host();
        state.CinematicsEnabled = false;

        SheepExpression.Evaluate("""ForceCutToCameraAngle("OPEN_WARDROBE")""", api);

        Assert.Equal("OPEN_WARDROBE", state.CameraAngle);
    }

    [Fact]
    public void A_glide_arrives_where_a_cut_would()
    {
        // The travelling is not observable yet; the angle it ends at is.
        (GameState state, Gk3SheepApi api) = Host();

        SheepExpression.Evaluate("""GlideToCameraAngle("VIEW_LH")""", api);

        Assert.Equal("VIEW_LH", state.CameraAngle);
        Assert.True(api.IsWaitable("GlideToCameraAngle"));
    }

    [Fact]
    public void A_camera_the_scene_does_not_name_is_reported_and_changes_nothing()
    {
        (GameState state, Gk3SheepApi api) = Host();

        SheepExpression.Evaluate("""CutToCameraAngle("WARD")""", api);
        SheepExpression.Evaluate("""CutToCameraAngle("NO_SUCH_ANGLE")""", api);

        Assert.Equal("WARD", state.CameraAngle);
        Assert.Contains(api.Diagnostics.Items, d => d.Code == "GK3R3202");
    }

    [Fact]
    public void Any_named_camera_counts_whatever_list_it_is_in()
    {
        SceneDefinition definition = Scene().Definition;

        Assert.NotNull(definition.AnyCameraNamed("WARD"));
        Assert.NotNull(definition.AnyCameraNamed("open_wardrobe"));
        Assert.NotNull(definition.AnyCameraNamed("VIEW_LH"));

        // No falling back to the default: a script asking for a camera that is not there
        // is a mistake worth hearing about, not a reason to point the view somewhere else.
        Assert.Null(definition.AnyCameraNamed("NO_SUCH_ANGLE"));
        Assert.Equal("WARD", definition.CameraNamed("NO_SUCH_ANGLE")?.Name);
    }

    [Fact]
    public void Where_the_camera_is_pointing_is_part_of_the_compared_state()
    {
        (GameState state, Gk3SheepApi api) = Host();
        string before = state.ComputeHash();

        SheepExpression.Evaluate("""CutToCameraAngle("OPEN_WARDROBE")""", api);

        Assert.NotEqual(before, state.ComputeHash());
    }

    [Fact]
    public void The_player_can_be_asked_about_cinematics_by_a_script_too()
    {
        (GameState state, Gk3SheepApi api) = Host();

        SheepExpression.Evaluate("DisableCinematics()", api);
        Assert.False(state.CinematicsEnabled);

        SheepExpression.Evaluate("EnableCinematics()", api);
        Assert.True(state.CinematicsEnabled);
    }

    [Fact]
    public void The_close_up_views_are_keyed_by_noun_and_by_model()
    {
        // A different shape from every other camera list: keyed by what they look at
        // rather than named, and the corpus keys 735 by noun and 470 by model. Reading
        // them the way the named lists are read takes "noun" for the camera's name and
        // gets one camera called noun, which is why this needs its own reader.
        SceneDefinition scene = Scene().Definition;

        SceneCamera? window = scene.InspectCameraFor("WINDOW");
        Assert.NotNull(window);
        Assert.Equal(387.65f, window.Position.X, 2);
        Assert.Equal(float.DegreesToRadians(180.94f), window.Yaw, 4);

        // Case-insensitive, because a noun in an action file and a noun in a scene file
        // agree on the letters and not on the case.
        Assert.NotNull(scene.InspectCameraFor("window"));

        // And by model, which is how several rooms frame a thing.
        Assert.NotNull(scene.InspectCameraFor("cs3_wardrobe"));

        // A model looked up under the noun that stands in front of it.
        Assert.NotNull(scene.InspectCameraFor("WARDROBE", "cs3_wardrobe"));

        // A commented-out line is not a camera.
        Assert.Null(scene.InspectCameraFor("ARCHWAY"));
        Assert.Null(scene.InspectCameraFor("nothing_by_that_name"));
    }

    [Fact]
    public void Inspecting_moves_the_view_and_letting_go_puts_it_back()
    {
        (GameState state, Gk3SheepApi api) = Host();

        SheepExpression.Evaluate("""CutToCameraAngle("OPEN_WARDROBE")""", api);
        Assert.Equal("OPEN_WARDROBE", state.CameraAngle);

        SheepExpression.Evaluate("""InspectObject("WINDOW")""", api);
        Assert.Equal("WINDOW", state.Inspecting);

        // The angle the story left the view at is untouched underneath, which is what
        // makes coming back free rather than something that has to be remembered.
        Assert.Equal("OPEN_WARDROBE", state.CameraAngle);

        SheepExpression.Evaluate("UnInspect()", api);
        Assert.Equal(string.Empty, state.Inspecting);
        Assert.Equal("OPEN_WARDROBE", state.CameraAngle);
    }

    [Fact]
    public void Inspecting_with_no_argument_looks_at_whatever_the_action_is_about()
    {
        // REGISTER, INSPECT, ALL, script={wait InspectObject();} is the whole of that rule
        // — 1,205 close-ups in the game and the commonest way of asking for one names
        // nothing at all.
        (GameState state, Gk3SheepApi api) = Host();

        api.ActingOn = "WINDOW";
        SheepExpression.Evaluate("InspectObject()", api);

        Assert.Equal("WINDOW", state.Inspecting);
    }

    [Fact]
    public void Inspecting_a_model_at_an_angle_uses_the_angle()
    {
        // The second argument is a camera the scene names, and naming one is the point of
        // this form: the model says what is being looked at and the camera says from where.
        (GameState state, Gk3SheepApi api) = Host();

        SheepExpression.Evaluate("""InspectModelUsingAngle("cs3_wardrobe", "OPEN_WARDROBE")""", api);

        Assert.Equal("OPEN_WARDROBE", state.Inspecting);
    }
}
