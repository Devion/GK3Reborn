using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Rendering;
using GK3Reborn.Rendering.Direct3D12;
using Xunit;

namespace GK3Reborn.Tests.Rendering;

/// <summary>
/// Getting a picture onto a Direct3D device, and getting the same picture back.
/// </summary>
/// <remarks>
/// The texture path has one mistake that hides and one that creeps. A texture is copied
/// through a buffer whose rows are padded to two hundred and fifty-six bytes, and a copy
/// that treats them as packed shears the picture a little further with every row — which is
/// invisible at any width that is a multiple of sixty-four, so these use a hundred. And a
/// mip level of odd width has an edge column with nothing to average against, which if
/// reached past rather than clamped makes a texture drift sideways as it coarsens.
/// </remarks>
[Collection(GpuTests.Name)]
public sealed class D3D12TextureTests
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

    /// <summary>A picture whose every pixel says where it is.</summary>
    /// <remarks>
    /// Red counts along the row and green down the column, so a picture that sheared, or
    /// that was read with the wrong pitch, is wrong in a way that names the axis.
    /// </remarks>
    private static DecodedImage Gradient(int width, int height)
    {
        byte[] pixels = new byte[width * height * 4];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int at = ((y * width) + x) * 4;
                pixels[at] = (byte)(x * 255 / Math.Max(1, width - 1));
                pixels[at + 1] = (byte)(y * 255 / Math.Max(1, height - 1));
                pixels[at + 2] = 128;
                pixels[at + 3] = 255;
            }
        }

        return new DecodedImage(width, height, pixels, HasAlpha: false, SourceFormat: "test");
    }

    [Theory]
    [InlineData(64, 64)]
    [InlineData(100, 60)]
    [InlineData(37, 91)]
    [InlineData(1, 1)]
    public void A_picture_survives_the_round_trip_at_any_width(int width, int height)
    {
        Assert.SkipUnless(HasDevice(), "no Direct3D device");

        using D3D12TextureProbe probe = D3D12TextureProbe.Create();

        DecodedImage source = Gradient(width, height);
        DecodedImage back = probe.RoundTrip(source);

        Assert.Equal(width, back.Width);
        Assert.Equal(height, back.Height);

        // Byte for byte. There is no filtering, no format conversion and no encode on this
        // path, so anything but an exact match is a pitch that was got wrong somewhere.
        Assert.Equal(source.Pixels, back.Pixels);
    }

    [Fact]
    public void A_mip_chain_halves_the_picture_without_moving_it()
    {
        Assert.SkipUnless(HasDevice(), "no Direct3D device");

        using D3D12TextureProbe probe = D3D12TextureProbe.Create();

        // A gradient's average is the middle of it, and stays the middle however many times
        // it is halved. A chain that reached past an edge instead of clamping would pull
        // the average towards whichever end it read twice.
        DecodedImage source = Gradient(64, 64);

        foreach (uint level in (uint[])[0, 1, 2, 3, 4])
        {
            (float r, float g, float b) = probe.AverageOfLevel(source, level);

            Assert.InRange(r, 0.47f, 0.53f);
            Assert.InRange(g, 0.47f, 0.53f);

            // Flat everywhere, so it must stay flat: a blue that drifted would mean the
            // filter was reading something other than the four texels it meant to.
            Assert.InRange(b, 0.49f, 0.52f);
        }
    }

    [Fact]
    public void A_mip_chain_of_an_odd_size_still_reaches_one_by_one()
    {
        Assert.SkipUnless(HasDevice(), "no Direct3D device");

        using D3D12TextureProbe probe = D3D12TextureProbe.Create();

        // 100 halves to 50, 25, 12, 6, 3, 1 — the odd steps are where an unclamped filter
        // reads past the end. The last level is one texel and must be the average of the
        // whole picture, which for a gradient is its middle.
        DecodedImage source = Gradient(100, 100);
        (float r, float g, float _) = probe.AverageOfLevel(source, 6);

        Assert.InRange(r, 0.44f, 0.56f);
        Assert.InRange(g, 0.44f, 0.56f);
    }

    /// <summary>A picture of alternating black and white pixels.</summary>
    /// <remarks>
    /// The one picture that tells the two filters apart. Every level below the top is half
    /// black and half white, and the answer is a different number depending on the space the
    /// halves are averaged in — 128 in the encoding, 188 in light.
    /// </remarks>
    private static DecodedImage Checkerboard(int size)
    {
        byte[] pixels = new byte[size * size * 4];

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int at = ((y * size) + x) * 4;
                byte value = (byte)(((x + y) % 2) == 0 ? 0 : 255);

                pixels[at] = value;
                pixels[at + 1] = value;
                pixels[at + 2] = value;
                pixels[at + 3] = 255;
            }
        }

        return new DecodedImage(size, size, pixels, HasAlpha: false, SourceFormat: "test");
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(2u)]
    [InlineData(3u)]
    public void A_colour_mip_chain_averages_in_light_rather_than_in_the_encoding(uint level)
    {
        Assert.SkipUnless(HasDevice(), "no Direct3D device");

        using D3D12TextureProbe probe = D3D12TextureProbe.Create();

        // Half black, half white. Averaged as light that is 0.5, which written back out
        // sRGB-encoded is 188. Averaged as the stored bytes it is 128, which is the sRGB
        // encoding of 0.216 — a level three quarters of a stop dark, and darker again at
        // every level after it, because each one averages the last one's answer.
        //
        // Vulkan builds this chain with vkCmdBlitImage, which decodes an sRGB source before
        // it filters and encodes the result after. This is the number that has to match, and
        // it is the whole of the difference between the two backends' pictures.
        DecodedImage level0 = Checkerboard(64);
        DecodedImage read = probe.LevelOf(level0, level, colour: true);

        foreach (int at in (int[])[0, read.Pixels.Length / 2, read.Pixels.Length - 4])
        {
            Assert.InRange(read.Pixels[at], 186, 190);
        }
    }

    [Fact]
    public void Uploading_says_nothing_to_the_debug_layer()
    {
        Assert.SkipUnless(HasDevice(), "no Direct3D device");

        using D3D12TextureProbe probe = D3D12TextureProbe.Create();
        probe.RoundTrip(Gradient(100, 60), mipmaps: true);

        Assert.DoesNotContain(
            probe.Messages,
            m => !m.Contains("MessageSeverityInfo", StringComparison.Ordinal));
    }

    [Fact]
    public void Building_a_colour_mip_chain_says_nothing_to_the_debug_layer()
    {
        Assert.SkipUnless(HasDevice(), "no Direct3D device");

        // The one path that reads through an sRGB view and writes through a plain one, which
        // is a cast between two fully typed formats and so is the thing a device might
        // refuse. Every check above this uploads as data and never asks.
        using D3D12TextureProbe probe = D3D12TextureProbe.Create();
        probe.LevelOf(Gradient(100, 60), level: 2, colour: true);

        Assert.DoesNotContain(
            probe.Messages,
            m => !m.Contains("MessageSeverityInfo", StringComparison.Ordinal));
    }
}
