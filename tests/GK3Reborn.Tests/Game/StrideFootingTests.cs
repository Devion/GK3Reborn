using System.Numerics;
using GK3Reborn.Formats.Animation;
using GK3Reborn.Formats.Models;
using GK3Reborn.Foundation.Diagnostics;
using GK3Reborn.Game.Actors;
using GK3Reborn.Tests.Formats;
using Xunit;

namespace GK3Reborn.Tests.Game;

public sealed class StrideFootingTests
{
    private static Vector3[] Points(float sole) =>
        [new(-1, sole, 0), new(1, sole, 0), new(0, sole, 2), new(0, -100, 0)];

    private static ModFile Model() => ModFile.FromMeshes("test", [Shoe(), Shoe()]);

    private static ModMesh Shoe() => new()
    {
        MeshToLocal = Matrix4x4.Identity, BoundsMin = new(-1, -100, 0), BoundsMax = new(1, 0, 2),
        Submeshes = [new ModSubmesh
        {
            TextureName = "shoe", Color = (255, 255, 255), Positions = Points(0),
            Normals = new Vector3[4], TexCoords = new Vector2[4], Indices = [0, 1, 2],
        }],
    };

    private static CharacterConfig Character() => new("TEST", 70, null, null, null,
        LeftShoe: new(0, 0, 3), RightShoe: new(1, 0, 3));

    [Theory]
    [InlineData(false, -6f)]
    [InlineData(true, -4f)]
    public void Only_drawn_vertices_set_one_offset_for_the_entire_cycle(bool deform, float expected)
    {
        var builder = new ClipBuilder(2, "test")
            .Frame((0, ClipBuilder.Transform(Matrix4x4.CreateTranslation(0, 5, 0))),
                   (1, ClipBuilder.Transform(Matrix4x4.CreateTranslation(0, 9, 0))))
            .Frame((0, ClipBuilder.Transform(Matrix4x4.CreateTranslation(0, 7, 0))),
                   (1, ClipBuilder.Transform(Matrix4x4.CreateTranslation(0, 4, 0))),
                   (1, ClipBuilder.Shape(0, Points(deform ? -2 : 0))));
        ActFile clip = ActFile.Read(builder.Build(), "test", new DiagnosticBag(), vertices: true)!;
        Assert.Equal(expected, StrideFooting.Offset(Model(), clip, Character(), Matrix4x4.CreateTranslation(0, 2, 0)));
    }

    [Fact]
    public void Missing_shoe_metadata_keeps_the_existing_alignment()
    {
        ActFile clip = ActFile.Read(new ClipBuilder(2, "test")
            .Frame((0, ClipBuilder.Transform(Matrix4x4.Identity))).Build(), "test", new DiagnosticBag())!;
        Assert.Equal(0f, StrideFooting.Offset(Model(), clip, null, Matrix4x4.Identity));
    }
}
