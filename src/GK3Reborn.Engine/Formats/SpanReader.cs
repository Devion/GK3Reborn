using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using GK3Reborn.Foundation.Diagnostics;

namespace GK3Reborn.Formats;

/// <summary>
/// Thrown when a parser reads past the end of its buffer or sees an impossible value.
/// </summary>
public sealed class FormatParseException : Exception
{
    /// <summary>Creates an exception carrying an actionable diagnostic.</summary>
    public FormatParseException(Diagnostic diagnostic)
        : base(diagnostic?.ToString() ?? "parse error")
    {
        Diagnostic = diagnostic!;
    }

    /// <summary>Creates an exception with a message only.</summary>
    public FormatParseException(string message)
        : base(message)
    {
        Diagnostic = new Diagnostic("GK3R1000", DiagnosticSeverity.Error, message);
    }

    /// <summary>Creates an exception with a message and inner cause.</summary>
    public FormatParseException(string message, Exception innerException)
        : base(message, innerException)
    {
        Diagnostic = new Diagnostic("GK3R1000", DiagnosticSeverity.Error, message);
    }

    /// <summary>The structured diagnostic for this failure.</summary>
    public Diagnostic Diagnostic { get; }
}

/// <summary>
/// A bounds-checked little-endian reader over a byte span.
/// </summary>
/// <remarks>
/// Every original-format parser reads through this type. Plan/01 requires checked
/// arithmetic and bounds checks in parsers, and Plan/02 requires that a corrupt or
/// truncated file fails safely with the file, offset and expectation named - not
/// with an <see cref="IndexOutOfRangeException"/> from somewhere deep in a loop.
/// </remarks>
public ref struct SpanReader
{
    private readonly ReadOnlySpan<byte> _data;
    private readonly string _file;
    private int _position;

    /// <summary>Creates a reader over <paramref name="data"/>.</summary>
    /// <param name="data">Buffer to read.</param>
    /// <param name="file">Name used in diagnostics.</param>
    public SpanReader(ReadOnlySpan<byte> data, string file = "<memory>")
    {
        _data = data;
        _file = file;
        _position = 0;
    }

    /// <summary>Current byte offset.</summary>
    public readonly int Position => _position;

    /// <summary>Total length of the buffer.</summary>
    public readonly int Length => _data.Length;

    /// <summary>Bytes not yet consumed.</summary>
    public readonly int Remaining => _data.Length - _position;

    /// <summary>Reads one unsigned byte.</summary>
    public byte ReadUInt8() => _data[Demand(1)];

    /// <summary>Reads one signed byte.</summary>
    public sbyte ReadInt8() => (sbyte)_data[Demand(1)];

    /// <summary>Reads a little-endian 16-bit unsigned integer.</summary>
    public ushort ReadUInt16() => BinaryPrimitives.ReadUInt16LittleEndian(_data[Demand(2)..]);

    /// <summary>Reads a little-endian 16-bit signed integer.</summary>
    public short ReadInt16() => BinaryPrimitives.ReadInt16LittleEndian(_data[Demand(2)..]);

    /// <summary>Reads a little-endian 32-bit unsigned integer.</summary>
    public uint ReadUInt32() => BinaryPrimitives.ReadUInt32LittleEndian(_data[Demand(4)..]);

    /// <summary>Reads a little-endian 32-bit signed integer.</summary>
    public int ReadInt32() => BinaryPrimitives.ReadInt32LittleEndian(_data[Demand(4)..]);

    /// <summary>Reads a little-endian 32-bit float.</summary>
    public float ReadSingle() => BinaryPrimitives.ReadSingleLittleEndian(_data[Demand(4)..]);

    /// <summary>Reads <paramref name="count"/> raw bytes.</summary>
    public ReadOnlySpan<byte> ReadBytes(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        return _data.Slice(Demand(count), count);
    }

    /// <summary>Reads a fixed-length string, stopping at the first NUL.</summary>
    /// <param name="length">Number of bytes occupied by the field.</param>
    /// <param name="encoding">Encoding to decode with; Latin-1 when null.</param>
    public string ReadFixedString(int length, Encoding? encoding = null)
    {
        ReadOnlySpan<byte> raw = ReadBytes(length);
        int nul = raw.IndexOf((byte)0);
        if (nul >= 0)
        {
            raw = raw[..nul];
        }

        return (encoding ?? Encoding.Latin1).GetString(raw);
    }

    /// <summary>Reads a length-prefixed string whose prefix is a single byte.</summary>
    public string ReadByteLengthString(Encoding? encoding = null) => ReadFixedString(ReadUInt8(), encoding);

    /// <summary>Moves the cursor to an absolute offset.</summary>
    public void Seek(int offset)
    {
        if ((uint)offset > (uint)_data.Length)
        {
            throw Fail(offset, $"offset within 0..{_data.Length}", offset.ToString(CultureInfo.InvariantCulture));
        }

        _position = offset;
    }

    /// <summary>Advances the cursor by <paramref name="count"/> bytes.</summary>
    public void Skip(int count) => Demand(count);

    /// <summary>
    /// Verifies that the next bytes equal <paramref name="expected"/> and consumes them.
    /// </summary>
    public void ExpectMagic(ReadOnlySpan<byte> expected, string description)
    {
        int at = _position;
        ReadOnlySpan<byte> actual = ReadBytes(expected.Length);
        if (!actual.SequenceEqual(expected))
        {
            throw new FormatParseException(new Diagnostic(
                "GK3R1002",
                DiagnosticSeverity.Error,
                $"{description}: signature mismatch.",
                _file,
                at,
                Describe(expected),
                Describe(actual),
                "The file is not of the expected type, or it is corrupt. Verify the source installation."));
        }
    }

    private int Demand(int count)
    {
        if (count < 0 || _position + count > _data.Length)
        {
            throw Fail(_position, $"{count} more byte(s)", $"{Remaining} remaining");
        }

        int start = _position;
        _position += count;
        return start;
    }

    private readonly FormatParseException Fail(int offset, string expected, string actual) =>
        new(new Diagnostic(
            "GK3R1001",
            DiagnosticSeverity.Error,
            "Read past end of buffer.",
            _file,
            offset,
            expected,
            actual,
            "The asset is truncated or the parser is misaligned. Re-extract the archive and report the offset."));

    private static string Describe(ReadOnlySpan<byte> bytes)
    {
        var sb = new StringBuilder();
        foreach (byte b in bytes)
        {
            sb.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
        }

        return sb.ToString();
    }
}
