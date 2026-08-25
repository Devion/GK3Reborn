using System.Buffers.Binary;

namespace GK3Reborn.Formats.Video.H264;

/// <summary>NAL unit types the decoder tells apart.</summary>
internal static class NalType
{
    public const int Slice = 1;
    public const int SliceDataA = 2;
    public const int SliceDataB = 3;
    public const int SliceDataC = 4;
    public const int IdrSlice = 5;
    public const int Sei = 6;
    public const int Sps = 7;
    public const int Pps = 8;
    public const int AccessUnitDelimiter = 9;
    public const int EndOfSequence = 10;
    public const int EndOfStream = 11;
    public const int FillerData = 12;
    public const int SpsExtension = 13;
    public const int PrefixNal = 14;
    public const int SubsetSps = 15;
    public const int AuxiliarySlice = 19;
    public const int SliceExtension = 20;
}

/// <summary>
/// One NAL unit with its emulation-prevention bytes removed.
/// </summary>
/// <remarks>
/// The payload buffer is reused between units, so a unit is valid only until the next is
/// read. The parameter sets, which have to outlive the unit they arrived in, copy what
/// they keep.
/// </remarks>
internal struct NalUnit
{
    public int Type;
    public int RefIdc;
    public byte[] Rbsp;
    public int Length;
}

/// <summary>
/// Splits an access unit into NAL units, in either of the two framings.
/// </summary>
/// <remarks>
/// MP4 samples carry each unit behind a length prefix whose width <c>avcC</c> declares;
/// raw streams use Annex B start codes. Both are read so that a test can feed the decoder
/// from an <c>.h264</c> file that FFmpeg wrote, which is the easiest reference to make.
/// </remarks>
internal sealed class NalReader
{
    private byte[] _rbsp = new byte[64 * 1024];

    /// <summary>Walks the units of a length-prefixed access unit.</summary>
    public IEnumerable<NalUnit> ReadLengthPrefixed(ReadOnlyMemory<byte> sample, int lengthSize)
    {
        int at = 0;

        while (at + lengthSize <= sample.Length)
        {
            ReadOnlySpan<byte> span = sample.Span;
            int length = lengthSize switch
            {
                1 => span[at],
                2 => BinaryPrimitives.ReadUInt16BigEndian(span[at..]),
                3 => (span[at] << 16) | (span[at + 1] << 8) | span[at + 2],
                _ => BinaryPrimitives.ReadInt32BigEndian(span[at..]),
            };

            at += lengthSize;

            if (length <= 0 || at + length > sample.Length)
            {
                yield break;
            }

            yield return Unescape(sample.Slice(at, length).Span);
            at += length;
        }
    }

    /// <summary>Walks the units of an Annex B byte stream.</summary>
    public IEnumerable<NalUnit> ReadAnnexB(ReadOnlyMemory<byte> stream)
    {
        int at = FindStartCode(stream.Span, 0);

        while (at >= 0)
        {
            // Skip the start code itself.
            int start = at + 3;
            int next = FindStartCode(stream.Span, start);
            int end = next < 0 ? stream.Length : next;

            // A four-byte start code has a zero before it that belongs to nobody.
            while (end > start && stream.Span[end - 1] == 0)
            {
                end--;
            }

            if (end > start)
            {
                yield return Unescape(stream.Slice(start, end - start).Span);
            }

            at = next;
        }
    }

    private static int FindStartCode(ReadOnlySpan<byte> data, int from)
    {
        for (int i = from; i + 2 < data.Length; i++)
        {
            if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Removes the emulation-prevention bytes and reads the header.</summary>
    public NalUnit Unescape(ReadOnlySpan<byte> nal)
    {
        if (_rbsp.Length < nal.Length)
        {
            _rbsp = new byte[Math.Max(nal.Length, _rbsp.Length * 2)];
        }

        int length = 0;
        int zeros = 0;

        for (int i = 1; i < nal.Length; i++)
        {
            byte b = nal[i];

            if (zeros >= 2 && b == 3)
            {
                // 00 00 03 xx: the 03 is not data.
                zeros = 0;
                continue;
            }

            _rbsp[length++] = b;
            zeros = b == 0 ? zeros + 1 : 0;
        }

        return new NalUnit
        {
            Type = nal[0] & 0x1F,
            RefIdc = (nal[0] >> 5) & 3,
            Rbsp = _rbsp,
            Length = length,
        };
    }
}
