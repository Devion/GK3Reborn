using GK3Reborn.Game;
using GK3Reborn.Game.Sidney;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Tests for Sidney in a language other than English.
/// </summary>
/// <remarks>
/// <para>
/// <b>Sidney was the most translated screen in the game and the least translated screen in
/// the port.</b> <c>ESIDNEY.TXT</c> is re-cut for every localisation and carries every one
/// of its buttons, and the port wrote them out in English beside the paragraphs it did read
/// — so a German game drew a German analysis under <c>START ANALYSIS</c>.
/// </para>
/// <para>
/// The fixture here is the German release's own text, cut down: the same keys, the German
/// values. That is what makes these tests worth writing rather than tautological — the keys
/// are what the engine asks for and the values are what a player sees, and the whole class
/// of fault this covers is an engine that confuses the two.
/// </para>
/// </remarks>
public sealed class SidneyLanguageTests
{
    /// <summary>The German release's own strings, under the keys every release shares.</summary>
    private const string German = """
        [Main Screen]
        MenuName    = SIDNEY
        MenuItem1   = SUCHE
        MenuItem2   = ANALYSE
        MenuItem3   = ÜBERSETZUNG
        MenuItem4   = AUSWEIS-ERSTELLUNG
        MenuItem5   = VERDÄCHTIGE
        MenuItem6   = DATEN HINZUFÜGEN
        MenuItem7   = EMAIL
        MenuItem8   = ^
        MenuItem9   = VERLASSEN
        ImageDir    = Bilder
        FingerDir   = Fingerabdrücke
        AudioDir    = Audio
        TextDir     = Text
        LicenseDir  = Nummernschilder

        [Search Screen]
        ScreenName  = SUCHE
        Search      = SUCHE

        [Analyze Screen]
        ScreenName    = ANALYSE
        Menu1Name     = ÖFFNEN
        MenuItem1     = DATEI ÖFFNEN
        MenuItem2     = ANALYSE STARTEN
        Menu2Name     = TEXT
        Menu2Item1    = ANOMALIEN ISOLIEREN
        Menu2Item2    = ÜBERSETZUNG
        Menu2Item4    = TEXT ANALYSIEREN
        Menu3Name     = GRAFIK
        Menu3Item1    = GEOMETRISCHE FORMEN BETRACHTEN
        Menu3Item2    = SYMBOL DREHEN
        Menu4Name     = KARTE
        Menu4Item1    = PUNKTE EINGEBEN
        Menu4Item2    = PUNKTE LÖSCHEN
        Menu4Item4    = GITTER ZEICHNEN
        Menu4Item5    = GITTER LÖSCHEN
        ShapeCircle   = Kreis
        ShapeSquare   = Quadrat
        OKButton      = OK
        YesButton     = JA
        NoButton      = NEIN
        AnalyzeParch1 = 1. Der Text scheint Unregelmäßigkeiten aufzuweisen.
        ExtractParch1 = Die Buchstaben lauten:\nadagobertiiroietasionestcetresoretilestlamort.
        Parch1French  = a dagobert ii roi et a sion est ce tresor et il est la mort.
        ParchEnglish  = Textumbrüche können nicht entziffert werden, wenn der Text deutsch ist.
        ParchLatin    = Textumbrüche können nicht entziffert werden, wenn der Text lateinisch ist.
        Languages     = Sprachen:
        French        = FRANZÖSISCH
        English       = DEUTSCH
        Latin         = LATEIN

        [Suspects Screen]
        ScreenName  = VERDÄCHTIGE
        Menu3Item4  = ANALYSE DER ÜBEREINSTIMMUNG
        VehicleID4  = Unbekannt

        [AddData Screen]
        ScreenName  = DATEN HINZUFÜGEN
        FileList    = Datei-Liste

        [EMail Screen]
        ScreenName  = EMAIL
        NewEMail    = NEUE EMAIL
        From        = Von:
        To          = An:
        CC          = CC:

        [Translate Screen]
        ScreenName  = ÜBERSETZUNG
        MenuItem1   = DATEI ÖFFNEN
        English     = Deutsch
        Latin       = Latein
        French      = Französisch
        Italian     = Italienisch
        From        = Von:
        AbbeTape1   = Arnaud hier.
        AbbeTapeT1  = Arnaud hier.
        WrongFrom   = Falsche Ausgangssprache.
        """;

    /// <summary>The German string table, cut down to what Sidney reads out of it.</summary>
    private const string Table = """
        Day110a = Tag 1, 10.00 - 12.00 Uhr

        [ToolTips]
        v_parchment_1 = Pergament Nr. 1
        v_abbe_tape   = Aufnahme von Arnauds Telefonat
        """;

    private static SidneyMachine Machine(out GameState state)
    {
        state = new GameState { Ego = "GRACE" };

        return new SidneyMachine(SidneyLibrary.From(German), state)
        {
            Names = GameStrings.Parse(Table),
            Language = "de",
        };
    }

    [Fact]
    public void Every_button_on_the_analyze_screen_is_the_games_own_word_for_it()
    {
        SidneyWords words = Machine(out _).Words;

        Assert.Equal("ANALYSE STARTEN", words.Action(SidneyAction.Analyse));
        Assert.Equal("ANOMALIEN ISOLIEREN", words.Action(SidneyAction.ExtractAnomalies));
        Assert.Equal("TEXT ANALYSIEREN", words.Action(SidneyAction.AnalyseText));
        Assert.Equal("GEOMETRISCHE FORMEN BETRACHTEN", words.Action(SidneyAction.ViewGeometry));
        Assert.Equal("SYMBOL DREHEN", words.Action(SidneyAction.RotateShape));
        Assert.Equal("PUNKTE EINGEBEN", words.Action(SidneyAction.EnterPoints));
        Assert.Equal("PUNKTE LÖSCHEN", words.Action(SidneyAction.ClearPoints));
        Assert.Equal("GITTER ZEICHNEN", words.Action(SidneyAction.DrawGrid));
        Assert.Equal("GITTER LÖSCHEN", words.Action(SidneyAction.EraseGrid));
    }

    [Fact]
    public void The_one_operation_the_original_does_not_have_is_translated_here()
    {
        // UNDO POINT is the port's own: the original offers ENTER POINTS and CLEAR POINTS
        // and nothing between them. There is no key to read, so there is a table.
        Assert.Equal("PUNKT ZURÜCK", Machine(out _).Words.Action(SidneyAction.UndoPoint));
    }

    [Fact]
    public void The_rest_of_the_screens_take_their_words_from_the_same_file()
    {
        SidneyWords words = Machine(out _).Words;

        Assert.Equal("SUCHE", words.Search);
        Assert.Equal("ANALYSE DER ÜBEREINSTIMMUNG", words.Match);
        Assert.Equal("DATEI-LISTE", words.Files);
        Assert.Equal("NEUE EMAIL", words.NewMail);
        Assert.Equal("Von:", words.MailFrom);
        Assert.Equal("An:", words.MailTo);
        Assert.Equal("Unbekannt", words.Unknown);
        Assert.Equal("DATEI ÖFFNEN", words.OpenFile);
    }

    [Fact]
    public void A_file_is_filed_under_the_originals_own_directory_names()
    {
        // The port used to write "parchment", "fingerprint" and "licence plate" here, which
        // is a taxonomy nobody translated because nobody but the port has ever had one. The
        // original sorts its files into six directories and every release names them.
        SidneyWords words = Machine(out _).Words;

        Assert.Equal("Bilder", words.Kind(SidneyKind.Parchment1));
        Assert.Equal("Fingerabdrücke", words.Kind(SidneyKind.KnownPrint));
        Assert.Equal("Fingerabdrücke", words.Kind(SidneyKind.UnknownPrint));
        Assert.Equal("Audio", words.Kind(SidneyKind.Tape));
        Assert.Equal("Nummernschilder", words.Kind(SidneyKind.Licence));
    }

    [Fact]
    public void A_figure_is_named_the_way_the_file_names_it_and_saved_the_way_it_was()
    {
        SidneyWords words = Machine(out _).Words;

        Assert.Equal("Kreis", words.Shape(MapShape.Circle));
        Assert.Equal("Quadrat", words.Shape(MapShape.Square));

        // What a save writes and what a click answers to must not move with the language.
        Assert.Equal("Circle", SidneyMap.NameOf(MapShape.Circle));
    }

    [Fact]
    public void The_right_answer_to_the_parchment_is_the_key_and_not_the_word()
    {
        // The whole reason a choice carries a key. Every release relabels these three: the
        // German one offers FRANZÖSISCH, DEUTSCH and LATEIN, so an engine matching the word
        // FRENCH would answer "cannot decipher" to the right answer for ever — and the
        // Dagobert line is a step the story cannot go round.
        SidneyMachine sidney = Machine(out _);

        sidney.Scan("PARCHMENT_1");
        sidney.OpenFile(sidney.Files[0]);

        SidneyResult asked = sidney.Perform(SidneyAction.ExtractAnomalies);

        Assert.Equal("Sprachen:", asked.Asks);
        Assert.Equal(
            ["FRANZÖSISCH", "DEUTSCH", "LATEIN"],
            asked.Choices?.Select(choice => choice.Text));

        Assert.Equal(["French", "English", "Latin"], asked.Choices?.Select(choice => choice.Key));

        Assert.StartsWith("a dagobert", sidney.Answer("French").Text, StringComparison.Ordinal);
        Assert.StartsWith("Textumbrüche", sidney.Answer("English").Text, StringComparison.Ordinal);
        Assert.StartsWith("Textumbrüche", sidney.Answer("Latin").Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Reading_the_parchment_in_French_still_sets_the_flag_the_story_reads()
    {
        SidneyMachine sidney = Machine(out GameState state);

        sidney.Scan("PARCHMENT_1");
        sidney.OpenFile(sidney.Files[0]);
        sidney.Perform(SidneyAction.ExtractAnomalies);
        sidney.Answer("French");

        Assert.True(sidney.HasDone(sidney.Files[0], SidneyAction.Translate));
        Assert.False(state.GetFlag("nothing"));
    }

    [Fact]
    public void The_translate_screen_offers_its_languages_under_their_keys()
    {
        SidneyMachine sidney = Machine(out _);

        Assert.Equal(
            ["Deutsch", "Latein", "Französisch", "Italienisch"],
            sidney.Translator.Languages.Select(choice => choice.Text));

        Assert.Equal(
            ["English", "Latin", "French", "Italian"],
            sidney.Translator.Languages.Select(choice => choice.Key));

        sidney.Scan("ABBE_TAPE");
        sidney.OpenForTranslation(sidney.Files[0]);

        sidney.From = "Latin";
        Assert.Equal("Falsche Ausgangssprache.", sidney.Translate().Text);

        sidney.From = "French";
        Assert.Equal("Arnaud hier.", sidney.Translate().Text);
    }

    [Fact]
    public void A_scanned_file_is_called_what_the_bag_calls_it()
    {
        // The 293 tooltips are the one family of per-object text GK3 localised, and they
        // are what the inventory already draws. Sidney used to write the noun out with its
        // underscores taken away: "Parchment 1" in a German game, next to "Pergament Nr. 1"
        // in the bag.
        SidneyMachine sidney = Machine(out _);

        sidney.Scan("PARCHMENT_1");

        Assert.Equal("Pergament Nr. 1", sidney.Files[0].Label);
        Assert.Equal("Aufnahme von Arnauds Telefonat", sidney.NameOf("ABBE_TAPE"));

        // And the tidied identifier where the table has no name for it, which is what an
        // installation with no string table gets for everything.
        Assert.Equal("Unknown Print 1", sidney.NameOf("UNKNOWN_PRINT_1"));
    }

    [Fact]
    public void The_clock_says_what_the_string_table_calls_the_timeblock()
    {
        SidneyMachine sidney = Machine(out GameState state);

        state.Timeblock = new Timeblock(1, 10, IsAfternoon: false);

        Assert.Equal("Tag 1, 10.00 - 12.00 Uhr", sidney.Now);
    }

    [Fact]
    public void A_language_with_no_translation_of_its_own_reads_English()
    {
        // The rule the rest of the port follows: what a player loses by not having a
        // translation is that translation, not the screen. Russian has a pack and a code
        // page and no column here.
        var words = new SidneyWords(SidneyLibrary.Empty, "ru");

        Assert.Equal("No messages.", words.Own("NoMessages"));
        Assert.Equal("UNDO POINT", words.Action(SidneyAction.UndoPoint));
    }

    [Fact]
    public void Every_phrase_the_port_wrote_itself_exists_in_every_language_it_claims()
    {
        // One row a phrase, one column a language. A short row is a phrase that silently
        // falls back to English in one language and not in the others, which is the kind of
        // hole nobody sees until they are playing in that language.
        string[] codes = ["en", "de", "es", "fr", "it", "pt"];
        string[] phrases =
        [
            "NotOn", "NothingToShow", "NothingScanned", "NoMessages", "PickMessage",
            "AllScanned", "NothingToScan", "TypeSubject", "PickSuspect", "NoFiles",
            "NoFigures", "LinkWord", "UndoPoint", "ShapeLine", "AssistSays", "AssistAsks",
        ];

        var english = new SidneyWords(SidneyLibrary.Empty, "en");

        foreach (string code in codes)
        {
            var words = new SidneyWords(SidneyLibrary.Empty, code);

            foreach (string phrase in phrases)
            {
                string said = words.Own(phrase);

                Assert.False(said.Length == 0, $"{phrase} is empty in {code}");
                Assert.False(said == phrase, $"{phrase} has no row at all");

                // A row as long as the header with English in one of its columns is the
                // same hole with the count hiding it.
                if (code != "en")
                {
                    Assert.False(said == english.Own(phrase), $"{phrase} is English in {code}");
                }
            }
        }
    }

    [Fact]
    public void The_sentence_that_names_a_screen_names_the_translated_one()
    {
        // "Use ADD DATA to put something in" is a direction, and the place it directs to is
        // called something else in every release.
        var words = new SidneyWords(SidneyLibrary.From(German), "de");

        Assert.Contains("{0}", words.Own("NothingScanned"), StringComparison.Ordinal);

        Assert.Equal(
            "DATEN HINZUFÜGEN",
            SidneyLibrary.From(German).Say("ScreenName", "AddData Screen"));
    }
}
