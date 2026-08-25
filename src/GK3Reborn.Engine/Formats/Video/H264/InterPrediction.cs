using System.Runtime.CompilerServices;

namespace GK3Reborn.Formats.Video.H264;

/// <summary>
/// Fractional-sample interpolation, 8.4.2.2, and weighted sample prediction, 8.4.2.3.
/// </summary>
/// <remarks>
/// <para>
/// A block's reference window is copied into a local buffer with its coordinates clamped
/// to the picture, which is how the standard defines reads beyond the edge; the block
/// itself is then computed from that window without further bounds checks. Blocks whose
/// window lies entirely inside the picture, which is nearly all of them, skip the clamp.
/// </para>
/// <para>
/// Luma is quarter-sample with the six-tap filter, chroma of a 4:2:0 picture eighth-sample
/// bilinear; 4:4:4 chroma is interpolated like luma, as 8.4.2.2 requires.
/// </para>
/// </remarks>
internal static class InterPrediction
{
    private const int MaxBlock = 16;
    private const int WindowSize = (MaxBlock + 5) * (MaxBlock + 5);

    [ThreadStatic]
    private static int[]? _window;

    [ThreadStatic]
    private static int[]? _rows;

    [ThreadStatic]
    private static int[]? _cols;

    /// <summary>Fetches a (w + 5) x (h + 5) window starting five samples up and left of the block.</summary>
    private static int[] Window(byte[] plane, int stride, int width, int height, int x0, int y0, int w, int h)
    {
        int[] win = _window ??= new int[WindowSize];
        int ww = w + 5;
        int wh = h + 5;
        int sx = x0 - 2;
        int sy = y0 - 2;

        if (sx >= 0 && sy >= 0 && sx + ww <= width && sy + wh <= height)
        {
            for (int j = 0; j < wh; j++)
            {
                int src = (sy + j) * stride + sx;
                int dst = j * ww;

                for (int i = 0; i < ww; i++)
                {
                    win[dst + i] = plane[src + i];
                }
            }
        }
        else
        {
            for (int j = 0; j < wh; j++)
            {
                int yy = Math.Clamp(sy + j, 0, height - 1) * stride;
                int dst = j * ww;

                for (int i = 0; i < ww; i++)
                {
                    int xx = Math.Clamp(sx + i, 0, width - 1);
                    win[dst + i] = plane[yy + xx];
                }
            }
        }

        return win;
    }

    /// <summary>
    /// Predicts a luma-like block of w x h at (x, y) with quarter-sample vector (mvx, mvy)
    /// into <paramref name="dst"/>.
    /// </summary>
    public static void Luma(
        byte[] plane, int stride, int width, int height,
        int x, int y, int mvx, int mvy, int w, int h,
        byte[] dst, int dstOffset, int dstStride)
    {
        int xInt = x + (mvx >> 2);
        int yInt = y + (mvy >> 2);
        int fx = mvx & 3;
        int fy = mvy & 3;
        int[] win = Window(plane, stride, width, height, xInt, yInt, w, h);
        int ww = w + 5;

        // G(i, j) is win[(j + 2) * ww + i + 2].
        if (fx == 0 && fy == 0)
        {
            for (int j = 0; j < h; j++)
            {
                int src = (j + 2) * ww + 2;
                int d = dstOffset + j * dstStride;

                for (int i = 0; i < w; i++)
                {
                    dst[d + i] = (byte)win[src + i];
                }
            }

            return;
        }

        int[] rows = _rows ??= new int[WindowSize]; // b1: horizontal six-tap at integer rows, unclipped
        int[] cols = _cols ??= new int[WindowSize]; // h1: vertical six-tap at integer columns, unclipped

        bool needRows = fx != 0;      // b, s, j
        bool needCols = fy != 0;      // h, m, j
        bool needJ = fx != 0 && fy != 0 && (fx == 2 || fy == 2);

        if (needRows)
        {
            // Rows -2 .. h + 2 (needed for j), columns 0 .. w (column w gives nothing for b, but is harmless).
            int rowCount = needJ ? h + 5 : h + 1;
            int rowStart = needJ ? 0 : 2;

            for (int j = rowStart; j < rowStart + rowCount && j < h + 5; j++)
            {
                int src = j * ww;

                for (int i = 0; i < w; i++)
                {
                    int p = src + i;
                    rows[j * ww + i] = win[p] - 5 * win[p + 1] + 20 * win[p + 2] + 20 * win[p + 3] - 5 * win[p + 4] + win[p + 5];
                }
            }
        }

        if (needCols)
        {
            // Columns 0 .. w (column w is m), rows 0 .. h - 1.
            for (int j = 0; j < h; j++)
            {
                for (int i = 0; i <= w; i++)
                {
                    int p = j * ww + i + 2;
                    cols[j * ww + i] = win[p] - 5 * win[p + ww] + 20 * win[p + 2 * ww] + 20 * win[p + 3 * ww] - 5 * win[p + 4 * ww] + win[p + 5 * ww];
                }
            }
        }

        for (int j = 0; j < h; j++)
        {
            int d = dstOffset + j * dstStride;

            for (int i = 0; i < w; i++)
            {
                int g = win[(j + 2) * ww + i + 2];
                int value;

                switch (fy * 4 + fx)
                {
                    case 1: // a
                        value = (g + B(rows, ww, i, j) + 1) >> 1;
                        break;
                    case 2: // b
                        value = B(rows, ww, i, j);
                        break;
                    case 3: // c
                        value = (win[(j + 2) * ww + i + 3] + B(rows, ww, i, j) + 1) >> 1;
                        break;
                    case 4: // d
                        value = (g + H(cols, ww, i, j) + 1) >> 1;
                        break;
                    case 8: // h
                        value = H(cols, ww, i, j);
                        break;
                    case 12: // n
                        value = (win[(j + 3) * ww + i + 2] + H(cols, ww, i, j) + 1) >> 1;
                        break;
                    case 5: // e
                        value = (B(rows, ww, i, j) + H(cols, ww, i, j) + 1) >> 1;
                        break;
                    case 7: // g
                        value = (B(rows, ww, i, j) + H(cols, ww, i + 1, j) + 1) >> 1;
                        break;
                    case 13: // p
                        value = (H(cols, ww, i, j) + B(rows, ww, i, j + 1) + 1) >> 1;
                        break;
                    case 15: // r
                        value = (H(cols, ww, i + 1, j) + B(rows, ww, i, j + 1) + 1) >> 1;
                        break;
                    case 10: // j
                        value = J(rows, ww, i, j);
                        break;
                    case 6: // f
                        value = (B(rows, ww, i, j) + J(rows, ww, i, j) + 1) >> 1;
                        break;
                    case 14: // q
                        value = (J(rows, ww, i, j) + B(rows, ww, i, j + 1) + 1) >> 1;
                        break;
                    case 9: // i
                        value = (H(cols, ww, i, j) + J(rows, ww, i, j) + 1) >> 1;
                        break;
                    case 11: // k
                        value = (J(rows, ww, i, j) + H(cols, ww, i + 1, j) + 1) >> 1;
                        break;
                    default:
                        value = g;
                        break;
                }

                dst[d + i] = (byte)value;
            }
        }
    }

    /// <summary>b at (i, j): the clipped half-sample between G(i, j) and G(i + 1, j).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int B(int[] rows, int ww, int i, int j) => Clip((rows[(j + 2) * ww + i] + 16) >> 5);

    /// <summary>h at (i, j): the clipped half-sample between G(i, j) and G(i, j + 1).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int H(int[] cols, int ww, int i, int j) => Clip((cols[j * ww + i] + 16) >> 5);

    /// <summary>j at (i, j): the centre half-sample, from the unclipped horizontal intermediates.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int J(int[] rows, int ww, int i, int j)
    {
        int p = j * ww + i;
        int j1 = rows[p] - 5 * rows[p + ww] + 20 * rows[p + 2 * ww] + 20 * rows[p + 3 * ww] - 5 * rows[p + 4 * ww] + rows[p + 5 * ww];
        return Clip((j1 + 512) >> 10);
    }

    /// <summary>Predicts a 4:2:0 chroma block with an eighth-sample vector, 8.4.2.2.2.</summary>
    public static void Chroma(
        byte[] plane, int stride, int width, int height,
        int x, int y, int mvx, int mvy, int w, int h,
        byte[] dst, int dstOffset, int dstStride)
    {
        int xInt = x + (mvx >> 3);
        int yInt = y + (mvy >> 3);
        int fx = mvx & 7;
        int fy = mvy & 7;
        int w00 = (8 - fx) * (8 - fy);
        int w10 = fx * (8 - fy);
        int w01 = (8 - fx) * fy;
        int w11 = fx * fy;

        bool inside = xInt >= 0 && yInt >= 0 && xInt + w + 1 <= width && yInt + h + 1 <= height;

        if (inside)
        {
            for (int j = 0; j < h; j++)
            {
                int row = (yInt + j) * stride + xInt;
                int d = dstOffset + j * dstStride;

                for (int i = 0; i < w; i++)
                {
                    int a = plane[row + i];
                    int b = plane[row + i + 1];
                    int c = plane[row + stride + i];
                    int dd = plane[row + stride + i + 1];
                    dst[d + i] = (byte)((w00 * a + w10 * b + w01 * c + w11 * dd + 32) >> 6);
                }
            }
        }
        else
        {
            for (int j = 0; j < h; j++)
            {
                int y0 = Math.Clamp(yInt + j, 0, height - 1) * stride;
                int y1 = Math.Clamp(yInt + j + 1, 0, height - 1) * stride;
                int d = dstOffset + j * dstStride;

                for (int i = 0; i < w; i++)
                {
                    int x0 = Math.Clamp(xInt + i, 0, width - 1);
                    int x1 = Math.Clamp(xInt + i + 1, 0, width - 1);
                    int a = plane[y0 + x0];
                    int b = plane[y0 + x1];
                    int c = plane[y1 + x0];
                    int dd = plane[y1 + x1];
                    dst[d + i] = (byte)((w00 * a + w10 * b + w01 * c + w11 * dd + 32) >> 6);
                }
            }
        }
    }

    /// <summary>Default weighted prediction of two lists: the rounded average, 8.4.2.3.1.</summary>
    public static void Average(byte[] p0, byte[] p1, int w, int h, byte[] plane, int stride, int pos)
    {
        for (int j = 0; j < h; j++)
        {
            int s = j * MaxBlock;
            int d = pos + j * stride;

            for (int i = 0; i < w; i++)
            {
                plane[d + i] = (byte)((p0[s + i] + p1[s + i] + 1) >> 1);
            }
        }
    }

    /// <summary>Copies a single-list prediction into the picture.</summary>
    public static void Copy(byte[] p, int w, int h, byte[] plane, int stride, int pos)
    {
        for (int j = 0; j < h; j++)
        {
            Array.Copy(p, j * MaxBlock, plane, pos + j * stride, w);
        }
    }

    /// <summary>Explicit weighted prediction from one list, 8.4.2.3.2.</summary>
    public static void Weigh(byte[] p, int w, int h, int weight, int offset, int logWd, byte[] plane, int stride, int pos)
    {
        int round = logWd >= 1 ? 1 << (logWd - 1) : 0;

        for (int j = 0; j < h; j++)
        {
            int s = j * MaxBlock;
            int d = pos + j * stride;

            for (int i = 0; i < w; i++)
            {
                int v = logWd >= 1 ? ((p[s + i] * weight + round) >> logWd) + offset : p[s + i] * weight + offset;
                plane[d + i] = Clip(v);
            }
        }
    }

    /// <summary>Explicit or implicit weighted prediction from two lists, 8.4.2.3.2.</summary>
    public static void WeighBi(
        byte[] p0, byte[] p1, int w, int h, int w0, int w1, int o0, int o1, int logWd,
        byte[] plane, int stride, int pos)
    {
        int round = 1 << logWd;
        int offset = (o0 + o1 + 1) >> 1;

        for (int j = 0; j < h; j++)
        {
            int s = j * MaxBlock;
            int d = pos + j * stride;

            for (int i = 0; i < w; i++)
            {
                plane[d + i] = Clip(((p0[s + i] * w0 + p1[s + i] * w1 + round) >> (logWd + 1)) + offset);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte Clip(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
}
