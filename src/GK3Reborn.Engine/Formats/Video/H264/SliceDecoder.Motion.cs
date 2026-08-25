namespace GK3Reborn.Formats.Video.H264;

/// <summary>
/// Motion vector prediction, 8.4.1: turning differences into vectors, and inferring the
/// vectors of skipped and direct-predicted blocks from their neighbours or from the
/// co-located block of the first list-1 reference.
/// </summary>
internal sealed partial class SliceDecoder
{
    /// <summary>Which 4x4 blocks of the current macroblock already have their vectors.</summary>
    private int _mvDecoded;

    /// <summary>Works out the implicit bi-prediction weights of the slice, 8.4.2.3.1.</summary>
    private void PrepareWeights()
    {
        _implicitWeights = null;
        _colocated = _h.IsB ? RefList1[0] : null;

        if (!_h.IsB || _pps.WeightedBipredIdc != 2)
        {
            return;
        }

        int n0 = _h.NumRefIdxL0Active;
        int n1 = _h.NumRefIdxL1Active;
        var weights = new int[n0, n1];

        for (int i = 0; i < n0; i++)
        {
            for (int j = 0; j < n1; j++)
            {
                Picture? pic0 = RefList0[i];
                Picture? pic1 = RefList1[j];
                int w0 = 32;

                if (pic0 is not null && pic1 is not null && !pic0.IsLongTermRef && !pic1.IsLongTermRef)
                {
                    int tb = Math.Clamp(_pic.Poc - pic0.Poc, -128, 127);
                    int td = Math.Clamp(pic1.Poc - pic0.Poc, -128, 127);

                    if (td != 0)
                    {
                        int tx = (16384 + Math.Abs(td / 2)) / td;
                        int scale = Math.Clamp((tb * tx + 32) >> 6, -1024, 1023);

                        if ((scale >> 2) >= -64 && (scale >> 2) <= 128)
                        {
                            w0 = 64 - (scale >> 2);
                        }
                    }
                }

                weights[i, j] = w0;
            }
        }

        _implicitWeights = weights;
    }

    /// <summary>A neighbouring block's vector and reference for a list, or ref -1 when it has none.</summary>
    private void NeighbourMotion(int list, int addr, int block, out int mv, out int refIdx)
    {
        if (addr < 0)
        {
            mv = 0;
            refIdx = -1;
            return;
        }

        if (addr == _mb.Addr)
        {
            if ((_mvDecoded & (1 << block)) == 0)
            {
                mv = 0;
                refIdx = -1;
                return;
            }

            refIdx = list == 0 ? _mb.Ref0[block] : _mb.Ref1[block];
            mv = list == 0 ? _mb.Mv0[block] : _mb.Mv1[block];
            return;
        }

        if ((_pic.MbFlags[addr] & MbFlag.Intra) != 0)
        {
            mv = 0;
            refIdx = -1;
            return;
        }

        int index = addr * 16 + block;
        refIdx = list == 0 ? _pic.Ref0[index] : _pic.Ref1[index];
        mv = refIdx < 0 ? 0 : list == 0 ? _pic.Mv0[index] : _pic.Mv1[index];
    }

    /// <summary>
    /// The neighbouring block for a position in 4x4 units relative to the macroblock,
    /// where x or y may be -1 and x may reach beyond the right edge (6.4.11.7).
    /// </summary>
    private int NeighbourAt(int bx, int by, out int addr)
    {
        if (by < 0)
        {
            if (bx < 0)
            {
                addr = _mbD;
                return 15;
            }

            if (bx < 4)
            {
                addr = _mbB;
                return 12 + bx;
            }

            addr = _mbC;
            return 12;
        }

        if (bx < 0)
        {
            addr = _mbA;
            return by * 4 + 3;
        }

        if (bx >= 4)
        {
            addr = -1;
            return 0;
        }

        addr = _mb.Addr;
        return by * 4 + bx;
    }

    /// <summary>Median motion vector prediction for a partition, 8.4.1.3.</summary>
    /// <param name="list">0 or 1.</param>
    /// <param name="bx">Left edge of the partition in 4x4 units.</param>
    /// <param name="by">Top edge.</param>
    /// <param name="w">Width in 4x4 units.</param>
    /// <param name="h">Height in 4x4 units.</param>
    /// <param name="refIdx">The partition's reference index in that list.</param>
    /// <param name="shapeHint">16x8 partition index + 1, or -(8x16 partition index + 1), or 0.</param>
    private int PredictMv(int list, int bx, int by, int w, int h, int refIdx, int shapeHint)
    {
        int blkA = NeighbourAt(bx - 1, by, out int addrA);
        int blkB = NeighbourAt(bx, by - 1, out int addrB);
        int blkC = NeighbourAt(bx + w, by - 1, out int addrC);
        bool cAvailable = addrC >= 0;

        if (!cAvailable)
        {
            blkC = NeighbourAt(bx - 1, by - 1, out addrC);
        }

        NeighbourMotion(list, addrA, blkA, out int mvA, out int refA);
        NeighbourMotion(list, addrB, blkB, out int mvB, out int refB);
        NeighbourMotion(list, addrC, blkC, out int mvC, out int refC);

        // Directional prediction for the two-partition shapes, 8.4.1.3.
        if (shapeHint == 1 && refB == refIdx)
        {
            return mvB;
        }

        if (shapeHint == 2 && refA == refIdx)
        {
            return mvA;
        }

        if (shapeHint == -1 && refA == refIdx)
        {
            return mvA;
        }

        if (shapeHint == -2 && refC == refIdx)
        {
            return mvC;
        }

        // 8.4.1.3.1: with only A available, B and C take its value.
        if (addrB < 0 && addrC < 0 && addrA >= 0)
        {
            mvB = mvC = mvA;
            refB = refC = refA;
        }

        int matches = (refA == refIdx ? 1 : 0) + (refB == refIdx ? 1 : 0) + (refC == refIdx ? 1 : 0);

        if (matches == 1)
        {
            return refA == refIdx ? mvA : refB == refIdx ? mvB : mvC;
        }

        int x = Median(Picture.MvX(mvA), Picture.MvX(mvB), Picture.MvX(mvC));
        int y = Median(Picture.MvY(mvA), Picture.MvY(mvB), Picture.MvY(mvC));
        return Picture.PackMv(x, y);
    }

    private static int Median(int a, int b, int c) => a + b + c - Math.Min(a, Math.Min(b, c)) - Math.Max(a, Math.Max(b, c));

    private void FillMotion(int list, int bx, int by, int w, int h, int mv, int refIdx)
    {
        int[] mvs = list == 0 ? _mb.Mv0 : _mb.Mv1;
        sbyte[] refs = list == 0 ? _mb.Ref0 : _mb.Ref1;

        for (int y = by; y < by + h; y++)
        {
            for (int x = bx; x < bx + w; x++)
            {
                mvs[y * 4 + x] = mv;
                refs[y * 4 + x] = (sbyte)refIdx;
            }
        }
    }

    private void MarkDecoded(int bx, int by, int w, int h)
    {
        for (int y = by; y < by + h; y++)
        {
            for (int x = bx; x < bx + w; x++)
            {
                _mvDecoded |= 1 << (y * 4 + x);
            }
        }
    }

    /// <summary>Derives every vector of an inter macroblock from the parsed differences.</summary>
    private void DeriveMotion()
    {
        Macroblock mb = _mb;
        _mvDecoded = 0;
        Array.Fill(mb.Ref0, (sbyte)-1);
        Array.Fill(mb.Ref1, (sbyte)-1);
        Array.Clear(mb.Mv0);
        Array.Clear(mb.Mv1);
        _pic.Direct8x8[mb.Addr] = 0;

        if (mb.NumParts != 4)
        {
            for (int part = 0; part < mb.NumParts; part++)
            {
                (int px, int py, int pw, int ph) = PartitionGeometry(part);
                int hint = mb.Shape == PartitionShape.Part16x8 ? part + 1 : mb.Shape == PartitionShape.Part8x16 ? -(part + 1) : 0;
                int b8 = First8x8OfPartition(part);

                for (int list = 0; list < 2; list++)
                {
                    if ((mb.PredFlags[b8] & (1 << list)) == 0)
                    {
                        continue;
                    }

                    int refIdx = list == 0 ? mb.RefIdx0[b8] : mb.RefIdx1[b8];
                    int mvp = PredictMv(list, px, py, pw, ph, refIdx, hint);
                    int mvd = (list == 0 ? mb.Mvd0 : mb.Mvd1)[py * 4 + px];
                    int mv = Picture.PackMv(Picture.MvX(mvp) + Picture.MvX(mvd), Picture.MvY(mvp) + Picture.MvY(mvd));
                    FillMotion(list, px, py, pw, ph, mv, refIdx);
                }

                MarkDecoded(px, py, pw, ph);
            }

            return;
        }

        for (int b8 = 0; b8 < 4; b8++)
        {
            int bx8 = (b8 & 1) * 2;
            int by8 = (b8 >> 1) * 2;

            if (mb.SubDirect[b8])
            {
                DeriveDirect(b8);
                _pic.Direct8x8[mb.Addr] |= (byte)(1 << b8);
                MarkDecoded(bx8, by8, 2, 2);
                continue;
            }

            (int subParts, int subW, int subH) = SubPartitionGeometry(mb.SubMbType[b8]);

            for (int sub = 0; sub < subParts; sub++)
            {
                (int sx, int sy) = SubPartitionOrigin(bx8, by8, subW, subH, sub);

                for (int list = 0; list < 2; list++)
                {
                    if ((mb.PredFlags[b8] & (1 << list)) == 0)
                    {
                        continue;
                    }

                    int refIdx = list == 0 ? mb.RefIdx0[b8] : mb.RefIdx1[b8];
                    int mvp = PredictMv(list, sx, sy, subW, subH, refIdx, 0);
                    int mvd = (list == 0 ? mb.Mvd0 : mb.Mvd1)[sy * 4 + sx];
                    int mv = Picture.PackMv(Picture.MvX(mvp) + Picture.MvX(mvd), Picture.MvY(mvp) + Picture.MvY(mvd));
                    FillMotion(list, sx, sy, subW, subH, mv, refIdx);
                }

                MarkDecoded(sx, sy, subW, subH);
            }
        }
    }

    /// <summary>The vectors of a skipped macroblock: P_Skip by 8.4.1.1, B_Skip by direct prediction.</summary>
    private void ReconstructSkip()
    {
        Macroblock mb = _mb;
        ApplyQp(false);
        mb.Cbp = 0;
        _mvDecoded = 0;
        Array.Fill(mb.Ref0, (sbyte)-1);
        Array.Fill(mb.Ref1, (sbyte)-1);
        Array.Clear(mb.Mv0);
        Array.Clear(mb.Mv1);
        _pic.Direct8x8[mb.Addr] = 0;

        if (_h.IsB)
        {
            mb.Shape = PartitionShape.Part8x8;
            mb.NumParts = 4;

            for (int b8 = 0; b8 < 4; b8++)
            {
                mb.SubDirect[b8] = true;
                DeriveDirect(b8);
                _pic.Direct8x8[mb.Addr] |= (byte)(1 << b8);
                MarkDecoded((b8 & 1) * 2, (b8 >> 1) * 2, 2, 2);
            }
        }
        else
        {
            mb.Shape = PartitionShape.Part16x16;
            mb.NumParts = 1;
            mb.PredFlags[0] = mb.PredFlags[1] = mb.PredFlags[2] = mb.PredFlags[3] = 1;

            int blkA = NeighbourAt(-1, 0, out int addrA);
            int blkB = NeighbourAt(0, -1, out int addrB);
            NeighbourMotion(0, addrA, blkA, out int mvA, out int refA);
            NeighbourMotion(0, addrB, blkB, out int mvB, out int refB);
            int mv;

            if (addrA < 0 || addrB < 0 || (refA == 0 && mvA == 0) || (refB == 0 && mvB == 0))
            {
                mv = 0;
            }
            else
            {
                mv = PredictMv(0, 0, 0, 4, 4, 0, 0);
            }

            FillMotion(0, 0, 0, 4, 4, mv, 0);
            MarkDecoded(0, 0, 4, 4);
        }

        MotionCompensate();
    }

    /// <summary>Direct prediction of one 8x8 block, spatial (8.4.1.2.2) or temporal (8.4.1.2.3).</summary>
    private void DeriveDirect(int b8)
    {
        if (_h.DirectSpatialMvPred)
        {
            DeriveSpatialDirect(b8);
        }
        else
        {
            DeriveTemporalDirect(b8);
        }
    }

    private int _spatialRef0 = -2;
    private int _spatialRef1;
    private int _spatialMv0;
    private int _spatialMv1;

    private void DeriveSpatialDirect(int b8)
    {
        Macroblock mb = _mb;
        int bx8 = (b8 & 1) * 2;
        int by8 = (b8 >> 1) * 2;

        // The reference indices and vectors are those of the whole macroblock and are
        // computed once, for the first direct block.
        if (_spatialRef0 == -2)
        {
            int blkA = NeighbourAt(-1, 0, out int addrA);
            int blkB = NeighbourAt(0, -1, out int addrB);
            int blkC = NeighbourAt(4, -1, out int addrC);

            if (addrC < 0)
            {
                blkC = NeighbourAt(-1, -1, out addrC);
            }

            for (int list = 0; list < 2; list++)
            {
                NeighbourMotion(list, addrA, blkA, out _, out int refA);
                NeighbourMotion(list, addrB, blkB, out _, out int refB);
                NeighbourMotion(list, addrC, blkC, out _, out int refC);
                int refIdx = MinPositive(refA, MinPositive(refB, refC));

                if (list == 0)
                {
                    _spatialRef0 = refIdx;
                }
                else
                {
                    _spatialRef1 = refIdx;
                }
            }

            if (_spatialRef0 < 0 && _spatialRef1 < 0)
            {
                _spatialRef0 = _spatialRef1 = 0;
                _spatialMv0 = _spatialMv1 = 0;
            }
            else
            {
                _spatialMv0 = _spatialRef0 >= 0 ? PredictMv(0, 0, 0, 4, 4, _spatialRef0, 0) : 0;
                _spatialMv1 = _spatialRef1 >= 0 ? PredictMv(1, 0, 0, 4, 4, _spatialRef1, 0) : 0;
            }
        }

        int ref0 = _spatialRef0;
        int ref1 = _spatialRef1;
        mb.PredFlags[b8] = (ref0 >= 0 ? 1 : 0) | (ref1 >= 0 ? 2 : 0);
        Picture? col = _colocated;
        bool colShortTerm = col is not null && col.IsShortTermRef;

        for (int sub = 0; sub < 4; sub++)
        {
            int bx = bx8 + (sub & 1);
            int by = by8 + (sub >> 1);
            bool colZero = false;

            if (colShortTerm && (ref0 == 0 || ref1 == 0))
            {
                int colBlock = ColocatedBlock(bx, by);
                ColocatedMotion(col!, colBlock, out int mvCol, out int refCol, out _);
                colZero = refCol == 0 && Picture.MvX(mvCol) is >= -1 and <= 1 && Picture.MvY(mvCol) is >= -1 and <= 1;
            }

            int raster = by * 4 + bx;

            if (ref0 >= 0)
            {
                mb.Ref0[raster] = (sbyte)ref0;
                mb.Mv0[raster] = ref0 == 0 && colZero ? 0 : _spatialMv0;
            }

            if (ref1 >= 0)
            {
                mb.Ref1[raster] = (sbyte)ref1;
                mb.Mv1[raster] = ref1 == 0 && colZero ? 0 : _spatialMv1;
            }
        }
    }

    private static int MinPositive(int a, int b) => a >= 0 && b >= 0 ? Math.Min(a, b) : Math.Max(a, b);

    /// <summary>The co-located 4x4 block for (bx, by): the corner of its 8x8 under direct_8x8_inference.</summary>
    private int ColocatedBlock(int bx, int by)
    {
        if (_sps.Direct8x8Inference)
        {
            int cx = bx < 2 ? 0 : 3;
            int cy = by < 2 ? 0 : 3;
            return cy * 4 + cx;
        }

        return by * 4 + bx;
    }

    /// <summary>The motion of a block of the co-located picture, 8.4.1.2.1.</summary>
    private void ColocatedMotion(Picture col, int block, out int mvCol, out int refCol, out int refPicSerial)
    {
        int addr = _mb.Addr;

        if ((col.MbFlags[addr] & MbFlag.Intra) != 0)
        {
            mvCol = 0;
            refCol = -1;
            refPicSerial = 0;
            return;
        }

        int index = addr * 16 + block;

        if (col.Ref0[index] >= 0)
        {
            mvCol = col.Mv0[index];
            refCol = col.Ref0[index];
            refPicSerial = col.RefPic0[index];
        }
        else
        {
            mvCol = col.Mv1[index];
            refCol = col.Ref1[index];
            refPicSerial = col.RefPic1[index];
        }
    }

    private void DeriveTemporalDirect(int b8)
    {
        Macroblock mb = _mb;
        int bx8 = (b8 & 1) * 2;
        int by8 = (b8 >> 1) * 2;
        mb.PredFlags[b8] = 3;
        Picture? col = _colocated;

        for (int sub = 0; sub < 4; sub++)
        {
            int bx = bx8 + (sub & 1);
            int by = by8 + (sub >> 1);
            int raster = by * 4 + bx;
            int mvCol = 0;
            int refIdx0 = 0;

            if (col is not null)
            {
                ColocatedMotion(col, ColocatedBlock(bx, by), out mvCol, out int refCol, out int serial);

                if (refCol >= 0)
                {
                    refIdx0 = FindInList0(serial);
                }
            }

            Picture? pic0 = RefList0[refIdx0];
            Picture? pic1 = RefList1[0];
            int mv0;
            int mv1;

            if (pic0 is null || pic1 is null || pic0.IsLongTermRef)
            {
                mv0 = mvCol;
                mv1 = 0;
            }
            else
            {
                int tb = Math.Clamp(_pic.Poc - pic0.Poc, -128, 127);
                int td = Math.Clamp(pic1.Poc - pic0.Poc, -128, 127);

                if (td == 0)
                {
                    mv0 = mvCol;
                    mv1 = 0;
                }
                else
                {
                    int tx = (16384 + Math.Abs(td / 2)) / td;
                    int scale = Math.Clamp((tb * tx + 32) >> 6, -1024, 1023);
                    int x0 = (scale * Picture.MvX(mvCol) + 128) >> 8;
                    int y0 = (scale * Picture.MvY(mvCol) + 128) >> 8;
                    mv0 = Picture.PackMv(x0, y0);
                    mv1 = Picture.PackMv(x0 - Picture.MvX(mvCol), y0 - Picture.MvY(mvCol));
                }
            }

            mb.Ref0[raster] = (sbyte)refIdx0;
            mb.Ref1[raster] = 0;
            mb.Mv0[raster] = mv0;
            mb.Mv1[raster] = mv1;
        }
    }

    /// <summary>The lowest index in list 0 that refers to a picture, or 0 when none does.</summary>
    private int FindInList0(int serial)
    {
        for (int i = 0; i < _h.NumRefIdxL0Active; i++)
        {
            if (RefList0[i]?.Serial == serial)
            {
                return i;
            }
        }

        return 0;
    }
}
