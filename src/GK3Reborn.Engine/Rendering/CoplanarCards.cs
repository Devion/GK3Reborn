using System.Numerics;
using GK3Reborn.Formats.Models;
using GK3Reborn.Formats.Scenes;

namespace GK3Reborn.Rendering;

/// <summary>
/// Gives a room's zero-thickness cards a thickness, so a depth buffer can tell their two
/// sides apart.
/// </summary>
public static class CoplanarCards
{
    /// <summary>How far each face of a card moves along its own normal, in scene units.</summary>
    public const float Separation = 0.05f;

    /// <summary>
    /// How far each of a room's surfaces has to move to stop coinciding with another.
    /// </summary>
    /// <param name="scene">The room.</param>
    /// <param name="separation">How far a face of a card moves.</param>
    /// <returns>
    /// One offset per surface, in world units, zero for every surface that coincides with
    /// nothing — which is nearly all of them.
    /// </returns>
    public static Vector3[] Apart(BspFile scene, float separation = Separation)
    {
        ArgumentNullException.ThrowIfNull(scene);

        var offsets = new Vector3[scene.Surfaces.Count];
        Face?[] faces = Faces(scene);

        // Bucketed by the plane's distance from the origin, rounded to a unit. A card's two
        // faces have the same distance and opposite normals, so they land in the same
        // bucket or in the one next door, and nothing outside those two can coincide with
        // either. Without this the search is every surface against every other, which on a
        // room of three thousand is nine million comparisons of a dozen vertices each.
        Dictionary<int, List<int>> buckets = [];

        for (int s = 0; s < faces.Length; s++)
        {
            if (faces[s] is { } face)
            {
                int bucket = (int)MathF.Round(MathF.Abs(face.Distance));
                (buckets.TryGetValue(bucket, out List<int>? found)
                    ? found
                    : buckets[bucket] = []).Add(s);
            }
        }

        foreach ((int bucket, List<int> here) in buckets)
        {
            List<int> near = buckets.TryGetValue(bucket + 1, out List<int>? above) ? above : [];

            foreach (int a in here)
            {
                foreach (int b in here.Concat(near))
                {
                    if (a != b && Coincide(faces[a]!, faces[b]!))
                    {
                        offsets[a] = faces[a]!.Normal * separation;
                        offsets[b] = faces[b]!.Normal * separation;
                    }
                }
            }
        }

        return offsets;
    }

    /// <summary>
    /// Which of a model's submeshes are the two faces of one sheet of paper.
    /// </summary>
    /// <param name="model">The model.</param>
    /// <returns>
    /// The mesh and submesh of each face that has an exact opposite, which is empty for
    /// nearly every model in the game.
    /// </returns>
    public static IReadOnlySet<(int Mesh, int Submesh)> BackToBack(ModFile model)
    {
        ArgumentNullException.ThrowIfNull(model);

        List<((int Mesh, int Submesh) Where, Face Plane)> flat = [];

        for (int mesh = 0; mesh < model.Meshes.Count; mesh++)
        {
            ModMesh group = model.Meshes[mesh];

            for (int submesh = 0; submesh < group.Submeshes.Count; submesh++)
            {
                if (Flat(group.Submeshes[submesh], group.MeshToLocal) is { } plane)
                {
                    flat.Add(((mesh, submesh), plane));
                }
            }
        }

        HashSet<(int Mesh, int Submesh)> paired = [];

        for (int a = 0; a < flat.Count; a++)
        {
            for (int b = a + 1; b < flat.Count; b++)
            {
                if (Coincide(flat[a].Plane, flat[b].Plane))
                {
                    paired.Add(flat[a].Where);
                    paired.Add(flat[b].Where);
                }
            }
        }

        return paired;
    }

    /// <summary>
    /// A submesh's plane, in the model's own space, or null when it is not a flat sheet.
    /// </summary>
    /// <param name="submesh">The submesh.</param>
    /// <param name="meshToLocal">Where its mesh group sits within the model.</param>
    /// <returns>The plane and the points on it, or null.</returns>
    private static Face? Flat(ModSubmesh submesh, Matrix4x4 meshToLocal)
    {
        if (submesh.Positions.Length < 3 || submesh.Indices.Length < 3)
        {
            return null;
        }

        // The first triangle's plane, and then every vertex against it. Taken from the
        // indices rather than from the first three positions because a submesh may carry
        // vertices no triangle uses — the poem's do, four of them, sixty units out.
        Vector3 At(int i) => Vector3.Transform(submesh.Positions[submesh.Indices[i]], meshToLocal);

        Vector3 normal = Vector3.Cross(At(1) - At(0), At(2) - At(0));

        if (normal.LengthSquared() <= 1e-12f)
        {
            return null;
        }

        normal = Vector3.Normalize(normal);

        Vector3 origin = At(0);
        var points = new Vector3[submesh.Indices.Length];

        for (int i = 0; i < submesh.Indices.Length; i++)
        {
            points[i] = At(i);

            // Against a point on the plane rather than against the plane's distance from
            // the origin, for the reason Coincide is: see there.
            if (MathF.Abs(Vector3.Dot(normal, points[i] - origin)) > Tolerance)
            {
                return null;
            }
        }

        return new Face(normal, Vector3.Dot(normal, origin), origin, points);
    }

    /// <summary>A surface's plane and the points it covers.</summary>
    /// <param name="Normal">Which way it faces.</param>
    /// <param name="Distance">How far the plane is from the origin, for bucketing only.</param>
    /// <param name="Origin">A point on it, which is what coincidence is measured against.</param>
    /// <param name="Points">Every point of it, for the overlap test.</param>
    private sealed record Face(Vector3 Normal, float Distance, Vector3 Origin, Vector3[] Points);

    /// <summary>Reads every surface's plane, or null where it has no polygon to take one from.</summary>
    private static Face?[] Faces(BspFile scene)
    {
        var points = new List<Vector3>[scene.Surfaces.Count];
        var first = new BspPolygon?[scene.Surfaces.Count];

        foreach (BspPolygon polygon in scene.Polygons)
        {
            int surface = polygon.SurfaceIndex;

            if (surface < 0 || surface >= points.Length)
            {
                continue;
            }

            points[surface] ??= [];
            first[surface] ??= polygon;

            for (int i = 0; i < polygon.VertexIndexCount; i++)
            {
                points[surface].Add(scene.Vertices[scene.VertexIndices[polygon.VertexIndexOffset + i]]);
            }
        }

        var faces = new Face?[scene.Surfaces.Count];

        for (int s = 0; s < faces.Length; s++)
        {
            if (first[s] is not { VertexIndexCount: >= 3 } polygon)
            {
                continue;
            }

            Vector3 At(int i) => scene.Vertices[scene.VertexIndices[polygon.VertexIndexOffset + i]];

            Vector3 normal = Vector3.Cross(At(1) - At(0), At(2) - At(0));

            if (normal.LengthSquared() <= 1e-12f)
            {
                continue;
            }

            normal = Vector3.Normalize(normal);
            faces[s] = new Face(normal, Vector3.Dot(normal, At(0)), At(0), [.. points[s]]);
        }

        return faces;
    }

    /// <summary>How far apart two planes may be and still count as the same one.</summary>
    private const float Tolerance = 0.01f;

    /// <summary>Whether two surfaces are the two sides of one card.</summary>
    private static bool Coincide(Face a, Face b)
    {
        if (Vector3.Dot(a.Normal, b.Normal) > -0.9998f)
        {
            return false;
        }

        foreach (Vector3 point in b.Points)
        {
            if (MathF.Abs(Vector3.Dot(a.Normal, point - a.Origin)) > Tolerance)
            {
                return false;
            }
        }

        return Overlap(a, b);
    }

    /// <summary>Whether two coplanar surfaces cover any of the same ground.</summary>
    private static bool Overlap(Face a, Face b)
    {
        Vector3 across = Vector3.Normalize(Vector3.Cross(
            MathF.Abs(a.Normal.Y) > 0.9f ? Vector3.UnitX : Vector3.UnitY, a.Normal));

        Vector3 up = Vector3.Cross(a.Normal, across);

        (float Low, float High) Span(Vector3[] points, Vector3 axis)
        {
            float low = float.MaxValue;
            float high = float.MinValue;

            foreach (Vector3 point in points)
            {
                float at = Vector3.Dot(point, axis);
                low = MathF.Min(low, at);
                high = MathF.Max(high, at);
            }

            return (low, high);
        }

        (float Low, float High) au = Span(a.Points, across);
        (float Low, float High) bu = Span(b.Points, across);
        (float Low, float High) av = Span(a.Points, up);
        (float Low, float High) bv = Span(b.Points, up);

        return MathF.Min(au.High, bu.High) - MathF.Max(au.Low, bu.Low) > 0f &&
               MathF.Min(av.High, bv.High) - MathF.Max(av.Low, bv.Low) > 0f;
    }
}
