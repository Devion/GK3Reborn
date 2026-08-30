using System.Numerics;
using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Rendering;
using Xunit;

namespace GK3Reborn.Tests.Rendering;

/// <summary>
/// Tests for the arithmetic behind the reconstructed horizon.
/// </summary>
/// <remarks>
/// <see cref="TerrainPlan"/> is everything about drawing a backdrop that is not a device —
/// the mesh, the forest gathered by species, which trees are near enough to be models, and
/// the two constant blocks a frame is drawn with — and both backends read it. It exists
/// apart from either of them so that it can be checked without one, because the failures
/// here are the kind that look like a shader bug: a horizon at the wrong scale, a wood of
/// the wrong species, a camera standing inside its own hillside.
/// </remarks>
public sealed class TerrainPlanTests
{
    /// <summary>A backdrop over a flat grid, with whatever forest is asked for.</summary>
    private static TerrainBackdrop Flat(
        int grid = 5, float extent = 100f, float[]? trees = null, float height = 0f)
    {
        float[] heights = new float[grid * grid];
        Array.Fill(heights, height);

        var pixel = new DecodedImage(1, 1, [255, 255, 255, 255], HasAlpha: false, "tile");

        return new TerrainBackdrop
        {
            Grid = grid,
            ExtentMeters = extent,
            Heights = heights,
            Splat = pixel,
            Tint = pixel,
            TileForest = pixel,
            TileRock = pixel,
            TileGrass = pixel,
            TileDirt = pixel,
            SunDirection = null,
            Azimuth = 0f,
            AnchorUnits = Vector3.Zero,
            Trees = trees ?? [],
        };
    }

    private static Camera Looking(Vector3 from) =>
        new() { Position = from, Target = from + new Vector3(0f, 0f, 1f) };

    [Fact]
    public void The_grid_becomes_a_mesh_that_spans_its_own_extent()
    {
        TerrainPlan plan = TerrainPlan.Create(Flat(grid: 5, extent: 100f), sheets: 0);

        // Every other cell of a five-wide grid is three corners a side, and a quad between
        // each pair of them is two triangles.
        Assert.Equal(9, plan.Vertices.Length);
        Assert.Equal(2 * 2 * 6, plan.Indices.Length);
        Assert.True(plan.HasGround);

        Assert.Equal(-100f, plan.Vertices.Min(v => v.Position.X));
        Assert.Equal(100f, plan.Vertices.Max(v => v.Position.X));
        Assert.Equal(-100f, plan.Vertices.Min(v => v.Position.Z));
        Assert.Equal(100f, plan.Vertices.Max(v => v.Position.Z));

        // Flat ground faces straight up, and a normal that does not is the first thing a
        // lit hillside gets wrong.
        Assert.All(plan.Vertices, v => Assert.Equal(1f, v.Normal.Y, 4));
    }

    [Fact]
    public void A_heightfield_that_does_not_match_its_grid_is_refused()
    {
        TerrainBackdrop wrong = Flat() with { Heights = new float[3] };

        Assert.Throws<ArgumentException>(() => TerrainPlan.Create(wrong, sheets: 0));
    }

    [Fact]
    public void The_forest_is_gathered_by_species()
    {
        // Four trees: a broadleaf, two conifers and a cypress, in that order.
        float[] trees =
        [
            10f, 0f, 10f, 1f, 0f, 1f,
            20f, 0f, 20f, 1f, 0f, 0f,
            30f, 0f, 30f, 1f, 0f, 2f,
            40f, 0f, 40f, 1f, 0f, 0f,
        ];

        TerrainPlan plan = TerrainPlan.Create(Flat(trees: trees), sheets: 0);

        Assert.Equal(4u, plan.TreeCount);
        Assert.Equal(TerrainPlan.ImpostorCount, plan.Stands.Length);

        // Gathered so that one species is one draw over one slice: the conifers first,
        // because they are shape zero, then the broadleaf, then the cypress.
        Assert.Equal((0u, 2u), plan.Stands[0]);
        Assert.Equal((2u, 1u), plan.Stands[1]);
        Assert.Equal((3u, 1u), plan.Stands[2]);
        Assert.Equal((4u, 0u), plan.Stands[3]);

        // And each slice holds the trees it says it does.
        Assert.Equal(20f, plan.TreeInstances[0]);
        Assert.Equal(40f, plan.TreeInstances[TerrainPlan.Stride]);
        Assert.Equal(10f, plan.TreeInstances[2 * TerrainPlan.Stride]);
        Assert.Equal(30f, plan.TreeInstances[3 * TerrainPlan.Stride]);
    }

    [Fact]
    public void A_species_the_file_does_not_know_is_still_a_tree()
    {
        // Shape nine is not a shape. It falls to the conifer rather than being dropped,
        // because a set written before the shapes existed says nothing useful here.
        float[] trees = [5f, 0f, 5f, 1f, 0f, 9f];

        TerrainPlan plan = TerrainPlan.Create(Flat(trees: trees), sheets: 0);

        Assert.Equal(1u, plan.TreeCount);
        Assert.Equal((0u, 1u), plan.Stands[0]);
    }

    [Fact]
    public void Every_impostor_shape_has_geometry_of_its_own()
    {
        float[] trees = [0f, 0f, 0f, 1f, 0f, 0f];
        TerrainPlan plan = TerrainPlan.Create(Flat(trees: trees), sheets: 0);

        Assert.Equal(TerrainPlan.ImpostorCount, plan.ImpostorRanges.Length);

        for (int kind = 0; kind < plan.ImpostorRanges.Length; kind++)
        {
            (uint first, int vertexOffset, uint count) = plan.ImpostorRanges[kind];

            Assert.True(count > 0, $"shape {kind} has no triangles");
            Assert.True(first + count <= (uint)plan.TreeIndices.Length);

            // Indices are relative to the shape's own first vertex, because the draw adds
            // the offset for us. Absolute here and every shape but the first would be built
            // out of another shape's corners.
            for (uint at = first; at < first + count; at++)
            {
                Assert.True(vertexOffset + plan.TreeIndices[at] < plan.TreeVertices.Length);
            }
        }
    }

    [Fact]
    public void The_camera_moves_through_the_backdrop_in_metres()
    {
        TerrainPlan plan = TerrainPlan.Create(Flat(extent: 1000f), sheets: 0);
        plan.LiftMeters = 0f;
        plan.ClearanceMeters = 0f;

        // Forty units east of the scene's centre is one metre east in the backdrop, which
        // is the one constant where a room unit meets a terrain metre.
        TerrainFrame frame = plan.Frame(Looking(new Vector3(40f, 0f, 0f)), 640, 480);

        Assert.Equal(1f, frame.Eye.X, 4);
        Assert.Equal(0f, frame.Eye.Z, 4);
        Assert.Equal(40f * TerrainPlan.MetersPerUnit, frame.Eye.X, 4);
    }

    [Fact]
    public void The_camera_is_never_left_under_the_ground_it_stands_on()
    {
        // A reconstruction that sits at ten metres, with the standard twelve of lift: the
        // camera would be two metres inside the hill, and every direction a wall of it.
        TerrainPlan plan = TerrainPlan.Create(Flat(height: 10f), sheets: 0);

        TerrainFrame frame = plan.Frame(Looking(Vector3.Zero), 640, 480);

        Assert.Equal(10f + plan.ClearanceMeters, frame.Eye.Y, 4);
    }

    [Fact]
    public void A_camera_may_not_leave_the_grid()
    {
        TerrainPlan plan = TerrainPlan.Create(Flat(extent: 100f), sheets: 0);

        // A quarter of the extent out, however far the scripts put the camera.
        TerrainFrame frame = plan.Frame(Looking(new Vector3(100_000f, 0f, 0f)), 640, 480);

        Assert.Equal(25f, frame.Eye.X, 3);
    }

    [Fact]
    public void The_frame_carries_what_the_stages_read()
    {
        TerrainPlan plan = TerrainPlan.Create(Flat(extent: 1500f), sheets: 0);
        plan.CloudCoverage = 0.5f;

        TerrainFrame frame = plan.Frame(Looking(Vector3.Zero), 800, 600);

        Assert.Equal(plan.TileMeters, frame.Ground.Params.X, 4);
        Assert.Equal(plan.TintAmount, frame.Ground.Params.Y, 4);
        Assert.Equal(plan.HazeDensity, frame.Ground.Params.Z, 6);
        Assert.Equal(1500f, frame.Ground.Params.W, 4);
        Assert.Equal(plan.HazeHeight, frame.Ground.Haze.W, 4);
        Assert.Equal(0.5f, frame.Ground.Eye.W, 4);

        // A sunless hour is told so in w, and the ground light is made entirely of it.
        Assert.Equal(0f, frame.Ground.Sun.W);
        Assert.Equal(0f, frame.Sky.Sun.W);

        Assert.Equal(800f, frame.Sky.Viewport.X);
        Assert.Equal(600f, frame.Sky.Viewport.Y);
        Assert.Equal(0.5f, frame.Sky.Clouds.X, 4);

        // Nothing to reselect where nothing has a model, and so nothing to write again.
        Assert.False(frame.Reselected);
        Assert.Equal(0u, plan.ModelCount);
        Assert.Equal(0f, frame.Ground.Haze.X);
    }

    [Fact]
    public void A_sun_reaches_both_blocks()
    {
        TerrainBackdrop lit = Flat() with { SunDirection = new Vector3(0f, -1f, 0f) };
        TerrainPlan plan = TerrainPlan.Create(lit, sheets: 0);

        TerrainFrame frame = plan.Frame(Looking(Vector3.Zero), 640, 480);

        // Toward the sun, which is the reverse of the way its light travels.
        Assert.Equal(1f, frame.Ground.Sun.W);
        Assert.Equal(1f, frame.Ground.Sun.Y, 4);
        Assert.Equal(1f, frame.Sky.Sun.Y, 4);
    }
}
