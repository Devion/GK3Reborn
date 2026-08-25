using GK3Reborn.Formats.Bitmaps;
using Xunit;

namespace GK3Reborn.Tests.Formats;

/// <summary>
/// Tests for expanding the content pipeline's blocks on the host.
/// </summary>
/// <remarks>
/// <para>
/// This runs on any device with no BC formats, which today means Apple silicon. What it
/// has to get right is the bit layout: BC7 packs eight different field arrangements into
/// the same sixteen bytes and picks between them with the position of the block's lowest
/// set bit, so reading a field one bit early decodes a picture rather than failing, and
/// the picture looks nearly right.
/// </para>
/// <para>
/// The evidence that the layout is right is not here — it is 240 of the pipeline's own
/// textures decoded and compared against the pictures they were encoded from, at 40.6 to
/// 61.2 dB, which is the compressor's own error and no more (docs/rendering.md, "Devices
/// with no block compression"). What is here is what that sweep cannot reach: the exact
/// values a channel format produces, the shapes of the partition tables, and the two
/// modes texconv never emits.
/// </para>
/// </remarks>
public sealed class BlockDecoderTests
{
    /// <summary>Writes fields into a block from the low bit up, as BC7 packs them.</summary>
    private sealed class BlockWriter
    {
        private readonly byte[] _bytes = new byte[16];
        private int _at;

        /// <summary>Appends a field.</summary>
        /// <param name="value">Its value.</param>
        /// <param name="count">Its width in bits.</param>
        public void Write(uint value, int count)
        {
            for (int i = 0; i < count; i++)
            {
                if (((value >> i) & 1) != 0)
                {
                    _bytes[(_at + i) / 8] |= (byte)(1 << ((_at + i) % 8));
                }
            }

            _at += count;
        }

        /// <summary>The block.</summary>
        /// <returns>Its sixteen bytes.</returns>
        public byte[] Block() => _bytes;
    }

    /// <summary>Decodes one block on its own.</summary>
    private static byte[] Decode(BlockFormat format, byte[] block)
    {
        byte[] pixels = new byte[16 * 4];
        BlockDecoder.DecodeLevel(format, block, 4, 4, pixels);
        return pixels;
    }

    /// <summary>Reads one texel of a decoded block.</summary>
    private static (byte R, byte G, byte B, byte A) Texel(byte[] pixels, int index) =>
        (pixels[index * 4], pixels[(index * 4) + 1], pixels[(index * 4) + 2], pixels[(index * 4) + 3]);

    [Fact]
    public void Bc4KeepsTheEndpointsAndSpacesTheRampBetweenThem()
    {
        // First endpoint above the second: eight values, none of them reserved.
        byte[] block = new byte[8];
        block[0] = 200;
        block[1] = 40;

        // Indices 0, 1, 2 and 3 in the first four texels. Three bits each from the low
        // bit up, so the third index straddles the two bytes.
        block[2] = 0b10001000;
        block[3] = 0b00000110;

        byte[] pixels = Decode(BlockFormat.Bc4Unorm, block);

        Assert.Equal(200, pixels[0]);
        Assert.Equal(40, pixels[4]);
        Assert.Equal((byte)(((6 * 200) + 40) / 7), pixels[8]);
        Assert.Equal((byte)(((5 * 200) + (2 * 40)) / 7), pixels[12]);
    }

    [Fact]
    public void Bc4ReservesTwoCodesForBlackAndWhiteWhenTheEndpointsAreOrdered()
    {
        // Second endpoint above the first: six interpolated values, and the last two
        // codes are exactly zero and exactly one. That is how a compressor spends a
        // block on a mask, and reading it as the eight-value ramp loses the extremes.
        byte[] block = new byte[8];
        block[0] = 40;
        block[1] = 200;
        block[2] = 0b00_111_110;

        byte[] pixels = Decode(BlockFormat.Bc4Unorm, block);

        Assert.Equal(0, pixels[0]);
        Assert.Equal(255, pixels[4]);
    }

    [Fact]
    public void Bc4AndBc5ReturnWhatTheHardwareReturnsForTheChannelsTheyDoNotCarry()
    {
        byte[] one = new byte[8];
        one[0] = 90;
        one[1] = 10;

        Assert.Equal((90, 0, 0, 255), Texel(Decode(BlockFormat.Bc4Unorm, one), 0));

        byte[] two = new byte[16];
        two[0] = 90;
        two[1] = 10;
        two[8] = 30;
        two[9] = 5;

        Assert.Equal((90, 30, 0, 255), Texel(Decode(BlockFormat.Bc5Unorm, two), 0));
    }

    [Fact]
    public void Bc5DecodesItsTwoHalvesIndependently()
    {
        byte[] block = new byte[16];
        block[0] = 250;
        block[1] = 50;
        block[2] = 0b00_001_000;
        block[8] = 60;
        block[9] = 20;
        block[10] = 0b00_001_000;

        byte[] pixels = Decode(BlockFormat.Bc5Unorm, block);

        Assert.Equal((250, 60, 0, 255), Texel(pixels, 0));
        Assert.Equal((50, 20, 0, 255), Texel(pixels, 1));
    }

    [Fact]
    public void Bc7Mode6SpendsEveryBitOnOneSubsetOfFourBitIndices()
    {
        var writer = new BlockWriter();
        writer.Write(64, 7);          // mode 6: six clear bits, then the bit that names it

        // Seven-bit endpoints, channel by channel, then alpha, then a parity bit each.
        writer.Write(0, 7);
        writer.Write(127, 7);         // red: black to white
        writer.Write(0, 7);
        writer.Write(0, 7);           // green: black to black
        writer.Write(0, 7);
        writer.Write(0, 7);           // blue
        writer.Write(127, 7);
        writer.Write(127, 7);         // alpha: opaque at both ends
        writer.Write(1, 1);
        writer.Write(1, 1);           // parity, which is each endpoint's low bit

        writer.Write(0, 3);           // texel 0 anchors the subset: one bit short
        writer.Write(15, 4);          // texel 1 takes the second endpoint
        writer.Write(8, 4);           // texel 2 lands between them

        byte[] pixels = Decode(BlockFormat.Bc7Srgb, writer.Block());

        // Green and blue are not zero: the parity bit is appended to every channel, so a
        // stored zero with a set parity bit widens to one.
        Assert.Equal((1, 1, 1, 255), Texel(pixels, 0));
        Assert.Equal((255, 1, 1, 255), Texel(pixels, 1));

        // Weight 34 of 64 between 1 and 255.
        Assert.Equal((byte)((((64 - 34) * 1) + (34 * 255) + 32) >> 6), Texel(pixels, 2).R);
    }

    [Fact]
    public void Bc7Mode5KeepsAlphaOnItsOwnIndexSet()
    {
        var writer = new BlockWriter();
        writer.Write(32, 6);          // mode 5
        writer.Write(0, 2);           // no rotation

        writer.Write(0, 7);
        writer.Write(127, 7);         // red
        writer.Write(0, 7);
        writer.Write(0, 7);           // green
        writer.Write(0, 7);
        writer.Write(0, 7);           // blue
        writer.Write(255, 8);
        writer.Write(0, 8);           // alpha: opaque to transparent, eight bits and no parity

        writer.Write(0, 1);           // colour index for texel 0, one bit short
        writer.Write(3, 2);           // texel 1 takes the far colour endpoint
        for (int i = 2; i < 16; i++)
        {
            writer.Write(0, 2);
        }

        writer.Write(0, 1);           // alpha index for texel 0 is anchored too
        writer.Write(3, 2);           // texel 1 takes the far alpha endpoint
        for (int i = 2; i < 16; i++)
        {
            writer.Write(0, 2);
        }

        byte[] pixels = Decode(BlockFormat.Bc7Srgb, writer.Block());

        Assert.Equal((0, 0, 0, 255), Texel(pixels, 0));
        Assert.Equal((255, 0, 0, 0), Texel(pixels, 1));
    }

    [Fact]
    public void Bc7RotationMovesAChannelIntoAlphaAndAlphaIntoItsPlace()
    {
        var writer = new BlockWriter();
        writer.Write(32, 6);          // mode 5
        writer.Write(1, 2);           // rotation 1: red and alpha change places

        writer.Write(127, 7);
        writer.Write(127, 7);         // red is white at both ends
        writer.Write(0, 7);
        writer.Write(0, 7);           // green
        writer.Write(0, 7);
        writer.Write(0, 7);           // blue
        writer.Write(30, 8);
        writer.Write(30, 8);          // alpha is 30 at both ends

        for (int i = 0; i < 16; i++)
        {
            writer.Write(0, i == 0 ? 1 : 2);
        }

        for (int i = 0; i < 16; i++)
        {
            writer.Write(0, i == 0 ? 1 : 2);
        }

        byte[] pixels = Decode(BlockFormat.Bc7Srgb, writer.Block());

        // What was stored as red comes back as alpha, and what was stored as alpha as red.
        Assert.Equal((30, 0, 0, 255), Texel(pixels, 0));
    }

    [Fact]
    public void Bc7ThreeSubsetModesReadTheirOwnPartitionTable()
    {
        // Mode 2: three subsets, six partition bits, five-bit endpoints, no parity, no
        // alpha, two-bit indices. texconv does not emit it at the quality the pipeline
        // asks for, so nothing in the corpus exercises the three-subset table.
        var writer = new BlockWriter();
        writer.Write(4, 3);           // mode 2
        writer.Write(0, 6);           // partition 0

        // Red separates the three subsets: 0, 15 and 31 in each subset's pair.
        writer.Write(0, 5);
        writer.Write(0, 5);
        writer.Write(15, 5);
        writer.Write(15, 5);
        writer.Write(31, 5);
        writer.Write(31, 5);

        for (int i = 0; i < 12; i++)
        {
            writer.Write(0, 5);       // green and blue, black throughout
        }

        for (int i = 0; i < 16; i++)
        {
            // Three anchors are a bit short, and which texels they are is the table's
            // business. Partition 0 anchors texels 0, 3 and 15.
            writer.Write(0, i is 0 or 3 or 15 ? 1 : 2);
        }

        byte[] pixels = Decode(BlockFormat.Bc7Unorm, writer.Block());

        // Partition 0 of the three-subset table, read as its three regions.
        int[] expected = [0, 0, 1, 1, 0, 0, 1, 1, 0, 2, 2, 1, 2, 2, 2, 2];
        byte[] reds = [0, 123, 255];

        for (int i = 0; i < 16; i++)
        {
            Assert.Equal(reds[expected[i]], Texel(pixels, i).R);
        }
    }

    [Fact]
    public void EveryPartitionShapeUsesEverySubsetAndAnchorsEachOnce()
    {
        // Khronos's published copy of these tables is known to contain errors, so the
        // shape of every row is checked rather than trusted: a partition that never uses
        // its third subset, or anchors one twice, decodes plausible rubbish.
        Check(BlockDecoder.TwoSubsetPartitions, 2);
        Check(BlockDecoder.ThreeSubsetPartitions, 3);

        static void Check(ReadOnlySpan<byte> table, int subsets)
        {
            Assert.Equal(BlockDecoder.PartitionShapes * BlockDecoder.BlockTexels, table.Length);

            for (int shape = 0; shape < BlockDecoder.PartitionShapes; shape++)
            {
                ReadOnlySpan<byte> row = table.Slice(shape * BlockDecoder.BlockTexels, BlockDecoder.BlockTexels);

                var used = new HashSet<int>();
                int anchors = 0;

                foreach (byte texel in row)
                {
                    used.Add(texel & 3);
                    if ((texel & BlockDecoder.AnchorFlag) != 0)
                    {
                        anchors++;
                    }
                }

                Assert.Equal(subsets, used.Count);
                Assert.Equal(subsets, anchors);
                Assert.Equal(subsets - 1, used.Max());

                // Texel zero anchors the first subset in every shape, which is what makes
                // a single-subset mode need no table at all.
                Assert.NotEqual(0, row[0] & BlockDecoder.AnchorFlag);
                Assert.Equal(0, row[0] & 3);
            }
        }
    }

    [Fact]
    public void ABlockOfZeroesNamesNoModeAndDecodesToNothing()
    {
        byte[] pixels = Decode(BlockFormat.Bc7Srgb, new byte[16]);

        Assert.All(pixels, b => Assert.Equal(0, b));
    }

    [Fact]
    public void ALevelNarrowerThanABlockTakesTheBlocksFirstTexels()
    {
        var writer = new BlockWriter();
        writer.Write(64, 7);
        writer.Write(0, 7);
        writer.Write(127, 7);
        writer.Write(0, 7);
        writer.Write(0, 7);
        writer.Write(0, 7);
        writer.Write(0, 7);
        writer.Write(127, 7);
        writer.Write(127, 7);
        writer.Write(1, 1);
        writer.Write(1, 1);
        writer.Write(0, 3);
        writer.Write(15, 4);

        byte[] pixels = new byte[2 * 2 * 4];
        BlockDecoder.DecodeLevel(BlockFormat.Bc7Srgb, writer.Block(), 2, 2, pixels);

        // The 2×2 level is the block's first two texels of its first two rows, not its
        // first four texels in a row.
        Assert.Equal((1, 1, 1, 255), Texel(pixels, 0));
        Assert.Equal((255, 1, 1, 255), Texel(pixels, 1));
    }

    [Fact]
    public void ATruncatedLevelStopsRatherThanReadingPastTheEnd()
    {
        byte[] pixels = new byte[8 * 8 * 4];

        BlockDecoder.DecodeLevel(BlockFormat.Bc7Srgb, new byte[16], 8, 8, pixels);

        Assert.All(pixels, b => Assert.Equal(0, b));
    }

    [Fact]
    public void OnlyTheFormatsThePipelineProducesCanBeDecoded()
    {
        Assert.True(BlockDecoder.CanDecode(BlockFormat.Bc7Srgb));
        Assert.True(BlockDecoder.CanDecode(BlockFormat.Bc7Unorm));
        Assert.True(BlockDecoder.CanDecode(BlockFormat.Bc5Unorm));
        Assert.True(BlockDecoder.CanDecode(BlockFormat.Bc4Unorm));

        Assert.Throws<NotSupportedException>(
            () => BlockDecoder.DecodeLevel((BlockFormat)99, new byte[16], 4, 4, new byte[64]));
    }
}
