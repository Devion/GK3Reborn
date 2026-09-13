using GK3Reborn.Game;
using GK3Reborn.Game.Sidney;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Tests for what Sidney's own work is worth, and for the three screens Gabriel will not use.
/// </summary>
public sealed class SidneyScoreTests
{
    private const string Text = """
        [Main Screen]
        MenuItem1 = SEARCH
        MenuItem2 = SUSPECTS
        MenuItem3 = MAKE I.D.
        MenuItem4 = EXIT

        [Analyze Screen]
        AnalyzePous   = Painting analysed.
        GeometryTenier1 = Analyzing...
        SaveArcadia   = Do you want to save the text to a new file?
        SavingArcadia = Text saved to a new file.
        ArcadiaAnalysis = Data is text only.
        GetVerse      = Image enlarged.  Biblical verse heading.  Retrieve verse?
        RetrieveVerse = Retrieving from Internet...
        Verse         = II Chronicles 3
        YesButton     = YES
        NoButton      = NO
        AnalyzeKPrint = Recognized as a fingerprint.  Use on Suspects Screen.
        AnalyzeLicense = Recognized as a license plate.
        AnalyzeTemp   = Analysis did not find any encoded references in the image.

        [MakeID Screen]
        Menu1Name  = MEDICAL
        Menu1Item1 = DOCTOR
        Menu2Name  = REPORTER
        Menu2Item1 = NEW YORK TIMES
        Menu2Item2 = FREELANCE

        [Suspects Screen]
        Name1         = Madeline Buthane
        Name2         = Vittorio Buchelli
        Name3         = Emilio Baza
        Name4         = Abbe Arnaud
        Name5         = Lady Howard
        Name6         = Estelle Stiles
        Name7         = John Wilkes
        Name8         = Larry Chester
        Name9         = Excelsior Montreaux
        Name10        = Franklin Mosely
        VehicleID1    = Van
        VehicleID2    = VDG945F
        VehicleID3    = HJK841J
        VehicleID4    = Unknown
        VehicleID5    = FKS427G
        VehicleID6    = FKS427G
        VehicleID7    = FED039A
        VehicleID8    = Blue Sedan
        VehicleID9    = Auto?
        VehicleID10   = ASD257K
        MatchCompare  = Comparing with:
        MatchNone     = ** No Match Found **
        MatchFound    = ** Match Found **
        NoSuspect     = You must first open the suspect file.
        NoFingerprint = You must link a fingerprint first.
        AlreadyLinked = This file has already been linked to a suspect.
        ExistingFP    = A fingerprint has already been linked to this suspect.
        """;

    private static SidneyMachine Machine(out GameState state, string ego = "GRACE")
    {
        state = new GameState { Ego = ego };

        return new SidneyMachine(SidneyLibrary.From(Text), state) { Scores = ScoreEvents.Open() };
    }

    /// <summary>Puts a suspect's own evidence on them, the way the screen does.</summary>
    private static void Link(SidneyMachine sidney, string name, string item)
    {
        sidney.Scan(item);
        sidney.OpenSuspect(
            sidney.Suspects().First(s => s.Name.Contains(name, StringComparison.Ordinal)));

        sidney.LinkToSuspect(sidney.Files.First(f => f.Item == item));
    }

    [Fact]
    public void Every_event_the_tables_name_is_on_the_score_sheet()
    {
        // An event the sheet does not have is worth nothing and is indistinguishable from
        // one the player has not earned, which is exactly how the whole of Sidney's scoring
        // went missing without anything saying so.
        ScoreEvents sheet = ScoreEvents.Open();

        foreach (string name in SidneyScores.Names)
        {
            Assert.True(sheet.Worth(name) is not null, $"{name} is not on the score sheet");
        }
    }

    [Fact]
    public void Scanning_evidence_in_is_worth_its_points()
    {
        SidneyMachine sidney = Machine(out GameState state);

        sidney.Scan("BUCHELLIS_FINGERPRINT");
        sidney.Scan("WILKES_LICENSE");
        sidney.Scan("PARCHMENT_1");

        Assert.Equal(3, state.Score);
    }

    [Fact]
    public void Nothing_is_paid_for_twice()
    {
        SidneyMachine sidney = Machine(out GameState state);

        sidney.Scan("BUCHELLIS_FINGERPRINT");
        sidney.Scan("BUCHELLIS_FINGERPRINT");

        Assert.Equal(1, state.Score);
    }

    [Fact]
    public void The_scanner_pays_nothing_for_what_the_game_does_not_score()
    {
        // Mosely's print is filed for the player rather than scanned, and the two prints
        // labelled with the wrong name are a mistake the player is allowed to make.
        SidneyMachine sidney = Machine(out GameState state);

        sidney.Scan("MOSELYS_PRINT");
        sidney.Scan("WILKES_FINGERPRINT_LABELED_BUCHELLI");

        Assert.Equal(0, state.Score);
    }

    [Fact]
    public void Putting_a_suspects_own_evidence_on_them_is_worth_its_points()
    {
        SidneyMachine sidney = Machine(out GameState state);

        Link(sidney, "Buchelli", "BUCHELLIS_LICENSE");

        // One for scanning the plate and one for working out whose it is.
        Assert.Equal(2, state.Score);
        Assert.True(state.GetFlag("IDedBuchelliVehicle"));
    }

    [Fact]
    public void Putting_it_on_the_wrong_person_is_worth_nothing()
    {
        SidneyMachine sidney = Machine(out GameState state);

        Link(sidney, "Wilkes", "BUCHELLIS_LICENSE");

        Assert.Equal(1, state.Score);
    }

    [Fact]
    public void A_fingerprint_on_its_owner_is_worth_its_point()
    {
        SidneyMachine sidney = Machine(out GameState state);

        Link(sidney, "Larry", "LARRYS_FINGERPRINT");

        Assert.Equal(2, state.Score);
    }

    [Fact]
    public void A_print_that_names_nobody_still_matches_the_person_it_belongs_to()
    {
        // The three off the manuscript are the reason the suspects screen exists. Comparing
        // the file's name against the suspect's could never match them, which left the
        // three flags the action files read unreachable.
        SidneyMachine sidney = Machine(out GameState state);

        Link(sidney, "Buchelli", "UNKNOWN_PRINT_1");
        sidney.MatchPrint();

        Assert.True(state.GetFlag("MatchedBuchelli"));
        Assert.Equal(2, state.Score);
    }

    [Fact]
    public void Such_a_print_does_not_match_somebody_else()
    {
        SidneyMachine sidney = Machine(out GameState state);

        Link(sidney, "Wilkes", "UNKNOWN_PRINT_1");
        sidney.MatchPrint();

        Assert.False(state.GetFlag("MatchedWilkes"));
        Assert.Equal(1, state.Score);
    }

    [Fact]
    public void The_envelope_print_is_Estelles()
    {
        SidneyMachine sidney = Machine(out GameState state);

        Link(sidney, "Estelle", "ESTELLES_FINGERPRINT_LSR");
        sidney.MatchPrint();

        Assert.True(state.GetFlag("MatchedEstelle"));
        Assert.Equal(5, state.Score);
    }

    /// <summary>Scans a picture in and opens it on the analyze screen.</summary>
    private static SidneyMachine Looking(GameState state, string item)
    {
        SidneyMachine sidney = new(SidneyLibrary.From(Text), state) { Scores = ScoreEvents.Open() };

        sidney.Scan(item);
        sidney.OpenFile(sidney.Files.First(f => f.Item == item));

        return sidney;
    }

    [Fact]
    public void Only_the_Teniers_with_no_temple_can_be_zoomed()
    {
        // The pair differ by a building painted out, and the verse heading the zoom reads
        // is on that one alone.
        GameState state = new() { Ego = "GRACE" };

        Assert.DoesNotContain(
            SidneyAction.ZoomAndClarify,
            Looking(state, "TENIERS_POSTCARD_TEMP").Available());

        Assert.Contains(
            SidneyAction.ZoomAndClarify,
            Looking(new GameState { Ego = "GRACE" }, "TENIERS_POSTCARD_NO_TEMP").Available());
    }

    [Fact]
    public void Zooming_the_Teniers_postcard_is_worth_its_points_and_fetches_the_verse()
    {
        GameState state = new() { Ego = "GRACE" };
        SidneyMachine sidney = Looking(state, "TENIERS_POSTCARD_NO_TEMP");

        int before = state.Score;
        SidneyResult asked = sidney.Perform(SidneyAction.ZoomAndClarify);

        // Paid on the button, as the original pays it: the verse behind the question is
        // worth nothing on its own.
        Assert.Equal(2, state.Score - before);
        Assert.NotNull(asked.Choices);

        SidneyResult verse = sidney.Answer("Yes");

        Assert.Contains("II Chronicles", verse.Text, StringComparison.Ordinal);
        Assert.Equal("02OCB2ZQ35", sidney.TakeCue()?.Plate);
    }

    [Fact]
    public void Zooming_the_Poussin_postcard_saves_the_inscription_as_a_file()
    {
        // <b>The evening will not end without this flag.</b> R25307A reads SavedArcadiaText,
        // and the port was only setting it at the far end of the translate screen — after
        // the player had typed the missing word.
        GameState state = new() { Ego = "GRACE" };
        SidneyMachine sidney = Looking(state, "POUSSIN_POSTCARD");

        int before = state.Score;

        sidney.Perform(SidneyAction.ZoomAndClarify);

        Assert.False(state.GetFlag("SavedArcadiaText"));

        sidney.Answer("Yes");

        Assert.True(state.GetFlag("SavedArcadiaText"));
        Assert.Equal(2, state.Score - before);
    }

    [Fact]
    public void Saying_no_to_the_zoom_keeps_nothing()
    {
        GameState state = new() { Ego = "GRACE" };
        SidneyMachine sidney = Looking(state, "POUSSIN_POSTCARD");

        sidney.Perform(SidneyAction.ZoomAndClarify);
        sidney.Answer("No");

        Assert.False(state.GetFlag("SavedArcadiaText"));

        // And it can be asked again, which is the whole reason no is allowed to mean no.
        Assert.NotNull(sidney.Perform(SidneyAction.ZoomAndClarify).Choices);
    }

    [Fact]
    public void Every_card_the_printer_makes_is_one_the_scripts_ask_for()
    {
        // CSE202P names these five and nothing else opens the château door: a card the
        // printer spells differently is a card the story never sees.
        Assert.Equal(
            ["FAKE_ID_BLOODBANK", "FAKE_ID_CAR", "FAKE_ID_DIAPERS", "FAKE_ID_NYT_REP", "FAKE_ID_REPORTER"],
            SidneyMachine.PrintableCards.OrderBy(c => c, StringComparer.Ordinal));
    }

    [Fact]
    public void Gabriel_will_not_use_the_three_screens_he_has_no_business_in()
    {
        // He says so and the screen stays shut, which is what keeps him from working Le
        // Serpent Rouge out on Grace's behalf.
        foreach (SidneyScreen screen in
            new[] { SidneyScreen.Search, SidneyScreen.EMail, SidneyScreen.Analyze })
        {
            SidneyMachine sidney = Machine(out _, "GABRIEL");

            sidney.Show(screen);

            Assert.Equal(SidneyScreen.Main, sidney.Screen);
            Assert.True(sidney.HasCues);
        }
    }

    [Fact]
    public void Gabriel_uses_the_rest()
    {
        foreach (SidneyScreen screen in new[]
        {
            SidneyScreen.Files, SidneyScreen.Translate, SidneyScreen.AddData,
            SidneyScreen.MakeId, SidneyScreen.Suspects,
        })
        {
            SidneyMachine sidney = Machine(out _, "GABRIEL");

            sidney.Show(screen);

            Assert.Equal(screen, sidney.Screen);
        }
    }

    [Fact]
    public void Grace_uses_all_eight()
    {
        foreach (SidneyScreen screen in new[]
        {
            SidneyScreen.Search, SidneyScreen.EMail, SidneyScreen.Files, SidneyScreen.Analyze,
            SidneyScreen.Translate, SidneyScreen.AddData, SidneyScreen.MakeId,
            SidneyScreen.Suspects,
        })
        {
            SidneyMachine sidney = Machine(out _);

            sidney.Show(screen);

            Assert.Equal(screen, sidney.Screen);
        }
    }
}
