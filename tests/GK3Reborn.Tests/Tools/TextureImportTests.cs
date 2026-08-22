using System.Text.Json;
using GK3Reborn.Content.Manifests;
using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Foundation.Diagnostics;
using GK3Reborn.Tools.Stages;
using Xunit;

namespace GK3Reborn.Tests.Tools;

/// <summary>
/// Checks that an import leaves the enhanced set alone.
/// </summary>
/// <remarks>
/// The enhanced textures are hand-corrected work living outside the repository, so a
/// rerun that wrote over them destroys something nothing can give back. That makes this
/// worth a test rather than a convention: the failure is silent, it is discovered weeks
/// later when a room looks wrong, and by then there is nothing to compare against.
/// </remarks>
public sealed class TextureImportTests : IDisposable
{
    private readonly string _workspace = Path.Combine(
        Path.GetTempPath(), "gk3r-import-" + Guid.NewGuid().ToString("N"));

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Directory.Exists(_workspace))
        {
            Directory.Delete(_workspace, recursive: true);
        }
    }

    [Fact]
    public void A_texture_already_in_the_enhanced_set_is_not_written_over()
    {
        Build(out string enhanced, out _);

        byte[] byHand = Png(64, 64, 200);
        File.WriteAllBytes(Path.Combine(enhanced, "WALL.PNG"), byHand);

        var diagnostics = new DiagnosticBag();
        bool ran = Run(diagnostics, overwrite: false);

        Assert.True(ran);
        Assert.False(diagnostics.HasErrors);

        Assert.Equal(byHand, File.ReadAllBytes(Path.Combine(enhanced, "WALL.PNG")));

        EnhancedTexture record = Manifest().Textures.Single();
        Assert.Equal(TextureVerdict.Kept, record.Verdict);
        Assert.Empty(record.Rejections);
    }

    [Fact]
    public void Asking_for_it_writes_over_it()
    {
        Build(out string enhanced, out string candidates);

        File.WriteAllBytes(Path.Combine(enhanced, "WALL.PNG"), Png(64, 64, 200));

        Assert.True(Run(new DiagnosticBag(), overwrite: true));

        Assert.Equal(
            File.ReadAllBytes(Path.Combine(candidates, "WALL_v2.png")),
            File.ReadAllBytes(Path.Combine(enhanced, "WALL.PNG")));

        Assert.Equal(TextureVerdict.Draft, Manifest().Textures.Single().Verdict);
    }

    [Fact]
    public void A_name_nothing_has_yet_is_written()
    {
        Build(out string enhanced, out string candidates);

        Assert.True(Run(new DiagnosticBag(), overwrite: false));

        Assert.Equal(
            File.ReadAllBytes(Path.Combine(candidates, "WALL_v2.png")),
            File.ReadAllBytes(Path.Combine(enhanced, "WALL.PNG")));

        Assert.Equal(TextureVerdict.Draft, Manifest().Textures.Single().Verdict);
    }

    /// <summary>A workspace holding one original, one candidate and a plan naming it.</summary>
    private void Build(out string enhanced, out string candidates)
    {
        enhanced = Path.Combine(_workspace, "enhanced", "textures");
        candidates = Path.Combine(_workspace, "enhanced", "textures", "pilot");

        string normalized = Path.Combine(_workspace, "normalized", "textures");
        string manifests = Path.Combine(_workspace, "manifests");

        Directory.CreateDirectory(enhanced);
        Directory.CreateDirectory(candidates);
        Directory.CreateDirectory(normalized);
        Directory.CreateDirectory(manifests);

        File.WriteAllBytes(Path.Combine(normalized, "WALL.PNG"), Png(64, 64, 90));
        File.WriteAllBytes(Path.Combine(candidates, "WALL_v2.png"), Png(256, 256, 140));

        var plan = new TexturePlanManifest
        {
            SchemaVersion = 1,
            Stage = "test",
            SourceRoot = "test",
            TierCounts = new Dictionary<string, int> { ["tier0"] = 1 },
            TotalMegapixels = 0.004,
            Textures =
            [
                new TexturePlanEntry
                {
                    Name = "WALL",
                    Width = 64,
                    Height = 64,
                    HasAlpha = false,
                    Tier = 0,
                    IsFlatColor = false,
                    TargetSize = 256,
                    UsedByCharacters = 0,
                    UsedByProps = 0,
                    UsedByRooms = 1,
                    Referrers = ["TEST"],
                },
            ],
        };

        File.WriteAllText(
            Path.Combine(manifests, "texture-plan.json"),
            JsonSerializer.Serialize(plan, ManifestJson.Options));
    }

    private bool Run(DiagnosticBag diagnostics, bool overwrite) =>
        new TextureImportStage(_ => { }).Run(
            _workspace, Path.Combine("enhanced", "textures", "pilot"), "_v2", "test",
            overwrite, diagnostics);

    private EnhancedTextureManifest Manifest() =>
        JsonSerializer.Deserialize<EnhancedTextureManifest>(
            File.ReadAllText(Path.Combine(_workspace, "manifests", "enhanced-textures.json")),
            ManifestJson.Options)!;

    /// <summary>A flat grey square, distinct per shade so a copy can be told from a keep.</summary>
    private static byte[] Png(int width, int height, byte shade)
    {
        byte[] pixels = new byte[width * height * 4];

        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = shade;
            pixels[i + 1] = shade;
            pixels[i + 2] = shade;
            pixels[i + 3] = 255;
        }

        return PngWriter.Encode(new DecodedImage(width, height, pixels, HasAlpha: false, "test"));
    }
}
