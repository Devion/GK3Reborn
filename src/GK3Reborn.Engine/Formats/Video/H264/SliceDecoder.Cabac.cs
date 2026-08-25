namespace GK3Reborn.Formats.Video.H264;

/// <summary>
/// The CABAC half of macroblock parsing: 7.3.5 read with the contexts of 9.3.
/// </summary>
/// <remarks>
/// Context index increments are derived from the neighbouring macroblocks exactly as
/// 9.3.3.1.1 says, with the picture's stored per-macroblock and per-block state standing
/// in for "the syntax element of mbAddrN". The conventions that make those lookups uniform
/// — a skipped macroblock stores cbp 0, an I_PCM one stores cbp 0x2F and every coded block
/// flag set — are chosen in <see cref="SliceDecoder.Store"/>.
/// </remarks>
internal sealed partial class SliceDecoder
{
    // ctxIdxOffsets, Table 9-34.
    private const int CtxMbTypeI = 3;
    private const int CtxMbSkipP = 11;
    private const int CtxMbTypeP = 14;
    private const int CtxMbTypePSuffix = 17;
    private const int CtxSubMbTypeP = 21;
    private const int CtxMbSkipB = 24;
    private const int CtxMbTypeB = 27;
    private const int CtxMbTypeBSuffix = 32;
    private const int CtxSubMbTypeB = 36;
    private const int CtxMvdX = 40;
    private const int CtxMvdY = 47;
    private const int CtxRefIdx = 54;
    private const int CtxQpDelta = 60;
    private const int CtxIntraChroma = 64;
    private const int CtxPrevIntraMode = 68;
    private const int CtxRemIntraMode = 69;
    private const int CtxCbpLuma = 73;
    private const int CtxCbpChroma = 77;
    private const int CtxTransform8x8 = 399;

    private static readonly int[] CbfOffset = [85, 89, 93, 97, 101, 1012, 460, 464, 468, 1016, 472, 476, 480, 1020];
    private static readonly int[] SigOffset = [105, 120, 134, 149, 152, 402, 484, 499, 513, 660, 528, 543, 557, 718];
    private static readonly int[] LastOffset = [166, 181, 195, 210, 213, 417, 572, 587, 601, 690, 616, 631, 645, 748];
    private static readonly int[] AbsOffset = [227, 237, 247, 257, 266, 426, 952, 962, 972, 708, 982, 992, 1002, 766];

    private static readonly byte[] ChromaDcScan422 = [0, 2, 1, 5, 3, 6, 4, 7];
    private static readonly byte[] Identity = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15];

    /// <summary>The 4x4 blocks covering each 8x8 block, in raster order.</summary>
    private static readonly int[][] Blocks8x8 = [[0, 1, 4, 5], [2, 3, 6, 7], [8, 9, 12, 13], [10, 11, 14, 15]];

    private readonly int[] _sigPositions = new int[64];

    private bool DecodeCabacSkipFlag()
    {
        int offset = _h.IsB ? CtxMbSkipB : CtxMbSkipP;
        int inc = 0;

        if (_mbA >= 0 && (_pic.MbFlags[_mbA] & MbFlag.Skip) == 0)
        {
            inc++;
        }

        if (_mbB >= 0 && (_pic.MbFlags[_mbB] & MbFlag.Skip) == 0)
        {
            inc++;
        }

        return _cabac.Decode(offset + inc) == 1;
    }

    private void ParseCabacMacroblock()
    {
        Macroblock mb = _mb;
        int mbType;

        if (_h.IsIntra)
        {
            mbType = DecodeCabacIntraMbType(CtxMbTypeI, true);
        }
        else if (_h.IsP)
        {
            if (_cabac.Decode(CtxMbTypeP) == 0)
            {
                if (_cabac.Decode(CtxMbTypeP + 1) == 0)
                {
                    mbType = 3 * _cabac.Decode(CtxMbTypeP + 2);
                }
                else
                {
                    mbType = 2 - _cabac.Decode(CtxMbTypeP + 3);
                }

                SetPMbType(mbType);
                mbType = -1;
            }
            else
            {
                mbType = DecodeCabacIntraMbType(CtxMbTypePSuffix, false);
            }
        }
        else
        {
            int inc = 0;

            if (_mbA >= 0 && (_pic.MbFlags[_mbA] & (MbFlag.Skip | MbFlag.Direct16x16)) == 0)
            {
                inc++;
            }

            if (_mbB >= 0 && (_pic.MbFlags[_mbB] & (MbFlag.Skip | MbFlag.Direct16x16)) == 0)
            {
                inc++;
            }

            if (_cabac.Decode(CtxMbTypeB + inc) == 0)
            {
                SetBMbType(0);
                mbType = -1;
            }
            else if (_cabac.Decode(CtxMbTypeB + 3) == 0)
            {
                SetBMbType(1 + _cabac.Decode(CtxMbTypeB + 5));
                mbType = -1;
            }
            else
            {
                int bits = _cabac.Decode(CtxMbTypeB + 4) << 3;
                bits |= _cabac.Decode(CtxMbTypeB + 5) << 2;
                bits |= _cabac.Decode(CtxMbTypeB + 5) << 1;
                bits |= _cabac.Decode(CtxMbTypeB + 5);

                if (bits < 8)
                {
                    SetBMbType(bits + 3);
                    mbType = -1;
                }
                else if (bits == 13)
                {
                    mbType = DecodeCabacIntraMbType(CtxMbTypeBSuffix, false);
                }
                else if (bits == 14)
                {
                    SetBMbType(11);
                    mbType = -1;
                }
                else if (bits == 15)
                {
                    SetBMbType(22);
                    mbType = -1;
                }
                else
                {
                    bits = (bits << 1) | _cabac.Decode(CtxMbTypeB + 5);
                    SetBMbType(bits - 4);
                    mbType = -1;
                }
            }
        }

        if (mbType >= 0)
        {
            SetIMbType(mbType);
        }

        if (mb.Pcm)
        {
            int start = _cabac.PcmStart();
            int count = 256 + 2 * (_sps.ChromaWidthMb * _sps.ChromaHeightMb);
            ReadPcm(start, count);
            _cabac.Restart(start + count);
            _lastQpDeltaNonZero = false;
            ApplyQp(false);
            return;
        }

        if (mb.IntraNxN)
        {
            if (_pps.Transform8x8Mode)
            {
                mb.Transform8x8 = _cabac.Decode(CtxTransform8x8 + Transform8x8Inc()) == 1;
            }

            ParseCabacIntraModes();
        }

        if (mb.Intra)
        {
            if (_chromaFormat is 1 or 2)
            {
                mb.IntraChromaPredMode = DecodeCabacIntraChromaPredMode();
            }
        }
        else
        {
            ParseCabacInterPrediction();
        }

        if (!mb.Intra16x16)
        {
            mb.Cbp = DecodeCabacCbp();

            if (mb.CbpLuma > 0 && _pps.Transform8x8Mode && !mb.IntraNxN && Transform8x8Allowed())
            {
                mb.Transform8x8 = _cabac.Decode(CtxTransform8x8 + Transform8x8Inc()) == 1;
            }
        }

        if (mb.Cbp != 0 || mb.Intra16x16)
        {
            mb.QpDelta = DecodeCabacQpDelta();
            _lastQpDeltaNonZero = mb.QpDelta != 0;
            ApplyQp(true);
            ParseCabacResidual();
        }
        else
        {
            _lastQpDeltaNonZero = false;
            ApplyQp(false);
        }
    }

    /// <summary>Whether transform_size_8x8_flag may follow the cbp, 7.3.5.</summary>
    private bool Transform8x8Allowed()
    {
        Macroblock mb = _mb;

        if (mb.Direct16x16)
        {
            return _sps.Direct8x8Inference;
        }

        if (mb.NumParts != 4)
        {
            return true;
        }

        for (int i = 0; i < 4; i++)
        {
            if (_h.IsB)
            {
                if (mb.SubDirect[i])
                {
                    if (!_sps.Direct8x8Inference)
                    {
                        return false;
                    }
                }
                else if (mb.SubMbType[i] > 3)
                {
                    return false;
                }
            }
            else if (mb.SubMbType[i] != 0)
            {
                return false;
            }
        }

        return true;
    }

    private int Transform8x8Inc()
    {
        int inc = 0;

        if (_mbA >= 0 && (_pic.MbFlags[_mbA] & MbFlag.Transform8x8) != 0)
        {
            inc++;
        }

        if (_mbB >= 0 && (_pic.MbFlags[_mbB] & MbFlag.Transform8x8) != 0)
        {
            inc++;
        }

        return inc;
    }

    /// <summary>mb_type of an I slice, or the intra suffix in a P or B slice, 9.3.2.5.</summary>
    private int DecodeCabacIntraMbType(int offset, bool intraSlice)
    {
        int state;

        if (intraSlice)
        {
            int inc = 0;

            if (_mbA >= 0 && (_pic.MbFlags[_mbA] & MbFlag.IntraNxN) == 0)
            {
                inc++;
            }

            if (_mbB >= 0 && (_pic.MbFlags[_mbB] & MbFlag.IntraNxN) == 0)
            {
                inc++;
            }

            if (_cabac.Decode(offset + inc) == 0)
            {
                return 0;
            }

            state = offset + 2;
        }
        else
        {
            if (_cabac.Decode(offset) == 0)
            {
                return 0;
            }

            state = offset;
        }

        if (_cabac.DecodeTerminate() == 1)
        {
            return 25;
        }

        int mbType = 1;
        mbType += 12 * _cabac.Decode(state + 1);

        if (_cabac.Decode(state + 2) == 1)
        {
            mbType += 4 + 4 * _cabac.Decode(state + 2 + (intraSlice ? 1 : 0));
        }

        mbType += 2 * _cabac.Decode(state + 3 + (intraSlice ? 1 : 0));
        mbType += _cabac.Decode(state + 3 + (intraSlice ? 2 : 0));
        return mbType;
    }

    private void ParseCabacIntraModes()
    {
        Macroblock mb = _mb;

        if (mb.Transform8x8)
        {
            for (int b8 = 0; b8 < 4; b8++)
            {
                int prev = _cabac.Decode(CtxPrevIntraMode);
                int rem = -1;

                if (prev == 0)
                {
                    rem = _cabac.Decode(CtxRemIntraMode);
                    rem |= _cabac.Decode(CtxRemIntraMode) << 1;
                    rem |= _cabac.Decode(CtxRemIntraMode) << 2;
                }

                SetIntra8x8Mode(b8, rem);
            }
        }
        else
        {
            for (int blk = 0; blk < 16; blk++)
            {
                int prev = _cabac.Decode(CtxPrevIntraMode);
                int rem = -1;

                if (prev == 0)
                {
                    rem = _cabac.Decode(CtxRemIntraMode);
                    rem |= _cabac.Decode(CtxRemIntraMode) << 1;
                    rem |= _cabac.Decode(CtxRemIntraMode) << 2;
                }

                SetIntra4x4Mode(blk, rem);
            }
        }
    }

    private int DecodeCabacIntraChromaPredMode()
    {
        int inc = 0;

        if (_mbA >= 0 && (_pic.MbFlags[_mbA] & MbFlag.Intra) != 0 && (_pic.MbFlags[_mbA] & MbFlag.Pcm) == 0
            && _pic.IntraChromaPredMode[_mbA] != 0)
        {
            inc++;
        }

        if (_mbB >= 0 && (_pic.MbFlags[_mbB] & MbFlag.Intra) != 0 && (_pic.MbFlags[_mbB] & MbFlag.Pcm) == 0
            && _pic.IntraChromaPredMode[_mbB] != 0)
        {
            inc++;
        }

        if (_cabac.Decode(CtxIntraChroma + inc) == 0)
        {
            return 0;
        }

        if (_cabac.Decode(CtxIntraChroma + 3) == 0)
        {
            return 1;
        }

        return 2 + _cabac.Decode(CtxIntraChroma + 3);
    }

    private void ParseCabacInterPrediction()
    {
        Macroblock mb = _mb;

        // B_Direct_16x16 is four direct 8x8 blocks with no sub_mb_type of their own.
        if (mb.NumParts == 4 && !mb.Direct16x16)
        {
            for (int i = 0; i < 4; i++)
            {
                if (_h.IsB)
                {
                    SetBSubMbType(i, DecodeCabacSubMbTypeB());
                }
                else
                {
                    SetPSubMbType(i, DecodeCabacSubMbTypeP());
                }
            }
        }

        // ref_idx_l0 for every partition, then l1, then mvd_l0, then mvd_l1.
        for (int list = 0; list < 2; list++)
        {
            int flag = 1 << list;
            int active = list == 0 ? _h.NumRefIdxL0Active : _h.NumRefIdxL1Active;
            int[] refs = list == 0 ? mb.RefIdx0 : mb.RefIdx1;

            for (int part = 0; part < mb.NumParts; part++)
            {
                int b8 = First8x8OfPartition(part);

                if (mb.SubDirect[b8] || (mb.PredFlags[b8] & flag) == 0)
                {
                    continue;
                }

                int refIdx = active > 1 ? DecodeCabacRefIdx(list, b8) : 0;

                if (refIdx >= active)
                {
                    throw new FormatParseException("H.264: ref_idx beyond the active list.");
                }

                foreach (int block in PartitionBlocks8x8(part))
                {
                    refs[block] = refIdx;
                }
            }
        }

        for (int list = 0; list < 2; list++)
        {
            int flag = 1 << list;
            int[] mvd = list == 0 ? mb.Mvd0 : mb.Mvd1;

            for (int part = 0; part < mb.NumParts; part++)
            {
                int b8 = First8x8OfPartition(part);

                if (mb.SubDirect[b8] || (mb.PredFlags[b8] & flag) == 0)
                {
                    continue;
                }

                if (mb.NumParts == 4)
                {
                    int subType = mb.SubMbType[b8];
                    (int subParts, int subW, int subH) = SubPartitionGeometry(subType);
                    int bx8 = (b8 & 1) * 2;
                    int by8 = (b8 >> 1) * 2;

                    for (int sub = 0; sub < subParts; sub++)
                    {
                        (int sx, int sy) = SubPartitionOrigin(bx8, by8, subW, subH, sub);
                        int x = DecodeCabacMvd(list, 0, sx, sy);
                        int y = DecodeCabacMvd(list, 1, sx, sy);
                        FillMvd(mvd, sx, sy, subW, subH, Picture.PackMv(x, y));
                    }
                }
                else
                {
                    (int px, int py, int pw, int ph) = PartitionGeometry(part);
                    int x = DecodeCabacMvd(list, 0, px, py);
                    int y = DecodeCabacMvd(list, 1, px, py);
                    FillMvd(mvd, px, py, pw, ph, Picture.PackMv(x, y));
                }
            }
        }
    }

    private static void FillMvd(int[] mvd, int bx, int by, int w, int h, int value)
    {
        for (int y = by; y < by + h; y++)
        {
            for (int x = bx; x < bx + w; x++)
            {
                mvd[y * 4 + x] = value;
            }
        }
    }

    private int DecodeCabacSubMbTypeP()
    {
        if (_cabac.Decode(CtxSubMbTypeP) == 1)
        {
            return 0;
        }

        if (_cabac.Decode(CtxSubMbTypeP + 1) == 0)
        {
            return 1;
        }

        return _cabac.Decode(CtxSubMbTypeP + 2) == 1 ? 2 : 3;
    }

    private int DecodeCabacSubMbTypeB()
    {
        if (_cabac.Decode(CtxSubMbTypeB) == 0)
        {
            return 0;
        }

        if (_cabac.Decode(CtxSubMbTypeB + 1) == 0)
        {
            return 1 + _cabac.Decode(CtxSubMbTypeB + 3);
        }

        int type = 3;

        if (_cabac.Decode(CtxSubMbTypeB + 2) == 1)
        {
            if (_cabac.Decode(CtxSubMbTypeB + 3) == 1)
            {
                return 11 + _cabac.Decode(CtxSubMbTypeB + 3);
            }

            type += 4;
        }

        type += 2 * _cabac.Decode(CtxSubMbTypeB + 3);
        type += _cabac.Decode(CtxSubMbTypeB + 3);
        return type;
    }

    /// <summary>ref_idx_lX for the partition whose first 8x8 block is b8, 9.3.3.1.1.6.</summary>
    private int DecodeCabacRefIdx(int list, int b8)
    {
        int bx = (b8 & 1) * 2;
        int by = (b8 >> 1) * 2;
        int inc = RefIdxCond(list, LeftBlock(bx, by, out int addrA), addrA)
                + 2 * RefIdxCond(list, TopBlock(bx, by, out int addrB), addrB);

        if (_cabac.Decode(CtxRefIdx + inc) == 0)
        {
            return 0;
        }

        int refIdx = 1;
        int ctx = CtxRefIdx + 4;

        while (_cabac.Decode(ctx) == 1)
        {
            refIdx++;
            ctx = CtxRefIdx + 5;

            if (refIdx > 32)
            {
                throw new FormatParseException("H.264: ref_idx does not end.");
            }
        }

        return refIdx;
    }

    private int RefIdxCond(int list, int block, int addr)
    {
        if (addr < 0)
        {
            return 0;
        }

        int b8 = (block >> 3) * 2 + ((block & 3) >> 1);

        if (addr == _mb.Addr)
        {
            Macroblock mb = _mb;

            if (mb.SubDirect[b8] || (mb.PredFlags[b8] & (1 << list)) == 0)
            {
                return 0;
            }

            return (list == 0 ? mb.RefIdx0[b8] : mb.RefIdx1[b8]) > 0 ? 1 : 0;
        }

        byte flags = _pic.MbFlags[addr];

        if ((flags & (MbFlag.Intra | MbFlag.Skip)) != 0 || (_pic.Direct8x8[addr] & (1 << b8)) != 0)
        {
            return 0;
        }

        sbyte refIdx = list == 0 ? _pic.Ref0[addr * 16 + block] : _pic.Ref1[addr * 16 + block];
        return refIdx > 0 ? 1 : 0;
    }

    /// <summary>mvd_lX[comp] for the partition starting at 4x4 block (bx, by), 9.3.3.1.1.7.</summary>
    private int DecodeCabacMvd(int list, int comp, int bx, int by)
    {
        int sum = AbsMvd(list, comp, LeftBlock(bx, by, out int addrA), addrA)
                + AbsMvd(list, comp, TopBlock(bx, by, out int addrB), addrB);
        int inc = sum < 3 ? 0 : sum > 32 ? 2 : 1;
        int offset = comp == 0 ? CtxMvdX : CtxMvdY;

        if (_cabac.Decode(offset + inc) == 0)
        {
            return 0;
        }

        int ctx = offset + 3;
        int value = 1;

        while (value < 9 && _cabac.Decode(ctx) == 1)
        {
            if (value < 4)
            {
                ctx++;
            }

            value++;
        }

        if (value >= 9)
        {
            int k = 3;

            while (_cabac.DecodeBypass() == 1)
            {
                value += 1 << k;
                k++;

                if (k > 24)
                {
                    throw new FormatParseException("H.264: an impossible motion vector difference.");
                }
            }

            while (k-- > 0)
            {
                value += _cabac.DecodeBypass() << k;
            }
        }

        return _cabac.DecodeBypass() == 1 ? -value : value;
    }

    private int AbsMvd(int list, int comp, int block, int addr)
    {
        if (addr < 0)
        {
            return 0;
        }

        int packed;

        if (addr == _mb.Addr)
        {
            packed = list == 0 ? _mb.Mvd0[block] : _mb.Mvd1[block];
        }
        else
        {
            packed = list == 0 ? _pic.Mvd0[addr * 16 + block] : _pic.Mvd1[addr * 16 + block];
        }

        return Math.Abs(comp == 0 ? Picture.MvX(packed) : Picture.MvY(packed));
    }

    /// <summary>coded_block_pattern, 9.3.3.1.1.4.</summary>
    private int DecodeCabacCbp()
    {
        int luma = 0;

        for (int b8 = 0; b8 < 4; b8++)
        {
            int bx = b8 & 1;
            int by = b8 >> 1;
            int condA;
            int condB;

            if (bx > 0)
            {
                condA = ((luma >> (b8 - 1)) & 1) == 0 ? 1 : 0;
            }
            else
            {
                condA = _mbA >= 0 && ((_pic.Cbp[_mbA] >> (b8 + 1)) & 1) == 0 ? 1 : 0;
            }

            if (by > 0)
            {
                condB = ((luma >> (b8 - 2)) & 1) == 0 ? 1 : 0;
            }
            else
            {
                condB = _mbB >= 0 && ((_pic.Cbp[_mbB] >> (b8 + 2)) & 1) == 0 ? 1 : 0;
            }

            luma |= _cabac.Decode(CtxCbpLuma + condA + 2 * condB) << b8;
        }

        int chroma = 0;

        if (_chromaFormat is 1 or 2)
        {
            int condA = _mbA >= 0 && (_pic.MbFlags[_mbA] & MbFlag.Skip) == 0 && (_pic.Cbp[_mbA] >> 4) != 0 ? 1 : 0;
            int condB = _mbB >= 0 && (_pic.MbFlags[_mbB] & MbFlag.Skip) == 0 && (_pic.Cbp[_mbB] >> 4) != 0 ? 1 : 0;

            if (_cabac.Decode(CtxCbpChroma + condA + 2 * condB) == 1)
            {
                condA = _mbA >= 0 && (_pic.MbFlags[_mbA] & MbFlag.Skip) == 0 && (_pic.Cbp[_mbA] >> 4) == 2 ? 1 : 0;
                condB = _mbB >= 0 && (_pic.MbFlags[_mbB] & MbFlag.Skip) == 0 && (_pic.Cbp[_mbB] >> 4) == 2 ? 1 : 0;
                chroma = 1 + _cabac.Decode(CtxCbpChroma + 4 + condA + 2 * condB);
            }
        }

        return luma | (chroma << 4);
    }

    /// <summary>mb_qp_delta, 9.3.2.7 and 9.3.3.1.1.5.</summary>
    private int DecodeCabacQpDelta()
    {
        if (_cabac.Decode(CtxQpDelta + (_lastQpDeltaNonZero ? 1 : 0)) == 0)
        {
            return 0;
        }

        int k = 1;
        int ctx = CtxQpDelta + 2;

        while (_cabac.Decode(ctx) == 1)
        {
            k++;
            ctx = CtxQpDelta + 3;

            if (k > 104)
            {
                throw new FormatParseException("H.264: mb_qp_delta does not end.");
            }
        }

        return (k & 1) != 0 ? (k + 1) >> 1 : -(k >> 1);
    }

    // ---- residual ---------------------------------------------------------------------------

    private void ParseCabacResidual()
    {
        Macroblock mb = _mb;
        ParseCabacResidualLuma(mb.Luma, 0, 0);

        if (_chromaFormat is 1 or 2)
        {
            int numDc = _chromaFormat == 1 ? 4 : 8;
            byte[] dcScan = _chromaFormat == 1 ? Identity : ChromaDcScan422;

            if ((mb.CbpChroma & 3) != 0)
            {
                for (int c = 0; c < 2; c++)
                {
                    Residual r = c == 0 ? mb.Cb : mb.Cr;
                    int count = ReadCabacBlock(3, CbfIncChromaDc(c), r.Dc, 0, dcScan, 0, numDc, out bool cbf);

                    if (cbf)
                    {
                        r.Cbf |= 1 << 16;
                        r.HasDc = count > 0;
                    }
                }
            }

            if ((mb.CbpChroma & 2) != 0)
            {
                int wide = ChromaBlocksWide;
                int high = ChromaBlocksHigh;

                for (int c = 0; c < 2; c++)
                {
                    Residual r = c == 0 ? mb.Cb : mb.Cr;

                    for (int blk = 0; blk < wide * high; blk++)
                    {
                        int bx = blk % wide;
                        int by = blk / wide;
                        int count = ReadCabacBlock(
                            4, CbfIncChromaAc(c, bx, by), r.Coeff4x4, blk * 16, Tables.Zigzag4x4, 1, 15, out bool cbf);

                        if (cbf)
                        {
                            r.Cbf |= 1 << blk;
                        }

                        r.Nnz[blk] = (byte)count;

                        if (count > 0)
                        {
                            r.NonZero4x4 |= 1 << blk;
                        }
                    }
                }
            }
        }
        else if (_chromaFormat == 3)
        {
            ParseCabacResidualLuma(mb.Cb, 1, 6);
            ParseCabacResidualLuma(mb.Cr, 2, 10);
        }
    }

    /// <summary>residual_luma(), also used for Cb and Cr in 4:4:4 with their own categories.</summary>
    private void ParseCabacResidualLuma(Residual r, int component, int catBase)
    {
        Macroblock mb = _mb;
        int catDc = catBase;                          // 0, 6, 10
        int catAc = catBase + 1;                      // 1, 7, 11
        int cat4x4 = catBase + 2;                     // 2, 8, 12
        int cat8x8 = catBase == 0 ? 5 : catBase + 3;  // 5, 9, 13

        if (mb.Intra16x16)
        {
            int count = ReadCabacBlock(catDc, CbfIncDc(component), r.Dc, 0, Tables.Zigzag4x4, 0, 16, out bool cbf);

            if (cbf)
            {
                r.Cbf |= 1 << 16;
                r.HasDc = count > 0;
            }
        }

        for (int b8 = 0; b8 < 4; b8++)
        {
            if ((mb.CbpLuma & (1 << b8)) == 0)
            {
                continue;
            }

            if (mb.Transform8x8)
            {
                int bx = (b8 & 1) * 2;
                int by = (b8 >> 1) * 2;
                int count = ReadCabacBlock(
                    cat8x8, CbfInc8x8(component, bx, by), r.Coeff8x8, b8 * 64, Tables.Zigzag8x8, 0, 64, out bool cbf);

                if (cbf)
                {
                    // Every 4x4 inside carries the 8x8's flag, so neighbours find it by
                    // position; for 4:2:0 the flag is inferred and cbf is always true here.
                    foreach (int blk in Blocks8x8[b8])
                    {
                        r.Cbf |= 1 << blk;
                    }
                }

                if (count > 0)
                {
                    r.NonZero8x8 |= 1 << b8;

                    foreach (int blk in Blocks8x8[b8])
                    {
                        r.Nnz[blk] = (byte)count;
                    }
                }
            }
            else
            {
                foreach (int blk in Blocks8x8[b8])
                {
                    int bx = blk & 3;
                    int by = blk >> 2;
                    int count;
                    bool cbf;

                    if (mb.Intra16x16)
                    {
                        count = ReadCabacBlock(
                            catAc, CbfInc4x4(component, bx, by), r.Coeff4x4, blk * 16, Tables.Zigzag4x4, 1, 15, out cbf);
                    }
                    else
                    {
                        count = ReadCabacBlock(
                            cat4x4, CbfInc4x4(component, bx, by), r.Coeff4x4, blk * 16, Tables.Zigzag4x4, 0, 16, out cbf);
                    }

                    if (cbf)
                    {
                        r.Cbf |= 1 << blk;
                    }

                    r.Nnz[blk] = (byte)count;

                    if (count > 0)
                    {
                        r.NonZero4x4 |= 1 << blk;
                    }
                }
            }
        }
    }

    private int CbfBits(int addr, int component)
    {
        if (addr == _mb.Addr)
        {
            return component == 0 ? _mb.Luma.Cbf : component == 1 ? _mb.Cb.Cbf : _mb.Cr.Cbf;
        }

        return component == 0 ? _pic.CbfLuma[addr] : component == 1 ? _pic.CbfCb[addr] : _pic.CbfCr[addr];
    }

    /// <summary>The unavailable-neighbour value of condTermFlagN: 1 for intra, 0 for inter.</summary>
    private int CbfUnavailable() => _mb.Intra ? 1 : 0;

    private int CbfIncDc(int component)
    {
        int condA = _mbA < 0 ? CbfUnavailable() : (CbfBits(_mbA, component) >> 16) & 1;
        int condB = _mbB < 0 ? CbfUnavailable() : (CbfBits(_mbB, component) >> 16) & 1;
        return condA + 2 * condB;
    }

    private int CbfInc4x4(int component, int bx, int by)
    {
        int blkA = LeftBlock(bx, by, out int addrA);
        int blkB = TopBlock(bx, by, out int addrB);
        int condA = addrA < 0 ? CbfUnavailable() : (CbfBits(addrA, component) >> blkA) & 1;
        int condB = addrB < 0 ? CbfUnavailable() : (CbfBits(addrB, component) >> blkB) & 1;
        return condA + 2 * condB;
    }

    private int CbfInc8x8(int component, int bx, int by)
    {
        int blkA = LeftBlock(bx, by, out int addrA);
        int blkB = TopBlock(bx, by, out int addrB);
        return Cbf8x8Cond(addrA, component, blkA) + 2 * Cbf8x8Cond(addrB, component, blkB);
    }

    private int Cbf8x8Cond(int addr, int component, int block)
    {
        if (addr < 0)
        {
            return CbfUnavailable();
        }

        if (addr == _mb.Addr)
        {
            return (CbfBits(addr, component) >> block) & 1;
        }

        byte flags = _pic.MbFlags[addr];

        if ((flags & MbFlag.Pcm) != 0)
        {
            return 1;
        }

        if ((flags & MbFlag.Transform8x8) == 0)
        {
            return 0;
        }

        return (CbfBits(addr, component) >> block) & 1;
    }

    private int CbfIncChromaDc(int c)
    {
        int condA = _mbA < 0 ? CbfUnavailable() : (CbfBits(_mbA, 1 + c) >> 16) & 1;
        int condB = _mbB < 0 ? CbfUnavailable() : (CbfBits(_mbB, 1 + c) >> 16) & 1;
        return condA + 2 * condB;
    }

    private int CbfIncChromaAc(int c, int bx, int by)
    {
        int blkA = LeftChromaBlock(bx, by, out int addrA);
        int blkB = TopChromaBlock(bx, by, out int addrB);
        int condA = addrA < 0 ? CbfUnavailable() : (CbfBits(addrA, 1 + c) >> blkA) & 1;
        int condB = addrB < 0 ? CbfUnavailable() : (CbfBits(addrB, 1 + c) >> blkB) & 1;
        return condA + 2 * condB;
    }

    /// <summary>residual_block_cabac(), 7.3.5.3.3 with the contexts of 9.3.3.1.3.</summary>
    /// <returns>How many coefficients are not zero.</returns>
    private int ReadCabacBlock(
        int cat, int cbfInc, int[] dest, int destOffset, byte[] scan, int first, int maxCoeff, out bool cbf)
    {
        // The 8x8 luma flag is inferred unless chroma is coded like luma.
        if (maxCoeff != 64 || _chromaFormat == 3)
        {
            if (_cabac.Decode(CbfOffset[cat] + cbfInc) == 0)
            {
                cbf = false;
                return 0;
            }
        }

        cbf = true;
        int sigOffset = SigOffset[cat];
        int lastOffset = LastOffset[cat];
        int[] positions = _sigPositions;
        int count = 0;
        bool is8x8 = maxCoeff == 64;
        bool isChromaDc = cat == 3;
        int numC8x8 = isChromaDc ? 4 / (_sps.SubWidthC * _sps.SubHeightC) : 1;
        int i = 0;

        for (; i < maxCoeff - 1; i++)
        {
            int inc = is8x8 ? Tables.SigCoeff8x8[i] : isChromaDc ? Math.Min(i / numC8x8, 2) : i;

            if (_cabac.Decode(sigOffset + inc) == 1)
            {
                positions[count++] = i;
                int lastInc = is8x8 ? Tables.LastCoeff8x8[i] : isChromaDc ? Math.Min(i / numC8x8, 2) : i;

                if (_cabac.Decode(lastOffset + lastInc) == 1)
                {
                    break;
                }
            }
        }

        if (i == maxCoeff - 1)
        {
            positions[count++] = i;
        }

        int absOffset = AbsOffset[cat];
        int numGt1 = 0;
        int numEq1 = 0;
        int gt1Ctx = isChromaDc ? 3 : 4;

        for (int k = count - 1; k >= 0; k--)
        {
            int inc = numGt1 != 0 ? 0 : Math.Min(4, 1 + numEq1);
            int level;

            if (_cabac.Decode(absOffset + inc) == 0)
            {
                level = 1;
                numEq1++;
            }
            else
            {
                int incG = 5 + Math.Min(gt1Ctx, numGt1);
                int n = 1;

                while (n < 14 && _cabac.Decode(absOffset + incG) == 1)
                {
                    n++;
                }

                if (n == 14)
                {
                    int exp = 0;

                    while (_cabac.DecodeBypass() == 1)
                    {
                        n += 1 << exp;
                        exp++;

                        if (exp > 24)
                        {
                            throw new FormatParseException("H.264: an impossible coefficient.");
                        }
                    }

                    while (exp-- > 0)
                    {
                        n += _cabac.DecodeBypass() << exp;
                    }
                }

                level = n + 1;
                numGt1++;
            }

            if (_cabac.DecodeBypass() == 1)
            {
                level = -level;
            }

            dest[destOffset + scan[positions[k] + first]] = level;
        }

        return count;
    }

    private void ReadPcm(int start, int count)
    {
        byte[] rbsp = _cabacRbsp;

        if (start + count > _cabacLength)
        {
            throw new FormatParseException("H.264: I_PCM samples run past the end of the slice.");
        }

        Array.Copy(rbsp, start, _mb.PcmSamples, 0, count);
    }
}
