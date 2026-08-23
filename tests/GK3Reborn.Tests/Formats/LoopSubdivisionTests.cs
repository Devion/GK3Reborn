using System.Numerics;
using GK3Reborn.Formats.Models;
using Xunit;

namespace GK3Reborn.Tests.Formats;

/// <summary>
/// Tests for the subdivision that rounds off a character's outline.
/// </summary>
/// <remarks>
/// Two things have to hold. It has to actually round the silhouette, which is the whole
/// point and is checked by watching the angles between neighbouring faces open towards
/// flat. And it must not open a crack where two submeshes meet, because GK3 splits a head
/// into a face, a hairline, an ear and a patch of skin, and a seam that comes apart along
/// the hairline is worse than the hard outline it replaced.
/// </remarks>
public sealed class LoopSubdivisionTests
{
    /// <summary>An octahedron: the crudest closed surface with every vertex a fan.</summary>
    private static (Vector3[] Positions, ushort[] Indices) Octahedron()
    {
        Vector3[] positions =
        [
            new(1f, 0f, 0f), new(-1f, 0f, 0f),
            new(0f, 1f, 0f), new(0f, -1f, 0f),
            new(0f, 0f, 1f), new(0f, 0f, -1f),
        ];

        ushort[] indices =
        [
            0, 2, 4,  2, 1, 4,  1, 3, 4,  3, 0, 4,
            2, 0, 5,  1, 2, 5,  3, 1, 5,  0, 3, 5,
        ];

        return (positions, indices);
    }

    private static Vector2[] Zeroed(int count) => new Vector2[count];

    /// <summary>The angle between the planes of two faces that share an edge.</summary>
    private static List<float> Dihedrals(Vector3[] positions, ushort[] indices)
    {
        Dictionary<(int, int), List<Vector3>> edges = [];

        for (int i = 0; i + 2 < indices.Length; i += 3)
        {
            Vector3 normal = Vector3.Normalize(Vector3.Cross(
                positions[indices[i + 1]] - positions[indices[i]],
                positions[indices[i + 2]] - positions[indices[i]]));

            for (int e = 0; e < 3; e++)
            {
                int a = indices[i + e];
                int b = indices[i + ((e + 1) % 3)];
                (int, int) key = a < b ? (a, b) : (b, a);
                (edges.TryGetValue(key, out List<Vector3>? faces) ? faces : edges[key] = [])
                    .Add(normal);
            }
        }

        return
        [
            .. edges.Values
                .Where(f => f.Count == 2)
                .Select(f => 180f - (MathF.Acos(Math.Clamp(Vector3.Dot(f[0], f[1]), -1f, 1f)) *
                                     180f / MathF.PI)),
        ];
    }

    [Fact]
    public void ProducesFourTrianglesForEveryOne()
    {
        (Vector3[] positions, ushort[] indices) = Octahedron();

        RefinedMesh refined = LoopSubdivision.Refine(
            positions, Zeroed(positions.Length), indices);

        Assert.Equal(indices.Length * 4, refined.Indices.Length);

        // Six corners and twelve edges on an octahedron.
        Assert.Equal(18, refined.Positions.Length);
        Assert.Equal(refined.Positions.Length, refined.TexCoords.Length);
    }

    [Fact]
    public void PredictsTheVertexCountItWillProduce()
    {
        (Vector3[] positions, ushort[] indices) = Octahedron();

        Assert.Equal(
            LoopSubdivision.Refine(positions, Zeroed(positions.Length), indices).Positions.Length,
            LoopSubdivision.Predict(positions.Length, indices));
    }

    /// <summary>
    /// The claim the whole change rests on: this makes an outline rounder. An octahedron's
    /// faces meet at 109°, and a rounder surface is one whose faces meet closer to flat.
    /// </summary>
    [Fact]
    public void RoundsTheSilhouette()
    {
        (Vector3[] positions, ushort[] indices) = Octahedron();

        float before = Dihedrals(positions, indices).Min();

        RefinedMesh once = LoopSubdivision.Refine(
            positions, Zeroed(positions.Length), indices);
        float after = Dihedrals(once.Positions, once.Indices).Min();

        RefinedMesh twice = LoopSubdivision.Refine(
            once.Positions, once.TexCoords, once.Indices);
        float again = Dihedrals(twice.Positions, twice.Indices).Min();

        Assert.True(after > before, $"{before}° to {after}°");
        Assert.True(again > after, $"{after}° to {again}°");

        // The worst corner of an octahedron opens from 109° to about 154° over two levels:
        // the six extraordinary vertices are where a subdivision surface stays creased
        // longest, and an octahedron is nothing but extraordinary vertices. A real head is
        // mostly valence six and rounds off faster than this.
        Assert.True(again > 150f, $"two levels only reached {again}°");
        // Measured: an octahedron's faces all meet at 109.5°, and two levels take the worst
        // corner to 154° and the surface as a whole to 164°.
        float mean = Dihedrals(twice.Positions, twice.Indices).Average();
        Assert.True(mean > 160f, $"mean dihedral was {mean}");
    }

    /// <summary>
    /// Two submeshes that share a boundary have to refine to the same boundary, or the
    /// hairline comes apart. Both sides are refined on their own — they never see each
    /// other — so agreement has to fall out of the rules rather than be arranged.
    /// </summary>
    [Fact]
    public void KeepsASharedSeamShut()
    {
        // A curved seam, so that a rule which moved boundary vertices would move them
        // somewhere and the two sides would have to agree on where by luck.
        Vector3[] seam =
        [
            new(0f, 0f, 0f), new(1f, 0.4f, 0f), new(2f, 0.1f, 0f), new(3f, 0.6f, 0f),
        ];

        (Vector3[] Positions, ushort[] Indices) Side(float away)
        {
            var positions = new List<Vector3>(seam);
            var indices = new List<ushort>();

            for (int i = 0; i < seam.Length; i++)
            {
                positions.Add(seam[i] + new Vector3(0f, 0f, away));
            }

            for (int i = 0; i + 1 < seam.Length; i++)
            {
                int a = i;
                int b = i + 1;
                int c = seam.Length + i;
                int d = seam.Length + i + 1;

                indices.AddRange(away > 0
                    ? [(ushort)a, (ushort)b, (ushort)c, (ushort)b, (ushort)d, (ushort)c]
                    : new ushort[] { (ushort)b, (ushort)a, (ushort)c, (ushort)d, (ushort)b, (ushort)c });
            }

            return ([.. positions], [.. indices]);
        }

        (Vector3[] nearPositions, ushort[] nearIndices) = Side(1f);
        (Vector3[] farPositions, ushort[] farIndices) = Side(-1f);

        RefinedMesh near = LoopSubdivision.Refine(
            nearPositions, Zeroed(nearPositions.Length), nearIndices);
        RefinedMesh far = LoopSubdivision.Refine(
            farPositions, Zeroed(farPositions.Length), farIndices);

        // Every point either side put on the plane of the seam, which is where the two
        // surfaces have to meet.
        static List<Vector3> OnSeam(RefinedMesh mesh) =>
            [.. mesh.Positions.Where(p => MathF.Abs(p.Z) < 1e-4f).OrderBy(p => p.X).ThenBy(p => p.Y)];

        List<Vector3> left = OnSeam(near);
        List<Vector3> right = OnSeam(far);

        Assert.NotEmpty(left);
        Assert.Equal(left.Count, right.Count);

        for (int i = 0; i < left.Count; i++)
        {
            Assert.True(
                Vector3.Distance(left[i], right[i]) < 1e-5f,
                $"seam point {i}: {left[i]} against {right[i]}");
        }
    }

    /// <summary>
    /// Boundary vertices stay exactly put. This is what stops the rim at a character's neck
    /// shrinking away from the collar it is supposed to be hidden inside.
    /// </summary>
    [Fact]
    public void PinsTheEdgeOfAnOpenSurface()
    {
        Vector3[] positions =
        [
            new(0f, 0f, 0f), new(2f, 0f, 0f), new(4f, 0f, 0f),
            new(0f, 2f, 0f), new(2f, 2f, 1f), new(4f, 2f, 0f),
            new(0f, 4f, 0f), new(2f, 4f, 0f), new(4f, 4f, 0f),
        ];

        ushort[] indices =
        [
            0, 1, 3,  1, 4, 3,  1, 2, 4,  2, 5, 4,
            3, 4, 6,  4, 7, 6,  4, 5, 7,  5, 8, 7,
        ];

        RefinedMesh refined = LoopSubdivision.Refine(
            positions, Zeroed(positions.Length), indices);

        // Everything except the middle vertex is on the boundary of this patch.
        for (int i = 0; i < positions.Length; i++)
        {
            if (i == 4)
            {
                continue;
            }

            Assert.Equal(positions[i], refined.Positions[i]);
        }
    }

    [Fact]
    public void PutsANewVertexHalfwayAlongTheTextureCoordinates()
    {
        Vector3[] positions = [new(0f, 0f, 0f), new(1f, 0f, 0f), new(0f, 1f, 0f)];
        Vector2[] texCoords = [new(0f, 0f), new(1f, 0f), new(0f, 1f)];
        ushort[] indices = [0, 1, 2];

        RefinedMesh refined = LoopSubdivision.Refine(positions, texCoords, indices);

        // The originals keep both their index and their mapping: the face texture is
        // composited at fixed pixel coordinates and cannot be allowed to drift.
        for (int i = 0; i < texCoords.Length; i++)
        {
            Assert.Equal(texCoords[i], refined.TexCoords[i]);
        }

        Vector2[] added = [.. refined.TexCoords.Skip(positions.Length)];

        Assert.Equal(3, added.Length);
        Assert.Contains(added, uv => Vector2.Distance(uv, new Vector2(0.5f, 0f)) < 1e-5f);
        Assert.Contains(added, uv => Vector2.Distance(uv, new Vector2(0.5f, 0.5f)) < 1e-5f);
        Assert.Contains(added, uv => Vector2.Distance(uv, new Vector2(0f, 0.5f)) < 1e-5f);
    }
}
