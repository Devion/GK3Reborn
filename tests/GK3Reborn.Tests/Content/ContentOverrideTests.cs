using GK3Reborn.Content;
using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Formats.Rebarn;
using Xunit;

namespace GK3Reborn.Tests.Content;

/// <summary>
/// Tests for the <c>overrides/</c> directory, which outranks the packs and the archives.
/// </summary>
/// <remarks>
/// Two properties are worth pinning and everything else follows from them. A file put
/// there has to <em>win</em> — over the packs, over a loose build, over the game's own
/// archives — because an override that loses is indistinguishable from one that was never
/// read. And a directory that is not there has to cost nothing, because that is every
/// installation nobody has modified.
/// </remarks>
public sealed class ContentOverrideTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "gk3reborn-overrides-" + Guid.NewGuid().ToString("N"));

    public ContentOverrideTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>Writes a file at a path relative to the overrides root.</summary>
    private string Put(string relative, byte[] bytes)
    {
        string path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);

        return path;
    }

    private string PutPng(string relative, int width, int height, byte grey)
    {
        byte[] pixels = new byte[width * height * 4];

        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = grey;
            pixels[i + 1] = grey;
            pixels[i + 2] = grey;
            pixels[i + 3] = 255;
        }

        return Put(
            relative,
            PngWriter.Encode(new DecodedImage(width, height, pixels, false, "test")));
    }

    [Fact]
    public void A_directory_that_is_not_there_is_simply_an_empty_set()
    {
        // Every installation nobody has modified. It has to cost nothing and say nothing.
        ContentOverrides set = ContentOverrides.Open(Path.Combine(_root, "nothing-here"));

        Assert.True(set.IsEmpty);
        Assert.Equal(0, set.Count);
        Assert.Null(set.Describe());
        Assert.False(set.HasArchive("R25.SIF"));
        Assert.False(set.Has(RebarnKind.Texture, "R25WALLS"));
    }

    [Fact]
    public void A_png_with_no_directory_around_it_is_a_colour_texture()
    {
        // What somebody who dropped a picture in meant. Nothing else it could be.
        PutPng("R25WALLS.png", 4, 4, 200);

        ContentOverrides set = ContentOverrides.Open(_root);

        Assert.True(set.Has(RebarnKind.Texture, "R25WALLS"));
        Assert.True(set.Images(RebarnKind.Texture).ContainsKey("R25WALLS"));
        Assert.False(set.Has(RebarnKind.Normal, "R25WALLS"));
    }

    [Fact]
    public void A_kind_directory_anywhere_in_the_path_decides_the_kind()
    {
        // The player's own filing is theirs. Only the segments that name a kind are read,
        // so a mod folder around them changes nothing.
        PutPng("my mod/normals/R25WALLS.png", 4, 4, 128);

        ContentOverrides set = ContentOverrides.Open(_root);

        Assert.True(set.Has(RebarnKind.Normal, "R25WALLS"));
        Assert.False(set.Has(RebarnKind.Texture, "R25WALLS"));
    }

    [Fact]
    public void The_last_kind_directory_wins_over_an_earlier_one()
    {
        PutPng("normals/experiments/height/R25WALLS.png", 4, 4, 128);

        ContentOverrides set = ContentOverrides.Open(_root);

        Assert.True(set.Has(RebarnKind.Height, "R25WALLS"));
        Assert.False(set.Has(RebarnKind.Normal, "R25WALLS"));
    }

    [Fact]
    public void An_asset_of_the_original_game_stands_in_front_of_the_archives()
    {
        // Scripts, room definitions, sounds — everything a barn holds is matched by its
        // whole file name, because that is how the archives themselves are keyed.
        Put("R25.SIF", "[SCENE]"u8.ToArray());
        Put("anywhere/at/all/R25.NVC", "// mine"u8.ToArray());

        ContentOverrides set = ContentOverrides.Open(_root);

        Assert.True(set.HasArchive("R25.SIF"));
        Assert.True(set.HasArchive("r25.nvc"));
        Assert.Equal("// mine"u8.ToArray(), set.ReadArchive("R25.NVC"));

        // And not as a pack entry, because a .SIF is not something a pack has ever held.
        Assert.False(set.Has(RebarnKind.Texture, "R25"));
    }

    [Fact]
    public void A_texture_is_registered_in_front_of_the_archives_as_well()
    {
        // The point of doing both. A dropped bitmap has to reach the places that ask an
        // archive for one by name as well as the texture stack that asks for R25WALLS.
        PutPng("textures/R25WALLS.png", 4, 4, 200);

        ContentOverrides set = ContentOverrides.Open(_root);

        Assert.True(set.HasArchive("R25WALLS.png"));
        Assert.True(set.Has(RebarnKind.Texture, "R25WALLS"));
    }

    [Fact]
    public void An_override_beats_the_archive_it_stands_in_front_of()
    {
        Put("SOMETHING.TXT", "mine"u8.ToArray());

        // No archives at all, which is the same shape as an archive that has the name:
        // the override answers before any of them is asked.
        using GameArchives archives = GameArchives.Open(_root);
        archives.Overrides = ContentOverrides.Open(_root);

        Assert.True(archives.Exists("SOMETHING.TXT"));
        Assert.Equal("mine"u8.ToArray(), archives.Read("SOMETHING.TXT"));
        Assert.Contains("SOMETHING.TXT", archives.Names());
        Assert.Contains("SOMETHING.TXT", archives.Names(".TXT"));
        Assert.DoesNotContain("SOMETHING.TXT", archives.Names(".SIF"));
    }

    [Fact]
    public void An_image_override_joins_the_layer_that_is_asked_before_the_compressed_one()
    {
        // This is what makes a PNG beat a packed BC7 of the same name. The loader asks the
        // enhanced layer first everywhere, so an override that only reached the compressed
        // set would be shadowed by every texture that already has an enhanced version.
        PutPng("textures/R25WALLS.png", 4, 4, 77);

        ContentOverrides overrides = ContentOverrides.Open(_root);
        EnhancedTextures set = EnhancedTextures.Open(string.Empty, overrides);

        Assert.Equal(1, set.OverriddenCount);
        Assert.True(set.Has("R25WALLS"));

        DecodedImage? read = set.Read("R25WALLS");
        Assert.NotNull(read);
        Assert.Equal(77, read.Value.Pixels[0]);
    }

    [Fact]
    public void An_image_override_wins_over_a_workspace_file_of_the_same_name()
    {
        string workspace = Path.Combine(_root, "..", "gk3reborn-workspace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);

        try
        {
            byte[] pixels = new byte[4 * 4 * 4];
            Array.Fill(pixels, (byte)11);

            File.WriteAllBytes(
                Path.Combine(workspace, "R25WALLS.PNG"),
                PngWriter.Encode(new DecodedImage(4, 4, pixels, false, "test")));

            PutPng("textures/R25WALLS.png", 4, 4, 222);

            EnhancedTextures set =
                EnhancedTextures.Open(workspace, ContentOverrides.Open(_root));

            Assert.Equal(222, set.Read("R25WALLS")!.Value.Pixels[0]);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public void A_bitmap_override_decodes_even_though_the_layer_is_named_for_png()
    {
        // Somebody who extracted the original and edited it has a .BMP, not a .PNG, and
        // both have to reach the same place. Decided from the bytes rather than the name.
        PutPng("textures/BOTH.png", 4, 4, 5);
        Put("textures/PLAIN.bmp", Bitmap(4, 4, 90));

        ContentOverrides overrides = ContentOverrides.Open(_root);
        EnhancedTextures set = EnhancedTextures.Open(string.Empty, overrides);

        DecodedImage? read = set.Read("PLAIN");
        Assert.NotNull(read);
        Assert.Equal(90, read.Value.Pixels[0]);
    }

    [Fact]
    public void Editor_leavings_are_not_counted_as_overrides()
    {
        // A count that says 3 when the player put 2 files there is a count nobody trusts
        // again, and .DS_Store is a name nothing would ever ask for anyway.
        PutPng("textures/A.png", 4, 4, 1);
        Put(".DS_Store", [0]);
        Put("textures/Thumbs.db", [0]);

        ContentOverrides set = ContentOverrides.Open(_root);

        Assert.Equal(1, set.Count);
    }

    [Fact]
    public void What_is_being_overridden_is_said_out_loud()
    {
        // An override is invisible once it is on screen, which is the point of it. A run
        // in which a forgotten file stands in for the shipped one has to be tellable from
        // a run without it, and this line is the only way it ever will be.
        PutPng("textures/A.png", 4, 4, 1);
        Put("R25.SIF", "[SCENE]"u8.ToArray());

        string? said = ContentOverrides.Open(_root).Describe();

        Assert.NotNull(said);
        Assert.Contains("1 textures", said, StringComparison.Ordinal);
        Assert.Contains("1 game asset(s)", said, StringComparison.Ordinal);
    }

    /// <summary>A minimal 32-bit Windows bitmap of one colour.</summary>
    private static byte[] Bitmap(int width, int height, byte grey)
    {
        const int Header = 14 + 40;
        int pixels = width * height * 4;
        byte[] file = new byte[Header + pixels];

        file[0] = (byte)'B';
        file[1] = (byte)'M';
        BitConverter.GetBytes(file.Length).CopyTo(file, 2);
        BitConverter.GetBytes(Header).CopyTo(file, 10);
        BitConverter.GetBytes(40).CopyTo(file, 14);
        BitConverter.GetBytes(width).CopyTo(file, 18);
        BitConverter.GetBytes(height).CopyTo(file, 22);
        BitConverter.GetBytes((short)1).CopyTo(file, 26);
        BitConverter.GetBytes((short)32).CopyTo(file, 28);
        BitConverter.GetBytes(pixels).CopyTo(file, 34);

        for (int i = Header; i < file.Length; i += 4)
        {
            file[i] = grey;
            file[i + 1] = grey;
            file[i + 2] = grey;
            file[i + 3] = 255;
        }

        return file;
    }
}
