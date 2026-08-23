using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using System.Runtime.Intrinsics;
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
/// <para>
/// It is also on the critical path for showing a room. The enhanced textures are 2048²,
/// which is 16 MB of pixels each, and a room asks for dozens; at 80 ms and 75 MB of garbage
/// apiece that was ten seconds and several gigabytes for one scene. So every size here is
/// known before anything is allocated, rows are reconstructed in place, and the filter is
/// chosen once a row rather than once a byte.
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

        // Walked twice: once to learn the size of everything, once to gather the image data
        // into a buffer of exactly that size. The second walk only hops chunk headers, and
        // knowing the total up front is worth more than the hop — a growing stream copies
        // everything it already holds each time it doubles, which on a 2048² texture is most
        // of the garbage the decode used to produce.
        (int width, int height, int channels, int compressedLength) = Survey(data, name);

        byte[] compressed = new byte[compressedLength];
        int filled = 0;

        foreach ((int Body, int Length, string Kind) chunk in Chunks(data, name))
        {
            if (chunk.Kind == "IDAT")
            {
                data.Slice(chunk.Body, chunk.Length).CopyTo(compressed.AsSpan(filled));
                filled += chunk.Length;
            }
        }

        int stride = width * channels;
        byte[] raw = Inflate(compressed, ((long)stride + 1) * height, name);

        return new DecodedImage(
            width,
            height,
            Reconstruct(raw, width, height, channels, name),
            channels == 4,
            $"png-rgb{(channels == 4 ? "a" : string.Empty)}8");
    }

    /// <summary>Walks the chunks for the header's answers and the image data's size.</summary>
    private static (int Width, int Height, int Channels, int Compressed) Survey(
        ReadOnlySpan<byte> data, string name)
    {
        int width = 0;
        int height = 0;
        int channels = 0;
        long compressed = 0;
        bool seenHeader = false;

        foreach ((int Body, int Length, string Kind) chunk in Chunks(data, name))
        {
            switch (chunk.Kind)
            {
                case "IHDR":
                    (width, height, channels) =
                        ReadHeader(data.Slice(chunk.Body, chunk.Length), name, chunk.Body - 8);
                    seenHeader = true;
                    break;

                case "IDAT":
                    compressed += chunk.Length;
                    break;

                default:
                    break;
            }
        }

        if (!seenHeader)
        {
            throw Fail(name, 0, "an IHDR chunk", "a file without one");
        }

        if (compressed == 0)
        {
            throw Fail(name, 0, "some image data", "a file with no IDAT chunk");
        }

        return (width, height, channels, (int)compressed);
    }

    /// <summary>Every chunk in the file, in order, up to and including IEND.</summary>
    /// <remarks>
    /// Materialised rather than yielded, because a span cannot cross an iterator. It is a few
    /// dozen entries and the bodies stay where they are. The CRC is not checked: a truncated
    /// or corrupt file fails in the inflater or the row arithmetic either way, and with a
    /// name attached, which is what a reader owes its caller.
    /// </remarks>
    private static List<(int Body, int Length, string Kind)> Chunks(
        ReadOnlySpan<byte> data, string name)
    {
        var chunks = new List<(int Body, int Length, string Kind)>();
        int at = Signature.Length;

        while (at + 8 <= data.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(data[at..]);
            string kind = System.Text.Encoding.ASCII.GetString(data.Slice(at + 4, 4));
            int body = at + 8;

            if (length < 0 || body + length + 4 > data.Length)
            {
                throw Fail(name, at, "a chunk inside the file", $"{kind} running past the end");
            }

            chunks.Add((body, length, kind));

            if (kind == "IEND")
            {
                break;
            }

            at = body + length + 4;
        }

        return chunks;
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

    /// <summary>Inflates the image data into a buffer of exactly the size it must be.</summary>
    /// <remarks>
    /// The header says how many rows there are and how wide each is, and PNG adds one filter
    /// byte a row, so the inflated size is known before a byte is read. Reading exactly that
    /// many also does the truncation check for free.
    /// </remarks>
    private static byte[] Inflate(byte[] compressed, long expected, string name)
    {
        byte[] raw = new byte[expected];

        try
        {
            using var source = new MemoryStream(compressed, writable: false);
            using var inflater = new ZLibStream(source, CompressionMode.Decompress);

            inflater.ReadExactly(raw);
            return raw;
        }
        catch (EndOfStreamException)
        {
            throw new FormatParseException(new Diagnostic(
                "GK3R1091", DiagnosticSeverity.Error,
                $"The image data ran out before {expected} bytes had been read.",
                name, null, $"{expected} bytes of rows", "fewer",
                "The file is truncated; produce it again."));
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
    /// <para>
    /// Every row carries a filter byte and is predicted from the pixel to its left and the
    /// row above, so rows have to be walked in order and cannot be skipped to. The five
    /// filters are the whole of PNG's compression cleverness; the rest is zlib.
    /// </para>
    /// <para>
    /// Reconstructed <b>in place</b>, and the filter chosen once a row. Choosing it per byte
    /// — which is what falls out of writing this the obvious way — puts an unpredictable
    /// branch in front of every one of a 2048² texture's sixteen million bytes, and stops
    /// the Up filter, which is what an encoder picks for most rows of a photographic image,
    /// from vectorising at all.
    /// </para>
    /// </remarks>
    private static byte[] Reconstruct(byte[] raw, int width, int height, int channels, string name)
    {
        int stride = width * channels;
        byte[] pixels = new byte[width * height * 4];

        // The row above row zero, which PNG defines as zeroes.
        byte[] first = new byte[stride];

        for (int y = 0; y < height; y++)
        {
            int row = (y * (stride + 1)) + 1;
            byte filter = raw[row - 1];

            Span<byte> current = raw.AsSpan(row, stride);
            ReadOnlySpan<byte> above = y > 0 ? raw.AsSpan(row - stride - 1, stride) : first;

            switch (filter)
            {
                case 0:
                    break;

                case 1:
                    Sub(current, channels);
                    break;

                case 2:
                    Up(current, above);
                    break;

                case 3:
                    Average(current, above, channels);
                    break;

                case 4:
                    Paeth(current, above, channels);
                    break;

                default:
                    throw Fail(name, row - 1, "a filter from 0 to 4", $"filter {filter}");
            }

            Widen(current, pixels.AsSpan(y * width * 4, width * 4), width, channels);
        }

        return pixels;
    }

    /// <summary>Each byte is predicted from the one a pixel to its left.</summary>
    private static void Sub(Span<byte> current, int bytesPerPixel)
    {
        for (int i = bytesPerPixel; i < current.Length; i++)
        {
            current[i] += current[i - bytesPerPixel];
        }
    }

    /// <summary>Each byte is predicted from the one above it.</summary>
    /// <remarks>
    /// Nothing in the row depends on anything else in it, so the whole row adds a vector at
    /// a time.
    /// </remarks>
    private static void Up(Span<byte> current, ReadOnlySpan<byte> above)
    {
        int i = 0;

        if (Vector.IsHardwareAccelerated && current.Length >= Vector<byte>.Count)
        {
            int step = Vector<byte>.Count;

            for (; i + step <= current.Length; i += step)
            {
                (new Vector<byte>(current.Slice(i, step)) + new Vector<byte>(above.Slice(i, step)))
                    .CopyTo(current.Slice(i, step));
            }
        }

        for (; i < current.Length; i++)
        {
            current[i] += above[i];
        }
    }

    /// <summary>Each byte is predicted from the mean of the one left and the one above.</summary>
    private static void Average(Span<byte> current, ReadOnlySpan<byte> above, int bytesPerPixel)
    {
        int lead = Math.Min(bytesPerPixel, current.Length);

        // With no pixel to the left, the mean is of the byte above and nothing.
        for (int i = 0; i < lead; i++)
        {
            current[i] += (byte)(above[i] >> 1);
        }

        for (int i = bytesPerPixel; i < current.Length; i++)
        {
            current[i] += (byte)((current[i - bytesPerPixel] + above[i]) >> 1);
        }
    }

    /// <summary>Each byte is predicted from whichever neighbour the gradient points at.</summary>
    /// <remarks>
    /// <b>95% of the rows in the enhanced set are this one</b>, so it is where the decode
    /// spends its time and the only filter worth taking trouble over. Each byte depends on
    /// the byte a pixel to its left, so a row cannot be split — but the four channels
    /// <i>within</i> a pixel do not depend on each other, which is a vector four lanes wide.
    /// </remarks>
    private static void Paeth(Span<byte> current, ReadOnlySpan<byte> above, int bytesPerPixel)
    {
        int lead = Math.Min(bytesPerPixel, current.Length);

        // With no pixel to the left, the predictor reduces to the byte above.
        for (int i = 0; i < lead; i++)
        {
            current[i] += above[i];
        }

        if (Vector128.IsHardwareAccelerated && BitConverter.IsLittleEndian && current.Length >= 8)
        {
            PaethVector(current, above, bytesPerPixel);
            return;
        }

        for (int i = bytesPerPixel; i < current.Length; i++)
        {
            current[i] += (byte)Predict(
                current[i - bytesPerPixel], above[i], above[i - bytesPerPixel]);
        }
    }

    /// <summary>The Paeth filter, a pixel at a time.</summary>
    /// <remarks>
    /// <para>
    /// The pixel to the left is whatever the last turn of the loop just wrote, so it stays
    /// in a register rather than being read back; the same goes for the pixel above-left,
    /// which is the pixel above from the turn before.
    /// </para>
    /// <para>
    /// Four lanes are loaded whatever the pixel is worth, because reading three bytes costs
    /// the same as reading four. On RGB the fourth lane holds the next pixel's red, computes
    /// a result nobody stores, and is overwritten on the next turn — so the loop stops while
    /// four bytes are still readable and the last pixel or two go the scalar way.
    /// </para>
    /// </remarks>
    private static void PaethVector(Span<byte> current, ReadOnlySpan<byte> above, int bytesPerPixel)
    {
        Vector128<short> low = Vector128.Create((short)0xFF);
        Vector128<short> left = Pixel(current);
        Vector128<short> upLeft = Pixel(above);
        int at = bytesPerPixel;

        for (; at + 4 <= current.Length; at += bytesPerPixel)
        {
            Vector128<short> up = Pixel(above[at..]);

            // p = left + up - upLeft, and each distance from it drops a term.
            Vector128<short> toLeft = Vector128.Abs(up - upLeft);
            Vector128<short> toUp = Vector128.Abs(left - upLeft);
            Vector128<short> toCorner = Vector128.Abs(left + up - upLeft - upLeft);

            Vector128<short> nearestIsLeft =
                Vector128.LessThanOrEqual(toLeft, toUp) & Vector128.LessThanOrEqual(toLeft, toCorner);

            Vector128<short> predicted = Vector128.ConditionalSelect(
                nearestIsLeft,
                left,
                Vector128.ConditionalSelect(Vector128.LessThanOrEqual(toUp, toCorner), up, upLeft));

            // Masked rather than saturated, because a byte plus a byte wraps in PNG.
            left = (Pixel(current[at..]) + predicted) & low;
            upLeft = up;

            Store(left, current[at..], bytesPerPixel);
        }

        for (int i = at; i < current.Length; i++)
        {
            current[i] += (byte)Predict(
                current[i - bytesPerPixel], above[i], above[i - bytesPerPixel]);
        }
    }

    /// <summary>Reads one RGBA pixel into the low four lanes.</summary>
    private static Vector128<short> Pixel(ReadOnlySpan<byte> from) =>
        Vector128.WidenLower(
            Vector128.CreateScalar(BinaryPrimitives.ReadUInt32LittleEndian(from)).AsByte())
            .AsInt16();

    /// <summary>Writes the low lanes back as one pixel.</summary>
    private static void Store(Vector128<short> value, Span<byte> to, int bytesPerPixel)
    {
        uint packed = Vector128.Narrow(value.AsUInt16(), value.AsUInt16()).AsUInt32().GetElement(0);

        to[0] = (byte)packed;
        to[1] = (byte)(packed >> 8);
        to[2] = (byte)(packed >> 16);

        if (bytesPerPixel == 4)
        {
            to[3] = (byte)(packed >> 24);
        }
    }

    /// <summary>The Paeth predictor: whichever neighbour the gradient points at.</summary>
    private static int Predict(int left, int up, int upLeft)
    {
        int estimate = left + up - upLeft;
        int toLeft = Math.Abs(estimate - left);
        int toUp = Math.Abs(estimate - up);
        int toCorner = Math.Abs(estimate - upLeft);

        return toLeft <= toUp && toLeft <= toCorner ? left : toUp <= toCorner ? up : upLeft;
    }

    /// <summary>Copies a reconstructed row out as RGBA.</summary>
    /// <remarks>
    /// An RGBA source is already in the layout the device wants, so the row is one copy. RGB
    /// has to be spread out, which is the only per-pixel work left in the decode.
    /// </remarks>
    private static void Widen(
        ReadOnlySpan<byte> row, Span<byte> destination, int width, int channels)
    {
        if (channels == 4)
        {
            row.CopyTo(destination);
            return;
        }

        int at = 0;
        int wrote = 0;

        // Four pixels a turn: twelve bytes in, sixteen out, with the alpha lanes shuffled
        // to zero and then filled. Every texture in the enhanced set is RGB, so this is the
        // loop that widening actually runs.
        if (Vector128.IsHardwareAccelerated && row.Length >= 16)
        {
            Vector128<byte> spread = Vector128.Create(
                (byte)0, 1, 2, 0x80, 3, 4, 5, 0x80, 6, 7, 8, 0x80, 9, 10, 11, 0x80);
            Vector128<uint> opaque = Vector128.Create(0xFF000000u);

            for (; at + 16 <= row.Length && wrote + 16 <= destination.Length; at += 12, wrote += 16)
            {
                (Vector128.Shuffle(Vector128.Create(row.Slice(at, 16)), spread).AsUInt32() | opaque)
                    .AsByte()
                    .CopyTo(destination.Slice(wrote, 16));
            }
        }

        for (; at + 3 <= row.Length; at += 3, wrote += 4)
        {
            destination[wrote] = row[at];
            destination[wrote + 1] = row[at + 1];
            destination[wrote + 2] = row[at + 2];
            destination[wrote + 3] = 255;
        }
    }

    private static FormatParseException Fail(
        string name, int offset, string expected, string actual) =>
        new(new Diagnostic(
            "GK3R1092", DiagnosticSeverity.Error,
            $"{name} is not a PNG this reader can decode.",
            name, offset, expected, actual,
            "Eight bits a channel, RGB or RGBA, not interlaced."));
}
