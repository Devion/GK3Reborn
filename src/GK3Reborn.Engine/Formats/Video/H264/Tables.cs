using System.Runtime.CompilerServices;

namespace GK3Reborn.Formats.Video.H264;

/// <summary>One code of a variable-length table: its bits, how many, and what it means.</summary>
internal readonly record struct VlcCode(int Code, int Length, int Value);

/// <summary>
/// The small tables of the standard: scans, quantiser adjustments, block geometry.
/// </summary>
/// <remarks>
/// The large ones — CABAC initialisation and the CAVLC codes — are in the generated half
/// of this class. Everything here is short enough to check against the standard by eye.
/// </remarks>
internal static partial class Tables
{
    /// <summary>Frame zigzag scan for 4x4 blocks: scan position to raster index, 8.5.6.</summary>
    public static readonly byte[] Zigzag4x4 = [0, 1, 4, 8, 5, 2, 3, 6, 9, 12, 13, 10, 7, 11, 14, 15];

    /// <summary>Frame zigzag scan for 8x8 blocks: scan position to raster index, 8.5.7.</summary>
    public static readonly byte[] Zigzag8x8 =
    [
        0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5, 12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51, 58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63,
    ];

    private static readonly int[,] NormAdjust4 =
    {
        { 10, 16, 13 }, { 11, 18, 14 }, { 13, 20, 16 }, { 14, 23, 18 }, { 16, 25, 20 }, { 18, 29, 23 },
    };

    private static readonly int[,] NormAdjust8 =
    {
        { 20, 18, 32, 19, 25, 24 }, { 22, 19, 35, 21, 28, 26 }, { 26, 23, 42, 24, 33, 31 },
        { 28, 25, 45, 26, 35, 33 }, { 32, 28, 51, 30, 40, 38 }, { 36, 32, 58, 34, 46, 43 },
    };

    /// <summary>normAdjust4x4(m, i, j) of 8.5.9 for a raster position.</summary>
    public static int NormAdjust4x4(int m, int raster)
    {
        int i = raster >> 2;
        int j = raster & 3;
        int kind = (i & 1) == 0 && (j & 1) == 0 ? 0 : (i & 1) == 1 && (j & 1) == 1 ? 1 : 2;
        return NormAdjust4[m, kind];
    }

    /// <summary>normAdjust8x8(m, i, j) of 8.5.9 for a raster position.</summary>
    public static int NormAdjust8x8(int m, int raster)
    {
        int i = raster >> 3;
        int j = raster & 7;
        int kind;

        if ((i & 3) == 0 && (j & 3) == 0)
        {
            kind = 0;
        }
        else if ((i & 1) == 1 && (j & 1) == 1)
        {
            kind = 1;
        }
        else if ((i & 3) == 2 && (j & 3) == 2)
        {
            kind = 2;
        }
        else if (((i & 3) == 0 && (j & 1) == 1) || ((i & 1) == 1 && (j & 3) == 0))
        {
            kind = 3;
        }
        else if (((i & 3) == 0 && (j & 3) == 2) || ((i & 3) == 2 && (j & 3) == 0))
        {
            kind = 4;
        }
        else
        {
            kind = 5;
        }

        return NormAdjust8[m, kind];
    }

    /// <summary>Default_4x4_Intra, Table 7-3, in zigzag order.</summary>
    public static readonly int[] Default4x4Intra = [6, 13, 13, 20, 20, 20, 28, 28, 28, 28, 32, 32, 32, 37, 37, 42];

    /// <summary>Default_4x4_Inter, Table 7-3, in zigzag order.</summary>
    public static readonly int[] Default4x4Inter = [10, 14, 14, 20, 20, 20, 24, 24, 24, 24, 27, 27, 27, 30, 30, 34];

    /// <summary>Default_8x8_Intra, Table 7-4, in zigzag order.</summary>
    public static readonly int[] Default8x8Intra =
    [
        6, 10, 10, 13, 11, 13, 16, 16, 16, 16, 18, 18, 18, 18, 18, 23, 23, 23, 23, 23, 23, 25, 25, 25, 25, 25, 25, 25, 27, 27, 27, 27,
        27, 27, 27, 27, 29, 29, 29, 29, 29, 29, 29, 31, 31, 31, 31, 31, 31, 33, 33, 33, 33, 33, 36, 36, 36, 36, 38, 38, 38, 40, 40, 42,
    ];

    /// <summary>Default_8x8_Inter, Table 7-4, in zigzag order.</summary>
    public static readonly int[] Default8x8Inter =
    [
        9, 13, 13, 15, 13, 15, 17, 17, 17, 17, 19, 19, 19, 19, 19, 21, 21, 21, 21, 21, 21, 22, 22, 22, 22, 22, 22, 22, 24, 24, 24, 24,
        24, 24, 24, 24, 25, 25, 25, 25, 25, 25, 25, 27, 27, 27, 27, 27, 27, 28, 28, 28, 28, 28, 30, 30, 30, 30, 32, 32, 32, 33, 33, 35,
    ];

    private static readonly byte[] ChromaQpTable =
    [
        29, 30, 31, 32, 32, 33, 34, 34, 35, 35, 36, 36, 37, 37, 37, 38, 38, 38, 39, 39, 39, 39,
    ];

    /// <summary>QPc from qPI, Table 8-15.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ChromaQp(int qpi) => qpi < 30 ? qpi : ChromaQpTable[Math.Min(qpi, 51) - 30];

    /// <summary>alpha' of Table 8-16, indexed by indexA.</summary>
    public static readonly byte[] Alpha =
    [
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 4, 4, 5, 6, 7, 8, 9, 10, 12, 13, 15, 17, 20, 22, 25, 28,
        32, 36, 40, 45, 50, 56, 63, 71, 80, 90, 101, 113, 127, 144, 162, 182, 203, 226, 255, 255,
    ];

    /// <summary>beta' of Table 8-16, indexed by indexB.</summary>
    public static readonly byte[] Beta =
    [
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 6, 6, 7, 7, 8, 8,
        9, 9, 10, 10, 11, 11, 12, 12, 13, 13, 14, 14, 15, 15, 16, 16, 17, 17, 18, 18,
    ];

    /// <summary>tC0' of Table 8-17, indexed by [bS - 1][indexA].</summary>
    public static readonly byte[][] Tc0 =
    [
        [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 4, 4, 4, 5, 6, 6, 7, 8, 9, 10, 11, 13],
        [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 4, 4, 5, 5, 6, 7, 8, 8, 10, 11, 12, 13, 15, 17],
        [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 4, 4, 4, 5, 6, 6, 7, 8, 9, 10, 11, 13, 14, 16, 18, 20, 23, 25],
    ];

    /// <summary>x offset in pixels of luma 4x4 block blkIdx (6.4.3).</summary>
    public static readonly byte[] Blk4x4X = [0, 4, 0, 4, 8, 12, 8, 12, 0, 4, 0, 4, 8, 12, 8, 12];

    /// <summary>y offset in pixels of luma 4x4 block blkIdx.</summary>
    public static readonly byte[] Blk4x4Y = [0, 0, 4, 4, 0, 0, 4, 4, 8, 8, 12, 12, 8, 8, 12, 12];

    /// <summary>Which 4x4 block index sits at raster position (y / 4) * 4 + (x / 4).</summary>
    public static readonly byte[] RasterToBlk4x4 = [0, 1, 4, 5, 2, 3, 6, 7, 8, 9, 12, 13, 10, 11, 14, 15];

    /// <summary>The 4x4 block to the left of each, or -1 + the index in the left neighbour (as 16 + idx).</summary>
    public static readonly sbyte[] LeftBlk4x4 = [16 + 5, 0, 16 + 7, 2, 1, 4, 3, 6, 16 + 13, 8, 16 + 15, 10, 9, 12, 11, 14];

    /// <summary>The 4x4 block above each, or 16 + the index in the top neighbour.</summary>
    public static readonly sbyte[] TopBlk4x4 = [16 + 10, 16 + 11, 0, 1, 16 + 14, 16 + 15, 4, 5, 2, 3, 8, 9, 6, 7, 12, 13];

    /// <summary>coded_block_pattern from codeNum for ChromaArrayType 1 or 2, intra, Table 9-4.</summary>
    public static readonly byte[] CbpIntraColour =
    [
        47, 31, 15, 0, 23, 27, 29, 30, 7, 11, 13, 14, 39, 43, 45, 46, 16, 3, 5, 10, 12, 19, 21, 26, 28, 35, 37, 42, 44, 1, 2, 4,
        8, 17, 18, 20, 24, 6, 9, 22, 25, 32, 33, 34, 36, 40, 38, 41,
    ];

    /// <summary>coded_block_pattern from codeNum for ChromaArrayType 1 or 2, inter, Table 9-4.</summary>
    public static readonly byte[] CbpInterColour =
    [
        0, 16, 1, 2, 4, 8, 32, 3, 5, 10, 12, 15, 47, 7, 11, 13, 14, 6, 9, 31, 35, 37, 42, 44, 33, 34, 36, 40, 39, 43, 45, 46,
        17, 18, 20, 24, 19, 21, 26, 28, 23, 27, 29, 30, 22, 25, 38, 41,
    ];

    /// <summary>coded_block_pattern from codeNum for ChromaArrayType 0 or 3, intra, Table 9-4.</summary>
    public static readonly byte[] CbpIntraMono = [15, 0, 7, 11, 13, 14, 3, 5, 10, 12, 1, 2, 4, 8, 6, 9];

    /// <summary>coded_block_pattern from codeNum for ChromaArrayType 0 or 3, inter, Table 9-4.</summary>
    public static readonly byte[] CbpInterMono = [0, 1, 2, 4, 8, 3, 5, 10, 12, 15, 7, 11, 13, 14, 6, 9];

    /// <summary>ctxIdxInc for significant_coeff_flag in 8x8 blocks (frame), Table 9-43.</summary>
    public static readonly byte[] SigCoeff8x8 =
    [
        0, 1, 2, 3, 4, 5, 5, 4, 4, 3, 3, 4, 4, 4, 5, 5, 4, 4, 4, 4, 3, 3, 6, 7, 7, 7, 8, 9, 10, 9, 8, 7,
        7, 6, 11, 12, 13, 11, 6, 7, 8, 9, 14, 10, 9, 8, 6, 11, 12, 13, 11, 6, 9, 14, 10, 9, 11, 12, 13, 11, 14, 10, 12,
    ];

    /// <summary>ctxIdxInc for last_significant_coeff_flag in 8x8 blocks, Table 9-43.</summary>
    public static readonly byte[] LastCoeff8x8 =
    [
        0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2,
        3, 3, 3, 3, 3, 3, 3, 3, 4, 4, 4, 4, 4, 4, 4, 4, 5, 5, 5, 5, 6, 6, 6, 6, 7, 7, 7, 7, 8, 8, 8,
    ];
}
