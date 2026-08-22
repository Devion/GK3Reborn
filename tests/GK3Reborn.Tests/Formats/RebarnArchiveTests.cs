using System.Text;
using GK3Reborn.Content;
using GK3Reborn.Formats;
using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Formats.Rebarn;
using GK3Reborn.Foundation.Diagnostics;
using Xunit;

namespace GK3Reborn.Tests.Formats;

/// <summary>
/// The ReBarn container: what it writes, what it reads back, and how it fails.
/// </summary>
/// <remarks>
/// The round trip is the point of most of these. A pack is written once by a tool and read
/// thousands of times by the game, and every one of the interesting failures — a truncated
/// volume, an index that does not match the data, an entry pointing past the end — produces
/// bytes that look perfectly plausible until something checks them.
/// </remarks>
public sealed class RebarnArchiveTests : IDisposable
{
    private readonly string _directory =
        Directory.CreateTempSubdirectory("gk3r-rebarn").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A mapping the test failed to release keeps the file; not worth failing over.
        }
    }

    private string Path(string name) => System.IO.Path.Combine(_directory, name);

    [Fact]
    public void WritesAndReadsBack()
    {
        var builder = new RebarnBuilder();
        byte[] colour = RandomBytes(4096);
        byte[] normal = RandomBytes(1024);

        Assert.True(builder.AddBytes(RebarnKind.Texture, "R25WALLS.DDS", colour, RebarnPayload.Dds));
        Assert.True(builder.AddBytes(RebarnKind.Normal, "R25WALLS.DDS", normal, RebarnPayload.Dds));

        string path = Path("Reborn.rebarn");
        RebarnVolumeReport report = builder.Write(path);

        Assert.Equal(2, report.Count);

        using RebarnArchive pack = RebarnArchive.Open(path);

        Assert.Equal(2, pack.Count);
        Assert.Equal(colour, pack.Read(RebarnKind.Texture, "R25WALLS"));
        Assert.Equal(normal, pack.Read(RebarnKind.Normal, "R25WALLS"));
    }

    [Fact]
    public void TheSameNameUnderTwoKindsIsTwoEntries()
    {
        // Every material channel is named for its colour texture, so this is the normal
        // case rather than an edge one: without the kind in the key they would collide.
        var builder = new RebarnBuilder();

        builder.AddBytes(RebarnKind.Texture, "GAB_FACE.DDS", [1, 2, 3], RebarnPayload.Dds);
        builder.AddBytes(RebarnKind.Normal, "GAB_FACE.DDS", [4, 5, 6], RebarnPayload.Dds);
        builder.AddBytes(RebarnKind.Orm, "GAB_FACE.DDS", [7, 8, 9], RebarnPayload.Dds);
        builder.AddBytes(RebarnKind.Height, "GAB_FACE.DDS", [10, 11], RebarnPayload.Dds);

        string path = Path("Reborn.rebarn");
        builder.Write(path);

        using RebarnArchive pack = RebarnArchive.Open(path);

        Assert.Equal([1, 2, 3], pack.Read(RebarnKind.Texture, "GAB_FACE"));
        Assert.Equal([4, 5, 6], pack.Read(RebarnKind.Normal, "GAB_FACE"));
        Assert.Equal([7, 8, 9], pack.Read(RebarnKind.Orm, "GAB_FACE"));
        Assert.Equal([10, 11], pack.Read(RebarnKind.Height, "GAB_FACE"));
    }

    [Fact]
    public void NamesAreMatchedWithoutExtensionOrCase()
    {
        var builder = new RebarnBuilder();
        builder.AddBytes(RebarnKind.Texture, "R25WALLS.DDS", [42], RebarnPayload.Dds);
        builder.Write(Path("Reborn.rebarn"));

        using RebarnArchive pack = RebarnArchive.Open(Path("Reborn.rebarn"));

        Assert.True(pack.Has(RebarnKind.Texture, "R25WALLS"));
        Assert.True(pack.Has(RebarnKind.Texture, "r25walls"));
        Assert.True(pack.Has(RebarnKind.Texture, "R25WALLS.BMP"));
        Assert.True(pack.Has(RebarnKind.Texture, "textures/R25WALLS.dds"));
        Assert.False(pack.Has(RebarnKind.Normal, "R25WALLS"));
    }

    [Fact]
    public void DeflatedEntriesRoundTrip()
    {
        byte[] json = Encoding.UTF8.GetBytes(new string('{', 40000));

        var builder = new RebarnBuilder();
        builder.AddBytes(
            RebarnKind.Manifest, "corpus.json", json, RebarnPayload.Json, RebarnCompression.Deflate);

        string path = Path("Reborn.rebarn");
        RebarnVolumeReport report = builder.Write(path);

        // Highly repetitive, so this is a real check that it was compressed at all rather
        // than stored under a compression flag.
        Assert.True(report.Bytes < json.Length / 4);

        using RebarnArchive pack = RebarnArchive.Open(path);

        Assert.Equal(json, pack.Read(RebarnKind.Manifest, "corpus"));
    }

    [Fact]
    public void EveryEntryStartsOnAnAlignedBoundary()
    {
        var builder = new RebarnBuilder();

        // Deliberately awkward lengths, so that without padding the next entry would start
        // somewhere a copy engine would not like.
        for (int i = 0; i < 12; i++)
        {
            builder.AddBytes(RebarnKind.Texture, $"T{i}.DDS", RandomBytes(37 + (i * 13)), RebarnPayload.Dds);
        }

        string path = Path("Reborn.rebarn");
        builder.Write(path);

        using RebarnArchive pack = RebarnArchive.Open(path);

        foreach (RebarnEntry entry in pack.Entries)
        {
            Assert.Equal(0, entry.Offset % RebarnFormat.Alignment);
        }
    }

    [Fact]
    public void TheSameInputsProduceTheSameFile()
    {
        static void Build(string path)
        {
            var builder = new RebarnBuilder();

            for (int i = 0; i < 8; i++)
            {
                builder.AddBytes(RebarnKind.Texture, $"T{i}.DDS", [(byte)i, (byte)(i * 3)], RebarnPayload.Dds);
            }

            builder.Write(path);
        }

        Build(Path("a.rebarn"));
        Build(Path("b.rebarn"));

        byte[] a = File.ReadAllBytes(Path("a.rebarn"));
        byte[] b = File.ReadAllBytes(Path("b.rebarn"));

        // Everything but the timestamp in the header, which is deliberately the one field
        // that moves. Bytes 56..64 are BuiltUtcTicks.
        Assert.Equal(a.Length, b.Length);
        Assert.Equal(a[..56], b[..56]);
        Assert.Equal(a[64..], b[64..]);
    }

    [Fact]
    public void ADuplicateKeyIsRefusedRatherThanOverwriting()
    {
        var builder = new RebarnBuilder();

        Assert.True(builder.AddBytes(RebarnKind.Texture, "R25WALLS.DDS", [1]));
        Assert.False(builder.AddBytes(RebarnKind.Texture, "r25walls.dds", [2]));
        Assert.Equal(1, builder.Count);
    }

    [Fact]
    public void ATruncatedPackIsRefusedOnOpen()
    {
        var builder = new RebarnBuilder();
        builder.AddBytes(RebarnKind.Texture, "R25WALLS.DDS", RandomBytes(8192), RebarnPayload.Dds);

        string path = Path("Reborn.rebarn");
        builder.Write(path);

        byte[] whole = File.ReadAllBytes(path);
        File.WriteAllBytes(path, whole[..(whole.Length / 2)]);

        FormatParseException error =
            Assert.Throws<FormatParseException>(() => RebarnArchive.Open(path));

        Assert.Equal("GK3R1172", error.Diagnostic.Code);
    }

    [Fact]
    public void ADamagedIndexIsRefusedOnOpen()
    {
        var builder = new RebarnBuilder();
        builder.AddBytes(RebarnKind.Texture, "R25WALLS.DDS", RandomBytes(512), RebarnPayload.Dds);

        string path = Path("Reborn.rebarn");
        builder.Write(path);

        byte[] whole = File.ReadAllBytes(path);

        // Flip a bit in the last index record, which the header's checksum covers.
        whole[^3] ^= 0x40;
        File.WriteAllBytes(path, whole);

        FormatParseException error =
            Assert.Throws<FormatParseException>(() => RebarnArchive.Open(path));

        Assert.Equal("GK3R1173", error.Diagnostic.Code);
    }

    [Fact]
    public void SomethingThatIsNotAPackIsRefusedByName()
    {
        string path = Path("Reborn.rebarn");
        File.WriteAllBytes(path, RandomBytes(4096));

        FormatParseException error =
            Assert.Throws<FormatParseException>(() => RebarnArchive.Open(path));

        Assert.Equal("GK3R1170", error.Diagnostic.Code);
    }

    [Fact]
    public void DamageToTheDataSectionIsFoundByVerifyRatherThanOnOpen()
    {
        // The index checksum says nothing about the data section, which is where all the
        // bytes are. Verify is what reads them.
        var builder = new RebarnBuilder();
        builder.AddBytes(RebarnKind.Texture, "R25WALLS.DDS", RandomBytes(4096), RebarnPayload.Dds);

        string path = Path("Reborn.rebarn");
        builder.Write(path);

        byte[] whole = File.ReadAllBytes(path);
        whole[RebarnFormat.Alignment + 10] ^= 0xFF;
        File.WriteAllBytes(path, whole);

        using RebarnArchive pack = RebarnArchive.Open(path);

        Assert.False(pack.Verify(pack.Entries[0]));
    }

    [Fact]
    public void LaterPacksOverrideEarlierOnes()
    {
        var first = new RebarnBuilder();
        first.AddBytes(RebarnKind.Texture, "R25WALLS.DDS", [1], RebarnPayload.Dds);
        first.AddBytes(RebarnKind.Texture, "R25FLOOR.DDS", [2], RebarnPayload.Dds);
        first.Write(Path("Reborn.rebarn"));

        var patch = new RebarnBuilder();
        patch.AddBytes(RebarnKind.Texture, "R25WALLS.DDS", [99], RebarnPayload.Dds);
        patch.Write(Path("RebornPatch.rebarn"));

        using RebarnContent content = RebarnContent.Open(_directory);

        Assert.Equal(2, content.VolumeCount);
        Assert.Equal(2, content.Count);
        Assert.Equal([99], content.Read(RebarnKind.Texture, "R25WALLS"));
        Assert.Equal([2], content.Read(RebarnKind.Texture, "R25FLOOR"));
    }

    [Fact]
    public void AMissingDirectoryIsNotAnError()
    {
        using RebarnContent content = RebarnContent.Open(Path("nothing-here"));

        Assert.Equal(0, content.VolumeCount);
        Assert.Equal(0, content.Count);
        Assert.False(content.Has(RebarnKind.Texture, "R25WALLS"));
        Assert.Null(content.Describe());
    }

    [Fact]
    public void OneUnreadablePackCostsThatPackAndNothingElse()
    {
        var good = new RebarnBuilder();
        good.AddBytes(RebarnKind.Texture, "R25WALLS.DDS", [7], RebarnPayload.Dds);
        good.Write(Path("Reborn.rebarn"));

        File.WriteAllText(Path("RebornBroken.rebarn"), "this is not a pack at all, at all");

        var diagnostics = new DiagnosticBag();
        using RebarnContent content = RebarnContent.Open(_directory, diagnostics);

        Assert.Equal(1, content.VolumeCount);
        Assert.Equal([7], content.Read(RebarnKind.Texture, "R25WALLS"));
        Assert.Contains(diagnostics.Items, d => d.Code == "GK3R1176");
    }

    [Fact]
    public void AMappedReadSeesTheSameBytesAsACopiedOne()
    {
        byte[] payload = RandomBytes(9999);

        var builder = new RebarnBuilder();
        builder.AddBytes(RebarnKind.Texture, "R25WALLS.DDS", payload, RebarnPayload.Dds);
        builder.Write(Path("Reborn.rebarn"));

        using RebarnArchive pack = RebarnArchive.Open(Path("Reborn.rebarn"));
        RebarnEntry entry = pack.Entries[0];

        Assert.Equal(payload, pack.ReadMapped(entry).ToArray());
        Assert.Equal(payload, pack.Read(entry));
    }

    [Fact]
    public void KeysIgnoreDirectoriesAndExtensions()
    {
        Assert.Equal(
            RebarnFormat.Key(RebarnKind.Texture, "R25WALLS"),
            RebarnFormat.Key(RebarnKind.Texture, @"build\textures\r25walls.dds"));

        Assert.NotEqual(
            RebarnFormat.Key(RebarnKind.Texture, "R25WALLS"),
            RebarnFormat.Key(RebarnKind.Normal, "R25WALLS"));
    }

    [Fact]
    public void ADdsReadOutOfAPackDecodesToTheSameImageAsALooseOne()
    {
        // The whole point of the container: a texture goes to the device straight out of
        // the mapped pack. If DdsFile cannot read what the packer wrote, the tool produces
        // something the game silently falls back from, which is the failure this project
        // keeps meeting.
        byte[] bc7 = Dds(256, 128, mips: 9, dxgi: 99, bytesPerBlock: 16);
        byte[] bc5 = Dds(128, 128, mips: 8, dxgi: 83, bytesPerBlock: 16);
        byte[] bc4 = Dds(64, 64, mips: 7, dxgi: 80, bytesPerBlock: 8);

        var builder = new RebarnBuilder();
        builder.AddBytes(RebarnKind.Texture, "R25WALLS.DDS", bc7, RebarnPayload.Dds);
        builder.AddBytes(RebarnKind.Normal, "R25WALLS.DDS", bc5, RebarnPayload.Dds);
        builder.AddBytes(RebarnKind.Height, "R25WALLS.DDS", bc4, RebarnPayload.Dds);
        builder.Write(Path("Reborn.rebarn"));

        using RebarnContent content = RebarnContent.Open(_directory);

        CompressedImage? colour = content.ReadTexture(RebarnKind.Texture, "R25WALLS");
        CompressedImage? normal = content.ReadTexture(RebarnKind.Normal, "R25WALLS");
        CompressedImage? height = content.ReadTexture(RebarnKind.Height, "R25WALLS");

        Assert.Equal(BlockFormat.Bc7Srgb, colour!.Value.Format);
        Assert.Equal(256, colour.Value.Width);
        Assert.Equal(BlockFormat.Bc5Unorm, normal!.Value.Format);
        Assert.Equal(BlockFormat.Bc4Unorm, height!.Value.Format);

        // A BC4 block is eight bytes, not sixteen, and every level offset depends on it.
        Assert.Equal(8, height.Value.BlockSize);
        Assert.Equal((0, 16 * 16 * 8, 64, 64), height.Value.Level(0));
        Assert.Equal((16 * 16 * 8, 8 * 8 * 8, 32, 32), height.Value.Level(1));

        // Same bytes as decoding the file directly, which is what "no copy" has to mean.
        Assert.Equal(
            DdsFile.Read(bc4, "loose").Blocks.ToArray(),
            height.Value.Blocks.ToArray());
    }

    [Fact]
    public void ABadDdsInAPackCostsThatTextureAndNothingElse()
    {
        var builder = new RebarnBuilder();
        builder.AddBytes(RebarnKind.Texture, "GOOD.DDS", Dds(64, 64, 7, 99, 16), RebarnPayload.Dds);
        builder.AddBytes(RebarnKind.Texture, "BAD.DDS", RandomBytes(400), RebarnPayload.Dds);
        builder.Write(Path("Reborn.rebarn"));

        using RebarnContent content = RebarnContent.Open(_directory);
        var diagnostics = new DiagnosticBag();

        Assert.NotNull(content.ReadTexture(RebarnKind.Texture, "GOOD", diagnostics));
        Assert.Null(content.ReadTexture(RebarnKind.Texture, "BAD", diagnostics));
        Assert.Contains(diagnostics.Items, d => d.Code == "GK3R1177");
    }

    [Fact]
    public void TheLoaderSTextureLayerReadsFromAPack()
    {
        // The path SceneLoader actually takes. RebarnContent being right is not the same as
        // CompressedTextures asking it, and the second is what puts a texture on screen.
        var builder = new RebarnBuilder();
        builder.AddBytes(RebarnKind.Texture, "R25WALLS.DDS", Dds(64, 64, 7, 99, 16), RebarnPayload.Dds);
        builder.AddBytes(RebarnKind.Normal, "R25WALLS.DDS", Dds(64, 64, 7, 83, 16), RebarnPayload.Dds);
        builder.AddBytes(RebarnKind.Orm, "R25WALLS.DDS", Dds(64, 64, 7, 98, 16), RebarnPayload.Dds);
        builder.AddBytes(RebarnKind.Height, "R25WALLS.DDS", Dds(64, 64, 7, 80, 8), RebarnPayload.Dds);
        builder.Write(Path("Reborn.rebarn"));

        using RebarnContent packs = RebarnContent.Open(_directory);
        CompressedTextures set = CompressedTextures.Open(Path("no-build-directory"), packs);

        Assert.Equal(1, set.Count);
        Assert.Equal(1, set.NormalCount);
        Assert.Equal(1, set.OrmCount);
        Assert.Equal(1, set.HeightCount);

        Assert.True(set.Has("R25WALLS"));
        Assert.True(set.HasNormal("r25walls.bmp"));
        Assert.False(set.Has("R25FLOOR"));

        Assert.Equal(BlockFormat.Bc7Srgb, set.Read("R25WALLS")!.Value.Format);
        Assert.Equal(BlockFormat.Bc5Unorm, set.ReadNormal("R25WALLS")!.Value.Format);
        Assert.Equal(BlockFormat.Bc7Unorm, set.ReadOrm("R25WALLS")!.Value.Format);
        Assert.Equal(BlockFormat.Bc4Unorm, set.ReadHeight("R25WALLS")!.Value.Format);
        Assert.Null(set.Read("R25FLOOR"));
    }

    [Fact]
    public void ALooseDdsWinsOverThePack()
    {
        // While a set is still moving, a texture recompressed into build/ has to take effect
        // without a fifteen-gigabyte rebuild.
        var builder = new RebarnBuilder();
        builder.AddBytes(RebarnKind.Texture, "R25WALLS.DDS", Dds(64, 64, 7, 99, 16), RebarnPayload.Dds);
        builder.Write(Path("Reborn.rebarn"));

        string build = Path("build");
        Directory.CreateDirectory(System.IO.Path.Combine(build, "textures"));

        // A different size, so which one answered is visible in the result.
        File.WriteAllBytes(
            System.IO.Path.Combine(build, "textures", "R25WALLS.dds"), Dds(128, 128, 8, 99, 16));

        using RebarnContent packs = RebarnContent.Open(_directory);
        CompressedTextures set = CompressedTextures.Open(build, packs);

        Assert.Equal(1, set.Count);
        Assert.Equal(128, set.Read("R25WALLS")!.Value.Width);
    }

    [Fact]
    public void AnEmptyBuildDirectoryMeansThePacksAndNothingElse()
    {
        // What --rebarn passes. It must not be combined with "textures" into a relative
        // path, which would index whatever happened to sit beside the working directory.
        var builder = new RebarnBuilder();
        builder.AddBytes(RebarnKind.Texture, "R25WALLS.DDS", Dds(64, 64, 7, 99, 16), RebarnPayload.Dds);
        builder.Write(Path("Reborn.rebarn"));

        using RebarnContent packs = RebarnContent.Open(_directory);
        CompressedTextures set = CompressedTextures.Open(string.Empty, packs);

        Assert.Equal(1, set.Count);
        Assert.Equal(64, set.Read("R25WALLS")!.Value.Width);
    }

    /// <summary>A DDS with a DX10 header and a full chain of blocks that are not zero.</summary>
    private static byte[] Dds(int width, int height, int mips, uint dxgi, int bytesPerBlock)
    {
        int blocks = 0;
        int w = width;
        int h = height;

        for (int i = 0; i < mips; i++)
        {
            blocks += Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4);
            w = Math.Max(1, w / 2);
            h = Math.Max(1, h / 2);
        }

        var file = new byte[148 + (blocks * bytesPerBlock)];
        "DDS "u8.CopyTo(file);
        BitConverter.GetBytes(124).CopyTo(file, 4);
        BitConverter.GetBytes(height).CopyTo(file, 12);
        BitConverter.GetBytes(width).CopyTo(file, 16);
        BitConverter.GetBytes(mips).CopyTo(file, 28);
        "DX10"u8.CopyTo(file.AsSpan(84));
        BitConverter.GetBytes(dxgi).CopyTo(file, 128);
        BitConverter.GetBytes(3).CopyTo(file, 132);   // resource dimension: 2D
        BitConverter.GetBytes(1).CopyTo(file, 140);   // array size

        RandomBytes(file.Length - 148).CopyTo(file, 148);
        return file;
    }

    private static byte[] RandomBytes(int count)
    {
        // Seeded, so a failure is reproducible.
        var random = new Random(count * 7919);
        var bytes = new byte[count];
        random.NextBytes(bytes);
        return bytes;
    }
}
