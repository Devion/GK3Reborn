using System.Runtime.CompilerServices;

namespace GK3Reborn.Formats.Video.H264;

/// <summary>
/// The in-loop deblocking filter, 8.7, run over a whole picture once its slices are
/// decoded.
/// </summary>
/// <remarks>
/// <para>
/// Macroblock by macroblock in raster order, and within each: the vertical luma edges
/// left to right, the horizontal ones top to bottom, then chroma the same way. The order
/// matters because each edge is filtered in place and the next reads what the last wrote.
/// </para>
/// <para>
/// Boundary strengths come from the per-block state the slice decoder stored: intra,
/// coefficients, and which pictures each block predicted from — compared by picture, not
/// by index, since two indices can name one picture.
/// </para>
/// </remarks>
internal sealed class Deblocker
{
    private readonly Picture _pic;
    private readonly SequenceParameterSet _sps;
    private readonly PictureParameterSet _pps;
    private readonly int[] _bs = new int[2 * 4 * 4]; // [direction][edge][segment]

    public Deblocker(Picture picture, SequenceParameterSet sps, PictureParameterSet pps)
    {
        _pic = picture;
        _sps = sps;
        _pps = pps;
    }

    public void Run()
    {
        Picture pic = _pic;

        for (int addr = 0; addr < pic.MbCount; addr++)
        {
            SliceHeader slice = pic.Slices[pic.SliceId[addr]];

            if (slice.DisableDeblockingFilterIdc == 1)
            {
                continue;
            }

            int x = addr % pic.WidthMbs;
            int y = addr / pic.WidthMbs;
            int left = x > 0 ? addr - 1 : -1;
            int top = y > 0 ? addr - pic.WidthMbs : -1;

            if (slice.DisableDeblockingFilterIdc == 2)
            {
                if (left >= 0 && pic.SliceId[left] != pic.SliceId[addr])
                {
                    left = -1;
                }

                if (top >= 0 && pic.SliceId[top] != pic.SliceId[addr])
                {
                    top = -1;
                }
            }

            ComputeStrengths(addr, left, top);
            FilterMacroblock(addr, x, y, left, top, slice);
        }
    }

    // ---- boundary strength, 8.7.2.1 ----------------------------------------------------------

    private void ComputeStrengths(int addr, int left, int top)
    {
        Picture pic = _pic;
        bool intra = (pic.MbFlags[addr] & MbFlag.Intra) != 0;
        bool t8x8 = (pic.MbFlags[addr] & MbFlag.Transform8x8) != 0;

        // Vertical edges (direction 0): edge e at x = 4e, segments are rows of 4x4 blocks.
        for (int e = 0; e < 4; e++)
        {
            for (int s = 0; s < 4; s++)
            {
                int bs;

                if (e == 0)
                {
                    bs = left < 0 ? 0 : Strength(addr, s * 4, left, s * 4 + 3, true, intra);
                }
                else if (t8x8 && (e & 1) != 0)
                {
                    bs = 0;
                }
                else
                {
                    bs = Strength(addr, s * 4 + e, addr, s * 4 + e - 1, false, intra);
                }

                _bs[e * 4 + s] = bs;
            }
        }

        // Horizontal edges (direction 1): edge e at y = 4e, segments are columns.
        for (int e = 0; e < 4; e++)
        {
            for (int s = 0; s < 4; s++)
            {
                int bs;

                if (e == 0)
                {
                    bs = top < 0 ? 0 : Strength(addr, s, top, 12 + s, true, intra);
                }
                else if (t8x8 && (e & 1) != 0)
                {
                    bs = 0;
                }
                else
                {
                    bs = Strength(addr, e * 4 + s, addr, (e - 1) * 4 + s, false, intra);
                }

                _bs[16 + e * 4 + s] = bs;
            }
        }
    }

    /// <summary>bS between block q (in the current macroblock) and block p.</summary>
    private int Strength(int addrQ, int blkQ, int addrP, int blkP, bool mbEdge, bool intraQ)
    {
        Picture pic = _pic;
        bool intraP = (pic.MbFlags[addrP] & MbFlag.Intra) != 0;

        if (intraQ || intraP)
        {
            return mbEdge ? 4 : 3;
        }

        if (((pic.NonZeroBits[addrQ] >> blkQ) & 1) != 0 || ((pic.NonZeroBits[addrP] >> blkP) & 1) != 0)
        {
            return 2;
        }

        int q = addrQ * 16 + blkQ;
        int p = addrP * 16 + blkP;
        int q0 = pic.Ref0[q] >= 0 ? pic.RefPic0[q] : 0;
        int q1 = pic.Ref1[q] >= 0 ? pic.RefPic1[q] : 0;
        int p0 = pic.Ref0[p] >= 0 ? pic.RefPic0[p] : 0;
        int p1 = pic.Ref1[p] >= 0 ? pic.RefPic1[p] : 0;
        int countQ = (q0 != 0 ? 1 : 0) + (q1 != 0 ? 1 : 0);
        int countP = (p0 != 0 ? 1 : 0) + (p1 != 0 ? 1 : 0);

        if (countQ != countP)
        {
            return 1;
        }

        int mvQ0 = pic.Mv0[q], mvQ1 = pic.Mv1[q], mvP0 = pic.Mv0[p], mvP1 = pic.Mv1[p];

        if (countQ == 1)
        {
            int refQ = q0 != 0 ? q0 : q1;
            int refP = p0 != 0 ? p0 : p1;

            if (refQ != refP)
            {
                return 1;
            }

            int mvQ = q0 != 0 ? mvQ0 : mvQ1;
            int mvP = p0 != 0 ? mvP0 : mvP1;
            return Differ(mvQ, mvP) ? 1 : 0;
        }

        // Two references each: the sets must match.
        bool sameOrder = q0 == p0 && q1 == p1;
        bool crossOrder = q0 == p1 && q1 == p0;

        if (!sameOrder && !crossOrder)
        {
            return 1;
        }

        if (q0 != q1)
        {
            if (sameOrder)
            {
                return Differ(mvQ0, mvP0) || Differ(mvQ1, mvP1) ? 1 : 0;
            }

            return Differ(mvQ0, mvP1) || Differ(mvQ1, mvP0) ? 1 : 0;
        }

        // The same picture twice: both pairings have to fail for the edge to count.
        bool straight = Differ(mvQ0, mvP0) || Differ(mvQ1, mvP1);
        bool crossed = Differ(mvQ0, mvP1) || Differ(mvQ1, mvP0);
        return straight && crossed ? 1 : 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Differ(int a, int b) =>
        Math.Abs(Picture.MvX(a) - Picture.MvX(b)) >= 4 || Math.Abs(Picture.MvY(a) - Picture.MvY(b)) >= 4;

    // ---- filtering, 8.7.2.2 to 8.7.2.4 ----------------------------------------------------------

    private int LumaQp(int addr) => (_pic.MbFlags[addr] & MbFlag.Pcm) != 0 ? 0 : _pic.QpY[addr];

    private int ChromaQp(int addr, int component)
    {
        int offset = component == 0 ? _pps.ChromaQpIndexOffset : _pps.SecondChromaQpIndexOffset;
        return Tables.ChromaQp(Math.Clamp(LumaQp(addr) + offset, 0, 51));
    }

    private void FilterMacroblock(int addr, int mbX, int mbY, int left, int top, SliceHeader slice)
    {
        Picture pic = _pic;
        bool t8x8 = (pic.MbFlags[addr] & MbFlag.Transform8x8) != 0;
        int offsetA = slice.SliceAlphaOffset;
        int offsetB = slice.SliceBetaOffset;
        int qpQ = LumaQp(addr);

        // Luma.
        int lumaPos = mbY * 16 * pic.Stride + mbX * 16;

        for (int e = 0; e < 4; e++)
        {
            if (e == 0 && left < 0)
            {
                continue;
            }

            if (e != 0 && t8x8 && (e & 1) != 0)
            {
                continue;
            }

            int qpP = e == 0 ? LumaQp(left) : qpQ;
            int qpAv = (qpP + qpQ + 1) >> 1;

            for (int s = 0; s < 4; s++)
            {
                int bs = _bs[e * 4 + s];

                if (bs == 0)
                {
                    continue;
                }

                FilterEdge(pic.Y, lumaPos + s * 4 * pic.Stride + e * 4, 1, pic.Stride, 4, bs, qpAv, offsetA, offsetB, false);
            }
        }

        for (int e = 0; e < 4; e++)
        {
            if (e == 0 && top < 0)
            {
                continue;
            }

            if (e != 0 && t8x8 && (e & 1) != 0)
            {
                continue;
            }

            int qpP = e == 0 ? LumaQp(top) : qpQ;
            int qpAv = (qpP + qpQ + 1) >> 1;

            for (int s = 0; s < 4; s++)
            {
                int bs = _bs[16 + e * 4 + s];

                if (bs == 0)
                {
                    continue;
                }

                FilterEdge(pic.Y, lumaPos + e * 4 * pic.Stride + s * 4, pic.Stride, 1, 4, bs, qpAv, offsetA, offsetB, false);
            }
        }

        if (_sps.ChromaFormatIdc == 0)
        {
            return;
        }

        if (_sps.ChromaFormatIdc == 3)
        {
            // Chroma coded like luma is filtered like luma, with chroma QPs.
            for (int c = 0; c < 2; c++)
            {
                byte[] plane = c == 0 ? pic.Cb : pic.Cr;
                int qpCq = ChromaQp(addr, c);
                int pos = mbY * 16 * pic.ChromaStride + mbX * 16;

                for (int e = 0; e < 4; e++)
                {
                    if ((e == 0 && left < 0) || (e != 0 && t8x8 && (e & 1) != 0))
                    {
                        continue;
                    }

                    int qpAv = ((e == 0 ? ChromaQp(left, c) : qpCq) + qpCq + 1) >> 1;

                    for (int s = 0; s < 4; s++)
                    {
                        int bs = _bs[e * 4 + s];

                        if (bs != 0)
                        {
                            FilterEdge(plane, pos + s * 4 * pic.ChromaStride + e * 4, 1, pic.ChromaStride, 4, bs, qpAv, offsetA, offsetB, false);
                        }
                    }
                }

                for (int e = 0; e < 4; e++)
                {
                    if ((e == 0 && top < 0) || (e != 0 && t8x8 && (e & 1) != 0))
                    {
                        continue;
                    }

                    int qpAv = ((e == 0 ? ChromaQp(top, c) : qpCq) + qpCq + 1) >> 1;

                    for (int s = 0; s < 4; s++)
                    {
                        int bs = _bs[16 + e * 4 + s];

                        if (bs != 0)
                        {
                            FilterEdge(plane, pos + e * 4 * pic.ChromaStride + s * 4, pic.ChromaStride, 1, 4, bs, qpAv, offsetA, offsetB, false);
                        }
                    }
                }
            }

            return;
        }

        // 4:2:0: an 8x8 chroma block with edges at 0 and 4, each chroma row taking the
        // strength of the luma row it sits on.
        for (int c = 0; c < 2; c++)
        {
            byte[] plane = c == 0 ? pic.Cb : pic.Cr;
            int qpCq = ChromaQp(addr, c);
            int pos = mbY * 8 * pic.ChromaStride + mbX * 8;

            for (int e = 0; e < 2; e++)
            {
                if (e == 0 && left < 0)
                {
                    continue;
                }

                int qpAv = ((e == 0 ? ChromaQp(left, c) : qpCq) + qpCq + 1) >> 1;
                int lumaEdge = e * 2;

                for (int row = 0; row < 8; row += 2)
                {
                    int bs = _bs[lumaEdge * 4 + (row >> 1)];

                    if (bs != 0)
                    {
                        FilterEdge(plane, pos + row * pic.ChromaStride + e * 4, 1, pic.ChromaStride, 2, bs, qpAv, offsetA, offsetB, true);
                    }
                }
            }

            for (int e = 0; e < 2; e++)
            {
                if (e == 0 && top < 0)
                {
                    continue;
                }

                int qpAv = ((e == 0 ? ChromaQp(top, c) : qpCq) + qpCq + 1) >> 1;
                int lumaEdge = e * 2;

                for (int col = 0; col < 8; col += 2)
                {
                    int bs = _bs[16 + lumaEdge * 4 + (col >> 1)];

                    if (bs != 0)
                    {
                        FilterEdge(plane, pos + e * 4 * pic.ChromaStride + col, pic.ChromaStride, 1, 2, bs, qpAv, offsetA, offsetB, true);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Filters <paramref name="count"/> lines across one edge. <paramref name="across"/> is
    /// the step from p0 to q0 and <paramref name="along"/> the step between lines.
    /// </summary>
    private static void FilterEdge(
        byte[] plane, int q0Pos, int across, int along, int count, int bs, int qpAv, int offsetA, int offsetB, bool chromaStyle)
    {
        int indexA = Math.Clamp(qpAv + offsetA, 0, 51);
        int indexB = Math.Clamp(qpAv + offsetB, 0, 51);
        int alpha = Tables.Alpha[indexA];
        int beta = Tables.Beta[indexB];

        if (alpha == 0 || beta == 0)
        {
            return;
        }

        for (int line = 0; line < count; line++)
        {
            int q = q0Pos + line * along;
            int p = q - across;
            int p0 = plane[p];
            int p1 = plane[p - across];
            int q0 = plane[q];
            int q1 = plane[q + across];

            if (Math.Abs(p0 - q0) >= alpha || Math.Abs(p1 - p0) >= beta || Math.Abs(q1 - q0) >= beta)
            {
                continue;
            }

            if (bs < 4)
            {
                int tc0 = Tables.Tc0[bs - 1][indexA];
                int tc;

                if (chromaStyle)
                {
                    tc = tc0 + 1;
                }
                else
                {
                    int p2 = plane[p - 2 * across];
                    int q2 = plane[q + 2 * across];
                    bool ap = Math.Abs(p2 - p0) < beta;
                    bool aq = Math.Abs(q2 - q0) < beta;
                    tc = tc0 + (ap ? 1 : 0) + (aq ? 1 : 0);

                    if (ap)
                    {
                        plane[p - across] = (byte)(p1 + Math.Clamp((p2 + ((p0 + q0 + 1) >> 1) - (p1 << 1)) >> 1, -tc0, tc0));
                    }

                    if (aq)
                    {
                        plane[q + across] = (byte)(q1 + Math.Clamp((q2 + ((p0 + q0 + 1) >> 1) - (q1 << 1)) >> 1, -tc0, tc0));
                    }
                }

                int delta = Math.Clamp((((q0 - p0) << 2) + (p1 - q1) + 4) >> 3, -tc, tc);
                plane[p] = Clip(p0 + delta);
                plane[q] = Clip(q0 - delta);
            }
            else
            {
                if (chromaStyle)
                {
                    plane[p] = (byte)((2 * p1 + p0 + q1 + 2) >> 2);
                    plane[q] = (byte)((2 * q1 + q0 + p1 + 2) >> 2);
                    continue;
                }

                int p2 = plane[p - 2 * across];
                int q2 = plane[q + 2 * across];
                bool strong = Math.Abs(p0 - q0) < ((alpha >> 2) + 2);

                if (strong && Math.Abs(p2 - p0) < beta)
                {
                    int p3 = plane[p - 3 * across];
                    plane[p] = (byte)((p2 + 2 * p1 + 2 * p0 + 2 * q0 + q1 + 4) >> 3);
                    plane[p - across] = (byte)((p2 + p1 + p0 + q0 + 2) >> 2);
                    plane[p - 2 * across] = (byte)((2 * p3 + 3 * p2 + p1 + p0 + q0 + 4) >> 3);
                }
                else
                {
                    plane[p] = (byte)((2 * p1 + p0 + q1 + 2) >> 2);
                }

                if (strong && Math.Abs(q2 - q0) < beta)
                {
                    int q3 = plane[q + 3 * across];
                    plane[q] = (byte)((p1 + 2 * p0 + 2 * q0 + 2 * q1 + q2 + 4) >> 3);
                    plane[q + across] = (byte)((p0 + q0 + q1 + q2 + 2) >> 2);
                    plane[q + 2 * across] = (byte)((2 * q3 + 3 * q2 + q1 + q0 + p0 + 4) >> 3);
                }
                else
                {
                    plane[q] = (byte)((2 * q1 + q0 + p1 + 2) >> 2);
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte Clip(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
}
