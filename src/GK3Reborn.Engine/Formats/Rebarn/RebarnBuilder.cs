using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace GK3Reborn.Formats.Rebarn;

/// <summary>
/// Writes a ReBarn pack.
/// </summary>
/// <remarks>
/// <para>
/// One streaming pass. Sources are added by path or by bytes, then <see cref="Write"/>
/// copies each one into the volume in turn, recording where it landed, and finishes with
/// the name table, the index and a rewritten header. Nothing is held in memory but the
/// index — fifteen gigabytes of textures go through a one-megabyte buffer.
/// </para>
/// <para>
/// The order entries are written in is the order they were added, and the index is sorted
/// by key hash afterwards, so a pack built twice from the same inputs is byte for byte the
/// same file. That is what lets a build be compared, and what stops a rebuild looking like
/// a change to anything watching the directory.
/// </para>
/// </remarks>
public sealed class RebarnBuilder
{
    private readonly List<Source> _sources = [];
    private readonly HashSet<string> _keys = new(StringComparer.Ordinal);

    /// <summary>Which volume of a multi-file set this will be.</summary>
    public ushort Volume { get; set; }

    /// <summary>How many entries have been added.</summary>
    public int Count => _sources.Count;

    /// <summary>How many bytes the sources are, before alignment.</summary>
    public long SourceBytes => _sources.Sum(s => s.Length);

    /// <summary>Adds a file.</summary>
    /// <param name="kind">What the entry is for.</param>
    /// <param name="path">The file to pack.</param>
    /// <param name="name">
    /// The name it answers to, or null to take the file's own name. The extension is kept
    /// so that an unpacked entry gets its original spelling back, but it plays no part in
    /// the key.
    /// </param>
    /// <param name="compression">How to store it; <see cref="RebarnCompression.Store"/> by default.</param>
    /// <returns>True when it was added, false when the same key was already present.</returns>
    /// <remarks>
    /// A duplicate key is refused rather than overwriting, because the two candidates are
    /// two different files on disk and picking one silently is how a pack comes to hold
    /// something nobody chose. The caller decides which to keep.
    /// </remarks>
    public bool AddFile(
        RebarnKind kind,
        string path,
        string? name = null,
        RebarnCompression compression = RebarnCompression.Store)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string entryName = name ?? Path.GetFileName(path);
        string key = RebarnFormat.Key(kind, entryName);

        if (!_keys.Add(key))
        {
            return false;
        }

        _sources.Add(new Source(
            kind,
            entryName,
            path,
            null,
            new FileInfo(path).Length,
            RebarnFormat.PayloadOf(path),
            compression));

        return true;
    }

    /// <summary>Adds bytes the caller already holds.</summary>
    /// <param name="kind">What the entry is for.</param>
    /// <param name="name">The name it answers to, extension included.</param>
    /// <param name="bytes">Its content.</param>
    /// <param name="payload">What the bytes are.</param>
    /// <param name="compression">How to store them.</param>
    /// <returns>True when it was added, false when the same key was already present.</returns>
    public bool AddBytes(
        RebarnKind kind,
        string name,
        byte[] bytes,
        RebarnPayload payload = RebarnPayload.Raw,
        RebarnCompression compression = RebarnCompression.Store)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(bytes);

        string key = RebarnFormat.Key(kind, name);

        if (!_keys.Add(key))
        {
            return false;
        }

        _sources.Add(new Source(kind, name, null, bytes, bytes.Length, payload, compression));
        return true;
    }

    /// <summary>Whether an entry with this key has been added.</summary>
    /// <param name="kind">What the entry is for.</param>
    /// <param name="name">The name.</param>
    /// <returns>True when it has.</returns>
    public bool Has(RebarnKind kind, string name) => _keys.Contains(RebarnFormat.Key(kind, name));

    /// <summary>Writes the pack.</summary>
    /// <param name="path">Where to write it. An existing file is replaced.</param>
    /// <param name="progress">Called after each entry with its name and the bytes written so far.</param>
    /// <returns>What the volume came to.</returns>
    public RebarnVolumeReport Write(string path, Action<string, long>? progress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));

        if (directory is { Length: > 0 })
        {
            Directory.CreateDirectory(directory);
        }

        var written = new List<Written>(_sources.Count);
        long dataOffset = RebarnFormat.HeaderBytes;

        using (var stream = new FileStream(
            path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        {
            stream.SetLength(0);
            stream.Position = dataOffset;

            foreach (Source source in _sources)
            {
                Pad(stream);

                long at = stream.Position;
                (long stored, uint crc) = Copy(stream, source);

                written.Add(new Written(source, at, stored, crc));
                progress?.Invoke(source.Name, stream.Position);
            }

            Pad(stream);

            long nameTableOffset = stream.Position;
            byte[] names = BuildNameTable(written, out int[] offsets);
            stream.Write(names);

            long indexOffset = stream.Position;
            byte[] index = BuildIndex(written, offsets);
            stream.Write(index);

            ulong hash = Fnv(index, Fnv(names, 14695981039346656037UL));

            var header = new RebarnHeader
            {
                Version = RebarnFormat.Version,
                Volume = Volume,
                Flags = 0,
                EntryCount = (uint)written.Count,
                IndexOffset = indexOffset,
                NameTableOffset = nameTableOffset,
                NameTableLength = names.Length,
                DataOffset = dataOffset,
                IndexHash = hash,
                BuiltUtcTicks = DateTime.UtcNow.Ticks,
            };

            Span<byte> head = stackalloc byte[RebarnFormat.HeaderBytes];
            RebarnFormat.WriteHeader(head, header);

            stream.Position = 0;
            stream.Write(head);
        }

        long total = new FileInfo(path).Length;

        return new RebarnVolumeReport(
            path,
            written.Count,
            total,
            written.Sum(w => w.Source.Length),
            [.. written
                .GroupBy(w => w.Source.Kind)
                .OrderBy(g => g.Key)
                .Select(g => new RebarnKindReport(g.Key, g.Count(), g.Sum(w => w.Stored)))]);
    }

    private static void Pad(FileStream stream)
    {
        long aligned = RebarnFormat.Align(stream.Position);

        if (aligned == stream.Position)
        {
            return;
        }

        Span<byte> zeros = stackalloc byte[RebarnFormat.Alignment];
        zeros.Clear();
        stream.Write(zeros[..(int)(aligned - stream.Position)]);
    }

    private static (long Stored, uint Crc) Copy(FileStream into, Source source)
    {
        if (source.Compression == RebarnCompression.Deflate)
        {
            // Compressed into memory first, so the CRC is of the bytes that will be on
            // disk without reading them back out of a file opened for writing. Only
            // manifests, models and other small things take this path — a block-compressed
            // texture is stored, and is where all the size is.
            using var buffered = new MemoryStream();

            using (var deflate = new DeflateStream(buffered, CompressionLevel.Optimal, leaveOpen: true))
            {
                WriteBody(deflate, source);
            }

            byte[] compressed = buffered.ToArray();
            into.Write(compressed);

            return (compressed.Length, Crc32.Compute(compressed));
        }

        uint crc = Crc32.Begin();
        long written = 0;

        if (source.Bytes is { } bytes)
        {
            into.Write(bytes);
            crc = Crc32.Continue(crc, bytes);
            written = bytes.Length;
        }
        else
        {
            using var file = new FileStream(
                source.Path!, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);

            byte[] buffer = new byte[1 << 20];
            int read;

            while ((read = file.Read(buffer, 0, buffer.Length)) > 0)
            {
                into.Write(buffer, 0, read);
                crc = Crc32.Continue(crc, buffer.AsSpan(0, read));
                written += read;
            }
        }

        return (written, Crc32.End(crc));
    }

    private static void WriteBody(Stream into, Source source)
    {
        if (source.Bytes is { } bytes)
        {
            into.Write(bytes);
            return;
        }

        using var file = new FileStream(
            source.Path!, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);

        file.CopyTo(into, 1 << 20);
    }

    private static byte[] BuildNameTable(List<Written> written, out int[] offsets)
    {
        offsets = new int[written.Count];
        var table = new List<byte>(written.Count * 16);

        for (int i = 0; i < written.Count; i++)
        {
            offsets[i] = table.Count;
            table.AddRange(Encoding.UTF8.GetBytes(written[i].Source.Name));
        }

        return [.. table];
    }

    private static byte[] BuildIndex(List<Written> written, int[] offsets)
    {
        // Sorted by key hash, then by name, so that the same inputs always produce the same
        // file. The name breaks a hash collision, which two distinct keys may always have.
        var order = Enumerable.Range(0, written.Count)
            .OrderBy(i => RebarnFormat.Hash(RebarnFormat.Key(
                written[i].Source.Kind, written[i].Source.Name)))
            .ThenBy(i => written[i].Source.Name, StringComparer.Ordinal)
            .ToArray();

        var index = new byte[written.Count * RebarnFormat.EntryBytes];

        for (int slot = 0; slot < order.Length; slot++)
        {
            int i = order[slot];
            Written entry = written[i];
            Span<byte> record = index.AsSpan(slot * RebarnFormat.EntryBytes, RebarnFormat.EntryBytes);

            byte[] name = Encoding.UTF8.GetBytes(entry.Source.Name);

            BinaryPrimitives.WriteUInt64LittleEndian(
                record, RebarnFormat.Hash(RebarnFormat.Key(entry.Source.Kind, entry.Source.Name)));
            BinaryPrimitives.WriteInt64LittleEndian(record[8..], entry.Offset);
            BinaryPrimitives.WriteInt64LittleEndian(record[16..], entry.Stored);
            BinaryPrimitives.WriteInt64LittleEndian(record[24..], entry.Source.Length);
            BinaryPrimitives.WriteInt32LittleEndian(record[32..], offsets[i]);
            BinaryPrimitives.WriteUInt16LittleEndian(record[36..], (ushort)name.Length);
            record[38] = (byte)entry.Source.Kind;
            record[39] = (byte)entry.Source.Payload;
            record[40] = (byte)entry.Source.Compression;
            record[41] = 0;
            BinaryPrimitives.WriteUInt16LittleEndian(record[42..], 0);
            BinaryPrimitives.WriteUInt32LittleEndian(record[44..], entry.Crc);
        }

        return index;
    }

    private static ulong Fnv(ReadOnlySpan<byte> data, ulong seed)
    {
        ulong hash = seed;

        foreach (byte b in data)
        {
            hash ^= b;
            hash *= 1099511628211UL;
        }

        return hash;
    }

    private sealed record Source(
        RebarnKind Kind,
        string Name,
        string? Path,
        byte[]? Bytes,
        long Length,
        RebarnPayload Payload,
        RebarnCompression Compression);

    private sealed record Written(Source Source, long Offset, long Stored, uint Crc);
}

/// <summary>What one kind came to in a written volume.</summary>
/// <param name="Kind">The kind.</param>
/// <param name="Count">How many entries of it there are.</param>
/// <param name="Bytes">How many bytes they occupy.</param>
public readonly record struct RebarnKindReport(RebarnKind Kind, int Count, long Bytes);

/// <summary>What a written volume came to.</summary>
/// <param name="Path">Where it was written.</param>
/// <param name="Count">How many entries it holds.</param>
/// <param name="Bytes">How large the file is.</param>
/// <param name="SourceBytes">How large the sources were, before compression and alignment.</param>
/// <param name="Kinds">The breakdown by kind.</param>
public sealed record RebarnVolumeReport(
    string Path,
    int Count,
    long Bytes,
    long SourceBytes,
    IReadOnlyList<RebarnKindReport> Kinds);
