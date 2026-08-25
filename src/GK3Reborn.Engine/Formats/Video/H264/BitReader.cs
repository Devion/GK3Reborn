using System.Runtime.CompilerServices;

namespace GK3Reborn.Formats.Video.H264;

/// <summary>
/// Reads an RBSP most-significant-bit first: fixed fields and Exp-Golomb codes.
/// </summary>
/// <remarks>
/// Over a buffer that has already had its emulation-prevention bytes removed, so the
/// reader itself never has to look for <c>00 00 03</c>. Reading past the end yields zero
/// bits rather than throwing; parsers check <see cref="Overrun"/> where a truncated header
/// would matter, and a truncated slice merely decodes as garbage in its last macroblocks,
/// which is what every other decoder does with it too.
/// </remarks>
internal struct BitReader
{
    private readonly byte[] _data;
    private readonly int _length;
    private int _position; // in bits

    public BitReader(byte[] data, int length, int startByte = 0)
    {
        _data = data;
        _length = length;
        _position = startByte * 8;
    }

    /// <summary>Bits read so far.</summary>
    public readonly int Position => _position;

    /// <summary>How many bits remain.</summary>
    public readonly int Remaining => _length * 8 - _position;

    /// <summary>Whether more has been read than there was.</summary>
    public readonly bool Overrun => _position > _length * 8;

    /// <summary>The byte index of the next unread bit.</summary>
    public readonly int BytePosition => (_position + 7) >> 3;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ReadBit()
    {
        int index = _position >> 3;
        int bit = index < _length ? (_data[index] >> (7 - (_position & 7))) & 1 : 0;
        _position++;
        return bit;
    }

    public bool ReadFlag() => ReadBit() != 0;

    /// <summary>Reads up to 32 bits.</summary>
    public uint ReadBits(int count)
    {
        uint value = 0;

        for (int i = 0; i < count; i++)
        {
            value = (value << 1) | (uint)ReadBit();
        }

        return value;
    }

    /// <summary>Peeks at up to 32 bits without consuming them.</summary>
    public readonly uint PeekBits(int count)
    {
        BitReader copy = this;
        return copy.ReadBits(count);
    }

    public void Skip(int count) => _position += count;

    /// <summary>ue(v).</summary>
    public int ReadUe()
    {
        int zeros = 0;

        while (ReadBit() == 0)
        {
            zeros++;

            if (zeros > 31)
            {
                throw new FormatParseException("H.264: an Exp-Golomb code longer than 32 bits.");
            }
        }

        return zeros == 0 ? 0 : (int)((1u << zeros) - 1 + ReadBits(zeros));
    }

    /// <summary>se(v).</summary>
    public int ReadSe()
    {
        int k = ReadUe();
        return (k & 1) != 0 ? (k + 1) >> 1 : -(k >> 1);
    }

    /// <summary>te(v) with the given range.</summary>
    public int ReadTe(int range) => range > 1 ? ReadUe() : 1 - ReadBit();

    /// <summary>Moves to the next byte boundary.</summary>
    public void AlignToByte() => _position = (_position + 7) & ~7;

    /// <summary>more_rbsp_data(): whether anything but the trailing stop bit remains.</summary>
    public readonly bool MoreRbspData()
    {
        if (_position >= _length * 8)
        {
            return false;
        }

        // Find the last one bit in the buffer; that is the stop bit.
        int lastByte = _length - 1;

        while (lastByte >= 0 && _data[lastByte] == 0)
        {
            lastByte--;
        }

        if (lastByte < 0)
        {
            return false;
        }

        int trailing = 0;
        byte b = _data[lastByte];

        while ((b & 1) == 0)
        {
            b >>= 1;
            trailing++;
        }

        int stopBit = lastByte * 8 + (7 - trailing);
        return _position < stopBit;
    }
}
