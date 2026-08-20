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
}
