using System.Numerics;
using GK3Reborn.Rendering;
using Xunit;

namespace GK3Reborn.Tests.Rendering;

/// <summary>
/// Tests for which lights reach which part of a room.
/// </summary>
/// <remarks>
/// The grid exists to make the shading loop short, and the only way it can be wrong is by
/// making it too short: a light that reaches a point and is not in that point's cell is a
/// lamp that stops lighting the thing under it. That is the invariant every test here is
/// about, and it is checked exhaustively against the honest answer — the distance — rather
/// than against another implementation of the same idea.
/// </remarks>
public sealed class SceneLightGridTests
{
    private static readonly Vector3 Low = new(-1000, -200, -1000);
    private static readonly Vector3 High = new(1000, 200, 1000);

    /// <summary>A spread of lights that reach different distances.</summary>
    private static GridLight[] Rig(int count, int seed)
    {
        var random = new Random(seed);
        var lights = new GridLight[count];

        for (int i = 0; i < count; i++)
        {
            var at = new Vector3(
                (float)(random.NextDouble() * 2000 - 1000),
                (float)(random.NextDouble() * 400 - 200),
                (float)(random.NextDouble() * 2000 - 1000));

            lights[i] = new GridLight(
                at, (float)(random.NextDouble() * 600 + 50), Everywhere: false, Weight: i + 1);
        }

        return lights;
    }

    [Fact]
    public void Every_light_that_reaches_a_point_is_in_that_points_cell()
    {
        GridLight[] rig = Rig(40, seed: 7);
        SceneLightGrid grid = SceneLightGrid.Build(rig, Low, High);

        var random = new Random(11);

        for (int trial = 0; trial < 2000; trial++)
        {
            var point = new Vector3(
                (float)(random.NextDouble() * 2000 - 1000),
                (float)(random.NextDouble() * 400 - 200),
                (float)(random.NextDouble() * 2000 - 1000));

            int cell = grid.CellAt(point);
            var listed = new HashSet<int>();

            for (int i = grid.Offsets[cell]; i < grid.Offsets[cell + 1]; i++)
            {
                listed.Add(grid.Indices[i]);
            }

            for (int i = 0; i < rig.Length; i++)
            {
                if (Vector3.Distance(point, rig[i].Position) <= rig[i].Reach)
                {
                    Assert.True(
                        listed.Contains(i),
                        $"light {i} reaches {point} and is not in cell {cell}");
                }
            }
        }
    }

    [Fact]
    public void A_light_that_reaches_nothing_nearby_is_left_out()
    {
        // The whole point: a cell must not list the rig. One light at each end of a long
        // room, neither able to reach the other's end.
        GridLight[] rig =
        [
            new(new Vector3(-900, 0, 0), 100f, Everywhere: false, Weight: 1),
            new(new Vector3(900, 0, 0), 100f, Everywhere: false, Weight: 1),
        ];

        SceneLightGrid grid = SceneLightGrid.Build(rig, Low, High);

        int near = grid.CellAt(new Vector3(-900, 0, 0));

        Assert.Equal(1, grid.Offsets[near + 1] - grid.Offsets[near]);
        Assert.Equal(0, grid.Indices[grid.Offsets[near]]);
    }

    [Fact]
    public void A_light_with_no_falloff_is_in_every_cell()
    {
        // The sun is not somewhere in the room.
        GridLight[] rig =
        [
            new(new Vector3(50_000, 40_000, 0), 0f, Everywhere: true, Weight: 100),
            new(new Vector3(0, 0, 0), 50f, Everywhere: false, Weight: 1),
        ];

        SceneLightGrid grid = SceneLightGrid.Build(rig, Low, High);

        for (int cell = 0; cell < grid.CellCount; cell++)
        {
            bool found = false;

            for (int i = grid.Offsets[cell]; i < grid.Offsets[cell + 1]; i++)
            {
                found |= grid.Indices[i] == 0;
            }

            Assert.True(found, $"cell {cell} does not have the sun in it");
        }
    }

    [Fact]
    public void The_heaviest_light_in_a_cell_comes_first()
    {
        // The passes that can only afford a couple of rays spend them on the front of the
        // list, so the order is not decoration.
        GridLight[] rig =
        [
            new(Vector3.Zero, 5000f, Everywhere: false, Weight: 1),
            new(Vector3.Zero, 5000f, Everywhere: false, Weight: 900),
            new(Vector3.Zero, 5000f, Everywhere: false, Weight: 40),
        ];

        SceneLightGrid grid = SceneLightGrid.Build(rig, Low, High);

        int cell = grid.CellAt(Vector3.Zero);

        Assert.Equal(1, grid.Indices[grid.Offsets[cell]]);
        Assert.Equal(2, grid.Indices[grid.Offsets[cell] + 1]);
        Assert.Equal(0, grid.Indices[grid.Offsets[cell] + 2]);
    }

    [Fact]
    public void A_point_outside_the_room_is_lit_by_the_cell_beside_it()
    {
        // A walk cycle swings an arm past the geometry's own bounding box. Being lit by
        // nothing out there is a black silhouette; being lit by the nearest cell is right.
        SceneLightGrid grid = SceneLightGrid.Build(Rig(8, seed: 3), Low, High);

        int inside = grid.CellAt(new Vector3(-999, -199, -999));
        int outside = grid.CellAt(new Vector3(-99_999, -99_999, -99_999));

        Assert.Equal(inside, outside);
        Assert.InRange(grid.CellAt(new Vector3(99_999, 99_999, 99_999)), 0, grid.CellCount - 1);
    }

    [Fact]
    public void A_room_with_no_extent_is_one_cell()
    {
        SceneLightGrid grid = SceneLightGrid.Build(Rig(4, seed: 1), Vector3.Zero, Vector3.Zero);

        Assert.Equal(1, grid.CellCount);
        Assert.Equal(0, grid.CellAt(new Vector3(500, 500, 500)));
    }

    [Fact]
    public void The_grid_stays_inside_its_budget()
    {
        // A village is thousands of units across and the buffers are allocated for the
        // worst case before any room is loaded.
        SceneLightGrid grid = SceneLightGrid.Build(
            Rig(200, seed: 5), new Vector3(-20_000, -2_000, -20_000), new Vector3(20_000, 2_000, 20_000));

        Assert.InRange(grid.CellCount, 1, SceneLightGrid.MostCells);
        Assert.InRange(grid.Indices.Length, 0, SceneLightGrid.MostIndices);
        Assert.InRange(grid.Busiest, 0, SceneLightGrid.MostPerCell);
        Assert.Equal(grid.Indices.Length, grid.Offsets[grid.CellCount]);
    }

    [Fact]
    public void The_offsets_run_forwards_and_cover_every_index()
    {
        SceneLightGrid grid = SceneLightGrid.Build(Rig(30, seed: 9), Low, High);

        Assert.Equal(grid.CellCount + 1, grid.Offsets.Length);
        Assert.Equal(0, grid.Offsets[0]);

        for (int cell = 0; cell < grid.CellCount; cell++)
        {
            Assert.True(
                grid.Offsets[cell + 1] >= grid.Offsets[cell],
                $"cell {cell} ends before it starts");
        }
    }

    [Fact]
    public void A_grid_is_shorter_than_the_rig_it_divides()
    {
        // What the whole thing is for. The hotel hallway declares ninety-two lights and no
        // part of it is reached by ninety-two.
        SceneLightGrid grid = SceneLightGrid.Build(Rig(92, seed: 13), Low, High);

        Assert.True(grid.Average < 92, $"a cell holds {grid.Average:0.0} lights of 92");
        Assert.True(grid.Busiest < 92, $"the busiest cell holds {grid.Busiest} of 92");
    }
}
