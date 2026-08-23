// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Numerics;

namespace GK3Reborn.Rendering;

/// <summary>One corner of one triangle, before or after rounding.</summary>
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

/// <summary>
/// Rounds a whole scene object off, across every surface it is made of.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Formats.Models.LoopSubdivision"/> rounds a character's head and cannot round a
/// bell, for a structural reason: it pins boundary vertices, and a lathed object is strips
/// and caps whose vertices are <b>all</b> on a boundary — the rim between the side of the
/// bell and its top belongs to two surfaces, so refining each surface alone sees it as an
/// edge to hold still, and the hexagonal silhouette survives any amount of subdivision.
/// </para>
/// <para>
/// So this welds the whole object by <em>position</em> first — the rim becomes interior the
/// moment both surfaces are in the same mesh — and carries texture coordinates per corner
/// rather than per vertex, so a seam where two textures meet stays a seam. Edges genuinely
/// open on one side are smoothed along the boundary curve instead of being pinned, which is
/// what turns a hexagonal rim into something close to a circle.
/// </para>
/// <para>
/// The rules are Loop's: an interior edge midpoint takes three eighths of its ends and one
/// eighth of the two opposite corners; a boundary midpoint is the middle of its edge; an
/// interior vertex is drawn toward the average of its neighbours by a weight set by how many
/// it has; a boundary vertex takes three quarters of itself and one eighth of each boundary
/// neighbour.
/// </para>
/// </remarks>
public static class ObjectRounding
{
    /// <summary>Positions closer together than this are the same point.</summary>
    private const float Coincident = 1e-3f;

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
    /// One level of subdivision: every triangle becomes four, and every vertex moves.
    /// </summary>
    /// <param name="triangles">The mesh, replaced by the refined one.</param>
    /// <param name="positions">The welded pool, replaced likewise.</param>
    public static (List<RoundedTriangle> Triangles, List<Vector3> Positions) Refine(
        IReadOnlyList<RoundedTriangle> triangles, IReadOnlyList<Vector3> positions)
    {
        ArgumentNullException.ThrowIfNull(triangles);
        ArgumentNullException.ThrowIfNull(positions);

        // Every edge, how often it is used, and what sits opposite it. An edge used once is
        // a boundary; twice is interior; more is non-manifold and treated as boundary,
        // which moves nothing it should not.
        Dictionary<(int, int), (int Count, Vector3 Opposite)> edges = [];

        static (int, int) Edge(int a, int b) => a < b ? (a, b) : (b, a);

        foreach (RoundedTriangle triangle in triangles)
        {
            Add(triangle.A.Position, triangle.B.Position, positions[triangle.C.Position]);
            Add(triangle.B.Position, triangle.C.Position, positions[triangle.A.Position]);
            Add(triangle.C.Position, triangle.A.Position, positions[triangle.B.Position]);

            void Add(int a, int b, Vector3 opposite)
            {
                (int, int) key = Edge(a, b);

                edges[key] = edges.TryGetValue(key, out (int Count, Vector3 Opposite) seen)
                    ? (seen.Count + 1, seen.Opposite + opposite)
                    : (1, opposite);
            }
        }

        // Each vertex's neighbours, split by whether the edge to them is a boundary.
        var around = new List<int>[positions.Count];
        var rim = new List<int>[positions.Count];

        foreach (((int a, int b), (int count, Vector3 _)) in edges)
        {
            if (count == 2)
            {
                (around[a] ??= []).Add(b);
                (around[b] ??= []).Add(a);
            }
            else
            {
                (rim[a] ??= []).Add(b);
                (rim[b] ??= []).Add(a);
            }
        }

        // The repositioned originals. A vertex on any boundary follows the boundary rule —
        // three quarters itself, one eighth each of two boundary neighbours — which is what
        // lets a hexagonal rim relax towards a circle. Interior vertices follow Loop's
        // valence weight.
        var moved = new Vector3[positions.Count];

        for (int i = 0; i < positions.Count; i++)
        {
            if (rim[i] is { Count: 2 } edge)
            {
                moved[i] = (0.75f * positions[i]) +
                           (0.125f * positions[edge[0]]) +
                           (0.125f * positions[edge[1]]);
            }
            else if (rim[i] is { Count: > 0 } || around[i] is not { Count: >= 3 } ring)
            {
                moved[i] = positions[i];
            }
            else
            {
                int n = ring.Count;
                float beta = n == 3 ? 3f / 16f : 3f / (8f * n);

                Vector3 sum = Vector3.Zero;

                foreach (int neighbour in ring)
                {
                    sum += positions[neighbour];
                }

                moved[i] = ((1f - (n * beta)) * positions[i]) + (beta * sum);
            }
        }

        // The midpoints, one welded position per edge however many triangles use it.
        List<Vector3> refined = [.. moved];
        Dictionary<(int, int), int> midpoints = [];

        int MidpointOf(int a, int b)
        {
            (int, int) key = Edge(a, b);

            if (midpoints.TryGetValue(key, out int index))
            {
                return index;
            }

            (int count, Vector3 opposite) = edges[key];

            Vector3 position = count == 2
                ? (0.375f * (positions[a] + positions[b])) + (0.125f * opposite)
                : 0.5f * (positions[a] + positions[b]);

            index = refined.Count;
            midpoints[key] = index;
            refined.Add(position);

            return index;
        }

        List<RoundedTriangle> pieces = new(triangles.Count * 4);

        foreach (RoundedTriangle triangle in triangles)
        {
            RoundedCorner a = triangle.A;
            RoundedCorner b = triangle.B;
            RoundedCorner c = triangle.C;

            // The texture coordinate of a midpoint is this triangle's own average, kept per
            // corner: a seam between two textures gets one position and two coordinates,
            // which is exactly what a seam is.
            var ab = new RoundedCorner(
                MidpointOf(a.Position, b.Position), (a.TexCoord + b.TexCoord) / 2f);
            var bc = new RoundedCorner(
                MidpointOf(b.Position, c.Position), (b.TexCoord + c.TexCoord) / 2f);
            var ca = new RoundedCorner(
                MidpointOf(c.Position, a.Position), (c.TexCoord + a.TexCoord) / 2f);

            pieces.Add(new RoundedTriangle(a, ab, ca, triangle.Surface));
            pieces.Add(new RoundedTriangle(ab, b, bc, triangle.Surface));
            pieces.Add(new RoundedTriangle(ca, bc, c, triangle.Surface));
            pieces.Add(new RoundedTriangle(ab, bc, ca, triangle.Surface));
        }

        return (pieces, refined);
    }

    /// <summary>Smooth normals over the welded pool, area-weighted.</summary>
    /// <param name="triangles">The mesh.</param>
    /// <param name="positions">Its pool.</param>
    /// <returns>One normal per pooled position.</returns>
    /// <remarks>
    /// The cross products are taken the same way round as the scene's flat path takes them,
    /// so the shading sign agrees with what the surfaces had before they were rounded.
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
}
