using System.Numerics;
using GK3Reborn.Content.Authoring;
using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Formats.Scenes;
using GK3Reborn.Rendering;
using GK3Reborn.Rendering.Geometry;
using GK3Reborn.Rendering.Materials;
using GK3Reborn.Rendering.Vulkan;
using Xunit;

namespace GK3Reborn.Tests.Rendering;

/// <summary>
/// Tests for drawing a computer screen as a raster rather than as five bitmaps.
/// </summary>
public sealed class ScreenRasterTests
{
    /// <summary>The glass of Larry's monitor, measured off the 1999 bitmap.</summary>
    private static readonly Vector4 Glass = new(0.109375f, 0.140625f, 0.890625f, 0.84375f);

    /// <summary>
    /// A deliberately different rectangle for the second frame of the animation.
    /// </summary>
    private static readonly Vector4 Other = new(0.2f, 0.2f, 0.8f, 0.8f);

    private static bool HasDevice()
    {
        try
        {
            DeviceReport report = VulkanDeviceSelector.Survey();
            return report.Available && report.Adapters.Count > 0;
        }
        catch (VulkanException)
        {
            return false;
        }
    }

    private static DecodedImage Solid(byte r, byte g, byte b)
    {
        const int Size = 4;
        byte[] pixels = new byte[Size * Size * 4];

        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = r;
            pixels[i + 1] = g;
            pixels[i + 2] = b;
            pixels[i + 3] = 255;
        }

        return new DecodedImage(Size, Size, pixels, false, "test");
    }

    /// <summary>One quad, one surface, one object called <c>computerscreen</c>.</summary>
    private static BspFile Monitor(string texture)
    {
        Vector3[] points =
        [
            new(-1, -1, 4), new(-1, 1, 4), new(1, 1, 4), new(1, -1, 4),
        ];

        return BspFile.FromParts(
            "test",
            ["computerscreen"],
            [
                new BspSurface
                {
                    ObjectIndex = 0,
                    TextureName = texture,
                    LightmapUvOffset = Vector2.Zero,
                    LightmapUvScale = Vector2.One,
                    Flags = 0,
                },
            ],
            [new BspPolygon { VertexIndexOffset = 0, VertexIndexCount = 4, SurfaceIndex = 0 }],
            points,
            [.. points.Select(_ => Vector2.Zero)],
            [0, 1, 2, 3]);
    }

    private static MaterialDefinition Definition(string id, Vector4 glass) =>
        new()
        {
            Id = id,
            BaseColorTexture = id,
            Roughness = 0.18f,
            Metallic = 0f,
            Provenance = AuthoringProvenance.Edited,
            Confidence = 1f,
            Screen = glass != Vector4.Zero,
            ScreenGlass = glass,
        };

    /// <summary>The dark monitor, the running one, and a later frame of the same animation.</summary>
    private static SurfaceFinishes Library() => SurfaceFinishes.From(new MaterialLibrary
    {
        SchemaVersion = 1,
        LibraryId = "test",
        Materials =
        [
            Definition("LHICOMPSCR", Vector4.Zero),
            Definition("LHICOMPANIM1", Glass),
            Definition("LHICOMPANIM2", Other),
        ],
    });

    [Fact]
    public void A_screen_lights_when_the_room_paints_one_onto_it_and_goes_out_again()
    {
        Assert.SkipUnless(HasDevice(), "no Vulkan device");

        using VulkanContext context = VulkanContext.CreateHeadless();
        using SceneRenderer renderer = SceneRenderer.Create(context);
        using SceneGeometry geometry = renderer.CreateGeometry();

        geometry.Materials = Library();
        geometry.AddTexture("LHICOMPSCR", Solid(10, 12, 10));
        geometry.AddTexture("LHICOMPANIM1", Solid(20, 180, 60));
        geometry.AddScene(Monitor("LHICOMPSCR"));
        geometry.Finish();

        // At rest the monitor carries a picture of a dark screen, and nothing about it is
        // a screen as far as the shader is concerned: the rectangle has no area, which is
        // what Raster tests on its first line.
        Assert.Equal(Vector4.Zero, geometry.Draws().Single().Constants.Screen);

        // The animation switches the machine on.
        Assert.True(geometry.PaintSceneObject("computerscreen", "LHICOMPANIM1"));
        Assert.Equal(Glass, geometry.Draws().Single().Constants.Screen);

        // And painting the dark screen back switches it off, which is the whole reason
        // LHICOMPSCR is deliberately not marked as one.
        Assert.True(geometry.PaintSceneObject("computerscreen", "LHICOMPSCR"));
        Assert.Equal(Vector4.Zero, geometry.Draws().Single().Constants.Screen);
    }

    [Fact]
    public void A_screen_is_not_repainted_by_another_frame_of_its_own_animation()
    {
        Assert.SkipUnless(HasDevice(), "no Vulkan device");

        using VulkanContext context = VulkanContext.CreateHeadless();
        using SceneRenderer renderer = SceneRenderer.Create(context);
        using SceneGeometry geometry = renderer.CreateGeometry();

        geometry.Materials = Library();
        geometry.AddTexture("LHICOMPSCR", Solid(10, 12, 10));
        geometry.AddTexture("LHICOMPANIM1", Solid(20, 180, 60));
        geometry.AddTexture("LHICOMPANIM2", Solid(20, 190, 60));
        geometry.AddScene(Monitor("LHICOMPSCR"));
        geometry.Finish();

        geometry.PaintSceneObject("computerscreen", "LHICOMPANIM1");

        // The 1999 animation lands four more frames after the first, at three a second.
        // They are pictures of a thing the shader is already doing, and only the first of
        // them has an enhanced version — so they are ignored rather than drawn.
        geometry.PaintSceneObject("computerscreen", "LHICOMPANIM2");

        Assert.Equal(Glass, geometry.Draws().Single().Constants.Screen);
    }
}
