using System.Buffers.Binary;
using GK3Reborn.Foundation.Diagnostics;

namespace GK3Reborn.Formats.Bitmaps;

/// <summary>Which block format a compressed texture is in.</summary>
/// <remarks>
/// Named rather than carried as a Vulkan enum so that the format layer stays free of the
/// renderer, the way <see cref="DecodedImage"/> does.
/// </remarks>
public enum BlockFormat
{
    /// <summary>Four-channel colour, sRGB. What the base colours are compressed to.</summary>
    Bc7Srgb,

    /// <summary>Four-channel data, linear.</summary>
    Bc7Unorm,

    /// <summary>Two channels, linear. What the normal maps are compressed to.</summary>
    Bc5Unorm,
}

/// <summary>
/// A texture that is already in the form the device wants.
/// </summary>
/// <param name="Width">Width of the largest level, in pixels.</param>
/// <param name="Height">Height of the largest level, in pixels.</param>
/// <param name="Mips">How many levels the chain holds.</param>
/// <param name="Format">Which block format the levels are in.</param>
/// <param name="Blocks">
/// Every level, largest first, laid end to end. A window onto the file rather than a copy
/// of it: a 2048-pixel texture is 5.6 MB, a room wants dozens, and they are decoded on
/// every core at once, so copying the blocks out doubles the high-water mark to save
/// nothing at all.
/// </param>
/// <param name="Name">Name used in diagnostics.</param>
public readonly record struct CompressedImage(
    int Width,
    int Height,
    int Mips,
    BlockFormat Format,
    ReadOnlyMemory<byte> Blocks,
    string Name)
{
    /// <summary>How many bytes one 4×4 block takes. Sixteen, for every format here.</summary>
    public const int BlockBytes = 16;

    /// <summary>Where a level starts and how long it is.</summary>
    /// <param name="level">Level index, zero being the largest.</param>
    /// <returns>The offset into <see cref="Blocks"/>, the level's size, and its extent.</returns>
    public (int Offset, int Length, int Width, int Height) Level(int level)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(level);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(level, Mips);

        int offset = 0;
        int width = Width;
        int height = Height;

        for (int i = 0; ; i++)
        {
            int length = Blocks4(width) * Blocks4(height) * BlockBytes;

            if (i == level)
            {
                return (offset, length, width, height);
            }

            offset += length;
            width = Math.Max(1, width / 2);
            height = Math.Max(1, height / 2);
        }
    }

    /// <summary>How many 4×4 blocks span an extent, rounding up.</summary>
    internal static int Blocks4(int extent) => Math.Max(1, (extent + 3) / 4);
}

/// <summary>
/// Reads DDS textures.
/// </summary>
/// <remarks>
/// <para>
/// A block-compressed texture is the one image format that costs nothing to load: there is
/// no decode, the mip chain is already built, and it takes a quarter of the video memory an
/// <c>R8G8B8A8</c> copy would. `PbrLab` measures the pilot set at 13.71 GiB uncompressed
/// against 3.43 GiB compressed, and 45.5–47.0 dB on colour, which is not visible.
/// </para>
/// <para>
/// As narrow as the PNG reader, and for the same reason. Two-dimensional, no arrays, no
/// cube maps, and only the three block formats the content pipeline emits. Anything else is
/// refused by name so that a pipeline which starts producing something new hears about it.
/// </para>
/// </remarks>
public static class DdsFile
{
    private const uint Magic = 0x20534444; // "DDS ", little-endian
    private const int HeaderEnd = 128;
    private const int ExtendedHeaderEnd = 148;

    private const uint FourCcDx10 = 0x30315844; // "DX10"
    private const uint FourCcBc5U = 0x55354342; // "BC5U"
    private const uint FourCcAti2 = 0x32495441; // "ATI2", the older spelling of BC5

    private const uint DxgiBc7Unorm = 98;
    private const uint DxgiBc7UnormSrgb = 99;
    private const uint DxgiBc5Unorm = 83;

    /// <summary>Whether the data looks like a DDS file at all.</summary>
    /// <param name="data">The bytes.</param>
    /// <returns>True when it starts with the DDS magic.</returns>
    public static bool CanDecode(ReadOnlySpan<byte> data) =>
        data.Length >= HeaderEnd && BinaryPrimitives.ReadUInt32LittleEndian(data) == Magic;

    /// <summary>Reads a DDS texture.</summary>
    /// <param name="file">The file's bytes, which the result points into.</param>
    /// <param name="name">Name used in diagnostics.</param>
    /// <returns>The texture, with every level as the file holds it.</returns>
    /// <exception cref="FormatParseException">The file is not a DDS this can read.</exception>
    public static CompressedImage Read(ReadOnlyMemory<byte> file, string name = "<memory>")
    {
        ReadOnlySpan<byte> data = file.Span;

        if (!CanDecode(data))
        {
            throw Fail(name, 0, "the DDS magic", "something else");
        }

        int height = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[12..]);
        int width = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[16..]);
        int mips = Math.Max(1, (int)BinaryPrimitives.ReadUInt32LittleEndian(data[28..]));
        uint fourCc = BinaryPrimitives.ReadUInt32LittleEndian(data[84..]);

        if (width <= 0 || height <= 0)
        {
            throw Fail(name, 12, "a workable image size", $"{width}x{height}");
        }

        (BlockFormat format, int start) = fourCc switch
        {
            FourCcDx10 => (Extended(data, name), ExtendedHeaderEnd),
            FourCcBc5U or FourCcAti2 => (BlockFormat.Bc5Unorm, HeaderEnd),
            _ => throw Fail(
                name, 84, "a block-compressed format", $"four-character code {FourCc(fourCc)}"),
        };

        // Every level of the chain, largest first, which is the order the file holds them
        // in and the order the copy commands want them.
        long expected = 0;
        int levelWidth = width;
        int levelHeight = height;

        for (int level = 0; level < mips; level++)
        {
            expected += (long)CompressedImage.Blocks4(levelWidth)
                * CompressedImage.Blocks4(levelHeight) * CompressedImage.BlockBytes;

            levelWidth = Math.Max(1, levelWidth / 2);
            levelHeight = Math.Max(1, levelHeight / 2);
        }

        if (data.Length - start < expected)
        {
            throw Fail(
                name, start,
                $"{expected} bytes of blocks for {mips} level(s)",
                $"{data.Length - start} bytes");
        }

        return new CompressedImage(width, height, mips, format, file.Slice(start, (int)expected), name);
    }

    /// <summary>Reads the DX10 header, which is where a modern format is named.</summary>
    private static BlockFormat Extended(ReadOnlySpan<byte> data, string name)
    {
        if (data.Length < ExtendedHeaderEnd)
        {
            throw Fail(name, HeaderEnd, "a DX10 header", $"{data.Length - HeaderEnd} bytes");
        }

        uint dxgi = BinaryPrimitives.ReadUInt32LittleEndian(data[HeaderEnd..]);
        uint arraySize = BinaryPrimitives.ReadUInt32LittleEndian(data[140..]);

        if (arraySize > 1)
        {
            throw Fail(name, 140, "a single image", $"an array of {arraySize}");
        }

        return dxgi switch
        {
            DxgiBc7UnormSrgb => BlockFormat.Bc7Srgb,
            DxgiBc7Unorm => BlockFormat.Bc7Unorm,
            DxgiBc5Unorm => BlockFormat.Bc5Unorm,
            _ => throw Fail(name, HeaderEnd, "BC5 or BC7", $"DXGI format {dxgi}"),
        };
    }

    private static string FourCc(uint value) =>
        new([(char)(byte)value, (char)(byte)(value >> 8),
             (char)(byte)(value >> 16), (char)(byte)(value >> 24)]);

    private static FormatParseException Fail(
        string name, int offset, string expected, string actual) =>
        new(new Diagnostic(
            "GK3R1094", DiagnosticSeverity.Error,
            $"{name} is not a DDS this reader can decode.",
            name, offset, expected, actual,
            "Two-dimensional BC5 or BC7, with or without a mip chain."));
}
