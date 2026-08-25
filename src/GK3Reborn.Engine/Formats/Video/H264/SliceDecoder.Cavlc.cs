namespace GK3Reborn.Formats.Video.H264;

/// <summary>A variable-length table turned into a lookup on the next sixteen bits.</summary>
internal sealed class VlcTable
{
    private readonly ushort[] _lengths = new ushort[1 << 16];
    private readonly ushort[] _values = new ushort[1 << 16];

    public VlcTable(VlcCode[] codes)
    {
        foreach (VlcCode code in codes)
        {
            int shift = 16 - code.Length;
            int first = code.Code << shift;
            int count = 1 << shift;

            for (int i = 0; i < count; i++)
            {
                _lengths[first + i] = (ushort)code.Length;
                _values[first + i] = (ushort)code.Value;
            }
        }
    }

    /// <summary>Reads one code, or throws when the bits match nothing.</summary>
    public int Read(ref BitReader reader)
    {
        int key = (int)reader.PeekBits(16);
        int length = _lengths[key];

        if (length == 0)
        {
            throw new FormatParseException("H.264: a CAVLC code that is in no table.");
        }

        reader.Skip(length);
        return _values[key];
    }
}

/// <summary>
/// The CAVLC half of macroblock parsing: 7.3.5 read with Exp-Golomb codes and the
/// coefficient coding of 9.2.
/// </summary>
internal sealed partial class SliceDecoder
{
    private static readonly Lazy<VlcTable[]> CoeffTokenTables = new(() =>
    [
        new VlcTable(Tables.CoeffToken0),
        new VlcTable(Tables.CoeffToken2),
        new VlcTable(Tables.CoeffToken4),
        new VlcTable(Tables.CoeffToken8),
        new VlcTable(Tables.CoeffTokenChromaDc420),
        new VlcTable(Tables.CoeffTokenChromaDc422),
    ]);

    private static readonly Lazy<VlcTable[]> TotalZeros16Tables = new(() => Build(Tables.TotalZeros16));
    private static readonly Lazy<VlcTable[]> TotalZeros4Tables = new(() => Build(Tables.TotalZeros4));
    private static readonly Lazy<VlcTable[]> TotalZeros8Tables = new(() => Build(Tables.TotalZeros8));
    private static readonly Lazy<VlcTable[]> RunBeforeTables = new(() => Build(Tables.RunBefore));

    private readonly int[] _levels = new int[16];
    private readonly int[] _runs = new int[16];
    private readonly int[] _interleaved = new int[16];

    private static VlcTable[] Build(VlcCode[][] tables)
    {
        var built = new VlcTable[tables.Length];

        for (int i = 0; i < tables.Length; i++)
        {
            built[i] = new VlcTable(tables[i]);
        }

        return built;
    }

    private void ParseCavlcMacroblock()
    {
        Macroblock mb = _mb;
        int type = _reader.ReadUe();

        if (_h.IsIntra)
        {
            SetIMbType(type);
        }
        else if (_h.IsP)
        {
            if (type < 5)
            {
                SetPMbType(type);
            }
            else
            {
                SetIMbType(type - 5);
            }
        }
        else
        {
            if (type < 23)
            {
                SetBMbType(type);
            }
            else
            {
                SetIMbType(type - 23);
            }
        }

        if (mb.Pcm)
        {
            _reader.AlignToByte();
            int count = 256 + 2 * (_sps.ChromaWidthMb * _sps.ChromaHeightMb);

            for (int i = 0; i < count; i++)
            {
                mb.PcmSamples[i] = (byte)_reader.ReadBits(8);
            }

            ApplyQp(false);
            return;
        }

        if (mb.IntraNxN)
        {
            if (_pps.Transform8x8Mode)
            {
                mb.Transform8x8 = _reader.ReadFlag();
            }

            if (mb.Transform8x8)
            {
                for (int b8 = 0; b8 < 4; b8++)
                {
                    int rem = _reader.ReadFlag() ? -1 : (int)_reader.ReadBits(3);
                    SetIntra8x8Mode(b8, rem);
                }
            }
            else
            {
                for (int blk = 0; blk < 16; blk++)
                {
                    int rem = _reader.ReadFlag() ? -1 : (int)_reader.ReadBits(3);
                    SetIntra4x4Mode(blk, rem);
                }
            }
        }

        if (mb.Intra)
        {
            if (_chromaFormat is 1 or 2)
            {
                mb.IntraChromaPredMode = _reader.ReadUe();

                if (mb.IntraChromaPredMode > 3)
                {
                    throw new FormatParseException("H.264: intra_chroma_pred_mode out of range.");
                }
            }
        }
        else
        {
            ParseCavlcInterPrediction(type == 4 && _h.IsP);
        }

        if (!mb.Intra16x16)
        {
            int codeNum = _reader.ReadUe();
            bool colour = _chromaFormat is 1 or 2;
            byte[] table = mb.Intra
                ? (colour ? Tables.CbpIntraColour : Tables.CbpIntraMono)
                : (colour ? Tables.CbpInterColour : Tables.CbpInterMono);

            if (codeNum >= table.Length)
            {
                throw new FormatParseException("H.264: coded_block_pattern out of range.");
            }

            mb.Cbp = table[codeNum];

            if (mb.CbpLuma > 0 && _pps.Transform8x8Mode && !mb.IntraNxN && Transform8x8Allowed())
            {
                mb.Transform8x8 = _reader.ReadFlag();
            }
        }

        if (mb.Cbp != 0 || mb.Intra16x16)
        {
            mb.QpDelta = _reader.ReadSe();
            ApplyQp(true);
            ParseCavlcResidual();
        }
        else
        {
            ApplyQp(false);
        }
    }

    private void ParseCavlcInterPrediction(bool refsAreZero)
    {
        Macroblock mb = _mb;

        // B_Direct_16x16 is four direct 8x8 blocks with no sub_mb_type of their own.
        if (mb.NumParts == 4 && !mb.Direct16x16)
        {
            for (int i = 0; i < 4; i++)
            {
                int sub = _reader.ReadUe();

                if (_h.IsB)
                {
                    SetBSubMbType(i, sub);
                }
                else
                {
                    SetPSubMbType(i, sub);
                }
            }
        }

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

                int refIdx = active > 1 && !refsAreZero ? _reader.ReadTe(active - 1) : 0;

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
                    (int subParts, int subW, int subH) = SubPartitionGeometry(mb.SubMbType[b8]);
                    int bx8 = (b8 & 1) * 2;
                    int by8 = (b8 >> 1) * 2;

                    for (int sub = 0; sub < subParts; sub++)
                    {
                        (int sx, int sy) = SubPartitionOrigin(bx8, by8, subW, subH, sub);
                        int x = _reader.ReadSe();
                        int y = _reader.ReadSe();
                        FillMvd(mvd, sx, sy, subW, subH, Picture.PackMv(x, y));
                    }
                }
                else
                {
                    (int px, int py, int pw, int ph) = PartitionGeometry(part);
                    int x = _reader.ReadSe();
                    int y = _reader.ReadSe();
                    FillMvd(mvd, px, py, pw, ph, Picture.PackMv(x, y));
                }
            }
        }
    }

    // ---- residual ---------------------------------------------------------------------------

    private void ParseCavlcResidual()
    {
        Macroblock mb = _mb;
        ParseCavlcResidualLuma(mb.Luma, 0);

        if (_chromaFormat is 1 or 2)
        {
            int numDc = _chromaFormat == 1 ? 4 : 8;
            byte[] dcScan = _chromaFormat == 1 ? Identity : ChromaDcScan422;

            if ((mb.CbpChroma & 3) != 0)
            {
                for (int c = 0; c < 2; c++)
                {
                    Residual r = c == 0 ? mb.Cb : mb.Cr;
                    int count = ReadCavlcBlock(_chromaFormat == 1 ? -1 : -2, r.Dc, 0, dcScan, 0, numDc);
                    r.HasDc = count > 0;
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
                        int nC = ChromaNc(c, bx, by);
                        int count = ReadCavlcBlock(nC, r.Coeff4x4, blk * 16, Tables.Zigzag4x4, 1, 15);
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
            ParseCavlcResidualLuma(mb.Cb, 1);
            ParseCavlcResidualLuma(mb.Cr, 2);
        }
    }

    private void ParseCavlcResidualLuma(Residual r, int component)
    {
        Macroblock mb = _mb;

        if (mb.Intra16x16)
        {
            int count = ReadCavlcBlock(LumaNc(component, 0, 0), r.Dc, 0, Tables.Zigzag4x4, 0, 16);
            r.HasDc = count > 0;
        }

        for (int b8 = 0; b8 < 4; b8++)
        {
            if ((mb.CbpLuma & (1 << b8)) == 0)
            {
                continue;
            }

            if (mb.Transform8x8)
            {
                // Four 4x4 blocks, interleaved into the 8x8's scan: 8.5.7 / 7.3.5.3.2.
                int any = 0;

                for (int i4 = 0; i4 < 4; i4++)
                {
                    int blk = Blocks8x8[b8][i4];
                    int bx = blk & 3;
                    int by = blk >> 2;
                    Array.Clear(_interleaved);
                    int count = ReadCavlcBlock(LumaNc(component, bx, by), _interleaved, 0, Identity, 0, 16);
                    r.Nnz[blk] = (byte)count;
                    any += count;

                    if (count > 0)
                    {
                        for (int k = 0; k < 16; k++)
                        {
                            r.Coeff8x8[b8 * 64 + Tables.Zigzag8x8[4 * k + i4]] = _interleaved[k];
                        }
                    }
                }

                if (any > 0)
                {
                    r.NonZero8x8 |= 1 << b8;
                }
            }
            else
            {
                foreach (int blk in Blocks8x8[b8])
                {
                    int bx = blk & 3;
                    int by = blk >> 2;
                    int nC = LumaNc(component, bx, by);
                    int count = mb.Intra16x16
                        ? ReadCavlcBlock(nC, r.Coeff4x4, blk * 16, Tables.Zigzag4x4, 1, 15)
                        : ReadCavlcBlock(nC, r.Coeff4x4, blk * 16, Tables.Zigzag4x4, 0, 16);
                    r.Nnz[blk] = (byte)count;

                    if (count > 0)
                    {
                        r.NonZero4x4 |= 1 << blk;
                    }
                }
            }
        }
    }

    /// <summary>nC for a luma-like 4x4 block, 9.2.1.</summary>
    private int LumaNc(int component, int bx, int by)
    {
        int blkA = LeftBlock(bx, by, out int addrA);
        int blkB = TopBlock(bx, by, out int addrB);
        return CombineNc(addrA, blkA, addrB, blkB, component);
    }

    private int ChromaNc(int c, int bx, int by)
    {
        int blkA = LeftChromaBlock(bx, by, out int addrA);
        int blkB = TopChromaBlock(bx, by, out int addrB);
        return CombineNc(addrA, blkA, addrB, blkB, 1 + c);
    }

    private int CombineNc(int addrA, int blkA, int addrB, int blkB, int component)
    {
        bool availableA = addrA >= 0;
        bool availableB = addrB >= 0;

        if (availableA && availableB)
        {
            return (NnzOf(addrA, component, blkA) + NnzOf(addrB, component, blkB) + 1) >> 1;
        }

        if (availableA)
        {
            return NnzOf(addrA, component, blkA);
        }

        if (availableB)
        {
            return NnzOf(addrB, component, blkB);
        }

        return 0;
    }

    /// <summary>residual_block_cavlc(), 7.3.5.3.2 and 9.2.</summary>
    /// <returns>TotalCoeff.</returns>
    private int ReadCavlcBlock(int nC, int[] dest, int destOffset, byte[] scan, int first, int maxCoeff)
    {
        VlcTable tokens = nC switch
        {
            -1 => CoeffTokenTables.Value[4],
            -2 => CoeffTokenTables.Value[5],
            < 2 => CoeffTokenTables.Value[0],
            < 4 => CoeffTokenTables.Value[1],
            < 8 => CoeffTokenTables.Value[2],
            _ => CoeffTokenTables.Value[3],
        };

        int token = tokens.Read(ref _reader);
        int totalCoeff = token >> 2;
        int trailingOnes = token & 3;

        if (totalCoeff == 0)
        {
            return 0;
        }

        if (totalCoeff > maxCoeff)
        {
            throw new FormatParseException("H.264: more coefficients than the block holds.");
        }

        int[] levels = _levels;
        int suffixLength = totalCoeff > 10 && trailingOnes < 3 ? 1 : 0;

        for (int i = 0; i < totalCoeff; i++)
        {
            if (i < trailingOnes)
            {
                levels[i] = _reader.ReadBit() == 1 ? -1 : 1;
                continue;
            }

            int prefix = 0;

            while (_reader.ReadBit() == 0)
            {
                prefix++;

                if (prefix > 32)
                {
                    throw new FormatParseException("H.264: level_prefix does not end.");
                }
            }

            int levelCode = Math.Min(15, prefix) << suffixLength;

            if (suffixLength > 0 || prefix >= 14)
            {
                int suffixSize = prefix == 14 && suffixLength == 0 ? 4 : prefix >= 15 ? prefix - 3 : suffixLength;

                if (suffixSize > 0)
                {
                    levelCode += (int)_reader.ReadBits(suffixSize);
                }
            }

            if (prefix >= 15 && suffixLength == 0)
            {
                levelCode += 15;
            }

            if (prefix >= 16)
            {
                levelCode += (1 << (prefix - 3)) - 4096;
            }

            if (i == trailingOnes && trailingOnes < 3)
            {
                levelCode += 2;
            }

            levels[i] = (levelCode & 1) == 0 ? (levelCode + 2) >> 1 : (-levelCode - 1) >> 1;

            if (suffixLength == 0)
            {
                suffixLength = 1;
            }

            if (Math.Abs(levels[i]) > (3 << (suffixLength - 1)) && suffixLength < 6)
            {
                suffixLength++;
            }
        }

        int totalZeros = 0;

        if (totalCoeff < maxCoeff)
        {
            VlcTable[] tables = maxCoeff == 4 ? TotalZeros4Tables.Value
                : maxCoeff == 8 ? TotalZeros8Tables.Value
                : TotalZeros16Tables.Value;
            totalZeros = tables[totalCoeff - 1].Read(ref _reader);
        }

        int[] runs = _runs;
        int zerosLeft = totalZeros;

        for (int i = 0; i < totalCoeff - 1; i++)
        {
            if (zerosLeft > 0)
            {
                runs[i] = RunBeforeTables.Value[Math.Min(zerosLeft, 7) - 1].Read(ref _reader);
                zerosLeft -= runs[i];
            }
            else
            {
                runs[i] = 0;
            }
        }

        runs[totalCoeff - 1] = zerosLeft;

        int coeffNum = -1;

        for (int i = totalCoeff - 1; i >= 0; i--)
        {
            coeffNum += runs[i] + 1;

            if (coeffNum + first >= maxCoeff + first || coeffNum >= maxCoeff)
            {
                throw new FormatParseException("H.264: a coefficient placed beyond its block.");
            }

            dest[destOffset + scan[coeffNum + first]] = levels[i];
        }

        return totalCoeff;
    }
}
