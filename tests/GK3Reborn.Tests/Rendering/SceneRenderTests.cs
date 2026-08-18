using System.Numerics;
using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Formats.Models;
using GK3Reborn.Rendering;
using GK3Reborn.Rendering.Vulkan;
using Xunit;

namespace GK3Reborn.Tests.Rendering;

/// <summary>
/// End-to-end render tests for the mesh pipeline.
/// </summary>
/// <remarks>
/// <para>
/// The geometry is synthetic rather than taken from the game, so the suite still needs no
/// copyrighted data, but it goes through exactly the path a real model does: a parsed
/// <see cref="ModFile"/>, a decoded texture, vertex and index buffers, descriptor sets and
/// a draw.
/// </para>
/// <para>
/// They skip where no Vulkan device is available so a build agent without a GPU still
/// reports a green run, and do the real check on a machine that has one — which is the
/// only way to tell "drew nothing" apart from "did not crash".
/// </para>
/// </remarks>
public sealed class SceneRenderTests
{
    private static bool HasDevice()
    {
        try
        {
            VulkanDeviceReport report = VulkanDeviceSelector.Survey();
            return report.VulkanAvailable && report.Devices.Count > 0;
        }
        catch (VulkanException)
        {
            return false;
        }
    }

    private static (byte R, byte G, byte B) Pixel(DecodedImage image, int x, int y)
    {
        int at = ((y * image.Width) + x) * 4;
        return (image.Pixels[at], image.Pixels[at + 1], image.Pixels[at + 2]);
    }

    /// <summary>A unit quad facing the camera, spanning the whole texture.</summary>
    private static ModFile Quad(string texture)
    {
        Vector3[] positions =
        [
            new(-1, -1, 0), new(1, -1, 0), new(1, 1, 0), new(-1, 1, 0),
        ];

        Vector3[] normals = [-Vector3.UnitZ, -Vector3.UnitZ, -Vector3.UnitZ, -Vector3.UnitZ];

        Vector2[] texCoords =
        [
            new(0, 1), new(1, 1), new(1, 0), new(0, 0),
        ];

        var submesh = new ModSubmesh
        {
            TextureName = texture,
            Color = (255, 255, 255),
            Positions = positions,
            Normals = normals,
            TexCoords = texCoords,
            Indices = [0, 1, 2, 0, 2, 3],
        };

        return ModFile.FromMeshes(
            "quad",
            [
                new ModMesh
                {
                    MeshToLocal = Matrix4x4.Identity,
                    BoundsMin = new Vector3(-1, -1, 0),
                    BoundsMax = new Vector3(1, 1, 0),
                    Submeshes = [submesh],
                },
            ]);
    }

    private static DecodedImage Solid(byte r, byte g, byte b)
    {
        const int Size = 8;
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

    /// <summary>A camera looking at the origin from straight in front of the quad.</summary>
    private static Camera FacingQuad() => new()
    {
        Position = new Vector3(0, 0, -3),
        Target = Vector3.Zero,
        Up = Vector3.UnitY,
        Background = new Vector3(0, 0, 0),

        // Straight on, so the quad's own normal faces the key light and shading is
        // predictable rather than depending on the default light's angle.
        LightDirection = new Vector3(0, 0, 1),
    };

    [Fact]
    public void A_textured_quad_draws_its_texture()
    {
        Assert.SkipUnless(HasDevice(), "no Vulkan device");

        using VulkanContext context = VulkanContext.CreateHeadless();
        using SceneRenderer renderer = SceneRenderer.Create(context);
        using SceneGeometry geometry = renderer.CreateGeometry();

        geometry.AddTexture("wall", Solid(200, 40, 40));
        geometry.Add(Quad("wall"));

        DecodedImage image = renderer.Render(geometry, 128, 128, FacingQuad());

        (byte r, byte g, byte b) = Pixel(image, 64, 64);

        Assert.True(r > 150, $"centre red was {r}");
        Assert.True(g < 90, $"centre green was {g}");
        Assert.True(b < 90, $"centre blue was {b}");
    }

    [Fact]
    public void The_background_shows_where_nothing_is_drawn()
    {
        Assert.SkipUnless(HasDevice(), "no Vulkan device");

        using VulkanContext context = VulkanContext.CreateHeadless();
        using SceneRenderer renderer = SceneRenderer.Create(context);
        using SceneGeometry geometry = renderer.CreateGeometry();

        geometry.AddTexture("wall", Solid(200, 40, 40));
        geometry.Add(Quad("wall"));

        DecodedImage image = renderer.Render(geometry, 128, 128, FacingQuad());

        // The quad covers the middle of the frame but not its corners.
        Assert.Equal((0, 0, 0), Pixel(image, 1, 1));
    }

    [Fact]
    public void A_magenta_texture_is_discarded_rather_than_drawn()
    {
        Assert.SkipUnless(HasDevice(), "no Vulkan device");

        using VulkanContext context = VulkanContext.CreateHeadless();
        using SceneRenderer renderer = SceneRenderer.Create(context);
        using SceneGeometry geometry = renderer.CreateGeometry();

        geometry.AddTexture("keyed", Solid(255, 0, 255));
        geometry.Add(Quad("keyed"));

        DecodedImage image = renderer.Render(geometry, 128, 128, FacingQuad());

        Assert.Equal((0, 0, 0), Pixel(image, 64, 64));
    }

    [Fact]
    public void A_missing_texture_draws_the_fallback_rather_than_nothing()
    {
        Assert.SkipUnless(HasDevice(), "no Vulkan device");

        using VulkanContext context = VulkanContext.CreateHeadless();
        using SceneRenderer renderer = SceneRenderer.Create(context);
        using SceneGeometry geometry = renderer.CreateGeometry();

        geometry.Add(Quad("nothing-has-this-name"));

        DecodedImage image = renderer.Render(geometry, 128, 128, FacingQuad());

        (byte r, byte g, byte b) = Pixel(image, 64, 64);

        // The fallback is a magenta-and-dark checker, so something is visible and
        // obviously wrong rather than silently absent.
        Assert.True(r > 20 || b > 20, "the fallback texture drew nothing");
    }

    [Fact]
    public void Nearer_geometry_hides_what_is_behind_it()
    {
        Assert.SkipUnless(HasDevice(), "no Vulkan device");

        using VulkanContext context = VulkanContext.CreateHeadless();
        using SceneRenderer renderer = SceneRenderer.Create(context);
        using SceneGeometry geometry = renderer.CreateGeometry();

        geometry.AddTexture("far", Solid(40, 200, 40));
        geometry.AddTexture("near", Solid(200, 40, 40));

        // The nearer quad is added first, so drawing in submission order alone would leave
        // the farther one on top. Red winning is therefore the depth test, not luck.
        geometry.Add(Quad("near"), Matrix4x4.CreateTranslation(0, 0, -1));
        geometry.Add(Quad("far"), Matrix4x4.CreateTranslation(0, 0, 1));

        DecodedImage image = renderer.Render(geometry, 128, 128, FacingQuad());

        (byte r, byte g, _) = Pixel(image, 64, 64);

        Assert.True(r > g, $"the nearer quad was hidden: red {r}, green {g}");
    }

    [Fact]
    public void Geometry_reports_the_bounds_of_what_it_holds()
    {
        Assert.SkipUnless(HasDevice(), "no Vulkan device");

        using VulkanContext context = VulkanContext.CreateHeadless();
        using SceneRenderer renderer = SceneRenderer.Create(context);
        using SceneGeometry geometry = renderer.CreateGeometry();

        geometry.Add(Quad("wall"), Matrix4x4.CreateTranslation(10, 0, 0));

        Assert.Equal(new Vector3(9, -1, 0), geometry.Minimum);
        Assert.Equal(new Vector3(11, 1, 0), geometry.Maximum);
        Assert.Equal(2, geometry.TriangleCount);
    }
}
