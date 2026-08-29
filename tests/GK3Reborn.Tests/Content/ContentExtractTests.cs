using GK3Reborn.Content;
using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Formats.Rebarn;
using Xunit;

namespace GK3Reborn.Tests.Content;

/// <summary>
/// Tests for writing a pack back out as files somebody can edit.
/// </summary>
/// <remarks>
/// The half of the override story that makes the other half usable, and it has one
/// property that has to hold: what comes out has to go back in. The layout is read back by
/// <see cref="ContentOverrides"/> without anything being moved, and a texture asked for as
/// a PNG has to be the picture that was compressed rather than what the block format
/// happened to keep.
/// </remarks>
public sealed class ContentExtractTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "gk3reborn-extract-" + Guid.NewGuid().ToString("N"));

    public ContentExtractTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>Writes a one-volume pack holding a colour texture and a height map.</summary>
    private string WritePack()
    {
        var builder = new RebarnBuilder();

        builder.AddBytes(
            RebarnKind.Texture, "NUWALL2B.dds", Dds(BlockFormat.Bc7Unorm), RebarnPayload.Dds);

        builder.AddBytes(
            RebarnKind.Height, "NUWALL2B.dds", Dds(BlockFormat.Bc4Unorm), RebarnPayload.Dds);

        builder.AddBytes(
            RebarnKind.Manifest, "trees.json", "{}"u8.ToArray(), RebarnPayload.Json);

        string path = Path.Combine(_root, "Test.rebarn");
        builder.Write(path);

        return path;
    }

    [Fact]
    public void An_extract_writes_the_layout_the_override_layer_reads_back()
    {
        // The round trip is the whole feature: extract, edit in place, run. If the two
        // disagreed about where a normal map lives, every extracted file would have to be
        // moved by hand before it did anything.
        string pack = WritePack();
        string into = Path.Combine(_root, "overrides");

        using (RebarnContent packs = RebarnContent.OpenFiles([pack]))
        {
            ContentExtract.Result result =
                ContentExtract.FromPacks(packs, into, null, null, asPng: false, _ => { });

            Assert.Equal(3, result.Written);
            Assert.Equal(0, result.Failed);
        }

        Assert.True(File.Exists(Path.Combine(into, "textures", "NUWALL2B.dds")));
        Assert.True(File.Exists(Path.Combine(into, "height", "NUWALL2B.dds")));
        Assert.True(File.Exists(Path.Combine(into, "manifests", "trees.json")));

        ContentOverrides read = ContentOverrides.Open(into);

        Assert.True(read.Has(RebarnKind.Texture, "NUWALL2B"));
        Assert.True(read.Has(RebarnKind.Height, "NUWALL2B"));
        Assert.True(read.Has(RebarnKind.Manifest, "trees"));
    }

    [Fact]
    public void One_name_reaches_every_kind_that_holds_it()
    {
        // A surface is four files under one name, and somebody replacing a wall wants all
        // four in front of them, not the colour alone.
        string pack = WritePack();
        string into = Path.Combine(_root, "one");

        using RebarnContent packs = RebarnContent.OpenFiles([pack]);

        ContentExtract.Result result =
            ContentExtract.FromPacks(packs, into, null, "NUWALL2B", asPng: false, _ => { });

        Assert.Equal(2, result.Written);
        Assert.False(File.Exists(Path.Combine(into, "manifests", "trees.json")));
    }

    [Fact]
    public void A_height_map_asked_for_as_png_comes_out_grey_rather_than_red()
    {
        // BC4 is one channel because the source was grey stored across three - measured,
        // which is why it is BC4 at all. Dumped straight it would be a red picture that
        // loads perfectly and is not the map that was compressed.
        string pack = WritePack();
        string into = Path.Combine(_root, "png");

        using (RebarnContent packs = RebarnContent.OpenFiles([pack]))
        {
            ContentExtract.FromPacks(
                packs, into, [RebarnKind.Height], null, asPng: true, _ => { });
        }

        string file = Path.Combine(into, "height", "NUWALL2B.png");
        Assert.True(File.Exists(file));

        DecodedImage image = PngReader.Decode(File.ReadAllBytes(file), file);

        Assert.Equal(image.Pixels[0], image.Pixels[1]);
        Assert.Equal(image.Pixels[0], image.Pixels[2]);
    }

    [Fact]
    public void An_override_is_read_back_in_front_of_the_pack_it_came_from()
    {
        // Which is the point of extracting into overrides/, and also why extracting the
        // whole pack there is refused: every file would stand in front of itself.
        string pack = WritePack();
        string into = Path.Combine(_root, "layered");

        Directory.CreateDirectory(Path.Combine(into, "manifests"));
        File.WriteAllBytes(Path.Combine(into, "manifests", "trees.json"), "{\"mine\":1}"u8.ToArray());

        using RebarnContent packs = RebarnContent.OpenFiles([pack]);
        packs.Overrides = ContentOverrides.Open(into);

        Assert.Equal("{\"mine\":1}"u8.ToArray(), packs.Read(RebarnKind.Manifest, "trees"));

        // And a name only the override has is a name the pack now answers to, so anything
        // that lists what is available lists it.
        Assert.Contains("trees", packs.Names(RebarnKind.Manifest));
        Assert.Equal(1, packs.CountOf(RebarnKind.Manifest));
    }

    /// <summary>A one-block DDS in a format the engine's own reader accepts.</summary>
    private static byte[] Dds(BlockFormat format)
    {
        int blockBytes = CompressedImage.BytesPerBlock(format);
        byte[] file = new byte[148 + blockBytes];

        "DDS "u8.CopyTo(file);
        BitConverter.GetBytes(124).CopyTo(file, 4);
        BitConverter.GetBytes(0x000A_1007).CopyTo(file, 8);   // caps|height|width|pixelformat|linearsize
        BitConverter.GetBytes(4).CopyTo(file, 12);            // height
        BitConverter.GetBytes(4).CopyTo(file, 16);            // width
        BitConverter.GetBytes(blockBytes).CopyTo(file, 20);   // linear size
        BitConverter.GetBytes(1).CopyTo(file, 28);            // mip count
        BitConverter.GetBytes(32).CopyTo(file, 76);           // pixel format size
        BitConverter.GetBytes(0x4).CopyTo(file, 80);          // DDPF_FOURCC
        "DX10"u8.CopyTo(file.AsSpan(84));
        BitConverter.GetBytes(0x1000).CopyTo(file, 108);      // caps: texture

        // DX10 header: format, 2D, no flags, one array element.
        BitConverter.GetBytes(DxgiOf(format)).CopyTo(file, 128);
        BitConverter.GetBytes(3).CopyTo(file, 132);
        BitConverter.GetBytes(0).CopyTo(file, 136);
        BitConverter.GetBytes(1).CopyTo(file, 140);
        BitConverter.GetBytes(0).CopyTo(file, 144);

        // A BC4 block whose endpoints are equal reads as one constant value everywhere,
        // which is what makes the grey check above say something.
        if (format == BlockFormat.Bc4Unorm)
        {
            file[148] = 140;
            file[149] = 140;
        }

        return file;
    }

    private static int DxgiOf(BlockFormat format) => format switch
    {
        BlockFormat.Bc7Srgb => 99,
        BlockFormat.Bc7Unorm => 98,
        BlockFormat.Bc5Unorm => 83,
        _ => 80,
    };
}
