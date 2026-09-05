using System.Text.Json;
using GK3Reborn.Content;
using GK3Reborn.Formats.Rebarn;
using GK3Reborn.Tools.Stages;
using GK3Reborn.UI;
using Xunit;

namespace GK3Reborn.Tests.Content;

/// <summary>
/// Tests that a language's pack carries the port's own words.
/// </summary>
/// <remarks>
/// <para>
/// The assembly carries them too, and that is what a player with no packs reads. The pack's
/// copy exists so that a translation can be corrected — or one written for a language the
/// port carries nothing for — without rebuilding the game, which is the same bargain every
/// other layer here strikes.
/// </para>
/// <para>
/// It is worth a test rather than an eye because the failure is silent in the one direction
/// that matters: a pack built without the file is a pack that works, reads correctly, and
/// gives back the assembly's words instead of its own.
/// </para>
/// </remarks>
public sealed class InterfacePackTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "gk3r-interface-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void A_language_that_ships_gets_its_words_in_its_own_volume()
    {
        // The plan is discovered from the workspace, exactly as a real build's is: a
        // language is in it because somebody extracted a release for it.
        string workspace = Path.Combine(_root, "ContentWorkspace");
        string german = Path.Combine(workspace, "enhanced", "localized", "DE", "localized");

        Directory.CreateDirectory(german);
        File.WriteAllBytes(Path.Combine(german, "27KASHAF.BMP"), [1, 2, 3, 4]);

        // The manifest extract-localized writes, without which a pack is not taken for a
        // language pack at all — a file name is a weak claim.
        string manifests = Path.Combine(
            workspace, "enhanced", "localized", "DE", "manifests");

        Directory.CreateDirectory(manifests);
        File.WriteAllText(
            Path.Combine(manifests, LocalizedContent.ManifestName + ".json"),
            """{"language":"de","prefix":"G","name":"German","assets":1}""");

        IReadOnlyList<PackKind> plan = ContentPackStage.LanguagePlan(workspace);

        Assert.Contains(plan, kind => kind.Source == "build/interface/DE");

        string output = Path.Combine(_root, "packs");

        Assert.True(new ContentPackStage(_ => { }).Run(
            workspace, output, plan, texconv: "unused", useSizePlan: false));

        using LocalizedContent? pack = LocalizedContent.Open(output, GameLanguage.Of("de"));

        Assert.NotNull(pack);

        byte[]? words = pack!.ReadManifest(UiText.FileName);

        Assert.NotNull(words);

        Dictionary<string, string>? read =
            JsonSerializer.Deserialize<Dictionary<string, string>>(words!);

        Assert.NotNull(read);
        Assert.Equal("Einstellungen", read!["menu.options"]);

        // And it is what the game would actually read, through the door the game uses.
        Assert.Equal(
            "Einstellungen",
            UiText.Of(GameLanguage.Of("de"), pack).Say("menu.options", "Settings"));
    }

    [Fact]
    public void A_pack_may_carry_words_the_assembly_does_not_have()
    {
        // Which is the whole reason the pack carries a copy at all: Russian has a pack, a
        // prefix and a code page, and nobody has written its interface. Somebody can.
        var builder = new RebarnBuilder();

        builder.AddBytes(
            RebarnKind.Manifest,
            UiText.FileName,
            System.Text.Encoding.UTF8.GetBytes(
                """{"menu.options":"Настройки","menu.quit":"Выход"}"""));

        builder.AddBytes(
            RebarnKind.Manifest,
            LocalizedContent.ManifestName + ".json",
            System.Text.Encoding.UTF8.GetBytes("""{"language":"ru"}"""));

        Directory.CreateDirectory(_root);
        builder.Write(Path.Combine(_root, "Reborn_RU.rebarn"));

        using LocalizedContent? pack = LocalizedContent.Open(_root, GameLanguage.Of("ru"));

        Assert.NotNull(pack);

        UiText words = UiText.Of(GameLanguage.Of("ru"), pack);

        Assert.Equal("Настройки", words.Say("menu.options", "Settings"));

        // Everything it does not carry falls back to the English the call site holds,
        // which is what makes a partial translation a usable one.
        Assert.Equal("Picture", words.Say("settings.section.video", "Picture"));
    }
}
