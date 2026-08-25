using System.Buffers.Binary;
using System.Text;

namespace GK3Reborn.Formats.Video.Mp4;

/// <summary>What a track carries.</summary>
public enum Mp4TrackKind
{
    /// <summary>Something the demuxer does not play: hints, subtitles, metadata.</summary>
    Other,

    /// <summary>Pictures.</summary>
    Video,

    /// <summary>Sound.</summary>
    Audio,
}

/// <summary>One sample: a compressed frame or access unit, and where it is.</summary>
/// <param name="Offset">Where it starts in the file.</param>
/// <param name="Size">How many bytes it is.</param>
/// <param name="DecodeTime">When it is decoded, in track ticks.</param>
/// <param name="CompositionOffset">
/// How far after that it is shown, in ticks. Negative with a version 1 <c>ctts</c>.
/// </param>
/// <param name="Sync">Whether decoding can start here.</param>
public readonly record struct Mp4Sample(
    long Offset, int Size, long DecodeTime, int CompositionOffset, bool Sync)
{
    /// <summary>When it is shown, in ticks.</summary>
    public long PresentationTime => DecodeTime + CompositionOffset;
}

/// <summary>
/// One track of an MP4: its codec configuration and its sample table, flattened.
/// </summary>
public sealed class Mp4Track
{
    internal Mp4Track()
    {
    }

    /// <summary>What it carries.</summary>
    public Mp4TrackKind Kind { get; internal set; }

    /// <summary>Its track id.</summary>
    public int Id { get; internal set; }

    /// <summary>The sample entry's four characters: <c>avc1</c>, <c>mp4a</c>, ...</summary>
    public string Codec { get; internal set; } = string.Empty;

    /// <summary>Ticks per second for every time in the track.</summary>
    public int Timescale { get; internal set; }

    /// <summary>How long it runs, in ticks, from the media header.</summary>
    public long Duration { get; internal set; }

    /// <summary>
    /// Ticks to subtract from every presentation time before it means anything.
    /// </summary>
    /// <remarks>
    /// The edit list. A file whose pictures are reordered, or whose sound has an encoder
    /// delay, says so by declaring that the presentation starts some way into the media
    /// rather than at its first sample; a player that ignores it shows the first few
    /// frames late and the sound early. Only the simplest and by far most common form is
    /// honoured — a single edit, possibly preceded by an empty one — which is what FFmpeg
    /// writes and what the import produces.
    /// </remarks>
    public long EditOffset { get; internal set; }

    /// <summary>Ticks of silence or blank the presentation starts with, from an empty edit.</summary>
    public long EditDelay { get; internal set; }

    /// <summary>For video: the picture's width, from the sample entry.</summary>
    public int Width { get; internal set; }

    /// <summary>For video: the picture's height, from the sample entry.</summary>
    public int Height { get; internal set; }

    /// <summary>For audio: how many channels, from the sample entry.</summary>
    public int Channels { get; internal set; }

    /// <summary>For audio: samples per second, from the sample entry.</summary>
    public int SampleRate { get; internal set; }

    /// <summary>For <c>avc1</c>: how many bytes each NAL unit's length prefix is.</summary>
    public int NalLengthSize { get; internal set; } = 4;

    /// <summary>For <c>avc1</c>: the sequence parameter sets from <c>avcC</c>.</summary>
    public List<byte[]> SequenceParameterSets { get; } = [];

    /// <summary>For <c>avc1</c>: the picture parameter sets from <c>avcC</c>.</summary>
    public List<byte[]> PictureParameterSets { get; } = [];

    /// <summary>For <c>mp4a</c>: the AudioSpecificConfig from <c>esds</c>, or empty.</summary>
    public byte[] AudioSpecificConfig { get; internal set; } = [];

    /// <summary>Every sample, in decode order.</summary>
    public List<Mp4Sample> Samples { get; } = [];

    /// <summary>Converts ticks to seconds.</summary>
    public double Seconds(long ticks) => Timescale > 0 ? ticks / (double)Timescale : 0;
}

/// <summary>
/// An ISO base media file — MP4, M4A, MOV — read far enough to hand out its samples.
/// </summary>
/// <remarks>
/// <para>
/// Only what a player needs: the sample tables of the tracks, and each track's decoder
/// configuration. Fragmented files (<c>moof</c>) are not read, because nothing produces
/// them here; the import writes <c>+faststart</c> files whose <c>moov</c> precedes the
/// data, and a file with <c>moov</c> at the end is read just as well by seeking.
/// </para>
/// <para>
/// The stream stays open for as long as the file does, because a movie is read frame by
/// frame from it rather than copied into memory first.
/// </para>
/// </remarks>
public sealed class Mp4File : IDisposable
{
    private readonly Stream _stream;
    private readonly bool _ownsStream;
    private readonly string _name;

    private Mp4File(Stream stream, bool ownsStream, string name)
    {
        _stream = stream;
        _ownsStream = ownsStream;
        _name = name;
    }

    /// <summary>Every track in the file.</summary>
    public List<Mp4Track> Tracks { get; } = [];

    /// <summary>The first video track, or null.</summary>
    public Mp4Track? Video => Tracks.Find(t => t.Kind == Mp4TrackKind.Video);

    /// <summary>The first audio track, or null.</summary>
    public Mp4Track? Audio => Tracks.Find(t => t.Kind == Mp4TrackKind.Audio);

    /// <summary>Ticks per second of the movie header.</summary>
    public int MovieTimescale { get; private set; }

    /// <summary>How long the movie runs, in movie ticks.</summary>
    public long MovieDuration { get; private set; }

    /// <summary>How long the movie runs.</summary>
    public TimeSpan Duration =>
        MovieTimescale > 0 ? TimeSpan.FromSeconds(MovieDuration / (double)MovieTimescale) : TimeSpan.Zero;

    /// <summary>Opens a file.</summary>
    /// <param name="stream">The file; must be seekable. Kept until the file is disposed.</param>
    /// <param name="name">Its name, for diagnostics.</param>
    /// <param name="ownsStream">Whether disposing the file disposes the stream.</param>
    /// <returns>The file, with its tracks read.</returns>
    /// <exception cref="FormatParseException">When it is not an MP4 or is truncated.</exception>
    public static Mp4File Open(Stream stream, string name = "<stream>", bool ownsStream = false)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (!stream.CanSeek)
        {
            throw new FormatParseException($"{name}: an MP4 has to be read from a seekable stream.");
        }

        var file = new Mp4File(stream, ownsStream, name);

        try
        {
            file.ReadTopLevel();
        }
        catch (Exception error) when (error is ArgumentOutOfRangeException or IndexOutOfRangeException or EndOfStreamException)
        {
            throw new FormatParseException($"{name}: the MP4's tables are truncated.", error);
        }

        if (file.Tracks.Count == 0)
        {
            throw new FormatParseException($"{name}: no moov box, so it is not a playable MP4.");
        }

        return file;
    }

    /// <summary>Reads one sample's bytes.</summary>
    /// <param name="sample">Which.</param>
    /// <param name="into">Where; must be at least <see cref="Mp4Sample.Size"/> long.</param>
    public void Read(in Mp4Sample sample, Span<byte> into)
    {
        if (into.Length < sample.Size)
        {
            throw new ArgumentException("The buffer is smaller than the sample.", nameof(into));
        }

        _stream.Position = sample.Offset;
        _stream.ReadExactly(into[..sample.Size]);
    }

    /// <summary>Reads one sample's bytes into a new array.</summary>
    /// <param name="sample">Which.</param>
    /// <returns>Its bytes.</returns>
    public byte[] Read(in Mp4Sample sample)
    {
        var bytes = new byte[sample.Size];
        Read(sample, bytes);
        return bytes;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_ownsStream)
        {
            _stream.Dispose();
        }
    }

    // ---- box walking -----------------------------------------------------------------

    private void ReadTopLevel()
    {
        long end = _stream.Length;
        long at = 0;

        while (at + 8 <= end)
        {
            (string type, long headerSize, long size) = ReadBoxHeader(at, end);

            if (type == "moov")
            {
                byte[] moov = ReadBytes(at + headerSize, checked((int)(size - headerSize)));
                ReadMoov(moov);
            }

            at += size;
        }
    }

    private (string Type, long HeaderSize, long Size) ReadBoxHeader(long at, long end)
    {
        Span<byte> head = stackalloc byte[16];
        _stream.Position = at;
        _stream.ReadExactly(head[..8]);

        long size = BinaryPrimitives.ReadUInt32BigEndian(head);
        string type = Encoding.ASCII.GetString(head.Slice(4, 4));
        long headerSize = 8;

        if (size == 1)
        {
            _stream.ReadExactly(head.Slice(8, 8));
            size = BinaryPrimitives.ReadInt64BigEndian(head[8..]);
            headerSize = 16;
        }
        else if (size == 0)
        {
            size = end - at;
        }

        if (size < headerSize || at + size > end)
        {
            throw new FormatParseException($"{_name}: box '{type}' at {at} runs past the end of the file.");
        }

        return (type, headerSize, size);
    }

    private byte[] ReadBytes(long at, int count)
    {
        var bytes = new byte[count];
        _stream.Position = at;
        _stream.ReadExactly(bytes);
        return bytes;
    }

    /// <summary>A box inside a buffer already in memory.</summary>
    private readonly ref struct Box
    {
        public readonly string Type;
        public readonly ReadOnlySpan<byte> Body;

        public Box(string type, ReadOnlySpan<byte> body)
        {
            Type = type;
            Body = body;
        }
    }

    /// <summary>Walks the boxes in a buffer, calling back for each.</summary>
    private delegate void BoxVisitor(Box box);

    private void Walk(ReadOnlySpan<byte> data, BoxVisitor visit)
    {
        int at = 0;

        while (at + 8 <= data.Length)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(data[at..]);
            string type = Encoding.ASCII.GetString(data.Slice(at + 4, 4));
            int header = 8;

            if (size == 1)
            {
                if (at + 16 > data.Length)
                {
                    break;
                }

                size = BinaryPrimitives.ReadInt64BigEndian(data[(at + 8)..]);
                header = 16;
            }
            else if (size == 0)
            {
                size = data.Length - at;
            }

            if (size < header || at + size > data.Length)
            {
                throw new FormatParseException($"{_name}: box '{type}' is larger than its parent.");
            }

            visit(new Box(type, data.Slice(at + header, (int)(size - header))));
            at += (int)size;
        }
    }

    private void ReadMoov(ReadOnlySpan<byte> moov)
    {
        Walk(moov, box =>
        {
            switch (box.Type)
            {
                case "mvhd":
                    ReadMvhd(box.Body);
                    break;
                case "trak":
                    ReadTrak(box.Body);
                    break;
            }
        });
    }

    private void ReadMvhd(ReadOnlySpan<byte> body)
    {
        int version = body[0];

        if (version == 1)
        {
            MovieTimescale = BinaryPrimitives.ReadInt32BigEndian(body[20..]);
            MovieDuration = BinaryPrimitives.ReadInt64BigEndian(body[24..]);
        }
        else
        {
            MovieTimescale = BinaryPrimitives.ReadInt32BigEndian(body[12..]);
            MovieDuration = BinaryPrimitives.ReadUInt32BigEndian(body[16..]);
        }
    }

    private void ReadTrak(ReadOnlySpan<byte> trak)
    {
        var track = new Mp4Track();
        var table = new SampleTable();

        Walk(trak, box =>
        {
            switch (box.Type)
            {
                case "tkhd":
                    track.Id = box.Body[0] == 1
                        ? BinaryPrimitives.ReadInt32BigEndian(box.Body[20..])
                        : BinaryPrimitives.ReadInt32BigEndian(box.Body[12..]);
                    break;
                case "edts":
                    ReadEdts(box.Body, track);
                    break;
                case "mdia":
                    ReadMdia(box.Body, track, table);
                    break;
            }
        });

        // The edit list's durations are in movie ticks; its media times in the track's.
        if (MovieTimescale > 0 && track.Timescale > 0 && track.EditDelay > 0)
        {
            track.EditDelay = track.EditDelay * track.Timescale / MovieTimescale;
        }

        table.Flatten(track, _name);
        Tracks.Add(track);
    }

    private void ReadEdts(ReadOnlySpan<byte> edts, Mp4Track track)
    {
        Walk(edts, box =>
        {
            if (box.Type != "elst")
            {
                return;
            }

            ReadOnlySpan<byte> body = box.Body;
            int version = body[0];
            int count = BinaryPrimitives.ReadInt32BigEndian(body[4..]);
            int at = 8;
            bool firstSeen = false;

            for (int i = 0; i < count; i++)
            {
                long segmentDuration;
                long mediaTime;

                if (version == 1)
                {
                    segmentDuration = BinaryPrimitives.ReadInt64BigEndian(body[at..]);
                    mediaTime = BinaryPrimitives.ReadInt64BigEndian(body[(at + 8)..]);
                    at += 20;
                }
                else
                {
                    segmentDuration = BinaryPrimitives.ReadUInt32BigEndian(body[at..]);
                    mediaTime = BinaryPrimitives.ReadInt32BigEndian(body[(at + 4)..]);
                    at += 12;
                }

                if (mediaTime == -1)
                {
                    // An empty edit: the presentation starts with nothing for this long.
                    track.EditDelay += segmentDuration;
                }
                else if (!firstSeen)
                {
                    track.EditOffset = mediaTime;
                    firstSeen = true;
                }
            }
        });
    }

    private void ReadMdia(ReadOnlySpan<byte> mdia, Mp4Track track, SampleTable table)
    {
        Walk(mdia, box =>
        {
            switch (box.Type)
            {
                case "mdhd":
                    if (box.Body[0] == 1)
                    {
                        track.Timescale = BinaryPrimitives.ReadInt32BigEndian(box.Body[20..]);
                        track.Duration = BinaryPrimitives.ReadInt64BigEndian(box.Body[24..]);
                    }
                    else
                    {
                        track.Timescale = BinaryPrimitives.ReadInt32BigEndian(box.Body[12..]);
                        track.Duration = BinaryPrimitives.ReadUInt32BigEndian(box.Body[16..]);
                    }

                    break;
                case "hdlr":
                    track.Kind = Encoding.ASCII.GetString(box.Body.Slice(8, 4)) switch
                    {
                        "vide" => Mp4TrackKind.Video,
                        "soun" => Mp4TrackKind.Audio,
                        _ => Mp4TrackKind.Other,
                    };
                    break;
                case "minf":
                    Walk(box.Body, inner =>
                    {
                        if (inner.Type == "stbl")
                        {
                            ReadStbl(inner.Body, track, table);
                        }
                    });
                    break;
            }
        });
    }

    private void ReadStbl(ReadOnlySpan<byte> stbl, Mp4Track track, SampleTable table)
    {
        Walk(stbl, box =>
        {
            ReadOnlySpan<byte> body = box.Body;

            switch (box.Type)
            {
                case "stsd":
                    ReadStsd(body, track);
                    break;
                case "stts":
                    table.TimeToSample = ReadPairs(body);
                    break;
                case "ctts":
                    table.CompositionOffsets = ReadPairs(body);
                    break;
                case "stsc":
                    table.SampleToChunk = ReadTriples(body);
                    break;
                case "stsz":
                    table.ConstantSize = BinaryPrimitives.ReadInt32BigEndian(body[4..]);
                    if (table.ConstantSize == 0)
                    {
                        int count = BinaryPrimitives.ReadInt32BigEndian(body[8..]);
                        table.Sizes = new int[count];
                        for (int i = 0; i < count; i++)
                        {
                            table.Sizes[i] = BinaryPrimitives.ReadInt32BigEndian(body[(12 + i * 4)..]);
                        }
                    }
                    else
                    {
                        table.SampleCount = BinaryPrimitives.ReadInt32BigEndian(body[8..]);
                    }

                    break;
                case "stz2":
                    ReadStz2(body, table);
                    break;
                case "stco":
                    {
                        int count = BinaryPrimitives.ReadInt32BigEndian(body[4..]);
                        table.ChunkOffsets = new long[count];
                        for (int i = 0; i < count; i++)
                        {
                            table.ChunkOffsets[i] = BinaryPrimitives.ReadUInt32BigEndian(body[(8 + i * 4)..]);
                        }

                        break;
                    }

                case "co64":
                    {
                        int count = BinaryPrimitives.ReadInt32BigEndian(body[4..]);
                        table.ChunkOffsets = new long[count];
                        for (int i = 0; i < count; i++)
                        {
                            table.ChunkOffsets[i] = BinaryPrimitives.ReadInt64BigEndian(body[(8 + i * 8)..]);
                        }

                        break;
                    }

                case "stss":
                    {
                        int count = BinaryPrimitives.ReadInt32BigEndian(body[4..]);
                        table.SyncSamples = new int[count];
                        for (int i = 0; i < count; i++)
                        {
                            table.SyncSamples[i] = BinaryPrimitives.ReadInt32BigEndian(body[(8 + i * 4)..]);
                        }

                        break;
                    }
            }
        });
    }

    private static void ReadStz2(ReadOnlySpan<byte> body, SampleTable table)
    {
        int fieldSize = body[7];
        int count = BinaryPrimitives.ReadInt32BigEndian(body[8..]);
        table.Sizes = new int[count];
        ReadOnlySpan<byte> data = body[12..];

        for (int i = 0; i < count; i++)
        {
            table.Sizes[i] = fieldSize switch
            {
                4 => (data[i / 2] >> ((i & 1) == 0 ? 4 : 0)) & 0xF,
                8 => data[i],
                16 => BinaryPrimitives.ReadUInt16BigEndian(data[(i * 2)..]),
                _ => throw new FormatParseException($"stz2 field size {fieldSize} is not 4, 8 or 16."),
            };
        }
    }

    private static (int, int)[] ReadPairs(ReadOnlySpan<byte> body)
    {
        int count = BinaryPrimitives.ReadInt32BigEndian(body[4..]);
        var pairs = new (int, int)[count];

        for (int i = 0; i < count; i++)
        {
            pairs[i] = (
                BinaryPrimitives.ReadInt32BigEndian(body[(8 + i * 8)..]),
                BinaryPrimitives.ReadInt32BigEndian(body[(12 + i * 8)..]));
        }

        return pairs;
    }

    private static (int, int, int)[] ReadTriples(ReadOnlySpan<byte> body)
    {
        int count = BinaryPrimitives.ReadInt32BigEndian(body[4..]);
        var triples = new (int, int, int)[count];

        for (int i = 0; i < count; i++)
        {
            triples[i] = (
                BinaryPrimitives.ReadInt32BigEndian(body[(8 + i * 12)..]),
                BinaryPrimitives.ReadInt32BigEndian(body[(12 + i * 12)..]),
                BinaryPrimitives.ReadInt32BigEndian(body[(16 + i * 12)..]));
        }

        return triples;
    }

    private void ReadStsd(ReadOnlySpan<byte> body, Mp4Track track)
    {
        // Full box header, then entry count; only the first entry is used.
        Walk(body[8..], entry =>
        {
            if (track.Codec.Length > 0)
            {
                return;
            }

            track.Codec = entry.Type;
            ReadOnlySpan<byte> e = entry.Body;

            switch (track.Kind)
            {
                case Mp4TrackKind.Video:
                    // VisualSampleEntry: 6 reserved, 2 data reference index, 16 predefined
                    // and reserved, then width and height.
                    track.Width = BinaryPrimitives.ReadUInt16BigEndian(e[24..]);
                    track.Height = BinaryPrimitives.ReadUInt16BigEndian(e[26..]);
                    // 78 bytes of VisualSampleEntry, then the codec's own boxes.
                    Walk(e[78..], inner =>
                    {
                        if (inner.Type == "avcC")
                        {
                            ReadAvcC(inner.Body, track);
                        }
                    });
                    break;

                case Mp4TrackKind.Audio:
                    {
                        // AudioSampleEntry: 6 reserved, 2 data reference index, then a
                        // version (QuickTime) that changes how long the entry is.
                        int version = BinaryPrimitives.ReadUInt16BigEndian(e[8..]);
                        track.Channels = BinaryPrimitives.ReadUInt16BigEndian(e[16..]);
                        track.SampleRate = BinaryPrimitives.ReadUInt16BigEndian(e[24..]);
                        int length = version switch { 1 => 28 + 16, 2 => 28 + 36, _ => 28 };
                        if (e.Length > length)
                        {
                            Walk(e[length..], inner =>
                            {
                                if (inner.Type == "esds")
                                {
                                    track.AudioSpecificConfig = ReadEsds(inner.Body);
                                }
                            });
                        }

                        break;
                    }
            }
        });
    }

    private static void ReadAvcC(ReadOnlySpan<byte> body, Mp4Track track)
    {
        // configurationVersion, profile, compatibility, level, then lengthSizeMinusOne
        // in the low two bits, then the parameter sets.
        track.NalLengthSize = (body[4] & 3) + 1;
        int at = 5;
        int spsCount = body[at++] & 0x1F;

        for (int i = 0; i < spsCount; i++)
        {
            int length = BinaryPrimitives.ReadUInt16BigEndian(body[at..]);
            track.SequenceParameterSets.Add(body.Slice(at + 2, length).ToArray());
            at += 2 + length;
        }

        int ppsCount = body[at++];

        for (int i = 0; i < ppsCount; i++)
        {
            int length = BinaryPrimitives.ReadUInt16BigEndian(body[at..]);
            track.PictureParameterSets.Add(body.Slice(at + 2, length).ToArray());
            at += 2 + length;
        }
    }

    /// <summary>Digs the AudioSpecificConfig out of an MPEG-4 elementary stream descriptor.</summary>
    private static byte[] ReadEsds(ReadOnlySpan<byte> body)
    {
        int at = 4; // full box header

        // ES_Descriptor (tag 3)
        if (at >= body.Length || body[at++] != 0x03)
        {
            return [];
        }

        ReadDescriptorLength(body, ref at);
        at += 2; // ES_ID
        int flags = body[at++];
        if ((flags & 0x80) != 0)
        {
            at += 2; // dependsOn_ES_ID
        }

        if ((flags & 0x40) != 0)
        {
            at += body[at] + 1; // URL
        }

        if ((flags & 0x20) != 0)
        {
            at += 2; // OCR_ES_Id
        }

        // DecoderConfigDescriptor (tag 4)
        if (at >= body.Length || body[at++] != 0x04)
        {
            return [];
        }

        int configLength = ReadDescriptorLength(body, ref at);
        int configEnd = at + configLength;
        at += 13; // objectTypeIndication, streamType, bufferSizeDB, maxBitrate, avgBitrate

        // DecoderSpecificInfo (tag 5)
        if (at >= body.Length || at >= configEnd || body[at++] != 0x05)
        {
            return [];
        }

        int infoLength = ReadDescriptorLength(body, ref at);
        return body.Slice(at, Math.Min(infoLength, body.Length - at)).ToArray();
    }

    private static int ReadDescriptorLength(ReadOnlySpan<byte> body, ref int at)
    {
        int length = 0;

        for (int i = 0; i < 4; i++)
        {
            int b = body[at++];
            length = (length << 7) | (b & 0x7F);
            if ((b & 0x80) == 0)
            {
                break;
            }
        }

        return length;
    }

    /// <summary>The raw sample tables, before they are flattened into one list.</summary>
    private sealed class SampleTable
    {
        public (int Count, int Delta)[] TimeToSample = [];
        public (int Count, int Offset)[] CompositionOffsets = [];
        public (int FirstChunk, int SamplesPerChunk, int DescriptionIndex)[] SampleToChunk = [];
        public int[]? Sizes;
        public int ConstantSize;
        public int SampleCount;
        public long[] ChunkOffsets = [];
        public int[]? SyncSamples;

        public void Flatten(Mp4Track track, string name)
        {
            int count = Sizes?.Length ?? SampleCount;

            if (count == 0)
            {
                return;
            }

            if (ChunkOffsets.Length == 0 || SampleToChunk.Length == 0)
            {
                throw new FormatParseException($"{name}: track {track.Id} has samples but no chunks.");
            }

            // Where each sample is: walk the chunks, each holding a run of samples.
            var offsets = new long[count];
            int sample = 0;

            for (int run = 0; run < SampleToChunk.Length && sample < count; run++)
            {
                int firstChunk = SampleToChunk[run].FirstChunk - 1;
                int lastChunk = run + 1 < SampleToChunk.Length
                    ? SampleToChunk[run + 1].FirstChunk - 1
                    : ChunkOffsets.Length;
                int perChunk = SampleToChunk[run].SamplesPerChunk;

                for (int chunk = firstChunk; chunk < lastChunk && sample < count; chunk++)
                {
                    if (chunk >= ChunkOffsets.Length)
                    {
                        throw new FormatParseException($"{name}: track {track.Id} refers to a chunk it does not have.");
                    }

                    long at = ChunkOffsets[chunk];

                    for (int i = 0; i < perChunk && sample < count; i++, sample++)
                    {
                        offsets[sample] = at;
                        at += Sizes?[sample] ?? ConstantSize;
                    }
                }
            }

            if (sample < count)
            {
                throw new FormatParseException($"{name}: track {track.Id} places only {sample} of {count} samples.");
            }

            // When each is decoded.
            var decodeTimes = new long[count];
            long time = 0;
            sample = 0;

            foreach ((int runCount, int delta) in TimeToSample)
            {
                for (int i = 0; i < runCount && sample < count; i++, sample++)
                {
                    decodeTimes[sample] = time;
                    time += delta;
                }
            }

            for (; sample < count; sample++)
            {
                decodeTimes[sample] = time;
            }

            // How much later each is shown.
            var ctsOffsets = new int[count];
            sample = 0;

            foreach ((int runCount, int offset) in CompositionOffsets)
            {
                for (int i = 0; i < runCount && sample < count; i++, sample++)
                {
                    ctsOffsets[sample] = offset;
                }
            }

            var sync = new bool[count];

            if (SyncSamples is null)
            {
                Array.Fill(sync, true);
            }
            else
            {
                foreach (int index in SyncSamples)
                {
                    if (index >= 1 && index <= count)
                    {
                        sync[index - 1] = true;
                    }
                }
            }

            track.Samples.Capacity = count;

            for (int i = 0; i < count; i++)
            {
                track.Samples.Add(new Mp4Sample(
                    offsets[i], Sizes?[i] ?? ConstantSize, decodeTimes[i], ctsOffsets[i], sync[i]));
            }
        }
    }
}
