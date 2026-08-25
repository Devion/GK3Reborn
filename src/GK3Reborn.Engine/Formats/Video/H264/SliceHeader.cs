namespace GK3Reborn.Formats.Video.H264;

/// <summary>slice_type with the 5..9 aliases folded away.</summary>
internal enum SliceType
{
    P = 0,
    B = 1,
    I = 2,
    SP = 3,
    SI = 4,
}

/// <summary>One ref_pic_list_modification operation.</summary>
internal readonly record struct RefListModification(int Idc, int Value);

/// <summary>One memory_management_control_operation.</summary>
internal readonly record struct Mmco(int Op, int DifferenceOfPicNums, int LongTermPicNum, int LongTermFrameIdx, int MaxLongTermFrameIdxPlus1);

/// <summary>Explicit weights for one reference in one list.</summary>
internal struct PredWeight
{
    public bool LumaPresent;
    public int LumaWeight;
    public int LumaOffset;
    public bool ChromaPresent;
    public int CbWeight;
    public int CbOffset;
    public int CrWeight;
    public int CrOffset;
}

/// <summary>
/// A slice header, 7.3.3, with the parts that need the parameter sets already resolved.
/// </summary>
internal sealed class SliceHeader
{
    public int NalType;
    public int NalRefIdc;
    public bool Idr;

    public int FirstMb;
    public SliceType Type;
    public int PpsId;
    public int FrameNum;
    public int IdrPicId;
    public int PocLsb;
    public int DeltaPocBottom;
    public int DeltaPoc0;
    public int DeltaPoc1;
    public int RedundantPicCnt;
    public bool DirectSpatialMvPred;
    public int NumRefIdxL0Active;
    public int NumRefIdxL1Active;
    public List<RefListModification> ModificationsL0 = [];
    public List<RefListModification> ModificationsL1 = [];

    public int LumaLog2WeightDenom;
    public int ChromaLog2WeightDenom;
    public PredWeight[] WeightsL0 = [];
    public PredWeight[] WeightsL1 = [];
    public bool HasExplicitWeights;

    public bool NoOutputOfPriorPics;
    public bool LongTermReference;
    public bool AdaptiveRefPicMarking;
    public List<Mmco> Mmcos = [];

    public int CabacInitIdc;
    public int SliceQp;
    public int DisableDeblockingFilterIdc;
    public int SliceAlphaOffset;
    public int SliceBetaOffset;

    /// <summary>Where the slice data starts, in bits from the start of the RBSP.</summary>
    public int DataBitOffset;

    public SequenceParameterSet Sps = null!;
    public PictureParameterSet Pps = null!;

    public bool IsIntra => Type is SliceType.I or SliceType.SI;
    public bool IsB => Type == SliceType.B;
    public bool IsP => Type is SliceType.P or SliceType.SP;

    public static SliceHeader Parse(
        NalUnit nal,
        Func<int, PictureParameterSet?> ppsById,
        Func<int, SequenceParameterSet?> spsById)
    {
        var r = new BitReader(nal.Rbsp, nal.Length);
        var h = new SliceHeader
        {
            NalType = nal.Type,
            NalRefIdc = nal.RefIdc,
            Idr = nal.Type == H264.NalType.IdrSlice,
            FirstMb = r.ReadUe(),
        };

        int sliceType = r.ReadUe();

        if (sliceType > 9)
        {
            throw new FormatParseException($"H.264: slice_type {sliceType} is not valid.");
        }

        h.Type = (SliceType)(sliceType % 5);

        if (h.Type is SliceType.SP or SliceType.SI)
        {
            throw new NotSupportedException("H.264: SP and SI slices are not supported.");
        }

        h.PpsId = r.ReadUe();
        h.Pps = ppsById(h.PpsId)
            ?? throw new FormatParseException($"H.264: a slice refers to PPS {h.PpsId}, which has not arrived.");
        h.Sps = spsById(h.Pps.SpsId)
            ?? throw new FormatParseException($"H.264: PPS {h.PpsId} refers to SPS {h.Pps.SpsId}, which has not arrived.");

        SequenceParameterSet sps = h.Sps;
        PictureParameterSet pps = h.Pps;

        h.FrameNum = (int)r.ReadBits(sps.Log2MaxFrameNum);

        // frame_mbs_only_flag is required to be set, so there is no field_pic_flag.
        if (h.Idr)
        {
            h.IdrPicId = r.ReadUe();
        }

        if (sps.PocType == 0)
        {
            h.PocLsb = (int)r.ReadBits(sps.Log2MaxPocLsb);

            if (pps.BottomFieldPicOrderInFramePresent)
            {
                h.DeltaPocBottom = r.ReadSe();
            }
        }
        else if (sps.PocType == 1 && !sps.DeltaPicOrderAlwaysZero)
        {
            h.DeltaPoc0 = r.ReadSe();

            if (pps.BottomFieldPicOrderInFramePresent)
            {
                h.DeltaPoc1 = r.ReadSe();
            }
        }

        if (pps.RedundantPicCntPresent)
        {
            h.RedundantPicCnt = r.ReadUe();
        }

        if (h.Type == SliceType.B)
        {
            h.DirectSpatialMvPred = r.ReadFlag();
        }

        h.NumRefIdxL0Active = pps.NumRefIdxL0DefaultActive;
        h.NumRefIdxL1Active = pps.NumRefIdxL1DefaultActive;

        if (h.Type is SliceType.P or SliceType.B)
        {
            if (r.ReadFlag()) // num_ref_idx_active_override_flag
            {
                h.NumRefIdxL0Active = r.ReadUe() + 1;

                if (h.Type == SliceType.B)
                {
                    h.NumRefIdxL1Active = r.ReadUe() + 1;
                }
            }

            if (h.NumRefIdxL0Active > 32 || h.NumRefIdxL1Active > 32)
            {
                throw new FormatParseException("H.264: more than 32 active references.");
            }

            ReadModifications(ref r, h.ModificationsL0);

            if (h.Type == SliceType.B)
            {
                ReadModifications(ref r, h.ModificationsL1);
            }
        }

        if ((pps.WeightedPred && h.Type == SliceType.P) || (pps.WeightedBipredIdc == 1 && h.Type == SliceType.B))
        {
            ReadPredWeightTable(ref r, h);
        }

        if (nal.RefIdc != 0)
        {
            if (h.Idr)
            {
                h.NoOutputOfPriorPics = r.ReadFlag();
                h.LongTermReference = r.ReadFlag();
            }
            else
            {
                h.AdaptiveRefPicMarking = r.ReadFlag();

                if (h.AdaptiveRefPicMarking)
                {
                    for (int guard = 0; guard < 64; guard++)
                    {
                        int op = r.ReadUe();

                        if (op == 0)
                        {
                            break;
                        }

                        int difference = 0, longTermPicNum = 0, longTermFrameIdx = 0, maxPlus1 = 0;

                        if (op is 1 or 3)
                        {
                            difference = r.ReadUe() + 1;
                        }

                        if (op == 2)
                        {
                            longTermPicNum = r.ReadUe();
                        }

                        if (op is 3 or 6)
                        {
                            longTermFrameIdx = r.ReadUe();
                        }

                        if (op == 4)
                        {
                            maxPlus1 = r.ReadUe();
                        }

                        h.Mmcos.Add(new Mmco(op, difference, longTermPicNum, longTermFrameIdx, maxPlus1));
                    }
                }
            }
        }

        if (pps.Cabac && h.Type != SliceType.I)
        {
            h.CabacInitIdc = r.ReadUe();

            if (h.CabacInitIdc > 2)
            {
                throw new FormatParseException("H.264: cabac_init_idc out of range.");
            }
        }

        h.SliceQp = pps.PicInitQp + r.ReadSe();

        if (h.SliceQp < 0 || h.SliceQp > 51)
        {
            throw new FormatParseException("H.264: the slice QP is out of range.");
        }

        if (pps.DeblockingFilterControlPresent)
        {
            h.DisableDeblockingFilterIdc = r.ReadUe();

            if (h.DisableDeblockingFilterIdc != 1)
            {
                h.SliceAlphaOffset = r.ReadSe() * 2;
                h.SliceBetaOffset = r.ReadSe() * 2;
            }
        }

        if (r.Overrun)
        {
            throw new FormatParseException("H.264: the slice header is truncated.");
        }

        h.DataBitOffset = r.Position;
        return h;
    }

    private static void ReadModifications(ref BitReader r, List<RefListModification> into)
    {
        if (!r.ReadFlag()) // ref_pic_list_modification_flag
        {
            return;
        }

        for (int guard = 0; guard < 64; guard++)
        {
            int idc = r.ReadUe();

            if (idc == 3)
            {
                return;
            }

            if (idc > 3)
            {
                throw new FormatParseException("H.264: modification_of_pic_nums_idc out of range.");
            }

            into.Add(new RefListModification(idc, r.ReadUe()));
        }

        throw new FormatParseException("H.264: the reference list modification does not end.");
    }

    private static void ReadPredWeightTable(ref BitReader r, SliceHeader h)
    {
        h.HasExplicitWeights = true;
        h.LumaLog2WeightDenom = r.ReadUe();

        if (h.Sps.ChromaFormatIdc != 0)
        {
            h.ChromaLog2WeightDenom = r.ReadUe();
        }

        h.WeightsL0 = ReadWeights(ref r, h, h.NumRefIdxL0Active);

        if (h.Type == SliceType.B)
        {
            h.WeightsL1 = ReadWeights(ref r, h, h.NumRefIdxL1Active);
        }
    }

    private static PredWeight[] ReadWeights(ref BitReader r, SliceHeader h, int count)
    {
        var weights = new PredWeight[count];

        for (int i = 0; i < count; i++)
        {
            ref PredWeight w = ref weights[i];
            w.LumaWeight = 1 << h.LumaLog2WeightDenom;
            w.CbWeight = w.CrWeight = 1 << h.ChromaLog2WeightDenom;
            w.LumaPresent = r.ReadFlag();

            if (w.LumaPresent)
            {
                w.LumaWeight = r.ReadSe();
                w.LumaOffset = r.ReadSe();
            }

            if (h.Sps.ChromaFormatIdc != 0)
            {
                w.ChromaPresent = r.ReadFlag();

                if (w.ChromaPresent)
                {
                    w.CbWeight = r.ReadSe();
                    w.CbOffset = r.ReadSe();
                    w.CrWeight = r.ReadSe();
                    w.CrOffset = r.ReadSe();
                }
            }
        }

        return weights;
    }
}
