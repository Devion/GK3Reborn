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

    /// <summary>How far the floor moved, over every vertex of every piece.</summary>
    private static (float Most, float Typical) Moved(
        BspFile room, ReliefPlan plan, HeightField field, float depth)
    {
        float most = 0f;
        double total = 0;
        int count = 0;

        foreach ((List<ReliefVertex> vertices, _) in Cut(room, plan, field, depth))
        {
            foreach (ReliefVertex vertex in vertices)
            {
                float moved = MathF.Abs(vertex.Position.Y);

                most = MathF.Max(most, moved);
                total += moved;
                count++;
            }
        }

        return (most, count > 0 ? (float)(total / count) : 0f);
    }

    [Fact]
    public void Two_patches_that_abut_without_sharing_a_vertex_both_move()
    {
        // GK3's ground is laid as separate flat patches that touch and are not welded, so
        // every edge along the join is used once and looks like the end of the floor. Held
        // down, they take the relief with them: the village moved 0.32 units where it should
        // have moved 1.42, and read as a painted plane at every angle anybody looks at a
        // street from.
        //
        // Two slabs whose join is at x=100, cut in different places along it so that not one
        // vertex is shared.
        BspFile split = Room(
            Slab(0, 100, 0, 200),
            Slab(100, 200, 0, 60),
            Slab(100, 200, 60, 200));

        BspFile whole = Room(Slab(0, 200, 0, 200));

        ReliefPlan apart = ReliefPlan.For(split, "the_floor", _ => true, 200_000)!;
        ReliefPlan together = ReliefPlan.For(whole, "the_floor", _ => true, 200_000)!;

        (float _, float typicalApart) = Moved(split, apart, Field(), 4f);
        (float _, float typicalTogether) = Moved(whole, together, Field(), 4f);

        Assert.True(apart.Boundary.Continued > 0, "no edge was found to carry on");

        // Within a fifth of the floor that was one piece to begin with. Not equal: the join
        // still holds its own corners, and one patch has an edge the other does not.
        Assert.InRange(typicalApart, typicalTogether * 0.8f, typicalTogether * 1.2f);
    }

    [Fact]
    public void Where_the_floor_really_stops_is_still_held_down()
    {
        // The other half of it. An edge with nothing against it is the floor meeting a wall,
        // and lifting it opens a gap under the skirting board.
        BspFile room = Room(Slab(0, 100, 0, 100));
        ReliefPlan plan = ReliefPlan.For(room, "the_floor", _ => true, 200_000)!;

        Assert.Equal(0, plan.Boundary.Continued);
        Assert.True(plan.Boundary.Pinned >= 4, $"only {plan.Boundary.Pinned} edges were held");

        foreach ((List<ReliefVertex> vertices, _) in Cut(room, plan, Field(), 4f))
        {
            foreach (ReliefVertex vertex in vertices)
            {
                bool onTheEdge =
                    vertex.Position.X <= 0.01f || vertex.Position.X >= 99.99f ||
                    vertex.Position.Z <= 0.01f || vertex.Position.Z >= 99.99f;

                if (onTheEdge)
                {
                    Assert.InRange(vertex.Position.Y, -1e-3f, 1e-3f);
                }
            }
        }
    }

    [Fact]
    public void One_triangle_with_collapsed_coordinates_does_not_decide_the_lattice()
    {
        // The step is one number for a whole texture, and it used to be the mean of the
        // triangles' own rates. `rc1Coblston` is laid at a clean 120 units to the texture
        // across the village square and a handful of triangles whose coordinates are all but
        // collapsed took that mean to 42,641 — so every cobble asked for a lattice a
        // thousand times too fine, was refused as impossible, and came out flat.
        //
        // Here: nine tiles of ordinary floor and one whose texture is squeezed into a
        // thousandth of the coordinate space, which is a rate a thousand times the rest.
        (Vector3[] corners, Vector2[] _) = Slab(300, 400, 0, 100);

        BspFile poisoned = Room(
            Slab(0, 100, 0, 100), Slab(100, 200, 0, 100), Slab(200, 300, 0, 100),
            Slab(0, 100, 100, 200), Slab(100, 200, 100, 200), Slab(200, 300, 100, 200),
            Slab(0, 100, 200, 300), Slab(100, 200, 200, 300), Slab(200, 300, 200, 300),
            (corners,
            [
                new Vector2(3.0f, 0f),
                new Vector2(3.0f, 0.001f),
                new Vector2(3.001f, 0.001f),
                new Vector2(3.001f, 0f),
            ]));

        BspFile clean = Room(
            Slab(0, 100, 0, 100), Slab(100, 200, 0, 100), Slab(200, 300, 0, 100),
            Slab(0, 100, 100, 200), Slab(100, 200, 100, 200), Slab(200, 300, 100, 200),
            Slab(0, 100, 200, 300), Slab(100, 200, 200, 300), Slab(200, 300, 200, 300));

        ReliefPlan spoiled = ReliefPlan.For(poisoned, "the_floor", _ => true, 200_000)!;
        ReliefPlan plain = ReliefPlan.For(clean, "the_floor", _ => true, 200_000)!;

        // The nine ordinary tiles get the cell they would have got on their own, and the odd
        // one out is set aside rather than allowed to ask for a lattice nobody can afford —
        // two triangles of it, since a quad is two.
        Assert.InRange(spoiled.Cell, plain.Cell * 0.9f, plain.Cell * 1.1f);
        Assert.Equal(2, spoiled.SetApart);
        Assert.Equal(0, plain.SetApart);
    }

    [Fact]
    public void The_floor_says_how_far_it_moved()
    {
        // Every other number a displaced floor prints reads the same whether it moved or not,
        // which is how this shipped flat twice.
        BspFile room = Room(Slab(0, 400, 0, 400));
        ReliefPlan plan = ReliefPlan.For(room, "the_floor", _ => true, 200_000)!;

        Assert.Equal(0f, plan.Moved);

        Cut(room, plan, Field(), 4f);

        Assert.True(plan.Moved > 0.5f, $"{plan.Moved} is not a floor that moved");
        Assert.True(plan.MovedTypically > 0.1f, $"{plan.MovedTypically} typically is not either");
        Assert.True(plan.Moved <= 4f + 1e-3f, $"{plan.Moved} is past the depth it was given");
    }

    [Fact]
    public void A_floor_cut_finer_does_not_cost_less()
    {
        // The budget is solved by walking the cell coarser until the estimate fits, which is
        // only valid if the cost falls as it goes. It used to rise: a triangle asking for
        // more cells than the per-triangle cap was left whole, and one asking for slightly
        // fewer was cut into all of them, so the village came out at seven million triangles
        // at a 263-unit cell having been under a million at seven.
        BspFile room = Room(Slab(0, 400, 0, 400), Slab(400, 800, 0, 400));

        int coarser = 0;

        for (int budget = 400_000; budget >= 1_000; budget /= 2)
        {
            ReliefPlan plan = ReliefPlan.For(room, "the_floor", _ => true, budget)!;
            int made = Cut(room, plan, Field(), 4f).Sum(p => p.Indices.Count / 3);

            Assert.True(
                coarser == 0 || made <= coarser,
                $"a budget of {budget} cut {made} triangles, against {coarser} for twice it");

            coarser = made;
        }
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
