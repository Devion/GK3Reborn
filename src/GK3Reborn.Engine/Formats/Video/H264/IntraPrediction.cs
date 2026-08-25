using System.Runtime.CompilerServices;

namespace GK3Reborn.Formats.Video.H264;

/// <summary>
/// Intra prediction, 8.3: builds a block from the reconstructed samples around it.
/// </summary>
/// <remarks>
/// <para>
/// The 4x4 and 8x8 predictors share one implementation over an edge array laid out so
/// that the standard's <c>p[x, -1]</c> and <c>p[-1, y]</c> are plain offsets from the
/// corner: the left column bottom-up, the corner, then the top row. The formulas can then
/// be written as they appear in 8.3.1.2 and 8.3.2.2, negative indices included.
/// </para>
/// <para>
/// Prediction writes straight into the picture; the residual is added on top afterwards.
/// </para>
/// </remarks>
internal static class IntraPrediction
{
    public const int Vertical = 0;
    public const int Horizontal = 1;
    public const int Dc = 2;
    public const int DiagonalDownLeft = 3;
    public const int DiagonalDownRight = 4;
    public const int VerticalRight = 5;
    public const int HorizontalDown = 6;
    public const int VerticalLeft = 7;
    public const int HorizontalUp = 8;

    /// <summary>Which neighbouring samples exist for a block.</summary>
    public struct Edges
    {
        public bool Left;
        public bool Top;
        public bool TopLeft;
        public bool TopRight;
    }

    [ThreadStatic]
    private static int[]? _edge;

    [ThreadStatic]
    private static int[]? _filtered;

    /// <summary>
    /// Fills the edge array for an NxN block at (x, y): left column bottom-up in [0, n),
    /// the corner at n, the top row of 2n samples from n + 1.
    /// </summary>
    private static int[] GatherEdge(byte[] plane, int stride, int x, int y, int n, in Edges edges)
    {
        int[] e = _edge ??= new int[3 * 16 + 1];
        int pos = y * stride + x;

        if (edges.Left)
        {
            for (int i = 0; i < n; i++)
            {
                e[n - 1 - i] = plane[pos + i * stride - 1];
            }
        }

        e[n] = edges.TopLeft ? plane[pos - stride - 1] : 0;

        if (edges.Top)
        {
            int top = pos - stride;

            for (int i = 0; i < n; i++)
            {
                e[n + 1 + i] = plane[top + i];
            }

            if (edges.TopRight)
            {
                for (int i = n; i < 2 * n; i++)
                {
                    e[n + 1 + i] = plane[top + i];
                }
            }
            else
            {
                int last = plane[top + n - 1];

                for (int i = n; i < 2 * n; i++)
                {
                    e[n + 1 + i] = last;
                }
            }
        }

        return e;
    }

    /// <summary>Intra_4x4 prediction of one block, 8.3.1.2.</summary>
    public static void Predict4x4(byte[] plane, int stride, int x, int y, int mode, in Edges edges)
    {
        int[] e = GatherEdge(plane, stride, x, y, 4, edges);
        PredictNxN(plane, stride, x, y, 4, mode, e, edges);
    }

    /// <summary>Intra_8x8 prediction of one block with reference filtering, 8.3.2.2.</summary>
    public static void Predict8x8(byte[] plane, int stride, int x, int y, int mode, in Edges edges)
    {
        int[] raw = GatherEdge(plane, stride, x, y, 8, edges);
        int[] e = _filtered ??= new int[3 * 16 + 1];
        const int n = 8;

        // 8.3.2.2.1: the reference samples are smoothed before use.
        if (edges.Top)
        {
            if (edges.TopLeft)
            {
                e[n + 1] = (raw[n] + 2 * raw[n + 1] + raw[n + 2] + 2) >> 2;
            }
            else
            {
                e[n + 1] = (3 * raw[n + 1] + raw[n + 2] + 2) >> 2;
            }

            for (int i = 1; i < 15; i++)
            {
                e[n + 1 + i] = (raw[n + i] + 2 * raw[n + 1 + i] + raw[n + 2 + i] + 2) >> 2;
            }

            e[n + 1 + 15] = (raw[n + 1 + 14] + 3 * raw[n + 1 + 15] + 2) >> 2;
        }

        if (edges.TopLeft)
        {
            if (edges.Top && edges.Left)
            {
                e[n] = (raw[n + 1] + 2 * raw[n] + raw[n - 1] + 2) >> 2;
            }
            else if (edges.Top)
            {
                e[n] = (3 * raw[n] + raw[n + 1] + 2) >> 2;
            }
            else if (edges.Left)
            {
                e[n] = (3 * raw[n] + raw[n - 1] + 2) >> 2;
            }
            else
            {
                e[n] = raw[n];
            }
        }

        if (edges.Left)
        {
            // raw[n - 1 - y] is p[-1, y].
            if (edges.TopLeft)
            {
                e[n - 1] = (raw[n] + 2 * raw[n - 1] + raw[n - 2] + 2) >> 2;
            }
            else
            {
                e[n - 1] = (3 * raw[n - 1] + raw[n - 2] + 2) >> 2;
            }

            for (int yy = 1; yy < 7; yy++)
            {
                e[n - 1 - yy] = (raw[n - yy] + 2 * raw[n - 1 - yy] + raw[n - 2 - yy] + 2) >> 2;
            }

            e[0] = (raw[1] + 3 * raw[0] + 2) >> 2;
        }

        PredictNxN(plane, stride, x, y, 8, mode, e, edges);
    }

    /// <summary>The nine directional modes over the edge array, for n = 4 or 8.</summary>
    private static void PredictNxN(byte[] plane, int stride, int x, int y, int n, int mode, int[] e, in Edges edges)
    {
        int pos = y * stride + x;

        // p[x, -1] = e[n + 1 + x]; p[-1, y] = e[n - 1 - y]; p[-1, -1] = e[n].
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int T(int[] e, int n, int x) => e[n + 1 + x];

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int L(int[] e, int n, int y) => e[n - 1 - y];

        switch (mode)
        {
            case Vertical:
                if (!edges.Top)
                {
                    throw new FormatParseException($"H.264: vertical intra prediction with no row above at ({x},{y}) size {n}.");
                }

                for (int yy = 0; yy < n; yy++)
                {
                    for (int xx = 0; xx < n; xx++)
                    {
                        plane[pos + yy * stride + xx] = (byte)T(e, n, xx);
                    }
                }

                break;

            case Horizontal:
                if (!edges.Left)
                {
                    throw new FormatParseException($"H.264: horizontal intra prediction with no column to the left at ({x},{y}) size {n}.");
                }

                for (int yy = 0; yy < n; yy++)
                {
                    byte v = (byte)L(e, n, yy);

                    for (int xx = 0; xx < n; xx++)
                    {
                        plane[pos + yy * stride + xx] = v;
                    }
                }

                break;

            case Dc:
                {
                    int sum = 0;
                    int dc;

                    if (edges.Top && edges.Left)
                    {
                        for (int i = 0; i < n; i++)
                        {
                            sum += T(e, n, i) + L(e, n, i);
                        }

                        dc = (sum + n) >> (n == 4 ? 3 : 4);
                    }
                    else if (edges.Left)
                    {
                        for (int i = 0; i < n; i++)
                        {
                            sum += L(e, n, i);
                        }

                        dc = (sum + (n >> 1)) >> (n == 4 ? 2 : 3);
                    }
                    else if (edges.Top)
                    {
                        for (int i = 0; i < n; i++)
                        {
                            sum += T(e, n, i);
                        }

                        dc = (sum + (n >> 1)) >> (n == 4 ? 2 : 3);
                    }
                    else
                    {
                        dc = 128;
                    }

                    for (int yy = 0; yy < n; yy++)
                    {
                        for (int xx = 0; xx < n; xx++)
                        {
                            plane[pos + yy * stride + xx] = (byte)dc;
                        }
                    }

                    break;
                }

            case DiagonalDownLeft:
                if (!edges.Top)
                {
                    throw new FormatParseException($"H.264: diagonal intra prediction with no row above at ({x},{y}) size {n}.");
                }

                for (int yy = 0; yy < n; yy++)
                {
                    for (int xx = 0; xx < n; xx++)
                    {
                        int v = xx == n - 1 && yy == n - 1
                            ? (T(e, n, 2 * n - 2) + 3 * T(e, n, 2 * n - 1) + 2) >> 2
                            : (T(e, n, xx + yy) + 2 * T(e, n, xx + yy + 1) + T(e, n, xx + yy + 2) + 2) >> 2;
                        plane[pos + yy * stride + xx] = (byte)v;
                    }
                }

                break;

            case DiagonalDownRight:
                RequireAll(edges);

                for (int yy = 0; yy < n; yy++)
                {
                    for (int xx = 0; xx < n; xx++)
                    {
                        int v;

                        if (xx > yy)
                        {
                            v = (T(e, n, xx - yy - 2) + 2 * T(e, n, xx - yy - 1) + T(e, n, xx - yy) + 2) >> 2;
                        }
                        else if (xx < yy)
                        {
                            v = (L(e, n, yy - xx - 2) + 2 * L(e, n, yy - xx - 1) + L(e, n, yy - xx) + 2) >> 2;
                        }
                        else
                        {
                            v = (T(e, n, 0) + 2 * e[n] + L(e, n, 0) + 2) >> 2;
                        }

                        plane[pos + yy * stride + xx] = (byte)v;
                    }
                }

                break;

            case VerticalRight:
                RequireAll(edges);

                for (int yy = 0; yy < n; yy++)
                {
                    for (int xx = 0; xx < n; xx++)
                    {
                        int z = 2 * xx - yy;
                        int v;

                        if (z >= 0 && (z & 1) == 0)
                        {
                            v = (T(e, n, xx - (yy >> 1) - 1) + T(e, n, xx - (yy >> 1)) + 1) >> 1;
                        }
                        else if (z >= 0)
                        {
                            v = (T(e, n, xx - (yy >> 1) - 2) + 2 * T(e, n, xx - (yy >> 1) - 1) + T(e, n, xx - (yy >> 1)) + 2) >> 2;
                        }
                        else if (z == -1)
                        {
                            v = (L(e, n, 0) + 2 * e[n] + T(e, n, 0) + 2) >> 2;
                        }
                        else
                        {
                            v = (L(e, n, yy - 2 * xx - 1) + 2 * L(e, n, yy - 2 * xx - 2) + L(e, n, yy - 2 * xx - 3) + 2) >> 2;
                        }

                        plane[pos + yy * stride + xx] = (byte)v;
                    }
                }

                break;

            case HorizontalDown:
                RequireAll(edges);

                for (int yy = 0; yy < n; yy++)
                {
                    for (int xx = 0; xx < n; xx++)
                    {
                        int z = 2 * yy - xx;
                        int v;

                        if (z >= 0 && (z & 1) == 0)
                        {
                            v = (L(e, n, yy - (xx >> 1) - 1) + L(e, n, yy - (xx >> 1)) + 1) >> 1;
                        }
                        else if (z >= 0)
                        {
                            v = (L(e, n, yy - (xx >> 1) - 2) + 2 * L(e, n, yy - (xx >> 1) - 1) + L(e, n, yy - (xx >> 1)) + 2) >> 2;
                        }
                        else if (z == -1)
                        {
                            v = (L(e, n, 0) + 2 * e[n] + T(e, n, 0) + 2) >> 2;
                        }
                        else
                        {
                            v = (T(e, n, xx - 2 * yy - 1) + 2 * T(e, n, xx - 2 * yy - 2) + T(e, n, xx - 2 * yy - 3) + 2) >> 2;
                        }

                        plane[pos + yy * stride + xx] = (byte)v;
                    }
                }

                break;

            case VerticalLeft:
                if (!edges.Top)
                {
                    throw new FormatParseException($"H.264: vertical-left intra prediction with no row above at ({x},{y}) size {n}.");
                }

                for (int yy = 0; yy < n; yy++)
                {
                    for (int xx = 0; xx < n; xx++)
                    {
                        int v = (yy & 1) == 0
                            ? (T(e, n, xx + (yy >> 1)) + T(e, n, xx + (yy >> 1) + 1) + 1) >> 1
                            : (T(e, n, xx + (yy >> 1)) + 2 * T(e, n, xx + (yy >> 1) + 1) + T(e, n, xx + (yy >> 1) + 2) + 2) >> 2;
                        plane[pos + yy * stride + xx] = (byte)v;
                    }
                }

                break;

            case HorizontalUp:
                if (!edges.Left)
                {
                    throw new FormatParseException($"H.264: horizontal-up intra prediction with no column to the left at ({x},{y}) size {n}.");
                }

                {
                    int limit = 2 * n - 3;

                    for (int yy = 0; yy < n; yy++)
                    {
                        for (int xx = 0; xx < n; xx++)
                        {
                            int z = xx + 2 * yy;
                            int v;

                            if (z < limit && (z & 1) == 0)
                            {
                                v = (L(e, n, yy + (xx >> 1)) + L(e, n, yy + (xx >> 1) + 1) + 1) >> 1;
                            }
                            else if (z < limit)
                            {
                                v = (L(e, n, yy + (xx >> 1)) + 2 * L(e, n, yy + (xx >> 1) + 1) + L(e, n, yy + (xx >> 1) + 2) + 2) >> 2;
                            }
                            else if (z == limit)
                            {
                                v = (L(e, n, n - 2) + 3 * L(e, n, n - 1) + 2) >> 2;
                            }
                            else
                            {
                                v = L(e, n, n - 1);
                            }

                            plane[pos + yy * stride + xx] = (byte)v;
                        }
                    }
                }

                break;

            default:
                throw new FormatParseException($"H.264: intra prediction mode {mode} does not exist.");
        }
    }

    private static void RequireAll(in Edges edges)
    {
        if (!edges.Top || !edges.Left || !edges.TopLeft)
        {
            throw new FormatParseException("H.264: a diagonal intra mode with a neighbour missing.");
        }
    }

    /// <summary>Intra_16x16 prediction, 8.3.3.</summary>
    public static void Predict16x16(byte[] plane, int stride, int x, int y, int mode, bool left, bool top, bool topLeft)
    {
        int pos = y * stride + x;

        switch (mode)
        {
            case 0: // vertical
                if (!top)
                {
                    throw new FormatParseException($"H.264: vertical 16x16 prediction with no row above at ({x},{y}).");
                }

                for (int yy = 0; yy < 16; yy++)
                {
                    Array.Copy(plane, pos - stride, plane, pos + yy * stride, 16);
                }

                break;

            case 1: // horizontal
                if (!left)
                {
                    throw new FormatParseException($"H.264: horizontal 16x16 prediction with no column to the left at ({x},{y}).");
                }

                for (int yy = 0; yy < 16; yy++)
                {
                    byte v = plane[pos + yy * stride - 1];
                    Array.Fill(plane, v, pos + yy * stride, 16);
                }

                break;

            case 2: // DC
                {
                    int sum = 0;
                    int dc;

                    if (top && left)
                    {
                        for (int i = 0; i < 16; i++)
                        {
                            sum += plane[pos - stride + i] + plane[pos + i * stride - 1];
                        }

                        dc = (sum + 16) >> 5;
                    }
                    else if (left)
                    {
                        for (int i = 0; i < 16; i++)
                        {
                            sum += plane[pos + i * stride - 1];
                        }

                        dc = (sum + 8) >> 4;
                    }
                    else if (top)
                    {
                        for (int i = 0; i < 16; i++)
                        {
                            sum += plane[pos - stride + i];
                        }

                        dc = (sum + 8) >> 4;
                    }
                    else
                    {
                        dc = 128;
                    }

                    for (int yy = 0; yy < 16; yy++)
                    {
                        Array.Fill(plane, (byte)dc, pos + yy * stride, 16);
                    }

                    break;
                }

            case 3: // plane
                {
                    if (!top || !left || !topLeft)
                    {
                        throw new FormatParseException($"H.264: plane 16x16 prediction with a neighbour missing at ({x},{y}).");
                    }

                    int h = 0;
                    int v = 0;
                    int topRow = pos - stride;

                    for (int i = 0; i < 8; i++)
                    {
                        h += (i + 1) * (plane[topRow + 8 + i] - plane[topRow + 6 - i]);
                        v += (i + 1) * (plane[pos + (8 + i) * stride - 1] - (6 - i >= 0 ? plane[pos + (6 - i) * stride - 1] : plane[topRow - 1]));
                    }

                    int a = 16 * (plane[pos + 15 * stride - 1] + plane[topRow + 15]);
                    int b = (5 * h + 32) >> 6;
                    int c = (5 * v + 32) >> 6;

                    for (int yy = 0; yy < 16; yy++)
                    {
                        for (int xx = 0; xx < 16; xx++)
                        {
                            plane[pos + yy * stride + xx] = Clip((a + b * (xx - 7) + c * (yy - 7) + 16) >> 5);
                        }
                    }

                    break;
                }

            default:
                throw new FormatParseException($"H.264: 16x16 prediction mode {mode} does not exist.");
        }
    }

    /// <summary>Chroma prediction for 4:2:0 and 4:2:2, 8.3.4.</summary>
    public static void PredictChroma(
        byte[] plane, int stride, int x, int y, int width, int height, int mode, bool left, bool top, bool topLeft)
    {
        int pos = y * stride + x;

        switch (mode)
        {
            case 0: // DC, per 4x4 block
                for (int by = 0; by < height; by += 4)
                {
                    for (int bx = 0; bx < width; bx += 4)
                    {
                        int sumTop = 0;
                        int sumLeft = 0;

                        if (top)
                        {
                            for (int i = 0; i < 4; i++)
                            {
                                sumTop += plane[pos - stride + bx + i];
                            }
                        }

                        if (left)
                        {
                            for (int i = 0; i < 4; i++)
                            {
                                sumLeft += plane[pos + (by + i) * stride - 1];
                            }
                        }

                        int dc;

                        if ((bx == 0 && by == 0) || (bx > 0 && by > 0))
                        {
                            dc = top && left ? (sumTop + sumLeft + 4) >> 3
                                : left ? (sumLeft + 2) >> 2
                                : top ? (sumTop + 2) >> 2
                                : 128;
                        }
                        else if (bx > 0)
                        {
                            dc = top ? (sumTop + 2) >> 2 : left ? (sumLeft + 2) >> 2 : 128;
                        }
                        else
                        {
                            dc = left ? (sumLeft + 2) >> 2 : top ? (sumTop + 2) >> 2 : 128;
                        }

                        for (int yy = 0; yy < 4; yy++)
                        {
                            Array.Fill(plane, (byte)dc, pos + (by + yy) * stride + bx, 4);
                        }
                    }
                }

                break;

            case 1: // horizontal
                if (!left)
                {
                    throw new FormatParseException($"H.264: horizontal chroma prediction with no column to the left at ({x},{y}).");
                }

                for (int yy = 0; yy < height; yy++)
                {
                    Array.Fill(plane, plane[pos + yy * stride - 1], pos + yy * stride, width);
                }

                break;

            case 2: // vertical
                if (!top)
                {
                    throw new FormatParseException($"H.264: vertical chroma prediction with no row above at ({x},{y}).");
                }

                for (int yy = 0; yy < height; yy++)
                {
                    Array.Copy(plane, pos - stride, plane, pos + yy * stride, width);
                }

                break;

            case 3: // plane
                {
                    if (!top || !left || !topLeft)
                    {
                        throw new FormatParseException($"H.264: plane chroma prediction with a neighbour missing at ({x},{y}).");
                    }

                    int xCF = width == 16 ? 4 : 0;
                    int yCF = height == 16 ? 4 : 0;
                    int h = 0;
                    int v = 0;
                    int topRow = pos - stride;

                    for (int i = 0; i <= 3 + xCF; i++)
                    {
                        int right = plane[topRow + 4 + xCF + i];
                        int leftIdx = 2 + xCF - i;
                        int leftSample = leftIdx >= 0 ? plane[topRow + leftIdx] : plane[topRow - 1];
                        h += (i + 1) * (right - leftSample);
                    }

                    for (int i = 0; i <= 3 + yCF; i++)
                    {
                        int below = plane[pos + (4 + yCF + i) * stride - 1];
                        int aboveIdx = 2 + yCF - i;
                        int above = aboveIdx >= 0 ? plane[pos + aboveIdx * stride - 1] : plane[topRow - 1];
                        v += (i + 1) * (below - above);
                    }

                    int a = 16 * (plane[pos + (height - 1) * stride - 1] + plane[topRow + width - 1]);
                    int b = ((34 - 29 * (width == 16 ? 1 : 0)) * h + 32) >> 6;
                    int c = ((34 - 29 * (height == 16 ? 1 : 0)) * v + 32) >> 6;

                    for (int yy = 0; yy < height; yy++)
                    {
                        for (int xx = 0; xx < width; xx++)
                        {
                            plane[pos + yy * stride + xx] = Clip((a + b * (xx - 3 - xCF) + c * (yy - 3 - yCF) + 16) >> 5);
                        }
                    }

                    break;
                }

            default:
                throw new FormatParseException($"H.264: chroma prediction mode {mode} does not exist.");
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte Clip(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
}
