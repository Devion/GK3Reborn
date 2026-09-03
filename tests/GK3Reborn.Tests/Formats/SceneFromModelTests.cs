using System.Numerics;
using GK3Reborn.Formats.Models;
using GK3Reborn.Formats.Scenes;
using GK3Reborn.Foundation.Diagnostics;
using Xunit;

namespace GK3Reborn.Tests.Formats;

/// <summary>
/// Tests for building a room out of a model.
/// </summary>
/// <remarks>
/// This is how a room the game never had can exist at all: there is no writer for the 1999
/// <c>.BSP</c>, and there does not need to be, because what the rest of the engine asks a
/// room for is what <c>BspFile.FromParts</c> takes. What has to hold is that the result is
/// a room in every sense the rest of the engine means — its objects are named, its surfaces
/// belong to them, and it does not expect a bake that does not exist.
/// </remarks>
public sealed class SceneFromModelTests
{
    [Fact]
    public void One_node_becomes_one_object_under_its_own_name()
    {
        // The node names are what a scene file binds nouns to, so this is what decides
        // whether the player can click on anything in such a room.
        ModFile model = Model(("te2_floor", 1), ("te2_firebasin", 2));

        BspFile room = Assert.IsType<BspFile>(SceneFromModel.Build(model, "Te2"));

        Assert.Equal(["te2_floor", "te2_firebasin"], room.ObjectNames);
        Assert.Equal(3, room.Surfaces.Count);
        Assert.Equal(0, room.Surfaces[0].ObjectIndex);
        Assert.Equal(1, room.Surfaces[1].ObjectIndex);
        Assert.Equal(1, room.Surfaces[2].ObjectIndex);
    }

    [Fact]
    public void A_surface_carries_the_material_name_as_its_texture()
    {
        ModFile model = Model(("te2_walls", 1));

        BspFile room = Assert.IsType<BspFile>(SceneFromModel.Build(model, "Te2"));

        Assert.Equal("TE3WALL0", room.Surfaces[0].TextureName);
    }

    [Fact]
    public void Every_surface_ignores_the_lightmap_it_has_not_got()
    {
        // There are no lightmaps for a room that never shipped, and a surface that expects
        // one and has none is drawn black. This is the difference between a room lit by its
        // rig and a room that is a silhouette.
        ModFile model = Model(("te2_walls", 2));

        BspFile room = Assert.IsType<BspFile>(SceneFromModel.Build(model, "Te2"));

        Assert.All(room.Surfaces, s =>
            Assert.Equal(BspSurface.IgnoreLightmapFlag, s.Flags & BspSurface.IgnoreLightmapFlag));
    }

    [Fact]
    public void The_nodes_transform_is_baked_into_the_vertices()
    {
        // A .MOD keeps a node's transform separate so a scene can pose the parts of a
        // model. A room is not posed, and every consumer of a BSP expects its vertices to
        // be where they are.
        var submesh = new ModSubmesh
        {
            TextureName = "T",
            Color = (255, 255, 255),
            Positions = [Vector3.Zero, Vector3.UnitX, Vector3.UnitZ],
            Normals = [Vector3.UnitY, Vector3.UnitY, Vector3.UnitY],
            TexCoords = [Vector2.Zero, Vector2.Zero, Vector2.Zero],
            Indices = [0, 1, 2],
        };

        ModFile model = ModFile.FromMeshes("Te2",
        [
            new ModMesh
            {
                Name = "te2_floor",
                MeshToLocal = Matrix4x4.CreateTranslation(new Vector3(10f, 20f, 30f)),
                BoundsMin = Vector3.Zero,
                BoundsMax = Vector3.One,
                Submeshes = [submesh],
            },
        ]);

        BspFile room = Assert.IsType<BspFile>(SceneFromModel.Build(model, "Te2"));

        Assert.Equal(new Vector3(10f, 20f, 30f), room.Vertices[0]);
        Assert.Equal(new Vector3(11f, 20f, 30f), room.Vertices[1]);
    }

    [Fact]
    public void A_model_with_no_triangles_is_not_a_room()
    {
        ModFile model = ModFile.FromMeshes("Te2", []);
        var diagnostics = new DiagnosticBag();

        Assert.Null(SceneFromModel.Build(model, "Te2", diagnostics));
        Assert.Contains(diagnostics.Items, d => d.Code == "GK3R1197");
    }

    [Fact]
    public void A_model_too_big_to_index_is_refused_whole()
    {
        // A BSP indexes its vertices with 16-bit indices. Half a room drawn and the other
        // half missing is the sort of failure nobody reports as a format problem, so it is
        // refused at the door instead.
        var positions = new Vector3[SceneFromModel.MostVertices + 3];
        var texCoords = new Vector2[positions.Length];
        var normals = new Vector3[positions.Length];

        ModFile model = ModFile.FromMeshes("Te2",
            [
                new ModMesh
                {
                    Name = "huge",
                    MeshToLocal = Matrix4x4.Identity,
                    BoundsMin = Vector3.Zero,
                    BoundsMax = Vector3.One,
                    Submeshes =
                    [
                        new ModSubmesh
                        {
                            TextureName = "T",
                            Color = (255, 255, 255),
                            Positions = positions,
                            Normals = normals,
                            TexCoords = texCoords,
                            Indices = [0, 1, 2],
                        },
                    ],
                },
            ]);

        var diagnostics = new DiagnosticBag();

        Assert.Null(SceneFromModel.Build(model, "Te2", diagnostics));
        Assert.Contains(diagnostics.Items, d => d.Code == "GK3R1196");
    }

    private static ModFile Model(params (string Name, int Submeshes)[] meshes)
    {
        List<ModMesh> built = [];

        foreach ((string name, int count) in meshes)
        {
            List<ModSubmesh> submeshes = [];

            for (int i = 0; i < count; i++)
            {
                submeshes.Add(new ModSubmesh
                {
                    TextureName = "TE3WALL" + i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Color = (255, 255, 255),
                    Positions = [Vector3.Zero, Vector3.UnitX, Vector3.UnitZ],
                    Normals = [Vector3.UnitY, Vector3.UnitY, Vector3.UnitY],
                    TexCoords = [Vector2.Zero, Vector2.Zero, Vector2.Zero],
                    Indices = [0, 1, 2],
                });
            }

            built.Add(new ModMesh
            {
                Name = name,
                MeshToLocal = Matrix4x4.Identity,
                BoundsMin = Vector3.Zero,
                BoundsMax = Vector3.One,
                Submeshes = submeshes,
            });
        }

        return ModFile.FromMeshes("Te2", built);
    }
}
