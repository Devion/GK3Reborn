using System.Numerics;
using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Formats.Models;
using GK3Reborn.Formats.Scenes;
using GK3Reborn.Rendering;
using Xunit;

namespace GK3Reborn.Tests.Rendering;

/// <summary>
/// Tests for loading a scene where there is nothing to draw it with.
/// </summary>
/// <remarks>
/// The sink is what makes "every scene loads headlessly" answerable on a build agent, so
/// what matters is that it takes everything the loader hands it and measures it honestly.
/// A sink that quietly dropped models would turn a sweep of the whole corpus into a sweep
/// of the parts that happen to be geometry.
/// </remarks>
public sealed class HeadlessSceneSinkTests
{
    private static ModFile Triangle(Vector3 offset)
    {
        var submesh = new ModSubmesh
        {
            TextureName = "skin",
            Color = (255, 255, 255),
            Positions = [offset, offset + Vector3.UnitX, offset + Vector3.UnitY],
            Normals = [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ],
            TexCoords = new Vector2[3],
            Indices = [0, 1, 2],
        };

        return ModFile.FromMeshes(
            "test",
            [
                new ModMesh
                {
                    MeshToLocal = Matrix4x4.Identity,
                    BoundsMin = offset,
                    BoundsMax = offset + Vector3.One,
                    Submeshes = [submesh],
                },
            ]);
    }

    [Fact]
    public void An_empty_sink_measures_nothing()
    {
        var sink = new HeadlessSceneSink();

        Assert.Equal(0, sink.TriangleCount);
        Assert.Equal(0, sink.TextureCount);
        Assert.Equal(0, sink.ModelCount);
        Assert.Equal(Vector3.Zero, sink.Minimum);
        Assert.Equal(Vector3.Zero, sink.Maximum);
    }

    [Fact]
    public void A_model_counts_its_triangles_and_grows_the_bounds_where_it_stands()
    {
        var sink = new HeadlessSceneSink();

        sink.Add(Triangle(Vector3.Zero));
        sink.Add(Triangle(Vector3.Zero), Matrix4x4.CreateTranslation(10, 0, 0));

        Assert.Equal(2, sink.TriangleCount);
        Assert.Equal(2, sink.ModelCount);

        // The second stands ten units along X, so the bounds have to have followed it.
        Assert.Equal(new Vector3(0, 0, 0), sink.Minimum);
        Assert.Equal(new Vector3(11, 1, 0), sink.Maximum);
    }

    [Fact]
    public void A_texture_is_counted_once_however_often_it_is_given()
    {
        var sink = new HeadlessSceneSink();
        var image = new DecodedImage(4, 4, new byte[4 * 4 * 4], false, "test");

        sink.AddTexture("wallpaper", image);
        sink.AddTexture("WALLPAPER", image);
        sink.AddTexture("carpet", image);

        Assert.Equal(2, sink.TextureCount);
        Assert.Equal(32, sink.TextureTexels);
    }

    [Fact]
    public void A_room_counts_its_triangles_after_the_polygons_are_fanned()
    {
        var sink = new HeadlessSceneSink();

        // One four-sided polygon, which fans into two triangles.
        BspFile room = BspFile.FromParts(
            "room",
            ["wall"],
            [
                new BspSurface
                {
                    ObjectIndex = 0,
                    TextureName = "wallpaper",
                    LightmapUvOffset = Vector2.Zero,
                    LightmapUvScale = Vector2.One,
                    Flags = 0,
                },
            ],
            [new BspPolygon { VertexIndexOffset = 0, VertexIndexCount = 4, SurfaceIndex = 0 }],
            [
                new Vector3(-5, -5, 20),
                new Vector3(-5, 5, 20),
                new Vector3(5, 5, 20),
                new Vector3(5, -5, 20),
            ],
            new Vector2[4],
            [0, 1, 2, 3]);

        sink.AddScene(room);

        Assert.Equal(2, sink.TriangleCount);
        Assert.Equal(new Vector3(-5, -5, 20), sink.Minimum);
        Assert.Equal(new Vector3(5, 5, 20), sink.Maximum);
    }
}
