namespace GK3Reborn.Formats.Video.H264;

/// <summary>
/// From a parsed macroblock to samples in the picture: prediction, then the residual on
/// top of it.
/// </summary>
internal sealed partial class SliceDecoder
{
    private readonly byte[] _pred0 = new byte[16 * 16];
    private readonly byte[] _pred1 = new byte[16 * 16];

    private void Reconstruct()
    {
        Macroblock mb = _mb;

        if (mb.Pcm)
        {
            ReconstructPcm();
            return;
        }

        if (mb.Intra)
        {
            ReconstructIntra();
            return;
        }

        DeriveMotion();
        MotionCompensate();
        AddInterResidual();
    }

    private void ReconstructPcm()
    {
        Picture pic = _pic;
        Macroblock mb = _mb;
        int pos = mb.Y * 16 * pic.Stride + mb.X * 16;

        for (int y = 0; y < 16; y++)
        {
            Array.Copy(mb.PcmSamples, y * 16, pic.Y, pos + y * pic.Stride, 16);
        }

        if (_chromaFormat != 0)
        {
            int cw = _sps.ChromaWidthMb;
            int ch = _sps.ChromaHeightMb;
            int cpos = mb.Y * ch * pic.ChromaStride + mb.X * cw;
            int at = 256;

            for (int y = 0; y < ch; y++, at += cw)
            {
                Array.Copy(mb.PcmSamples, at, pic.Cb, cpos + y * pic.ChromaStride, cw);
            }

            for (int y = 0; y < ch; y++, at += cw)
            {
                Array.Copy(mb.PcmSamples, at, pic.Cr, cpos + y * pic.ChromaStride, cw);
            }
        }
    }

    // ---- intra --------------------------------------------------------------------------------

    /// <summary>Whether a neighbouring macroblock's samples may be used for intra prediction.</summary>
    private bool IntraAvailable(int addr) =>
        addr >= 0 && (!_pps.ConstrainedIntraPred || (_pic.MbFlags[addr] & MbFlag.Intra) != 0);

    private void ReconstructIntra()
    {
        Macroblock mb = _mb;
        Picture pic = _pic;
        bool left = IntraAvailable(_mbA);
        bool top = IntraAvailable(_mbB);
        bool topLeft = IntraAvailable(_mbD);
        bool topRight = IntraAvailable(_mbC);
        int lumaPos = mb.Y * 16 * pic.Stride + mb.X * 16;

        // Luma, and in 4:4:4 the two chroma planes the same way with the same modes.
        int planes = _chromaFormat == 3 ? 3 : 1;

        for (int p = 0; p < planes; p++)
        {
            byte[] plane = p == 0 ? pic.Y : p == 1 ? pic.Cb : pic.Cr;
            Residual r = p == 0 ? mb.Luma : p == 1 ? mb.Cb : mb.Cr;
            int qp = p == 0 ? mb.QpY : p == 1 ? mb.QpCb : mb.QpCr;

            if (mb.Intra16x16)
            {
                IntraPrediction.Predict16x16(plane, pic.Stride, mb.X * 16, mb.Y * 16, mb.Intra16x16PredMode, left, top, topLeft);
                AddIntra16x16Residual(r, plane, lumaPos, p, qp);
            }
            else if (mb.Transform8x8)
            {
                for (int b8 = 0; b8 < 4; b8++)
                {
                    int bx = (b8 & 1) * 8;
                    int by = (b8 >> 1) * 8;
                    var edges = new IntraPrediction.Edges
                    {
                        Left = bx > 0 || left,
                        Top = by > 0 || top,
                        TopLeft = bx > 0 && by > 0 || (bx == 0 && by == 0 ? topLeft : bx == 0 ? left : top),
                        TopRight = b8 switch { 0 => top, 1 => topRight, 2 => true, _ => false },
                    };
                    IntraPrediction.Predict8x8(plane, pic.Stride, mb.X * 16 + bx, mb.Y * 16 + by, mb.IntraModes[(by / 4) * 4 + bx / 4], edges);

                    if ((r.NonZero8x8 & (1 << b8)) != 0)
                    {
                        int list = ScalingList8x8(p, true);
                        Transform.Dequant8x8(r.Coeff8x8, b8 * 64, _pps.LevelScale8x8[list][qp % 6], qp);
                        Transform.Add8x8(plane, pic.Stride, lumaPos + by * pic.Stride + bx, r.Coeff8x8, b8 * 64);
                    }
                }
            }
            else
            {
                for (int blkIdx = 0; blkIdx < 16; blkIdx++)
                {
                    int raster = Tables.RasterToBlk4x4[blkIdx];
                    int bx4 = raster & 3;
                    int by4 = raster >> 2;
                    int bx = bx4 * 4;
                    int by = by4 * 4;
                    var edges = new IntraPrediction.Edges
                    {
                        Left = bx4 > 0 || left,
                        Top = by4 > 0 || top,
                        TopLeft = bx4 > 0 && by4 > 0 || (bx4 == 0 && by4 == 0 ? topLeft : bx4 == 0 ? left : top),
                        TopRight = TopRightAvailable4x4(bx4, by4, top, topRight),
                    };
                    IntraPrediction.Predict4x4(plane, pic.Stride, mb.X * 16 + bx, mb.Y * 16 + by, mb.IntraModes[raster], edges);

                    if ((r.NonZero4x4 & (1 << raster)) != 0)
                    {
                        int list = ScalingList4x4(p, true);
                        Transform.Dequant4x4(r.Coeff4x4, raster * 16, _pps.LevelScale4x4[list][qp % 6], qp, false);
                        Transform.Add4x4(plane, pic.Stride, lumaPos + by * pic.Stride + bx, r.Coeff4x4, raster * 16);
                    }
                }
            }
        }

        if (_chromaFormat is 1 or 2)
        {
            int cw = _sps.ChromaWidthMb;
            int ch = _sps.ChromaHeightMb;
            IntraPrediction.PredictChroma(pic.Cb, pic.ChromaStride, mb.X * cw, mb.Y * ch, cw, ch, mb.IntraChromaPredMode, left, top, topLeft);
            IntraPrediction.PredictChroma(pic.Cr, pic.ChromaStride, mb.X * cw, mb.Y * ch, cw, ch, mb.IntraChromaPredMode, left, top, topLeft);
            AddChromaResidual(true);
        }
    }

    /// <summary>Whether the samples above and to the right of a 4x4 block exist yet.</summary>
    private static bool TopRightAvailable4x4(int bx, int by, bool top, bool topRight)
    {
        if (by == 0)
        {
            return bx < 3 ? top : topRight;
        }

        if (bx == 3)
        {
            return false;
        }

        // Inside the macroblock: only when the block up and to the right was decoded first.
        return Tables.RasterToBlk4x4[(by - 1) * 4 + bx + 1] < Tables.RasterToBlk4x4[by * 4 + bx];
    }

    private static int ScalingList4x4(int component, bool intra) => intra ? component : 3 + component;

    private static int ScalingList8x8(int component, bool intra) => component * 2 + (intra ? 0 : 1);

    private void AddIntra16x16Residual(Residual r, byte[] plane, int pos, int component, int qp)
    {
        int list = ScalingList4x4(component, true);
        int[] levelScale = _pps.LevelScale4x4[list][qp % 6];

        if (r.HasDc)
        {
            Transform.LumaDc(r.Dc, levelScale[0], qp);
        }

        for (int raster = 0; raster < 16; raster++)
        {
            int dc = r.Dc[raster];

            if (dc == 0 && (r.NonZero4x4 & (1 << raster)) == 0)
            {
                continue;
            }

            Transform.Dequant4x4(r.Coeff4x4, raster * 16, levelScale, qp, true);
            r.Coeff4x4[raster * 16] = dc;
            int bx = (raster & 3) * 4;
            int by = (raster >> 2) * 4;
            Transform.Add4x4(plane, _pic.Stride, pos + by * _pic.Stride + bx, r.Coeff4x4, raster * 16);
        }
    }

    /// <summary>Adds the residual of a luma-like component to an inter prediction already in the picture.</summary>
    private void AddLumaLikeResidual(Residual r, byte[] plane, int pos, int component, int qp)
    {
        if (_mb.Transform8x8)
        {
            int list = ScalingList8x8(component, false);

            for (int b8 = 0; b8 < 4; b8++)
            {
                if ((r.NonZero8x8 & (1 << b8)) == 0)
                {
                    continue;
                }

                int bx = (b8 & 1) * 8;
                int by = (b8 >> 1) * 8;
                Transform.Dequant8x8(r.Coeff8x8, b8 * 64, _pps.LevelScale8x8[list][qp % 6], qp);
                Transform.Add8x8(plane, _pic.Stride, pos + by * _pic.Stride + bx, r.Coeff8x8, b8 * 64);
            }
        }
        else
        {
            int list = ScalingList4x4(component, false);

            for (int raster = 0; raster < 16; raster++)
            {
                if ((r.NonZero4x4 & (1 << raster)) == 0)
                {
                    continue;
                }

                int bx = (raster & 3) * 4;
                int by = (raster >> 2) * 4;
                Transform.Dequant4x4(r.Coeff4x4, raster * 16, _pps.LevelScale4x4[list][qp % 6], qp, false);
                Transform.Add4x4(plane, _pic.Stride, pos + by * _pic.Stride + bx, r.Coeff4x4, raster * 16);
            }
        }
    }

    private void AddInterResidual()
    {
        Macroblock mb = _mb;
        Picture pic = _pic;
        int pos = mb.Y * 16 * pic.Stride + mb.X * 16;
        AddLumaLikeResidual(mb.Luma, pic.Y, pos, 0, mb.QpY);

        if (_chromaFormat == 3)
        {
            AddLumaLikeResidual(mb.Cb, pic.Cb, pos, 1, mb.QpCb);
            AddLumaLikeResidual(mb.Cr, pic.Cr, pos, 2, mb.QpCr);
        }
        else if (_chromaFormat != 0)
        {
            AddChromaResidual(false);
        }
    }

    /// <summary>The 4:2:0 chroma residual: the 2x2 DC transform, then each 4x4 block, 8.5.11.</summary>
    private void AddChromaResidual(bool intra)
    {
        Macroblock mb = _mb;
        Picture pic = _pic;
        int cw = _sps.ChromaWidthMb;
        int ch = _sps.ChromaHeightMb;
        int pos = mb.Y * ch * pic.ChromaStride + mb.X * cw;
        int wide = ChromaBlocksWide;
        int high = ChromaBlocksHigh;

        for (int c = 0; c < 2; c++)
        {
            Residual r = c == 0 ? mb.Cb : mb.Cr;
            byte[] plane = c == 0 ? pic.Cb : pic.Cr;
            int qp = c == 0 ? mb.QpCb : mb.QpCr;
            int[] levelScale = _pps.LevelScale4x4[ScalingList4x4(1 + c, intra)][qp % 6];

            if (r.HasDc)
            {
                Transform.ChromaDc420(r.Dc, levelScale[0], qp);
            }

            for (int blk = 0; blk < wide * high; blk++)
            {
                int dc = r.Dc[blk];

                if (dc == 0 && (r.NonZero4x4 & (1 << blk)) == 0)
                {
                    continue;
                }

                Transform.Dequant4x4(r.Coeff4x4, blk * 16, levelScale, qp, true);
                r.Coeff4x4[blk * 16] = dc;
                int bx = (blk % wide) * 4;
                int by = (blk / wide) * 4;
                Transform.Add4x4(plane, pic.ChromaStride, pos + by * pic.ChromaStride + bx, r.Coeff4x4, blk * 16);
            }
        }
    }

    // ---- inter --------------------------------------------------------------------------------

    /// <summary>Predicts every partition of the macroblock from its references into the picture.</summary>
    private void MotionCompensate()
    {
        Macroblock mb = _mb;

        if (mb.NumParts != 4)
        {
            for (int part = 0; part < mb.NumParts; part++)
            {
                (int px, int py, int pw, int ph) = PartitionGeometry(part);
                PredictBlock(px, py, pw, ph);
            }

            return;
        }

        for (int b8 = 0; b8 < 4; b8++)
        {
            int bx8 = (b8 & 1) * 2;
            int by8 = (b8 >> 1) * 2;

            if (mb.SubDirect[b8])
            {
                // Direct blocks may differ per 4x4 unless 8x8 inference applies.
                if (_sps.Direct8x8Inference)
                {
                    PredictBlock(bx8, by8, 2, 2);
                }
                else
                {
                    for (int sub = 0; sub < 4; sub++)
                    {
                        PredictBlock(bx8 + (sub & 1), by8 + (sub >> 1), 1, 1);
                    }
                }

                continue;
            }

            (int subParts, int subW, int subH) = SubPartitionGeometry(mb.SubMbType[b8]);

            for (int sub = 0; sub < subParts; sub++)
            {
                (int sx, int sy) = SubPartitionOrigin(bx8, by8, subW, subH, sub);
                PredictBlock(sx, sy, subW, subH);
            }
        }
    }

    /// <summary>Predicts one block of w x h 4x4 units at (bx, by), all three components.</summary>
    private void PredictBlock(int bx, int by, int w, int h)
    {
        Macroblock mb = _mb;
        Picture pic = _pic;
        int raster = by * 4 + bx;
        int ref0 = mb.Ref0[raster];
        int ref1 = mb.Ref1[raster];
        Picture? pic0 = ref0 >= 0 ? RefList0[ref0] : null;
        Picture? pic1 = ref1 >= 0 ? RefList1[ref1] : null;

        if (ref0 >= 0 && pic0 is null)
        {
            throw new FormatParseException("H.264: a block refers to a list 0 picture that does not exist.");
        }

        if (ref1 >= 0 && pic1 is null)
        {
            throw new FormatParseException("H.264: a block refers to a list 1 picture that does not exist.");
        }

        if (pic0 is null && pic1 is null)
        {
            throw new FormatParseException("H.264: an inter block with no reference.");
        }

        int mv0 = mb.Mv0[raster];
        int mv1 = mb.Mv1[raster];
        int x = mb.X * 16 + bx * 4;
        int y = mb.Y * 16 + by * 4;
        int pw = w * 4;
        int ph = h * 4;

        // Luma.
        if (pic0 is not null)
        {
            InterPrediction.Luma(pic0.Y, pic0.Stride, pic0.Width, pic0.Height, x, y, Picture.MvX(mv0), Picture.MvY(mv0), pw, ph, _pred0, 0, 16);
        }

        if (pic1 is not null)
        {
            InterPrediction.Luma(pic1.Y, pic1.Stride, pic1.Width, pic1.Height, x, y, Picture.MvX(mv1), Picture.MvY(mv1), pw, ph, _pred1, 0, 16);
        }

        Combine(0, ref0, ref1, pw, ph, pic.Y, pic.Stride, y * pic.Stride + x);

        if (_chromaFormat == 0)
        {
            return;
        }

        if (_chromaFormat == 3)
        {
            for (int c = 0; c < 2; c++)
            {
                if (pic0 is not null)
                {
                    byte[] plane = c == 0 ? pic0.Cb : pic0.Cr;
                    InterPrediction.Luma(plane, pic0.ChromaStride, pic0.ChromaWidth, pic0.ChromaHeight, x, y, Picture.MvX(mv0), Picture.MvY(mv0), pw, ph, _pred0, 0, 16);
                }

                if (pic1 is not null)
                {
                    byte[] plane = c == 0 ? pic1.Cb : pic1.Cr;
                    InterPrediction.Luma(plane, pic1.ChromaStride, pic1.ChromaWidth, pic1.ChromaHeight, x, y, Picture.MvX(mv1), Picture.MvY(mv1), pw, ph, _pred1, 0, 16);
                }

                Combine(1 + c, ref0, ref1, pw, ph, c == 0 ? pic.Cb : pic.Cr, pic.ChromaStride, y * pic.ChromaStride + x);
            }

            return;
        }

        // 4:2:0: half the size, eighth-sample vectors.
        int cx = x / 2;
        int cy = y / 2;
        int cw = pw / 2;
        int chh = ph / 2;

        for (int c = 0; c < 2; c++)
        {
            if (pic0 is not null)
            {
                byte[] plane = c == 0 ? pic0.Cb : pic0.Cr;
                InterPrediction.Chroma(plane, pic0.ChromaStride, pic0.ChromaWidth, pic0.ChromaHeight, cx, cy, Picture.MvX(mv0), Picture.MvY(mv0), cw, chh, _pred0, 0, 16);
            }

            if (pic1 is not null)
            {
                byte[] plane = c == 0 ? pic1.Cb : pic1.Cr;
                InterPrediction.Chroma(plane, pic1.ChromaStride, pic1.ChromaWidth, pic1.ChromaHeight, cx, cy, Picture.MvX(mv1), Picture.MvY(mv1), cw, chh, _pred1, 0, 16);
            }

            Combine(1 + c, ref0, ref1, cw, chh, c == 0 ? pic.Cb : pic.Cr, pic.ChromaStride, cy * pic.ChromaStride + cx);
        }
    }

    /// <summary>Weighted sample prediction, 8.4.2.3, of the block(s) just interpolated.</summary>
    private void Combine(int component, int ref0, int ref1, int w, int h, byte[] plane, int stride, int pos)
    {
        bool bi = ref0 >= 0 && ref1 >= 0;
        int mode = _h.IsP ? (_pps.WeightedPred ? 1 : 0) : _pps.WeightedBipredIdc;

        if (mode == 0 || (mode == 2 && !bi))
        {
            if (bi)
            {
                InterPrediction.Average(_pred0, _pred1, w, h, plane, stride, pos);
            }
            else
            {
                InterPrediction.Copy(ref0 >= 0 ? _pred0 : _pred1, w, h, plane, stride, pos);
            }

            return;
        }

        if (mode == 2)
        {
            int w0 = _implicitWeights![ref0, ref1];
            InterPrediction.WeighBi(_pred0, _pred1, w, h, w0, 64 - w0, 0, 0, 5, plane, stride, pos);
            return;
        }

        // Explicit.
        int logWd = component == 0 ? _h.LumaLog2WeightDenom : _h.ChromaLog2WeightDenom;
        GetWeight(_h.WeightsL0, ref0, component, out int wt0, out int o0);
        GetWeight(_h.WeightsL1, ref1, component, out int wt1, out int o1);

        if (bi)
        {
            InterPrediction.WeighBi(_pred0, _pred1, w, h, wt0, wt1, o0, o1, logWd, plane, stride, pos);
        }
        else if (ref0 >= 0)
        {
            InterPrediction.Weigh(_pred0, w, h, wt0, o0, logWd, plane, stride, pos);
        }
        else
        {
            InterPrediction.Weigh(_pred1, w, h, wt1, o1, logWd, plane, stride, pos);
        }
    }

    private static void GetWeight(PredWeight[] weights, int refIdx, int component, out int weight, out int offset)
    {
        if (refIdx < 0 || refIdx >= weights.Length)
        {
            weight = 1;
            offset = 0;
            return;
        }

        ref PredWeight w = ref weights[refIdx];

        switch (component)
        {
            case 0:
                weight = w.LumaWeight;
                offset = w.LumaOffset;
                break;
            case 1:
                weight = w.CbWeight;
                offset = w.CbOffset;
                break;
            default:
                weight = w.CrWeight;
                offset = w.CrOffset;
                break;
        }
    }
}
