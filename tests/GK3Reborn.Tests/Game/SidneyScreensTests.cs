using GK3Reborn.Game;
using GK3Reborn.Game.Sidney;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Tests for Sidney's search, suspects and identity screens.
/// </summary>
/// <remarks>
/// These three were the ones that said "not implemented yet", and one of them —
/// fingerprint matching — decides whether a murder can be pinned on anybody. The test that
/// matters most is the mislabelled print: <c>BUCHELLIS_FINGERPRINT_LABELED_WILKES</c> is
/// Buchelli's however it is labelled, and an engine that believed the label would quietly
/// convict the wrong man.
/// </remarks>
public sealed class SidneyScreensTests
{
    private const string Text = """
        [Main Screen]
        MenuItem1 = SEARCH
        MenuItem2 = SUSPECTS
        MenuItem3 = MAKE I.D.
        MenuItem4 = EXIT

        [Search Screen]
        NotFound = Subject not found.

        [MakeID Screen]
        Menu1Name  = MEDICAL
        Menu1Item1 = DOCTOR
        Menu1Item2 = CORONER
        Menu2Name  = POLICE
        Menu2Item1 = NEW ORLEANS
        Select     = SELECT:
        Print      = PRINT IDENTIFICATION

        [Suspects Screen]
        Name1         = Madeline Buthane
        Name2         = Vittorio Buchelli
        Name3         = John Wilkes
        Nationality1  = French
        Nationality2  = Italian
        Nationality3  = Australian
        VehicleID1    = Van
        VehicleID2    = VDG945F
        VehicleID3    = FED039A
        MatchCompare  = Comparing with:
        MatchNone     = ** No Match Found **
        MatchFound    = ** Match Found **
        NoSuspect     = You must first open the suspect file.
        NoFingerprint = You must link a fingerprint first.
        AlreadyLinked = This file has already been linked to a suspect.
        ExistingFP    = A fingerprint has already been linked to this suspect.
        NoLinks       = There are no linked files for this suspect.
        GabesPrint    = Print matches record on file for "Gabriel Knight."
        """;

    private const string Index = """
        [Arcadia.html]
        text=arcadia,et in arcadia,sheperds,shepherd,shepherds

        [Cathars.html]
        text=cathars,cathar
        """;

    private const string Page = """
        <HTML><HEAD><TITLE>Arcadia</TITLE></HEAD><BODY>
        <P><FONT SIZE=+2>Arcadia</FONT><HR ALIGN=LEFT>
        Arcadia is a mythological place of pastoral serenity, shepherds and nymphs.
        <P>
        These concepts are related to the <A HREF="treeofknowledge.html">Tree of Knowledge</A>.
        </BODY></HTML>
        """;

    private static SidneyMachine Machine(out GameState state)
    {
        state = new GameState { Ego = "GRACE" };

        return new SidneyMachine(SidneyLibrary.From(Text), state)
        {
            Search = SidneySearch.From(Index, name => name == "Arcadia.html" ? Page : null),
        };
    }

    [Fact]
    public void Every_spelling_the_game_lists_finds_its_page()
    {
        SidneyMachine sidney = Machine(out _);

        foreach (string spelling in new[] { "arcadia", "ARCADIA", " shepherds ", "sheperds" })
        {
            Assert.Equal("Arcadia", sidney.Search.Look(spelling)?.Title);
        }
    }

    [Fact]
    public void A_subject_nobody_listed_is_not_found()
    {
        // The index carries the variations somebody thought of; guessing past it would let
        // the player find pages the puzzle means them to work for.
        SidneyMachine sidney = Machine(out _);

        Assert.Null(sidney.Search.Look("arcadian"));
        Assert.Null(sidney.Search.Look("the holy grail"));
        Assert.Null(sidney.Search.Look(""));
    }

    [Fact]
    public void A_page_comes_back_as_headings_rules_links_and_prose()
    {
        SearchPage page = Machine(out _).Search.Look("arcadia")!;

        Assert.Equal("Arcadia", page.Title);
        Assert.Contains(page.Lines, l => l.Heading);
        Assert.Contains(page.Lines, l => l.Rule);
        Assert.Contains(page.Lines, l => l.Text.Contains("mythological", StringComparison.Ordinal));
        Assert.Contains(page.Lines, l => l.Link == "treeofknowledge.html");
    }

    [Fact]
    public void Markup_the_interface_cannot_show_is_dropped_rather_than_printed()
    {
        SearchPage page = Machine(out _).Search.Look("arcadia")!;

        Assert.DoesNotContain(page.Lines, l => l.Text.Contains('<', StringComparison.Ordinal));
        Assert.DoesNotContain(page.Lines, l => l.Text.Contains("HTML", StringComparison.Ordinal));
    }

    [Fact]
    public void A_search_that_finds_nothing_says_so_in_the_games_own_words()
    {
        SidneyMachine sidney = Machine(out _);

        sidney.Typed = "nothing at all";

        Assert.Equal("Subject not found.", sidney.Look().Text);
        Assert.Null(sidney.Page);
    }

    [Fact]
    public void The_suspects_come_out_of_the_games_own_text()
    {
        IReadOnlyList<SidneySuspect> people = Machine(out _).Library.Suspects();

        Assert.Equal(3, people.Count);
        Assert.Equal("Vittorio Buchelli", people[1].Name);
        Assert.Equal("Italian", people[1].Nationality);
        Assert.Equal("VDG945F", people[1].Vehicle);
    }

    [Fact]
    public void A_print_labelled_with_the_wrong_name_matches_whose_it_actually_is()
    {
        // The whole point of that item, and the one thing here a wrong answer would ruin.
        SidneyMachine sidney = Machine(out _);

        sidney.Scan("BUCHELLIS_FINGERPRINT_LABELED_WILKES");

        sidney.OpenSuspect(sidney.Library.Suspects().First(s => s.Name.Contains("Buchelli", StringComparison.Ordinal)));
        sidney.LinkToSuspect(sidney.Files[0]);

        Assert.Contains("Match Found", sidney.MatchPrint().Text, StringComparison.Ordinal);

        sidney.OpenSuspect(sidney.Library.Suspects().First(s => s.Name.Contains("Wilkes", StringComparison.Ordinal)));
        sidney.LinkToSuspect(sidney.Files[0]);

        Assert.Contains("No Match Found", sidney.MatchPrint().Text, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_print_matches_nobody()
    {
        // Which is what the game's own analysis says it is for: bringing it here to be
        // matched against a known one.
        SidneyMachine sidney = Machine(out _);

        sidney.Scan("UNKNOWN_PRINT_1");
        sidney.OpenSuspect(sidney.Library.Suspects()[0]);
        sidney.LinkToSuspect(sidney.Files[0]);

        Assert.Contains("No Match Found", sidney.MatchPrint().Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Matching_with_nothing_open_or_nothing_linked_says_which()
    {
        SidneyMachine sidney = Machine(out _);

        Assert.Equal("You must first open the suspect file.", sidney.MatchPrint().Text);

        sidney.OpenSuspect(sidney.Library.Suspects()[0]);

        Assert.Equal("You must link a fingerprint first.", sidney.MatchPrint().Text);
    }

    [Fact]
    public void A_suspect_holds_one_fingerprint_at_a_time()
    {
        SidneyMachine sidney = Machine(out _);

        sidney.Scan("ABBE_FINGERPRINT");
        sidney.Scan("WILKES_FINGERPRINT");
        sidney.OpenSuspect(sidney.Library.Suspects()[0]);

        Assert.Contains("linked", sidney.LinkToSuspect(sidney.Files[0]).Text, StringComparison.Ordinal);
        Assert.Equal(
            "A fingerprint has already been linked to this suspect.",
            sidney.LinkToSuspect(sidney.Files[1]).Text);
    }

    [Fact]
    public void Un_linking_puts_a_file_back()
    {
        SidneyMachine sidney = Machine(out _);

        sidney.Scan("ABBE_FINGERPRINT");
        sidney.OpenSuspect(sidney.Library.Suspects()[0]);
        sidney.LinkToSuspect(sidney.Files[0]);

        Assert.Single(sidney.LinkedTo(sidney.Library.Suspects()[0]));

        sidney.UnlinkFromSuspect(sidney.Files[0]);

        Assert.Empty(sidney.LinkedTo(sidney.Library.Suspects()[0]));
    }

    [Fact]
    public void What_is_linked_survives_a_save()
    {
        SidneyMachine sidney = Machine(out GameState state);

        sidney.Scan("ABBE_FINGERPRINT");
        sidney.OpenSuspect(sidney.Library.Suspects()[0]);
        sidney.LinkToSuspect(sidney.Files[0]);

        var reloaded = new GameState();
        reloaded.Restore(state.Capture());

        var after = new SidneyMachine(SidneyLibrary.From(Text), reloaded);
        after.OpenSuspect(after.Library.Suspects()[0]);

        Assert.Single(after.LinkedTo(after.Library.Suspects()[0]));
    }

    [Fact]
    public void The_identities_come_out_of_the_games_own_text_grouped_by_trade()
    {
        IReadOnlyList<SidneyIdentity> identities = Machine(out _).Library.Identities();

        Assert.Equal(3, identities.Count);
        Assert.Equal("MEDICAL", identities[0].Category);
        Assert.Equal("DOCTOR", identities[0].Title);
        Assert.Equal("POLICE", identities[2].Category);
    }

    [Fact]
    public void Printing_an_identity_is_something_the_story_can_read()
    {
        SidneyMachine sidney = Machine(out GameState state);

        sidney.PrintIdentity(sidney.Library.Identities()[0]);

        Assert.Equal("DOCTOR", sidney.Identity?.Title);
        Assert.True(state.GetFlag("SidneyId:DOCTOR"));
    }
}

/// <summary>
/// Tests for the map the moped is ridden around.
/// </summary>
public sealed class DrivingMapTests
{
    [Fact]
    public void The_map_has_the_sixteen_places_the_retail_engine_lists()
    {
        Assert.Equal(16, DrivingMap.All.Count);

        // Every place is somewhere on the 640 by 480 painting, which is the one thing a
        // transcribed coordinate could be wrong about in a way nobody would notice until
        // a marker was drawn off the edge of it.
        foreach (DrivingStop stop in DrivingMap.All)
        {
            Assert.InRange(stop.X, 0, DrivingMap.MapWidth - 1);
            Assert.InRange(stop.Y, 0, DrivingMap.MapHeight - 1);
            Assert.NotEmpty(stop.Scene);
            Assert.StartsWith("dm_", stop.Sprite, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Five_places_are_on_the_map_before_the_story_says_anything()
    {
        var state = new GameState { Ego = "GABRIEL" };

        IReadOnlyList<DrivingStop> open = DrivingMap.Open(state);

        Assert.Equal(5, open.Count);
        Assert.Contains(open, s => s.Code == "RLC");
        Assert.Contains(open, s => s.Code == "TR1");
        Assert.DoesNotContain(open, s => s.Code == "WOD");
    }

    [Fact]
    public void A_script_can_put_a_place_on_the_map()
    {
        var state = new GameState { Ego = "GABRIEL" };

        Assert.True(DrivingMap.Reveal(state, "WOD"));
        Assert.Contains(DrivingMap.Open(state), s => s.Code == "WOD");

        Assert.False(DrivingMap.Reveal(state, "NOWHERE"));
    }

    [Fact]
    public void Somewhere_the_player_has_been_stays_on_the_map()
    {
        var state = new GameState { Ego = "GABRIEL" };

        state.EnterLocation("GABRIEL", "MCB");

        Assert.Contains(DrivingMap.Open(state), s => s.Code == "MCB");
    }

    [Fact]
    public void The_room_the_player_is_standing_in_is_not_offered()
    {
        var state = new GameState { Ego = "GABRIEL" };

        Assert.DoesNotContain(DrivingMap.Open(state, "MOP"), s => s.Scene == "MOP");
    }

    [Fact]
    public void What_is_on_the_map_survives_a_save()
    {
        var state = new GameState { Ego = "GABRIEL" };

        DrivingMap.Reveal(state, "POU");

        var reloaded = new GameState();
        reloaded.Restore(state.Capture());

        Assert.Contains(DrivingMap.Open(reloaded), s => s.Code == "POU");
    }
}
