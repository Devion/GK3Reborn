using System.Numerics;
using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Formats.Scenes;
using GK3Reborn.Rendering;
using GK3Reborn.Rendering.Geometry;
using GK3Reborn.Rendering.Vulkan;
using Xunit;

namespace GK3Reborn.Tests.Rendering;

/// <summary>
/// Tests for the room's stains: the shadow under a parked moped, the blood on ARM's floor,
/// the gate's shadow on the gravel at Serres. Ninety surfaces across twenty-six rooms carry
/// the flag, each a white picture with the mark painted dark on it, and each meant to be
/// multiplied into what it lies over rather than drawn as a slab in front of it.
/// </summary>
public sealed class ShadowDecalTests
{
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

        return new DecodedImage(Size, Size, pixels, HasAlpha: false, "test");
    }

    private static (byte R, byte G, byte B) Pixel(DecodedImage image, int x, int y)
    {
        int at = ((y * image.Width) + x) * 4;
        return (image.Pixels[at], image.Pixels[at + 1], image.Pixels[at + 2]);
    }

    private static Camera Facing() => new()
    {
        Position = new Vector3(0, 0, -6),
        Target = Vector3.Zero,
        Up = Vector3.UnitY,
        Background = Vector3.Zero,
        LightDirection = new Vector3(0, 0, 1),
    };

    /// <summary>A quad facing the camera, across part of the view.</summary>
    private static Vector3[] Quad(float left, float right, float depth) =>
    [
        new(left, -2f, depth), new(left, 2f, depth),
        new(right, 2f, depth), new(right, -2f, depth),
    ];

    /// <summary>A room of quads, one surface and one object each, with its own flags.</summary>
    private static BspFile Room(params (Vector3[] Points, string Texture, uint Flags)[] quads)
    {
        List<Vector3> vertices = [];
        List<ushort> indices = [];
        List<BspSurface> surfaces = [];
        List<BspPolygon> polygons = [];
        List<string> names = [];

        foreach ((Vector3[] points, string texture, uint flags) in quads)
        {
            int at = vertices.Count;
            int offset = indices.Count;

            vertices.AddRange(points);
            indices.AddRange(Enumerable.Range(at, points.Length).Select(i => (ushort)i));
            names.Add($"object{names.Count}");

            surfaces.Add(new BspSurface
            {
                ObjectIndex = names.Count - 1,
                TextureName = texture,
                LightmapUvOffset = Vector2.Zero,
                LightmapUvScale = Vector2.One,
                Flags = flags,
            });

            polygons.Add(new BspPolygon
            {
                VertexIndexOffset = offset,
                VertexIndexCount = points.Length,
                SurfaceIndex = surfaces.Count - 1,
            });
        }

        return BspFile.FromParts(
            "test",
            names,
            surfaces,
            polygons,
            [.. vertices],
            [.. vertices.Select(_ => Vector2.Zero)],
            [.. indices]);
    }

    [Fact]
    public void Only_bit_sixty_four_makes_a_surface_a_stain()
    {
        BspSurface Surface(uint flags) => new()
        {
            ObjectIndex = 0,
            TextureName = "x",
            LightmapUvOffset = Vector2.Zero,
            LightmapUvScale = Vector2.One,
            Flags = flags,
        };

        // The three spellings the corpus actually uses, and the ones next door to them.
        Assert.True(Surface(64).IsDecal);
        Assert.True(Surface(68).IsDecal);
        Assert.True(Surface(80).IsDecal);
        Assert.False(Surface(0).IsDecal);
        Assert.False(Surface(8).IsDecal);
        Assert.False(Surface(16).IsDecal);
    }

    [Fact]
    public void A_stain_is_drawn_after_the_room_and_says_so()
    {
        Assert.SkipUnless(HasDevice(), "no Vulkan device");

        using VulkanContext context = VulkanContext.CreateHeadless();
        using SceneRenderer renderer = SceneRenderer.Create(context);
        using SceneGeometry geometry = renderer.CreateGeometry();

        geometry.AddTexture("gravel", Solid(220, 200, 160));
        geometry.AddTexture("shade", Solid(60, 60, 60));

        // The stain first in the file, to prove the order comes from the flag rather than
        // from the order the surfaces happen to be written in.
        geometry.AddScene(Room(
            (Quad(-1f, 1f, 3.9f), "shade", 68u),
            (Quad(-2f, 2f, 4f), "gravel", 0u)));

        geometry.Finish();

        SceneDraw[] draws = [.. geometry.Draws()];

        Assert.Equal(2, draws.Length);
        Assert.False(draws[0].Decal, "the room's own surface should come first");
        Assert.True(draws[1].Decal, "the stain should come last");

        // Sixteen in the flag word is what the fragment shader reads to write white into
        // every target but the picture — read as a bit, because a stain is also self-lit
        // and so carries the one beside it. See MeshShaders.
        Assert.Equal(16, (int)draws[1].Constants.Shading.Z & 16);
        Assert.Equal(0, (int)draws[0].Constants.Shading.Z & 16);
    }

    [Fact]
    public void A_stain_darkens_what_it_lies_over_instead_of_covering_it()
    {
        // The whole of the defect the château's gate reported: drawn as ordinary geometry
        // the shadow is an opaque slab of its own picture — a white rectangle with a navy
        // shape on it — and drawn as the original draws it the gravel shows through,
        // darkened.
        Assert.SkipUnless(HasDevice(), "no Vulkan device");

        using VulkanContext context = VulkanContext.CreateHeadless();
        using SceneRenderer renderer = SceneRenderer.Create(context);
        using SceneGeometry geometry = renderer.CreateGeometry();

        geometry.AddTexture("gravel", Solid(240, 120, 40));
        geometry.AddTexture("shade", Solid(128, 128, 128));

        geometry.AddScene(Room(
            (Quad(-3f, 3f, 4f), "gravel", 0u),
            (Quad(-3f, 0f, 3.9f), "shade", 68u)));

        geometry.Finish();

        DecodedImage image = renderer.Render(geometry, 128, 128, Facing());

        (byte lr, byte lg, byte lb) = Pixel(image, 32, 64);
        (byte rr, byte rg, byte rb) = Pixel(image, 96, 64);

        // The half with no stain on it is the gravel, and the stained half is the same
        // colour and darker — not grey, which is what painting the shade over it would give.
        Assert.True(rr > rg && rg > rb, $"the bare half is not the gravel: {rr},{rg},{rb}");
        Assert.True(lr < rr, $"the stain did not darken the gravel: {lr},{lg},{lb}");
        Assert.True(lr > lg && lg > lb, $"the stain covered the gravel: {lr},{lg},{lb}");
    }
}
