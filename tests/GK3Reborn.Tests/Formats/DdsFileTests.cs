using System.Buffers.Binary;
using GK3Reborn.Formats;
using GK3Reborn.Formats.Bitmaps;
using Xunit;

namespace GK3Reborn.Tests.Formats;

/// <summary>
/// Tests for the block-compressed textures the content pipeline builds.
/// </summary>
/// <remarks>
/// The trap in DDS is the mip chain. Nothing in the file says where a level starts — the
/// offsets are arithmetic over the block count, and a level narrower than four pixels still
/// occupies a whole block. Get that wrong and every level but the first is uploaded from
/// the wrong place, which shows up as garbage only once a surface is far enough away to be
/// minified.
/// </remarks>
public sealed class DdsFileTests
{
    private const uint DxgiBc7UnormSrgb = 99;

    /// <summary>Builds a DDS file with a chain of the right size but no meaningful content.</summary>
    private static byte[] File(
        int width, int height, int mips, string fourCc = "DX10", uint dxgi = DxgiBc7UnormSrgb)
    {
        int blocks = 0;
        int levelWidth = width;
        int levelHeight = height;

        for (int level = 0; level < mips; level++)
        {
            blocks += Math.Max(1, (levelWidth + 3) / 4) * Math.Max(1, (levelHeight + 3) / 4);
            levelWidth = Math.Max(1, levelWidth / 2);
            levelHeight = Math.Max(1, levelHeight / 2);
        }

        bool extended = fourCc == "DX10";
        int start = extended ? 148 : 128;
        byte[] file = new byte[start + (blocks * 16)];

        "DDS "u8.CopyTo(file);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), 124);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(12), (uint)height);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(16), (uint)width);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(28), (uint)mips);
        System.Text.Encoding.ASCII.GetBytes(fourCc).CopyTo(file.AsSpan(84));

        if (extended)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(128), dxgi);
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(140), 1);
        }

        // Every block distinguishable, so a level read from the wrong offset is visible.
        for (int i = start; i < file.Length; i++)
        {
            file[i] = (byte)(i - start);
        }

        return file;
    }

    [Fact]
    public void A_dx10_header_names_the_format()
    {
        CompressedImage image = DdsFile.Read(File(256, 128, 1), "TEST");

        Assert.Equal(256, image.Width);
        Assert.Equal(128, image.Height);
        Assert.Equal(1, image.Mips);
        Assert.Equal(BlockFormat.Bc7Srgb, image.Format);
    }

    [Fact]
    public void The_older_four_character_code_for_bc5_is_understood()
    {
        // texconv writes BC5U; other tools write ATI2 for the same thing, and the normal
        // maps in the build directory are the former.
        Assert.Equal(BlockFormat.Bc5Unorm, DdsFile.Read(File(64, 64, 1, "BC5U"), "T").Format);
        Assert.Equal(BlockFormat.Bc5Unorm, DdsFile.Read(File(64, 64, 1, "ATI2"), "T").Format);
    }

    [Fact]
    public void Each_level_starts_where_the_ones_before_it_end()
    {
        CompressedImage image = DdsFile.Read(File(256, 256, 9), "TEST");

        Assert.Equal(9, image.Mips);

        int expected = 0;

        for (int level = 0; level < image.Mips; level++)
        {
            (int offset, int length, int width, int height) = image.Level(level);

            Assert.Equal(expected, offset);
            Assert.Equal(256 >> level, width);
            Assert.Equal(256 >> level, height);

            expected += length;
        }

        Assert.Equal(image.Blocks.Length, expected);
    }

    [Fact]
    public void A_level_smaller_than_a_block_still_occupies_one()
    {
        // The last three levels of a chain are 4, 2 and 1 pixels, and all three are one
        // block. Dividing rather than rounding up makes the tail of the chain zero-length
        // and every offset after it wrong.
        CompressedImage image = DdsFile.Read(File(8, 8, 4), "TEST");

        (_, int length, int width, int height) = image.Level(3);

        Assert.Equal(1, width);
        Assert.Equal(1, height);
        Assert.Equal(CompressedImage.BlockBytes, length);
    }

    [Fact]
    public void The_blocks_are_a_window_onto_the_file_and_not_a_copy()
    {
        byte[] file = File(64, 64, 1);
        CompressedImage image = DdsFile.Read(file, "TEST");

        Assert.Equal(file.Length - 148, image.Blocks.Length);
        Assert.Equal(file[148], image.Blocks.Span[0]);
    }

    [Fact]
    public void A_file_with_fewer_blocks_than_its_chain_needs_is_refused()
    {
        byte[] file = File(256, 256, 9);

        Assert.Throws<FormatParseException>(() => DdsFile.Read(file.AsMemory(0, 2000), "TEST"));
    }

    [Fact]
    public void A_format_the_renderer_has_no_name_for_is_refused()
    {
        // Refused rather than guessed at. A pipeline that starts emitting BC1 or a
        // half-float should hear about it, not have its blocks read as BC7.
        Assert.Throws<FormatParseException>(
            () => DdsFile.Read(File(64, 64, 1, "DX10", dxgi: 71), "TEST"));

        Assert.Throws<FormatParseException>(() => DdsFile.Read(File(64, 64, 1, "DXT1"), "TEST"));
    }

    [Fact]
    public void Something_that_is_not_a_dds_file_is_refused()
    {
        Assert.False(DdsFile.CanDecode("not a texture"u8));
        Assert.Throws<FormatParseException>(() => DdsFile.Read(new byte[256], "TEST"));
    }
}
