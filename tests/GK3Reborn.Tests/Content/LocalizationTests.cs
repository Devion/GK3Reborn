using GK3Reborn.Content;
using GK3Reborn.Content.Manifests;
using GK3Reborn.Formats.Rebarn;
using GK3Reborn.Foundation;
using GK3Reborn.Game;
using GK3Reborn.Tools.Stages;
using Xunit;

namespace GK3Reborn.Tests.Content;

/// <summary>
/// Tests for the language table, the pack that carries a language, and the rule that
/// works out which assets belong in one.
/// </summary>
/// <remarks>
/// The expensive half of localisation is a comparison of two three-hundred-megabyte
/// releases and cannot be a test. What can be tested is every rule that comparison turns
/// on, and those are the ones that are silently wrong when they are wrong: a prefix
/// stripped from a name that never carried one collides five assets into each other, and a
/// prefix <em>not</em> stripped leaves seven thousand French lip-sync files with no English
/// counterpart and the English pack empty.
/// </remarks>
public sealed class LocalizationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "gk3r-loc-" + Guid.NewGuid().ToString("N"));

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Every_language_GK3_was_published_in_is_known_by_its_code()
    {
        // The eight official localisations, from GK3.ini's own list, plus Simplified
        // Chinese, which somebody else translated. A code this build has never heard of is
        // English rather than a failure to start, for the same reason every other setting
        // is clamped.
        Assert.Equal(9, GameLanguage.Known.Count);

        foreach (string code in new[] { "en", "fr", "de", "it", "es", "pt", "ru", "pl", "zh" })
        {
            Assert.NotNull(GameLanguage.Find(code));
        }

        Assert.Null(GameLanguage.Find("xx"));
        Assert.Null(GameLanguage.Find(null));
        Assert.Equal(GameLanguage.Default, GameLanguage.Of("xx"));
        Assert.Equal(GameLanguage.Default, GameLanguage.Of(null));

        // Case and stray whitespace are somebody editing the settings file by hand.
        Assert.Equal("fr", GameLanguage.Of("FR").Code);
        Assert.Equal("fr", GameLanguage.Of(" fr ").Code);

        // And a release directory is called whatever the person who unpacked it typed.
        // Refusing ESP and insisting on es would be a rule nobody can see written down,
        // enforced by silence: the language simply would not appear.
        Assert.Equal("es", GameLanguage.Of("ESP").Code);
        Assert.Equal("es", GameLanguage.Of("Spanish").Code);
        Assert.Equal("de", GameLanguage.Of("GER").Code);
        Assert.Equal("de", GameLanguage.Of("Deutsch").Code);
        Assert.Equal("pt", GameLanguage.Of("pt-BR").Code);

        // Every alias belongs to exactly one language, or one of them is unreachable.
        List<string> all = [.. GameLanguage.Known.SelectMany(
            l => l.Aliases.Append(l.Code).Append(l.Name))];

        Assert.Equal(all.Count, all.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void The_prefix_is_not_derivable_from_the_code_and_is_not_unique()
    {
        // Sierra renamed the spoken assets for four localisations and left the other three
        // with English's spellings, so three languages answer to E and are told apart by
        // what is inside the file. A build that assumed one letter per language would read
        // Portuguese out of the English pack and never say so.
        Assert.Equal('F', GameLanguage.Of("fr").Prefix);
        Assert.Equal('G', GameLanguage.Of("de").Prefix);
        Assert.Equal('I', GameLanguage.Of("it").Prefix);
        Assert.Equal('S', GameLanguage.Of("es").Prefix);

        Assert.Equal('E', GameLanguage.Of("pt").Prefix);
        Assert.Equal('E', GameLanguage.Of("ru").Prefix);
        Assert.Equal('E', GameLanguage.Of("pl").Prefix);

        Assert.Equal("FSTRINGS.TXT", GameLanguage.Of("fr").StringTable);
        Assert.Equal("ESTRINGS.TXT", GameLanguage.Of("pt").StringTable);
    }

    [Fact]
    public void Text_is_decoded_in_the_language_that_was_authored_in()
    {
        // Windows-1252 and Latin-1 differ in exactly one place that matters, and it is the
        // place French uses most: the curly apostrophe is 0x92, which Latin-1 leaves as a
        // control character. "L'Empereur" then arrives with a hole in it.
        byte[] french = [0x4C, 0x92, 0x45, 0x6D, 0x70, 0x65, 0x72, 0x65, 0x75, 0x72];

        Assert.Equal("L’Empereur", Gk3Encoding.GetString(french, 1252));
        Assert.Equal(1252, GameLanguage.Of("fr").CodePage);

        // Polish and Russian are not Western European and never were.
        Assert.Equal(1250, GameLanguage.Of("pl").CodePage);
        Assert.Equal(1251, GameLanguage.Of("ru").CodePage);

        Assert.Equal("Łódź", Gk3Encoding.GetString([0xA3, 0xF3, 0x64, 0x9F], 1250));
        Assert.Equal("Москва", Gk3Encoding.GetString([0xCC, 0xEE, 0xF1, 0xEA, 0xE2, 0xE0], 1251));

        // And a page that is not one byte a character goes to the platform, because GBK is
        // twenty-two thousand mappings in which a byte above 0x80 begins a pair. Reading it
        // a byte at a time is not a visible failure: the text decodes, into the wrong
        // characters, silently.
        Assert.Equal(936, GameLanguage.Of("zh").CodePage);
        Assert.Equal("中文", Gk3Encoding.GetString([0xD6, 0xD0, 0xCE, 0xC4], 936));
        Assert.Equal(new byte[] { 0xD6, 0xD0, 0xCE, 0xC4 }, Gk3Encoding.GetBytes("中文", 936));

        // A page nobody has is the wrong text in a game that started, rather than an
        // exception at the first line of dialogue.
        Assert.Equal("A", Gk3Encoding.GetString([0x41], 999999));

        // Round trip, because an extracted asset put back into overrides/ has to be a file
        // the 1999 game would read.
        Assert.Equal(french, Gk3Encoding.GetBytes("L’Empereur", 1252));
    }

    [Fact]
    public void A_localised_entry_keeps_its_whole_name()
    {
        // The one thing that distinguishes this kind from every other. A pack key drops the
        // extension, which is right for a texture called R25WALLS and catastrophic here:
        // A014ED3S.6J1 and .6J2 are different lines, FSTRINGS.TXT and ESTRINGS.TXT are
        // different languages, and 27KASHAF.BMP must not answer for 27KASHAF.MOD.
        Assert.NotEqual(
            RebarnFormat.Key(RebarnKind.Localized, "A014ED3S.6J1"),
            RebarnFormat.Key(RebarnKind.Localized, "A014ED3S.6J2"));

        Assert.NotEqual(
            RebarnFormat.Key(RebarnKind.Localized, "FSTRINGS.TXT"),
            RebarnFormat.Key(RebarnKind.Localized, "ESTRINGS.TXT"));

        // And it is still a key: case and directory are not part of a name.
        Assert.Equal(
            RebarnFormat.Key(RebarnKind.Localized, "localized/27kashaf.bmp"),
            RebarnFormat.Key(RebarnKind.Localized, "27KASHAF.BMP"));

        // A movie's soundtrack is addressed the way a movie is, without one.
        Assert.Equal(
            RebarnFormat.Key(RebarnKind.MovieAudio, "INTRO.m4a"),
            RebarnFormat.Key(RebarnKind.MovieAudio, "intro"));
    }

    [Fact]
    public void Only_the_families_that_carry_a_letter_have_it_taken_off()
    {
        GameLanguage french = GameLanguage.Of("fr");
        GameLanguage english = GameLanguage.Default;

        // A line of dialogue's lip-sync and a scripted moment are one asset under two
        // names, and this is what makes the English pack hold E where the French holds F.
        Assert.Equal(
            LocalizationExtractStage.Canonical("F014ED3S6J1.YAK", french),
            LocalizationExtractStage.Canonical("E014ED3S6J1.YAK", english));

        Assert.Equal(
            "ECOFFEEPOT.MOM",
            LocalizationExtractStage.Spell(
                LocalizationExtractStage.Canonical("FCOFFEEPOT.MOM", french), english));

        Assert.Equal(
            "FSTRINGS.TXT",
            LocalizationExtractStage.Spell(
                LocalizationExtractStage.Canonical("ESTRINGS.TXT", english), french));

        // And this is what stops five different cutscenes becoming one. GK3's cutscene
        // lip-sync files are named for the scene, not for a line, so their first character
        // is not a language's letter and must survive.
        Assert.Equal(
            "205PEND.YAK", LocalizationExtractStage.Canonical("205PEND.YAK", french));

        // ESIDNEY.TXT keeps its E in the French release and changes its contents instead,
        // so .TXT as a whole is not a prefixed family. Stripping it would have French
        // looking for FSIDNEY.TXT, which does not exist in any release.
        Assert.Equal(
            "ESIDNEY.TXT", LocalizationExtractStage.Canonical("ESIDNEY.TXT", english));

        // A name that happens to start with another language's letter is left alone: the
        // letter is only a prefix when it is this language's.
        Assert.Equal(
            "F014ED3S6J1.YAK", LocalizationExtractStage.Canonical("F014ED3S6J1.YAK", english));
    }

    [Fact]
    public void A_pack_has_to_declare_itself_to_be_read_as_a_language()
    {
        // The file name is a weak claim. Reborn_HD.rebarn matches the pattern and is
        // somebody's texture mod; a pack that says which language it is for is one that
        // meant to.
        string packs = Path.Combine(_root, "packs");
        Directory.CreateDirectory(packs);

        Write(Path.Combine(packs, "Reborn_FR.rebarn"), GameLanguage.Of("fr"),
            ("FSTRINGS.TXT", "loc_lby = Hall de l'hotel"));

        // The same content under a name that claims a language the manifest does not.
        Write(Path.Combine(packs, "Reborn_DE.rebarn"), GameLanguage.Of("fr"),
            ("FSTRINGS.TXT", "loc_lby = Hall de l'hotel"));

        Assert.Equal(
            ["en", "fr"],
            LocalizedContent.Available(packs).Select(l => l.Code));

        using LocalizedContent? german = LocalizedContent.Open(packs, GameLanguage.Of("de"));
        Assert.Null(german);

        using LocalizedContent? french = LocalizedContent.Open(packs, GameLanguage.Of("fr"));
        Assert.NotNull(french);
        Assert.Equal("fr", french.Language.Code);
        Assert.True(french.HasArchive("FSTRINGS.TXT"));
        Assert.False(french.HasArchive("ESTRINGS.TXT"));
    }

    [Fact]
    public void A_language_pack_is_not_part_of_the_shared_content()
    {
        // The game opens exactly one language — the one the player chose — and a shipped
        // install may carry several. Merging every one it happens to have installed into
        // the shared namespace would put the last one alphabetically in front of the
        // archives for everybody, in a language nobody asked for.
        string packs = Path.Combine(_root, "mixed");
        Directory.CreateDirectory(packs);

        Write(Path.Combine(packs, "Reborn_FR.rebarn"), GameLanguage.Of("fr"),
            ("FSTRINGS.TXT", "français"));

        using RebarnContent shared = RebarnContent.Open(packs);

        Assert.Equal(0, shared.VolumeCount);
        Assert.False(shared.Has(RebarnKind.Localized, "FSTRINGS.TXT"));
    }

    [Fact]
    public void The_language_is_read_in_front_of_the_archives_and_behind_the_overrides()
    {
        // The whole arrangement in one assertion. An installation answers for everything
        // the language does not hold, which is what makes a partial pack harmless; and a
        // file a player put in overrides/ is theirs whatever language the game is in.
        string packs = Path.Combine(_root, "layers");
        Directory.CreateDirectory(packs);

        Write(Path.Combine(packs, "Reborn_FR.rebarn"), GameLanguage.Of("fr"),
            ("FSTRINGS.TXT", "loc_lby = Hall de l'hotel"),
            ("SHARED.TXT", "française"));

        using LocalizedContent? french = LocalizedContent.Open(packs, GameLanguage.Of("fr"));
        Assert.NotNull(french);

        Assert.Equal("française", System.Text.Encoding.UTF8.GetString(french.Read("SHARED.TXT")!));
        Assert.Null(french.Read("ONLY_THE_GAME_HAS_THIS.SIF"));
    }

    [Fact]
    public void The_string_table_a_language_reads_is_the_one_it_spells()
    {
        // ESTRINGS.TXT in English and Portuguese, FSTRINGS.TXT in French. Reading the
        // English one under a French game is not a crash and not a blank screen: it is
        // every place in the game showing its three-letter code, which reads as an
        // unfinished port rather than as a file that was not found.
        var table = GameStrings.Parse("loc_lby = Hall de l'hôtel\nDay110a = Jour 1\n");

        Assert.Equal("Hall de l'hôtel", table.Place("lby"));
        Assert.Equal("Jour 1", table.When("110a"));
        Assert.Equal("ESTRINGS.TXT", GameStrings.None.File);
    }

    [Fact]
    public void The_language_the_plan_packs_is_whichever_one_is_in_the_workspace()
    {
        // Which languages a build ships is a fact about what extract-localized has been run
        // over, and it changes when somebody sources another release. Writing them into the
        // default plan would make adding German a code change.
        string workspace = Path.Combine(_root, "ws");
        Directory.CreateDirectory(Path.Combine(workspace, "enhanced", "localized", "FR", "localized"));
        Directory.CreateDirectory(Path.Combine(workspace, "enhanced", "localtextures", "FR"));

        IReadOnlyList<PackKind> plan = ContentPackStage.LanguagePlan(workspace);

        Assert.All(plan, k => Assert.Equal("Reborn_FR", k.Volume));
        Assert.Contains(plan, k => k.Kind == RebarnKind.Localized);
        Assert.Contains(plan, k => k.Kind == RebarnKind.MovieAudio);
        Assert.Contains(plan, k => k.Kind == RebarnKind.Video);
        Assert.Contains(plan, k => k.Kind == RebarnKind.Manifest);

        // The repainted textures need a cache of their own, because their names are the
        // shared set's names: without it the French sign and the English one are the same
        // file in build/rebarn/textures and which reaches which pack is down to which was
        // encoded second.
        PackKind textures = Assert.Single(plan, k => k.Kind == RebarnKind.Texture);
        Assert.Equal("localtextures/FR", textures.Cache);

        Assert.Empty(ContentPackStage.LanguagePlan(Path.Combine(_root, "empty-ws")));
    }

    [Fact]
    public void A_language_that_has_nothing_is_not_offered()
    {
        // A row in the menu that does nothing when chosen is worse than no row, so the
        // listing opens each pack rather than believing its file name — and a directory
        // with no packs at all still offers English, because that is what every
        // installation can already read.
        string empty = Path.Combine(_root, "none");
        Directory.CreateDirectory(empty);

        Assert.Equal(["en"], LocalizedContent.Available(empty).Select(l => l.Code));
        Assert.Equal(["en"], LocalizedContent.Available(Path.Combine(_root, "missing")).Select(l => l.Code));

        Assert.Equal("Reborn_FR.rebarn", LocalizedContent.FileNameOf(GameLanguage.Of("fr")));

        // The pattern only says what a directory looks like it holds. Whether a file *is* a
        // language pack is decided by its manifest, which is why Reborn_HD.rebarn matching
        // here is not a problem.
        Assert.Matches(LocalizedContent.FileNamePattern(), "Reborn_FR.rebarn");
        Assert.Matches(LocalizedContent.FileNamePattern(), "reborn_de.rebarn");
        Assert.DoesNotMatch(LocalizedContent.FileNamePattern(), "Reborn.rebarn");
        Assert.DoesNotMatch(LocalizedContent.FileNamePattern(), "RebornMaterials.rebarn");
    }

    [Fact]
    public void The_players_things_are_called_what_the_game_calls_them()
    {
        // 293 of these, under v_black_marker in the tooltips section, and the port had
        // never read one: it drew the identifier with its underscores taken out, so the
        // game's "Tape of Abbé's phone call" came out as "Abbe Tape".
        //
        // They are also the *only* per-object text GK3 localised — there is no table of
        // noun or verb names anywhere in the data — so these are the whole of what a French
        // game can say about the player's pockets in French.
        var table = GameStrings.Parse("""
            [ToolTips]
            v_abbe_tape   = Enregistrement de l'appel téléphonique de l'abbé
            v_binoculars  = Jumelles
            V_BLOODLINE_MANUSCRIPT = Manuscrit de Larry
            """);

        Assert.Equal("Jumelles", table.Item("BINOCULARS"));
        Assert.Equal("Manuscrit de Larry", table.Item("bloodline_manuscript"));
        Assert.Equal(
            "Enregistrement de l'appel téléphonique de l'abbé", table.Item("ABBE_TAPE"));

        // A thing the table has no name for falls back to the tidied identifier, which is
        // what the interface drew before this existed.
        Assert.Null(table.Item("SOMETHING_NOBODY_NAMED"));
        Assert.Null(table.Item(null));
        Assert.Null(GameStrings.None.Item("BINOCULARS"));
    }

    [Fact]
    public void The_language_row_steps_through_what_is_installed_and_nothing_else()
    {
        // Not through every language GK3 was published in: a player with English and French
        // should step between two rows rather than through eight, six of which name a pack
        // they do not have and do nothing when chosen.
        var front = new GK3Reborn.UI.FrontEnd(new Settings())
        {
            Languages = [GameLanguage.Default, GameLanguage.Of("fr")],
        };

        front.Show(GK3Reborn.UI.FrontEndPage.Gameplay);

        GK3Reborn.UI.MenuItem row = Assert.Single(front.Items, i => i.Id == "language");

        Assert.Equal("English", row.Value);

        front.Choose(new GK3Reborn.UI.MenuAction("language", 1));
        Assert.Equal("fr", front.Settings.Language);

        // In itself, and in English beside it: somebody looking for French is looking for
        // "Français", and somebody who has landed on a language they cannot read needs
        // "French" to find their way back out of it.
        front.Show(GK3Reborn.UI.FrontEndPage.Gameplay);
        Assert.Equal(
            "Français (French)",
            Assert.Single(front.Items, i => i.Id == "language").Value);

        // And round, rather than stopping at the end, like every other list here.
        front.Choose(new GK3Reborn.UI.MenuAction("language", 1));
        Assert.Equal("en", front.Settings.Language);

        // A list of one steps to itself rather than dividing by zero.
        var alone = new GK3Reborn.UI.FrontEnd(new Settings { Language = "en" });
        alone.Show(GK3Reborn.UI.FrontEndPage.Gameplay);
        alone.Choose(new GK3Reborn.UI.MenuAction("language", 1));
        Assert.Equal("en", alone.Settings.Language);
    }

    [Fact]
    public void A_language_nobody_has_heard_of_is_English_rather_than_a_refusal_to_start()
    {
        // A settings file is a text file somebody may edit, and every other value here is
        // clamped rather than rejected for the same reason.
        Assert.Equal("en", new Settings { Language = "klingon" }.Sane().Language);

        // Normalised rather than merely checked: two files that say the same thing should
        // compare equal.
        Assert.Equal("fr", new Settings { Language = "FR" }.Sane().Language);

        // A language this build knows but has no pack for is kept. The player may be about
        // to install one, and quietly rewriting their choice would make that look as though
        // it had not worked.
        Assert.Equal("de", new Settings { Language = "de" }.Sane().Language);
    }

    [Fact]
    public void Nothing_writes_into_the_hand_curated_texture_directory()
    {
        // enhanced/localtextures/<CODE> is where a person prunes a hundred candidates down
        // to the ones they mean to repaint, and then repaints them. The shape of that
        // decision *is which files are there*, so a run that put back what somebody had
        // deleted would undo an afternoon without touching a byte of anybody's painting.
        //
        // It happened once, on 2026-09-05: a re-derivation copied thirty-four pruned
        // pictures back in and said nothing. The seeding was taken out rather than put
        // behind a flag, because a flag is something a script somebody else wrote passes.
        string stage = File.ReadAllText(Path.Combine(
            Root(), "tools", "GK3Reborn.Tools", "Stages", "LocalizationExtractStage.cs"));

        // Copying is the whole of how the seeding worked, and there is now no reason for
        // this stage to copy a file anywhere.
        Assert.DoesNotContain("File.Copy", stage, StringComparison.Ordinal);

        // And every directory it creates, every file it writes and every file it deletes is
        // named on a line that says where. None of them says localtextures.
        string[] writes = ["Directory.CreateDirectory", "File.WriteAllText",
                           "AtomicFile.WriteAllText", "File.WriteAllBytes", "File.Delete"];

        const char Newline = (char)10;

        foreach (string line in stage.Split(Newline))
        {
            if (writes.Any(w => line.Contains(w, StringComparison.Ordinal)))
            {
                Assert.DoesNotContain("localtextures", line, StringComparison.Ordinal);
            }
        }
    }

    /// <summary>The repository root, from wherever the tests are running.</summary>
    private static string Root()
    {
        var here = new DirectoryInfo(AppContext.BaseDirectory);

        while (here is not null && !File.Exists(Path.Combine(here.FullName, "GK3Reborn.slnx")))
        {
            here = here.Parent;
        }

        Assert.NotNull(here);
        return here.FullName;
    }

    /// <summary>Writes a language pack holding a manifest and some assets.</summary>
    private static void Write(
        string path, GameLanguage declared, params (string Name, string Text)[] assets)
    {
        var builder = new RebarnBuilder();

        builder.AddBytes(
            RebarnKind.Manifest,
            LocalizedContent.ManifestName + ".json",
            System.Text.Encoding.UTF8.GetBytes(
                System.Text.Json.JsonSerializer.Serialize(
                    new LocalizationManifest(
                        declared.Code, declared.Prefix, declared.Name, assets.Length),
                    ManifestJson.Options)));

        foreach ((string name, string text) in assets)
        {
            builder.AddBytes(RebarnKind.Localized, name, System.Text.Encoding.UTF8.GetBytes(text));
        }

        builder.Write(path);
    }
}
