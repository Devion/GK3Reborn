using System.Runtime.CompilerServices;

namespace GK3Reborn.Formats.Video.H264;

/// <summary>
/// Scaling and inverse transforms, 8.5: from coefficient levels to residual samples
/// added onto a prediction.
/// </summary>
/// <remarks>
/// All integer, exactly as the standard specifies them, because a decoder that is off by
/// one anywhere drifts further from the encoder's reconstruction with every predicted
/// frame. The tests compare whole clips against FFmpeg sample for sample.
/// </remarks>
internal static class Transform
{
    /// <summary>Scales a 4x4 block in place, 8.5.12.1; index 0 is skipped when its DC came from elsewhere.</summary>
    public static void Dequant4x4(int[] c, int offset, int[] levelScale, int qp, bool skipDc)
    {
        int shift = qp / 6;
        int start = skipDc ? 1 : 0;

        if (qp >= 24)
        {
            int s = shift - 4;

            for (int i = start; i < 16; i++)
            {
                c[offset + i] = (c[offset + i] * levelScale[i]) << s;
            }
        }
        else
        {
            int s = 4 - shift;
            int round = 1 << (3 - shift);

            for (int i = start; i < 16; i++)
            {
                c[offset + i] = (c[offset + i] * levelScale[i] + round) >> s;
            }
        }
    }

    /// <summary>Scales an 8x8 block in place, 8.5.13.1.</summary>
    public static void Dequant8x8(int[] c, int offset, int[] levelScale, int qp)
    {
        int shift = qp / 6;

        if (qp >= 36)
        {
            int s = shift - 6;

            for (int i = 0; i < 64; i++)
            {
                c[offset + i] = (c[offset + i] * levelScale[i]) << s;
            }
        }
        else
        {
            int s = 6 - shift;
            int round = 1 << (5 - shift);

            for (int i = 0; i < 64; i++)
            {
                c[offset + i] = (c[offset + i] * levelScale[i] + round) >> s;
            }
        }
    }

    /// <summary>Inverse Hadamard and scaling of the Intra_16x16 DC block, 8.5.10. In and out are raster 4x4.</summary>
    public static void LumaDc(int[] dc, int levelScale0, int qp)
    {
        Span<int> t = stackalloc int[16];

        for (int i = 0; i < 4; i++)
        {
            int c0 = dc[i * 4], c1 = dc[i * 4 + 1], c2 = dc[i * 4 + 2], c3 = dc[i * 4 + 3];
            int a = c0 + c1, b = c0 - c1, c = c2 + c3, d = c2 - c3;
            // Rows of the matrix [[1,1,1,1],[1,1,-1,-1],[1,-1,-1,1],[1,-1,1,-1]] applied to (c0..c3).
            t[i * 4] = a + c;
            t[i * 4 + 1] = a - c;
            t[i * 4 + 2] = b - d;
            t[i * 4 + 3] = b + d;
        }

        for (int j = 0; j < 4; j++)
        {
            int c0 = t[j], c1 = t[4 + j], c2 = t[8 + j], c3 = t[12 + j];
            int a = c0 + c1, b = c0 - c1, c = c2 + c3, d = c2 - c3;
            int f0 = a + c, f1 = a - c, f2 = b - d, f3 = b + d;

            if (qp >= 36)
            {
                int s = qp / 6 - 6;
                dc[j] = (f0 * levelScale0) << s;
                dc[4 + j] = (f1 * levelScale0) << s;
                dc[8 + j] = (f2 * levelScale0) << s;
                dc[12 + j] = (f3 * levelScale0) << s;
            }
            else
            {
                int s = 6 - qp / 6;
                int round = 1 << (5 - qp / 6);
                dc[j] = (f0 * levelScale0 + round) >> s;
                dc[4 + j] = (f1 * levelScale0 + round) >> s;
                dc[8 + j] = (f2 * levelScale0 + round) >> s;
                dc[12 + j] = (f3 * levelScale0 + round) >> s;
            }
        }
    }

    /// <summary>Inverse 2x2 transform and scaling of a 4:2:0 chroma DC block, 8.5.11.</summary>
    public static void ChromaDc420(int[] dc, int levelScale0, int qp)
    {
        int c0 = dc[0], c1 = dc[1], c2 = dc[2], c3 = dc[3];
        int f0 = c0 + c1 + c2 + c3;
        int f1 = c0 - c1 + c2 - c3;
        int f2 = c0 + c1 - c2 - c3;
        int f3 = c0 - c1 - c2 + c3;
        int shift = qp / 6;
        dc[0] = ((f0 * levelScale0) << shift) >> 5;
        dc[1] = ((f1 * levelScale0) << shift) >> 5;
        dc[2] = ((f2 * levelScale0) << shift) >> 5;
        dc[3] = ((f3 * levelScale0) << shift) >> 5;
    }

    /// <summary>Inverse 4x4 transform of scaled coefficients, added to the plane, 8.5.12.2.</summary>
    public static void Add4x4(byte[] plane, int stride, int pos, int[] c, int offset)
    {
        Span<int> t = stackalloc int[16];

        for (int i = 0; i < 4; i++)
        {
            int d0 = c[offset + i * 4], d1 = c[offset + i * 4 + 1], d2 = c[offset + i * 4 + 2], d3 = c[offset + i * 4 + 3];
            int e0 = d0 + d2;
            int e1 = d0 - d2;
            int e2 = (d1 >> 1) - d3;
            int e3 = d1 + (d3 >> 1);
            t[i * 4] = e0 + e3;
            t[i * 4 + 1] = e1 + e2;
            t[i * 4 + 2] = e1 - e2;
            t[i * 4 + 3] = e0 - e3;
        }

        for (int j = 0; j < 4; j++)
        {
            int d0 = t[j], d1 = t[4 + j], d2 = t[8 + j], d3 = t[12 + j];
            int e0 = d0 + d2;
            int e1 = d0 - d2;
            int e2 = (d1 >> 1) - d3;
            int e3 = d1 + (d3 >> 1);
            int p = pos + j;
            plane[p] = Clip(plane[p] + ((e0 + e3 + 32) >> 6));
            plane[p + stride] = Clip(plane[p + stride] + ((e1 + e2 + 32) >> 6));
            plane[p + 2 * stride] = Clip(plane[p + 2 * stride] + ((e1 - e2 + 32) >> 6));
            plane[p + 3 * stride] = Clip(plane[p + 3 * stride] + ((e0 - e3 + 32) >> 6));
        }
    }

    /// <summary>Inverse 8x8 transform of scaled coefficients, added to the plane, 8.5.13.2.</summary>
    public static void Add8x8(byte[] plane, int stride, int pos, int[] c, int offset)
    {
        Span<int> t = stackalloc int[64];

        for (int i = 0; i < 8; i++)
        {
            Butterfly8(c, offset + i * 8, 1, t, i * 8, 1);
        }

        Span<int> col = stackalloc int[8];

        for (int j = 0; j < 8; j++)
        {
            Butterfly8(t, j, 8, col, 0, 1);

            for (int i = 0; i < 8; i++)
            {
                int p = pos + i * stride + j;
                plane[p] = Clip(plane[p] + ((col[i] + 32) >> 6));
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Butterfly8(ReadOnlySpan<int> d, int o, int s, Span<int> r, int ro, int rs)
    {
        int d0 = d[o], d1 = d[o + s], d2 = d[o + 2 * s], d3 = d[o + 3 * s];
        int d4 = d[o + 4 * s], d5 = d[o + 5 * s], d6 = d[o + 6 * s], d7 = d[o + 7 * s];

        int a0 = d0 + d4;
        int a4 = d0 - d4;
        int a2 = (d2 >> 1) - d6;
        int a6 = d2 + (d6 >> 1);
        int b0 = a0 + a6;
        int b2 = a4 + a2;
        int b4 = a4 - a2;
        int b6 = a0 - a6;
        int a1 = -d3 + d5 - d7 - (d7 >> 1);
        int a3 = d1 + d7 - d3 - (d3 >> 1);
        int a5 = -d1 + d7 + d5 + (d5 >> 1);
        int a7 = d3 + d5 + d1 + (d1 >> 1);
        int b1 = a1 + (a7 >> 2);
        int b7 = a7 - (a1 >> 2);
        int b3 = a3 + (a5 >> 2);
        int b5 = (a3 >> 2) - a5;

        r[ro] = b0 + b7;
        r[ro + rs] = b2 + b5;
        r[ro + 2 * rs] = b4 + b3;
        r[ro + 3 * rs] = b6 + b1;
        r[ro + 4 * rs] = b6 - b1;
        r[ro + 5 * rs] = b4 - b3;
        r[ro + 6 * rs] = b2 - b5;
        r[ro + 7 * rs] = b0 - b7;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Butterfly8(int[] d, int o, int s, Span<int> r, int ro, int rs) =>
        Butterfly8(d.AsSpan(), o, s, r, ro, rs);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte Clip(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
}
