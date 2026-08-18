using System.Text;
using GK3Reborn.Formats;
using Xunit;

namespace GK3Reborn.Tests.Formats;

public sealed class SpanReaderTests
{
    [Fact]
    public void Reads_little_endian_primitives_in_order()
    {
        byte[] data = [0x01, 0x02, 0x03, 0x04, 0x05, 0x06];
        var reader = new SpanReader(data);

        Assert.Equal(0x0201, reader.ReadUInt16());
        Assert.Equal(0x06050403u, reader.ReadUInt32());
        Assert.Equal(0, reader.Remaining);
    }

    [Fact]
    public void Reading_past_the_end_names_the_file_and_offset()
    {
        byte[] data = [0x01, 0x02];
        var reader = new SpanReader(data, "core.brn");
        reader.ReadUInt16();

        var ex = Assert.Throws<FormatParseException>(() =>
        {
            var r = new SpanReader(data, "core.brn");
            r.ReadUInt16();
            r.ReadUInt32();
        });

        Assert.Equal("GK3R1001", ex.Diagnostic.Code);
        Assert.Equal("core.brn", ex.Diagnostic.File);
        Assert.Equal(2, ex.Diagnostic.Offset);
        Assert.NotNull(ex.Diagnostic.Remediation);
    }

    [Fact]
    public void Magic_mismatch_reports_expected_and_actual()
    {
        byte[] data = Encoding.ASCII.GetBytes("NOPE!Barn");

        var ex = Assert.Throws<FormatParseException>(() =>
        {
            var r = new SpanReader(data, "bad.brn");
            r.ExpectMagic("GK3!Barn"u8, "Barn archive header");
        });

        Assert.Equal("GK3R1002", ex.Diagnostic.Code);
        Assert.Equal("GK3!Barn", ex.Diagnostic.Expected);
        Assert.Equal("NOPE!Bar", ex.Diagnostic.Actual);
    }

    [Fact]
    public void Matching_magic_consumes_it()
    {
        byte[] data = Encoding.ASCII.GetBytes("GK3!Barn\x01\x00\x00\x00");
        var reader = new SpanReader(data, "core.brn");

        reader.ExpectMagic("GK3!Barn"u8, "Barn archive header");
        Assert.Equal(8, reader.Position);
        Assert.Equal(1u, reader.ReadUInt32());
    }

    [Fact]
    public void Fixed_strings_stop_at_the_first_nul()
    {
        byte[] data = [(byte)'M', (byte)'O', (byte)'D', 0, 0xFF, 0xFF];
        var reader = new SpanReader(data);

        Assert.Equal("MOD", reader.ReadFixedString(6));
        Assert.Equal(6, reader.Position);
    }

    [Fact]
    public void Seeking_outside_the_buffer_fails_loudly()
    {
        byte[] data = [1, 2, 3, 4];

        Assert.Throws<FormatParseException>(() =>
        {
            var r = new SpanReader(data, "x.bin");
            r.Seek(5);
        });

        Assert.Throws<FormatParseException>(() =>
        {
            var r = new SpanReader(data, "x.bin");
            r.Seek(-1);
        });
    }
}
