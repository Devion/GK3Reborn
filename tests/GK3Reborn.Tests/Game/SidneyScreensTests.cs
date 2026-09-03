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
        Name3         = Emilio Baza
        Name4         = Abbe Arnaud
        Name5         = Lady Howard
        Name6         = Estelle Stiles
        Name7         = John Wilkes
        Name8         = Larry Chester
        Name9         = Excelsior Montreaux
        Name10        = Franklin Mosely
        Nationality1  = French
        Nationality2  = Italian
        Nationality3  = Unknown
        Nationality4  = French
        Nationality5  = British
        Nationality6  = British
        Nationality7  = Australian
        Nationality8  = British
        Nationality9  = French
        Nationality10 = American
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

        Assert.Equal(10, people.Count);
        Assert.Equal("Vittorio Buchelli", people[1].Name);
        Assert.Equal("Italian", people[1].Nationality);
        Assert.Equal("VDG945F", people[1].Vehicle);
    }

    [Fact]
    public void A_registration_is_only_known_once_a_plate_has_been_linked()
    {
        // The screen used to print every registration the moment it was opened, which hands
        // the player the answer to the plates they are out photographing. The game's own
        // refusal for a second licence — "Vehicle information has already been determined
        // for this suspect" — only means something if there was a point at which it had not
        // been, and its analysis of a plate says to "use on Suspects Screen to link vehicles
        // to suspects".
        SidneyMachine sidney = Machine(out _);
        SidneySuspect buchelli = sidney.Library.Suspects()
            .First(s => s.Name.Contains("Buchelli", StringComparison.Ordinal));

        Assert.True(buchelli.Registered);
        Assert.False(sidney.KnowsVehicle(buchelli));

        sidney.Scan("BUCHELLIS_LICENSE");
        sidney.OpenSuspect(buchelli);
        sidney.LinkToSuspect(sidney.Files[0]);

        Assert.True(sidney.KnowsVehicle(buchelli));
    }

    [Fact]
    public void A_car_somebody_merely_saw_is_known_without_any_plate()
    {
        // Five of the ten carry a plate, and they are exactly the five licences the player
        // can photograph. The rest carry what one could tell by looking, and hiding that
        // would hide something they already saw.
        IReadOnlyList<SidneySuspect> people = Machine(out _).Library.Suspects();

        Assert.Equal("Van", people[0].Vehicle);
        Assert.False(people[0].Registered);
        Assert.True(people[1].Registered);

        // The Abbé's is the game's own word for a car nobody ever works out.
        Assert.Equal("Unknown", people[3].Vehicle);
        Assert.False(people[3].Registered);

        // Six registrations against four descriptions — but only five plates and five
        // licence items, because Lady Howard and Estelle Stiles share a car. That is the
        // story point, and it means Estelle's registration can only ever be filled in by
        // linking Lady Howard's licence to her.
        Assert.Equal(
            ["Buchelli", "Emilio", "Howard", "Estelle", "Wilkes", "Mosely"],
            people.Where(person => person.Registered).Select(person => person.Noun));

        Assert.Equal(
            people[4].Vehicle,
            people[5].Vehicle);

        Assert.Equal(
            5,
            people.Where(person => person.Registered)
                .Select(person => person.Vehicle)
                .Distinct()
                .Count());
    }

    [Theory]
    [InlineData("ABBE_FINGERPRINT", "Abbe Arnaud")]
    [InlineData("BUCHELLIS_FINGERPRINT", "Vittorio Buchelli")]
    [InlineData("BUTHANES_FINGERPRINT", "Madeline Buthane")]
    [InlineData("ESTELLES_FINGERPRINT", "Estelle Stiles")]
    [InlineData("HOWARDS_FINGERPRINT", "Lady Howard")]
    [InlineData("LARRYS_FINGERPRINT", "Larry Chester")]
    [InlineData("MONTREAUX_FINGERPRINT", "Excelsior Montreaux")]
    [InlineData("MOSELYS_FINGERPRINT", "Franklin Mosely")]
    [InlineData("WILKES_FINGERPRINT", "John Wilkes")]
    public void Every_print_the_game_ships_reaches_exactly_the_person_it_belongs_to(
        string item, string owner)
    {
        // Evidence is named after the noun the game knows somebody by, and three of them are
        // not their surname: the Abbé by his title, Estelle Stiles and Larry Chester by their
        // first names. Reading a surname off the suspect list left those three prints
        // matching nobody at all — no match, no flag, and no way to convict them.
        SidneyMachine sidney = Machine(out _);

        sidney.Scan(item);

        foreach (SidneySuspect person in sidney.Library.Suspects())
        {
            sidney.OpenSuspect(person);
            sidney.LinkToSuspect(sidney.Files[0]);

            string said = sidney.MatchPrint().Text;
            bool theirs = person.Name.Equals(owner, StringComparison.Ordinal);

            Assert.Equal(
                theirs,
                said.Contains("** Match Found **", StringComparison.Ordinal));

            sidney.UnlinkFromSuspect(sidney.Files[0]);
        }
    }

    [Fact]
    public void Matching_a_print_sets_the_flag_the_story_is_waiting_on()
    {
        // "SidneyMatched:6" was written and read by nothing. What the game's own scripts ask
        // for is MatchedEstelle, and setting it is what opens the T_LSR topic with her in the
        // lobby and gives Grace something to say over the LSR envelope. Four of these flags
        // are named in the scripts — Buthane, Buchelli, Estelle, Mosely — and this is how
        // they are spelt.
        SidneyMachine sidney = Machine(out GameState state);
        SidneySuspect estelle = sidney.Library.Suspects()
            .First(s => s.Name.Contains("Estelle", StringComparison.Ordinal));

        sidney.Scan("ESTELLES_FINGERPRINT");
        sidney.OpenSuspect(estelle);
        sidney.LinkToSuspect(sidney.Files[0]);

        Assert.False(state.GetFlag("MatchedEstelle"));

        Assert.Contains("Match Found", sidney.MatchPrint().Text, StringComparison.Ordinal);

        Assert.True(state.GetFlag("MatchedEstelle"));
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
