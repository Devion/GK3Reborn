using System.Buffers.Binary;
using System.IO.Compression;
using GK3Reborn.Foundation.Diagnostics;

namespace GK3Reborn.Formats.Bitmaps;

/// <summary>
/// Reads PNG images.
/// </summary>
/// <remarks>
/// <para>
/// The originals are GK3's own bitmaps, which <see cref="BitmapDecoder"/> handles;
/// everything the project produces or takes in afterwards is PNG, because that is what
/// <c>Plan/02-content-pipeline.md</c> chose as the editable form for images. Enhanced
/// textures arrive that way, so loading one needs a reader and not only the writer that
/// already existed.
/// </para>
/// <para>
/// Deliberately narrow. Every PNG in the corpus and in the enhanced set is eight bits a
/// channel, RGB or RGBA, and not interlaced — 6,658 normalised textures and every
/// candidate produced so far — so that is what this reads, and anything else is refused by
/// name rather than half-decoded. A generator that starts emitting sixteen-bit or
/// palettised output should hear about it from a diagnostic, not from a texture that looks
/// subtly wrong.
/// </para>
/// </remarks>
public static class PngReader
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Whether the data looks like a PNG at all.</summary>
    /// <param name="data">The bytes.</param>
    /// <returns>True when it starts with the PNG signature.</returns>
    public static bool CanDecode(ReadOnlySpan<byte> data) =>
        data.Length >= Signature.Length && data[..Signature.Length].SequenceEqual(Signature);

    /// <summary>Decodes a PNG.</summary>
    /// <param name="data">The file's bytes.</param>
    /// <param name="name">Name used in diagnostics.</param>
    /// <returns>The image, as 8-bit RGBA with the top row first.</returns>
    /// <exception cref="FormatParseException">The data is not a PNG this can read.</exception>
    public static DecodedImage Decode(ReadOnlySpan<byte> data, string name = "<memory>")
    {
        if (!CanDecode(data))
        {
            throw Fail(name, 0, "the PNG signature", "something else");
        }

        int width = 0;
        int height = 0;
        int channels = 0;
        var compressed = new MemoryStream();
        int at = Signature.Length;
        bool seenHeader = false;

        while (at + 8 <= data.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(data[at..]);
            string kind = System.Text.Encoding.ASCII.GetString(data.Slice(at + 4, 4));
            int body = at + 8;

            if (length < 0 || body + length + 4 > data.Length)
            {
                throw Fail(name, at, "a chunk inside the file", $"{kind} running past the end");
            }

            switch (kind)
            {
                case "IHDR":
                    (width, height, channels) = ReadHeader(data.Slice(body, length), name, at);
                    seenHeader = true;
                    break;

                case "IDAT":
                    compressed.Write(data.Slice(body, length));
                    break;

                case "IEND":
                    at = data.Length;
                    continue;

                default:
                    break;
            }

            // Length, type, body, CRC. The CRC is not checked: a truncated or corrupt file
            // fails in the inflater or the row arithmetic either way, and with a name
            // attached, which is what a reader owes its caller.
            at = body + length + 4;
        }

        if (!seenHeader)
        {
            throw Fail(name, 0, "an IHDR chunk", "a file without one");
        }

        return new DecodedImage(
            width,
            height,
            Unfilter(Inflate(compressed, name), width, height, channels, name),
            channels == 4,
            $"png-rgb{(channels == 4 ? "a" : string.Empty)}8");
    }

    /// <summary>Reads the header, and refuses anything outside the narrow case.</summary>
    private static (int Width, int Height, int Channels) ReadHeader(
        ReadOnlySpan<byte> header, string name, int offset)
    {
        if (header.Length < 13)
        {
            throw Fail(name, offset, "a 13-byte IHDR", $"{header.Length} bytes");
        }

        int width = BinaryPrimitives.ReadInt32BigEndian(header);
        int height = BinaryPrimitives.ReadInt32BigEndian(header[4..]);
        int depth = header[8];
        int colour = header[9];
        int interlace = header[12];

        if (width <= 0 || height <= 0 || (long)width * height > 512L * 1024 * 1024)
        {
            throw Fail(name, offset, "a workable image size", $"{width}x{height}");
        }

        if (depth != 8)
        {
            throw Fail(name, offset, "eight bits a channel", $"{depth}-bit");
        }

        if (interlace != 0)
        {
            throw Fail(name, offset, "a non-interlaced image", "an interlaced one");
        }

        return colour switch
        {
            2 => (width, height, 3),
            6 => (width, height, 4),
            _ => throw Fail(name, offset, "RGB or RGBA colour", $"colour type {colour}"),
        };
    }

    /// <summary>Inflates the image data, which the file may have split across chunks.</summary>
    private static byte[] Inflate(MemoryStream compressed, string name)
    {
        compressed.Position = 0;

        try
        {
            using var inflater = new ZLibStream(compressed, CompressionMode.Decompress);
            using var raw = new MemoryStream();
            inflater.CopyTo(raw);
            return raw.ToArray();
        }
        catch (InvalidDataException ex)
        {
            throw new FormatParseException(new Diagnostic(
                "GK3R1090", DiagnosticSeverity.Error,
                $"The image data will not decompress: {ex.Message}",
                name, null, "a complete zlib stream", "a truncated or corrupt one",
                "The file is damaged; produce it again."));
        }
    }

    /// <summary>
    /// Undoes the per-row filters and widens to RGBA.
    /// </summary>
    /// <remarks>
    /// Every row carries a filter byte and is predicted from the pixel to its left and the
    /// row above, so rows have to be walked in order and cannot be skipped to. The five
    /// filters are the whole of PNG's compression cleverness; the rest is zlib.
    /// </remarks>
    private static byte[] Unfilter(
        byte[] raw, int width, int height, int channels, string name)
    {
        int stride = width * channels;
        long expected = (long)(stride + 1) * height;

        if (raw.Length < expected)
        {
            throw new FormatParseException(new Diagnostic(
                "GK3R1091", DiagnosticSeverity.Error,
                $"The image data is {raw.Length} bytes where {expected} were needed.",
                name, null, $"{height} rows of {stride} bytes", $"{raw.Length} bytes",
                "The file is truncated; produce it again."));
        }

        byte[] pixels = new byte[width * height * 4];
        byte[] previous = new byte[stride];
        byte[] current = new byte[stride];

        for (int y = 0; y < height; y++)
        {
            int row = y * (stride + 1);
            int filter = raw[row];
            Array.Copy(raw, row + 1, current, 0, stride);

            for (int i = 0; i < stride; i++)
            {
                int left = i >= channels ? current[i - channels] : 0;
                int up = previous[i];
                int upLeft = i >= channels ? previous[i - channels] : 0;

                current[i] = filter switch
                {
                    0 => current[i],
                    1 => (byte)(current[i] + left),
                    2 => (byte)(current[i] + up),
                    3 => (byte)(current[i] + ((left + up) / 2)),
                    4 => (byte)(current[i] + Paeth(left, up, upLeft)),
                    _ => throw Fail(name, row, "a filter from 0 to 4", $"filter {filter}"),
                };
            }

            for (int x = 0; x < width; x++)
            {
                int from = x * channels;
                int to = ((y * width) + x) * 4;

                pixels[to] = current[from];
                pixels[to + 1] = current[from + 1];
                pixels[to + 2] = current[from + 2];
                pixels[to + 3] = channels == 4 ? current[from + 3] : (byte)255;
            }

            (previous, current) = (current, previous);
        }

        return pixels;
    }

    /// <summary>The Paeth predictor: whichever neighbour the gradient points at.</summary>
    private static int Paeth(int left, int up, int upLeft)
    {
        int estimate = left + up - upLeft;
        int toLeft = Math.Abs(estimate - left);
        int toUp = Math.Abs(estimate - up);
        int toCorner = Math.Abs(estimate - upLeft);

        return toLeft <= toUp && toLeft <= toCorner ? left : toUp <= toCorner ? up : upLeft;
    }

    private static FormatParseException Fail(
        string name, int offset, string expected, string actual) =>
        new(new Diagnostic(
            "GK3R1092", DiagnosticSeverity.Error,
            $"{name} is not a PNG this reader can decode.",
            name, offset, expected, actual,
            "Eight bits a channel, RGB or RGBA, not interlaced."));
}
