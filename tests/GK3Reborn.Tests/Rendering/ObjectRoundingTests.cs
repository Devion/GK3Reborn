// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Numerics;
using GK3Reborn.Rendering;
using Xunit;

namespace GK3Reborn.Tests.Rendering;

/// <summary>
/// What rounding a scene object may and may not do to it.
/// </summary>
/// <remarks>
/// The first attempt at this shipped a lamp shade whose panels had sagged inward and whose
/// rim had spiked, so most of what is pinned here is what rounding must <em>not</em> do: no
/// authored vertex may move, nothing may go inside the shape it started as, and a flat face
/// must come out flat however many times it is cut up.
/// </remarks>
public sealed class ObjectRoundingTests
{
    /// <summary>A closed lathe of <paramref name="sides"/> sides, as loose triangles.</summary>
    private static List<(Vector3, Vector3, Vector3, Vector2, Vector2, Vector2, int)> Lathe(
        int sides, float radius, float height, bool capped)
    {
        List<(Vector3, Vector3, Vector3, Vector2, Vector2, Vector2, int)> triangles = [];

        Vector3 At(int i, float y)
        {
            float angle = 2f * MathF.PI * i / sides;

            return new Vector3(radius * MathF.Cos(angle), y, radius * MathF.Sin(angle));
        }

        for (int i = 0; i < sides; i++)
        {
            Vector3 a = At(i, 0);
            Vector3 b = At(i + 1, 0);
            Vector3 c = At(i + 1, height);
            Vector3 d = At(i, height);

            triangles.Add((a, b, c, Vector2.Zero, Vector2.UnitX, Vector2.One, 0));
            triangles.Add((a, c, d, Vector2.Zero, Vector2.One, Vector2.UnitY, 0));

            if (capped)
            {
                triangles.Add((
                    new Vector3(0, height, 0), d, c,
                    Vector2.Zero, Vector2.UnitX, Vector2.One, 1));
            }
        }

        return triangles;
    }

    /// <summary>One flat square, as two triangles.</summary>
    private static List<(Vector3, Vector3, Vector3, Vector2, Vector2, Vector2, int)> Square() =>
    [
        (new Vector3(0, 0, 0), new Vector3(10, 0, 0), new Vector3(10, 0, 10),
            Vector2.Zero, Vector2.UnitX, Vector2.One, 0),
        (new Vector3(0, 0, 0), new Vector3(10, 0, 10), new Vector3(0, 0, 10),
            Vector2.Zero, Vector2.One, Vector2.UnitY, 0),
    ];

    private static (List<CurvedTriangle> Pieces, List<Vector3> Positions) Round(
        List<(Vector3, Vector3, Vector3, Vector2, Vector2, Vector2, int)> raw, int levels = 2)
    {
        List<Vector3> positions = [];
        List<RoundedTriangle> welded = ObjectRounding.Weld(raw, positions);

        return (ObjectRounding.Curve(welded, positions, levels), positions);
    }

    [Fact]
    public void A_flat_face_comes_out_flat()
    {
        (List<CurvedTriangle> pieces, _) = Round(Square());

        Assert.Equal(2 * 16, pieces.Count);

        foreach (CurvedTriangle piece in pieces)
        {
            foreach (CurvedCorner corner in (ReadOnlySpan<CurvedCorner>)[piece.A, piece.B, piece.C])
            {
                Assert.InRange(corner.Position.Y, -1e-4f, 1e-4f);
            }
        }
    }

    [Fact]
    public void Every_authored_vertex_is_still_exactly_where_it_was()
    {
        (List<CurvedTriangle> pieces, List<Vector3> positions) = Round(Lathe(12, 20f, 30f, true));

        List<Vector3> made =
        [
            .. pieces.SelectMany(p => new[] { p.A.Position, p.B.Position, p.C.Position }),
        ];

        foreach (Vector3 authored in positions)
        {
            Assert.Contains(made, one => (one - authored).Length() < 1e-3f);
        }
    }

    [Fact]
    public void A_lathe_bulges_outward_and_never_inward()
    {
        const float Radius = 20f;

        (List<CurvedTriangle> pieces, _) = Round(Lathe(12, Radius, 30f, false));

        // The straight sides of a twelve-sided lathe stand at the cosine of fifteen degrees
        // of its radius. Anything between that and the radius itself is the curve the
        // authored normals describe; anything under it is the sag the first attempt had.
        float flat = Radius * MathF.Cos(MathF.PI / 12f);
        float furthest = 0f;

        foreach (CurvedTriangle piece in pieces)
        {
            foreach (CurvedCorner corner in (ReadOnlySpan<CurvedCorner>)[piece.A, piece.B, piece.C])
            {
                float from = new Vector2(corner.Position.X, corner.Position.Z).Length();

                Assert.InRange(from, flat - 1e-3f, Radius + 1e-3f);

                furthest = MathF.Max(furthest, from);
            }
        }

        // And it does bulge: some of it stands past where the flat side was.
        Assert.True(furthest > flat + 0.05f, $"{furthest} never left the chord at {flat}");
    }

    [Fact]
    public void The_rim_of_an_open_lathe_is_rounded_along_itself()
    {
        const float Radius = 20f;
        const float Height = 30f;

        (List<CurvedTriangle> pieces, _) = Round(Lathe(8, Radius, Height, false));

        // The top rim is a crease in the sense that matters — it has one face, not two — so
        // the surface either side of it says nothing about its shape. Left alone it stays an
        // octagon, and an octagon at the widest point of the object is the whole silhouette.
        float flat = Radius * MathF.Cos(MathF.PI / 8f);
        float furthest = 0f;

        foreach (CurvedTriangle piece in pieces)
        {
            foreach (CurvedCorner corner in (ReadOnlySpan<CurvedCorner>)[piece.A, piece.B, piece.C])
            {
                if (MathF.Abs(corner.Position.Y - Height) > 1e-3f)
                {
                    continue;
                }

                furthest = MathF.Max(
                    furthest, new Vector2(corner.Position.X, corner.Position.Z).Length());
            }
        }

        Assert.True(furthest > flat + 0.05f, $"the rim stayed an octagon at {furthest}");
        Assert.True(furthest <= Radius + 1e-3f, $"the rim bulged past its own vertices, to {furthest}");
    }

    [Fact]
    public void A_square_panels_corner_is_not_rounded_off()
    {
        // Its rim turns ninety degrees at each corner, which is a corner and not a facet of
        // anything. Rounding it would round off every step, sill and doorframe an object
        // happens to include.
        (List<CurvedTriangle> pieces, _) = Round(Square());

        foreach (CurvedTriangle piece in pieces)
        {
            foreach (CurvedCorner corner in (ReadOnlySpan<CurvedCorner>)[piece.A, piece.B, piece.C])
            {
                Assert.InRange(corner.Position.X, -1e-3f, 10f + 1e-3f);
                Assert.InRange(corner.Position.Z, -1e-3f, 10f + 1e-3f);
            }
        }
    }

    [Fact]
    public void A_cap_keeps_its_own_shading_and_the_side_keeps_its()
    {
        List<Vector3> positions = [];
        List<RoundedTriangle> welded = ObjectRounding.Weld(Lathe(12, 20f, 30f, true), positions);

        Vector3[] normals = ObjectRounding.Creased(welded, positions);

        // A vertex on the rim between the side and the flat top belongs to both, and the two
        // must not be averaged into one normal: that is what shaded a bell as though its rim
        // turned over smoothly. The cap's corners look straight up; the side's do not.
        var seen = new List<Vector3>();

        for (int t = 0; t < welded.Count; t++)
        {
            foreach (int corner in (ReadOnlySpan<int>)[0, 1, 2])
            {
                Vector3 position = positions[
                    corner == 0 ? welded[t].A.Position
                    : corner == 1 ? welded[t].B.Position
                    : welded[t].C.Position];

                if (MathF.Abs(position.Y - 30f) < 1e-3f &&
                    new Vector2(position.X, position.Z).Length() > 19f)
                {
                    seen.Add(normals[(t * 3) + corner]);
                }
            }
        }

        // Straight up or straight down depending on how the cap is wound, which is the
        // artist's business; what matters is that it is one or the other and not a blend.
        Assert.Contains(seen, one => MathF.Abs(one.Y) > 0.99f);
        Assert.Contains(seen, one => MathF.Abs(one.Y) < 0.01f);
    }

    [Fact]
    public void Neighbouring_triangles_agree_about_the_edge_they_share()
    {
        // A crack is what a scheme like this fails by, and it fails silently: a hairline of
        // skybox down the side of a lamp. Every point either side of a shared edge has to be
        // put in the same place by both of its triangles.
        (List<CurvedTriangle> pieces, _) = Round(Lathe(12, 20f, 30f, true));

        var counts = new Dictionary<(int, int, int), int>();

        foreach (CurvedTriangle piece in pieces)
        {
            foreach (CurvedCorner corner in (ReadOnlySpan<CurvedCorner>)[piece.A, piece.B, piece.C])
            {
                (int, int, int) key = (
                    (int)MathF.Round(corner.Position.X * 1000f),
                    (int)MathF.Round(corner.Position.Y * 1000f),
                    (int)MathF.Round(corner.Position.Z * 1000f));

                counts[key] = counts.TryGetValue(key, out int already) ? already + 1 : 1;
            }
        }

        // Every point on the seam between two source triangles is reached from both sides,
        // so it is used by more than the three pieces one triangle's own interior gives it.
        // If the two sides put it in different places there would be twice as many distinct
        // points as there are.
        int onSeams = counts.Count(one => one.Value >= 4);

        Assert.True(onSeams > 100, $"only {onSeams} points are shared between triangles");
    }

    [Fact]
    public void Rounding_nothing_is_not_an_error()
    {
        (List<CurvedTriangle> pieces, _) = Round([]);

        Assert.Empty(pieces);
    }

    [Fact]
    public void Level_zero_leaves_the_shape_and_keeps_the_shading()
    {
        (List<CurvedTriangle> pieces, _) = Round(Lathe(12, 20f, 30f, true), levels: 0);

        Assert.Equal(36, pieces.Count);

        foreach (CurvedTriangle piece in pieces)
        {
            foreach (CurvedCorner corner in (ReadOnlySpan<CurvedCorner>)[piece.A, piece.B, piece.C])
            {
                float from = new Vector2(corner.Position.X, corner.Position.Z).Length();

                Assert.True(from < 20f + 1e-3f);
            }
        }
    }
}
