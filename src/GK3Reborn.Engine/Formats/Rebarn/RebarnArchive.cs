using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;
using GK3Reborn.Foundation.Diagnostics;
using Microsoft.Win32.SafeHandles;

namespace GK3Reborn.Formats.Rebarn;

/// <summary>
/// Reads one ReBarn pack.
/// </summary>
/// <remarks>
/// <para>
/// The whole volume is memory-mapped once and never read into the heap. A 2048-pixel BC7
/// texture is 5.6 MB, a room wants dozens of them, and they are read on every core at
/// once: copying each one out of the file into a byte array before uploading it doubles
/// the high-water mark of a scene load to achieve nothing at all.
/// <see cref="ReadMapped(RebarnEntry)"/>
/// hands back a window onto the mapping instead.
/// </para>
/// <para>
/// That window is only valid while the archive is open, which is the one sharp edge here
/// and the reason <see cref="Read(RebarnEntry)"/> — which copies — is the default. The engine keeps its
/// packs open for the life of the process, so the mapped path is safe there; a tool that
/// opens a pack in a <c>using</c> and holds the bytes afterwards would read freed address
/// space. <see cref="ReadMapped(RebarnEntry)"/> says so on its own summary.
/// </para>
/// <para>
/// Mapping costs address space rather than memory. An eleven-gigabyte volume reserves
/// eleven gigabytes of a 64-bit address space and pages in only what is touched, so a
/// session that visits four rooms pays for four rooms.
/// </para>
/// </remarks>
public sealed class RebarnArchive : IDisposable
{
    private readonly MemoryMappedFile _file;
    private readonly MemoryMappedViewAccessor _view;
    private readonly SafeMemoryMappedViewHandle _handle;
    private readonly Dictionary<string, RebarnEntry> _entries;
    private readonly long _length;

    private unsafe byte* _base;
    private bool _disposed;

    private unsafe RebarnArchive(
        string path,
        MemoryMappedFile file,
        MemoryMappedViewAccessor view,
        byte* origin,
        long length,
        RebarnHeader header,
        Dictionary<string, RebarnEntry> entries)
    {
        Path = path;
        Name = System.IO.Path.GetFileName(path);
        _file = file;
        _view = view;
        _handle = view.SafeMemoryMappedViewHandle;
        _base = origin;
        _length = length;
        Header = header;
        _entries = entries;
    }

    /// <summary>Where the pack was opened from.</summary>
    public string Path { get; }

    /// <summary>The pack's file name, for diagnostics.</summary>
    public string Name { get; }

    /// <summary>The pack's header.</summary>
    public RebarnHeader Header { get; }

    /// <summary>How many entries the pack holds.</summary>
    public int Count => _entries.Count;

    /// <summary>How many bytes the volume is.</summary>
    public long Length => _length;

    /// <summary>Every entry, ordered by kind and then by name.</summary>
    public IReadOnlyList<RebarnEntry> Entries =>
        [.. _entries.Values
            .OrderBy(e => e.Kind)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)];

    /// <summary>Opens a pack and parses its index.</summary>
    /// <param name="path">Path to the <c>.rebarn</c> file.</param>
    /// <returns>The opened pack.</returns>
    /// <exception cref="FormatParseException">The file is not a ReBarn pack, or is corrupt.</exception>
    public static unsafe RebarnArchive Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string name = System.IO.Path.GetFileName(path);
        long length = new FileInfo(path).Length;

        if (length < RebarnFormat.HeaderBytes)
        {
            throw new FormatParseException(new Diagnostic(
                "GK3R1170",
                DiagnosticSeverity.Error,
                $"{name} is too short to be a ReBarn pack.",
                path,
                0,
                $"at least {RebarnFormat.HeaderBytes} bytes",
                $"{length} bytes",
                "Produce it again with `pack-content`."));
        }

        MemoryMappedFile file = MemoryMappedFile.CreateFromFile(
            path, FileMode.Open, mapName: null, capacity: 0, MemoryMappedFileAccess.Read);

        MemoryMappedViewAccessor? view = null;
        byte* origin = null;

        try
        {
            view = file.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref origin);

            // A view can begin before the offset asked for, on the system's allocation
            // granularity. Zero was asked for, so this is zero — but reading it rather
            // than assuming it is what makes every offset below simply an offset.
            origin += view.PointerOffset;

            var head = new ReadOnlySpan<byte>(origin, RebarnFormat.HeaderBytes);
            RebarnHeader header = RebarnFormat.ReadHeader(head, name);

            Dictionary<string, RebarnEntry> entries = ReadIndex(origin, length, header, path, name);

            return new RebarnArchive(path, file, view, origin, length, header, entries);
        }
        catch
        {
            if (origin is not null && view is not null)
            {
                view.SafeMemoryMappedViewHandle.ReleasePointer();
            }

            view?.Dispose();
            file.Dispose();
            throw;
        }
    }

    private static unsafe Dictionary<string, RebarnEntry> ReadIndex(
        byte* origin, long length, in RebarnHeader header, string path, string name)
    {
        long indexLength = (long)header.EntryCount * RebarnFormat.EntryBytes;

        if (header.IndexOffset < 0 ||
            header.NameTableOffset < 0 ||
            header.NameTableLength < 0 ||
            header.IndexOffset + indexLength > length ||
            header.NameTableOffset + header.NameTableLength > length)
        {
            throw new FormatParseException(new Diagnostic(
                "GK3R1172",
                DiagnosticSeverity.Error,
                $"{name} says its index runs past the end of the file, so it is truncated.",
                path,
                header.IndexOffset,
                $"an index of {indexLength} bytes inside {length}",
                $"index at {header.IndexOffset}, names at {header.NameTableOffset}",
                "Produce it again with `pack-content`; a partial copy cannot be repaired."));
        }

        var index = new ReadOnlySpan<byte>(origin + header.IndexOffset, checked((int)indexLength));
        var names = new ReadOnlySpan<byte>(
            origin + header.NameTableOffset, checked((int)header.NameTableLength));

        ulong hash = Fnv(index, Fnv(names, 14695981039346656037UL));

        if (hash != header.IndexHash)
        {
            throw new FormatParseException(new Diagnostic(
                "GK3R1173",
                DiagnosticSeverity.Error,
                $"{name} has an index that does not match its own checksum, so it is damaged.",
                path,
                header.IndexOffset,
                header.IndexHash.ToString("X16", System.Globalization.CultureInfo.InvariantCulture),
                hash.ToString("X16", System.Globalization.CultureInfo.InvariantCulture),
                "Produce it again with `pack-content`."));
        }

        var entries = new Dictionary<string, RebarnEntry>(
            (int)header.EntryCount, StringComparer.Ordinal);

        for (int i = 0; i < header.EntryCount; i++)
        {
            ReadOnlySpan<byte> record = index.Slice(i * RebarnFormat.EntryBytes, RebarnFormat.EntryBytes);

            long offset = BinaryPrimitives.ReadInt64LittleEndian(record[8..]);
            long stored = BinaryPrimitives.ReadInt64LittleEndian(record[16..]);
            long actual = BinaryPrimitives.ReadInt64LittleEndian(record[24..]);
            int nameOffset = BinaryPrimitives.ReadInt32LittleEndian(record[32..]);
            int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(record[36..]);
            var kind = (RebarnKind)record[38];
            var payload = (RebarnPayload)record[39];
            var compression = (RebarnCompression)record[40];
            uint crc = BinaryPrimitives.ReadUInt32LittleEndian(record[44..]);

            if (offset < 0 || stored < 0 || offset + stored > length ||
                nameOffset < 0 || nameOffset + nameLength > names.Length)
            {
                throw new FormatParseException(new Diagnostic(
                    "GK3R1172",
                    DiagnosticSeverity.Error,
                    $"{name} has an entry pointing outside the file, so it is truncated.",
                    path,
                    header.IndexOffset + ((long)i * RebarnFormat.EntryBytes),
                    $"bytes inside a {length}-byte file",
                    $"{stored} bytes at {offset}",
                    "Produce it again with `pack-content`."));
            }

            string entryName = Encoding.UTF8.GetString(names.Slice(nameOffset, nameLength));

            var entry = new RebarnEntry(
                kind, entryName, offset, stored, actual, payload, compression, crc);

            entries[entry.Key] = entry;
        }

        return entries;
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

    /// <summary>Looks up an entry.</summary>
    /// <param name="kind">What the entry is for.</param>
    /// <param name="name">The name, with or without an extension.</param>
    /// <returns>The entry, or null when the pack does not hold it.</returns>
    public RebarnEntry? Find(RebarnKind kind, string name) =>
        _entries.GetValueOrDefault(RebarnFormat.Key(kind, name));

    /// <summary>Whether the pack holds an entry.</summary>
    /// <param name="kind">What the entry is for.</param>
    /// <param name="name">The name, with or without an extension.</param>
    /// <returns>True when it does.</returns>
    public bool Has(RebarnKind kind, string name) =>
        _entries.ContainsKey(RebarnFormat.Key(kind, name));

    /// <summary>Every name of one kind, in a stable order.</summary>
    /// <param name="kind">What the entries are for.</param>
    /// <returns>The names, without extensions, ordered.</returns>
    public IReadOnlyList<string> Names(RebarnKind kind) =>
        [.. _entries.Values
            .Where(e => e.Kind == kind)
            .Select(e => System.IO.Path.GetFileNameWithoutExtension(e.Name))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)];

    /// <summary>Reads an entry into a new array.</summary>
    /// <param name="entry">The entry, from <see cref="Find"/>.</param>
    /// <returns>Its bytes, decompressed.</returns>
    /// <exception cref="FormatParseException">The stored bytes will not decompress.</exception>
    public byte[] Read(RebarnEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ObjectDisposedException.ThrowIf(_disposed, this);

        ReadOnlyMemory<byte> stored = Window(entry);

        if (entry.Compression == RebarnCompression.Store)
        {
            return stored.ToArray();
        }

        var result = new byte[entry.Length];
        Inflate(stored, result, entry);
        return result;
    }

    /// <summary>Reads an entry, with or without an extension, into a new array.</summary>
    /// <param name="kind">What the entry is for.</param>
    /// <param name="name">The name.</param>
    /// <returns>Its bytes, or null when the pack does not hold it.</returns>
    public byte[]? Read(RebarnKind kind, string name) =>
        Find(kind, name) is { } entry ? Read(entry) : null;

    /// <summary>
    /// Reads an entry without copying it, when that is possible.
    /// </summary>
    /// <param name="entry">The entry, from <see cref="Find"/>.</param>
    /// <returns>Its bytes.</returns>
    /// <remarks>
    /// A window onto the memory-mapped file for a stored entry, and a fresh array for a
    /// compressed one. <strong>The window is only valid while this archive is open.</strong>
    /// Use it for something that is consumed within the call — uploading a texture to the
    /// device — and <see cref="Read(RebarnEntry)"/> for anything that outlives it.
    /// </remarks>
    public ReadOnlyMemory<byte> ReadMapped(RebarnEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ObjectDisposedException.ThrowIf(_disposed, this);

        return entry.Compression == RebarnCompression.Store
            ? Window(entry)
            : Read(entry);
    }

    /// <summary>Reads an entry by name without copying it, when that is possible.</summary>
    /// <param name="kind">What the entry is for.</param>
    /// <param name="name">The name.</param>
    /// <returns>Its bytes, or null when the pack does not hold it.</returns>
    /// <remarks>See <see cref="ReadMapped(RebarnEntry)"/> for how long the result lives.</remarks>
    public ReadOnlyMemory<byte>? ReadMapped(RebarnKind kind, string name) =>
        Find(kind, name) is { } entry ? ReadMapped(entry) : null;

    /// <summary>Copies an entry into a stream, without holding it all in memory.</summary>
    /// <param name="entry">The entry.</param>
    /// <param name="into">Where to write it.</param>
    public void CopyTo(RebarnEntry entry, Stream into)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(into);
        ObjectDisposedException.ThrowIf(_disposed, this);

        ReadOnlyMemory<byte> stored = Window(entry);

        if (entry.Compression == RebarnCompression.Store)
        {
            // In pieces rather than in one write, so that a six-megabyte texture does not
            // ask the file system for a six-megabyte contiguous transfer.
            const int Chunk = 1 << 20;

            for (int at = 0; at < stored.Length; at += Chunk)
            {
                into.Write(stored.Span.Slice(at, Math.Min(Chunk, stored.Length - at)));
            }

            return;
        }

        using var source = new MemoryStream(stored.ToArray(), writable: false);
        using var inflate = new DeflateStream(source, CompressionMode.Decompress);
        inflate.CopyTo(into);
    }

    /// <summary>Checks an entry's stored bytes against the CRC the index carries.</summary>
    /// <param name="entry">The entry.</param>
    /// <returns>True when they match, or when the pack recorded no CRC for it.</returns>
    public bool Verify(RebarnEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ObjectDisposedException.ThrowIf(_disposed, this);

        return entry.Crc32 == 0 || Crc32.Compute(Window(entry).Span) == entry.Crc32;
    }

    private unsafe ReadOnlyMemory<byte> Window(RebarnEntry entry)
    {
        if (entry.StoredLength > int.MaxValue)
        {
            throw new FormatParseException(new Diagnostic(
                "GK3R1174",
                DiagnosticSeverity.Error,
                $"{entry.Name} is {entry.StoredLength} bytes, past what one entry may be.",
                Path,
                entry.Offset,
                "at most 2 GiB",
                $"{entry.StoredLength} bytes",
                "Split the asset, or store it outside the pack."));
        }

        return new MappedRegion(_base + entry.Offset, (int)entry.StoredLength).Memory;
    }

    private void Inflate(ReadOnlyMemory<byte> stored, byte[] into, RebarnEntry entry)
    {
        using var source = new MemoryStream(
            MemoryMarshal.TryGetArray(stored, out ArraySegment<byte> segment) && segment.Array is not null
                ? segment.Array
                : stored.ToArray(),
            writable: false);

        using var inflate = new DeflateStream(source, CompressionMode.Decompress);

        int at = 0;

        while (at < into.Length)
        {
            int read = inflate.Read(into, at, into.Length - at);

            if (read == 0)
            {
                throw new FormatParseException(new Diagnostic(
                    "GK3R1175",
                    DiagnosticSeverity.Error,
                    $"{entry.Name} ran out of compressed data {at} bytes into {into.Length}.",
                    Path,
                    entry.Offset,
                    $"{into.Length} bytes",
                    $"{at} bytes",
                    "Produce the pack again with `pack-content`."));
            }

            at += read;
        }
    }

    /// <inheritdoc/>
    public unsafe void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_base is not null)
        {
            _handle.ReleasePointer();
            _base = null;
        }

        _view.Dispose();
        _file.Dispose();
    }

    /// <summary>A window onto mapped memory, so an entry can be read without being copied.</summary>
    private sealed unsafe class MappedRegion : MemoryManager<byte>
    {
        private readonly byte* _pointer;
        private readonly int _length;

        internal MappedRegion(byte* pointer, int length)
        {
            _pointer = pointer;
            _length = length;
        }

        public override Span<byte> GetSpan() => new(_pointer, _length);

        public override MemoryHandle Pin(int elementIndex = 0)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(elementIndex);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(elementIndex, _length);

            // Already pinned: the mapping does not move and the archive holds the pointer.
            return new MemoryHandle(_pointer + elementIndex);
        }

        public override void Unpin()
        {
        }

        protected override void Dispose(bool disposing)
        {
        }
    }
}
