using System.Numerics;
using GK3Reborn.Formats.Models;
using Xunit;

namespace GK3Reborn.Tests.Formats;

/// <summary>
/// Which space a model's normals are in, and what happens to a character when it is read
/// wrongly.
/// </summary>
public sealed class ModNormalsTests
{
    /// <summary>The turn a character mesh group carries: 3ds Max's Z-up into GK3's Y-up.</summary>
    private static Matrix4x4 ZUpToYUp()
    {
        Matrix4x4 turn = Matrix4x4.CreateRotationX(-MathF.PI / 2f);

        // The third axis reversed, exactly as ActFile.Mirror does it.
        turn.M31 = -turn.M31;
        turn.M32 = -turn.M32;
        turn.M33 = -turn.M33;

        return turn;
    }

    /// <summary>A box, whose normals are unambiguously the way its faces point.</summary>
    /// <param name="meshToLocal">The group's transform.</param>
    /// <param name="local">
    /// True to write the normals already placed by that transform, as a character does;
    /// false to write them in the mesh's own space, as a prop does.
    /// </param>
    private static ModFile Box(Matrix4x4 meshToLocal, bool local)
    {
        Vector3[] corners =
        [
            new(-10, -10, -10), new(10, -10, -10), new(10, 10, -10), new(-10, 10, -10),
            new(-10, -10, 10), new(10, -10, 10), new(10, 10, 10), new(-10, 10, 10),
        ];

        ushort[] indices =
        [
            0, 2, 1, 0, 3, 2,
            4, 5, 6, 4, 6, 7,
            0, 1, 5, 0, 5, 4,
            2, 3, 7, 2, 7, 6,
            1, 2, 6, 1, 6, 5,
            0, 4, 7, 0, 7, 3,
        ];

        // A box centred on the origin, so "out of the surface" is "away from the centre"
        // and the normals need no smoothing to be unambiguous.
        Vector3[] normals = [.. corners.Select(c =>
        {
            Vector3 outward = Vector3.Normalize(c);
            return local ? Vector3.Normalize(Vector3.TransformNormal(outward, meshToLocal)) : outward;
        })];

        return ModFile.FromMeshes(
            "box",
            [
                new ModMesh
                {
                    MeshToLocal = meshToLocal,
                    BoundsMin = new Vector3(-10, -10, -10),
                    BoundsMax = new Vector3(10, 10, 10),
                    Submeshes =
                    [
                        new ModSubmesh
                        {
                            TextureName = "white",
                            Color = (255, 255, 255),
                            Positions = corners,
                            Normals = normals,
                            TexCoords = [.. corners.Select(_ => Vector2.Zero)],
                            Indices = indices,
                        },
                    ],
                },
            ]);
    }

    [Fact]
    public void Normals_written_in_the_mesh_s_own_space_are_left_alone()
    {
        ModFile prop = Box(ZUpToYUp(), local: false);

        Assert.False(ModNormals.AreLocal(prop));
        Assert.Equal(Matrix4x4.Identity, ModNormals.CorrectionFor(prop.Meshes[0], model: false));
    }

    [Fact]
    public void Normals_written_already_placed_are_corrected()
    {
        ModFile character = Box(ZUpToYUp(), local: true);

        Assert.True(ModNormals.AreLocal(character));
        Assert.NotEqual(Matrix4x4.Identity, ModNormals.CorrectionFor(character.Meshes[0], model: true));
    }

    /// <summary>
    /// The correction undoes exactly the transform the renderer will apply, and no more.
    /// </summary>
    [Fact]
    public void The_correction_cancels_the_transform_the_renderer_applies()
    {
        Matrix4x4 meshToLocal = ZUpToYUp();
        ModFile character = Box(meshToLocal, local: true);
        ModSubmesh submesh = character.Meshes[0].Submeshes[0];

        Matrix4x4 correction = ModNormals.CorrectionFor(character.Meshes[0], model: true);

        for (int i = 0; i < submesh.Normals.Length; i++)
        {
            // What the renderer ends up shading with: the stored normal, then the mesh
            // transform the shader applies.
            Vector3 shaded = Vector3.Normalize(Vector3.TransformNormal(
                Vector3.TransformNormal(submesh.Normals[i], correction), meshToLocal));

            Assert.True(
                Vector3.Distance(shaded, submesh.Normals[i]) < 1e-4f,
                $"normal {i} came out as {shaded} rather than {submesh.Normals[i]}");
        }
    }

    /// <summary>
    /// A group that knows its own answer keeps it, whatever the rest of the model said.
    /// </summary>
    [Fact]
    public void A_group_that_disagrees_with_its_model_is_read_its_own_way()
    {
        Matrix4x4 meshToLocal = ZUpToYUp();

        Assert.False(ModNormals.AreLocal(Box(meshToLocal, local: false).Meshes[0], model: true));
        Assert.True(ModNormals.AreLocal(Box(meshToLocal, local: true).Meshes[0], model: false));
    }

    /// <summary>
    /// A mesh with nothing to say about its normals is left exactly as it was drawn before.
    /// </summary>
    [Fact]
    public void A_mesh_with_no_usable_normals_is_left_alone()
    {
        ModFile empty = ModFile.FromMeshes(
            "nothing",
            [
                new ModMesh
                {
                    MeshToLocal = ZUpToYUp(),
                    BoundsMin = Vector3.Zero,
                    BoundsMax = Vector3.Zero,
                    Submeshes =
                    [
                        new ModSubmesh
                        {
                            TextureName = "white",
                            Color = (255, 255, 255),
                            Positions = [Vector3.Zero, Vector3.Zero, Vector3.Zero],
                            Normals = [Vector3.Zero, Vector3.Zero, Vector3.Zero],
                            TexCoords = [Vector2.Zero, Vector2.Zero, Vector2.Zero],
                            Indices = [0, 1, 2],
                        },
                    ],
                },
            ]);

        Assert.False(ModNormals.AreLocal(empty));
        Assert.Equal(Matrix4x4.Identity, ModNormals.CorrectionFor(empty.Meshes[0], model: false));
    }
}
