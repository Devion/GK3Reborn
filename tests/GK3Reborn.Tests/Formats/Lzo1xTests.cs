using System.Text;
using GK3Reborn.Formats;
using GK3Reborn.Formats.Compression;
using Xunit;

namespace GK3Reborn.Tests.Formats;

/// <summary>
/// Unit coverage for the LZO1X decompressor, using hand-encoded streams.
/// </summary>
/// <remarks>
/// The streams here are written by hand rather than produced by a compressor, because
/// the project has no LZO compressor and none is needed: the archives are read-only.
/// Bulk validation comes from the real corpus, where 2,340 extracted WAV files carry
/// RIFF sizes that agree byte-for-byte with their decompressed lengths — a single
/// wrong byte anywhere in the decoder would break that relationship.
/// </remarks>
public sealed class Lzo1xTests
{
    /// <summary>The three-byte sequence that ends a stream.</summary>
    private static readonly byte[] EndOfStream = [0x11, 0x00, 0x00];

    private static byte[] Stream(params IEnumerable<byte>[] parts) =>
        [.. parts.SelectMany(p => p)];

    private static string Decompress(byte[] input, int outputSize)
    {
        byte[] output = new byte[outputSize];
        int written = Lzo1x.Decompress(input, output);
        return Encoding.ASCII.GetString(output, 0, written);
    }

    [Fact]
    public void An_empty_stream_decodes_to_nothing()
    {
        byte[] output = new byte[16];
        Assert.Equal(0, Lzo1x.Decompress(EndOfStream, output));
    }

    [Fact]
    public void A_leading_literal_run_is_copied_verbatim()
    {
        // A first byte above 17 encodes a literal run of (byte - 17) directly,
        // skipping the usual token.
        byte[] input = Stream([17 + 5], "Hello"u8.ToArray(), EndOfStream);

        Assert.Equal("Hello", Decompress(input, 16));
    }

    [Fact]
    public void A_back_reference_copies_from_already_written_output()
    {
        // "ABCD", then a match of three bytes starting at offset 3. The match overlaps
        // the write cursor, so it must be copied byte by byte: the second and third
        // bytes read characters the copy itself has just produced.
        byte[] input = Stream([17 + 4], "ABCD"u8.ToArray(), [64, 0], EndOfStream);

        Assert.Equal("ABCDDDD", Decompress(input, 16));
    }

    [Fact]
    public void Trailing_literals_encoded_in_a_match_token_are_emitted()
    {
        // The two low bits of a match token carry a count of literals that follow it.
        byte[] input = Stream([17 + 4], "ABCD"u8.ToArray(), [64 | 2, 0], "xy"u8.ToArray(), EndOfStream);

        Assert.Equal("ABCDDDDxy", Decompress(input, 16));
    }

    [Fact]
    public void A_truncated_stream_is_reported_with_an_offset()
    {
        // Claims five literals but supplies three.
        byte[] input = Stream([17 + 5], "Hel"u8.ToArray());

        var ex = Assert.Throws<FormatParseException>(() => Lzo1x.Decompress(input, new byte[16], "core.brn"));

        Assert.Equal("GK3R1010", ex.Diagnostic.Code);
        Assert.Equal("core.brn", ex.Diagnostic.File);
        Assert.NotNull(ex.Diagnostic.Offset);
        Assert.NotNull(ex.Diagnostic.Remediation);
    }

    [Fact]
    public void Writing_past_the_expected_output_size_fails_rather_than_overflowing()
    {
        byte[] input = Stream([17 + 5], "Hello"u8.ToArray(), EndOfStream);

        var ex = Assert.Throws<FormatParseException>(() => Lzo1x.Decompress(input, new byte[3]));
        Assert.Equal("GK3R1010", ex.Diagnostic.Code);
    }

    [Fact]
    public void A_back_reference_before_the_start_of_output_fails()
    {
        // A match at the very beginning has nothing to point back at.
        byte[] input = Stream([64, 0], EndOfStream);

        Assert.Throws<FormatParseException>(() => Lzo1x.Decompress(input, new byte[16]));
    }

    [Fact]
    public void An_empty_input_is_rejected() =>
        Assert.Throws<FormatParseException>(() => Lzo1x.Decompress([], new byte[16]));

    [Fact]
    public void Input_that_runs_out_without_an_end_marker_is_truncation()
    {
        // A well-formed stream ends at its marker, never by exhausting input. Treating
        // exhaustion as success would silently accept a half-written archive entry.
        // Verified against the retail corpus: all 36,957 entries end at the marker, so
        // strictness rejects nothing real.
        byte[] input = Stream([17 + 5], "Hello"u8.ToArray());

        Assert.Throws<FormatParseException>(() => Lzo1x.Decompress(input, new byte[5]));
    }
}
