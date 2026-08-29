using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Rendering;
using GK3Reborn.Rendering.Direct3D12;
using Xunit;

namespace GK3Reborn.Tests.Rendering;

/// <summary>
/// Direct3D render tests that need a working GPU.
/// </summary>
/// <remarks>
/// The twin of <see cref="OffscreenRenderTests"/>, and skipped rather than failed where
/// there is no Direct3D device — which is everywhere but Windows, and on Windows a machine
/// with no adapter that reaches the compatibility tier. A build agent without a GPU still
/// reports a green run; a machine that has one gets the real check.
/// </remarks>
[Collection(GpuTests.Name)]
public sealed class D3D12RenderTests
{
    private static bool HasDevice()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            DeviceReport report = D3D12DeviceSelector.Survey();
            return report.Available && report.Selected is not null;
        }
        catch (D3D12Exception)
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
        Assert.SkipUnless(HasDevice(), "no Direct3D device");

        using D3D12OffscreenRenderer renderer = D3D12OffscreenRenderer.Create();
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

        // The triangle covers 0.72 of the four square units of clip space, which is
        // eighteen per cent. Asserting the area rather than merely "something drew" is what
        // catches a viewport that is half the target or a depth test nobody asked for.
        double covered = (double)lit / (image.Width * image.Height);
        Assert.InRange(covered, 0.16, 0.20);
    }

    [Fact]
    public void The_picture_is_the_way_up_that_vulkan_draws_it()
    {
        Assert.SkipUnless(HasDevice(), "no Direct3D device");

        using D3D12OffscreenRenderer renderer = D3D12OffscreenRenderer.Create();
        DecodedImage image = renderer.RenderTriangle(200, 120, (0f, 0f, 0f));

        // The apex is at clip y = -0.6, which is the top of the picture in Vulkan's clip
        // space and the bottom in Direct3D's. The translation undoes that — see
        // HlslTranspiler and Camera.ProjectionWithoutJitter — so the apex must be at the
        // top here as well: few lit pixels in the top quarter, many in the bottom.
        //
        // This is the test that matters most in this file. An upside-down world compiles,
        // draws, and looks almost right, and the project has already been bitten once by
        // its mirror image; see docs/known-issues.md.
        int top = LitRows(image, 0, image.Height / 4);
        int bottom = LitRows(image, image.Height * 3 / 4, image.Height);

        Assert.True(
            bottom > top * 2,
            $"the triangle is upside down: {top} lit in the top quarter, {bottom} in the bottom");
    }

    [Fact]
    public void The_picture_is_not_mirrored_left_to_right()
    {
        Assert.SkipUnless(HasDevice(), "no Direct3D device");

        using D3D12OffscreenRenderer renderer = D3D12OffscreenRenderer.Create();
        DecodedImage image = renderer.RenderTriangle(200, 120, (0f, 0f, 0f));

        // Blue is the vertex at clip x = -0.6 and green the one at +0.6, so blue belongs on
        // the left. Nothing in the translation touches X, which is the point: if this ever
        // fails, something has started flipping a handedness that was already correct.
        //
        // Sampled three quarters of the way down. The base sits at clip y = -0.6, which is
        // exactly four fifths of the way down, so a row chosen there lands on the edge
        // itself and reads whichever way the rasteriser rounded it; the rows below are
        // background. Three quarters is comfortably inside the triangle and still wide
        // enough that the two samples are near its corners.
        int row = image.Height * 3 / 4;
        (byte _, byte leftGreen, byte leftBlue) = Pixel(image, image.Width / 4, row);
        (byte _, byte rightGreen, byte rightBlue) = Pixel(image, image.Width * 3 / 4, row);

        Assert.True(leftBlue > leftGreen, $"the left of the base is not blue: {leftGreen} green, {leftBlue} blue");
        Assert.True(rightGreen > rightBlue, $"the right of the base is not green: {rightGreen} green, {rightBlue} blue");
    }

    [Fact]
    public void Nothing_the_debug_layer_says_goes_unheard()
    {
        Assert.SkipUnless(HasDevice(), "no Direct3D device");

        using D3D12OffscreenRenderer renderer = D3D12OffscreenRenderer.Create();
        renderer.RenderTriangle(64, 64, (0f, 0f, 0f));

        // A clean frame says nothing. Anything here is a resource in the wrong state, a
        // root signature that disagrees with its shader, or a barrier that was not needed —
        // none of which shows up in the picture until it shows up as corruption on somebody
        // else's driver.
        Assert.DoesNotContain(
            renderer.Messages,
            m => !m.Contains("MessageSeverityInfo", StringComparison.Ordinal));
    }

    private static int LitRows(DecodedImage image, int from, int to)
    {
        int lit = 0;

        for (int y = from; y < to; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                (byte r, byte g, byte b) = Pixel(image, x, y);
                if (r + g + b > 30)
                {
                    lit++;
                }
            }
        }

        return lit;
    }
}
