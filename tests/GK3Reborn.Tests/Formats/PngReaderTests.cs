using GK3Reborn.Formats;
using GK3Reborn.Formats.Bitmaps;
using Xunit;

namespace GK3Reborn.Tests.Formats;

/// <summary>
/// Tests for reading PNG.
/// </summary>
/// <remarks>
/// The reader is deliberately narrow — eight bits a channel, RGB or RGBA, not interlaced,
/// which is every PNG in the corpus and every enhanced candidate — so half of what matters
/// is that it refuses everything else by name instead of half-decoding it.
/// </remarks>
public sealed class PngReaderTests
{
    private static DecodedImage Image(int width, int height, bool alpha)
    {
        byte[] pixels = new byte[width * height * 4];

        for (int i = 0; i < width * height; i++)
        {
            // A gradient rather than a flat fill: every row filter predicts from its
            // neighbours, and a flat image decodes correctly even when they are wrong.
            pixels[(i * 4) + 0] = (byte)(i * 7);
            pixels[(i * 4) + 1] = (byte)(i * 13);
            pixels[(i * 4) + 2] = (byte)(255 - (i * 3));
            pixels[(i * 4) + 3] = alpha ? (byte)(i * 5) : (byte)255;
        }

        return new DecodedImage(width, height, pixels, alpha, "test");
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(7, 3)]
    [InlineData(64, 16)]
    [InlineData(129, 65)]
    public void What_the_writer_produces_the_reader_reads_back(int width, int height)
    {
        DecodedImage original = Image(width, height, alpha: false);

        DecodedImage read = PngReader.Decode(PngWriter.Encode(original), "test.png");

        Assert.Equal(original.Width, read.Width);
        Assert.Equal(original.Height, read.Height);
        Assert.Equal(original.Pixels, read.Pixels);
    }

    [Fact]
    public void Alpha_survives_the_round_trip()
    {
        DecodedImage original = Image(16, 16, alpha: true);

        DecodedImage read = PngReader.Decode(PngWriter.Encode(original), "test.png");

        Assert.True(read.HasAlpha);
        Assert.Equal(original.Pixels, read.Pixels);
    }

    [Fact]
    public void An_opaque_image_reads_back_opaque()
    {
        DecodedImage read = PngReader.Decode(PngWriter.Encode(Image(8, 8, alpha: false)));

        Assert.False(read.HasAlpha);
        Assert.All(
            Enumerable.Range(0, 64).Select(i => read.Pixels[(i * 4) + 3]),
            a => Assert.Equal(255, a));
    }

    [Fact]
    public void Something_that_is_not_a_PNG_is_refused_by_name()
    {
        FormatParseException error = Assert.Throws<FormatParseException>(
            () => PngReader.Decode("BM not a png at all"u8, "wrong.png"));

        Assert.Equal("GK3R1092", error.Diagnostic.Code);
        Assert.Contains("wrong.png", error.Diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_truncated_file_says_so_rather_than_decoding_half_an_image()
    {
        byte[] whole = PngWriter.Encode(Image(32, 32, alpha: false));

        Assert.Throws<FormatParseException>(() => PngReader.Decode(whole.AsSpan(0, whole.Length / 2)));
    }

    [Fact]
    public void A_colour_type_the_reader_does_not_handle_is_named()
    {
        // Grayscale, which nothing in the corpus uses and which this does not decode.
        byte[] png = PngWriter.Encode(Image(4, 4, alpha: false));
        int colourType = FindHeaderField(png) + 9;
        png[colourType] = 0;

        FormatParseException error = Assert.Throws<FormatParseException>(() => PngReader.Decode(png));

        Assert.Contains("colour type 0", error.Diagnostic.Actual, StringComparison.Ordinal);
    }

    [Fact]
    public void Sixteen_bit_output_is_refused_rather_than_misread()
    {
        byte[] png = PngWriter.Encode(Image(4, 4, alpha: false));
        int depth = FindHeaderField(png) + 8;
        png[depth] = 16;

        FormatParseException error = Assert.Throws<FormatParseException>(() => PngReader.Decode(png));

        Assert.Contains("16-bit", error.Diagnostic.Actual, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_a_PNG_says_it_can_be_decoded()
    {
        Assert.True(PngReader.CanDecode(PngWriter.Encode(Image(2, 2, alpha: false))));
        Assert.False(PngReader.CanDecode("BM"u8));
        Assert.False(PngReader.CanDecode([]));
    }

    /// <summary>Offset of the IHDR body, which follows the signature and the chunk header.</summary>
    private static int FindHeaderField(byte[] png) => 8 + 8;
}
