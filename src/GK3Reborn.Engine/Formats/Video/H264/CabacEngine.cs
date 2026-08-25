using System.Runtime.CompilerServices;

namespace GK3Reborn.Formats.Video.H264;

/// <summary>
/// The arithmetic decoding engine of CABAC, 9.3.1.2 and 9.3.3.2.
/// </summary>
/// <remarks>
/// Written exactly as the standard describes it, one bit of renormalisation at a time,
/// rather than with the byte-at-a-time tricks real decoders use. The standard's form is
/// easy to check and, because most bins are decided without renormalising at all, it is
/// not the part of the decoder that costs anything.
/// </remarks>
internal sealed class CabacEngine
{
    private readonly byte[] _states = new byte[1024];
    private byte[] _data = [];
    private int _end;
    private int _bitPosition;
    private uint _range;
    private uint _offset;

    /// <summary>Sets every context from the initialisation tables, 9.3.1.1.</summary>
    public void InitContexts(SliceType type, int cabacInitIdc, int sliceQp)
    {
        sbyte[] table = type == SliceType.I ? Tables.CabacInitI : Tables.CabacInitPB[cabacInitIdc];
        int qp = Math.Clamp(sliceQp, 0, 51);

        for (int ctx = 0; ctx < 1024; ctx++)
        {
            int m = table[ctx * 2];
            int n = table[ctx * 2 + 1];
            int pre = Math.Clamp(((m * qp) >> 4) + n, 1, 126);
            _states[ctx] = pre <= 63 ? (byte)((63 - pre) << 1) : (byte)(((pre - 64) << 1) | 1);
        }
    }

    /// <summary>Starts decoding at a byte position of the RBSP, 9.3.1.2.</summary>
    public void Start(byte[] data, int byteOffset, int length)
    {
        _data = data;
        _end = length;
        _bitPosition = byteOffset * 8;
        _range = 510;
        _offset = ReadBits(9);
    }

    /// <summary>The byte after the last one consumed, for restarting after PCM samples.</summary>
    public int BytePosition => (_bitPosition + 7) >> 3;

    /// <summary>
    /// Where I_PCM samples start after a terminate bin of 1: the byte after the flush.
    /// </summary>
    /// <remarks>
    /// The encoder's flush (9.3.4.5) writes ten bits after the terminating decision, the
    /// last of them a 1; this decoder reads nine bits ahead, so after decoding that bin
    /// without renormalising, the next unread bit is that final 1. It is skipped, and the
    /// pcm_alignment_zero_bits take the position to the next byte. The same arithmetic is
    /// what makes end_of_slice_flag's flush bit the rbsp_stop_one_bit.
    /// </remarks>
    public int PcmStart() => (_bitPosition + 1 + 7) >> 3;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint ReadBit()
    {
        int index = _bitPosition >> 3;
        uint bit = index < _end ? (uint)(_data[index] >> (7 - (_bitPosition & 7))) & 1u : 0u;
        _bitPosition++;
        return bit;
    }

    private uint ReadBits(int count)
    {
        uint value = 0;

        for (int i = 0; i < count; i++)
        {
            value = (value << 1) | ReadBit();
        }

        return value;
    }

    /// <summary>Decodes one context-coded bin, 9.3.3.2.1.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Decode(int ctx)
    {
        byte state = _states[ctx];
        int pState = state >> 1;
        int bin = state & 1;
        uint lps = Tables.RangeLps[(pState << 2) + (int)((_range >> 6) & 3)];
        _range -= lps;

        if (_offset < _range)
        {
            _states[ctx] = (byte)((Tables.TransIdxMps[pState] << 1) | bin);

            if (_range >= 256)
            {
                return bin;
            }
        }
        else
        {
            _offset -= _range;
            _range = lps;
            bin ^= 1;
            int next = Tables.TransIdxLps[pState];
            int mps = pState == 0 ? bin : (state & 1);
            _states[ctx] = (byte)((next << 1) | mps);
        }

        // Renormalise.
        do
        {
            _range <<= 1;
            _offset = (_offset << 1) | ReadBit();
        }
        while (_range < 256);

        return bin;
    }

    /// <summary>Decodes one bypass bin, 9.3.3.2.3.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int DecodeBypass()
    {
        _offset = (_offset << 1) | ReadBit();

        if (_offset >= _range)
        {
            _offset -= _range;
            return 1;
        }

        return 0;
    }

    /// <summary>Decodes the terminate bin, 9.3.3.2.4.</summary>
    public int DecodeTerminate()
    {
        _range -= 2;

        if (_offset >= _range)
        {
            return 1;
        }

        while (_range < 256)
        {
            _range <<= 1;
            _offset = (_offset << 1) | ReadBit();
        }

        return 0;
    }

    /// <summary>Position in bits of the next unread bit, for PCM alignment.</summary>
    public int BitPosition => _bitPosition;

    /// <summary>Restarts the engine at a byte boundary after PCM samples, 9.3.1.2.</summary>
    public void Restart(int bytePosition)
    {
        _bitPosition = bytePosition * 8;
        _range = 510;
        _offset = ReadBits(9);
    }
}
