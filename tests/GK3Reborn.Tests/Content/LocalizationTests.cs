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

    [Fact]
    public void A_sign_the_language_repaints_is_not_answered_by_the_shared_enhanced_one()
    {
        // The rule this whole file exists for, at the one place it decides what is on
        // screen. Sierra re-cut every archive per language, so about a hundred surfaces a
        // language -- road signs, shop fronts, the nine panels of the Temple puzzle -- ship
        // as a different picture on a German disc. The enhanced set is shared by every
        // language and its words are English, so where the language repaints a surface and
        // the enhanced set does not, the enhanced picture is the wrong words at a higher
        // resolution. Which is worse than the right words at 1999's.
        string packs = Path.Combine(_root, "packs");
        Directory.CreateDirectory(packs);

        GameLanguage german = GameLanguage.Of("de");

        Write(
            Path.Combine(packs, LocalizedContent.FileNameOf(german)),
            german,
            ("RC2CHRCHSIGN01.BMP", "Kirche"),
            ("PANEL1.BMP", "Tafel"));

        using LocalizedContent? pack = LocalizedContent.Open(packs, german);
        Assert.NotNull(pack);

        // The shared enhanced set has an English picture for it and the language does not
        // repaint it, so the shared layer stands aside and the archive -- which reads the
        // pack in front of the installation -- answers.
        Assert.True(SceneLoader.ShadowsLanguage(pack, "RC2CHRCHSIGN01", ownPicture: false));

        // Unless the layer's answer is this language's own picture, out of
        // enhanced/localtextures/DE or out of Reborn_DE.rebarn. Then it is not the shared
        // one and it wins outright, which is the whole point of repainting it.
        Assert.False(SceneLoader.ShadowsLanguage(pack, "PANEL1", ownPicture: true));

        // A surface the language has no bitmap of its own for is shared by every language,
        // which is nearly all of GK3: nothing changes for those.
        Assert.False(SceneLoader.ShadowsLanguage(pack, "R25WALLS", ownPicture: false));

        // And with no language chosen there is no language to be wrong in.
        Assert.False(SceneLoader.ShadowsLanguage(null, "RC2CHRCHSIGN01", ownPicture: false));
    }

    [Fact]
    public void Each_texture_layer_says_whether_its_answer_is_the_languages_own()
    {
        // "The set has a picture for this sign" and "the picture it has was painted for
        // this language" are the same question for nearly every texture in GK3 and a
        // different one for the hundred that have words on them. The loader has to ask the
        // second, so both layers have to be able to answer it.
        string shared = Path.Combine(_root, "enhanced", "textures");
        string german = Path.Combine(_root, "enhanced", "localtextures", "DE");
        string dropped = Path.Combine(_root, "overrides", "textures");

        Directory.CreateDirectory(shared);
        Directory.CreateDirectory(german);
        Directory.CreateDirectory(dropped);

        // One name in the shared set alone, one repainted for German, one the player put
        // in overrides/ themselves.
        File.WriteAllBytes(Path.Combine(shared, "R25WALLS.png"), Png());
        File.WriteAllBytes(Path.Combine(shared, "PANEL1.png"), Png());
        File.WriteAllBytes(Path.Combine(german, "PANEL1.png"), Png());
        File.WriteAllBytes(Path.Combine(shared, "LERSIGN.png"), Png());
        File.WriteAllBytes(Path.Combine(dropped, "LERSIGN.png"), Png());

        ContentOverrides overrides = ContentOverrides.Open(Path.Combine(_root, "overrides"));

        EnhancedTextures pictures =
            EnhancedTextures.Open(shared, overrides, RebarnKind.Texture, german);

        Assert.True(pictures.Has("R25WALLS"));
        Assert.False(pictures.IsLocalized("R25WALLS"));
        Assert.False(pictures.IsOverridden("R25WALLS"));

        Assert.True(pictures.IsLocalized("PANEL1"));
        Assert.False(pictures.IsOverridden("PANEL1"));

        // The player's own file answers for itself, in whatever language they painted it,
        // and must not be stepped over the way the shared set's is.
        Assert.True(pictures.IsOverridden("LERSIGN"));

        // Names are matched the way every other layer matches them: without an extension
        // and without regard to case.
        Assert.True(pictures.IsLocalized("panel1.BMP"));
    }

    [Fact]
    public void A_pack_that_repeats_the_installation_stops_answering_for_1999()
    {
        // A German pack on a German installation cannot say anything the archives do not
        // already say, and it does not say it for free: the layer sits in front of both the
        // archives and the shared enhanced set. The case that matters is English on an
        // English installation, which is nearly every player -- the English release sourced
        // here is a dumped tree, and a dump has thrown away which archive an entry came
        // from, so its WOODTILE.BMP is a foliage card rather than the hotel lobby floor.
        string packs = Path.Combine(_root, "repeat");
        Directory.CreateDirectory(packs);

        GameLanguage english = GameLanguage.Default;

        Write(
            Path.Combine(packs, LocalizedContent.FileNameOf(english)),
            english,
            (english.StringTable, "the English table"),
            ("WOODTILE.BMP", "a foliage card, not the lobby floor"));

        using LocalizedContent? pack = LocalizedContent.Open(packs, english);
        Assert.NotNull(pack);

        // Before the question is asked it answers, which is what makes a French
        // installation playable in English.
        Assert.True(pack.HasArchive("WOODTILE.BMP"));

        // The installation holds the same string table, so it is the same localisation and
        // the pack is telling it its own language back.
        Assert.True(pack.RepeatsInstallation(
            name => name == english.StringTable
                ? System.Text.Encoding.UTF8.GetBytes("the English table")
                : null));

        Assert.True(pack.ArchivesRepeatInstallation);
        Assert.False(pack.HasArchive("WOODTILE.BMP"));
        Assert.Null(pack.Read("WOODTILE.BMP"));
    }

    [Fact]
    public void A_pack_over_another_languages_installation_answers_in_full()
    {
        // The mirror, and the reason Reborn_EN.rebarn is built at all: a French or German
        // installation played in English. The string tables differ, so the disc that is
        // installed is not the disc the pack came from, and every 1999 asset in it is
        // wanted.
        string packs = Path.Combine(_root, "crossed");
        Directory.CreateDirectory(packs);

        GameLanguage english = GameLanguage.Default;

        Write(
            Path.Combine(packs, LocalizedContent.FileNameOf(english)),
            english,
            (english.StringTable, "the English table"),
            ("R25POEM.BMP", "the poem in English"));

        using LocalizedContent? pack = LocalizedContent.Open(packs, english);
        Assert.NotNull(pack);

        // A French installation: it has no ESTRINGS.TXT of its own to match against.
        Assert.False(pack.RepeatsInstallation(_ => null));
        Assert.False(pack.ArchivesRepeatInstallation);
        Assert.True(pack.HasArchive("R25POEM.BMP"));

        // And one whose copy of that name is a different file -- Portuguese ships
        // ESTRINGS.TXT exactly as English does, and its contents are not English. Reading
        // the letter off the file name could not tell these apart; comparing the bytes can.
        Assert.False(pack.RepeatsInstallation(
            _ => System.Text.Encoding.UTF8.GetBytes("a tabela portuguesa")));

        Assert.False(pack.ArchivesRepeatInstallation);
        Assert.NotNull(pack.Read("R25POEM.BMP"));
    }

    [Fact]
    public void A_language_derives_its_own_material_channels()
    {
        // A normal map is derived from the colour texture it belongs to, so the shared
        // PANEL1 normal has the *English* words embossed in it. Laid under a German PANEL1
        // that reads as English lettering in relief beneath the German, lit from wherever
        // the room is lit from -- which is what was seen in the room before this existed.
        string workspace = Path.Combine(_root, "materials");

        Directory.CreateDirectory(Path.Combine(workspace, "enhanced", "localtextures", "DE"));

        foreach ((_, string channel, _, _) in ContentPackStage.LanguageMaterials)
        {
            Directory.CreateDirectory(
                Path.Combine(workspace, "enhanced", "local" + channel, "DE"));
        }

        IReadOnlyList<PackKind> plan = ContentPackStage.LanguagePlan(workspace);

        Assert.All(plan, k => Assert.Equal("Reborn_DE", k.Volume));

        // Each channel is read from the language's own directory and encoded the way the
        // shared set of that channel is -- they are the same channels of the same surfaces.
        foreach ((RebarnKind kind, string channel, string format, int cap) in
            ContentPackStage.LanguageMaterials)
        {
            PackKind found = Assert.Single(plan, k => k.Kind == kind);

            Assert.Equal($"enhanced/local{channel}/DE", found.Source);
            Assert.Equal(format, found.Format);
            Assert.Equal(cap, found.Cap);

            // And each needs a cache of its own for the same reason the colour does: its
            // name is the shared set's name.
            Assert.Equal($"local{channel}/DE", found.Cache);

            // Linear data, every one of them. An ORM through the sRGB path comes back with
            // every roughness pulled towards one end of its range.
            Assert.False(found.Colour);
        }
    }

    /// <summary>The smallest PNG that decodes: one opaque pixel.</summary>
    private static byte[] Png() =>
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
        0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
        0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4,
        0x89, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x44, 0x41,
        0x54, 0x78, 0x9C, 0x63, 0xF8, 0xCF, 0xC0, 0x00,
        0x00, 0x03, 0x01, 0x01, 0x00, 0x18, 0xDD, 0x8D,
        0xB0, 0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4E,
        0x44, 0xAE, 0x42, 0x60, 0x82,
    ];

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
