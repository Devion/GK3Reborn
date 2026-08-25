using System.Buffers.Binary;

namespace GK3Reborn.Formats.Bitmaps;

/// <summary>
/// Turns block-compressed levels back into eight-bit pixels.
/// </summary>
/// <remarks>
/// <para>
/// The content pipeline compresses to BC7, BC5 and BC4 because that is what a desktop
/// GPU reads without decoding anything. Apple's GPUs read none of them: Metal on Apple
/// silicon offers ASTC and ETC2 and no BC at all, so <c>textureCompressionBC</c> comes
/// back false through MoltenVK and creating a BC image there is illegal rather than
/// slow. This is what makes the same packs work on that hardware — the blocks are
/// expanded on the host and uploaded as ordinary pixels.
/// </para>
/// <para>
/// It reproduces the sampler exactly rather than approximately, which is what lets a
/// screenshot from a machine that decoded be compared with one from a machine that did
/// not. In particular BC4 returns <c>(r, 0, 0, 1)</c> and BC5 <c>(r, g, 0, 1)</c>,
/// because that is what the hardware returns for the channels those formats do not
/// carry, and a shader rebuilding a normal's z from two of them must see the same thing
/// either way.
/// </para>
/// <para>
/// Nothing here runs on a machine that supports BC. Where it does run it costs about a
/// second per hundred megabytes of blocks and four times the memory of the compressed
/// form, which is the price of the format not existing on that device.
/// </para>
/// </remarks>
public static partial class BlockDecoder
{
    /// <summary>Bytes one decoded pixel takes: RGBA, eight bits a channel.</summary>
    public const int BytesPerPixel = 4;

    // Weights for the two, three and four-bit index sets. A texel's colour is its
    // subset's two endpoints mixed by the weight its index names, in sixty-fourths.
    private static ReadOnlySpan<byte> Weights2 => [0, 21, 43, 64];

    private static ReadOnlySpan<byte> Weights3 => [0, 9, 18, 27, 37, 46, 55, 64];

    private static ReadOnlySpan<byte> Weights4 =>
        [0, 4, 9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64];

    // Per-mode field widths, indexed by mode. BC7's eight modes differ in nearly every
    // dimension, and the specification's own table is the clearest form this can take.
    private static ReadOnlySpan<byte> Subsets => [3, 2, 3, 2, 1, 1, 1, 2];

    private static ReadOnlySpan<byte> PartitionBits => [4, 6, 6, 6, 0, 0, 0, 6];

    private static ReadOnlySpan<byte> RotationBits => [0, 0, 0, 0, 2, 2, 0, 0];

    private static ReadOnlySpan<byte> SelectionBits => [0, 0, 0, 0, 1, 0, 0, 0];

    private static ReadOnlySpan<byte> ColourBits => [4, 6, 5, 7, 5, 7, 7, 5];

    private static ReadOnlySpan<byte> AlphaBits => [0, 0, 0, 0, 6, 8, 7, 5];

    /// <summary>Modes carrying one parity bit per endpoint.</summary>
    private static ReadOnlySpan<byte> EndpointParity => [1, 0, 0, 1, 0, 0, 1, 1];

    /// <summary>Mode 1 alone shares one parity bit between a subset's two endpoints.</summary>
    private static ReadOnlySpan<byte> SharedParity => [0, 1, 0, 0, 0, 0, 0, 0];

    private static ReadOnlySpan<byte> IndexBits => [3, 3, 2, 2, 2, 2, 4, 2];

    private static ReadOnlySpan<byte> IndexBits2 => [0, 0, 0, 0, 3, 2, 0, 0];

    /// <summary>Whether this decoder can expand a format.</summary>
    /// <param name="format">The block format.</param>
    /// <returns>True for every format the content pipeline produces.</returns>
    public static bool CanDecode(BlockFormat format) => format switch
    {
        BlockFormat.Bc7Srgb or BlockFormat.Bc7Unorm or BlockFormat.Bc5Unorm or BlockFormat.Bc4Unorm => true,
        _ => false,
    };

    /// <summary>How many bytes a decoded level of this size takes.</summary>
    /// <param name="width">Level width in pixels.</param>
    /// <param name="height">Level height in pixels.</param>
    /// <returns>The size of the RGBA buffer <see cref="DecodeLevel(CompressedImage, int, Span{byte})"/> wants.</returns>
    public static int DecodedLength(int width, int height) =>
        Math.Max(1, width) * Math.Max(1, height) * BytesPerPixel;

    /// <summary>Expands one level of a compressed image.</summary>
    /// <param name="image">The image the level belongs to.</param>
    /// <param name="level">Which level, zero being the largest.</param>
    /// <param name="pixels">Where to write, RGBA, top row first.</param>
    /// <remarks>
    /// Levels below four pixels still occupy a whole block, and the texels outside the
    /// level are decoded and dropped rather than skipped: the block cannot be read in
    /// part, and a 1×1 level's single pixel is the block's first texel.
    /// </remarks>
    public static void DecodeLevel(CompressedImage image, int level, Span<byte> pixels)
    {
        (int offset, int length, int width, int height) = image.Level(level);

        DecodeLevel(
            image.Format,
            image.Blocks.Span.Slice(offset, length),
            width,
            height,
            pixels);
    }

    /// <summary>Expands one level's blocks.</summary>
    /// <param name="format">Which block format the blocks are in.</param>
    /// <param name="blocks">The level's blocks, in raster order of 4×4 tiles.</param>
    /// <param name="width">The level's width in pixels.</param>
    /// <param name="height">The level's height in pixels.</param>
    /// <param name="pixels">Where to write, RGBA, top row first.</param>
    /// <exception cref="NotSupportedException">The format is not a decodable one.</exception>
    public static void DecodeLevel(
        BlockFormat format, ReadOnlySpan<byte> blocks, int width, int height, Span<byte> pixels)
    {
        if (!CanDecode(format))
        {
            throw new NotSupportedException($"No decoder for {format}.");
        }

        width = Math.Max(1, width);
        height = Math.Max(1, height);

        ArgumentOutOfRangeException.ThrowIfLessThan(pixels.Length, DecodedLength(width, height));

        int blockBytes = CompressedImage.BytesPerBlock(format);
        int across = (width + 3) / 4;
        int down = (height + 3) / 4;

        Span<byte> texels = stackalloc byte[16 * BytesPerPixel];

        for (int by = 0; by < down; by++)
        {
            for (int bx = 0; bx < across; bx++)
            {
                int at = ((by * across) + bx) * blockBytes;
                if (at + blockBytes > blocks.Length)
                {
                    // A truncated file is a reported condition where it is read; here the
                    // rest of the level is left as it was rather than read past the end.
                    return;
                }

                ReadOnlySpan<byte> block = blocks.Slice(at, blockBytes);

                switch (format)
                {
                    case BlockFormat.Bc7Srgb:
                    case BlockFormat.Bc7Unorm:
                        DecodeBc7(block, texels);
                        break;
                    case BlockFormat.Bc5Unorm:
                        DecodeBc5(block, texels);
                        break;
                    default:
                        DecodeBc4(block, texels);
                        break;
                }

                for (int ty = 0; ty < 4; ty++)
                {
                    int y = (by * 4) + ty;
                    if (y >= height)
                    {
                        break;
                    }

                    for (int tx = 0; tx < 4; tx++)
                    {
                        int x = (bx * 4) + tx;
                        if (x >= width)
                        {
                            break;
                        }

                        texels.Slice(((ty * 4) + tx) * BytesPerPixel, BytesPerPixel)
                            .CopyTo(pixels[((((y * width) + x) * BytesPerPixel))..]);
                    }
                }
            }
        }
    }

    /// <summary>Expands a compressed image's largest level.</summary>
    /// <param name="image">The image.</param>
    /// <returns>The decoded pixels, named after the format they came out of.</returns>
    /// <remarks>
    /// For tools and tests. The renderer decodes level by level into memory it has
    /// already staged, rather than allocating a chain of arrays it would immediately
    /// throw away.
    /// </remarks>
    public static DecodedImage Decode(CompressedImage image)
    {
        (_, _, int width, int height) = image.Level(0);

        byte[] pixels = new byte[DecodedLength(width, height)];
        DecodeLevel(image, 0, pixels);

        return new DecodedImage(width, height, pixels, HasAlpha: true, image.Format.ToString());
    }

    /// <summary>Expands one BC4 block: one channel, eight bytes.</summary>
    /// <param name="block">The eight bytes.</param>
    /// <param name="texels">Sixteen RGBA texels.</param>
    private static void DecodeBc4(ReadOnlySpan<byte> block, Span<byte> texels)
    {
        Span<byte> red = stackalloc byte[16];
        DecodeChannel(block, red);

        for (int i = 0; i < 16; i++)
        {
            int at = i * BytesPerPixel;
            texels[at] = red[i];
            texels[at + 1] = 0;
            texels[at + 2] = 0;
            texels[at + 3] = 255;
        }
    }

    /// <summary>Expands one BC5 block: two channels, sixteen bytes.</summary>
    /// <param name="block">The sixteen bytes.</param>
    /// <param name="texels">Sixteen RGBA texels.</param>
    private static void DecodeBc5(ReadOnlySpan<byte> block, Span<byte> texels)
    {
        Span<byte> red = stackalloc byte[16];
        Span<byte> green = stackalloc byte[16];

        DecodeChannel(block[..8], red);
        DecodeChannel(block.Slice(8, 8), green);

        for (int i = 0; i < 16; i++)
        {
            int at = i * BytesPerPixel;
            texels[at] = red[i];
            texels[at + 1] = green[i];
            texels[at + 2] = 0;
            texels[at + 3] = 255;
        }
    }

    /// <summary>Expands one eight-byte channel block, as BC4 and both halves of BC5 are.</summary>
    /// <param name="block">The eight bytes.</param>
    /// <param name="values">Sixteen values, in raster order within the tile.</param>
    /// <remarks>
    /// <para>
    /// Two endpoints and a three-bit index apiece. Which ramp the indices name depends on
    /// the endpoints' order: the six-value ramp reserves two codes for exactly zero and
    /// exactly one, which is how a compressor spends a block on a mask.
    /// </para>
    /// <para>
    /// The ramp is mixed in sixteen-bit fixed point and rounded, not divided by seven and
    /// truncated. The specification writes the values as fractions and hardware rounds
    /// them; truncating instead is one less over about a fifth of the ramp, which is
    /// invisible in a texture and not invisible in a normal map — it was worth 0.6% of a
    /// lit frame differing from the same frame drawn from the blocks themselves.
    /// </para>
    /// </remarks>
    private static void DecodeChannel(ReadOnlySpan<byte> block, Span<byte> values)
    {
        int first = block[0];
        int second = block[1];

        Span<int> ramp = stackalloc int[8];
        ramp[0] = first;
        ramp[1] = second;

        if (first > second)
        {
            for (int i = 1; i < 7; i++)
            {
                ramp[i + 1] = (((7 - i) * first) + (i * second) + 3) / 7;
            }
        }
        else
        {
            for (int i = 1; i < 5; i++)
            {
                ramp[i + 1] = (((5 - i) * first) + (i * second) + 2) / 5;
            }

            ramp[6] = 0;
            ramp[7] = 255;
        }

        ulong indices = 0;
        for (int i = 0; i < 6; i++)
        {
            indices |= (ulong)block[2 + i] << (8 * i);
        }

        for (int i = 0; i < 16; i++)
        {
            values[i] = (byte)ramp[(int)((indices >> (3 * i)) & 7)];
        }
    }

    /// <summary>Mixes two channel endpoints in sixteen-bit fixed point.</summary>
    /// <param name="first">The first endpoint.</param>
    /// <param name="second">The second.</param>
    /// <param name="weight">How much of the first, in 65536ths.</param>
    /// <returns>The ramp value.</returns>
    private static int Between(int first, int second, int weight) =>
        ((weight * first) + ((65536 - weight) * second) + 32768) >> 16;

    /// <summary>Sevenths, in 65536ths, for the ramp that interpolates six values.</summary>
    private static ReadOnlySpan<int> SixthsOfSeven => [9363, 18724, 28086, 37450, 46812, 56173];

    /// <summary>Fifths, in 65536ths, for the ramp that interpolates four.</summary>
    private static ReadOnlySpan<int> FifthsOfFive => [13107, 26215, 39321, 52429];

    /// <summary>Expands one BC7 block.</summary>
    /// <param name="block">The sixteen bytes.</param>
    /// <param name="texels">Sixteen RGBA texels.</param>
    /// <remarks>
    /// <para>
    /// The mode is the position of the lowest set bit, and everything else about the
    /// block follows from it — how many subsets the sixteen texels are divided into, how
    /// wide an endpoint is, whether alpha is stored at all, and whether the texel indices
    /// come in one set or two.
    /// </para>
    /// <para>
    /// A block of all zeroes names no mode. The specification leaves the result undefined
    /// and hardware decoders return transparent black, so this does too rather than
    /// throwing: it is reached by reading a hole in a file, and a hole should look like
    /// one instead of stopping a scene from loading.
    /// </para>
    /// </remarks>
    private static void DecodeBc7(ReadOnlySpan<byte> block, Span<byte> texels)
    {
        int mode = -1;
        for (int i = 0; i < 8; i++)
        {
            if ((block[0] & (1 << i)) != 0)
            {
                mode = i;
                break;
            }
        }

        if (mode < 0)
        {
            texels[..(16 * BytesPerPixel)].Clear();
            return;
        }

        var bits = new BlockBits(block, mode + 1);

        int subsets = Subsets[mode];
        int partition = PartitionBits[mode] > 0 ? (int)bits.Read(PartitionBits[mode]) : 0;
        int rotation = RotationBits[mode] > 0 ? (int)bits.Read(RotationBits[mode]) : 0;
        int selection = SelectionBits[mode] > 0 ? (int)bits.Read(SelectionBits[mode]) : 0;

        int colourBits = ColourBits[mode];
        int alphaBits = AlphaBits[mode];
        int endpoints = subsets * 2;

        // Channel by channel, then endpoint by endpoint within it: every red before any
        // green. Reading them endpoint-first is the mistake this ordering exists to make
        // visible, because it decodes plausible-looking rubbish rather than failing.
        Span<int> red = stackalloc int[6];
        Span<int> green = stackalloc int[6];
        Span<int> blue = stackalloc int[6];
        Span<int> alpha = stackalloc int[6];

        for (int i = 0; i < endpoints; i++)
        {
            red[i] = (int)bits.Read(colourBits);
        }

        for (int i = 0; i < endpoints; i++)
        {
            green[i] = (int)bits.Read(colourBits);
        }

        for (int i = 0; i < endpoints; i++)
        {
            blue[i] = (int)bits.Read(colourBits);
        }

        if (alphaBits > 0)
        {
            for (int i = 0; i < endpoints; i++)
            {
                alpha[i] = (int)bits.Read(alphaBits);
            }
        }

        // The parity bit is the endpoint's low bit, which is how a mode with four-bit
        // endpoints still reaches an odd value. Mode 1 shares one between a subset's pair.
        Span<int> parity = stackalloc int[6];
        bool hasParity = EndpointParity[mode] != 0 || SharedParity[mode] != 0;

        if (EndpointParity[mode] != 0)
        {
            for (int i = 0; i < endpoints; i++)
            {
                parity[i] = (int)bits.Read(1);
            }
        }
        else if (SharedParity[mode] != 0)
        {
            for (int i = 0; i < subsets; i++)
            {
                int shared = (int)bits.Read(1);
                parity[i * 2] = shared;
                parity[(i * 2) + 1] = shared;
            }
        }

        int colourWidth = colourBits + (hasParity ? 1 : 0);
        int alphaWidth = alphaBits + (hasParity ? 1 : 0);

        for (int i = 0; i < endpoints; i++)
        {
            red[i] = Widen(red[i], parity[i], colourWidth, hasParity);
            green[i] = Widen(green[i], parity[i], colourWidth, hasParity);
            blue[i] = Widen(blue[i], parity[i], colourWidth, hasParity);
            alpha[i] = alphaBits > 0 ? Widen(alpha[i], parity[i], alphaWidth, hasParity) : 255;
        }

        ReadOnlySpan<byte> table = subsets == 1
            ? default
            : subsets == 2
                ? TwoSubsetPartitions.Slice(partition * 16, 16)
                : ThreeSubsetPartitions.Slice(partition * 16, 16);

        int indexBits = IndexBits[mode];
        int indexBits2 = IndexBits2[mode];

        Span<int> primary = stackalloc int[16];
        Span<int> secondary = stackalloc int[16];

        ReadIndices(ref bits, table, subsets, indexBits, primary);

        if (indexBits2 > 0)
        {
            ReadIndices(ref bits, default, 1, indexBits2, secondary);
        }

        // Two index sets mean one is the colour's and the other alpha's, and which is
        // which is the mode's selection bit rather than their order in the block.
        bool swapped = indexBits2 > 0 && selection != 0;

        ReadOnlySpan<byte> colourWeights = WeightsFor(swapped ? indexBits2 : indexBits);
        ReadOnlySpan<byte> alphaWeights = indexBits2 == 0
            ? colourWeights
            : WeightsFor(swapped ? indexBits : indexBits2);

        for (int i = 0; i < 16; i++)
        {
            int subset = subsets == 1 ? 0 : table[i] & 3;
            int first = subset * 2;
            int second = first + 1;

            int colourIndex = swapped ? secondary[i] : primary[i];
            int alphaIndex = indexBits2 == 0
                ? colourIndex
                : swapped ? primary[i] : secondary[i];

            int cw = colourWeights[colourIndex];
            int aw = alphaWeights[alphaIndex];

            byte r = Mix(red[first], red[second], cw);
            byte g = Mix(green[first], green[second], cw);
            byte b = Mix(blue[first], blue[second], cw);
            byte a = alphaBits > 0 ? Mix(alpha[first], alpha[second], aw) : (byte)255;

            // A rotation moves one colour channel into alpha and alpha into its place,
            // which is how a mode with one alpha index set can spend its precision on
            // whichever channel varies most.
            switch (rotation)
            {
                case 1:
                    (r, a) = (a, r);
                    break;
                case 2:
                    (g, a) = (a, g);
                    break;
                case 3:
                    (b, a) = (a, b);
                    break;
                default:
                    break;
            }

            int at = i * BytesPerPixel;
            texels[at] = r;
            texels[at + 1] = g;
            texels[at + 2] = b;
            texels[at + 3] = a;
        }
    }

    /// <summary>Reads one set of texel indices.</summary>
    /// <param name="bits">The block's bit stream, positioned at the set.</param>
    /// <param name="table">The partition assignment, or empty for a single subset.</param>
    /// <param name="subsets">How many subsets the texels are divided into.</param>
    /// <param name="width">How many bits an index takes.</param>
    /// <param name="indices">Where to write the sixteen indices.</param>
    /// <remarks>
    /// One texel per subset — its anchor — is stored a bit short, its top bit implied
    /// zero. That is what fixes the direction of the subset's ramp, and it is why an
    /// index set cannot be read as sixteen equal fields.
    /// </remarks>
    private static void ReadIndices(
        ref BlockBits bits, scoped ReadOnlySpan<byte> table, int subsets, int width, scoped Span<int> indices)
    {
        for (int i = 0; i < 16; i++)
        {
            bool anchor = subsets == 1 ? i == 0 : (table[i] & 0x80) != 0;
            indices[i] = (int)bits.Read(anchor ? width - 1 : width);
        }
    }

    /// <summary>Mixes two endpoints by a weight in sixty-fourths.</summary>
    /// <param name="first">The subset's first endpoint.</param>
    /// <param name="second">Its second.</param>
    /// <param name="weight">The index's weight.</param>
    /// <returns>The texel's value in that channel.</returns>
    private static byte Mix(int first, int second, int weight) =>
        (byte)((((64 - weight) * first) + (weight * second) + 32) >> 6);

    /// <summary>Widens a stored endpoint to eight bits.</summary>
    /// <param name="value">The stored value.</param>
    /// <param name="parity">Its parity bit, where the mode has one.</param>
    /// <param name="width">How many bits it has once the parity bit is appended.</param>
    /// <param name="hasParity">Whether the mode carries parity bits at all.</param>
    /// <returns>The endpoint in eight bits.</returns>
    /// <remarks>
    /// The low bits are filled from the high ones rather than with zeroes, so that the
    /// largest storable value is white rather than nearly white.
    /// </remarks>
    private static int Widen(int value, int parity, int width, bool hasParity)
    {
        int v = hasParity ? (value << 1) | parity : value;

        return width >= 8 ? v & 0xFF : (v << (8 - width)) | (v >> ((2 * width) - 8));
    }

    /// <summary>The weight set an index width names.</summary>
    /// <param name="width">Two, three or four bits.</param>
    /// <returns>The weights.</returns>
    private static ReadOnlySpan<byte> WeightsFor(int width) => width switch
    {
        2 => Weights2,
        3 => Weights3,
        _ => Weights4,
    };

    /// <summary>Reads fields of a block from the low bit up.</summary>
    /// <remarks>
    /// A BC7 block is one 128-bit little-endian number and its fields are packed from the
    /// bottom, so the two halves are held as integers rather than indexed as bytes.
    /// </remarks>
    private ref struct BlockBits(ReadOnlySpan<byte> block, int start)
    {
        private readonly ulong _low = BinaryPrimitives.ReadUInt64LittleEndian(block);
        private readonly ulong _high = BinaryPrimitives.ReadUInt64LittleEndian(block[8..]);
        private int _at = start;

        /// <summary>Reads the next field.</summary>
        /// <param name="count">Its width in bits, never more than eight.</param>
        /// <returns>The field.</returns>
        public uint Read(int count)
        {
            ulong value;

            if (_at >= 64)
            {
                value = _high >> (_at - 64);
            }
            else
            {
                value = _low >> _at;

                if (_at + count > 64)
                {
                    value |= _high << (64 - _at);
                }
            }

            _at += count;

            return (uint)(value & ((1UL << count) - 1));
        }
    }
}
