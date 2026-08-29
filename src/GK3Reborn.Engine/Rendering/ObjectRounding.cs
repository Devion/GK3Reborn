// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using GK3Reborn.Rendering.Geometry;
using System.Numerics;

namespace GK3Reborn.Rendering;

/// <summary>One corner of one triangle, before it is rounded.</summary>
/// <param name="Position">Which welded position it stands on.</param>
/// <param name="TexCoord">Its texture coordinate, kept per corner.</param>
public readonly record struct RoundedCorner(int Position, Vector2 TexCoord);

/// <summary>One triangle, tagged with which surface it came from.</summary>
/// <param name="A">First corner.</param>
/// <param name="B">Second corner.</param>
/// <param name="C">Third corner.</param>
/// <param name="Surface">The index of the surface that owns it, for its batch and lightmap.</param>
public readonly record struct RoundedTriangle(
    RoundedCorner A, RoundedCorner B, RoundedCorner C, int Surface);

/// <summary>One corner of a rounded triangle, with everything needed to draw it.</summary>
/// <param name="Position">Where it is, in world space.</param>
/// <param name="Normal">The surface normal there.</param>
/// <param name="TexCoord">Its texture coordinate.</param>
public readonly record struct CurvedCorner(Vector3 Position, Vector3 Normal, Vector2 TexCoord);

/// <summary>One triangle of a rounded object.</summary>
/// <param name="A">First corner.</param>
/// <param name="B">Second corner.</param>
/// <param name="C">Third corner.</param>
/// <param name="Surface">The surface that owns it, for its batch and lightmap.</param>
public readonly record struct CurvedTriangle(
    CurvedCorner A, CurvedCorner B, CurvedCorner C, int Surface);

/// <summary>
/// Rounds a whole scene object off, across every surface it is made of.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why not the head's subdivision.</b> <see cref="Formats.Models.LoopSubdivision"/>
/// rounds a character's head and cannot round a bell, for a structural reason: it pins
/// boundary vertices, and a lathed object is strips and caps whose vertices are <b>all</b>
/// on a boundary — the rim between the side of the bell and its top belongs to two
/// surfaces, so refining each surface alone sees it as an edge to hold still, and the
/// hexagonal silhouette survives any amount of subdivision. So this welds the whole object
/// by <em>position</em> first, and carries texture coordinates per corner rather than per
/// vertex, so a seam where two textures meet stays a seam.
/// </para>
/// <para>
/// <b>Why not Loop's rules either, once welded.</b> That was tried, and it wrecked what it
/// touched: a lamp shade's panels sagged inward between their ribs and its rim came out
/// spiked. Loop is an <em>approximating</em> scheme — every original vertex moves toward the
/// average of its neighbours — and on a dense mesh that is invisible, while on a
/// twelve-sided shade it is the shape. Approximation also has no idea which edges are
/// creases, so it rounds off the rim of a lamp exactly as enthusiastically as it rounds the
/// lamp.
/// </para>
/// <para>
/// <b>What this does instead: PN triangles.</b> An <em>interpolating</em> scheme. Every
/// original vertex stays exactly where the artists put it, and the surface between them is
/// a cubic patch whose shape comes from the corner normals: the panels of a shade bow
/// outward to the cylinder their normals describe, and nothing anywhere can move inward
/// from the authored hull by more than the curve the normals imply. It cannot sag, because
/// there is no averaging in it.
/// </para>
/// <para>
/// <b>The normals it curves along stop at creases.</b> A single smoothed normal per position
/// is what made the earlier attempt shade a bell's rim as though the metal turned over
/// smoothly there. Faces are gathered into smoothing groups across edges they meet gently
/// at, a position carries one normal per group, and an edge between two groups is a crease:
/// it stays straight, and both sides agree that it does, so a crease cannot open a crack.
/// That one rule is also what keeps a flat cap flat — its normals stand perpendicular to
/// its own edges, and a perpendicular normal asks for no curvature at all.
/// </para>
/// </remarks>
public static class ObjectRounding
{
    /// <summary>Positions closer together than this are the same point.</summary>
    private const float Coincident = 1e-3f;

    /// <summary>
    /// How gently two faces must meet to be treated as one smooth surface, in degrees.
    /// </summary>
    /// <remarks>
    /// Sixty degrees, which is chosen against the objects this actually runs on rather than
    /// as a general default. They are named by hand (<c>SceneGeometry.RoundNames</c>) and
    /// they are all lathes: a lathe of twelve sides turns thirty degrees at each, of eight
    /// forty-five, of six sixty. At forty — the usual figure — the reception bell, which is
    /// eight-sided, was creased at every one of its own sides and came out exactly as
    /// faceted as it went in. What must still crease is the fold where a lathe meets its own
    /// cap or foot, and those are seventy degrees and over.
    /// </remarks>
    public const float CreaseDegrees = 60f;

    /// <summary>
    /// Welds loose triangles into the corner-and-pool form the rounding works on.
    /// </summary>
    /// <param name="triangles">Each triangle's three positions, texture coordinates, and surface.</param>
    /// <param name="positions">Receives the welded position pool.</param>
    /// <returns>The triangles, indexed into the pool.</returns>
    public static List<RoundedTriangle> Weld(
        IReadOnlyList<(Vector3 A, Vector3 B, Vector3 C, Vector2 Ua, Vector2 Ub, Vector2 Uc, int Surface)> triangles,
        List<Vector3> positions)
    {
        ArgumentNullException.ThrowIfNull(triangles);
        ArgumentNullException.ThrowIfNull(positions);

        Dictionary<(int, int, int), int> pool = [];
        List<RoundedTriangle> welded = [];

        int Of(Vector3 position)
        {
            (int, int, int) key = (
                (int)MathF.Round(position.X / Coincident),
                (int)MathF.Round(position.Y / Coincident),
                (int)MathF.Round(position.Z / Coincident));

            if (!pool.TryGetValue(key, out int index))
            {
                index = positions.Count;
                pool[key] = index;
                positions.Add(position);
            }

            return index;
        }

        foreach ((Vector3 a, Vector3 b, Vector3 c, Vector2 ua, Vector2 ub, Vector2 uc, int surface) in triangles)
        {
            welded.Add(new RoundedTriangle(
                new RoundedCorner(Of(a), ua),
                new RoundedCorner(Of(b), ub),
                new RoundedCorner(Of(c), uc),
                surface));
        }

        return welded;
    }

    /// <summary>
    /// A normal for every corner of every triangle, smoothed within a smoothing group.
    /// </summary>
    /// <param name="triangles">The welded mesh.</param>
    /// <param name="positions">Its position pool.</param>
    /// <param name="creaseDegrees">How gently two faces must meet to be smoothed together.</param>
    /// <returns>Three normals per triangle, in the order A, B, C.</returns>
    /// <remarks>
    /// Faces are unioned across every edge they meet gently at, and a position then carries
    /// one normal per group rather than one in all. Two faces that share a smooth edge are
    /// in the same group by construction, so they agree about that edge's normals exactly —
    /// which is what <see cref="Curve"/> needs to bend the edge the same way from both
    /// sides.
    /// </remarks>
    public static Vector3[] Creased(
        IReadOnlyList<RoundedTriangle> triangles,
        IReadOnlyList<Vector3> positions,
        float creaseDegrees = CreaseDegrees)
    {
        ArgumentNullException.ThrowIfNull(triangles);
        ArgumentNullException.ThrowIfNull(positions);

        int[] groups = Groups(triangles, positions, creaseDegrees, out Vector3[] faces, out float[] areas);

        // One normal per position and group, area-weighted. Area-weighted because a lathed
        // object is strips of long thin triangles and a fan of slivers at each end, and an
        // unweighted average lets the slivers outvote the surface.
        Dictionary<(int Position, int Group), Vector3> summed = [];

        for (int t = 0; t < triangles.Count; t++)
        {
            RoundedTriangle triangle = triangles[t];
            int group = Find(groups, t);

            foreach (int position in (ReadOnlySpan<int>)
                     [triangle.A.Position, triangle.B.Position, triangle.C.Position])
            {
                summed.TryGetValue((position, group), out Vector3 sum);
                summed[(position, group)] = sum + (faces[t] * areas[t]);
            }
        }

        var normals = new Vector3[triangles.Count * 3];

        for (int t = 0; t < triangles.Count; t++)
        {
            RoundedTriangle triangle = triangles[t];
            int group = Find(groups, t);

            normals[(t * 3) + 0] = Settle(summed[(triangle.A.Position, group)], faces[t]);
            normals[(t * 3) + 1] = Settle(summed[(triangle.B.Position, group)], faces[t]);
            normals[(t * 3) + 2] = Settle(summed[(triangle.C.Position, group)], faces[t]);
        }

        return normals;

        static Vector3 Settle(Vector3 sum, Vector3 face) =>
            sum.LengthSquared() > 1e-12f ? Vector3.Normalize(sum) : face;
    }

    /// <summary>
    /// Rounds the mesh off, keeping every authored vertex exactly where it is.
    /// </summary>
    /// <param name="triangles">The welded mesh.</param>
    /// <param name="positions">Its position pool.</param>
    /// <param name="levels">How many times to halve each edge; two is sixteen pieces.</param>
    /// <param name="creaseDegrees">How gently two faces must meet to be smoothed together.</param>
    /// <returns>The rounded triangles, with their own positions, normals and coordinates.</returns>
    /// <remarks>
    /// One cubic Bezier patch per triangle, in the form Vlachos gives: the corners are the
    /// authored vertices, each edge carries two control points placed by projecting the
    /// straight edge onto the plane of its end's normal, and the middle point is lifted to
    /// keep the patch from flattening. A crease edge — one whose two faces are in different
    /// smoothing groups, or which has only one face — takes the straight control points
    /// instead, which both of its sides compute identically.
    /// </remarks>
    public static List<CurvedTriangle> Curve(
        IReadOnlyList<RoundedTriangle> triangles,
        IReadOnlyList<Vector3> positions,
        int levels = 2,
        float creaseDegrees = CreaseDegrees)
    {
        ArgumentNullException.ThrowIfNull(triangles);
        ArgumentNullException.ThrowIfNull(positions);
        ArgumentOutOfRangeException.ThrowIfNegative(levels);

        Vector3[] normals = Creased(triangles, positions, creaseDegrees);
        int[] groups = Groups(triangles, positions, creaseDegrees, out _, out _);

        // Which edges bend. An edge used by one face is the object's own rim and stays
        // straight; an edge whose two faces are in different smoothing groups is a crease
        // and stays straight; anything else is the inside of a smooth surface.
        Dictionary<(int, int), List<int>> along = [];

        for (int t = 0; t < triangles.Count; t++)
        {
            RoundedTriangle triangle = triangles[t];

            Remember(triangle.A.Position, triangle.B.Position, t);
            Remember(triangle.B.Position, triangle.C.Position, t);
            Remember(triangle.C.Position, triangle.A.Position, t);
        }

        var bends = new HashSet<(int, int)>();
        var rims = new List<(int, int)>();

        foreach (((int, int) edge, List<int> faces) in along)
        {
            if (faces.Count == 2 && Find(groups, faces[0]) == Find(groups, faces[1]))
            {
                bends.Add(edge);
            }
            else
            {
                rims.Add(edge);
            }
        }

        // Which way each rim runs at each of its vertices. A crease is straight *across* —
        // that is what makes it a crease — and it does not follow that it is straight
        // *along*: the rim where a bell's dome meets its foot is an octagon, and an octagon
        // is the widest part of the bell and therefore its whole silhouette. Curving the
        // surface either side of it and leaving it an octagon rounds everything the eye does
        // not look at.
        //
        // So a rim is treated as a polyline in its own right and given the tangent a
        // Catmull-Rom spline would: half the way from the vertex before to the vertex after.
        // Both faces along the rim work it out from the same three positions and no face
        // enters into it, so they cannot disagree and a crease still cannot open a crack.
        // A vertex with any number of rim edges other than two is a junction rather than a
        // rim — a box corner has three — and is left alone, as is one where the rim turns
        // more sharply than <paramref name="creaseDegrees"/>, which is the corner of a
        // rectangular panel rather than a facet of a circle.
        var ends = new Dictionary<int, List<int>>();

        foreach ((int a, int b) in rims)
        {
            (ends.TryGetValue(a, out List<int>? fromA) ? fromA : ends[a] = []).Add(b);
            (ends.TryGetValue(b, out List<int>? fromB) ? fromB : ends[b] = []).Add(a);
        }

        float turns = MathF.Cos(creaseDegrees * MathF.PI / 180f);

        Vector3? Rim(int from, int to)
        {
            if (bends.Contains(Pair(from, to)) ||
                !ends.TryGetValue(from, out List<int>? both) ||
                both.Count != 2)
            {
                return null;
            }

            int before = both[0] == to ? both[1] : both[0];

            if (before == to)
            {
                return null;
            }

            Vector3 back = positions[from] - positions[before];
            Vector3 on = positions[to] - positions[from];

            if (back.LengthSquared() < 1e-12f || on.LengthSquared() < 1e-12f)
            {
                return null;
            }

            if (Vector3.Dot(Vector3.Normalize(back), Vector3.Normalize(on)) < turns)
            {
                return null;
            }

            return (positions[to] - positions[before]) / 2f;
        }

        int steps = 1 << levels;
        var pieces = new List<CurvedTriangle>(triangles.Count * steps * steps);

        for (int t = 0; t < triangles.Count; t++)
        {
            RoundedTriangle triangle = triangles[t];

            Vector3 p1 = positions[triangle.A.Position];
            Vector3 p2 = positions[triangle.B.Position];
            Vector3 p3 = positions[triangle.C.Position];

            Vector3 n1 = normals[(t * 3) + 0];
            Vector3 n2 = normals[(t * 3) + 1];
            Vector3 n3 = normals[(t * 3) + 2];

            Vector3 b210 = Control(p1, p2, n1, triangle.A.Position, triangle.B.Position);
            Vector3 b120 = Control(p2, p1, n2, triangle.B.Position, triangle.A.Position);
            Vector3 b021 = Control(p2, p3, n2, triangle.B.Position, triangle.C.Position);
            Vector3 b012 = Control(p3, p2, n3, triangle.C.Position, triangle.B.Position);
            Vector3 b102 = Control(p3, p1, n3, triangle.C.Position, triangle.A.Position);
            Vector3 b201 = Control(p1, p3, n1, triangle.A.Position, triangle.C.Position);

            Vector3 middle = (b210 + b120 + b021 + b012 + b102 + b201) / 6f;
            Vector3 corners = (p1 + p2 + p3) / 3f;
            Vector3 b111 = middle + ((middle - corners) / 2f);

            // A regular grid in barycentric coordinates. Every triangle is cut the same way
            // and its edges are cut at the same parameters from both sides, so two
            // neighbours put vertices at the same points along the edge they share.
            for (int i = 0; i < steps; i++)
            {
                for (int j = 0; j + i < steps; j++)
                {
                    pieces.Add(Piece(
                        (i, j), (i + 1, j), (i, j + 1)));

                    if (i + j + 2 <= steps)
                    {
                        pieces.Add(Piece(
                            (i + 1, j), (i + 1, j + 1), (i, j + 1)));
                    }
                }
            }

            CurvedTriangle Piece((int I, int J) a, (int I, int J) b, (int I, int J) c) =>
                new(At(a), At(b), At(c), triangle.Surface);

            CurvedCorner At((int I, int J) cell)
            {
                float v = (float)cell.I / steps;
                float w = (float)cell.J / steps;
                float u = 1f - v - w;

                Vector3 position =
                    (p1 * u * u * u) +
                    (p2 * v * v * v) +
                    (p3 * w * w * w) +
                    (b210 * 3f * u * u * v) +
                    (b120 * 3f * u * v * v) +
                    (b201 * 3f * u * u * w) +
                    (b021 * 3f * v * v * w) +
                    (b102 * 3f * u * w * w) +
                    (b012 * 3f * v * w * w) +
                    (b111 * 6f * u * v * w);

                Vector3 normal = (n1 * u) + (n2 * v) + (n3 * w);

                return new CurvedCorner(
                    position,
                    normal.LengthSquared() > 1e-12f ? Vector3.Normalize(normal) : n1,
                    (triangle.A.TexCoord * u) +
                    (triangle.B.TexCoord * v) +
                    (triangle.C.TexCoord * w));
            }

            // The control point a third of the way from one end of an edge toward the
            // other. Three cases, in order: a smooth edge bends along the surface, a rim
            // bends along itself, and anything else stays straight.
            Vector3 Control(Vector3 from, Vector3 to, Vector3 normal, int at, int toward)
            {
                if (bends.Contains(Pair(at, toward)))
                {
                    return Toward(from, to, normal, true);
                }

                return Rim(at, toward) is { } tangent
                    ? from + (tangent / 3f)
                    : Toward(from, to, normal, false);
            }
        }

        return pieces;

        void Remember(int a, int b, int face)
        {
            (int, int) key = Pair(a, b);

            if (!along.TryGetValue(key, out List<int>? faces))
            {
                faces = [];
                along[key] = faces;
            }

            faces.Add(face);
        }

        // The control point a third of the way from one end toward the other, dropped onto
        // the plane the end's normal describes. A normal square to the edge leaves it
        // exactly where a straight edge would put it, which is why a flat face stays flat
        // without being asked to.
        static Vector3 Toward(Vector3 from, Vector3 to, Vector3 normal, bool bends)
        {
            Vector3 straight = ((2f * from) + to) / 3f;

            return bends
                ? straight - (normal * (Vector3.Dot(to - from, normal) / 3f))
                : straight;
        }
    }

    /// <summary>Smooth normals over the welded pool, area-weighted and across everything.</summary>
    /// <param name="triangles">The mesh.</param>
    /// <param name="positions">Its pool.</param>
    /// <returns>One normal per pooled position.</returns>
    /// <remarks>
    /// The flat version of <see cref="Creased"/>, kept for the case where an object is to be
    /// shaded smooth without being reshaped. The cross products are taken the same way round
    /// as the scene's flat path takes them, so the shading sign agrees with what the
    /// surfaces had before they were rounded.
    /// </remarks>
    public static Vector3[] Normals(
        IReadOnlyList<RoundedTriangle> triangles, IReadOnlyList<Vector3> positions)
    {
        ArgumentNullException.ThrowIfNull(triangles);
        ArgumentNullException.ThrowIfNull(positions);

        var normals = new Vector3[positions.Count];

        foreach (RoundedTriangle triangle in triangles)
        {
            Vector3 across = Vector3.Cross(
                positions[triangle.B.Position] - positions[triangle.A.Position],
                positions[triangle.C.Position] - positions[triangle.A.Position]);

            normals[triangle.A.Position] += across;
            normals[triangle.B.Position] += across;
            normals[triangle.C.Position] += across;
        }

        for (int i = 0; i < normals.Length; i++)
        {
            normals[i] = normals[i].LengthSquared() > 1e-12f
                ? Vector3.Normalize(normals[i])
                : Vector3.UnitY;
        }

        return normals;
    }

    /// <summary>Gathers faces into smoothing groups across the edges they meet gently at.</summary>
    /// <returns>A union-find parent array over the faces.</returns>
    private static int[] Groups(
        IReadOnlyList<RoundedTriangle> triangles,
        IReadOnlyList<Vector3> positions,
        float creaseDegrees,
        out Vector3[] faces,
        out float[] areas)
    {
        faces = new Vector3[triangles.Count];
        areas = new float[triangles.Count];

        for (int t = 0; t < triangles.Count; t++)
        {
            RoundedTriangle triangle = triangles[t];

            Vector3 across = Vector3.Cross(
                positions[triangle.B.Position] - positions[triangle.A.Position],
                positions[triangle.C.Position] - positions[triangle.A.Position]);

            areas[t] = 0.5f * across.Length();
            faces[t] = across.LengthSquared() > 1e-12f
                ? Vector3.Normalize(across)
                : Vector3.UnitY;
        }

        Dictionary<(int, int), List<int>> along = [];

        for (int t = 0; t < triangles.Count; t++)
        {
            RoundedTriangle triangle = triangles[t];

            Remember(triangle.A.Position, triangle.B.Position, t);
            Remember(triangle.B.Position, triangle.C.Position, t);
            Remember(triangle.C.Position, triangle.A.Position, t);
        }

        var parents = new int[triangles.Count];

        for (int t = 0; t < parents.Length; t++)
        {
            parents[t] = t;
        }

        float gently = MathF.Cos(creaseDegrees * MathF.PI / 180f);

        foreach ((_, List<int> sharing) in along)
        {
            if (sharing.Count == 2 && Vector3.Dot(faces[sharing[0]], faces[sharing[1]]) >= gently)
            {
                Union(parents, sharing[0], sharing[1]);
            }
        }

        return parents;

        void Remember(int a, int b, int face)
        {
            (int, int) key = Pair(a, b);

            if (!along.TryGetValue(key, out List<int>? sharing))
            {
                sharing = [];
                along[key] = sharing;
            }

            sharing.Add(face);
        }
    }

    private static (int, int) Pair(int a, int b) => a < b ? (a, b) : (b, a);

    private static int Find(int[] parents, int of)
    {
        while (parents[of] != of)
        {
            parents[of] = parents[parents[of]];
            of = parents[of];
        }

        return of;
    }

    private static void Union(int[] parents, int a, int b)
    {
        int rootA = Find(parents, a);
        int rootB = Find(parents, b);

        if (rootA != rootB)
        {
            parents[rootB] = rootA;
        }
    }
}
