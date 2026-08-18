using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Rendering.Vulkan;
using Xunit;

namespace GK3Reborn.Tests.Rendering;

/// <summary>
/// Render tests that need a working GPU.
/// </summary>
/// <remarks>
/// These skip rather than fail where no Vulkan device is available, so a build agent
/// without a GPU still reports a green run. A machine that *does* have one gets the real
/// check, which is the only way to tell "drew nothing" apart from "did not crash".
/// </remarks>
public sealed class OffscreenRenderTests
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

    [Fact]
    public void The_shader_chain_produces_the_triangle_it_describes()
    {
        Assert.SkipUnless(HasDevice(), "no Vulkan device");

        using OffscreenRenderer renderer = OffscreenRenderer.Create();
        DecodedImage image = renderer.RenderTriangle(200, 120, (0f, 0f, 0f));

        Assert.Equal(200, image.Width);
        Assert.Equal(120, image.Height);

        // The clear colour is black, so any lit pixel means geometry reached the target.
        // Without this the test would pass on a renderer that drew nothing at all.
        int lit = 0;
        for (int i = 0; i < image.Pixels.Length; i += 4)
        {
            if (image.Pixels[i] + image.Pixels[i + 1] + image.Pixels[i + 2] > 30)
            {
                lit++;
            }
        }

        Assert.True(lit > 1000, $"only {lit} pixels were drawn; the triangle is missing");
    }

    [Fact]
    public void Vertex_colours_interpolate_across_the_triangle()
    {
        Assert.SkipUnless(HasDevice(), "no Vulkan device");

        using OffscreenRenderer renderer = OffscreenRenderer.Create();
        DecodedImage image = renderer.RenderTriangle(400, 240, (0f, 0f, 0f));

        // The shader puts red at the apex, green at the lower right and blue at the lower
        // left. Sampling inside each corner proves the varying reached the fragment stage
        // rather than the triangle being flat-shaded or the wrong way up.
        //
        // The triangle spans NDC -0.6 to 0.6, so in a 400x240 image its apex is at
        // (200, 48) and its base runs along y = 192. Sample points have to sit inside
        // that, not at the image corners.
        (byte R, byte G, byte B) top = Pixel(image, 200, 60);
        (byte R, byte G, byte B) lowerRight = Pixel(image, 270, 180);
        (byte R, byte G, byte B) lowerLeft = Pixel(image, 130, 180);

        Assert.True(top.R > top.G && top.R > top.B, $"apex should be reddest, was {top}");
        Assert.True(lowerRight.G > lowerRight.R, $"lower right should be greenest, was {lowerRight}");
        Assert.True(lowerLeft.B > lowerLeft.R, $"lower left should be bluest, was {lowerLeft}");
    }

    [Fact]
    public void The_clear_colour_reaches_the_corners()
    {
        Assert.SkipUnless(HasDevice(), "no Vulkan device");

        using OffscreenRenderer renderer = OffscreenRenderer.Create();
        DecodedImage image = renderer.RenderTriangle(200, 120, (1f, 0f, 0f));

        // The triangle does not reach the very corner, so this is background.
        (byte R, byte G, byte B) corner = Pixel(image, 2, 2);

        Assert.True(corner.R > 200, $"corner should carry the clear colour, was {corner}");
        Assert.True(corner.G < 40 && corner.B < 40, $"corner should be pure red, was {corner}");
    }

    [Fact]
    public void Compiling_a_broken_shader_reports_the_compiler_error()
    {
        Assert.SkipUnless(HasDevice(), "no Vulkan device");

        using var compiler = new ShaderCompiler();

        var ex = Assert.Throws<VulkanException>(() =>
            compiler.Compile("this is not HLSL", ShaderStage.Vertex, "broken.vert", "main"));

        Assert.Contains("broken.vert", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Compiled_shaders_are_cached_by_content()
    {
        Assert.SkipUnless(HasDevice(), "no Vulkan device");

        string cache = Path.Combine(Path.GetTempPath(), "gk3reborn-shader-cache", Path.GetRandomFileName());

        try
        {
            const string Source = """
                float4 main(uint id : SV_VertexID) : SV_Position
                {
                    return float4(0.0, 0.0, 0.0, 1.0);
                }
                """;

            using var compiler = new ShaderCompiler(cache);

            byte[] first = compiler.Compile(Source, ShaderStage.Vertex, "cached.vert");
            Assert.Single(Directory.GetFiles(cache, "*.spv"));

            byte[] second = compiler.Compile(Source, ShaderStage.Vertex, "cached.vert");
            Assert.Equal(first, second);
            Assert.Single(Directory.GetFiles(cache, "*.spv"));

            // A different shader is a different entry rather than a cache collision.
            compiler.Compile(Source.Replace("0.0, 0.0", "1.0, 0.0", StringComparison.Ordinal),
                ShaderStage.Vertex, "cached.vert");
            Assert.Equal(2, Directory.GetFiles(cache, "*.spv").Length);
        }
        finally
        {
            if (Directory.Exists(cache))
            {
                Directory.Delete(cache, recursive: true);
            }
        }
    }
}
