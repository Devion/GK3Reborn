using System.Numerics;
using GK3Reborn.Formats.Scenes;
using GK3Reborn.Game.Navigation;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Tests for whether one point in a room can see another.
/// </summary>
public sealed class SceneSightTests
{
    /// <summary>Builds a room out of quads, each given as its four corners.</summary>
    private static BspFile Room(params Vector3[][] quads)
    {
        var vertices = new List<Vector3>();
        var indices = new List<ushort>();
        var polygons = new List<BspPolygon>();

        foreach (Vector3[] quad in quads)
        {
            int start = indices.Count;

            foreach (Vector3 corner in quad)
            {
                indices.Add((ushort)vertices.Count);
                vertices.Add(corner);
            }

            polygons.Add(new BspPolygon
            {
                VertexIndexOffset = start,
                VertexIndexCount = quad.Length,
                SurfaceIndex = 0,
            });
        }

        var surface = new BspSurface
        {
            ObjectIndex = 0,
            TextureName = "wall",
            Flags = 0,
            LightmapUvOffset = System.Numerics.Vector2.Zero,
            LightmapUvScale = System.Numerics.Vector2.One,
        };

        return BspFile.FromParts(
            "room", ["room"], [surface], polygons, [.. vertices], [], [.. indices]);
    }

    /// <summary>A wall standing across the room at a given x, from -100 to 100 in z.</summary>
    private static Vector3[] Wall(float x) =>
        [
            new(x, 0, -100),
            new(x, 0, 100),
            new(x, 200, 100),
            new(x, 200, -100),
        ];

    /// <summary>A small thing to look at, as its two corners.</summary>
    private static (Vector3 Minimum, Vector3 Maximum) Thing(Vector3 at) =>
        (at - new Vector3(5), at + new Vector3(5));

    [Fact]
    public void NothingBetweenTwoPointsIsAClearLine()
    {
        SceneSight sight = SceneSight.For(Room(Wall(500)))!;

        Assert.True(sight.Clear(new Vector3(0, 60, 0), new Vector3(100, 60, 0)));
    }

    [Fact]
    public void AWallBetweenTwoPointsIsNot()
    {
        SceneSight sight = SceneSight.For(Room(Wall(50)))!;

        Assert.False(sight.Clear(new Vector3(0, 60, 0), new Vector3(100, 60, 0)));
    }

    [Fact]
    public void AWallBesideTheLineDoesNotBlockIt()
    {
        // The wall spans z -100..100 at x = 50; looking along z = 150 passes outside it.
        SceneSight sight = SceneSight.For(Room(Wall(50)))!;

        Assert.True(sight.Clear(new Vector3(0, 60, 150), new Vector3(100, 60, 150)));
    }

    [Fact]
    public void SomethingBehindAWallCannotBeSeen()
    {
        SceneSight sight = SceneSight.For(Room(Wall(50)))!;

        Assert.False(sight.InView(new Vector3(0, 60, 0), Thing(new Vector3(100, 60, 0)).Minimum,
            Thing(new Vector3(100, 60, 0)).Maximum));
    }

    [Fact]
    public void SomethingInTheOpenCan()
    {
        SceneSight sight = SceneSight.For(Room(Wall(500)))!;

        (Vector3 minimum, Vector3 maximum) = Thing(new Vector3(100, 60, 0));

        Assert.True(sight.InView(new Vector3(0, 60, 0), minimum, maximum));
    }

    [Fact]
    public void SomethingTooFarAwayCannotBeSeenHoweverClearTheLineIs()
    {
        // Otherwise "walk until you can see it" means "do not walk", and a character
        // describes a painting from the other end of the hall.
        SceneSight sight = SceneSight.For(Room(Wall(5000)))!;

        (Vector3 minimum, Vector3 maximum) = Thing(new Vector3(SceneSight.Reach + 50, 60, 0));

        Assert.False(sight.InView(new Vector3(0, 60, 0), minimum, maximum));
    }

    [Fact]
    public void AThingSetIntoAWallIsSeenByItsFaceRatherThanItsMiddle()
    {
        // A door, a panel, a noticeboard: the middle of its box is inside the wall, so a
        // single ray to the centre answers "hidden" about everything flat on a surface.
        SceneSight sight = SceneSight.For(Room(Wall(50)))!;

        // A box straddling the wall, as a door set into it does.
        var minimum = new Vector3(45, 40, -20);
        var maximum = new Vector3(55, 100, 20);

        Assert.True(sight.InView(new Vector3(0, 60, 0), minimum, maximum));
    }

    [Fact]
    public void ADoorwayInAWallCanBeSeenThrough()
    {
        // Two wall panels with a gap between them at z -10..10, which is what a doorway is.
        BspFile room = Room(
            [new(50, 0, -100), new(50, 0, -10), new(50, 200, -10), new(50, 200, -100)],
            [new(50, 0, 10), new(50, 0, 100), new(50, 200, 100), new(50, 200, 10)]);

        SceneSight sight = SceneSight.For(room)!;

        Assert.True(sight.Clear(new Vector3(0, 60, 0), new Vector3(100, 60, 0)));
        Assert.False(sight.Clear(new Vector3(0, 60, 40), new Vector3(100, 60, 40)));
    }

    /// <summary>
    /// A room with two things in it: a wall with a doorway, and a hollow box behind it.
    /// The box is the second object, and every quad of it names it.
    /// </summary>
    private static BspFile RoomWithABoxBehindADoorway(uint wallFlags = 0)
    {
        var vertices = new List<Vector3>();
        var indices = new List<ushort>();
        var polygons = new List<BspPolygon>();

        void Quad(int surface, params Vector3[] corners)
        {
            int start = indices.Count;

            foreach (Vector3 corner in corners)
            {
                indices.Add((ushort)vertices.Count);
                vertices.Add(corner);
            }

            polygons.Add(new BspPolygon
            {
                VertexIndexOffset = start,
                VertexIndexCount = corners.Length,
                SurfaceIndex = surface,
            });
        }

        // The wall at x = 50, with a gap at z -10..10.
        Quad(0, new(50, 0, -100), new(50, 0, -10), new(50, 200, -10), new(50, 200, -100));
        Quad(0, new(50, 0, 10), new(50, 0, 100), new(50, 200, 100), new(50, 200, 10));

        // The box, x 100..120, z -20..20, y 0..120, as four walls.
        Quad(1, new(100, 0, -20), new(100, 0, 20), new(100, 120, 20), new(100, 120, -20));
        Quad(1, new(120, 0, -20), new(120, 0, 20), new(120, 120, 20), new(120, 120, -20));
        Quad(1, new(100, 0, -20), new(120, 0, -20), new(120, 120, -20), new(100, 120, -20));
        Quad(1, new(100, 0, 20), new(120, 0, 20), new(120, 120, 20), new(100, 120, 20));

        BspSurface Surface(int obj, uint flags) => new()
        {
            ObjectIndex = obj,
            TextureName = "t",
            Flags = flags,
            LightmapUvOffset = System.Numerics.Vector2.Zero,
            LightmapUvScale = System.Numerics.Vector2.One,
        };

        return BspFile.FromParts(
            "room",
            ["wall", "box"],
            [Surface(0, wallFlags), Surface(1, 0)],
            polygons,
            [.. vertices],
            [],
            [.. indices]);
    }

    [Fact]
    public void ANamedThingIsSeenWhenTheLookLandsOnItFirst()
    {
        // The line to the box's middle meets the box's own front face before it gets
        // there. That is the box being seen, not the box being in the way of itself.
        SceneSight sight = SceneSight.For(RoomWithABoxBehindADoorway())!;

        var box = new SightTarget("box", new Vector3(100, 0, -20), new Vector3(120, 120, 20));

        Assert.True(sight.InView(new Vector3(0, 60, 0), box));

        // From beside the doorway the wall is met first, so it is not.
        Assert.False(sight.InView(new Vector3(0, 60, 40), box));
    }

    [Fact]
    public void GlassIsLookedThroughRatherThanAt()
    {
        // The same room with the wall marked translucent: it neither blocks the view nor
        // counts as a thing the look landed on.
        SceneSight sight = SceneSight.For(
            RoomWithABoxBehindADoorway(BspSurface.ShadowTextureFlag))!;

        var box = new SightTarget("box", new Vector3(100, 0, -20), new Vector3(120, 120, 20));

        Assert.True(sight.InView(new Vector3(0, 60, 40), box));
        Assert.True(sight.Clear(new Vector3(0, 60, 40), new Vector3(90, 60, 40)));
    }

    [Fact]
    public void ARoomWithNoGeometrySeesEverything()
    {
        Assert.Null(SceneSight.For(null));
    }

    [Fact]
    public void EveryTriangleOfTheRoomIsBucketed()
    {
        SceneSight sight = SceneSight.For(Room(Wall(50), Wall(100)))!;

        // Two quads, two triangles apiece.
        Assert.Equal(4, sight.TriangleCount);
    }
}
