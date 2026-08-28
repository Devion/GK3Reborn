using System.Numerics;
using GK3Reborn.Formats.Scenes;
using GK3Reborn.Game.Navigation;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Tests for asking how high the ground is.
/// </summary>
/// <remarks>
/// The floors here are built out of quads by hand rather than read from a room, because
/// the interesting cases — a ramp, two storeys over the same ground, a hole — are three or
/// four polygons each and a real room is thousands.
/// </remarks>
public sealed class WalkFloorTests
{
    /// <summary>Builds a room out of named horizontal-ish quads.</summary>
    /// <remarks>
    /// Each quad is four corners in order. They all go under one object name, because what
    /// the query needs is a floor made of several polygons rather than several floors.
    /// </remarks>
    private static BspFile Room(string objectName, params Vector3[][] quads)
    {
        List<Vector3> vertices = [];
        List<ushort> indices = [];
        List<BspPolygon> polygons = [];

        foreach (Vector3[] quad in quads)
        {
            polygons.Add(new BspPolygon
            {
                VertexIndexOffset = indices.Count,
                VertexIndexCount = quad.Length,
                SurfaceIndex = 0,
            });

            foreach (Vector3 corner in quad)
            {
                indices.Add((ushort)vertices.Count);
                vertices.Add(corner);
            }
        }

        return BspFile.FromParts(
            "test",
            [objectName],
            [
                new BspSurface
                {
                    ObjectIndex = 0,
                    TextureName = "floor",
                    Flags = 0,
                    LightmapUvOffset = Vector2.Zero,
                    LightmapUvScale = Vector2.One,
                },
            ],
            polygons,
            [.. vertices],
            new Vector2[vertices.Count],
            [.. indices]);
    }

    /// <summary>A flat square of floor at one height.</summary>
    private static Vector3[] Flat(float fromX, float fromZ, float toX, float toZ, float y) =>
    [
        new(fromX, y, fromZ),
        new(toX, y, fromZ),
        new(toX, y, toZ),
        new(fromX, y, toZ),
    ];

    [Fact]
    public void A_flat_floor_answers_its_own_height_everywhere_over_it()
    {
        WalkFloor floor = WalkFloor.From(Room("rc1_floor", Flat(0, 0, 200, 200, 40)), "rc1_floor")!;

        Assert.NotNull(floor);
        Assert.Equal(2, floor.Triangles);
        Assert.Equal(40f, floor.Height(new Vector3(10, 0, 10))!.Value, 3);
        Assert.Equal(40f, floor.Height(new Vector3(150, 999, 30))!.Value, 3);
    }

    [Fact]
    public void A_point_off_the_floor_has_no_height()
    {
        WalkFloor floor = WalkFloor.From(Room("f", Flat(0, 0, 100, 100, 0)), "f")!;

        Assert.Null(floor.Height(new Vector3(500, 0, 500)));
    }

    [Fact]
    public void A_ramp_reads_as_a_slope_rather_than_as_steps()
    {
        // The corners' heights are mixed by the same weights that decide whether the point
        // is on the triangle at all, so halfway up is halfway up rather than one end or
        // the other.
        WalkFloor floor = WalkFloor.From(
            Room(
                "f",
                [
                    new Vector3(0, 0, 0),
                    new Vector3(0, 0, 100),
                    new Vector3(100, 50, 100),
                    new Vector3(100, 50, 0),
                ]),
            "f")!;

        Assert.Equal(0.25f, floor.Height(new Vector3(0.5f, 0, 50))!.Value, 2);
        Assert.Equal(25f, floor.Height(new Vector3(50, 0, 50))!.Value, 1);
        Assert.Equal(49.75f, floor.Height(new Vector3(99.5f, 0, 50))!.Value, 2);
    }

    [Fact]
    public void A_gallery_over_a_hall_answers_with_the_storey_the_actor_is_on()
    {
        // Two floors covering the same ground. Neither "highest" nor "lowest" is right:
        // which one is meant is decided by where the actor already is, and getting it wrong
        // walks somebody through a ceiling.
        WalkFloor floor = WalkFloor.From(
            Room("f", Flat(0, 0, 200, 200, 0), Flat(0, 0, 200, 200, 300)),
            "f")!;

        Assert.Equal(0f, floor.Height(new Vector3(100, 2, 100))!.Value, 3);
        Assert.Equal(300f, floor.Height(new Vector3(100, 298, 100))!.Value, 3);
    }

    [Fact]
    public void A_step_up_is_taken_and_a_storey_up_is_not()
    {
        // The difference between a kerb and a staircase, which is the only thing keeping an
        // actor walking along a landing from being handed the floor below it.
        WalkFloor floor = WalkFloor.From(
            Room("f", Flat(0, 0, 100, 100, 0), Flat(100, 0, 200, 100, 20)),
            "f")!;

        // Standing on the low half, at the seam: the 20-unit step is within reach.
        Assert.Equal(20f, floor.Height(new Vector3(150, 0, 50))!.Value, 3);
    }

    [Fact]
    public void Ground_built_over_ground_stands_the_actor_on_the_upper_one()
    {
        // CD1, the ruins of Chateau de Blanchefort, and the shape a third of its floor has:
        // the hillside the ruins stand on belongs to the same floor object and runs on
        // underneath them. Standing on the hill at its own height, the paved ruins eleven
        // units up are the further of the two surfaces — and they are the one being walked
        // on. Nearest-to-the-feet handed back the hillside and buried Gabriel in the ruins
        // to the knee.
        WalkFloor floor = WalkFloor.From(
            Room("cd1_floor", Flat(0, 0, 200, 200, 688), Flat(100, 0, 200, 200, 699)),
            "cd1_floor")!;

        Assert.Equal(699f, floor.Height(new Vector3(150, 688, 100))!.Value, 3);

        // And the tower platform, a further twenty-three up: out of the hillside's reach,
        // not out of the ruins'. Which is why he sank further the higher the floor got.
        WalkFloor tower = WalkFloor.From(
            Room(
                "cd1_floor",
                Flat(0, 0, 200, 200, 688),
                Flat(100, 0, 200, 200, 699),
                Flat(150, 0, 200, 200, 722)),
            "cd1_floor")!;

        Assert.Equal(722f, tower.Height(new Vector3(175, 699, 100))!.Value, 3);
        Assert.Equal(699f, tower.Height(new Vector3(175, 688, 100))!.Value, 3);
    }

    [Fact]
    public void The_surface_underfoot_is_the_one_the_height_came_from()
    {
        // A footstep on the ruins must not sound like the hillside beneath them. Both
        // answers come out of the same triangle now; Surface used to run its own
        // nearest-height search with no notion of a storey at all.
        BspFile room = Room("cd1_floor", Flat(0, 0, 200, 200, 688), Flat(100, 0, 200, 200, 699));
        WalkFloor floor = WalkFloor.From(room, "cd1_floor")!;

        Assert.Equal(699f, floor.Height(new Vector3(150, 688, 100))!.Value, 3);
        Assert.Equal("floor", floor.Surface(new Vector3(150, 688, 100)));
        Assert.Null(floor.Surface(new Vector3(500, 688, 500)));
    }

    [Fact]
    public void A_room_that_names_no_floor_has_no_height_query()
    {
        Assert.Null(WalkFloor.From(Room("f", Flat(0, 0, 100, 100, 0)), null));
        Assert.Null(WalkFloor.From(Room("f", Flat(0, 0, 100, 100, 0)), "not_the_floor"));
        Assert.Null(WalkFloor.From(null, "f"));
    }

    [Fact]
    public void A_wall_in_the_floor_object_is_not_ground()
    {
        // A vertical quad has no "under". Dividing by its zero area would answer with an
        // infinity, which then wins every nearest-height comparison in the room.
        WalkFloor floor = WalkFloor.From(
            Room(
                "f",
                Flat(0, 0, 100, 100, 0),
                [
                    new Vector3(50, 0, 0),
                    new Vector3(50, 0, 100),
                    new Vector3(50, 200, 100),
                    new Vector3(50, 200, 0),
                ]),
            "f")!;

        Assert.Equal(0f, floor.Height(new Vector3(50, 0, 50))!.Value, 3);
    }

    [Fact]
    public void A_walker_follows_the_ground_instead_of_holding_the_height_it_set_off_at()
    {
        // What the whole thing is for. Without the hook the walk holds its starting height
        // and the ramp goes on without it.
        WalkFloor floor = WalkFloor.From(
            Room(
                "f",
                [
                    new Vector3(-50, 0, -50),
                    new Vector3(-50, 0, 50),
                    new Vector3(250, 60, 50),
                    new Vector3(250, 60, -50),
                ]),
            "f")!;

        var walker = new Walker(
            "gab",
            new WalkRoute(true, [new Vector3(200, 0, 0)]),
            Vector3.Zero,
            0f)
        {
            Ground = floor.Height,
        };

        while (walker.Advance(0.05f))
        {
        }

        // The ramp climbs 60 units over the 300 it spans, so five sixths of the way along
        // it is fifty up. Held at the height it set off at, this would still read zero.
        Assert.Equal(200f, walker.Position.X, 1);
        Assert.Equal(50f, walker.Position.Y, 1);
    }
}
