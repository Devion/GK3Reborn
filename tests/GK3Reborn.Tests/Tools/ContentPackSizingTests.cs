using GK3Reborn.Tools.Stages;
using Xunit;

namespace GK3Reborn.Tests.Tools;

/// <summary>
/// What the packer will reuse, and what size it encodes at.
/// </summary>
/// <remarks>
/// Both of these are worth a test because both failed silently in practice. A pack built
/// from a stale DDS is a valid pack full of last night's pictures, and a texture encoded at
/// the wrong size is a valid texture — neither throws, and both are discovered by somebody
/// noticing that a room looks wrong.
/// </remarks>
public sealed class ContentPackSizingTests : IDisposable
{
    private readonly string _directory =
        Directory.CreateTempSubdirectory("gk3r-packsize").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void ADdsOlderThanItsPngIsNotFresh()
    {
        // The defect this exists for: an earlier compression run leaves a DDS in build/,
        // the PNG is regenerated, the DDS keeps its dimensions — and a rule that only
        // compares dimensions packs the old picture. Found in the lobby, on the register.
        string png = Png(256, 256);
        string dds = Dds(256, 256);

        File.SetLastWriteTimeUtc(dds, new DateTime(2026, 8, 22, 0, 39, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(png, new DateTime(2026, 8, 22, 10, 46, 0, DateTimeKind.Utc));

        Assert.False(ContentPackStage.Fresh(dds, png, 256, 256));
    }

    [Fact]
    public void ADdsNewerThanItsPngAtTheRightSizeIsFresh()
    {
        string png = Png(256, 256);
        string dds = Dds(256, 256);

        File.SetLastWriteTimeUtc(png, new DateTime(2026, 8, 22, 0, 39, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(dds, new DateTime(2026, 8, 22, 10, 46, 0, DateTimeKind.Utc));

        Assert.True(ContentPackStage.Fresh(dds, png, 256, 256));
    }

    [Fact]
    public void ADdsOfTheWrongSizeIsNotFreshHoweverRecent()
    {
        string png = Png(256, 256);
        string dds = Dds(2048, 2048);

        File.SetLastWriteTimeUtc(png, new DateTime(2026, 8, 22, 0, 39, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(dds, new DateTime(2026, 8, 22, 10, 46, 0, DateTimeKind.Utc));

        Assert.False(ContentPackStage.Fresh(dds, png, 256, 256));
    }

    [Fact]
    public void AMissingDdsIsNotFresh() =>
        Assert.False(ContentPackStage.Fresh(
            Path.Combine(_directory, "nothing.dds"), Png(64, 64), 64, 64));

    [Theory]
    [InlineData(2048, 2048, 512, 512, 512)]
    [InlineData(2048, 1024, 512, 512, 256)]
    [InlineData(1024, 2048, 1024, 512, 1024)]
    [InlineData(512, 512, 1024, 512, 512)]   // never enlarged
    [InlineData(2048, 2048, 0, 2048, 2048)]  // no cap
    public void TheCapIsOnTheLongestEdgeAndKeepsTheAspect(
        int width, int height, int cap, int expectedWidth, int expectedHeight)
    {
        (int w, int h, _) = ContentPackStage.Target((width, height, false), cap);

        Assert.Equal(expectedWidth, w);
        Assert.Equal(expectedHeight, h);
    }

    [Fact]
    public void AnExtentTheCapBindsOnIsAWholeNumberOfBlocks()
    {
        // Only when the cap binds. A block is four texels, and a resize that lands on 239
        // pads the last block of every row; picking the size is free, so it picks a whole one.
        foreach ((int w, int h) in new[] { (1024, 960), (1856, 1728), (1504, 704), (2048, 2048) })
        {
            (int cw, int ch, _) = ContentPackStage.Target((w, h, false), 512);

            Assert.Equal(0, cw % 4);
            Assert.Equal(0, ch % 4);
            Assert.Equal(512, Math.Max(cw, ch));
        }
    }

    [Fact]
    public void ASourceInsideTheCapIsPassedThroughUntouched()
    {
        // Including the odd extents the corpus is full of — 81x26, 94x94. A block format
        // allows a partial final block, so resizing these to suit the encoder would be a
        // change to the picture for no reason at all.
        foreach ((int w, int h) in new[] { (81, 26), (94, 94), (32, 30), (2, 2) })
        {
            Assert.Equal((w, h, false), ContentPackStage.Target((w, h, false), 512));
        }
    }

    [Fact]
    public void AlphaSurvivesTheCap()
    {
        (_, _, bool alpha) = ContentPackStage.Target((2048, 2048, true), 512);
        Assert.True(alpha);
    }

    [Fact]
    public void ATruncatedPngIsRefusedRatherThanRead()
    {
        // Two files in the workspace are truncated. The header of a half-written PNG is
        // perfectly good and says nothing about the rest, so the size check has to look at
        // the end of the file as well.
        string path = Path.Combine(_directory, "cut.PNG");
        byte[] whole = File.ReadAllBytes(Png(64, 64));
        File.WriteAllBytes(path, whole[..(whole.Length - 20)]);

        Assert.Null(ContentPackStage.PngSize(path));
    }

    [Fact]
    public void AWholePngReadsItsSizeAndAlpha()
    {
        Assert.Equal((64, 32, false), ContentPackStage.PngSize(Png(64, 32)));
        Assert.Equal((64, 32, true), ContentPackStage.PngSize(Png(64, 32, alpha: true)));
    }

    /// <summary>A PNG with a real IHDR and a real IEND, which is all the size check reads.</summary>
    private string Png(int width, int height, bool alpha = false)
    {
        string path = Path.Combine(
            _directory, $"t{width}x{height}{(alpha ? "a" : string.Empty)}.PNG");

        var bytes = new List<byte>();
        bytes.AddRange([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        bytes.AddRange(Chunk("IHDR",
        [
            .. Big(width), .. Big(height),
            8, (byte)(alpha ? 6 : 2), 0, 0, 0,
        ]));

        // Enough filler that the file clears the size check's minimum length.
        bytes.AddRange(Chunk("IDAT", new byte[64]));
        bytes.AddRange(Chunk("IEND", []));

        File.WriteAllBytes(path, [.. bytes]);
        return path;
    }

    /// <summary>A DDS with a DX10 BC7 header, which is all the extent check reads.</summary>
    private string Dds(int width, int height)
    {
        string path = Path.Combine(_directory, $"t{width}x{height}.dds");
        var file = new byte[148];

        "DDS "u8.CopyTo(file);
        BitConverter.GetBytes(124).CopyTo(file, 4);
        BitConverter.GetBytes(height).CopyTo(file, 12);
        BitConverter.GetBytes(width).CopyTo(file, 16);
        BitConverter.GetBytes(1).CopyTo(file, 28);
        "DX10"u8.CopyTo(file.AsSpan(84));
        BitConverter.GetBytes(99).CopyTo(file, 128);

        File.WriteAllBytes(path, file);
        return path;
    }

    private static byte[] Big(int value) =>
        [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

    private static byte[] Chunk(string type, byte[] body) =>
        [.. Big(body.Length), .. System.Text.Encoding.ASCII.GetBytes(type), .. body, 0, 0, 0, 0];
}
