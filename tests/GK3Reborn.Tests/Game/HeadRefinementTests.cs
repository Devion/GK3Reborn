using System.Numerics;
using GK3Reborn.Formats.Models;
using GK3Reborn.Game.Actors;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Tests for giving a character a denser head without invalidating their animation.
/// </summary>
/// <remarks>
/// The refinement is only safe because of one thing: the rig keeps the authored vertex
/// positions, so a clip that addresses 307 vertices still has 307 vertices to address even
/// though the head being drawn now has thousands. Most of what is worth checking here is
/// that this separation holds — that the rig describes the model that went in and the mesh
/// describes the model that comes out, and that nothing outside the head is touched.
/// </remarks>
public sealed class HeadRefinementTests
{
    /// <summary>A head-shaped thing: an octahedron split into a face and a hairline.</summary>
    /// <remarks>
    /// Split across the equator, so the two submeshes share four vertices. That shared ring
    /// is where a hairline seam would show if the normals were not welded across it, which
    /// is the one thing about this that is not simply subdivision.
    /// </remarks>
    private static ModFile Character()
    {
        Vector3[] corners =
        [
            new(10f, 0f, 0f), new(-10f, 0f, 0f),
            new(0f, 10f, 0f), new(0f, -10f, 0f),
            new(0f, 0f, 10f), new(0f, 0f, -10f),
        ];

        ModSubmesh Cap(string texture, int pole, ushort[] triangles)
        {
            int[] used = [0, 1, 2, 3, pole];
            var positions = new Vector3[used.Length];

            for (int i = 0; i < used.Length; i++)
            {
                positions[i] = corners[used[i]];
            }

            return new ModSubmesh
            {
                TextureName = texture,
                Color = (255, 255, 255),
                Positions = positions,
                Normals = [.. positions.Select(Vector3.Normalize)],
                TexCoords = [.. positions.Select(p => new Vector2(p.X, p.Y))],
                Indices = triangles,
            };
        }

        // Index 4 is the pole in each cap's own vertex array.
        ModSubmesh face = Cap("GRA_FACE", 4, [0, 2, 4, 2, 1, 4, 1, 3, 4, 3, 0, 4]);
        ModSubmesh hair = Cap("GRA_HAIR", 5, [2, 0, 4, 1, 2, 4, 3, 1, 4, 0, 3, 4]);

        var body = new ModSubmesh
        {
            TextureName = "GRA_SHIRTB",
            Color = (255, 255, 255),
            Positions = [new(0f, 0f, 0f), new(1f, 0f, 0f), new(0f, 1f, 0f)],
            Normals = [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ],
            TexCoords = [Vector2.Zero, Vector2.UnitX, Vector2.UnitY],
            Indices = [0, 1, 2],
        };

        ModMesh Mesh(params ModSubmesh[] submeshes) => new()
        {
            MeshToLocal = Matrix4x4.Identity,
            BoundsMin = new Vector3(-10f),
            BoundsMax = new Vector3(10f),
            Submeshes = submeshes,
        };

        return ModFile.FromMeshes("GRA", [Mesh(body), Mesh(face, hair)]);
    }

    [Fact]
    public void FindsTheHeadAndLeavesEverythingElseAlone()
    {
        ModFile original = Character();

        (ModFile refined, HeadRig? rig) = HeadRefinement.Apply(original, 2);

        Assert.NotNull(rig);
        Assert.Equal(1, rig!.Mesh);

        // The body is the same object, not merely an equal one.
        Assert.Same(original.Meshes[0].Submeshes[0], refined.Meshes[0].Submeshes[0]);
    }

    /// <summary>
    /// The rig holds what the clips address, which is the model before refinement. If this
    /// ever came back holding refined positions, every fit would compare 307 vertices
    /// against thousands and fail, and every head in the game would stop moving.
    /// </summary>
    [Fact]
    public void KeepsTheAuthoredVerticesForTheClipsToAddress()
    {
        ModFile original = Character();
        IReadOnlyList<ModSubmesh> head = original.Meshes[1].Submeshes;

        (ModFile refined, HeadRig? rig) = HeadRefinement.Apply(original, 2);

        Assert.Equal(head.Count, rig!.Rest.Length);

        for (int i = 0; i < head.Count; i++)
        {
            Assert.Equal(head[i].Positions.Length, rig.Rest[i].Length);
            Assert.Equal(head[i].Positions, rig.Rest[i]);

            // And the head that is drawn is denser than the head that is addressed.
            Assert.True(
                refined.Meshes[1].Submeshes[i].Positions.Length > rig.Rest[i].Length);
        }
    }

    [Fact]
    public void RefinesFurtherWithMoreLevels()
    {
        int At(int levels) =>
            HeadRefinement.Apply(Character(), levels).Model.Meshes[1].Submeshes
                .Sum(s => s.Indices.Length / 3);

        int plain = Character().Meshes[1].Submeshes.Sum(s => s.Indices.Length / 3);

        Assert.Equal(plain * 4, At(1));
        Assert.Equal(plain * 16, At(2));
    }

    [Fact]
    public void DoesNothingWithoutLevels()
    {
        ModFile original = Character();

        (ModFile refined, HeadRig? rig) = HeadRefinement.Apply(original, 0);

        Assert.Same(original, refined);
        Assert.Null(rig);
    }

    [Fact]
    public void LeavesSomethingWithNoHeadAlone()
    {
        var prop = ModFile.FromMeshes("ALARMCLOCK",
        [
            new ModMesh
            {
                MeshToLocal = Matrix4x4.Identity,
                BoundsMin = Vector3.Zero,
                BoundsMax = Vector3.One,
                Submeshes =
                [
                    new ModSubmesh
                    {
                        TextureName = "LHICLK0",
                        Color = (255, 255, 255),
                        Positions = [Vector3.Zero, Vector3.UnitX, Vector3.UnitY],
                        Normals = [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ],
                        TexCoords = [Vector2.Zero, Vector2.UnitX, Vector2.UnitY],
                        Indices = [0, 1, 2],
                    },
                ],
            },
        ]);

        (ModFile refined, HeadRig? rig) = HeadRefinement.Apply(prop, 2);

        Assert.Same(prop, refined);
        Assert.Null(rig);
    }

    /// <summary>
    /// The submeshes are refined one at a time and never see each other, so the normals
    /// where they meet have to be reconciled afterwards or the hairline becomes a shading
    /// seam the original data does not have. The authored meshes agree to 0.0° across those
    /// seams; the refined ones have to as well.
    /// </summary>
    [Fact]
    public void WeldsNormalsWhereSubmeshesMeet()
    {
        (ModFile refined, HeadRig? _) = HeadRefinement.Apply(Character(), 2);

        IReadOnlyList<ModSubmesh> head = refined.Meshes[1].Submeshes;
        Dictionary<string, Vector3> seen = [];
        int shared = 0;

        foreach (ModSubmesh submesh in head)
        {
            for (int i = 0; i < submesh.Positions.Length; i++)
            {
                string key = $"{submesh.Positions[i].X:F3},{submesh.Positions[i].Y:F3}," +
                             $"{submesh.Positions[i].Z:F3}";

                if (seen.TryGetValue(key, out Vector3 other))
                {
                    shared++;
                    Assert.True(
                        Vector3.Distance(other, submesh.Normals[i]) < 1e-4f,
                        $"normals disagree at {key}: {other} against {submesh.Normals[i]}");
                }

                seen[key] = submesh.Normals[i];
            }
        }

        // The equator, subdivided twice: the test is worthless if nothing is actually shared.
        Assert.True(shared >= 4, $"only {shared} positions were shared");
    }

    /// <summary>
    /// A refined normal has to point out of the surface, not into it. The winding is read
    /// off the model's own normals rather than assumed, because GK3's world is left-handed
    /// and getting this backwards shades a face from the inside.
    /// </summary>
    [Fact]
    public void KeepsNormalsPointingOutwards()
    {
        (ModFile refined, HeadRig? _) = HeadRefinement.Apply(Character(), 2);

        foreach (ModSubmesh submesh in refined.Meshes[1].Submeshes)
        {
            for (int i = 0; i < submesh.Positions.Length; i++)
            {
                if (submesh.Positions[i].LengthSquared() < 1e-6f)
                {
                    continue;
                }

                Assert.True(
                    Vector3.Dot(
                        Vector3.Normalize(submesh.Positions[i]), submesh.Normals[i]) > 0f,
                    $"normal at {submesh.Positions[i]} points inwards");
            }
        }
    }

    /// <summary>The head's width, used to report a fit's error, ignores the axis triad.</summary>
    [Fact]
    public void MeasuresTheHeadWithoutTheAxisTriad()
    {
        ModFile original = Character();

        // The three markers every mesh group in the game carries, four times the size of
        // the head they are attached to.
        ModSubmesh face = original.Meshes[1].Submeshes[0];
        ModSubmesh withTriad = face with
        {
            Positions = [.. face.Positions, new(60f, 0f, 0f), new(0f, 60f, 0f), new(0f, 0f, 60f)],
            Normals = [.. face.Normals, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ],
            TexCoords = [.. face.TexCoords, Vector2.Zero, Vector2.Zero, Vector2.Zero],
        };

        ModFile marked = ModFile.FromMeshes("GRA",
        [
            original.Meshes[0],
            original.Meshes[1] with
            {
                Submeshes = [withTriad, original.Meshes[1].Submeshes[1]],
            },
        ]);

        HeadRig plain = HeadRefinement.Apply(original, 1).Rig!;
        HeadRig marker = HeadRefinement.Apply(marked, 1).Rig!;

        Assert.Equal(plain.Span, marker.Span, 3f);

        // And out of what the fit looks at, which is the more important of the two: the
        // width only scales a reported error, while three stationary points sixty units out
        // decide the rotation itself.
        Assert.Equal(face.Positions.Length + 3, marker.Rest[0].Length);
        Assert.Equal(face.Positions.Length, marker.Sample[0].Length);
        Assert.All(marker.Sample[0], i => Assert.True(i < face.Positions.Length));
    }
}
