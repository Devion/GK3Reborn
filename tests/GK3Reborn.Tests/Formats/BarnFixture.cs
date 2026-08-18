using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace GK3Reborn.Tests.Formats;

/// <summary>
/// Builds synthetic Barn archives for tests.
/// </summary>
/// <remarks>
/// The runtime is read-only for proprietary formats, so this writer exists purely so
/// tests can exercise the reader without shipping a byte of copyrighted game data. It
/// implements exactly as much of the format as the reader consumes.
/// </remarks>
internal sealed class BarnFixture
{
    private readonly List<(string Name, byte[] Data, int Compression)> _entries = [];
    private string _referencedArchive = string.Empty;
    private byte[] _gameMagic = "GK3!"u8.ToArray();

    /// <summary>Adds an entry stored verbatim.</summary>
    public BarnFixture AddStored(string name, string content)
    {
        _entries.Add((name, Encoding.ASCII.GetBytes(content), 0));
        return this;
    }

    /// <summary>Adds an entry stored as a zlib stream.</summary>
    public BarnFixture AddDeflated(string name, string content)
    {
        _entries.Add((name, Encoding.ASCII.GetBytes(content), 1));
        return this;
    }

    /// <summary>
    /// Adds an entry whose recorded compression type is 3, which the format treats as
    /// stored rather than as a distinct algorithm.
    /// </summary>
    public BarnFixture AddTypeThree(string name, string content)
    {
        _entries.Add((name, Encoding.ASCII.GetBytes(content), 3));
        return this;
    }

    /// <summary>Marks this archive's directory as pointing at another archive.</summary>
    public BarnFixture PointingAt(string archiveName)
    {
        _referencedArchive = archiveName;
        return this;
    }

    /// <summary>Corrupts the leading signature.</summary>
    public BarnFixture WithBadMagic()
    {
        _gameMagic = "NOPE"u8.ToArray();
        return this;
    }

    /// <summary>Serializes the archive.</summary>
    public byte[] Build()
    {
        // Lay the data section out first so entry offsets are known.
        var dataSection = new MemoryStream();
        List<(uint Offset, uint Size)> placements = [];

        byte[] prefix = new byte[8];
        foreach ((string _, byte[] data, int compression) in _entries)
        {
            uint offset = (uint)dataSection.Position;
            byte[] stored = compression == 1 ? Deflate(data) : data;

            if (compression is 1 or 2)
            {
                // Compressed entries are prefixed with the decompressed length and four
                // bytes the reader skips.
                BinaryPrimitives.WriteUInt32LittleEndian(prefix, (uint)data.Length);
                dataSection.Write(prefix);
            }

            dataSection.Write(stored);
            placements.Add((offset, (uint)stored.Length));
        }

        return Assemble(dataSection.ToArray(), placements);
    }

    private byte[] Assemble(byte[] dataSection, List<(uint Offset, uint Size)> placements)
    {
        var output = new MemoryStream();
        var writer = new BinaryWriter(output, Encoding.ASCII, leaveOpen: true);

        // Header: magic, three unused words, then the offset of the table of contents.
        writer.Write(_gameMagic);
        writer.Write("Barn"u8);
        writer.Write(65536u);
        writer.Write(65536u);
        writer.Write(0u);
        long tocOffsetPosition = output.Position;
        writer.Write(0u);

        long tocOffset = output.Position;
        writer.Write(2u); // one directory entry, one data entry

        long directoryTocPosition = output.Position;
        WriteTocEntry(writer, 0x44446972, 0, 0); // "DDir", offsets patched below
        long dataTocPosition = output.Position;
        WriteTocEntry(writer, 0x44617461, 0, 0); // "Data"

        // Directory header.
        long directoryHeaderOffset = output.Position;
        byte[] referenced = new byte[32];
        Encoding.ASCII.GetBytes(_referencedArchive).CopyTo(referenced, 0);
        writer.Write(referenced);
        writer.Write(new byte[48]);
        writer.Write((uint)_entries.Count);

        // Directory entries.
        long directoryDataOffset = output.Position;
        for (int i = 0; i < _entries.Count; i++)
        {
            (string name, byte[] _, int compression) = _entries[i];
            writer.Write(placements[i].Size);
            writer.Write(placements[i].Offset);
            writer.Write(new byte[5]);
            writer.Write((byte)compression);
            writer.Write((byte)name.Length);
            writer.Write(Encoding.ASCII.GetBytes(name));
            writer.Write((byte)0);
        }

        long dataSectionOffset = output.Position;
        writer.Write(dataSection);
        writer.Flush();

        // Patch the offsets now that everything is placed.
        Patch(output, tocOffsetPosition, (uint)tocOffset);
        Patch(output, directoryTocPosition + 20, (uint)directoryHeaderOffset);
        Patch(output, directoryTocPosition + 24, (uint)directoryDataOffset);
        Patch(output, dataTocPosition + 20, (uint)dataSectionOffset);

        return output.ToArray();
    }

    private static void WriteTocEntry(BinaryWriter writer, uint type, uint headerOffset, uint dataOffset)
    {
        writer.Write(type);
        writer.Write(new byte[16]);
        writer.Write(headerOffset);
        writer.Write(dataOffset);
    }

    private static void Patch(MemoryStream stream, long position, uint value)
    {
        long saved = stream.Position;
        stream.Position = position;
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        stream.Write(bytes);
        stream.Position = saved;
    }

    private static byte[] Deflate(byte[] data)
    {
        var output = new MemoryStream();
        using (var deflater = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflater.Write(data);
        }

        return output.ToArray();
    }
}
