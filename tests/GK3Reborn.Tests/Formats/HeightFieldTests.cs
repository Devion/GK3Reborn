using GK3Reborn.Formats.Bitmaps;
using Xunit;

namespace GK3Reborn.Tests.Formats;

/// <summary>
/// Tests for reading a height map as numbers rather than as a picture.
/// </summary>
/// <remarks>
/// Two things here are silent when wrong. A field that clamps instead of wrapping flattens
/// the relief along the far edge of every tile, which on a street the game tiles thirty
/// times is thirty smears nobody can trace back to a sampler. And BC4's second endpoint
/// order reserves two of its eight codes for the ends of the range rather than
/// interpolating them, which is the part of the format that gets written wrong and which
/// shows up as speckle rather than as a failure.
/// </remarks>
public sealed class HeightFieldTests
{
    /// <summary>A grey image whose red channel is what the callback says.</summary>
    private static DecodedImage Grey(int width, int height, Func<int, int, byte> value)
    {
        var pixels = new byte[width * height * 4];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                byte level = value(x, y);
                int at = ((y * width) + x) * 4;

                pixels[at] = level;
                pixels[at + 1] = level;
                pixels[at + 2] = level;
                pixels[at + 3] = 255;
            }
        }

        return new DecodedImage(width, height, pixels, HasAlpha: false, "test");
    }

    [Fact]
    public void Mid_grey_is_the_modelled_surface()
    {
        HeightField field = HeightField.From(Grey(8, 8, (_, _) => 128));

        // 128 of 255 is a hair over a half, which is as close as eight bits reach. The
        // convention is what matters: zero means the vertex does not move.
        Assert.InRange(field.At(0.5f, 0.5f), -0.01f, 0.01f);
    }

    [Fact]
    public void The_field_runs_either_side_of_the_surface()
    {
        HeightField field = HeightField.From(Grey(4, 4, (x, _) => x < 2 ? (byte)0 : (byte)255));

        Assert.InRange(field.At(0.125f, 0.5f), -0.5f, -0.45f);
        Assert.InRange(field.At(0.875f, 0.5f), 0.45f, 0.5f);
    }

    [Fact]
    public void A_coordinate_outside_the_unit_square_wraps()
    {
        // A floor tiles its texture dozens of times across a street, and the sampler that
        // draws it repeats. Clamping here would flatten every tile's far edge.
        HeightField field = HeightField.From(Grey(8, 8, (x, y) => (byte)((x * 31) + (y * 3))));

        Assert.Equal(field.At(0.3f, 0.4f), field.At(3.3f, -1.6f), 4);
        Assert.Equal(field.At(0.3f, 0.4f), field.At(-0.7f, 0.4f), 4);
    }

    [Fact]
    public void Averaging_over_a_cell_takes_out_what_a_vertex_cannot_carry()
    {
        // Alternating columns: a field with detail far finer than one cell of geometry. A
        // point sample lands on one or the other and the same street tessellated twice
        // comes out a different shape; the average over a cell is the surface underneath.
        HeightField field = HeightField.From(Grey(16, 16, (x, _) => x % 2 == 0 ? (byte)0 : (byte)255));

        Assert.InRange(field.Over(0.5f, 0.5f, 0.5f), -0.02f, 0.02f);
    }

    [Fact]
    public void A_block_compressed_map_decodes_to_what_it_encodes()
    {
        // One BC4 block, eight bytes: two endpoints and sixteen three-bit indices. The
        // endpoints are ordered high-then-low, which is the form that interpolates all six
        // codes between them rather than reserving two for the ends of the range.
        byte[] block = new byte[8];
        block[0] = 255;
        block[1] = 0;

        // Every texel takes index 0, which is the first endpoint.
        var image = new CompressedImage(4, 4, 1, BlockFormat.Bc4Unorm, block, "test");

        HeightField? field = HeightField.From(image);

        Assert.NotNull(field);
        Assert.InRange(field!.At(0.5f, 0.5f), 0.49f, 0.5f);
    }

    [Fact]
    public void The_second_endpoint_order_reserves_two_codes_for_the_range()
    {
        // Endpoints low-then-high: codes six and seven are zero and one exactly, not
        // interpolations. Index six here, so every texel is the bottom of the range.
        byte[] block = new byte[8];
        block[0] = 100;
        block[1] = 200;

        // Sixteen indices of six: 110 repeated, packed low bit first.
        ulong indices = 0;

        for (int i = 0; i < 16; i++)
        {
            indices |= 6UL << (i * 3);
        }

        for (int i = 0; i < 6; i++)
        {
            block[2 + i] = (byte)(indices >> (i * 8));
        }

        HeightField? field = HeightField.From(
            new CompressedImage(4, 4, 1, BlockFormat.Bc4Unorm, block, "test"));

        Assert.NotNull(field);
        Assert.InRange(field!.At(0.5f, 0.5f), -0.5f, -0.49f);
    }

    [Fact]
    public void A_format_that_is_not_one_channel_is_refused()
    {
        // Normals are BC5 and colour is BC7, and neither is a height field. Refusing by
        // name is what stops a pipeline that starts packing height differently from
        // silently producing a floor shaped like a normal map.
        HeightField? field = HeightField.From(
            new CompressedImage(4, 4, 1, BlockFormat.Bc5Unorm, new byte[16], "test"));

        Assert.Null(field);
    }

    [Fact]
    public void A_large_map_is_kept_small()
    {
        // The workspace's maps are 2,048 pixels and displacement samples them a few units
        // apart. Sixteen megabytes of floats per texture would answer the same question.
        HeightField field = HeightField.From(Grey(1024, 1024, (_, _) => 128), wanted: 128);

        Assert.Equal(128, field.Width);
        Assert.Equal(128, field.Height);
    }
}
