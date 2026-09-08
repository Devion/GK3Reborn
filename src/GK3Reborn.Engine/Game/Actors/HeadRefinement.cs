using System.Numerics;
using GK3Reborn.Formats.Models;

namespace GK3Reborn.Game.Actors;

/// <summary>
/// A character's head as the clips address it, kept beside the head that is drawn.
/// </summary>
/// <param name="Mesh">Which of the model's meshes is the head.</param>
/// <param name="Rest">
/// The authored vertex positions, per submesh, in the order a <c>.ACT</c> shapes them.
/// This is what a clip's vertices are compared against to recover the head's motion. Kept
/// whole, including the axis triad, because its length is what says whether a clip is
/// talking about this mesh at all.
/// </param>
/// <param name="Sample">
/// Which of those vertices to actually fit with, per submesh.
/// </param>
/// <param name="Span">
/// How wide the head is, so a fit's leftover can be reported as a fraction of it rather
/// than in scene units that mean nothing on their own.
/// </param>
public sealed record HeadRig(int Mesh, Vector3[][] Rest, int[][] Sample, float Span);

/// <summary>
/// Gives a character a denser head without invalidating a single frame of animation.
/// </summary>
public static class HeadRefinement
{
    /// <summary>The most levels worth applying.</summary>
    public const int MaximumLevels = 3;

    /// <summary>Positions closer together than this are the same point.</summary>
    private const float Coincident = 1e-3f;

    /// <summary>Refines a character's head, and says how to drive it.</summary>
    /// <param name="model">The character, as parsed.</param>
    /// <param name="levels">How many times to subdivide. Clamped to <see cref="MaximumLevels"/>.</param>
    /// <returns>
    /// The model to draw and the rig to drive its head with, or the model unchanged and no
    /// rig when it has no head to refine.
    /// </returns>
    public static (ModFile Model, HeadRig? Rig) Apply(ModFile model, int levels)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (levels <= 0 || CharacterHead.Find(model) is not { } head)
        {
            return (model, null);
        }

        ModMesh mesh = model.Meshes[head];

        if (mesh.Submeshes.Count == 0)
        {
            return (model, null);
        }

        Vector3[][] rest = [.. mesh.Submeshes.Select(s => s.Positions.ToArray())];

        var refined = new List<ModSubmesh>(mesh.Submeshes.Count);

        foreach (ModSubmesh submesh in mesh.Submeshes)
        {
            refined.Add(Refine(submesh, Math.Min(levels, MaximumLevels)));
        }

        Weld(refined);

        var meshes = new List<ModMesh>(model.Meshes);
        meshes[head] = mesh with { Submeshes = refined };

        return (
            ModFile.FromMeshes(model.Name, meshes, model.IsBillboard),
            new HeadRig(head, rest, Sampled(rest), Extent(rest)));
    }

    /// <summary>Subdivides one submesh as far as its 16-bit indices allow.</summary>
    private static ModSubmesh Refine(ModSubmesh submesh, int levels)
    {
        if (submesh.Positions.Length == 0 || submesh.Indices.Length < 3)
        {
            return submesh;
        }

        // The winding the model was authored with, read off its own normals rather than
        // assumed. GK3's world is left-handed and its triangles carry a fourth index nobody
        // has explained; taking a guess here shades a face inside out.
        float facing = Facing(submesh);

        Vector3[] positions = submesh.Positions;
        Vector2[] texCoords = submesh.TexCoords;
        ushort[] indices = submesh.Indices;

        for (int level = 0; level < levels; level++)
        {
            if (LoopSubdivision.Predict(positions.Length, indices) > ushort.MaxValue)
            {
                break;
            }

            RefinedMesh next = LoopSubdivision.Refine(positions, texCoords, indices);
            positions = next.Positions;
            texCoords = next.TexCoords;
            indices = next.Indices;
        }

        if (ReferenceEquals(positions, submesh.Positions))
        {
            return submesh;
        }

        return submesh with
        {
            Positions = positions,
            TexCoords = texCoords,
            Indices = indices,
            Normals = Normals(positions, indices, facing),
        };
    }

    /// <summary>Which way a cross product points on this submesh, +1 or −1.</summary>
    private static float Facing(ModSubmesh submesh)
    {
        float agreement = 0f;

        for (int i = 0; i + 2 < submesh.Indices.Length; i += 3)
        {
            int a = submesh.Indices[i];
            int b = submesh.Indices[i + 1];
            int c = submesh.Indices[i + 2];

            if (a >= submesh.Normals.Length || b >= submesh.Normals.Length ||
                c >= submesh.Normals.Length)
            {
                continue;
            }

            Vector3 face = Vector3.Cross(
                submesh.Positions[b] - submesh.Positions[a],
                submesh.Positions[c] - submesh.Positions[a]);

            agreement += Vector3.Dot(
                face,
                submesh.Normals[a] + submesh.Normals[b] + submesh.Normals[c]);
        }

        return agreement < 0f ? -1f : 1f;
    }

    /// <summary>Area-weighted vertex normals for a refined submesh.</summary>
    private static Vector3[] Normals(Vector3[] positions, ushort[] indices, float facing)
    {
        var normals = new Vector3[positions.Length];

        for (int i = 0; i + 2 < indices.Length; i += 3)
        {
            int a = indices[i];
            int b = indices[i + 1];
            int c = indices[i + 2];

            // Not normalised on purpose: the un-normalised cross product is twice the
            // triangle's area, so summing them weights each face by how much of the surface
            // it actually is. A fan of slivers should not outvote the one large triangle
            // beside it.
            Vector3 face = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]) * facing;

            normals[a] += face;
            normals[b] += face;
            normals[c] += face;
        }

        for (int i = 0; i < normals.Length; i++)
        {
            normals[i] = normals[i].LengthSquared() > 0f
                ? Vector3.Normalize(normals[i])
                : Vector3.UnitY;
        }

        return normals;
    }

    /// <summary>Averages normals where submeshes meet, so a hairline is not a seam.</summary>
    private static void Weld(List<ModSubmesh> submeshes)
    {
        Dictionary<(int, int, int), Vector3> shared = [];

        foreach (ModSubmesh submesh in submeshes)
        {
            for (int i = 0; i < submesh.Positions.Length && i < submesh.Normals.Length; i++)
            {
                (int, int, int) key = Key(submesh.Positions[i]);
                shared[key] = shared.TryGetValue(key, out Vector3 running)
                    ? running + submesh.Normals[i]
                    : submesh.Normals[i];
            }
        }

        foreach (ModSubmesh submesh in submeshes)
        {
            for (int i = 0; i < submesh.Positions.Length && i < submesh.Normals.Length; i++)
            {
                Vector3 total = shared[Key(submesh.Positions[i])];

                if (total.LengthSquared() > 0f)
                {
                    submesh.Normals[i] = Vector3.Normalize(total);
                }
            }
        }
    }

    /// <summary>A position rounded to the grid two submeshes have to agree on.</summary>
    private static (int, int, int) Key(Vector3 position) =>
        ((int)MathF.Round(position.X / Coincident),
         (int)MathF.Round(position.Y / Coincident),
         (int)MathF.Round(position.Z / Coincident));

    /// <summary>Which vertices of each submesh are head rather than bookkeeping.</summary>
    private static int[][] Sampled(Vector3[][] rest) =>
        [.. rest.Select(submesh => Enumerable
            .Range(0, submesh.Length)
            .Where(i => !IsAxisTriad(submesh[i]))
            .ToArray())];

    /// <summary>How wide the head is across its longest axis.</summary>
    private static float Extent(Vector3[][] rest)
    {
        Vector3 low = new(float.MaxValue);
        Vector3 high = new(float.MinValue);
        bool any = false;

        foreach (Vector3[] submesh in rest)
        {
            foreach (Vector3 point in submesh)
            {
                if (IsAxisTriad(point))
                {
                    continue;
                }

                low = Vector3.Min(low, point);
                high = Vector3.Max(high, point);
                any = true;
            }
        }

        return any ? (high - low).Length() : 0f;
    }

    /// <summary>Whether a point is one of the three axis markers every mesh group carries.</summary>
    private static bool IsAxisTriad(Vector3 point)
    {
        const float marker = 60f;
        const float slack = 1e-2f;

        return (Near(point.X, marker) && Near(point.Y, 0f) && Near(point.Z, 0f)) ||
               (Near(point.X, 0f) && Near(point.Y, marker) && Near(point.Z, 0f)) ||
               (Near(point.X, 0f) && Near(point.Y, 0f) && Near(point.Z, marker));

        static bool Near(float value, float to) => MathF.Abs(value - to) < slack;
    }
}
