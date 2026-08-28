using System.Numerics;
using GK3Reborn.Formats.Scenes;
using GK3Reborn.Rendering;
using Xunit;

namespace GK3Reborn.Tests.Rendering;

/// <summary>
/// Tests for giving a zero-thickness card a thickness.
/// </summary>
/// <remarks>
/// A great deal of GK3's scenery is one quad with a different picture on each side, and
/// both sides are drawn: the depth test cannot choose between two surfaces at exactly the
/// same depth, so the two pictures interleave. Reported as the Blanchefort signpost's
/// lettering striped through with the bare wood of its own back.
/// </remarks>
public sealed class CoplanarCardTests
{
    /// <summary>A quad, wound so its normal is the cross product of its first two edges.</summary>
    private static (Vector3[] Points, ushort[] Indices) Quad(
        Vector3 corner, Vector3 across, Vector3 up, bool flipped)
    {
        Vector3[] points = flipped
            ? [corner, corner + across, corner + across + up, corner + up]
            : [corner, corner + up, corner + across + up, corner + across];

        return (points, [0, 1, 2, 3]);
    }

    /// <summary>A room of quads, each its own surface and its own object.</summary>
    private static BspFile Room(params (Vector3[] Points, ushort[] Indices)[] quads)
    {
        List<Vector3> vertices = [];
        List<ushort> indices = [];
        List<BspSurface> surfaces = [];
        List<BspPolygon> polygons = [];

        foreach ((Vector3[] points, ushort[] order) in quads)
        {
            int at = vertices.Count;
            int offset = indices.Count;

            vertices.AddRange(points);
            indices.AddRange(order.Select(i => (ushort)(at + i)));

            surfaces.Add(new BspSurface
            {
                ObjectIndex = 0,
                TextureName = $"tex{surfaces.Count}",
                LightmapUvOffset = Vector2.Zero,
                LightmapUvScale = Vector2.One,
                Flags = 0,
            });

            polygons.Add(new BspPolygon
            {
                VertexIndexOffset = offset,
                VertexIndexCount = order.Length,
                SurfaceIndex = surfaces.Count - 1,
            });
        }

        return BspFile.FromParts(
            "test",
            ["object"],
            surfaces,
            polygons,
            [.. vertices],
            [.. vertices.Select(_ => Vector2.Zero)],
            [.. indices]);
    }

    [Fact]
    public void The_two_sides_of_a_card_are_moved_apart()
    {
        // The Mt Cardou signpost: one flat quad, lettering on the front and bare wood on
        // the back, both at exactly the same depth.
        var corner = new Vector3(0, 0, 0);
        var across = new Vector3(30, 0, 0);
        var up = new Vector3(0, 20, 0);

        BspFile room = Room(
            Quad(corner, across, up, flipped: false),
            Quad(corner, across, up, flipped: true));

        Vector3[] apart = CoplanarCards.Apart(room);

        // Both moved, by the separation, and in opposite directions — so each face ends up
        // in front of the other from its own side.
        Assert.Equal(CoplanarCards.Separation, apart[0].Length(), 4);
        Assert.Equal(CoplanarCards.Separation, apart[1].Length(), 4);
        Assert.Equal(
            2 * CoplanarCards.Separation, Vector3.Distance(apart[0], apart[1]), 4);
    }

    [Fact]
    public void A_wall_that_coincides_with_nothing_is_left_where_it_is()
    {
        BspFile room = Room(
            Quad(Vector3.Zero, new Vector3(30, 0, 0), new Vector3(0, 20, 0), flipped: false));

        Assert.Equal(Vector3.Zero, Assert.Single(CoplanarCards.Apart(room)));
    }

    [Fact]
    public void Two_surfaces_in_one_plane_that_are_side_by_side_are_left_alone()
    {
        // A wall exported in two halves, or a floor and the ceiling below it. They share a
        // plane and cover different ground, so nothing about them is ambiguous.
        var up = new Vector3(0, 20, 0);

        BspFile room = Room(
            Quad(Vector3.Zero, new Vector3(30, 0, 0), up, flipped: false),
            Quad(new Vector3(40, 0, 0), new Vector3(30, 0, 0), up, flipped: true));

        Assert.All(CoplanarCards.Apart(room), offset => Assert.Equal(Vector3.Zero, offset));
    }

    [Fact]
    public void A_board_with_a_real_thickness_is_left_alone()
    {
        // Blanchefort's other sign is a proper board a unit thick, and a unit is hundreds
        // of depth quanta at the distance it is read from. Only what coincides is moved.
        var across = new Vector3(30, 0, 0);
        var up = new Vector3(0, 20, 0);

        BspFile room = Room(
            Quad(Vector3.Zero, across, up, flipped: false),
            Quad(new Vector3(0, 0, 1), across, up, flipped: true));

        Assert.All(CoplanarCards.Apart(room), offset => Assert.Equal(Vector3.Zero, offset));
    }

    [Fact]
    public void Two_faces_of_a_card_end_up_on_the_side_their_own_normal_points()
    {
        // What makes this work from both sides of the sign rather than only one: each face
        // moves out along its own normal, so whichever the player is looking at is the one
        // in front.
        var corner = new Vector3(0, 0, 0);
        var across = new Vector3(30, 0, 0);
        var up = new Vector3(0, 20, 0);

        BspFile room = Room(
            Quad(corner, across, up, flipped: false),
            Quad(corner, across, up, flipped: true));

        Vector3[] apart = CoplanarCards.Apart(room);

        // The quads lie in the z = 0 plane, so one face moves towards +z and the other
        // towards −z.
        Assert.Equal(CoplanarCards.Separation, MathF.Abs(apart[0].Z), 4);
        Assert.Equal(-apart[0].Z, apart[1].Z, 4);
    }
}
