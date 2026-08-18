using System.Buffers.Binary;
using GK3Reborn.Foundation.Diagnostics;

namespace GK3Reborn.Formats.Bitmaps;

/// <summary>A decoded image: 8-bit RGBA, top row first.</summary>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
/// <param name="Pixels">RGBA bytes, four per pixel, row-major from the top.</param>
/// <param name="HasAlpha">Whether any pixel is not fully opaque.</param>
/// <param name="SourceFormat">Which on-disk format this came from.</param>
public readonly record struct DecodedImage(
    int Width,
    int Height,
    byte[] Pixels,
    bool HasAlpha,
    string SourceFormat);

/// <summary>
/// Decodes GK3's texture formats to RGBA.
/// </summary>
/// <remarks>
/// <para>
/// GK3 stores textures three ways. Most are its own container - 6,330 of them - which
/// despite G-Engine calling it "compressed" is a raw 16-bit RGB565 bitmap with a tiny
/// header. Nothing outside the game can open those, which is the main reason to convert.
/// The rest are ordinary Windows bitmaps (322 palettised, 6 truecolour) and a handful
/// of PNGs that are already fine as they are.
/// </para>
/// <para>
/// Layout of the GK3 container, from G-Engine's <c>Texture::LoadCompressedFormat</c>:
/// two bytes <c>0x3136</c>, two bytes <c>0x4D6E</c>, then <b>height</b> and
/// <b>width</b> as 16-bit values - in that order - followed by width x height RGB565
/// pixels from the top-left. Rows of odd width are padded with two bytes.
/// </para>
/// <para>
/// Magenta is the transparency key. G-Engine treats a texture as alpha-tested when its
/// top-left pixel is magenta, and that convention is preserved here: such images decode
/// with magenta made transparent, so a PNG viewer shows what the game shows. Images
/// without the marker keep every pixel opaque, magenta included, because in those the
/// colour is just a colour.
/// </para>
/// </remarks>
public static class BitmapDecoder
{
    /// <summary>Identifies whether a buffer is a bitmap this decoder handles.</summary>
    /// <param name="data">The asset's bytes.</param>
    /// <returns>True when <see cref="Decode"/> will succeed.</returns>
    public static bool CanDecode(ReadOnlySpan<byte> data) =>
        IsGk3(data) || IsWindows(data);

    /// <summary>Decodes a bitmap to RGBA.</summary>
    /// <param name="data">The asset's bytes.</param>
    /// <param name="name">Name used in diagnostics.</param>
    /// <returns>The decoded image.</returns>
    /// <exception cref="FormatParseException">The data is not a supported bitmap.</exception>
    public static DecodedImage Decode(ReadOnlySpan<byte> data, string name = "<memory>")
    {
        if (IsGk3(data))
        {
            return DecodeGk3(data, name);
        }

        if (IsWindows(data))
        {
            return DecodeWindows(data, name);
        }

        throw new FormatParseException(new Diagnostic(
            "GK3R1030", DiagnosticSeverity.Error,
            "Not a supported bitmap.",
            name, 0, "a GK3 or Windows bitmap signature",
            data.Length >= 2 ? $"0x{data[0]:X2}{data[1]:X2}" : "too short",
            "Only GK3's own container and Windows bitmaps are decoded; PNG assets pass through unchanged."));
    }

    private static bool IsGk3(ReadOnlySpan<byte> data) =>
        data.Length >= 8 && data[0] == 0x36 && data[1] == 0x31 && data[2] == 0x6E && data[3] == 0x4D;

    private static bool IsWindows(ReadOnlySpan<byte> data) =>
        data.Length >= 30 && data[0] == (byte)'B' && data[1] == (byte)'M';

    private static DecodedImage DecodeGk3(ReadOnlySpan<byte> data, string name)
    {
        // Height precedes width; getting this backwards yields a transposed image that
        // still decodes for square textures, which is exactly how it stays unnoticed.
        int height = BinaryPrimitives.ReadUInt16LittleEndian(data[4..]);
        int width = BinaryPrimitives.ReadUInt16LittleEndian(data[6..]);

        int stride = width + (width % 2);
        long required = 8L + ((long)stride * height * 2);
        if (width <= 0 || height <= 0 || required > data.Length)
        {
            throw Truncated(name, width, height, required, data.Length);
        }

        byte[] pixels = new byte[width * height * 4];
        int source = 8;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                ushort packed = BinaryPrimitives.ReadUInt16LittleEndian(data[source..]);
                source += 2;

                // RGB565 expanded so that all-ones maps to 255 rather than 248.
                int r = (packed & 0xF800) >> 11;
                int g = (packed & 0x07E0) >> 5;
                int b = packed & 0x001F;

                int target = ((y * width) + x) * 4;
                pixels[target] = (byte)(r * 255 / 31);
                pixels[target + 1] = (byte)(g * 255 / 63);
                pixels[target + 2] = (byte)(b * 255 / 31);
                pixels[target + 3] = 255;
            }

            if ((width % 2) != 0)
            {
                source += 2;
            }
        }

        bool keyed = ApplyMagentaKey(pixels);
        return new DecodedImage(width, height, pixels, keyed, "gk3-rgb565");
    }

    private static DecodedImage DecodeWindows(ReadOnlySpan<byte> data, string name)
    {
        uint dataOffset = BinaryPrimitives.ReadUInt32LittleEndian(data[10..]);
        uint headerSize = BinaryPrimitives.ReadUInt32LittleEndian(data[14..]);
        if (headerSize != 40)
        {
            throw Unsupported(name, "a 40-byte DIB header", headerSize.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        int width = BinaryPrimitives.ReadInt32LittleEndian(data[18..]);
        int height = BinaryPrimitives.ReadInt32LittleEndian(data[22..]);
        int bitsPerPixel = BinaryPrimitives.ReadUInt16LittleEndian(data[28..]);
        uint compression = BinaryPrimitives.ReadUInt32LittleEndian(data[30..]);

        if (compression != 0)
        {
            throw Unsupported(name, "an uncompressed bitmap", $"compression method {compression}");
        }

        if (bitsPerPixel is not (8 or 24 or 32))
        {
            throw Unsupported(name, "8, 24 or 32 bits per pixel", $"{bitsPerPixel} bpp");
        }

        // A negative height means the rows are already stored top-down.
        bool bottomUp = height > 0;
        height = Math.Abs(height);

        if (width <= 0 || height <= 0)
        {
            throw Unsupported(name, "positive dimensions", $"{width}x{height}");
        }

        ReadOnlySpan<byte> palette = bitsPerPixel == 8 ? data[54..(int)dataOffset] : default;
        int stride = ((width * bitsPerPixel / 8) + 3) & ~3;
        long required = dataOffset + ((long)stride * height);
        if (required > data.Length)
        {
            throw Truncated(name, width, height, required, data.Length);
        }

        byte[] pixels = new byte[width * height * 4];

        for (int y = 0; y < height; y++)
        {
            int sourceRow = (int)dataOffset + (stride * (bottomUp ? height - 1 - y : y));

            for (int x = 0; x < width; x++)
            {
                int target = ((y * width) + x) * 4;

                switch (bitsPerPixel)
                {
                    case 8:
                        {
                            // Palette entries are stored blue, green, red, reserved.
                            int index = data[sourceRow + x] * 4;
                            pixels[target] = palette[index + 2];
                            pixels[target + 1] = palette[index + 1];
                            pixels[target + 2] = palette[index];
                            pixels[target + 3] = 255;
                            break;
                        }

                    case 24:
                        {
                            int at = sourceRow + (x * 3);
                            pixels[target] = data[at + 2];
                            pixels[target + 1] = data[at + 1];
                            pixels[target + 2] = data[at];
                            pixels[target + 3] = 255;
                            break;
                        }

                    default:
                        {
                            int at = sourceRow + (x * 4);
                            pixels[target] = data[at + 2];
                            pixels[target + 1] = data[at + 1];
                            pixels[target + 2] = data[at];
                            pixels[target + 3] = data[at + 3];
                            break;
                        }
                }
            }
        }

        bool keyed = ApplyMagentaKey(pixels);
        bool hasAlpha = keyed || (bitsPerPixel == 32 && AnyTransparent(pixels));
        return new DecodedImage(width, height, pixels, hasAlpha, $"bmp-{bitsPerPixel}bpp");
    }

    /// <summary>
    /// Makes magenta transparent when the image is marked as alpha-tested.
    /// </summary>
    /// <remarks>
    /// The marker is the top-left pixel being magenta, which is how G-Engine decides a
    /// texture is alpha-tested. Applying the key only then avoids punching holes in
    /// artwork that legitimately contains magenta.
    /// </remarks>
    private static bool ApplyMagentaKey(byte[] pixels)
    {
        if (pixels.Length < 4 || !IsMagenta(pixels, 0))
        {
            return false;
        }

        for (int i = 0; i < pixels.Length; i += 4)
        {
            if (IsMagenta(pixels, i))
            {
                pixels[i + 3] = 0;
            }
        }

        return true;
    }

    private static bool IsMagenta(byte[] pixels, int at) =>
        pixels[at] == 255 && pixels[at + 1] == 0 && pixels[at + 2] == 255;

    private static bool AnyTransparent(byte[] pixels)
    {
        for (int i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 255)
            {
                return true;
            }
        }

        return false;
    }

    private static FormatParseException Truncated(string name, int width, int height, long required, int actual) =>
        new(new Diagnostic(
            "GK3R1031", DiagnosticSeverity.Error,
            "Bitmap is truncated.",
            name, actual, $"{required} bytes for a {width}x{height} image",
            $"{actual} bytes",
            "Re-extract the asset; the archive entry may be damaged."));

    private static FormatParseException Unsupported(string name, string expected, string actual) =>
        new(new Diagnostic(
            "GK3R1032", DiagnosticSeverity.Error,
            "Unsupported bitmap variant.",
            name, 0, expected, actual,
            "Report the asset name; this variant does not appear in the reference installation."));
}
