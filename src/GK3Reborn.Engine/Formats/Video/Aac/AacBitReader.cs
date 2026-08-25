using System.Buffers.Binary;

namespace GK3Reborn.Formats.Video.Aac;

/// <summary>
/// MSB-first bit reader over one AAC access unit.
/// </summary>
/// <remarks>
/// AAC syntax is a stream of fields from one to twenty-odd bits wide, so the reader
/// works on a 32-bit window that is refilled from the bytes on demand. Peeking past
/// the end returns zero bits (Huffman lookups peek further than the code they end up
/// consuming); actually consuming past the end is a corrupt frame and throws.
/// </remarks>
internal ref struct AacBitReader
{
    private readonly ReadOnlySpan<byte> _data;
    private int _position; // in bits

    public AacBitReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _position = 0;
    }

    /// <summary>Bits consumed so far.</summary>
    public readonly int Position => _position;

    /// <summary>Bits left before the end of the access unit.</summary>
    public readonly int Remaining => _data.Length * 8 - _position;

    /// <summary>Returns the next <paramref name="count"/> bits (at most 25) without consuming them.</summary>
    public readonly uint Peek(int count)
    {
        int byteIndex = _position >> 3;
        uint window;
        if (byteIndex + 4 <= _data.Length)
        {
            window = BinaryPrimitives.ReadUInt32BigEndian(_data.Slice(byteIndex, 4));
        }
        else
        {
            // Tail of the frame: pad with zero bits so peeks stay valid to the last bit.
            window = 0;
            for (int i = 0; i < 4; i++)
            {
                window <<= 8;
                if (byteIndex + i < _data.Length)
                {
                    window |= _data[byteIndex + i];
                }
            }
        }

        window <<= _position & 7;
        return window >> (32 - count);
    }

    /// <summary>Consumes <paramref name="count"/> bits (at most 25) and returns them.</summary>
    public uint ReadBits(int count)
    {
        if (count == 0)
        {
            return 0;
        }

        uint value = Peek(count);
        Skip(count);
        return value;
    }

    /// <summary>Consumes up to 32 bits.</summary>
    public uint ReadBitsLong(int count)
    {
        if (count <= 25)
        {
            return ReadBits(count);
        }

        uint high = ReadBits(count - 16);
        return (high << 16) | ReadBits(16);
    }

    public int ReadInt(int count) => (int)ReadBits(count);

    public bool ReadBool() => ReadBits(1) != 0;

    /// <summary>Advances past <paramref name="count"/> bits, refusing to leave the frame.</summary>
    public void Skip(int count)
    {
        _position += count;
        if (_position > _data.Length * 8)
        {
            throw new FormatParseException("AAC: access unit ended in the middle of a syntax element");
        }
    }

    /// <summary>Moves to the next byte boundary (a no-op when already on one).</summary>
    public void ByteAlign()
    {
        int rem = _position & 7;
        if (rem != 0)
        {
            Skip(8 - rem);
        }
    }
}
