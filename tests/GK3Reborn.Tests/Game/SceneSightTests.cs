using System.Numerics;
using GK3Reborn.Formats.Scenes;
using GK3Reborn.Game.Navigation;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Tests for whether one point in a room can see another.
/// </summary>
/// <remarks>
/// What <c>WalkToSee</c> rests on, and 2,120 of the corpus's approaches are one. The
/// failure that matters is the quiet one: a sight test that answers "yes" through a wall
/// stops a walk on the wrong side of it, and the character then does whatever they came to
/// do from there — reading a note through a door, shaking hands with somebody in the next
/// room.
/// </remarks>
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
