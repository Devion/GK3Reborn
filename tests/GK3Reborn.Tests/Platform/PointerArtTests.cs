using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Platform;
using Xunit;

namespace GK3Reborn.Tests.Platform;

/// <summary>
/// The pictures the pointer is drawn as: that the assembly carries every one, that they
/// shrink cleanly, and that the click lands on the art.
/// </summary>
public sealed class PointerArtTests
{
    [Fact]
    public void The_assembly_carries_a_picture_for_every_shape()
    {
        foreach (PointerShape shape in Enum.GetValues<PointerShape>())
        {
            PointerImage? picture = PointerArt.Load(shape, 48);

            Assert.NotNull(picture);
            Assert.Equal(48, picture.Width);
            Assert.Equal(48, picture.Height);
            Assert.Equal(48 * 48 * 4, picture.Pixels.Length);

            // A pointer nobody can see is a pointer that has gone missing.
            Assert.Contains(
                Enumerable.Range(0, 48 * 48),
                at => picture.Pixels[(at * 4) + 3] > 200);
        }
    }

    [Fact]
    public void The_arrow_clicks_with_its_corner_and_the_rest_click_on_their_art()
    {
        Assert.Equal((0f, 0f), PointerArt.HotspotOf(PointerShape.Default));

        foreach (PointerShape shape in Enum.GetValues<PointerShape>())
        {
            PointerImage picture = PointerArt.Load(shape, 64)!;

            Assert.InRange(picture.HotspotX, 0, 63);
            Assert.InRange(picture.HotspotY, 0, 63);

            // The pixel under the hotspot is part of the picture rather than the clear
            // margin round it: a click that lands on nothing looks like a click that missed.
            int at = ((picture.HotspotY * 64) + picture.HotspotX) * 4;

            Assert.True(
                picture.Pixels[at + 3] > 64,
                $"{shape}'s hotspot ({picture.HotspotX}, {picture.HotspotY}) is on clear pixels");
        }
    }

    [Fact]
    public void The_pointer_keeps_its_proportion_to_the_framebuffer()
    {
        Assert.Equal(45, PointerArt.SizeFor(1080, 1f));
        Assert.Equal(90, PointerArt.SizeFor(2160, 1f));
        Assert.Equal(90, PointerArt.SizeFor(1080, 2f));
        Assert.Equal(22, PointerArt.SizeFor(1080, 0.5f));

        // Too small to see and too big to be a pointer are both refused, and a scale that
        // is not a number is the usual one.
        Assert.Equal(PointerArt.Smallest, PointerArt.SizeFor(0, 1f));
        Assert.Equal(PointerArt.Largest, PointerArt.SizeFor(100_000, 1f));
        Assert.Equal(45, PointerArt.SizeFor(1080, float.NaN));
    }

    [Fact]
    public void Shrinking_averages_the_source_without_bleeding_the_colour_of_clear_pixels()
    {
        // Two by two: one opaque red pixel and three clear ones that happen to hold green.
        // Averaged naively the result is a muddy yellow at quarter strength; averaged with
        // alpha applied it is red at quarter strength, which is what a red edge over
        // nothing looks like.
        byte[] pixels =
        [
            255, 0, 0, 255, /**/ 0, 255, 0, 0,
            0, 255, 0, 0, /*  */ 0, 255, 0, 0,
        ];

        PointerImage shrunk = PointerArt.Resample(
            new DecodedImage(2, 2, pixels, true, "test"), 1, 0f, 0f);

        Assert.Equal(1, shrunk.Width);
        Assert.Equal(255, shrunk.Pixels[0]);
        Assert.Equal(0, shrunk.Pixels[1]);
        Assert.Equal(64, shrunk.Pixels[3]);
    }

    [Fact]
    public void A_source_that_does_not_divide_evenly_still_covers_every_pixel()
    {
        // Three into two: the middle source column is shared between the two results.
        byte[] pixels = new byte[3 * 3 * 4];

        for (int i = 0; i < 9; i++)
        {
            pixels[(i * 4) + 3] = 255;
            pixels[i * 4] = (byte)(i % 3 == 2 ? 255 : 0);
        }

        PointerImage shrunk = PointerArt.Resample(
            new DecodedImage(3, 3, pixels, true, "test"), 2, 1f, 1f);

        Assert.Equal(255, shrunk.Pixels[3]);
        Assert.Equal(255, shrunk.Pixels[7]);

        // The left result sees no red; the right one is two thirds red.
        Assert.Equal(0, shrunk.Pixels[0]);
        Assert.Equal(170, shrunk.Pixels[4]);

        Assert.Equal(1, shrunk.HotspotX);
        Assert.Equal(1, shrunk.HotspotY);
    }
}
