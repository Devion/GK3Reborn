using System.Numerics;
using GK3Reborn.Formats.Scenes;
using Xunit;

namespace GK3Reborn.Tests.Formats;

/// <summary>
/// Tests for the scene initialisation and scene asset readers.
/// </summary>
/// <remarks>
/// The fixtures are excerpts of R25 — Gabriel's hotel room — kept verbatim from the
/// shipped files, including their inconsistent spacing and their misspellings.
/// </remarks>
public sealed class SceneFileTests
{
    private const string InitFixture =
        """
        [GENERAL]
        floor=r25_floor
        cameraBounds=R25CameraBounds
        globalLight,pos={-35.900139,98.740967,205.638931}
        scene=r25_n

        [GENERAL={IsCurrentTime("110a")}]
        scene=r25_m

        [ACTORS]
        model=gab,noun=GABRIEL,idle=gabIdle.gas,talk=gabTalk.gas,ego

        [MODELS]
        model=RC1_HOTEL_01, type=scene, hidden

        [MODELS={IsCurrentTime("106p")}]
        model=luggageunderbed, noun=SUITCASES, type=scene
        model=RC1_HOTEL_01, type=scene

        [MODELS={!IsCurrentTime("202p")}]
        model=r25door2hal_scene,             noun=HALL_DOOR,             type=scene

        [MODELS={IsCurrentTime("202p")}]
        model=r25door2hal_scene,             noun=HALL_DOOR,             type=scene, hidden

        [ROOM_CAMERAS]
        START,        angle={-46.97, 0.90}, pos={277.58, 61.70, 50.57}, Default
        SITTINGAREA,  angle={-34.13, 0.00},pos={156.07, 56.25, 94.15}
        """;

    private const string AssetFixture =
        """
        //
        // GEngine Scene File
        //
        BSP=r25
        Version=0x202

        [Skybox]
        Left=RLC_N_512LF
        //Right=RLC_N_512RT
        Front=RLC_N_512FT
        Azimuth=-210.000000

        [Models]
        r25background=1
        r25couch=1

        [Lights]
        omni01
        spot01

        [Light_omni01]
        Type=0
        Position=45.533920,60.044212,22.611023
        Direction=0.000000,-1.000000,0.000000
        Color=0.337255,0.321569,0.243137
        AttenStart=10.000000
        AttenEnd=80.000000
        UseAtten=0
        CastShadows=0
        Intensity=0.500000
        Radius=2.000000

        [Light_spot01]
        Type=1
        Position=-2334.972900,1927.183228,155.825729
        Direction=0.779141,-0.626625,0.016744
        Color=0.847059,0.854902,0.647059
        HotSpot=0.039295
        Falloff=0.074202
        AttenStart=80.000000
        AttenEnd=320.000000
        UseAtten=0
        CastShadows=1
        Intensity=1.000000
        Radius=1.000000
        """;

    [Fact]
    public void The_unconditional_scene_asset_is_the_one_the_scene_starts_with()
    {
        SceneInitFile init = SceneInitFile.Parse(InitFixture, "R25.SIF");

        Assert.Equal("r25_n", init.SceneAsset());
    }

    [Fact]
    public void Camera_bounds_accumulate_across_the_blocks_that_apply()
    {
        // The one general setting the original adds to rather than overriding. R25 names
        // the room's shell unconditionally and a second one for the timeblocks where
        // Sidney is out on the desk; reading only the last would lose the room's own.
        const string Both =
            """
            [GENERAL]
            cameraBounds=R25CameraBounds

            [GENERAL={IsCurrentTime("202p")}]
            cameraBounds=r25_sidcm
            """;

        SceneInitFile init = SceneInitFile.Parse(
            Both, "R25.SIF", _ => true);

        Assert.Equal(["R25CameraBounds", "r25_sidcm"], init.CameraBounds());
    }

    [Fact]
    public void Camera_bounds_in_a_block_that_does_not_apply_are_left_out()
    {
        SceneInitFile init = SceneInitFile.Parse(InitFixture, "R25.SIF");

        Assert.Equal(["R25CameraBounds"], init.CameraBounds());
    }

    [Fact]
    public void A_scene_that_fences_the_camera_in_nowhere_says_so_with_an_empty_list() =>
        Assert.Empty(SceneInitFile.Parse("[GENERAL]\nfloor=ma2_floor\n", "MA2.SIF").CameraBounds());

    [Fact]
    public void Cameras_carry_their_angles_in_radians_and_their_default()
    {
        SceneInitFile init = SceneInitFile.Parse(InitFixture, "R25.SIF");

        SceneCamera start = Assert.Single(init.RoomCameras(), c => c.IsDefault);

        Assert.Equal("START", start.Name);
        Assert.Equal(new Vector3(277.58f, 61.70f, 50.57f), start.Position);
        Assert.Equal(float.DegreesToRadians(-46.97f), start.Yaw, 5);
        Assert.Equal(float.DegreesToRadians(0.90f), start.Pitch, 5);
        // Each call re-reads the file, so this compares by value, not identity.
        Assert.Equal(start, init.DefaultCamera());
    }

    [Fact]
    public void A_camera_looks_along_yaw_then_pitch_applied_to_positive_z()
    {
        var camera = new SceneCamera(
            "test", Vector3.Zero, float.DegreesToRadians(90f), 0f, IsDefault: false);

        Assert.Equal(1f, camera.Forward.X, 4);
        Assert.Equal(0f, camera.Forward.Y, 4);
        Assert.Equal(0f, camera.Forward.Z, 4);
    }

    [Fact]
    public void Pitching_up_raises_the_view()
    {
        var camera = new SceneCamera(
            "test", Vector3.Zero, 0f, float.DegreesToRadians(-30f), IsDefault: false);

        Assert.True(camera.Forward.Y > 0);
        Assert.Equal(1f, camera.Forward.Length(), 4);
    }

    [Fact]
    public void A_later_block_refines_the_type_and_noun_of_an_earlier_one()
    {
        SceneInitFile init = SceneInitFile.Parse(InitFixture, "R25.SIF");

        SceneModel hotel = Assert.Single(init.Models(), m => m.Name == "RC1_HOTEL_01");

        Assert.Equal("scene", hotel.Type);
    }

    [Fact]
    public void A_block_that_hides_a_model_another_block_shows_does_not_win()
    {
        SceneInitFile init = SceneInitFile.Parse(InitFixture, "R25.SIF");

        // The two blocks are complementary — 202p and not 202p — so one describes the
        // scene and the other describes a different state of it. Taking the last left
        // the hall door hidden in every timeblock and only its knob drawn.
        SceneModel door = Assert.Single(init.Models(), m => m.Name == "r25door2hal_scene");

        Assert.False(door.Hidden);
        Assert.True(door.VisibilityDisputed);
    }

    [Fact]
    public void A_model_every_block_hides_stays_hidden()
    {
        SceneInitFile init = SceneInitFile.Parse(
            """
            [MODELS={!IsCurrentTime("307a")}]
            model=r25unmadebed, noun=bed, type=scene, hidden

            [MODELS={IsCurrentTime("307a")}]
            model=r25unmadebed, noun=bed, type=scene, hidden
            """,
            "R25.SIF");

        SceneModel bed = Assert.Single(init.Models());

        Assert.True(bed.Hidden);
        Assert.False(bed.VisibilityDisputed);
    }

    [Fact]
    public void A_disagreement_survives_a_third_block_that_agrees_with_the_second()
    {
        SceneInitFile init = SceneInitFile.Parse(
            """
            [MODELS]
            model=lamp, type=scene, hidden

            [MODELS={IsCurrentTime("307a")}]
            model=lamp, type=scene

            [MODELS={IsCurrentTime("202p")}]
            model=lamp, type=scene
            """,
            "R25.SIF");

        Assert.True(Assert.Single(init.Models()).VisibilityDisputed);
    }

    [Fact]
    public void Actors_are_read_with_their_noun_and_ego_flag()
    {
        SceneActor actor = Assert.Single(SceneInitFile.Parse(InitFixture, "R25.SIF").Actors());

        Assert.Equal("gab", actor.Name);
        Assert.Equal("GABRIEL", actor.Noun);
        Assert.True(actor.IsEgo);
    }

    [Fact]
    public void Deciding_the_conditions_leaves_a_scene_in_one_state()
    {
        // 202P: the hall door is hidden and the suitcases are not there at all.
        SceneInitFile init = SceneInitFile.Parse(
            InitFixture, "R25.SIF", c => Mentions(c, "202p"));

        SceneModel door = Assert.Single(init.Models(), m => m.Name == "r25door2hal_scene");

        Assert.True(init.ConditionsResolved);
        Assert.True(door.Hidden);
        Assert.False(door.VisibilityDisputed);
        Assert.DoesNotContain(init.Models(), m => m.Name == "luggageunderbed");
    }

    [Fact]
    public void The_other_side_of_the_same_pair_leaves_the_door_standing()
    {
        SceneInitFile init = SceneInitFile.Parse(
            InitFixture, "R25.SIF", c => Mentions(c, "106p"));

        SceneModel door = Assert.Single(init.Models(), m => m.Name == "r25door2hal_scene");

        Assert.False(door.Hidden);

        // The later block turns the hotel exterior back on, and with one state to reason
        // about the last declaration simply wins.
        Assert.False(Assert.Single(init.Models(), m => m.Name == "RC1_HOTEL_01").Hidden);
    }

    [Fact]
    public void The_scene_asset_follows_the_conditions_that_hold()
    {
        Assert.Equal(
            "r25_m",
            SceneInitFile.Parse(InitFixture, "R25.SIF", c => Mentions(c, "110a"))
                .SceneAsset(includeConditional: true));

        Assert.Equal(
            "r25_n",
            SceneInitFile.Parse(InitFixture, "R25.SIF", c => Mentions(c, "202p"))
                .SceneAsset(includeConditional: true));
    }

    [Fact]
    public void Read_without_deciding_a_scene_holds_every_state_at_once()
    {
        SceneInitFile init = SceneInitFile.Parse(InitFixture, "R25.SIF");

        Assert.False(init.ConditionsResolved);
        Assert.True(Assert.Single(init.Models(), m => m.Name == "r25door2hal_scene").VisibilityDisputed);
        Assert.Contains(init.Models(), m => m.Name == "luggageunderbed");
    }

    /// <summary>
    /// Stands in for the Sheep evaluator: a condition holds when it names this timeblock
    /// and is not negated. Enough for the fixture, and it keeps the format tests free of
    /// the game layer.
    /// </summary>
    private static bool Mentions(string? condition, string timeblock)
    {
        if (condition is null)
        {
            return true;
        }

        bool names = condition.Contains(timeblock, StringComparison.OrdinalIgnoreCase);
        return condition.TrimStart().StartsWith('!') ? !names : names;
    }

    [Theory]
    [InlineData(0u, true)]
    [InlineData(1u, true)]
    [InlineData(2u, true)]
    [InlineData(4u, true)]
    [InlineData(8u, false)]
    [InlineData(16u, false)]
    [InlineData(24u, false)]
    [InlineData(64u, false)]
    public void Light_fittings_and_self_lit_surfaces_do_not_block_a_ray(uint flags, bool casts)
    {
        // R25's lamps are bit 16 and its window backdrop bit 12, and the rig's emitters
        // sit inside both. Tracing them shuts the lamps inside their shades.
        var surface = new BspSurface
        {
            ObjectIndex = 0,
            TextureName = "LAMPSHADE",
            LightmapUvOffset = Vector2.Zero,
            LightmapUvScale = Vector2.One,
            Flags = flags,
        };

        Assert.Equal(casts, surface.CastsShadows);
    }

    [Theory]
    [InlineData(0u, false)]
    [InlineData(16u, false)]
    [InlineData(8u, true)]
    [InlineData(12u, true)]
    [InlineData(64u, true)]
    public void The_bake_skips_the_surfaces_that_carry_their_own_brightness(uint flags, bool selfLit)
    {
        var surface = new BspSurface
        {
            ObjectIndex = 0,
            TextureName = "LIGHTBULB",
            LightmapUvOffset = Vector2.Zero,
            LightmapUvScale = Vector2.One,
            Flags = flags,
        };

        Assert.Equal(selfLit, surface.IsSelfLit);
    }

    [Fact]
    public void A_scene_asset_names_its_geometry_and_its_models()
    {
        SceneAssetFile asset = SceneAssetFile.Parse(AssetFixture, "R25_N.SCN");

        Assert.Equal("r25", asset.BspName);
        Assert.Equal(["r25background", "r25couch"], asset.Models);
    }

    [Fact]
    public void Point_lights_are_read_with_their_attenuation_range()
    {
        SceneAssetFile asset = SceneAssetFile.Parse(AssetFixture, "R25_N.SCN");

        AuthoredLight light = Assert.Single(asset.Lights, l => l.Name == "omni01");

        Assert.Equal(AuthoredLightKind.Point, light.Kind);
        Assert.Equal(new Vector3(45.53392f, 60.044212f, 22.611023f), light.Position);
        Assert.Equal(10f, light.AttenuationStart);
        Assert.Equal(80f, light.AttenuationEnd);
        Assert.False(light.UsesAttenuation);
        Assert.False(light.CastsShadows);
        Assert.Equal(0.5f, light.Intensity);
    }

    [Fact]
    public void Spot_lights_are_read_with_their_cone_and_direction()
    {
        SceneAssetFile asset = SceneAssetFile.Parse(AssetFixture, "R25_N.SCN");

        AuthoredLight light = Assert.Single(asset.Lights, l => l.Name == "spot01");

        Assert.Equal(AuthoredLightKind.Spot, light.Kind);
        Assert.Equal(0.039295f, light.HotSpot, 5);
        Assert.Equal(0.074202f, light.Falloff, 5);
        Assert.True(light.CastsShadows);

        // Stored directions are already unit length; normalising must not change them.
        Assert.Equal(1f, light.Direction.Length(), 4);
        Assert.Equal(0.779141f, light.Direction.X, 4);
    }

    /// <summary>The corpus's own trigger lines, including the two that are mistyped.</summary>
    private const string TriggerFixture =
        """
        [TRIGGERS]
        noun=GET_CLOSE,rect={48.84, -400.57, 370.19, -598.15}
        noun=EXIT2, rect={201.56,859.80,344.20,,1072.81}
        noun=SIDEWAYS,  rect={385.26, 163.02, 935.11, 11.03.58}
        noun=NO_RECTANGLE
        GET_CLOSE, pos={34.70, 173.75, 94.68, 185.80}
        """;

    [Fact]
    public void A_trigger_names_a_noun_and_a_rectangle_on_the_ground_plan()
    {
        SceneTrigger trigger = SceneInitFile.Parse(TriggerFixture, "MS3110A.SIF").Triggers()[0];

        Assert.Equal("GET_CLOSE", trigger.Noun);

        // The file writes the far corner first on Z, and a rectangle whose edges are the
        // wrong way round contains nothing at all.
        Assert.Equal(48.84f, trigger.Rect.MinX, 2);
        Assert.Equal(-598.15f, trigger.Rect.MinZ, 2);
        Assert.Equal(370.19f, trigger.Rect.MaxX, 2);
        Assert.Equal(-400.57f, trigger.Rect.MaxZ, 2);

        Assert.True(trigger.Rect.Contains(210f, -499f));
        Assert.False(trigger.Rect.Contains(-13f, -494f));

        // The museum's four hiding places sit just outside it, which is the whole point of
        // where they are: the edge counts as inside.
        Assert.True(trigger.Rect.Contains(48.84f, -400.57f));
    }

    [Fact]
    public void A_mistyped_rectangle_is_read_rather_than_dropped()
    {
        // Both of these are in CSE212P as shipped. A trigger dropped for a typo is a scene
        // where something quietly never happens, so the reader is as forgiving as the
        // original's, which discards empty elements and parses with stof.
        IReadOnlyList<SceneTrigger> triggers =
            SceneInitFile.Parse(TriggerFixture, "CSE212P.SIF").Triggers();

        SceneTrigger doubled = Assert.Single(triggers, t => t.Noun == "EXIT2");

        Assert.Equal(201.56f, doubled.Rect.MinX, 2);
        Assert.Equal(1072.81f, doubled.Rect.MaxZ, 2);

        SceneTrigger twoPoints = Assert.Single(triggers, t => t.Noun == "SIDEWAYS");

        Assert.Equal(11.03f, twoPoints.Rect.MinZ, 2);
    }

    [Fact]
    public void A_trigger_with_no_rectangle_is_no_trigger()
    {
        // One line in the corpus writes pos= where every other writes rect=, and one names
        // no area at all. Neither can say where the player has to stand, and the original
        // reads only rect, so both do nothing.
        Assert.Equal(
            ["GET_CLOSE", "EXIT2", "SIDEWAYS"],
            SceneInitFile.Parse(TriggerFixture, "R31.SIF").Triggers().Select(t => t.Noun));
    }

    [Fact]
    public void Commented_out_skybox_faces_are_absent_rather_than_empty()
    {
        SkyboxDefinition skybox = Assert.IsType<SkyboxDefinition>(
            SceneAssetFile.Parse(AssetFixture, "R25_N.SCN").Skybox);

        Assert.Equal("RLC_N_512LF", skybox.Left);
        Assert.Equal("RLC_N_512FT", skybox.Front);
        Assert.Null(skybox.Right);
        Assert.Equal(float.DegreesToRadians(-210f), skybox.Azimuth, 5);
    }
}
