namespace GK3Reborn.Formats.Video.H264;

/// <summary>How a macroblock is split for inter prediction.</summary>
internal enum PartitionShape
{
    Part16x16,
    Part16x8,
    Part8x16,
    Part8x8,
}

/// <summary>The residual of one colour component of a macroblock.</summary>
/// <remarks>
/// Coefficients are kept in raster order within each block — already un-zigzagged — so
/// the transform reads them straight. <see cref="Coeff4x4"/> holds sixteen 4x4 blocks in
/// raster block order (or the chroma blocks of a 4:2:0 macroblock), <see cref="Coeff8x8"/>
/// four 8x8 blocks, and <see cref="Dc"/> the separately coded DC terms.
/// </remarks>
internal sealed class Residual
{
    public readonly int[] Coeff4x4 = new int[256];
    public readonly int[] Coeff8x8 = new int[256];
    public readonly int[] Dc = new int[16];

    /// <summary>coded_block_flag per 4x4 block (bits 0..15, raster) and DC (bit 16).</summary>
    public int Cbf;

    /// <summary>Whether each block has coefficients: bits 0..15 for 4x4 blocks in raster order, or 0..3 for 8x8 blocks.</summary>
    public int NonZero4x4;
    public int NonZero8x8;
    public bool HasDc;

    /// <summary>Total coefficients per 4x4 block in raster order, for CAVLC's neighbours.</summary>
    public readonly byte[] Nnz = new byte[16];

    public void Clear()
    {
        Array.Clear(Coeff4x4);
        Array.Clear(Coeff8x8);
        Array.Clear(Dc);
        Array.Clear(Nnz);
        Cbf = 0;
        NonZero4x4 = 0;
        NonZero8x8 = 0;
        HasDc = false;
    }
}

/// <summary>
/// Everything parsed for one macroblock, before it is reconstructed.
/// </summary>
/// <remarks>
/// A scratch object the slice decoder owns and reuses: parsing fills it, reconstruction
/// reads it, the next macroblock overwrites it. Keeping the two halves apart is what lets
/// CABAC and CAVLC share every line of code after the entropy decoder.
/// </remarks>
internal sealed class Macroblock
{
    public int Addr;
    public int X;
    public int Y;

    public bool Skip;
    public bool Intra;
    public bool Pcm;
    public bool Intra16x16;
    public bool IntraNxN;
    public bool Transform8x8;
    public bool Direct16x16;

    public int Intra16x16PredMode;
    public int IntraChromaPredMode;

    /// <summary>Intra 4x4 or 8x8 prediction modes per 4x4 block, raster order.</summary>
    public readonly sbyte[] IntraModes = new sbyte[16];

    public int Cbp;
    public int CbpLuma => Cbp & 15;
    public int CbpChroma => Cbp >> 4;
    public int QpDelta;
    public int QpY;
    public int QpCb;
    public int QpCr;

    public PartitionShape Shape;
    public int NumParts;

    /// <summary>sub_mb_type per 8x8 partition, as coded (P: 0..3, B: 0..12).</summary>
    public readonly int[] SubMbType = new int[4];

    /// <summary>Prediction lists used per 8x8 partition: bit 0 for L0, bit 1 for L1.</summary>
    public readonly int[] PredFlags = new int[4];

    /// <summary>Whether each 8x8 partition is direct-predicted (B only).</summary>
    public readonly bool[] SubDirect = new bool[4];

    /// <summary>Reference indices per list per 8x8 partition.</summary>
    public readonly int[] RefIdx0 = new int[4];
    public readonly int[] RefIdx1 = new int[4];

    /// <summary>Motion vector differences per list per 4x4 block, raster order, packed.</summary>
    public readonly int[] Mvd0 = new int[16];
    public readonly int[] Mvd1 = new int[16];

    /// <summary>The decoded vectors and references per 4x4 block, raster order, after prediction.</summary>
    public readonly int[] Mv0 = new int[16];
    public readonly int[] Mv1 = new int[16];
    public readonly sbyte[] Ref0 = new sbyte[16];
    public readonly sbyte[] Ref1 = new sbyte[16];

    public readonly Residual Luma = new();
    public readonly Residual Cb = new();
    public readonly Residual Cr = new();

    /// <summary>I_PCM samples: 256 luma then the chroma, as they appear in the stream.</summary>
    public readonly byte[] PcmSamples = new byte[256 * 3];

    public void Reset(int addr, int x, int y)
    {
        Addr = addr;
        X = x;
        Y = y;
        Skip = Intra = Pcm = Intra16x16 = IntraNxN = Transform8x8 = Direct16x16 = false;
        Intra16x16PredMode = 0;
        IntraChromaPredMode = 0;
        Cbp = 0;
        QpDelta = 0;
        Shape = PartitionShape.Part16x16;
        NumParts = 1;
        Array.Clear(SubMbType);
        Array.Clear(PredFlags);
        Array.Clear(SubDirect);
        Array.Clear(RefIdx0);
        Array.Clear(RefIdx1);
        Array.Clear(Mvd0);
        Array.Clear(Mvd1);
        Array.Fill(IntraModes, (sbyte)2);
        Luma.Clear();
        Cb.Clear();
        Cr.Clear();
    }
}
