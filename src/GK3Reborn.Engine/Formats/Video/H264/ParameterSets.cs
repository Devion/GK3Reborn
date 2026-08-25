namespace GK3Reborn.Formats.Video.H264;

/// <summary>
/// A sequence parameter set: what every picture of a coded video sequence has in common.
/// </summary>
/// <remarks>
/// Only progressive 8-bit video is accepted. The import never writes anything else, and
/// interlaced coding (fields, MBAFF) would double the size of the macroblock layer for
/// pictures no game will ever contain. What is refused is refused at parse time, with a
/// message that names the feature, rather than by decoding garbage.
/// </remarks>
internal sealed class SequenceParameterSet
{
    public int Id;
    public int ProfileIdc;
    public int ConstraintFlags;
    public int LevelIdc;

    /// <summary>0 monochrome, 1 = 4:2:0, 2 = 4:2:2, 3 = 4:4:4.</summary>
    public int ChromaFormatIdc = 1;
    public bool SeparateColourPlanes;
    public int BitDepthLuma = 8;
    public int BitDepthChroma = 8;
    public bool TransformBypass;

    /// <summary>
    /// Scaling lists in the order they are transmitted (zigzag), or null for flat 16s.
    /// Six 4x4 lists then up to six 8x8 lists.
    /// </summary>
    public int[]?[] ScalingLists = new int[]?[12];
    public bool ScalingMatrixPresent;

    public int Log2MaxFrameNum;
    public int PocType;
    public int Log2MaxPocLsb;
    public bool DeltaPicOrderAlwaysZero;
    public int OffsetForNonRefPic;
    public int OffsetForTopToBottomField;
    public int[] OffsetForRefFrame = [];
    public int MaxNumRefFrames;
    public bool GapsInFrameNumAllowed;
    public int WidthMbs;
    public int HeightMbs;
    public bool FrameMbsOnly;
    public bool Direct8x8Inference;
    public int CropLeft, CropRight, CropTop, CropBottom;

    // From the VUI, when it says.
    public bool HasReorderInfo;
    public int NumReorderFrames;
    public int MaxDecFrameBuffering;
    public int ColourMatrix = 2; // matrix_coefficients: 1 = BT.709, 5/6 = BT.601, 2 = unspecified
    public bool FullRange;

    /// <summary>Horizontal chroma subsampling: 1 for 4:4:4, else 2.</summary>
    public int SubWidthC => ChromaFormatIdc == 3 ? 1 : 2;

    /// <summary>Vertical chroma subsampling: 1 for 4:4:4 and 4:2:2, else 2.</summary>
    public int SubHeightC => ChromaFormatIdc == 1 ? 2 : 1;

    public int ChromaWidthMb => ChromaFormatIdc == 0 ? 0 : 16 / SubWidthC;
    public int ChromaHeightMb => ChromaFormatIdc == 0 ? 0 : 16 / SubHeightC;

    /// <summary>Whether Cb and Cr are coded like luma, with their own 4x4 and 8x8 blocks.</summary>
    public bool ChromaLikeLuma => ChromaFormatIdc == 3;

    public int CodedWidth => WidthMbs * 16;
    public int CodedHeight => HeightMbs * 16;

    public int CroppedWidth => CodedWidth - SubWidthC * (CropLeft + CropRight);
    public int CroppedHeight => CodedHeight - SubHeightC * (CropTop + CropBottom);

    public static SequenceParameterSet Parse(byte[] rbsp, int length)
    {
        var r = new BitReader(rbsp, length);
        var sps = new SequenceParameterSet
        {
            ProfileIdc = (int)r.ReadBits(8),
            ConstraintFlags = (int)r.ReadBits(8),
            LevelIdc = (int)r.ReadBits(8),
            Id = r.ReadUe(),
        };

        if (sps.ProfileIdc is 100 or 110 or 122 or 244 or 44 or 83 or 86 or 118 or 128 or 138 or 139 or 134 or 135)
        {
            sps.ChromaFormatIdc = r.ReadUe();

            if (sps.ChromaFormatIdc == 3)
            {
                sps.SeparateColourPlanes = r.ReadFlag();
            }

            sps.BitDepthLuma = 8 + r.ReadUe();
            sps.BitDepthChroma = 8 + r.ReadUe();
            sps.TransformBypass = r.ReadFlag();
            sps.ScalingMatrixPresent = r.ReadFlag();

            if (sps.ScalingMatrixPresent)
            {
                int count = sps.ChromaFormatIdc == 3 ? 12 : 8;

                for (int i = 0; i < count; i++)
                {
                    sps.ScalingLists[i] = ScalingList.Read(ref r, i < 6 ? 16 : 64, out bool useDefault);

                    if (useDefault)
                    {
                        sps.ScalingLists[i] = ScalingList.Default(i);
                    }
                    else if (sps.ScalingLists[i] is null)
                    {
                        // Fall-back rule A: from the previous list of the same kind, or the default.
                        sps.ScalingLists[i] = ScalingList.FallBackA(sps.ScalingLists, i);
                    }
                }
            }
        }

        if (sps.ChromaFormatIdc > 3)
        {
            throw new FormatParseException($"H.264: chroma_format_idc {sps.ChromaFormatIdc} is not valid.");
        }

        if (sps.BitDepthLuma != 8 || sps.BitDepthChroma != 8)
        {
            throw new NotSupportedException(
                $"H.264: {sps.BitDepthLuma}-bit video is not supported; only 8-bit is.");
        }

        if (sps.SeparateColourPlanes)
        {
            throw new NotSupportedException("H.264: separate colour planes are not supported.");
        }

        sps.Log2MaxFrameNum = r.ReadUe() + 4;
        sps.PocType = r.ReadUe();

        if (sps.PocType == 0)
        {
            sps.Log2MaxPocLsb = r.ReadUe() + 4;
        }
        else if (sps.PocType == 1)
        {
            sps.DeltaPicOrderAlwaysZero = r.ReadFlag();
            sps.OffsetForNonRefPic = r.ReadSe();
            sps.OffsetForTopToBottomField = r.ReadSe();
            int count = r.ReadUe();

            if (count > 255)
            {
                throw new FormatParseException("H.264: too many offsets in the picture order cycle.");
            }

            sps.OffsetForRefFrame = new int[count];

            for (int i = 0; i < count; i++)
            {
                sps.OffsetForRefFrame[i] = r.ReadSe();
            }
        }
        else if (sps.PocType != 2)
        {
            throw new FormatParseException($"H.264: pic_order_cnt_type {sps.PocType} is not valid.");
        }

        sps.MaxNumRefFrames = r.ReadUe();
        sps.GapsInFrameNumAllowed = r.ReadFlag();
        sps.WidthMbs = r.ReadUe() + 1;
        sps.HeightMbs = r.ReadUe() + 1;
        sps.FrameMbsOnly = r.ReadFlag();

        if (!sps.FrameMbsOnly)
        {
            throw new NotSupportedException("H.264: interlaced (field or MBAFF) video is not supported.");
        }

        sps.Direct8x8Inference = r.ReadFlag();

        if (r.ReadFlag())
        {
            sps.CropLeft = r.ReadUe();
            sps.CropRight = r.ReadUe();
            sps.CropTop = r.ReadUe();
            sps.CropBottom = r.ReadUe();
        }

        if (sps.WidthMbs > 1024 || sps.HeightMbs > 1024 || sps.CroppedWidth <= 0 || sps.CroppedHeight <= 0)
        {
            throw new FormatParseException("H.264: the picture size in the SPS is not sensible.");
        }

        if (r.ReadFlag())
        {
            ReadVui(ref r, sps);
        }

        if (r.Overrun)
        {
            throw new FormatParseException("H.264: the SPS is truncated.");
        }

        return sps;
    }

    private static void ReadVui(ref BitReader r, SequenceParameterSet sps)
    {
        if (r.ReadFlag()) // aspect_ratio_info_present_flag
        {
            if (r.ReadBits(8) == 255) // Extended_SAR
            {
                r.Skip(32);
            }
        }

        if (r.ReadFlag()) // overscan_info_present_flag
        {
            r.Skip(1);
        }

        if (r.ReadFlag()) // video_signal_type_present_flag
        {
            r.Skip(3); // video_format
            sps.FullRange = r.ReadFlag();

            if (r.ReadFlag()) // colour_description_present_flag
            {
                r.Skip(8); // colour_primaries
                r.Skip(8); // transfer_characteristics
                sps.ColourMatrix = (int)r.ReadBits(8);
            }
        }

        if (r.ReadFlag()) // chroma_loc_info_present_flag
        {
            r.ReadUe();
            r.ReadUe();
        }

        if (r.ReadFlag()) // timing_info_present_flag
        {
            r.Skip(32); // num_units_in_tick
            r.Skip(32); // time_scale
            r.Skip(1);  // fixed_frame_rate_flag
        }

        bool nalHrd = r.ReadFlag();

        if (nalHrd)
        {
            SkipHrd(ref r);
        }

        bool vclHrd = r.ReadFlag();

        if (vclHrd)
        {
            SkipHrd(ref r);
        }

        if (nalHrd || vclHrd)
        {
            r.Skip(1); // low_delay_hrd_flag
        }

        r.Skip(1); // pic_struct_present_flag

        if (r.ReadFlag()) // bitstream_restriction_flag
        {
            r.Skip(1);  // motion_vectors_over_pic_boundaries_flag
            r.ReadUe(); // max_bytes_per_pic_denom
            r.ReadUe(); // max_bits_per_mb_denom
            r.ReadUe(); // log2_max_mv_length_horizontal
            r.ReadUe(); // log2_max_mv_length_vertical
            sps.NumReorderFrames = r.ReadUe();
            sps.MaxDecFrameBuffering = r.ReadUe();
            sps.HasReorderInfo = !r.Overrun;
        }
    }

    private static void SkipHrd(ref BitReader r)
    {
        int count = r.ReadUe() + 1;
        r.Skip(4); // bit_rate_scale
        r.Skip(4); // cpb_size_scale

        for (int i = 0; i < count; i++)
        {
            r.ReadUe(); // bit_rate_value_minus1
            r.ReadUe(); // cpb_size_value_minus1
            r.Skip(1);  // cbr_flag
        }

        r.Skip(5 + 5 + 5 + 5);
    }
}

/// <summary>
/// A picture parameter set: how the pictures that refer to it are coded.
/// </summary>
internal sealed class PictureParameterSet
{
    public int Id;
    public int SpsId;
    public bool Cabac;
    public bool BottomFieldPicOrderInFramePresent;
    public int NumRefIdxL0DefaultActive;
    public int NumRefIdxL1DefaultActive;
    public bool WeightedPred;
    public int WeightedBipredIdc;
    public int PicInitQp;
    public int PicInitQs;
    public int ChromaQpIndexOffset;
    public int SecondChromaQpIndexOffset;
    public bool DeblockingFilterControlPresent;
    public bool ConstrainedIntraPred;
    public bool RedundantPicCntPresent;
    public bool Transform8x8Mode;
    public bool ScalingMatrixPresent;
    public int[]?[] ScalingLists = new int[]?[12];

    /// <summary>The resolved 4x4 scaling lists, six of them, in zigzag order.</summary>
    public int[][] Resolved4x4 = new int[6][];

    /// <summary>The resolved 8x8 scaling lists, six of them, in zigzag order.</summary>
    public int[][] Resolved8x8 = new int[6][];

    /// <summary>
    /// LevelScale4x4[list][qp % 6][raster index]: the dequantisation multipliers.
    /// </summary>
    public int[][][] LevelScale4x4 = new int[6][][];

    /// <summary>LevelScale8x8[list][qp % 6][raster index].</summary>
    public int[][][] LevelScale8x8 = new int[6][][];

    public static PictureParameterSet Parse(byte[] rbsp, int length, Func<int, SequenceParameterSet?> spsById)
    {
        var r = new BitReader(rbsp, length);
        var pps = new PictureParameterSet
        {
            Id = r.ReadUe(),
            SpsId = r.ReadUe(),
        };

        SequenceParameterSet sps = spsById(pps.SpsId)
            ?? throw new FormatParseException($"H.264: PPS {pps.Id} refers to SPS {pps.SpsId}, which has not arrived.");

        pps.Cabac = r.ReadFlag();
        pps.BottomFieldPicOrderInFramePresent = r.ReadFlag();
        int sliceGroups = r.ReadUe() + 1;

        if (sliceGroups > 1)
        {
            throw new NotSupportedException("H.264: slice groups (FMO) are not supported.");
        }

        pps.NumRefIdxL0DefaultActive = r.ReadUe() + 1;
        pps.NumRefIdxL1DefaultActive = r.ReadUe() + 1;
        pps.WeightedPred = r.ReadFlag();
        pps.WeightedBipredIdc = (int)r.ReadBits(2);
        pps.PicInitQp = 26 + r.ReadSe();
        pps.PicInitQs = 26 + r.ReadSe();
        pps.ChromaQpIndexOffset = r.ReadSe();
        pps.DeblockingFilterControlPresent = r.ReadFlag();
        pps.ConstrainedIntraPred = r.ReadFlag();
        pps.RedundantPicCntPresent = r.ReadFlag();
        pps.SecondChromaQpIndexOffset = pps.ChromaQpIndexOffset;

        if (r.MoreRbspData())
        {
            pps.Transform8x8Mode = r.ReadFlag();
            pps.ScalingMatrixPresent = r.ReadFlag();

            if (pps.ScalingMatrixPresent)
            {
                int count = 6 + (sps.ChromaFormatIdc == 3 ? 6 : 2) * (pps.Transform8x8Mode ? 1 : 0);

                for (int i = 0; i < count; i++)
                {
                    pps.ScalingLists[i] = ScalingList.Read(ref r, i < 6 ? 16 : 64, out bool useDefault);

                    if (useDefault)
                    {
                        pps.ScalingLists[i] = ScalingList.Default(i);
                    }
                    else if (pps.ScalingLists[i] is null)
                    {
                        // Fall-back rule B: the SPS's list for the first of each kind, else
                        // the previous of the same kind.
                        pps.ScalingLists[i] = i is 0 or 3 or 6 or 7
                            ? (sps.ScalingMatrixPresent ? sps.ScalingLists[i] : ScalingList.Default(i))
                            : ScalingList.FallBackA(pps.ScalingLists, i);
                    }
                }
            }

            pps.SecondChromaQpIndexOffset = r.ReadSe();
        }

        if (r.Overrun)
        {
            throw new FormatParseException("H.264: the PPS is truncated.");
        }

        pps.Resolve(sps);
        return pps;
    }

    /// <summary>Works out the scaling lists in force and the dequantisation tables from them.</summary>
    private void Resolve(SequenceParameterSet sps)
    {
        for (int i = 0; i < 12; i++)
        {
            int[] list;

            if (ScalingMatrixPresent)
            {
                list = ScalingLists[i] ?? (i < 6 ? ScalingList.FallBackA(ScalingLists, i) : Fallback8x8(i));
            }
            else if (sps.ScalingMatrixPresent)
            {
                list = sps.ScalingLists[i] ?? (i < 6 ? ScalingList.FallBackA(sps.ScalingLists, i) : SpsFallback8x8(sps, i));
            }
            else
            {
                list = ScalingList.Flat(i < 6 ? 16 : 64);
            }

            if (i < 6)
            {
                Resolved4x4[i] = list;
            }
            else
            {
                Resolved8x8[i - 6] = list;
            }
        }

        for (int list = 0; list < 6; list++)
        {
            LevelScale4x4[list] = new int[6][];
            LevelScale8x8[list] = new int[6][];

            for (int m = 0; m < 6; m++)
            {
                var scale4 = new int[16];
                var scale8 = new int[64];

                for (int k = 0; k < 16; k++)
                {
                    int raster = Tables.Zigzag4x4[k];
                    scale4[raster] = Resolved4x4[list][k] * Tables.NormAdjust4x4(m, raster);
                }

                for (int k = 0; k < 64; k++)
                {
                    int raster = Tables.Zigzag8x8[k];
                    scale8[raster] = Resolved8x8[list][k] * Tables.NormAdjust8x8(m, raster);
                }

                LevelScale4x4[list][m] = scale4;
                LevelScale8x8[list][m] = scale8;
            }
        }
    }

    private int[] Fallback8x8(int i)
    {
        // Lists 8..11 (Cb, Cr) fall back to the one two before them (Y of the same kind);
        // 6 and 7 to the default.
        return i >= 8 ? (ScalingLists[i - 2] ?? Fallback8x8(i - 2)) : ScalingList.Default(i);
    }

    private static int[] SpsFallback8x8(SequenceParameterSet sps, int i)
    {
        return i >= 8 ? (sps.ScalingLists[i - 2] ?? SpsFallback8x8(sps, i - 2)) : ScalingList.Default(i);
    }
}

/// <summary>Reading and defaulting of scaling lists, 7.3.2.1.1.1.</summary>
internal static class ScalingList
{
    public static int[]? Read(ref BitReader r, int size, out bool useDefault)
    {
        useDefault = false;

        if (!r.ReadFlag()) // scaling_list_present_flag
        {
            return null;
        }

        var list = new int[size];
        int last = 8;
        int next = 8;

        for (int j = 0; j < size; j++)
        {
            if (next != 0)
            {
                int delta = r.ReadSe();
                next = (last + delta + 256) & 255;
                useDefault = j == 0 && next == 0;
            }

            list[j] = next == 0 ? last : next;
            last = list[j];
        }

        return list;
    }

    public static int[] Flat(int size)
    {
        var list = new int[size];
        Array.Fill(list, 16);
        return list;
    }

    /// <summary>The default list for a slot: Table 7-3 and 7-4, in zigzag order.</summary>
    public static int[] Default(int index) => index switch
    {
        < 3 => Tables.Default4x4Intra,
        < 6 => Tables.Default4x4Inter,
        6 or 8 or 10 => Tables.Default8x8Intra,
        _ => Tables.Default8x8Inter,
    };

    /// <summary>Fall-back rule A: the previous list of the same kind, or the default.</summary>
    public static int[] FallBackA(int[]?[] lists, int index)
    {
        if (index is 0 or 3 or 6 or 7)
        {
            return Default(index);
        }

        int previous = index < 6 ? index - 1 : index - 2;
        return lists[previous] ?? FallBackA(lists, previous);
    }
}
