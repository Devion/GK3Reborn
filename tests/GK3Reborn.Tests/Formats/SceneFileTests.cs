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
    public void A_later_block_overrides_an_earlier_one_for_the_same_model()
    {
        SceneInitFile init = SceneInitFile.Parse(InitFixture, "R25.SIF");

        // Hidden in the unconditional block, visible in the conditional one that follows.
        SceneModel hotel = Assert.Single(init.Models(), m => m.Name == "RC1_HOTEL_01");

        Assert.False(hotel.Hidden);
        Assert.Equal("scene", hotel.Type);
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
