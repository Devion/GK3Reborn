using System.Buffers.Binary;
using System.IO.Compression;
using GK3Reborn.Formats;
using GK3Reborn.Formats.Bitmaps;
using Xunit;

namespace GK3Reborn.Tests.Formats;

public sealed class BitmapTests
{
    /// <summary>Builds a GK3 bitmap. Note the header stores height before width.</summary>
    private static byte[] Gk3Bitmap(int width, int height, params ushort[] pixels)
    {
        var output = new MemoryStream();
        output.Write([0x36, 0x31, 0x6E, 0x4D]);

        Span<byte> field = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(field, (ushort)height);
        output.Write(field);
        BinaryPrimitives.WriteUInt16LittleEndian(field, (ushort)width);
        output.Write(field);

        int index = 0;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(field, index < pixels.Length ? pixels[index++] : (ushort)0);
                output.Write(field);
            }

            if ((width % 2) != 0)
            {
                output.Write([0xFF, 0xFF]); // row padding, must be skipped
            }
        }

        return output.ToArray();
    }

    private const ushort Red565 = 0xF800;
    private const ushort Green565 = 0x07E0;
    private const ushort Blue565 = 0x001F;
    private const ushort White565 = 0xFFFF;
    private const ushort Black565 = 0x0000;
    private const ushort Magenta565 = 0xF81F;

    private static (byte R, byte G, byte B, byte A) Pixel(DecodedImage image, int x, int y)
    {
        int at = ((y * image.Width) + x) * 4;
        return (image.Pixels[at], image.Pixels[at + 1], image.Pixels[at + 2], image.Pixels[at + 3]);
    }

    [Fact]
    public void Rgb565_expands_so_that_full_channels_reach_255()
    {
        // Naive shifting would give 248 for red and blue, leaving every white pixel
        // slightly grey and every texture subtly wrong.
        DecodedImage image = BitmapDecoder.Decode(Gk3Bitmap(2, 2, Red565, Green565, Blue565, White565));

        Assert.Equal((255, 0, 0, 255), Pixel(image, 0, 0));
        Assert.Equal((0, 255, 0, 255), Pixel(image, 1, 0));
        Assert.Equal((0, 0, 255, 255), Pixel(image, 0, 1));
        Assert.Equal((255, 255, 255, 255), Pixel(image, 1, 1));
    }

    [Fact]
    public void Width_and_height_are_not_transposed()
    {
        // The header stores height first. Getting it backwards still "works" for square
        // textures, which is exactly how the mistake survives to ship.
        DecodedImage image = BitmapDecoder.Decode(Gk3Bitmap(4, 2, Black565));

        Assert.Equal(4, image.Width);
        Assert.Equal(2, image.Height);
    }

    [Fact]
    public void Rows_of_odd_width_are_padded()
    {
        // Each row of an odd-width image carries two trailing bytes. Missing them shears
        // the image progressively, one pixel per row.
        DecodedImage image = BitmapDecoder.Decode(
            Gk3Bitmap(3, 2, Red565, Red565, Red565, Blue565, Blue565, Blue565));

        Assert.Equal((255, 0, 0, 255), Pixel(image, 2, 0));
        Assert.Equal((0, 0, 255, 255), Pixel(image, 0, 1));
        Assert.Equal((0, 0, 255, 255), Pixel(image, 2, 1));
    }

    [Fact]
    public void Magenta_becomes_transparent_when_the_image_is_marked_alpha_tested()
    {
        // The marker is a magenta top-left pixel, which is how G-Engine detects it.
        DecodedImage image = BitmapDecoder.Decode(
            Gk3Bitmap(2, 2, Magenta565, Red565, Magenta565, Blue565));

        Assert.True(image.HasAlpha);
        Assert.Equal(0, Pixel(image, 0, 0).A);
        Assert.Equal(0, Pixel(image, 0, 1).A);
        Assert.Equal(255, Pixel(image, 1, 0).A);
    }

    [Fact]
    public void Magenta_stays_opaque_when_the_image_is_not_marked()
    {
        // Without the marker, magenta is just a colour, and punching it out would put
        // holes in artwork that happens to use it.
        DecodedImage image = BitmapDecoder.Decode(
            Gk3Bitmap(2, 2, Red565, Magenta565, Magenta565, Blue565));

        Assert.False(image.HasAlpha);
        Assert.Equal(255, Pixel(image, 1, 0).A);
    }

    [Fact]
    public void A_truncated_bitmap_is_reported_rather_than_read_past()
    {
        byte[] complete = Gk3Bitmap(8, 8, White565);

        var ex = Assert.Throws<FormatParseException>(
            () => BitmapDecoder.Decode(complete.AsSpan(0, 20), "BIG.BMP"));

        Assert.Equal("GK3R1031", ex.Diagnostic.Code);
        Assert.Equal("BIG.BMP", ex.Diagnostic.File);
    }

    [Fact]
    public void Something_that_is_not_a_bitmap_is_refused()
    {
        Assert.False(BitmapDecoder.CanDecode("RIFF"u8));
        Assert.Throws<FormatParseException>(() => BitmapDecoder.Decode("RIFFxxxx"u8));
    }

    [Fact]
    public void Windows_bitmaps_decode_bottom_up_with_bgr_order()
    {
        // 24bpp, 1x2, stored bottom-up: the first row on disk is the bottom of the image.
        var output = new MemoryStream();
        var writer = new BinaryWriter(output);
        writer.Write("BM"u8);
        writer.Write(0u);            // file size, unused
        writer.Write(0u);            // reserved
        writer.Write(54u);           // pixel data offset
        writer.Write(40u);           // DIB header size
        writer.Write(1);             // width
        writer.Write(2);             // height, positive means bottom-up
        writer.Write((ushort)1);     // planes
        writer.Write((ushort)24);    // bits per pixel
        writer.Write(0u);            // no compression
        writer.Write(new byte[20]);  // remainder of the DIB header
        // Rows are padded to a four-byte stride, so one pixel of 24bpp occupies four bytes.
        writer.Write(new byte[] { 0x00, 0x00, 0xFF, 0 }); // bottom row: BGR red
        writer.Write(new byte[] { 0xFF, 0x00, 0x00, 0 }); // top row: BGR blue
        writer.Flush();

        DecodedImage image = BitmapDecoder.Decode(output.ToArray());

        Assert.Equal(1, image.Width);
        Assert.Equal(2, image.Height);
        Assert.Equal((0, 0, 255, 255), Pixel(image, 0, 0));
        Assert.Equal((255, 0, 0, 255), Pixel(image, 0, 1));
    }

    [Fact]
    public void Encoded_png_is_structurally_valid()
    {
        DecodedImage image = BitmapDecoder.Decode(Gk3Bitmap(2, 2, Red565, Green565, Blue565, White565));
        byte[] png = PngWriter.Encode(image);

        Assert.Equal<byte[]>([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], png.AsSpan(0, 8).ToArray());

        (string Type, byte[] Data)[] chunks = [.. ReadChunks(png)];
        Assert.Equal(["IHDR", "IDAT", "IEND"], chunks.Select(c => c.Type));

        Assert.Equal(2, BinaryPrimitives.ReadInt32BigEndian(chunks[0].Data));
        Assert.Equal(2, BinaryPrimitives.ReadInt32BigEndian(chunks[0].Data.AsSpan(4)));
        Assert.Equal(8, chunks[0].Data[8]);
        Assert.Equal(2, chunks[0].Data[9]); // colour type 2 is RGB
    }

    [Fact]
    public void An_image_with_transparency_is_encoded_as_rgba()
    {
        DecodedImage image = BitmapDecoder.Decode(Gk3Bitmap(2, 1, Magenta565, Red565));
        byte[] png = PngWriter.Encode(image);

        (string Type, byte[] Data) ihdr = ReadChunks(png).First();
        Assert.Equal(6, ihdr.Data[9]); // colour type 6 is RGBA
    }

    [Fact]
    public void Encoded_pixels_survive_a_round_trip()
    {
        DecodedImage image = BitmapDecoder.Decode(Gk3Bitmap(2, 2, Red565, Green565, Blue565, White565));
        byte[] png = PngWriter.Encode(image);

        byte[] idat = ReadChunks(png).Single(c => c.Type == "IDAT").Data;
        using var compressed = new MemoryStream(idat);
        using var inflate = new ZLibStream(compressed, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        inflate.CopyTo(raw);

        // Two rows of: filter byte, then three bytes per pixel.
        byte[] bytes = raw.ToArray();
        Assert.Equal(2 * (1 + (2 * 3)), bytes.Length);
        Assert.Equal(0, bytes[0]);
        Assert.Equal([255, 0, 0, 0, 255, 0], bytes[1..7]);
        Assert.Equal(0, bytes[7]);
        Assert.Equal([0, 0, 255, 255, 255, 255], bytes[8..14]);
    }

    private static IEnumerable<(string Type, byte[] Data)> ReadChunks(byte[] png)
    {
        int offset = 8;
        while (offset < png.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset));
            string type = System.Text.Encoding.ASCII.GetString(png, offset + 4, 4);
            byte[] data = png[(offset + 8)..(offset + 8 + length)];

            uint stored = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset + 8 + length));
            uint actual = System.IO.Hashing.Crc32.HashToUInt32(png.AsSpan(offset + 4, 4 + length));
            Assert.Equal(actual, stored);

            yield return (type, data);
            offset += 12 + length;
        }
    }
}
