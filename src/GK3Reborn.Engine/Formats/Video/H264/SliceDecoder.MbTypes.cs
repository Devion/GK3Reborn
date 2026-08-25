namespace GK3Reborn.Formats.Video.H264;

/// <summary>
/// What each mb_type and sub_mb_type means, Tables 7-11 to 7-18, and the block geometry
/// that follows from it.
/// </summary>
internal sealed partial class SliceDecoder
{
    /// <summary>Prediction lists per partition for B mb_type 1..21: bit 0 L0, bit 1 L1, first then second partition.</summary>
    private static readonly byte[] BPred0 = [0, 1, 2, 3, 1, 1, 2, 2, 1, 1, 2, 2, 1, 1, 2, 2, 3, 3, 3, 3, 3, 3];
    private static readonly byte[] BPred1 = [0, 0, 0, 0, 1, 1, 2, 2, 2, 2, 1, 1, 3, 3, 3, 3, 1, 1, 2, 2, 3, 3];

    /// <summary>Prediction lists for B sub_mb_type 0..12.</summary>
    private static readonly byte[] BSubPred = [0, 1, 2, 3, 1, 1, 2, 2, 3, 3, 1, 2, 3];

    private static readonly int[][] Partition8x8s16x16 = [[0, 1, 2, 3]];
    private static readonly int[][] Partition8x8s16x8 = [[0, 1], [2, 3]];
    private static readonly int[][] Partition8x8s8x16 = [[0, 2], [1, 3]];
    private static readonly int[][] Partition8x8s8x8 = [[0], [1], [2], [3]];

    private void SetIMbType(int type)
    {
        Macroblock mb = _mb;
        mb.Intra = true;

        if (type == 0)
        {
            mb.IntraNxN = true;
        }
        else if (type == 25)
        {
            mb.Pcm = true;
        }
        else if (type <= 24)
        {
            mb.Intra16x16 = true;
            mb.Intra16x16PredMode = (type - 1) & 3;
            int chroma = ((type - 1) >> 2) % 3;
            int luma = type >= 13 ? 15 : 0;
            mb.Cbp = luma | (chroma << 4);
        }
        else
        {
            throw new FormatParseException($"H.264: intra mb_type {type} is not valid.");
        }
    }

    private void SetPMbType(int type)
    {
        Macroblock mb = _mb;

        switch (type)
        {
            case 0:
                mb.Shape = PartitionShape.Part16x16;
                mb.NumParts = 1;
                break;
            case 1:
                mb.Shape = PartitionShape.Part16x8;
                mb.NumParts = 2;
                break;
            case 2:
                mb.Shape = PartitionShape.Part8x16;
                mb.NumParts = 2;
                break;
            case 3:
            case 4:
                mb.Shape = PartitionShape.Part8x8;
                mb.NumParts = 4;
                break;
            default:
                throw new FormatParseException($"H.264: P mb_type {type} is not valid.");
        }

        for (int i = 0; i < 4; i++)
        {
            mb.PredFlags[i] = 1;
        }
    }

    private void SetBMbType(int type)
    {
        Macroblock mb = _mb;

        if (type == 0)
        {
            mb.Direct16x16 = true;
            mb.Shape = PartitionShape.Part8x8;
            mb.NumParts = 4;

            for (int i = 0; i < 4; i++)
            {
                mb.SubDirect[i] = true;
            }

            return;
        }

        if (type == 22)
        {
            mb.Shape = PartitionShape.Part8x8;
            mb.NumParts = 4;
            return;
        }

        if (type > 22)
        {
            throw new FormatParseException($"H.264: B mb_type {type} is not valid.");
        }

        if (type <= 3)
        {
            mb.Shape = PartitionShape.Part16x16;
            mb.NumParts = 1;

            for (int i = 0; i < 4; i++)
            {
                mb.PredFlags[i] = BPred0[type];
            }

            return;
        }

        mb.NumParts = 2;
        mb.Shape = (type & 1) == 0 ? PartitionShape.Part16x8 : PartitionShape.Part8x16;
        int[][] parts = mb.Shape == PartitionShape.Part16x8 ? Partition8x8s16x8 : Partition8x8s8x16;

        foreach (int b8 in parts[0])
        {
            mb.PredFlags[b8] = BPred0[type];
        }

        foreach (int b8 in parts[1])
        {
            mb.PredFlags[b8] = BPred1[type];
        }
    }

    private void SetPSubMbType(int index, int type)
    {
        if (type > 3)
        {
            throw new FormatParseException($"H.264: P sub_mb_type {type} is not valid.");
        }

        _mb.SubMbType[index] = type;
        _mb.PredFlags[index] = 1;
    }

    private void SetBSubMbType(int index, int type)
    {
        if (type > 12)
        {
            throw new FormatParseException($"H.264: B sub_mb_type {type} is not valid.");
        }

        _mb.SubMbType[index] = type;

        if (type == 0)
        {
            _mb.SubDirect[index] = true;
        }
        else
        {
            _mb.PredFlags[index] = BSubPred[type];
        }
    }

    /// <summary>The 8x8 blocks a partition covers.</summary>
    private int[] PartitionBlocks8x8(int part) => _mb.Shape switch
    {
        PartitionShape.Part16x16 => Partition8x8s16x16[0],
        PartitionShape.Part16x8 => Partition8x8s16x8[part],
        PartitionShape.Part8x16 => Partition8x8s8x16[part],
        _ => Partition8x8s8x8[part],
    };

    private int First8x8OfPartition(int part) => _mb.Shape switch
    {
        PartitionShape.Part16x16 => 0,
        PartitionShape.Part16x8 => part * 2,
        _ => part,
    };

    /// <summary>Origin and size of a partition in 4x4 block units.</summary>
    private (int X, int Y, int W, int H) PartitionGeometry(int part) => _mb.Shape switch
    {
        PartitionShape.Part16x16 => (0, 0, 4, 4),
        PartitionShape.Part16x8 => (0, part * 2, 4, 2),
        PartitionShape.Part8x16 => (part * 2, 0, 2, 4),
        _ => ((part & 1) * 2, (part >> 1) * 2, 2, 2),
    };

    /// <summary>How a sub-macroblock splits: count and size of the sub-partitions in 4x4 units.</summary>
    private (int Count, int W, int H) SubPartitionGeometry(int subType)
    {
        if (_h.IsB)
        {
            return subType switch
            {
                <= 3 => (1, 2, 2),
                4 or 6 or 8 => (2, 2, 1),
                5 or 7 or 9 => (2, 1, 2),
                _ => (4, 1, 1),
            };
        }

        return subType switch
        {
            0 => (1, 2, 2),
            1 => (2, 2, 1),
            2 => (2, 1, 2),
            _ => (4, 1, 1),
        };
    }

    private static (int X, int Y) SubPartitionOrigin(int bx8, int by8, int w, int h, int sub)
    {
        if (w == 2 && h == 2)
        {
            return (bx8, by8);
        }

        if (w == 2)
        {
            return (bx8, by8 + sub);
        }

        if (h == 2)
        {
            return (bx8 + sub, by8);
        }

        return (bx8 + (sub & 1), by8 + (sub >> 1));
    }

    // ---- intra prediction modes, 8.3.1.1 and 8.3.2.1 ------------------------------------------

    private int StoredIntraMode(int addr, int block) =>
        addr < 0 ? 2 : addr == _mb.Addr ? _mb.IntraModes[block] : _pic.IntraModes[addr * 16 + block];

    /// <summary>Resolves the coded 4x4 mode of block blkIdx against its neighbours' modes, 8.3.1.1.</summary>
    private void SetIntra4x4Mode(int blkIdx, int rem)
    {
        int raster = Tables.RasterToBlk4x4[blkIdx];
        int bx = raster & 3;
        int by = raster >> 2;
        int blkA = LeftBlock(bx, by, out int addrA);
        int blkB = TopBlock(bx, by, out int addrB);
        int predicted = PredictedIntraMode(addrA, blkA, addrB, blkB);
        _mb.IntraModes[raster] = (sbyte)ResolveIntraMode(predicted, rem);
    }

    /// <summary>Resolves the coded 8x8 mode of block b8, 8.3.2.1.</summary>
    private void SetIntra8x8Mode(int b8, int rem)
    {
        int bx8 = b8 & 1;
        int by8 = b8 >> 1;
        int addrA;
        int blkA;
        int addrB;
        int blkB;

        if (bx8 > 0)
        {
            addrA = _mb.Addr;
            blkA = (by8 * 2) * 4 + 1;
        }
        else
        {
            // The top-right 4x4 of the 8x8 block to the left: what an I_4x4 neighbour
            // contributes (6.4.11.2), and the same as any other entry for an I_8x8 one.
            addrA = _mbA;
            blkA = (by8 * 2) * 4 + 3;
        }

        if (by8 > 0)
        {
            addrB = _mb.Addr;
            blkB = (by8 * 2 - 1) * 4 + bx8 * 2;
        }
        else
        {
            addrB = _mbB;
            blkB = 12 + bx8 * 2;
        }

        int mode = ResolveIntraMode(PredictedIntraMode(addrA, blkA, addrB, blkB), rem);

        foreach (int blk in Blocks8x8[b8])
        {
            _mb.IntraModes[blk] = (sbyte)mode;
        }
    }

    /// <summary>
    /// predIntraNxNPredMode: DC when either neighbouring macroblock is missing (or inter
    /// under constrained intra prediction), else the smaller of the two neighbours' modes,
    /// a neighbour that is not I_4x4 or I_8x8 counting as DC.
    /// </summary>
    private int PredictedIntraMode(int addrA, int blkA, int addrB, int blkB)
    {
        if (addrA < 0 || addrB < 0 || IntraNeighbourForcesDc(addrA) || IntraNeighbourForcesDc(addrB))
        {
            return 2;
        }

        return Math.Min(StoredIntraMode(addrA, blkA), StoredIntraMode(addrB, blkB));
    }

    private bool IntraNeighbourForcesDc(int addr) =>
        addr != _mb.Addr && _pps.ConstrainedIntraPred && (_pic.MbFlags[addr] & MbFlag.Intra) == 0;

    private static int ResolveIntraMode(int predicted, int rem)
    {
        if (rem < 0)
        {
            return predicted;
        }

        return rem < predicted ? rem : rem + 1;
    }
}
