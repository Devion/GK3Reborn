using System.Numerics;
using GK3Reborn.Content;
using GK3Reborn.Foundation.Diagnostics;
using GK3Reborn.Game;
using GK3Reborn.Rendering;
using GK3Reborn.Sheep;
using GK3Reborn.Tests.Formats;
using Xunit;

namespace GK3Reborn.Tests.Game;

public sealed class BinocularSceneTests
{
    private static BinocularView View(string entering = "CD1102PCSDEnt") => new(
        "CD1", new Sight("CSD_a", default, default, new(-139.22f, 23.62f),
            new(-844.48f, 453.95f, 191.13f), "csd_floor", entering, ""),
        default, 0, default, default);

    [Theory]
    [InlineData("CD1", "CSD_a")]
    [InlineData("CD1", "LHM_a_a")]
    [InlineData("CD1", "LHM_c_m")]
    [InlineData("CD1", "MA3_e")]
    [InlineData("CD1", "PL3")]
    [InlineData("MA3", "CD1_M")]
    public void Zoom_loads_the_lookouts_staging_with_the_selected_destinations_scenery(string originName, string assetName)
    {
        string root = Path.Combine(Path.GetTempPath(), "gk3-binocs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var barn = new BarnFixture()
                .AddStored(originName + ".SIF", """
                    [GENERAL]
                    scene=cd1_a
                    floor=cd1_floor
                    camerabounds=cd1_bounds
                    [POSITIONS]
                    MIDDLE1,pos={-1233.177,-33.18,-810.4},heading=164
                    """)
                .AddStored(originName + "102P.SIF", """
                    [ACTORS]
                    model=mad,noun=BUTHANE,pos=MIDDLE1,hidden
                    [MODELS]
                    model=madgps,type=prop,hidden
                    """)
                .AddStored("CSD.SIF", "[GENERAL]\nscene=csd_m\nfloor=csd_floor\n")
                .AddStored("CSD102P.SIF", "[ACTORS]\nmodel=mad,noun=BUTHANE,pos=SIGN\n")
                .AddStored(assetName + ".SCN", "BSP=destination\n")
                .AddStored("CSD_M.SCN", "[GENERAL]\nbsp=csd_m\n");
            File.WriteAllBytes(Path.Combine(root, "test.brn"), barn.Build());
            using GameArchives archives = GameArchives.Open(root);
            SceneRequest origin = SceneRequest.For(originName, "102P");
            Gk3SheepApi api = origin.Api!;
            api.Leaning = View() with { From = originName, Sight = View().Sight with { Location = assetName } };
            SceneRequest request = SceneRequest.Peeking(api, api.Leaning.Sight.Scene);
            var scene = new SceneLoader(archives).Compose(request, new DiagnosticBag());

            Assert.NotNull(scene);
            Assert.Equal(assetName[..3], scene.Name, ignoreCase: true);
            Assert.Equal(assetName + ".SCN", scene.Asset!.Name, ignoreCase: true);
            Assert.Equal("csd_floor", scene.Definition.FloorObject());
            Assert.Single(scene.Definition.Positions());
            Assert.Single(scene.Definition.Actors());
            Assert.Contains(scene.Definition.Models(), m => m.Name == "madgps");
            Assert.Empty(scene.Definition.CameraBounds());
            Assert.Null(scene.Definition.Boundary());
            Assert.Equal(originName, api.State.Location);
            Assert.Equal("CSD", SceneRequest.Continuing(api, "CSD").DefinitionScene);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false, "LHM_A_A.SCN")]
    [InlineData(true, "LHM_A_M.SCN")]
    public void Missing_morning_asset_keeps_the_undug_landscape_and_prefers_an_installed_original(bool originalPresent, string expected)
    {
        string root = Path.Combine(Path.GetTempPath(), "gk3-binocs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var barn = new BarnFixture()
                .AddStored("CD1.SIF", "[GENERAL]\nscene=cd1_m\n")
                .AddStored("LHM_A_A.SCN", "BSP=lhm_a\n")
                .AddStored("LHM_B_M.SCN", "BSP=lhm_b\n");
            if (originalPresent)
            {
                barn.AddStored("LHM_A_M.SCN", "BSP=lhm_a\n");
            }

            File.WriteAllBytes(Path.Combine(root, "test.brn"), barn.Build());
            using GameArchives archives = GameArchives.Open(root);
            var api = SceneRequest.For("CD1", "207A").Api!;
            api.Leaning = View() with { Sight = View().Sight with { Location = "LHM_A_M", Floor = "lhm_floor" } };
            var scene = new SceneLoader(archives).Compose(SceneRequest.Peeking(api, "LHM"), new DiagnosticBag());
            Assert.Equal(expected, scene!.Asset!.Name, ignoreCase: true);
            Assert.Equal("lhm_a", scene.Asset.BspName);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("CD1102PCSDEnt", true, "CD1ALLCSDEnt$,CD1102PCSDEnt$")]
    [InlineData("CD1102PCSDEnt", false, "CD1102PCSDEnt$")]
    [InlineData("CD1ALLCSDEnt", true, "CD1ALLCSDEnt$")]
    public void Entry_runs_shared_and_specific_scripts_without_repeating_the_shared_one(
        string entering, bool sharedExists, string expected)
    {
        var api = new Gk3SheepApi(new GameState());
        List<string> called = [];
        api.Declares = (_, _) => sharedExists;
        api.Register("CallSheep", args =>
        {
            called.Add(args[1].AsString());
            return SheepValue.FromInt(0);
        });
        View(entering).Enter(api);
        Assert.Equal(expected.Split(','), called);
    }

    [Fact]
    public void Zoom_uses_a_thirty_degree_lens_at_the_authored_position_and_angle()
    {
        Sight sight = View().Sight;
        var camera = new FreeCamera { Position = sight.Position, Aim = sight.Aim };
        Camera zoom = camera.ToCamera(new Camera(), MathF.PI / 6f);
        Assert.Equal(sight.Position, zoom.Position);
        Assert.Equal(MathF.PI / 6f, zoom.FieldOfView);
        // The lookout is above Madeleine, so the view must point down towards her.
        Vector3 madeleine = new(-1233.177f, -33.18f, -810.4f);
        Assert.True(Vector3.Dot(Vector3.Normalize(madeleine - zoom.Position),
            Vector3.Normalize(zoom.Target - zoom.Position)) > 0.95f);
    }
}
