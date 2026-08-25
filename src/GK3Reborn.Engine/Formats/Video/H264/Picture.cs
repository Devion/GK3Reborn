using System.Runtime.CompilerServices;

namespace GK3Reborn.Formats.Video.H264;

/// <summary>Bits kept per macroblock for the neighbours that come after it.</summary>
internal static class MbFlag
{
    public const byte Available = 1;      // decoded in this picture
    public const byte Intra = 2;
    public const byte Skip = 4;
    public const byte IntraNxN = 8;       // I_4x4 or I_8x8: has real intra modes
    public const byte Pcm = 16;
    public const byte Direct16x16 = 32;   // B_Direct_16x16 (for the mb_type context)
    public const byte Transform8x8 = 64;
    public const byte Intra16x16 = 128;
}

/// <summary>
/// A decoded picture: its three planes and everything later pictures and later
/// macroblocks need to know about how it was coded.
/// </summary>
/// <remarks>
/// <para>
/// Motion vectors, reference choices and coefficient counts are kept per 4x4 block in
/// raster order within the macroblock — <c>(y / 4) * 4 + x / 4</c> — rather than in the
/// standard's block-index order, because prediction looks up "the block to the left" far
/// more often than it looks up "block 5", and raster order makes that an offset of one.
/// </para>
/// <para>
/// Everything is flat arrays indexed by macroblock address so the decoder allocates
/// nothing per macroblock, and the arrays are reused when the picture is.
/// </para>
/// </remarks>
internal sealed class Picture
{
    private static int _serials;

    public Picture(int widthMbs, int heightMbs, int chromaFormat)
    {
        WidthMbs = widthMbs;
        HeightMbs = heightMbs;
        MbCount = widthMbs * heightMbs;
        ChromaFormat = chromaFormat;
        Width = widthMbs * 16;
        Height = heightMbs * 16;
        Stride = Width;
        Y = new byte[Width * Height];

        if (chromaFormat == 0)
        {
            ChromaWidth = ChromaHeight = ChromaStride = 0;
            Cb = Cr = [];
        }
        else
        {
            ChromaWidth = chromaFormat == 3 ? Width : Width / 2;
            ChromaHeight = chromaFormat == 1 ? Height / 2 : Height;
            ChromaStride = ChromaWidth;
            Cb = new byte[ChromaWidth * ChromaHeight];
            Cr = new byte[ChromaWidth * ChromaHeight];
        }

        int blocks = MbCount * 16;
        MbFlags = new byte[MbCount];
        SliceId = new int[MbCount];
        QpY = new sbyte[MbCount];
        QpCb = new sbyte[MbCount];
        QpCr = new sbyte[MbCount];
        Cbp = new byte[MbCount];
        IntraChromaPredMode = new byte[MbCount];
        CbfLuma = new int[MbCount];
        CbfCb = new int[MbCount];
        CbfCr = new int[MbCount];
        NonZeroBits = new int[MbCount];
        Mv0 = new int[blocks];
        Mv1 = new int[blocks];
        Ref0 = new sbyte[blocks];
        Ref1 = new sbyte[blocks];
        RefPic0 = new int[blocks];
        RefPic1 = new int[blocks];
        RefPoc0 = new int[blocks];
        RefPoc1 = new int[blocks];
        IntraModes = new sbyte[blocks];
        Nnz = new byte[MbCount * 48];
        Mvd0 = new int[blocks];
        Mvd1 = new int[blocks];
        Direct8x8 = new byte[MbCount];
        Serial = Interlocked.Increment(ref _serials);
    }

    public int WidthMbs { get; }
    public int HeightMbs { get; }
    public int MbCount { get; }
    public int ChromaFormat { get; }
    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public int ChromaWidth { get; }
    public int ChromaHeight { get; }
    public int ChromaStride { get; }
    public byte[] Y { get; }
    public byte[] Cb { get; }
    public byte[] Cr { get; }

    /// <summary>A number no other picture of this decoder has, for telling references apart.</summary>
    public int Serial { get; private set; }

    // ---- what the decoded picture buffer tracks ----------------------------------------

    public int Poc;
    public int FrameNum;
    public int LongTermFrameIdx;
    public bool IsShortTermRef;
    public bool IsLongTermRef;
    public bool NeededForOutput;
    public bool Idr;
    public bool InUse;

    /// <summary>Whatever the caller attached to the access unit that produced this picture.</summary>
    public long Tag;

    public bool IsReference => IsShortTermRef || IsLongTermRef;

    // ---- per macroblock ------------------------------------------------------------------

    public byte[] MbFlags;
    public int[] SliceId;
    public sbyte[] QpY;
    public sbyte[] QpCb;
    public sbyte[] QpCr;
    public byte[] Cbp;
    public byte[] IntraChromaPredMode;

    /// <summary>coded_block_flag bits: 0..15 the 4x4 blocks in raster order, 16 the DC.</summary>
    public int[] CbfLuma;
    public int[] CbfCb;
    public int[] CbfCr;

    /// <summary>
    /// One bit per luma 4x4 block (raster) that has coefficients, as the deblocking filter
    /// sees it: an 8x8 transform block with any coefficient marks all four of its 4x4s.
    /// Bits 16..31 do the same for Cb and Cr together when they are coded like luma.
    /// </summary>
    public int[] NonZeroBits;

    // ---- per 4x4 block, mb * 16 + raster ---------------------------------------------------

    /// <summary>Motion vectors, packed: x in the low 16 bits, y in the high 16.</summary>
    public int[] Mv0;
    public int[] Mv1;

    /// <summary>Reference indices, or -1 when the list is not used by that block.</summary>
    public sbyte[] Ref0;
    public sbyte[] Ref1;

    /// <summary>The serial of the picture the reference index resolved to, or 0.</summary>
    public int[] RefPic0;
    public int[] RefPic1;

    /// <summary>The POC of that picture, for temporal direct prediction from this picture.</summary>
    public int[] RefPoc0;
    public int[] RefPoc1;

    /// <summary>Intra 4x4 / 8x8 prediction modes, or 2 (DC) for anything else.</summary>
    public sbyte[] IntraModes;

    /// <summary>
    /// Total coefficient counts per 4x4 block: luma in 0..15, Cb in 16..31, Cr in 32..47,
    /// each in raster order. For 4:2:0 only the first four chroma entries are used.
    /// </summary>
    public byte[] Nnz;

    /// <summary>Absolute motion vector differences for CABAC contexts, packed like the vectors.</summary>
    public int[] Mvd0;
    public int[] Mvd1;

    /// <summary>One bit per 8x8 partition that was direct-predicted, for the ref_idx contexts.</summary>
    public byte[] Direct8x8;

    /// <summary>The slices of this picture, by slice id, for the deblocking filter.</summary>
    public List<SliceHeader> Slices { get; } = [];

    /// <summary>Makes the picture ready for a new decode.</summary>
    public void Reset()
    {
        Array.Clear(MbFlags);
        Array.Clear(SliceId);
        Slices.Clear();
        IsShortTermRef = false;
        IsLongTermRef = false;
        NeededForOutput = false;
        Idr = false;
        LongTermFrameIdx = 0;
        Serial = Interlocked.Increment(ref _serials);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int PackMv(int x, int y) => (x & 0xFFFF) | (y << 16);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int MvX(int packed) => (short)packed;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int MvY(int packed) => packed >> 16;
}
