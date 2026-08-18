using System.Buffers.Binary;
using System.IO.Compression;

namespace GK3Reborn.Formats.Bitmaps;

/// <summary>
/// Writes PNG files.
/// </summary>
/// <remarks>
/// A deliberately small encoder rather than an imaging library. The pipeline needs one
/// thing - lossless RGB or RGBA out - and PNG's container is simple enough that writing
/// it directly avoids taking a dependency with its own licence terms on a GPL project,
/// and avoids shipping an image library in the runtime that only the importer uses.
/// Deflate comes from the BCL.
/// </remarks>
public static class PngWriter
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <summary>Encodes an image as PNG.</summary>
    /// <param name="image">The image to encode.</param>
    /// <returns>The complete PNG file.</returns>
    public static byte[] Encode(DecodedImage image)
    {
        ArgumentNullException.ThrowIfNull(image.Pixels);

        // Colour type 6 is RGBA, 2 is RGB. Dropping the alpha channel where nothing uses
        // it saves a quarter of the pixel data across thousands of textures.
        bool alpha = image.HasAlpha;
        byte colourType = alpha ? (byte)6 : (byte)2;
        int channels = alpha ? 4 : 3;

        var output = new MemoryStream();
        output.Write(Signature);

        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, image.Width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], image.Height);
        header[8] = 8;             // bit depth
        header[9] = colourType;
        header[10] = 0;            // deflate
        header[11] = 0;            // adaptive filtering
        header[12] = 0;            // no interlacing
        WriteChunk(output, "IHDR"u8, header);

        WriteChunk(output, "IDAT"u8, Compress(image, channels));
        WriteChunk(output, "IEND"u8, default);

        return output.ToArray();
    }

    private static byte[] Compress(DecodedImage image, int channels)
    {
        // Each scanline is prefixed with its filter type. Filter 0 - none - keeps the
        // encoder trivial; deflate still does most of the work on this material.
        byte[] raw = new byte[image.Height * (1 + (image.Width * channels))];
        int target = 0;

        for (int y = 0; y < image.Height; y++)
        {
            raw[target++] = 0;

            for (int x = 0; x < image.Width; x++)
            {
                int source = ((y * image.Width) + x) * 4;
                raw[target++] = image.Pixels[source];
                raw[target++] = image.Pixels[source + 1];
                raw[target++] = image.Pixels[source + 2];

                if (channels == 4)
                {
                    raw[target++] = image.Pixels[source + 3];
                }
            }
        }

        var compressed = new MemoryStream();
        using (var deflate = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            deflate.Write(raw);
        }

        return compressed.ToArray();
    }

    private static void WriteChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        output.Write(length);

        output.Write(type);
        output.Write(data);

        uint crc = Crc32(type, data);
        Span<byte> checksum = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(checksum, crc);
        output.Write(checksum);
    }

    private static uint Crc32(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;

        foreach (byte b in type)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        foreach (byte b in data)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFF;
    }

    private static uint[] BuildCrcTable()
    {
        uint[] table = new uint[256];

        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}
