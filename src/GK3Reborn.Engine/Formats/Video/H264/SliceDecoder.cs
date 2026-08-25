using System.Runtime.CompilerServices;

namespace GK3Reborn.Formats.Video.H264;

/// <summary>
/// Decodes the macroblocks of one slice into a picture.
/// </summary>
/// <remarks>
/// <para>
/// One object per picture, re-pointed at each slice: the scratch macroblock, the entropy
/// decoders and the prediction buffers are allocated once. The slice loop is here; the
/// entropy-specific parsing is in the CABAC and CAVLC partial files, motion vector
/// prediction in the motion one, and the sample-level work — intra prediction, motion
/// compensation, transforms — in static helpers that know nothing about slices.
/// </para>
/// <para>
/// Macroblocks are addressed in raster order and a neighbour is available when it is in
/// the picture, has been decoded, and belongs to this slice — the last being what makes
/// slices independently decodable and what the deblocking filter, which runs afterwards
/// over the whole picture, does not care about.
/// </para>
/// </remarks>
internal sealed partial class SliceDecoder
{
    private readonly Picture _pic;
    private readonly SequenceParameterSet _sps;
    private readonly int _widthMbs;
    private readonly int _chromaFormat;
    private readonly Macroblock _mb = new();
    private readonly CabacEngine _cabac = new();
    private byte[] _cabacRbsp = [];
    private int _cabacLength;
    private BitReader _reader;

    private SliceHeader _h = null!;
    private PictureParameterSet _pps = null!;
    private int _sliceId;
    private int _qp;
    private bool _lastQpDeltaNonZero;
    private int _skipRun;

    // Neighbouring macroblock addresses, or -1 when not available to this slice.
    private int _mbA, _mbB, _mbC, _mbD;

    /// <summary>Reference picture lists for the current slice.</summary>
    public readonly Picture?[] RefList0 = new Picture?[33];
    public readonly Picture?[] RefList1 = new Picture?[33];

    /// <summary>The picture that supplies co-located motion for temporal direct prediction.</summary>
    private Picture? _colocated;

    /// <summary>Implicit bi-prediction weights per (refIdxL0, refIdxL1), or null.</summary>
    private int[,]? _implicitWeights;

    public SliceDecoder(Picture picture, SequenceParameterSet sps)
    {
        _pic = picture;
        _sps = sps;
        _widthMbs = sps.WidthMbs;
        _chromaFormat = sps.ChromaFormatIdc;
    }

    /// <summary>Decodes one slice's macroblocks.</summary>
    /// <param name="header">Its header.</param>
    /// <param name="nal">The unit it came in, with the header already read.</param>
    /// <param name="sliceId">A number distinct from every other slice of the picture.</param>
    public void Decode(SliceHeader header, NalUnit nal, int sliceId)
    {
        _h = header;
        _pps = header.Pps;
        _sliceId = sliceId;
        _qp = header.SliceQp;
        _lastQpDeltaNonZero = false;
        _pic.Slices.Add(header);

        PrepareWeights();


        int mbAddr = header.FirstMb;

        if (mbAddr >= _pic.MbCount)
        {
            throw new FormatParseException("H.264: a slice starts beyond the end of the picture.");
        }

        if (_pps.Cabac)
        {
            _cabacRbsp = nal.Rbsp;
            _cabacLength = nal.Length;
            _cabac.InitContexts(header.Type, header.CabacInitIdc, header.SliceQp);
            // cabac_alignment_one_bit: the data starts at the next byte.
            _cabac.Start(nal.Rbsp, (header.DataBitOffset + 7) >> 3, nal.Length);
            DecodeCabacSlice(mbAddr);
        }
        else
        {
            _reader = new BitReader(nal.Rbsp, nal.Length);
            _reader.Skip(header.DataBitOffset);
            _skipRun = -1;
            DecodeCavlcSlice(mbAddr);
        }
    }

    private void DecodeCabacSlice(int mbAddr)
    {
        while (mbAddr < _pic.MbCount)
        {
            Begin(mbAddr);

            bool skip = false;

            if (!_h.IsIntra)
            {
                skip = DecodeCabacSkipFlag();
            }

            if (skip)
            {
                _mb.Skip = true;
                _lastQpDeltaNonZero = false;
                ReconstructSkip();
            }
            else
            {
                ParseCabacMacroblock();
                Reconstruct();
            }

            Store();

            if (_cabac.DecodeTerminate() == 1)
            {
                return;
            }

            mbAddr++;
        }
    }

    private void DecodeCavlcSlice(int mbAddr)
    {
        // mb_skip_run is read before a coded macroblock, and again after each coded one;
        // a run that has just ended is followed directly by the macroblock it stopped at.
        while (mbAddr < _pic.MbCount)
        {
            Begin(mbAddr);

            if (!_h.IsIntra)
            {
                if (_skipRun < 0)
                {
                    _skipRun = _reader.ReadUe();
                }

                if (_skipRun > 0)
                {
                    _skipRun--;
                    _mb.Skip = true;
                    ReconstructSkip();
                    Store();
                    mbAddr++;

                    if (_skipRun == 0 && !_reader.MoreRbspData())
                    {
                        return;
                    }

                    continue;
                }
            }

            ParseCavlcMacroblock();
            Reconstruct();
            Store();
            mbAddr++;
            _skipRun = -1;

            if (!_reader.MoreRbspData())
            {
                return;
            }
        }
    }

    // ---- per macroblock bookkeeping --------------------------------------------------------

    private void Begin(int mbAddr)
    {
        int x = mbAddr % _widthMbs;
        int y = mbAddr / _widthMbs;
        _mb.Reset(mbAddr, x, y);
        _spatialRef0 = -2;

        _mbA = x > 0 ? Available(mbAddr - 1) : -1;
        _mbB = y > 0 ? Available(mbAddr - _widthMbs) : -1;
        _mbC = y > 0 && x < _widthMbs - 1 ? Available(mbAddr - _widthMbs + 1) : -1;
        _mbD = y > 0 && x > 0 ? Available(mbAddr - _widthMbs - 1) : -1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int Available(int addr) =>
        (_pic.MbFlags[addr] & MbFlag.Available) != 0 && _pic.SliceId[addr] == _sliceId ? addr : -1;

    /// <summary>Writes what later macroblocks and pictures need to know about this one.</summary>
    private void Store()
    {
        Macroblock mb = _mb;
        int addr = mb.Addr;
        Picture pic = _pic;


        byte flags = MbFlag.Available;

        if (mb.Intra)
        {
            flags |= MbFlag.Intra;
        }

        if (mb.Skip)
        {
            flags |= MbFlag.Skip;
        }

        if (mb.IntraNxN)
        {
            flags |= MbFlag.IntraNxN;
        }

        if (mb.Pcm)
        {
            flags |= MbFlag.Pcm;
        }

        if (mb.Direct16x16)
        {
            flags |= MbFlag.Direct16x16;
        }

        if (mb.Transform8x8)
        {
            flags |= MbFlag.Transform8x8;
        }

        if (mb.Intra16x16)
        {
            flags |= MbFlag.Intra16x16;
        }

        pic.MbFlags[addr] = flags;
        pic.SliceId[addr] = _sliceId;
        pic.QpY[addr] = (sbyte)mb.QpY;
        pic.QpCb[addr] = (sbyte)mb.QpCb;
        pic.QpCr[addr] = (sbyte)mb.QpCr;
        pic.Cbp[addr] = (byte)(mb.Pcm ? 0x2F : mb.Cbp);
        pic.IntraChromaPredMode[addr] = (byte)mb.IntraChromaPredMode;

        if (mb.Pcm)
        {
            pic.CbfLuma[addr] = pic.CbfCb[addr] = pic.CbfCr[addr] = 0x1FFFF;
            pic.NonZeroBits[addr] = 0xFFFF;
            Array.Fill(pic.Nnz, (byte)16, addr * 48, 48);
        }
        else
        {
            pic.CbfLuma[addr] = mb.Luma.Cbf;
            pic.CbfCb[addr] = mb.Cb.Cbf;
            pic.CbfCr[addr] = mb.Cr.Cbf;

            int nonZero = mb.Luma.NonZero4x4;

            if (mb.Transform8x8)
            {
                int nz8 = mb.Luma.NonZero8x8;
                nonZero = 0;

                for (int b = 0; b < 4; b++)
                {
                    if ((nz8 & (1 << b)) != 0)
                    {
                        int bx = (b & 1) * 2;
                        int by = (b >> 1) * 2;
                        nonZero |= (3 << (by * 4 + bx)) | (3 << ((by + 1) * 4 + bx));
                    }
                }
            }

            pic.NonZeroBits[addr] = nonZero;
            mb.Luma.Nnz.CopyTo(pic.Nnz, addr * 48);
            mb.Cb.Nnz.CopyTo(pic.Nnz, addr * 48 + 16);
            mb.Cr.Nnz.CopyTo(pic.Nnz, addr * 48 + 32);
        }

        int block = addr * 16;
        mb.IntraModes.CopyTo(pic.IntraModes, block);

        if (mb.Intra)
        {
            Array.Fill(pic.Ref0, (sbyte)-1, block, 16);
            Array.Fill(pic.Ref1, (sbyte)-1, block, 16);
            Array.Clear(pic.Mv0, block, 16);
            Array.Clear(pic.Mv1, block, 16);
            Array.Clear(pic.RefPic0, block, 16);
            Array.Clear(pic.RefPic1, block, 16);
            Array.Clear(pic.Mvd0, block, 16);
            Array.Clear(pic.Mvd1, block, 16);
        }
        else
        {
            mb.Mv0.CopyTo(pic.Mv0, block);
            mb.Mv1.CopyTo(pic.Mv1, block);
            mb.Ref0.CopyTo(pic.Ref0, block);
            mb.Ref1.CopyTo(pic.Ref1, block);
            mb.Mvd0.CopyTo(pic.Mvd0, block);
            mb.Mvd1.CopyTo(pic.Mvd1, block);

            for (int i = 0; i < 16; i++)
            {
                Picture? r0 = mb.Ref0[i] >= 0 ? RefList0[mb.Ref0[i]] : null;
                Picture? r1 = mb.Ref1[i] >= 0 ? RefList1[mb.Ref1[i]] : null;
                pic.RefPic0[block + i] = r0?.Serial ?? 0;
                pic.RefPic1[block + i] = r1?.Serial ?? 0;
                pic.RefPoc0[block + i] = r0?.Poc ?? 0;
                pic.RefPoc1[block + i] = r1?.Poc ?? 0;
            }
        }
    }

    /// <summary>Applies mb_qp_delta and derives the chroma QPs, 7.4.5 and 8.5.11.</summary>
    private void ApplyQp(bool hasDelta)
    {
        if (hasDelta)
        {
            int delta = _mb.QpDelta;

            if (delta < -26 || delta > 25)
            {
                throw new FormatParseException("H.264: mb_qp_delta out of range.");
            }

            _qp = (_qp + delta + 52) % 52;
        }

        _mb.QpY = _qp;
        _mb.QpCb = Tables.ChromaQp(Math.Clamp(_qp + _pps.ChromaQpIndexOffset, 0, 51));
        _mb.QpCr = Tables.ChromaQp(Math.Clamp(_qp + _pps.SecondChromaQpIndexOffset, 0, 51));
    }

    // ---- neighbour lookups shared by both entropy decoders ------------------------------------

    /// <summary>The address of the macroblock holding the 4x4 block left of (bx, by), and that block's raster index.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int LeftBlock(int bx, int by, out int addr)
    {
        if (bx > 0)
        {
            addr = _mb.Addr;
            return by * 4 + bx - 1;
        }

        addr = _mbA;
        return by * 4 + 3;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int TopBlock(int bx, int by, out int addr)
    {
        if (by > 0)
        {
            addr = _mb.Addr;
            return (by - 1) * 4 + bx;
        }

        addr = _mbB;
        return 12 + bx;
    }

    /// <summary>Number of 4x4 chroma blocks across and down a macroblock.</summary>
    private int ChromaBlocksWide => _chromaFormat == 3 ? 4 : 2;

    private int ChromaBlocksHigh => _chromaFormat == 1 ? 2 : 4;

    /// <summary>
    /// The chroma 4x4 block left of (bx, by) in chroma block units, using the chroma
    /// block grid of this chroma format.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int LeftChromaBlock(int bx, int by, out int addr)
    {
        int wide = ChromaBlocksWide;

        if (bx > 0)
        {
            addr = _mb.Addr;
            return by * wide + bx - 1;
        }

        addr = _mbA;
        return by * wide + wide - 1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int TopChromaBlock(int bx, int by, out int addr)
    {
        int wide = ChromaBlocksWide;

        if (by > 0)
        {
            addr = _mb.Addr;
            return (by - 1) * wide + bx;
        }

        addr = _mbB;
        return (ChromaBlocksHigh - 1) * wide + bx;
    }

    /// <summary>Total coefficients of a 4x4 block of the current or a stored macroblock.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int NnzOf(int addr, int component, int block)
    {
        if (addr == _mb.Addr)
        {
            Residual r = component == 0 ? _mb.Luma : component == 1 ? _mb.Cb : _mb.Cr;
            return r.Nnz[block];
        }

        return _pic.Nnz[addr * 48 + component * 16 + block];
    }

    private bool IsIntraMb(int addr) =>
        addr == _mb.Addr ? _mb.Intra : (_pic.MbFlags[addr] & MbFlag.Intra) != 0;
}
