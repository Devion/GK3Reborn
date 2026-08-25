namespace GK3Reborn.Formats.Video.H264;

/// <summary>
/// One decoded picture as handed to the caller: planes, geometry and the tag of the
/// access unit it came from.
/// </summary>
/// <remarks>
/// The planes belong to the decoder's picture pool. They stay valid until
/// <see cref="Release"/> is called, after which the decoder may reuse them; a caller that
/// wants to keep a frame copies it first. Coordinates are in the coded picture, so the
/// cropping rectangle has to be applied by whoever reads the samples.
/// </remarks>
public sealed class DecodedFrame
{
    private readonly Picture _picture;

    internal DecodedFrame(Picture picture, SequenceParameterSet sps)
    {
        _picture = picture;
        Width = sps.CroppedWidth;
        Height = sps.CroppedHeight;
        CropLeft = sps.CropLeft * sps.SubWidthC;
        CropTop = sps.CropTop * sps.SubHeightC;
        ChromaFormat = sps.ChromaFormatIdc;
        ColourMatrix = sps.ColourMatrix;
        FullRange = sps.FullRange;
        Tag = picture.Tag;
        Poc = picture.Poc;
    }

    /// <summary>Visible width.</summary>
    public int Width { get; }

    /// <summary>Visible height.</summary>
    public int Height { get; }

    /// <summary>Luma columns to skip on the left of the coded picture.</summary>
    public int CropLeft { get; }

    /// <summary>Luma rows to skip at the top of the coded picture.</summary>
    public int CropTop { get; }

    /// <summary>0 monochrome, 1 = 4:2:0, 3 = 4:4:4.</summary>
    public int ChromaFormat { get; }

    /// <summary>matrix_coefficients from the VUI: 1 = BT.709, 5 or 6 = BT.601, 2 = unspecified.</summary>
    public int ColourMatrix { get; }

    /// <summary>Whether the samples use the full 0..255 range rather than 16..235.</summary>
    public bool FullRange { get; }

    /// <summary>The tag given to the access unit that produced this frame.</summary>
    public long Tag { get; }

    /// <summary>The picture order count, which is its place in display order.</summary>
    public int Poc { get; }

    public byte[] Y => _picture.Y;
    public byte[] Cb => _picture.Cb;
    public byte[] Cr => _picture.Cr;
    public int Stride => _picture.Stride;
    public int ChromaStride => _picture.ChromaStride;
    public int CodedWidth => _picture.Width;
    public int CodedHeight => _picture.Height;

    /// <summary>Gives the planes back to the decoder.</summary>
    public void Release() => _picture.InUse = false;
}

/// <summary>
/// An H.264 decoder for progressive 8-bit 4:2:0, 4:4:4 and monochrome video: the parts of
/// the standard the game's cinematics use, with CAVLC and CABAC, all slice types, all
/// intra modes, weighted and direct prediction, and the deblocking filter.
/// </summary>
/// <remarks>
/// <para>
/// Managed code throughout, so the cutscenes play on every platform the engine builds for
/// with nothing to install and nothing to load beside the executable. FFmpeg would be
/// faster; it would also be sixty megabytes of versioned native libraries per platform,
/// which is what this replaces.
/// </para>
/// <para>
/// Access units go in one at a time and frames come out in display order through
/// <see cref="TryGetFrame"/>, delayed by as many pictures as the stream's reordering
/// needs; <see cref="Flush"/> at the end releases the rest.
/// </para>
/// </remarks>
public sealed class H264Decoder
{
    private readonly Dictionary<int, SequenceParameterSet> _sps = [];
    private readonly Dictionary<int, PictureParameterSet> _pps = [];
    private readonly NalReader _nals = new();
    private readonly List<Picture> _pool = [];
    private readonly List<Picture> _dpb = [];
    private readonly Queue<DecodedFrame> _output = new();
    private readonly List<Picture> _shortTerm = [];
    private readonly List<Picture> _longTerm = [];

    private SequenceParameterSet? _activeSps;
    private Picture? _current;
    private SliceDecoder? _sliceDecoder;
    private SliceHeader? _firstSlice;
    private int _maxReorder;

    // Picture order count state, 8.2.1.
    private int _prevPocMsb;
    private int _prevPocLsb;
    private int _prevFrameNumOffset;
    private int _prevFrameNum;
    private bool _prevHadMmco5;
    private int _prevRefFrameNum;

    /// <summary>The visible picture width, once an SPS has been seen; the active one, else the last parsed.</summary>
    public int Width => (_activeSps ?? _sps.Values.LastOrDefault())?.CroppedWidth ?? 0;

    /// <summary>The visible picture height, once an SPS has been seen.</summary>
    public int Height => (_activeSps ?? _sps.Values.LastOrDefault())?.CroppedHeight ?? 0;

    /// <summary>Whether an SPS has been seen.</summary>
    public bool Configured => _sps.Count > 0;

    /// <summary>Feeds the parameter sets from an <c>avcC</c> box, each with its NAL header byte.</summary>
    public void Configure(IEnumerable<byte[]> sequenceParameterSets, IEnumerable<byte[]> pictureParameterSets)
    {
        foreach (byte[] nal in sequenceParameterSets)
        {
            Handle(_nals.Unescape(nal), 0);
        }

        foreach (byte[] nal in pictureParameterSets)
        {
            Handle(_nals.Unescape(nal), 0);
        }
    }

    /// <summary>Decodes one length-prefixed access unit, as stored in an MP4 sample.</summary>
    /// <param name="accessUnit">Its bytes.</param>
    /// <param name="nalLengthSize">Width of each NAL unit's length prefix, from <c>avcC</c>.</param>
    /// <param name="tag">A value returned with the frame this unit produces, such as its presentation time.</param>
    public void Decode(ReadOnlyMemory<byte> accessUnit, int nalLengthSize, long tag)
    {
        foreach (NalUnit nal in _nals.ReadLengthPrefixed(accessUnit, nalLengthSize))
        {
            Handle(nal, tag);
        }

        FinishPicture();
    }

    /// <summary>Decodes an Annex B byte stream, as FFmpeg writes with <c>-f h264</c>; pictures are tagged in order.</summary>
    public void DecodeAnnexB(ReadOnlyMemory<byte> stream)
    {
        long tag = 0;

        foreach (NalUnit nal in _nals.ReadAnnexB(stream))
        {
            if (nal.Type is NalType.Slice or NalType.IdrSlice)
            {
                // A new picture starts where the first macroblock does.
                var peek = new BitReader(nal.Rbsp, nal.Length);

                if (peek.ReadUe() == 0)
                {
                    FinishPicture();
                    tag++;
                }
            }

            Handle(nal, tag);
        }

        FinishPicture();
    }

    /// <summary>Takes the next frame in display order, if one is ready.</summary>
    public bool TryGetFrame(out DecodedFrame frame) => _output.TryDequeue(out frame!);

    /// <summary>Ends the stream: every decoded picture becomes available for output.</summary>
    public void Flush()
    {
        FinishPicture();

        while (OutputOne())
        {
        }
    }

    /// <summary>Forgets every picture, as before seeking.</summary>
    public void Reset()
    {
        FinishPicture();
        _output.Clear();
        _dpb.Clear();
        _shortTerm.Clear();
        _longTerm.Clear();

        foreach (Picture picture in _pool)
        {
            picture.NeededForOutput = false;
            picture.IsShortTermRef = false;
            picture.IsLongTermRef = false;
        }

        _prevPocMsb = _prevPocLsb = _prevFrameNumOffset = _prevFrameNum = _prevRefFrameNum = 0;
        _prevHadMmco5 = false;
    }

    // ---- NAL dispatch -------------------------------------------------------------------------

    private void Handle(NalUnit nal, long tag)
    {
        switch (nal.Type)
        {
            case NalType.Sps:
                {
                    SequenceParameterSet sps = SequenceParameterSet.Parse(nal.Rbsp, nal.Length);

                    if (sps.ChromaFormatIdc == 2)
                    {
                        throw new NotSupportedException("H.264: 4:2:2 video is not supported.");
                    }

                    if (sps.TransformBypass)
                    {
                        throw new NotSupportedException("H.264: lossless (transform bypass) video is not supported.");
                    }

                    _sps[sps.Id] = sps;
                    break;
                }

            case NalType.Pps:
                {
                    PictureParameterSet pps = PictureParameterSet.Parse(nal.Rbsp, nal.Length, id => _sps.GetValueOrDefault(id));
                    _pps[pps.Id] = pps;
                    break;
                }

            case NalType.Slice:
            case NalType.IdrSlice:
                DecodeSlice(nal, tag);
                break;

            case NalType.SliceDataA:
            case NalType.SliceDataB:
            case NalType.SliceDataC:
                throw new NotSupportedException("H.264: slice data partitioning is not supported.");

            case NalType.EndOfSequence:
            case NalType.EndOfStream:
                FinishPicture();
                break;

            default:
                // SEI, delimiters, filler, and the scalable and multiview extensions: nothing here reads them.
                break;
        }
    }

    private void DecodeSlice(NalUnit nal, long tag)
    {
        SliceHeader header = SliceHeader.Parse(
            nal, id => _pps.GetValueOrDefault(id), id => _sps.GetValueOrDefault(id));

        if (header.RedundantPicCnt > 0)
        {
            return; // A redundant copy of a slice already decoded.
        }

        if (_current is null || header.FirstMb == 0 && _firstSlice is not null && IsNewPicture(header))
        {
            FinishPicture();
            StartPicture(header, tag);
        }

        Picture picture = _current!;
        BuildReferenceLists(header, picture);
        _sliceDecoder!.Decode(header, nal, picture.Slices.Count);
    }

    /// <summary>First-slice detection, 7.4.1.2.4, for streams that do not delimit their pictures.</summary>
    private bool IsNewPicture(SliceHeader h)
    {
        SliceHeader f = _firstSlice!;
        return h.FrameNum != f.FrameNum
            || h.PpsId != f.PpsId
            || (h.NalRefIdc == 0) != (f.NalRefIdc == 0)
            || h.Idr != f.Idr
            || (h.Idr && h.IdrPicId != f.IdrPicId)
            || (h.Sps.PocType == 0 && (h.PocLsb != f.PocLsb || h.DeltaPocBottom != f.DeltaPocBottom))
            || (h.Sps.PocType == 1 && (h.DeltaPoc0 != f.DeltaPoc0 || h.DeltaPoc1 != f.DeltaPoc1));
    }

    // ---- pictures -----------------------------------------------------------------------------

    private void StartPicture(SliceHeader header, long tag)
    {
        SequenceParameterSet sps = header.Sps;

        if (_activeSps is null || !ReferenceEquals(_activeSps, sps))
        {
            if (_activeSps is not null && (_activeSps.WidthMbs != sps.WidthMbs || _activeSps.HeightMbs != sps.HeightMbs || _activeSps.ChromaFormatIdc != sps.ChromaFormatIdc))
            {
                // A new size: nothing in the pool fits any more.
                Flush();
                _pool.Clear();
                _dpb.Clear();
                _shortTerm.Clear();
                _longTerm.Clear();
            }

            _activeSps = sps;
            _maxReorder = sps.HasReorderInfo ? sps.NumReorderFrames : MaxDpbFrames(sps);
        }

        if (header.Idr)
        {
            // 8.2.5.1: an IDR empties the reference set. Prior pictures still come out
            // unless the stream says they should not.
            foreach (Picture p in _dpb)
            {
                p.IsShortTermRef = false;
                p.IsLongTermRef = false;
            }

            _shortTerm.Clear();
            _longTerm.Clear();

            if (header.NoOutputOfPriorPics)
            {
                foreach (Picture p in _dpb)
                {
                    p.NeededForOutput = false;
                }

                _dpb.Clear();
            }
            else
            {
                while (OutputOne())
                {
                }
            }
        }

        Picture picture = Allocate(sps);
        picture.Reset();
        picture.Tag = tag;
        picture.Idr = header.Idr;
        picture.FrameNum = header.FrameNum;
        picture.Poc = ComputePoc(header);
        _current = picture;
        _firstSlice = header;
        _sliceDecoder = new SliceDecoder(picture, sps);
    }

    private Picture Allocate(SequenceParameterSet sps)
    {
        foreach (Picture p in _pool)
        {
            if (!p.IsReference && !p.NeededForOutput && !p.InUse && !_dpb.Contains(p))
            {
                return p;
            }
        }

        var picture = new Picture(sps.WidthMbs, sps.HeightMbs, sps.ChromaFormatIdc);
        _pool.Add(picture);
        return picture;
    }

    private void FinishPicture()
    {
        Picture? picture = _current;

        if (picture is null)
        {
            return;
        }

        SliceHeader first = _firstSlice!;
        _current = null;
        _firstSlice = null;
        _sliceDecoder = null;

        if (picture.Slices.Count > 0)
        {
            new Deblocker(picture, first.Sps, first.Pps).Run();
        }

        bool hadMmco5 = false;

        if (first.NalRefIdc != 0)
        {
            hadMmco5 = MarkReferences(picture, first);
        }

        // 8.2.1: what the next picture's order count is derived from.
        _prevHadMmco5 = hadMmco5;

        if (first.NalRefIdc != 0)
        {
            _prevRefFrameNum = hadMmco5 ? 0 : picture.FrameNum;

            if (first.Sps.PocType == 0)
            {
                if (hadMmco5)
                {
                    _prevPocMsb = 0;
                    _prevPocLsb = picture.Poc; // TopFieldOrderCnt after the MMCO 5 adjustment
                }
                else
                {
                    _prevPocMsb = _pocMsb;
                    _prevPocLsb = first.PocLsb;
                }
            }
        }

        _prevFrameNum = hadMmco5 ? 0 : picture.FrameNum;
        _prevFrameNumOffset = hadMmco5 ? 0 : _frameNumOffset;

        picture.NeededForOutput = true;
        _dpb.Add(picture);

        if (hadMmco5)
        {
            // Everything before it is output first, 8.2.5.4 / C.4.4.
            while (OutputOne(excluding: picture))
            {
            }
        }

        // Bumping, C.4.5.3: hold back only as many as reordering needs.
        while (CountWaiting() > _maxReorder && OutputOne())
        {
        }

        Prune();
    }

    private int CountWaiting()
    {
        int count = 0;

        foreach (Picture p in _dpb)
        {
            if (p.NeededForOutput)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>Outputs the waiting picture with the smallest order count.</summary>
    private bool OutputOne(Picture? excluding = null)
    {
        Picture? best = null;

        foreach (Picture p in _dpb)
        {
            if (p.NeededForOutput && p != excluding && (best is null || p.Poc < best.Poc))
            {
                best = p;
            }
        }

        if (best is null)
        {
            return false;
        }

        best.NeededForOutput = false;
        best.InUse = true;
        _output.Enqueue(new DecodedFrame(best, _activeSps!));
        Prune();
        return true;
    }

    /// <summary>Drops pictures that are neither waiting for output nor references.</summary>
    private void Prune() => _dpb.RemoveAll(p => !p.NeededForOutput && !p.IsReference);

    private static int MaxDpbFrames(SequenceParameterSet sps)
    {
        int maxDpbMbs = sps.LevelIdc switch
        {
            9 or 10 => 396,
            11 => (sps.ConstraintFlags & 0x10) != 0 ? 396 : 900,
            12 or 13 or 20 => 2376,
            21 => 4752,
            22 or 30 => 8100,
            31 => 18000,
            32 => 20480,
            40 or 41 => 32768,
            42 => 34816,
            50 => 110400,
            51 or 52 => 184320,
            _ => 696320,
        };

        return Math.Clamp(maxDpbMbs / (sps.WidthMbs * sps.HeightMbs), 1, 16);
    }

    // ---- picture order count, 8.2.1 -----------------------------------------------------------

    private int _pocMsb;
    private int _frameNumOffset;

    private int ComputePoc(SliceHeader h)
    {
        SequenceParameterSet sps = h.Sps;

        switch (sps.PocType)
        {
            case 0:
                {
                    int prevMsb = h.Idr ? 0 : _prevPocMsb;
                    int prevLsb = h.Idr ? 0 : _prevPocLsb;
                    int maxLsb = 1 << sps.Log2MaxPocLsb;
                    int msb;

                    if (h.PocLsb < prevLsb && prevLsb - h.PocLsb >= maxLsb / 2)
                    {
                        msb = prevMsb + maxLsb;
                    }
                    else if (h.PocLsb > prevLsb && h.PocLsb - prevLsb > maxLsb / 2)
                    {
                        msb = prevMsb - maxLsb;
                    }
                    else
                    {
                        msb = prevMsb;
                    }

                    _pocMsb = msb;
                    int top = msb + h.PocLsb;
                    int bottom = top + h.DeltaPocBottom;
                    return Math.Min(top, bottom);
                }

            case 1:
                {
                    int maxFrameNum = 1 << sps.Log2MaxFrameNum;
                    int offset = h.Idr ? 0 : _prevFrameNum > h.FrameNum ? _prevFrameNumOffset + maxFrameNum : _prevFrameNumOffset;
                    _frameNumOffset = offset;
                    int cycle = sps.OffsetForRefFrame.Length;
                    int absFrameNum = cycle != 0 ? offset + h.FrameNum : 0;

                    if (h.NalRefIdc == 0 && absFrameNum > 0)
                    {
                        absFrameNum--;
                    }

                    int expected = 0;

                    if (absFrameNum > 0)
                    {
                        int cycleCount = (absFrameNum - 1) / cycle;
                        int inCycle = (absFrameNum - 1) % cycle;
                        int deltaPerCycle = 0;

                        foreach (int o in sps.OffsetForRefFrame)
                        {
                            deltaPerCycle += o;
                        }

                        expected = cycleCount * deltaPerCycle;

                        for (int i = 0; i <= inCycle; i++)
                        {
                            expected += sps.OffsetForRefFrame[i];
                        }
                    }

                    if (h.NalRefIdc == 0)
                    {
                        expected += sps.OffsetForNonRefPic;
                    }

                    int top = expected + h.DeltaPoc0;
                    int bottom = top + sps.OffsetForTopToBottomField + h.DeltaPoc1;
                    return Math.Min(top, bottom);
                }

            default:
                {
                    int maxFrameNum = 1 << sps.Log2MaxFrameNum;
                    int offset = h.Idr ? 0 : _prevFrameNum > h.FrameNum ? _prevFrameNumOffset + maxFrameNum : _prevFrameNumOffset;
                    _frameNumOffset = offset;

                    if (h.Idr)
                    {
                        return 0;
                    }

                    return h.NalRefIdc == 0 ? 2 * (offset + h.FrameNum) - 1 : 2 * (offset + h.FrameNum);
                }
        }
    }

    // ---- reference marking, 8.2.5 -------------------------------------------------------------

    /// <returns>Whether a memory_management_control_operation 5 was applied.</returns>
    private bool MarkReferences(Picture picture, SliceHeader h)
    {
        SequenceParameterSet sps = h.Sps;
        bool mmco5 = false;

        if (h.Idr)
        {
            if (h.LongTermReference)
            {
                picture.IsLongTermRef = true;
                picture.LongTermFrameIdx = 0;
                _longTerm.Add(picture);
            }
            else
            {
                picture.IsShortTermRef = true;
                _shortTerm.Add(picture);
            }

            return false;
        }

        if (h.AdaptiveRefPicMarking)
        {
            int maxFrameNum = 1 << sps.Log2MaxFrameNum;
            int currPicNum = picture.FrameNum;

            foreach (Mmco op in h.Mmcos)
            {
                switch (op.Op)
                {
                    case 1:
                        {
                            int picNum = currPicNum - op.DifferenceOfPicNums;
                            Picture? target = FindShortTerm(picNum, maxFrameNum, currPicNum);

                            if (target is not null)
                            {
                                target.IsShortTermRef = false;
                                _shortTerm.Remove(target);
                            }

                            break;
                        }

                    case 2:
                        {
                            Picture? target = _longTerm.Find(p => p.LongTermFrameIdx == op.LongTermPicNum);

                            if (target is not null)
                            {
                                target.IsLongTermRef = false;
                                _longTerm.Remove(target);
                            }

                            break;
                        }

                    case 3:
                        {
                            int picNum = currPicNum - op.DifferenceOfPicNums;
                            Picture? target = FindShortTerm(picNum, maxFrameNum, currPicNum);
                            Picture? holder = _longTerm.Find(p => p.LongTermFrameIdx == op.LongTermFrameIdx);

                            if (holder is not null && holder != target)
                            {
                                holder.IsLongTermRef = false;
                                _longTerm.Remove(holder);
                            }

                            if (target is not null)
                            {
                                target.IsShortTermRef = false;
                                _shortTerm.Remove(target);
                                target.IsLongTermRef = true;
                                target.LongTermFrameIdx = op.LongTermFrameIdx;
                                _longTerm.Add(target);
                            }

                            break;
                        }

                    case 4:
                        {
                            int max = op.MaxLongTermFrameIdxPlus1 - 1;

                            foreach (Picture p in _longTerm.ToArray())
                            {
                                if (p.LongTermFrameIdx > max)
                                {
                                    p.IsLongTermRef = false;
                                    _longTerm.Remove(p);
                                }
                            }

                            break;
                        }

                    case 5:
                        foreach (Picture p in _shortTerm)
                        {
                            p.IsShortTermRef = false;
                        }

                        foreach (Picture p in _longTerm)
                        {
                            p.IsLongTermRef = false;
                        }

                        _shortTerm.Clear();
                        _longTerm.Clear();
                        mmco5 = true;
                        break;

                    case 6:
                        {
                            Picture? holder = _longTerm.Find(p => p.LongTermFrameIdx == op.LongTermFrameIdx);

                            if (holder is not null)
                            {
                                holder.IsLongTermRef = false;
                                _longTerm.Remove(holder);
                            }

                            picture.IsLongTermRef = true;
                            picture.LongTermFrameIdx = op.LongTermFrameIdx;
                            _longTerm.Add(picture);
                            break;
                        }
                }
            }

            if (mmco5)
            {
                // 8.2.1: the picture's order count is reset so later pictures count from it.
                picture.Poc = 0;
                picture.FrameNum = 0;
            }
        }
        else
        {
            // Sliding window, 8.2.5.3.
            int max = Math.Max(sps.MaxNumRefFrames, 1);

            if (_shortTerm.Count + _longTerm.Count >= max && _shortTerm.Count > 0)
            {
                Picture oldest = _shortTerm[0];
                int oldestWrap = FrameNumWrap(oldest, picture.FrameNum, 1 << sps.Log2MaxFrameNum);

                foreach (Picture p in _shortTerm)
                {
                    int wrap = FrameNumWrap(p, picture.FrameNum, 1 << sps.Log2MaxFrameNum);

                    if (wrap < oldestWrap)
                    {
                        oldest = p;
                        oldestWrap = wrap;
                    }
                }

                oldest.IsShortTermRef = false;
                _shortTerm.Remove(oldest);
            }
        }

        if (!picture.IsLongTermRef)
        {
            picture.IsShortTermRef = true;
            _shortTerm.Add(picture);
        }

        return mmco5;
    }

    private static int FrameNumWrap(Picture p, int currentFrameNum, int maxFrameNum) =>
        p.FrameNum > currentFrameNum ? p.FrameNum - maxFrameNum : p.FrameNum;

    private Picture? FindShortTerm(int picNum, int maxFrameNum, int currentFrameNum)
    {
        foreach (Picture p in _shortTerm)
        {
            if (FrameNumWrap(p, currentFrameNum, maxFrameNum) == picNum)
            {
                return p;
            }
        }

        return null;
    }

    // ---- reference lists, 8.2.4 ---------------------------------------------------------------

    private void BuildReferenceLists(SliceHeader h, Picture current)
    {
        SliceDecoder decoder = _sliceDecoder!;
        Array.Clear(decoder.RefList0);
        Array.Clear(decoder.RefList1);

        if (h.IsIntra)
        {
            return;
        }

        int maxFrameNum = 1 << h.Sps.Log2MaxFrameNum;
        var list0 = new List<Picture>();
        var list1 = new List<Picture>();

        if (h.IsP)
        {
            // Short-term by descending PicNum, then long-term by ascending index.
            list0.AddRange(_shortTerm.OrderByDescending(p => FrameNumWrap(p, current.FrameNum, maxFrameNum)));
            list0.AddRange(_longTerm.OrderBy(p => p.LongTermFrameIdx));
        }
        else
        {
            var before = _shortTerm.Where(p => p.Poc <= current.Poc).OrderByDescending(p => p.Poc).ToList();
            var after = _shortTerm.Where(p => p.Poc > current.Poc).OrderBy(p => p.Poc).ToList();
            var longTerm = _longTerm.OrderBy(p => p.LongTermFrameIdx).ToList();
            list0.AddRange(before);
            list0.AddRange(after);
            list0.AddRange(longTerm);
            list1.AddRange(after);
            list1.AddRange(before);
            list1.AddRange(longTerm);

            if (list1.Count > 1 && list1.SequenceEqual(list0))
            {
                (list1[0], list1[1]) = (list1[1], list1[0]);
            }
        }

        ApplyModifications(list0, h.ModificationsL0, h.NumRefIdxL0Active, current, maxFrameNum);
        FillList(decoder.RefList0, list0, h.NumRefIdxL0Active);

        if (h.IsB)
        {
            ApplyModifications(list1, h.ModificationsL1, h.NumRefIdxL1Active, current, maxFrameNum);
            FillList(decoder.RefList1, list1, h.NumRefIdxL1Active);
        }
    }

    private static void FillList(Picture?[] into, List<Picture> from, int active)
    {
        for (int i = 0; i < active; i++)
        {
            // A list shorter than the active count leaves entries with no picture; a
            // conforming stream never uses them, and a damaged one gets the nearest.
            into[i] = i < from.Count ? from[i] : from.Count > 0 ? from[^1] : null;
        }
    }

    /// <summary>ref_pic_list_modification, 8.2.4.3.</summary>
    private void ApplyModifications(List<Picture> list, List<RefListModification> ops, int active, Picture current, int maxFrameNum)
    {
        if (ops.Count == 0)
        {
            return;
        }

        // Work on a list of exactly the active length, as the standard does.
        while (list.Count < active)
        {
            list.Add(null!);
        }

        int picNumPred = current.FrameNum;
        int refIdx = 0;

        foreach (RefListModification op in ops)
        {
            Picture? target;

            if (op.Idc is 0 or 1)
            {
                int absDiff = op.Value + 1;
                int noWrap;

                if (op.Idc == 0)
                {
                    noWrap = picNumPred - absDiff;

                    if (noWrap < 0)
                    {
                        noWrap += maxFrameNum;
                    }
                }
                else
                {
                    noWrap = picNumPred + absDiff;

                    if (noWrap >= maxFrameNum)
                    {
                        noWrap -= maxFrameNum;
                    }
                }

                picNumPred = noWrap;
                int picNum = noWrap > current.FrameNum ? noWrap - maxFrameNum : noWrap;
                target = FindShortTerm(picNum, maxFrameNum, current.FrameNum);
            }
            else
            {
                target = _longTerm.Find(p => p.LongTermFrameIdx == op.Value);
            }

            if (target is null)
            {
                throw new FormatParseException("H.264: a reference list modification names a picture that is not there.");
            }

            // Insert at refIdx and drop the later duplicate.
            list.Insert(refIdx, target);
            refIdx++;
            int keep = refIdx;

            for (int i = refIdx; i < list.Count; i++)
            {
                if (list[i] != target)
                {
                    list[keep++] = list[i];
                }
            }

            list.RemoveRange(keep, list.Count - keep);

            while (list.Count < active)
            {
                list.Add(null!);
            }
        }

        list.RemoveAll(p => p is null);
    }
}
