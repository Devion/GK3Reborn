using GK3Reborn.Content;
using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Foundation.Diagnostics;
using Xunit;

namespace GK3Reborn.Tests.Content;

/// <summary>
/// Tests for putting higher-resolution textures in front of the archives.
/// </summary>
/// <remarks>
/// A layer, not a replacement: the archives stay as they are and a texture with no
/// enhanced version loads from them as before, so a partial set is a perfectly good set.
/// That is the property worth pinning, along with what happens when one of the files is
/// bad — which, for generated content, is a matter of when rather than if.
/// </remarks>
public sealed class EnhancedTextureTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "gk3reborn-enhanced-" + Guid.NewGuid().ToString("N"));

    public EnhancedTextureTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private void Write(string name, int width, int height)
    {
        byte[] pixels = new byte[width * height * 4];
        Array.Fill(pixels, (byte)200);

        File.WriteAllBytes(
            Path.Combine(_directory, name),
            PngWriter.Encode(new DecodedImage(width, height, pixels, false, "test")));
    }

    [Fact]
    public void A_directory_that_is_not_there_is_simply_an_empty_set()
    {
        // Enhanced content is optional by design: the game runs from an installation and
        // these are an addition to it.
        EnhancedTextures set = EnhancedTextures.Open(Path.Combine(_directory, "nothing-here"));

        Assert.Equal(0, set.Count);
        Assert.False(set.Has("R25WALLS"));
        Assert.Null(set.Read("R25WALLS"));
    }

    [Fact]
    public void A_texture_is_found_by_the_name_the_geometry_uses()
    {
        Write("R25WALLS.PNG", 8, 8);
        EnhancedTextures set = EnhancedTextures.Open(_directory);

        // A surface refers to R25WALLS, the archive holds R25WALLS.BMP, and the enhanced
        // set holds R25WALLS.PNG. All three are the same texture.
        Assert.True(set.Has("R25WALLS"));
        Assert.True(set.Has("r25walls"));
        Assert.True(set.Has("R25WALLS.BMP"));
        Assert.False(set.Has("R25FLOOR"));
    }

    [Fact]
    public void What_comes_back_is_the_enhanced_image()
    {
        Write("LAMPSHADE.PNG", 64, 32);
        EnhancedTextures set = EnhancedTextures.Open(_directory);

        DecodedImage image = Assert.NotNull(set.Read("LAMPSHADE"));

        Assert.Equal(64, image.Width);
        Assert.Equal(32, image.Height);
        Assert.Equal(1, set.Count);
        Assert.Equal(["LAMPSHADE"], set.Names);
    }

    [Fact]
    public void A_file_that_will_not_decode_costs_that_texture_and_nothing_else()
    {
        Write("GOOD.PNG", 4, 4);
        File.WriteAllBytes(Path.Combine(_directory, "BAD.PNG"), "not a png at all"u8.ToArray());

        EnhancedTextures set = EnhancedTextures.Open(_directory);
        var diagnostics = new DiagnosticBag();

        // Null rather than a throw, so the loader falls back to the original: one bad file
        // in a set of hundreds should not fail a scene.
        Assert.Null(set.Read("BAD", diagnostics));
        Assert.NotNull(set.Read("GOOD", diagnostics));
        Assert.Contains(diagnostics.Items, d => d.Code == "GK3R1093");
    }
}
