using System.Numerics;

namespace GK3Reborn.Formats.Models;

/// <summary>One refinement of a triangle mesh, positions and texture coordinates.</summary>
/// <param name="Positions">The refined vertices: the originals, then one per edge.</param>
/// <param name="TexCoords">Their texture coordinates, in the same order.</param>
/// <param name="Indices">Four triangles for every triangle that went in.</param>
public readonly record struct RefinedMesh(
    Vector3[] Positions, Vector2[] TexCoords, ushort[] Indices);

/// <summary>
/// Loop subdivision, for rounding off a 1999 silhouette.
/// </summary>
/// <remarks>
/// <para>
/// GK3's characters are already shaded smoothly — their vertex normals are welded and
/// agree to within a rounding error, across texture seams included — so what makes a head
/// read as low-polygon is not its shading but its outline. Grace's hair is twenty
/// triangles and Madeline's is thirteen, and no amount of work in texture space moves the
/// edge of a twenty-sided shape. Subdivision is the only thing that does.
/// </para>
/// <para>
/// <b>Boundary vertices are pinned rather than smoothed.</b> The textbook rule moves them
/// along the boundary curve, and where two submeshes meet — the hairline, the ear — that
/// would be fine, because both sides move identically and the seam stays shut. The rim at
/// the neck is the problem: it is the edge of the head shell, nothing on the other side is
/// being refined with it, and a rim that shrinks opens a hole in someone's throat. Pinning
/// costs a slightly flatter surface within one row of triangles of a boundary and cannot
/// open a gap anywhere.
/// </para>
/// <para>
/// Texture coordinates are interpolated linearly rather than subdivided. The face texture
/// is a composited surface — <c>FaceController</c> blits eyes and a mouth onto it at
/// coordinates <c>CHARACTERS.TXT</c> gives in pixels — so the mapping has to stay where it
/// was put. Smoothing the UVs as well would move a character's mouth.
/// </para>
/// </remarks>
public static class LoopSubdivision
{
    /// <summary>How many vertices one refinement of a mesh would produce.</summary>
    /// <param name="positions">How many vertices it has now.</param>
    /// <param name="indices">Its triangles.</param>
    /// <returns>The vertex count after one level.</returns>
    /// <remarks>
    /// Asked before refining rather than discovered after, because indices are 16-bit and
    /// a level that would overflow has to be declined rather than wrapped.
    /// </remarks>
    public static int Predict(int positions, ReadOnlySpan<ushort> indices)
    {
        HashSet<(int, int)> edges = [];

        for (int i = 0; i + 2 < indices.Length; i += 3)
        {
            edges.Add(Edge(indices[i], indices[i + 1]));
            edges.Add(Edge(indices[i + 1], indices[i + 2]));
            edges.Add(Edge(indices[i + 2], indices[i]));
        }

        return positions + edges.Count;
    }

    /// <summary>Refines a triangle mesh once.</summary>
    /// <param name="positions">Vertex positions.</param>
    /// <param name="texCoords">Texture coordinates, one per position.</param>
    /// <param name="indices">Triangles, three indices each.</param>
    /// <returns>The refined mesh.</returns>
    public static RefinedMesh Refine(
        ReadOnlySpan<Vector3> positions,
        ReadOnlySpan<Vector2> texCoords,
        ReadOnlySpan<ushort> indices)
    {
        int count = positions.Length;

        // Every edge, with how many triangles use it and the vertices across from it. Two
        // faces make it interior; one makes it a boundary, which both the edge rule and the
        // vertex rule treat differently.
        Dictionary<(int, int), Span> edges = [];

        for (int i = 0; i + 2 < indices.Length; i += 3)
        {
            Record(edges, indices[i], indices[i + 1], indices[i + 2]);
            Record(edges, indices[i + 1], indices[i + 2], indices[i]);
            Record(edges, indices[i + 2], indices[i], indices[i + 1]);
        }

        var refined = new List<Vector3>(count + edges.Count);
        var mapped = new List<Vector2>(count + edges.Count);

        // The originals keep their index, so the caller can still say which refined vertex
        // came from which authored one. Their positions are replaced below.
        for (int i = 0; i < count; i++)
        {
            refined.Add(positions[i]);
            mapped.Add(i < texCoords.Length ? texCoords[i] : Vector2.Zero);
        }

        Dictionary<(int, int), int> made = new(edges.Count);

        foreach (KeyValuePair<(int, int), Span> entry in edges)
        {
            (int a, int b) = entry.Key;
            Span span = entry.Value;

            Vector3 point = span.Faces >= 2
                ? ((positions[a] + positions[b]) * (3f / 8f)) +
                  ((positions[span.Left] + positions[span.Right]) * (1f / 8f))
                : (positions[a] + positions[b]) * 0.5f;

            made[entry.Key] = refined.Count;
            refined.Add(point);
            mapped.Add(((a < texCoords.Length ? texCoords[a] : Vector2.Zero) +
                        (b < texCoords.Length ? texCoords[b] : Vector2.Zero)) * 0.5f);
        }

        Smooth(positions, edges, refined);

        var output = new List<ushort>(indices.Length * 4);

        for (int i = 0; i + 2 < indices.Length; i += 3)
        {
            int a = indices[i];
            int b = indices[i + 1];
            int c = indices[i + 2];

            int ab = made[Edge(a, b)];
            int bc = made[Edge(b, c)];
            int ca = made[Edge(c, a)];

            Triangle(output, a, ab, ca);
            Triangle(output, ab, b, bc);
            Triangle(output, ca, bc, c);
            Triangle(output, ab, bc, ca);
        }

        return new RefinedMesh([.. refined], [.. mapped], [.. output]);
    }

    /// <summary>Moves the original vertices onto the limit surface.</summary>
    /// <remarks>
    /// Warren's weights. A vertex of valence <c>n</c> keeps <c>1 − nβ</c> of itself and
    /// shares <c>β</c> with each neighbour, and β is chosen so the limit surface is smooth
    /// at every valence rather than only at six. A vertex on a boundary, or one whose
    /// neighbourhood is not a fan at all, is left exactly where it is.
    /// </remarks>
    private static void Smooth(
        ReadOnlySpan<Vector3> positions,
        Dictionary<(int, int), Span> edges,
        List<Vector3> refined)
    {
        var neighbours = new List<int>[positions.Length];
        var onBoundary = new bool[positions.Length];

        foreach (KeyValuePair<(int, int), Span> entry in edges)
        {
            (int a, int b) = entry.Key;

            (neighbours[a] ??= []).Add(b);
            (neighbours[b] ??= []).Add(a);

            if (entry.Value.Faces < 2)
            {
                onBoundary[a] = true;
                onBoundary[b] = true;
            }
        }

        for (int i = 0; i < positions.Length; i++)
        {
            List<int>? ring = neighbours[i];

            if (onBoundary[i] || ring is not { Count: >= 3 })
            {
                continue;
            }

            int valence = ring.Count;
            float cosine = MathF.Cos(2f * MathF.PI / valence);
            float share = ((5f / 8f) - (float)Math.Pow((3f / 8f) + (0.25f * cosine), 2)) / valence;

            Vector3 total = Vector3.Zero;

            foreach (int neighbour in ring)
            {
                total += positions[neighbour];
            }

            refined[i] = (positions[i] * (1f - (valence * share))) + (total * share);
        }
    }

    /// <summary>Notes that a triangle uses an edge, and what lies across it.</summary>
    private static void Record(
        Dictionary<(int, int), Span> edges, int a, int b, int across)
    {
        (int, int) key = Edge(a, b);

        if (edges.TryGetValue(key, out Span span))
        {
            // A third face on one edge is not a surface. The extra one is ignored rather
            // than refused: GK3's meshes are hand-built and a stray duplicate triangle
            // should cost a slightly wrong edge point, not the whole character.
            edges[key] = span with
            {
                Faces = span.Faces + 1,
                Right = span.Faces == 1 ? across : span.Right,
            };

            return;
        }

        edges[key] = new Span(1, across, across);
    }

    /// <summary>An edge, keyed so that both triangles using it agree on the name.</summary>
    private static (int, int) Edge(int a, int b) => a < b ? (a, b) : (b, a);

    private static void Triangle(List<ushort> output, int a, int b, int c)
    {
        output.Add((ushort)a);
        output.Add((ushort)b);
        output.Add((ushort)c);
    }

    /// <summary>What is on either side of an edge.</summary>
    /// <param name="Faces">How many triangles use it.</param>
    /// <param name="Left">The vertex across from it in the first triangle.</param>
    /// <param name="Right">The vertex across from it in the second, if there is one.</param>
    private readonly record struct Span(int Faces, int Left, int Right);
}
