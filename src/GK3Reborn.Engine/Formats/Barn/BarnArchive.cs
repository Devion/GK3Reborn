using System.Buffers.Binary;
using System.IO.Compression;
using GK3Reborn.Formats.Compression;
using GK3Reborn.Foundation;
using GK3Reborn.Foundation.Diagnostics;

namespace GK3Reborn.Formats.Barn;

/// <summary>How an entry's bytes are stored in the archive.</summary>
public enum BarnCompression
{
    /// <summary>Stored verbatim.</summary>
    None = 0,

    /// <summary>zlib stream, header included.</summary>
    Zlib = 1,

    /// <summary>LZO1X stream.</summary>
    Lzo = 2,
}

/// <summary>One entry in a Barn archive's directory.</summary>
/// <remarks>
/// An archive can hold entries that are *pointers* into another archive rather than
/// data: the directory names the asset and the archive that really holds it. Those
/// entries carry no bytes here, which is why <see cref="ReferencedArchive"/> has to be
/// checked before extraction rather than after a confusing zero-length read.
/// </remarks>
public sealed record BarnEntry
{
    /// <summary>The asset's name, as spelled in the archive.</summary>
    public required string Name { get; init; }

    /// <summary>Normalized identity, extension included.</summary>
    public AssetId Id => AssetId.FromExact(Name);

    /// <summary>Offset from the start of the archive's data section.</summary>
    public required uint Offset { get; init; }

    /// <summary>Stored size in bytes; compressed size when compressed.</summary>
    public required uint Size { get; init; }

    /// <summary>How the bytes are stored.</summary>
    public required BarnCompression Compression { get; init; }

    /// <summary>Name of the archive that actually holds this asset, when this is a pointer.</summary>
    public string? ReferencedArchive { get; init; }

    /// <summary>True when this entry names an asset stored in a different archive.</summary>
    public bool IsPointer => ReferencedArchive is not null;
}

/// <summary>
/// Reader for GK3's "Barn" asset archives.
/// </summary>
/// <remarks>
/// <para>
/// Named for the animals: some asset types are called Sheep and Yak. The metaphor did
/// not go much further.
/// </para>
/// <para>
/// The reader opens the file, parses the table of contents and directories, and then
/// extracts entries on demand. It never loads a whole archive into memory - the eight
/// retail archives total 822 MB, and the importer walks all of them.
/// </para>
/// </remarks>
public sealed class BarnArchive : IDisposable
{
    private static readonly byte[] GameMagic = "GK3!"u8.ToArray();
    private static readonly byte[] BarnMagic = "Barn"u8.ToArray();

    private const uint DirectoryTag = 0x44446972; // "DDir", read as a little-endian uint
    private const uint DataTag = 0x44617461;      // "Data"

    private readonly FileStream _stream;
    private readonly Dictionary<AssetId, BarnEntry> _entries = [];
    private readonly uint _dataOffset;

    private BarnArchive(FileStream stream, string name, uint dataOffset, IEnumerable<BarnEntry> entries)
    {
        _stream = stream;
        _dataOffset = dataOffset;
        Name = name;

        foreach (BarnEntry entry in entries)
        {
            // Later directories win, matching the reference implementation's map assignment.
            _entries[entry.Id] = entry;
        }
    }

    /// <summary>File name of the archive.</summary>
    public string Name { get; }

    /// <summary>Every entry, ordered by name for deterministic output.</summary>
    public IReadOnlyList<BarnEntry> Entries =>
        [.. _entries.Values.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)];

    /// <summary>Number of entries in the directory.</summary>
    public int Count => _entries.Count;

    /// <summary>Opens an archive and parses its directory.</summary>
    /// <param name="path">Path to the <c>.brn</c> file.</param>
    /// <returns>The opened archive.</returns>
    /// <exception cref="FormatParseException">The file is not a Barn archive, or is corrupt.</exception>
    public static BarnArchive Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string name = Path.GetFileName(path);
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        try
        {
            return Parse(stream, name);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>Looks up an entry by name, with or without its extension being exact.</summary>
    /// <param name="name">Asset name as referenced by game data.</param>
    /// <returns>The entry, or null.</returns>
    public BarnEntry? Find(string name) => _entries.GetValueOrDefault(AssetId.FromExact(name));

    /// <summary>Extracts and decompresses one entry.</summary>
    /// <param name="entry">Entry to extract.</param>
    /// <returns>The asset's bytes.</returns>
    /// <exception cref="FormatParseException">The entry is a pointer, or its data is corrupt.</exception>
    public byte[] Extract(BarnEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (entry.IsPointer)
        {
            throw new FormatParseException(new Diagnostic(
                "GK3R1020", DiagnosticSeverity.Error,
                $"Asset '{entry.Name}' lives in another archive.",
                Name, entry.Offset, "an entry holding data", $"a pointer to {entry.ReferencedArchive}",
                $"Extract it from {entry.ReferencedArchive} instead."));
        }

        _stream.Seek(_dataOffset + entry.Offset, SeekOrigin.Begin);

        if (entry.Compression == BarnCompression.None)
        {
            return ReadExactly(entry.Size, entry.Name);
        }

        // Compressed entries are prefixed with the decompressed length and four bytes
        // that are not used.
        Span<byte> prefix = stackalloc byte[8];
        _stream.ReadExactly(prefix);
        uint decompressedSize = BinaryPrimitives.ReadUInt32LittleEndian(prefix);

        // The last entry in an archive sometimes claims more bytes than the file holds.
        // The data still decompresses correctly, so read what is actually there.
        long available = _stream.Length - _stream.Position;
        int toRead = (int)Math.Min(entry.Size, available);
        byte[] compressed = ReadExactly(toRead, entry.Name);

        byte[] output = new byte[decompressedSize];

        switch (entry.Compression)
        {
            case BarnCompression.Zlib:
                Inflate(compressed, output, entry);
                return output;

            case BarnCompression.Lzo:
                int written = Lzo1x.Decompress(compressed, output, $"{Name}:{entry.Name}");
                return written == output.Length ? output : output[..written];

            default:
                throw new FormatParseException(new Diagnostic(
                    "GK3R1021", DiagnosticSeverity.Error,
                    $"Asset '{entry.Name}' uses an unknown compression type.",
                    Name, entry.Offset, "none, zlib or LZO",
                    ((int)entry.Compression).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "The archive may be from an unsupported edition. Report the archive and asset name."));
        }
    }

    /// <inheritdoc/>
    public void Dispose() => _stream.Dispose();

    private static BarnArchive Parse(FileStream stream, string name)
    {
        // The header is small and fixed; read it in one go rather than seeking about.
        byte[] header = new byte[0x18];
        stream.ReadExactly(header);

        var reader = new SpanReader(header, name);
        reader.ExpectMagic(GameMagic, "Barn archive header");
        reader.ExpectMagic(BarnMagic, "Barn archive header");

        // Two constants (both 65536) and a size field that is not needed here.
        reader.Skip(12);
        uint tocOffset = reader.ReadUInt32();

        (List<uint> DirectoryHeaders, List<uint> DirectoryData, uint DataOffset) toc =
            ReadTableOfContents(stream, name, tocOffset);

        List<BarnEntry> entries = [];
        for (int i = 0; i < toc.DirectoryHeaders.Count; i++)
        {
            entries.AddRange(ReadDirectory(stream, name, toc.DirectoryHeaders[i], toc.DirectoryData[i]));
        }

        return new BarnArchive(stream, name, toc.DataOffset, entries);
    }

    private static (List<uint>, List<uint>, uint) ReadTableOfContents(FileStream stream, string name, uint tocOffset)
    {
        Seek(stream, tocOffset, name, "table of contents");

        byte[] countBytes = new byte[4];
        stream.ReadExactly(countBytes);
        uint entryCount = BinaryPrimitives.ReadUInt32LittleEndian(countBytes);

        // Each entry is 4 (type) + 16 (unused) + 4 + 4 bytes.
        const int TocEntrySize = 28;
        if (entryCount > 1024)
        {
            throw Corrupt(name, tocOffset, "a plausible table-of-contents entry count", entryCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        byte[] tocBytes = new byte[entryCount * TocEntrySize];
        stream.ReadExactly(tocBytes);

        var reader = new SpanReader(tocBytes, name);
        List<uint> headerOffsets = [];
        List<uint> dataOffsets = [];
        uint dataSectionOffset = 0;

        for (uint i = 0; i < entryCount; i++)
        {
            uint type = reader.ReadUInt32();
            reader.Skip(16);
            uint headerOffset = reader.ReadUInt32();
            uint dataOffset = reader.ReadUInt32();

            if (type == DirectoryTag)
            {
                headerOffsets.Add(headerOffset);
                dataOffsets.Add(dataOffset);
            }
            else if (type == DataTag)
            {
                dataSectionOffset = headerOffset;
            }
        }

        return (headerOffsets, dataOffsets, dataSectionOffset);
    }

    private static List<BarnEntry> ReadDirectory(FileStream stream, string name, uint headerOffset, uint dataOffset)
    {
        Seek(stream, headerOffset, name, "directory header");

        // 32 bytes: name of the archive these entries really live in, empty when local.
        // 4 unused, 40 bytes of human-readable description, 4 unused, then the count.
        byte[] directoryHeader = new byte[32 + 48 + 4];
        stream.ReadExactly(directoryHeader);

        var headerReader = new SpanReader(directoryHeader, name);
        string referenced = headerReader.ReadFixedString(32);
        headerReader.Skip(48);
        uint assetCount = headerReader.ReadUInt32();

        string? referencedArchive = string.IsNullOrEmpty(referenced) ? null : referenced;

        Seek(stream, dataOffset, name, "directory entries");

        List<BarnEntry> entries = new((int)Math.Min(assetCount, 65536));
        using var entryReader = new BinaryReader(stream, System.Text.Encoding.Latin1, leaveOpen: true);

        for (uint i = 0; i < assetCount; i++)
        {
            uint size = entryReader.ReadUInt32();
            uint offset = entryReader.ReadUInt32();
            entryReader.BaseStream.Seek(5, SeekOrigin.Current);
            byte compression = entryReader.ReadByte();

            byte nameLength = entryReader.ReadByte();
            string assetName = new(entryReader.ReadChars(nameLength));
            entryReader.BaseStream.Seek(1, SeekOrigin.Current); // trailing NUL

            int nul = assetName.IndexOf('\0', StringComparison.Ordinal);
            if (nul >= 0)
            {
                assetName = assetName[..nul];
            }

            entries.Add(new BarnEntry
            {
                Name = assetName,
                Offset = offset,
                Size = size,

                // Type 3 behaves as uncompressed in the reference implementation.
                Compression = compression == 3 ? BarnCompression.None : (BarnCompression)compression,
                ReferencedArchive = referencedArchive,
            });
        }

        return entries;
    }

    private static void Inflate(byte[] compressed, byte[] output, BarnEntry entry)
    {
        using var source = new MemoryStream(compressed, writable: false);
        using var inflater = new ZLibStream(source, CompressionMode.Decompress);
        inflater.ReadExactly(output);
    }

    private byte[] ReadExactly(int count, string assetName)
    {
        byte[] buffer = new byte[count];
        int read = _stream.ReadAtLeast(buffer, count, throwOnEndOfStream: false);
        if (read != count)
        {
            throw Corrupt(Name, _stream.Position, $"{count} bytes for '{assetName}'", $"{read} available");
        }

        return buffer;
    }

    private byte[] ReadExactly(uint count, string assetName) => ReadExactly(checked((int)count), assetName);

    private static void Seek(FileStream stream, uint offset, string name, string what)
    {
        if (offset >= stream.Length)
        {
            throw Corrupt(name, offset, $"a {what} offset within the file", $"offset past the {stream.Length} byte file");
        }

        stream.Seek(offset, SeekOrigin.Begin);
    }

    private static FormatParseException Corrupt(string file, long offset, string expected, string actual) =>
        new(new Diagnostic(
            "GK3R1022",
            DiagnosticSeverity.Error,
            "Barn archive is corrupt or truncated.",
            file,
            offset,
            expected,
            actual,
            "Verify the installation's integrity and re-run the import."));
}
