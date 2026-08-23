using System.Numerics;
using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Formats.Scenes;
using GK3Reborn.Rendering;
using Xunit;

namespace GK3Reborn.Tests.Rendering;

/// <summary>
/// Tests for cutting a floor's height map into its geometry.
/// </summary>
/// <remarks>
/// The failure this guards against is a crack. Every triangle of a floor is cut up
/// separately, so two of them that share an edge have to arrive at the same vertices along
/// it independently — and if they do not, the room has a hairline of skybox running across
/// its floor that only shows from one angle. That is what the lattice is for, and it is
/// what the first test here checks.
/// </remarks>
public sealed class SurfaceReliefTests
{
    /// <summary>A floor of one or more quads, all on the same object and texture.</summary>
    private static BspFile Room(params (Vector3[] Corners, Vector2[] Uvs)[] quads)
    {
        List<Vector3> vertices = [];
        List<Vector2> uvs = [];
        List<ushort> indices = [];
        List<BspPolygon> polygons = [];

        foreach ((Vector3[] corners, Vector2[] coordinates) in quads)
        {
            polygons.Add(new BspPolygon
            {
                VertexIndexOffset = indices.Count,
                VertexIndexCount = corners.Length,
                SurfaceIndex = 0,
            });

            for (int i = 0; i < corners.Length; i++)
            {
                indices.Add((ushort)vertices.Count);
                vertices.Add(corners[i]);
                uvs.Add(coordinates[i]);
            }
        }

        return BspFile.FromParts(
            "test",
            ["the_floor"],
            [
                new BspSurface
                {
                    ObjectIndex = 0,
                    TextureName = "COBBLES",
                    Flags = 0,
                    LightmapUvOffset = Vector2.Zero,
                    LightmapUvScale = Vector2.One,
                },
            ],
            polygons,
            [.. vertices],
            [.. uvs],
            [.. indices]);
    }

    /// <summary>
    /// A square of floor in the XZ plane, with one texture tile over 100 units.
    /// </summary>
    /// <remarks>
    /// Wound so the normal points up, which is what a floor's does and what lets a test say
    /// which way the relief went.
    /// </remarks>
    private static (Vector3[], Vector2[]) Slab(float fromX, float toX, float fromZ, float toZ) =>
    ([
        new Vector3(fromX, 0, fromZ),
        new Vector3(fromX, 0, toZ),
        new Vector3(toX, 0, toZ),
        new Vector3(toX, 0, fromZ),
    ],
    [
        new Vector2(fromX / 100f, fromZ / 100f),
        new Vector2(fromX / 100f, toZ / 100f),
        new Vector2(toX / 100f, toZ / 100f),
        new Vector2(toX / 100f, fromZ / 100f),
    ]);

    /// <summary>A field with a bump in the middle of every tile, so vertices actually move.</summary>
    private static HeightField Field()
    {
        const int extent = 64;
        var pixels = new byte[extent * extent * 4];

        for (int y = 0; y < extent; y++)
        {
            for (int x = 0; x < extent; x++)
            {
                double level = 128 + (100 * Math.Sin(x * 0.7) * Math.Sin(y * 0.7));
                int at = ((y * extent) + x) * 4;

                pixels[at] = (byte)Math.Clamp(level, 0, 255);
                pixels[at + 1] = pixels[at];
                pixels[at + 2] = pixels[at];
                pixels[at + 3] = 255;
            }
        }

        return HeightField.From(
            new DecodedImage(extent, extent, pixels, HasAlpha: false, "test"));
    }

    /// <summary>Cuts every triangle of a room's floor and returns what came out.</summary>
    private static List<(List<ReliefVertex> Vertices, List<int> Indices)> Cut(
        BspFile room, ReliefPlan plan, HeightField? field, float depth)
    {
        List<(List<ReliefVertex>, List<int>)> pieces = [];

        foreach (BspPolygon polygon in room.Polygons)
        {
            foreach ((ushort a, ushort b, ushort c) in room.Triangulate(polygon))
            {
                List<ReliefVertex> vertices = [];
                List<int> indices = [];

                plan.Tessellate(
                    room.Vertices[a], room.Vertices[b], room.Vertices[c],
                    room.TexCoordFor(a), room.TexCoordFor(b), room.TexCoordFor(c),
                    "COBBLES", field, depth, vertices, indices);

                pieces.Add(([.. vertices], [.. indices]));
            }
        }

        return pieces;
    }

    [Fact]
    public void A_scene_that_names_no_floor_is_left_alone()
    {
        Assert.Null(ReliefPlan.For(Room(Slab(0, 100, 0, 100)), null, _ => true, 1000));
        Assert.Null(ReliefPlan.For(Room(Slab(0, 100, 0, 100)), "not_here", _ => true, 1000));
    }

    [Fact]
    public void A_floor_whose_textures_have_nothing_to_displace_is_left_alone()
    {
        Assert.Null(ReliefPlan.For(Room(Slab(0, 100, 0, 100)), "the_floor", _ => false, 1000));
    }

    [Fact]
    public void The_cell_is_bought_with_the_budget()
    {
        BspFile room = Room(Slab(0, 800, 0, 800));

        ReliefPlan generous = ReliefPlan.For(room, "the_floor", _ => true, 200_000)!;
        ReliefPlan mean = ReliefPlan.For(room, "the_floor", _ => true, 2_000)!;

        Assert.NotNull(generous);
        Assert.NotNull(mean);

        // A tighter budget buys a coarser cell, and neither may go finer than the floor.
        Assert.True(mean.Cell > generous.Cell, $"{mean.Cell} should be coarser than {generous.Cell}");
        Assert.True(generous.Cell >= ReliefPlan.FinestCell);
    }

    [Fact]
    public void The_estimate_is_what_the_cut_actually_produces()
    {
        BspFile room = Room(Slab(0, 400, 0, 400));
        ReliefPlan plan = ReliefPlan.For(room, "the_floor", _ => true, 50_000)!;

        int made = Cut(room, plan, Field(), 4f).Sum(p => p.Indices.Count / 3);

        Assert.True(made <= 50_000, $"{made} triangles is over the budget it was given");

        // Within a quarter of the estimate, which is what makes the budget mean anything:
        // the cell is chosen from it before a single triangle is cut.
        Assert.InRange(made, plan.Triangles * 0.75, plan.Triangles * 1.25);
    }

    [Fact]
    public void Two_triangles_that_share_an_edge_agree_about_where_it_went()
    {
        // Two slabs side by side, so the seam between them is an interior edge that both
        // are free to move — and must move identically. This is the crack.
        BspFile room = Room(Slab(0, 200, 0, 200), Slab(200, 400, 0, 200));
        ReliefPlan plan = ReliefPlan.For(room, "the_floor", _ => true, 100_000)!;

        List<(List<ReliefVertex> Vertices, List<int> Indices)> pieces = Cut(room, plan, Field(), 4f);

        var left = new List<Vector3>();
        var right = new List<Vector3>();

        // A quad is two triangles, so the first slab is the first two pieces and the
        // second slab the last two. Which side a seam vertex came from cannot be read off
        // the vertex itself: both sides put one at exactly the same place, which is the
        // whole point of the test.
        for (int piece = 0; piece < pieces.Count; piece++)
        {
            foreach (ReliefVertex vertex in pieces[piece].Vertices)
            {
                // Points on the seam, which runs along x = 200.
                if (MathF.Abs(vertex.Position.X - 200f) > 0.01f)
                {
                    continue;
                }

                (piece < 2 ? left : right).Add(vertex.Position);
            }
        }

        Assert.NotEmpty(left);
        Assert.NotEmpty(right);

        // Every vertex one side put on the seam is a vertex the other side put there too,
        // at the same height. A vertex only one side has is a T-junction; one at a
        // different height is a hole.
        foreach (Vector3 point in left)
        {
            Assert.Contains(right, other =>
                MathF.Abs(other.Z - point.Z) < 0.01f && MathF.Abs(other.Y - point.Y) < 0.001f);
        }
    }

    [Fact]
    public void The_floors_outer_edge_does_not_move()
    {
        // Lifting the edge where a floor meets a wall opens a gap under the skirting, so
        // an edge no second triangle shares stays exactly where the 1999 geometry put it.
        BspFile room = Room(Slab(0, 200, 0, 200));
        ReliefPlan plan = ReliefPlan.For(room, "the_floor", _ => true, 100_000)!;

        bool anyMoved = false;

        foreach ((List<ReliefVertex> vertices, _) in Cut(room, plan, Field(), 4f))
        {
            foreach (ReliefVertex vertex in vertices)
            {
                bool onBoundary =
                    MathF.Abs(vertex.Position.X) < 0.01f ||
                    MathF.Abs(vertex.Position.X - 200f) < 0.01f ||
                    MathF.Abs(vertex.Position.Z) < 0.01f ||
                    MathF.Abs(vertex.Position.Z - 200f) < 0.01f;

                if (onBoundary)
                {
                    Assert.Equal(0f, vertex.Position.Y, 3);
                }
                else if (MathF.Abs(vertex.Position.Y) > 0.05f)
                {
                    anyMoved = true;
                }
            }
        }

        // And the inside did move, or the test above is passing for the wrong reason.
        Assert.True(anyMoved, "nothing was displaced at all");
    }

    [Fact]
    public void The_relief_is_carved_into_the_surface_rather_than_raised_out_of_it()
    {
        // A floor is the one surface other things rest on, and the game lays a rug or a
        // shadow decal flush with the plane its geometry describes. Relief that rose above
        // that plane would punch through every one of them.
        BspFile room = Room(Slab(0, 400, 0, 400));
        ReliefPlan plan = ReliefPlan.For(room, "the_floor", _ => true, 100_000)!;

        foreach ((List<ReliefVertex> vertices, _) in Cut(room, plan, Field(), 4f))
        {
            foreach (ReliefVertex vertex in vertices)
            {
                // Never above the modelled surface, and never deeper than the depth it was
                // given.
                Assert.InRange(vertex.Position.Y, -4.001f, 0.001f);
            }
        }
    }

    [Fact]
    public void Without_a_field_the_floor_is_cut_but_not_moved()
    {
        BspFile room = Room(Slab(0, 400, 0, 400));
        ReliefPlan plan = ReliefPlan.For(room, "the_floor", _ => true, 100_000)!;

        List<(List<ReliefVertex> Vertices, List<int> Indices)> pieces = Cut(room, plan, null, 4f);

        Assert.True(pieces.Sum(p => p.Indices.Count / 3) > 2, "the floor was not cut up");

        foreach ((List<ReliefVertex> vertices, _) in pieces)
        {
            Assert.All(vertices, v => Assert.Equal(0f, v.Position.Y, 4));
        }
    }

    [Fact]
    public void A_surface_with_no_texture_area_is_left_as_it_was()
    {
        // Every corner on the same texture coordinate: there is no lattice to lay over it,
        // and dividing by that area is how a floor becomes a NaN.
        var corners = new Vector3[]
        {
            new(0, 0, 0), new(200, 0, 0), new(200, 0, 200), new(0, 0, 200),
        };

        BspFile room = Room((corners, [Vector2.Zero, Vector2.Zero, Vector2.Zero, Vector2.Zero]));
        ReliefPlan? plan = ReliefPlan.For(room, "the_floor", _ => true, 100_000);

        // The plan itself declines: no texture area anywhere means no tiling to work from.
        Assert.Null(plan);
    }
}
