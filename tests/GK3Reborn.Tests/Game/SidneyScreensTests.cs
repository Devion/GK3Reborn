using GK3Reborn.Game;
using GK3Reborn.Game.Sidney;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Tests for Sidney's search, suspects and identity screens.
/// </summary>
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
        Menu1Item3 = BLOOD BANK
        Menu2Name  = REPORTER
        Menu2Item1 = N.Y. TIMES
        Menu2Item2 = FREELANCE
        Menu2Item3 = E. MONTHLY
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

        [VAMPIRE.html]
        text=vampire,vamp,vampires
        """;

    private const string Dialog = """
        [alchemytiltedsquare.html]
        ME=02osu586w1
        flag=KnowTiltedSquare
        Flags=!LockedSquare,!KnowTiltedSquare
        LSR=2
        LSRMax=3
        Onetime=1

        [vampire.html]
        ME=02VA1,02VA2
        Flag=vampire_dlg
        Onetime=1
        """;

    private const string Vampires = """
        <HTML><HEAD><TITLE>Vampires</TITLE></HEAD><BODY>
        <P><FONT SIZE=+2>Vampires</FONT><HR ALIGN=LEFT>
        A vampire is a creature of folklore said to subsist on blood.
        </BODY></HTML>
        """;

    private const string Square = """
        <HTML><HEAD><TITLE>Alchemy: Tilting the Square</TITLE></HEAD><BODY>
        <P><FONT SIZE=+2>Alchemy: Tilting the Square</FONT><HR ALIGN=LEFT>
        The square is tilted to forty-five degrees.
        </BODY></HTML>
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
            Search = SidneySearch.From(Index, Markup, Dialog),
        };
    }

    // The archives resolve a name without its path, its extension or its case, which is
    // what lets one page link to another in whatever spelling it pleases.
    private static string? Markup(string name) => SidneySearch.PageKey(name).ToUpperInvariant()
        switch
        {
            "ARCADIA" => Page,
            "VAMPIRE" => Vampires,
            "ALCHEMYTILTEDSQUARE" => Square,
            _ => null,
        };

    [Fact]
    public void Reading_the_vampire_page_sets_the_flag_the_tour_waits_on()
    {
        // 207A will not let Grace knock on Mosely's door to start the Magdala tour until
        // the "vampire" flag is set, and nothing in the game data sets it: the retail
        // engine sets it on reading the page, and sixteen pages work the same way.
        SidneyMachine sidney = Machine(out GameState state);

        Assert.False(state.GetFlag("vampire"));

        sidney.Typed = "vampires";
        sidney.Look();

        Assert.True(state.GetFlag("vampire"));
    }

    [Fact]
    public void Reading_a_page_the_game_scores_is_worth_its_point_once()
    {
        ScoreEvents scores = ScoreEvents.Open();
        GameState state = new() { Ego = "GRACE" };

        var sidney = new SidneyMachine(SidneyLibrary.From(Text), state)
        {
            Search = SidneySearch.From(Index, Markup, Dialog),
            Scores = scores,
        };

        sidney.Typed = "vampire";
        sidney.Look();

        int earned = state.Score;

        Assert.Equal(scores.Worth("e_sidney_search_vampires"), earned);

        sidney.Look();

        Assert.Equal(earned, state.Score);
    }

    [Fact]
    public void A_page_with_no_flag_of_its_own_changes_nothing()
    {
        SidneyMachine sidney = Machine(out GameState state);

        sidney.Typed = "arcadia";
        sidney.Look();

        Assert.False(state.GetFlag("Arcadia"));
        Assert.False(state.GetFlag("vampire"));
    }

    [Fact]
    public void Following_a_link_to_a_page_counts_as_reading_it()
    {
        // The pages link to each other, and arriving that way is the same arrival.
        SidneyMachine sidney = Machine(out GameState state);

        sidney.Follow("vampire.html");

        Assert.Equal("Vampires", sidney.Page?.Title);
        Assert.True(state.GetFlag("vampire"));
    }

    [Fact]
    public void Grace_says_her_piece_over_a_page_once()
    {
        SidneyMachine sidney = Machine(out GameState state);

        sidney.Typed = "vampire";
        sidney.Look();

        Assert.Equal(["02VA1", "02VA2"], Said(sidney));
        Assert.True(state.GetFlag("vampire_dlg"));

        sidney.Look();

        Assert.Empty(Said(sidney));
    }

    [Fact]
    public void A_remark_waits_for_the_verse_and_the_flags_its_file_names()
    {
        SidneyMachine sidney = Machine(out GameState state);

        // Before Pisces, the tilted square has nothing to say.
        sidney.Follow("alchemytiltedsquare.html");
        Assert.Empty(Said(sidney));

        state.SetFlag("Aquarius");
        state.SetFlag("Pisces");

        // And not while the square is already locked down, which the file forbids.
        state.SetFlag("LockedSquare");
        sidney.Follow("alchemytiltedsquare.html");
        Assert.Empty(Said(sidney));

        state.ClearFlag("LockedSquare");
        sidney.Follow("alchemytiltedsquare.html");

        Assert.Equal(["02osu586w1"], Said(sidney));
    }

    private static List<string> Said(SidneyMachine sidney)
    {
        List<string> plates = [];

        while (sidney.TakeCue() is { } cue)
        {
            plates.Add(cue.Plate);
        }

        return plates;
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

        Assert.Equal(6, identities.Count);
        Assert.Equal("MEDICAL", identities[0].Category);
        Assert.Equal("DOCTOR", identities[0].Title);
        Assert.Equal("REPORTER", identities[3].Category);

        // The trade behind a row is its position, and nothing in the game's data says so:
        // the retail engine holds the list, and the card's picture is filed under it.
        Assert.Equal("DOC", identities[0].Job);
        Assert.Equal("NYTIMES", identities[3].Job);
    }

    /// <summary>The row with a given key, which is how the tests name a trade.</summary>
    private static SidneyIdentity Row(SidneyMachine sidney, string key) =>
        sidney.Library.Identities().First(i => i.Key == key);

    [Fact]
    public void A_press_card_is_printed_into_the_pocket_and_is_worth_its_points()
    {
        // <b>Without the item there is no way into the château.</b> CSE202P asks for these
        // five by name — FAKE_ID_NYT_REP and its siblings — and nothing else opens the door
        // or gets Gabriel past the bartender.
        GameState state = Ready();
        state.Timeblock = new Timeblock(2, 2, IsAfternoon: true);

        SidneyMachine sidney = Scored(state);

        sidney.ChooseIdentity(Row(sidney, "Menu2Item1"));
        sidney.PrintIdentity();

        Assert.True(state.Inventory.Has("GABRIEL", "FAKE_ID_NYT_REP"));

        // Keyed on the row rather than on the job, because the job is translated and the
        // key is not: a card printed in a French game means the same thing in an English
        // one.
        Assert.True(state.GetFlag("SidneyId:Menu2Item1"));

        // "That should work!", and the two points the press cards carry.
        Assert.Equal("02O8G5F772", sidney.TakeCue()?.Plate);
        Assert.Equal(2, state.Score);
    }

    [Fact]
    public void The_other_three_cards_print_and_are_worth_nothing()
    {
        GameState state = Ready();
        state.Timeblock = new Timeblock(2, 2, IsAfternoon: true);

        SidneyMachine sidney = Scored(state);

        sidney.ChooseIdentity(Row(sidney, "Menu1Item3"));
        sidney.PrintIdentity();

        Assert.True(state.Inventory.Has("GABRIEL", "FAKE_ID_BLOODBANK"));

        // "Might provoke an interesting response."
        Assert.Equal("02O8G5FQ21", sidney.TakeCue()?.Plate);
        Assert.Equal(0, state.Score);
    }

    [Fact]
    public void A_card_already_in_the_pocket_is_not_printed_twice()
    {
        GameState state = Ready();
        state.Timeblock = new Timeblock(2, 2, IsAfternoon: true);

        SidneyMachine sidney = Scored(state);

        sidney.ChooseIdentity(Row(sidney, "Menu2Item1"));
        sidney.PrintIdentity();

        while (sidney.HasCues)
        {
            sidney.TakeCue();
        }

        sidney.PrintIdentity();

        Assert.Equal("0XF724XBL1", sidney.TakeCue()?.Plate);
    }

    [Fact]
    public void Gabriel_refuses_a_trade_that_will_not_get_him_through_the_gate()
    {
        GameState state = Ready();
        state.Timeblock = new Timeblock(2, 2, IsAfternoon: true);

        SidneyMachine sidney = Scored(state);

        // A doctor's card is no use at a vineyard, and a sports magazine is the right idea
        // with the wrong masthead. Two different lines, and neither prints anything.
        sidney.ChooseIdentity(Row(sidney, "Menu1Item1"));
        sidney.PrintIdentity();

        Assert.Equal("02O8G5F1K1", sidney.TakeCue()?.Plate);

        sidney.ChooseIdentity(Row(sidney, "Menu2Item3"));
        sidney.PrintIdentity();

        Assert.Equal("02O8G5FNV1", sidney.TakeCue()?.Plate);

        Assert.Empty(state.Inventory.ItemsOf("GABRIEL"));
        Assert.False(state.GetFlag("SidneyId:Menu1Item1"));
    }

    [Fact]
    public void Gabriel_will_not_print_a_card_with_Graces_face_on_it()
    {
        GameState state = Ready();
        state.Timeblock = new Timeblock(2, 2, IsAfternoon: true);

        SidneyMachine sidney = Scored(state);

        sidney.ChooseIdentity(Row(sidney, "Menu2Item1"));
        sidney.GracesFace = true;

        Assert.Equal("GRA_NYTIMES", sidney.IdentityPicture);

        sidney.PrintIdentity();

        Assert.Equal("02O8G5F961", sidney.TakeCue()?.Plate);
        Assert.Empty(state.Inventory.ItemsOf("GABRIEL"));
    }

    /// <summary>Gabriel at Sidney, with the score sheet the game ships.</summary>
    private static GameState Ready() => new() { Ego = "GABRIEL" };

    private static SidneyMachine Scored(GameState state) =>
        new(SidneyLibrary.From(Text), state) { Scores = ScoreEvents.Open() };

    /// <summary>
    /// A card is only needed the afternoon Gabriel calls on Montreaux; any other time the
    /// printer says so, in whichever voice is sitting there, and prints nothing.
    /// </summary>
    [Fact]
    public void Printing_an_identity_any_other_time_is_refused_aloud()
    {
        SidneyMachine sidney = Machine(out GameState state);

        sidney.ChooseIdentity(Row(sidney, "Menu2Item1"));
        sidney.PrintIdentity();

        Assert.False(state.GetFlag("SidneyId:Menu2Item1"));
        Assert.Equal("02O8G5FZ51", sidney.TakeCue()?.Plate);
        Assert.Null(sidney.TakeCue()?.Plate);

        state.Ego = "GABRIEL";
        sidney.PrintIdentity();

        Assert.Equal("02O8G5FVU1", sidney.TakeCue()?.Plate);
        Assert.Empty(state.Inventory.ItemsOf("GABRIEL"));
    }

    /// <summary>The scanner on the second morning: Grace has nothing to scan, and says so.</summary>
    [Fact]
    public void Add_data_on_the_second_morning_says_there_is_nothing_to_scan()
    {
        SidneyMachine sidney = Machine(out GameState state);
        state.Timeblock = new Timeblock(2, 7, IsAfternoon: false);

        sidney.Show(SidneyScreen.AddData);

        Assert.Equal(SidneyScreen.AddData, sidney.Screen);
        Assert.Equal("0264G2ZPF1", sidney.TakeCue()?.Plate);

        state.Timeblock = new Timeblock(2, 10, IsAfternoon: false);
        sidney.Show(SidneyScreen.AddData);

        Assert.Null(sidney.TakeCue()?.Plate);
    }

    /// <summary>
    /// The list grows as the story does: Montreaux once Grace has met him, Mosely from five
    /// that evening and only if his print was lifted. All ten on the first morning named
    /// two people the story had not.
    /// </summary>
    [Fact]
    public void The_suspects_are_added_when_the_story_adds_them()
    {
        SidneyMachine sidney = Machine(out GameState state);

        Assert.Equal(8, sidney.Suspects().Count);
        Assert.DoesNotContain(sidney.Suspects(), s => s.Name.Contains("Montreaux", StringComparison.Ordinal));

        state.Timeblock = new Timeblock(2, 10, IsAfternoon: false);
        Assert.Equal(8, sidney.Suspects().Count);

        state.Timeblock = new Timeblock(2, 2, IsAfternoon: true);
        Assert.Equal(9, sidney.Suspects().Count);
        Assert.Contains(sidney.Suspects(), s => s.Name.Contains("Montreaux", StringComparison.Ordinal));

        state.Timeblock = new Timeblock(2, 5, IsAfternoon: true);
        Assert.Equal(9, sidney.Suspects().Count);

        state.SetFlag("GotPMoselyPrint");
        Assert.Equal(10, sidney.Suspects().Count);

        state.Timeblock = new Timeblock(2, 2, IsAfternoon: true);
        Assert.Equal(9, sidney.Suspects().Count);
    }

    /// <summary>Mosely arrives with his print already on his file, as the original does it.</summary>
    [Fact]
    public void Moselys_print_is_linked_to_him_as_he_is_added()
    {
        SidneyMachine sidney = Machine(out GameState state);
        state.Timeblock = new Timeblock(2, 5, IsAfternoon: true);
        state.SetFlag("GotPMoselyPrint");

        SidneySuspect mosely = sidney.Suspects().Single(s => s.Index == 10);

        sidney.Scan("MOSELYS_PRINT");

        Assert.Contains(sidney.LinkedTo(mosely), f => f.Kind == SidneyKind.KnownPrint);
    }

    /// <summary>
    /// The inbox fills as the days go by: three from the start, the temple's divisions from
    /// the third noon, the symbols from Serres from that evening, the egg's only once found.
    /// </summary>
    [Fact]
    public void Mail_arrives_when_the_story_sends_it()
    {
        const string mail = """
            [EMail Files]
            EMail1 = Hello!
            EMail2 = Greetings
            EMail3 = Itinerary
            EMail4 = Temple of Solomon
            EMail5 = Symbols from Serres
            EMail6 = Egg

            [EMail1]
            Subject = Hello!
            """;

        var state = new GameState { Ego = "GRACE" };
        var sidney = new SidneyMachine(SidneyLibrary.From(Text, mail), state);

        Assert.Equal(["EMail1", "EMail2", "EMail3"], sidney.Mail().Select(m => m.Id));
        Assert.Equal(3, sidney.Unread);

        state.Timeblock = new Timeblock(3, 10, IsAfternoon: false);
        Assert.Equal(3, sidney.Mail().Count);

        state.Timeblock = new Timeblock(3, 12, IsAfternoon: true);
        Assert.Equal(["EMail1", "EMail2", "EMail3", "EMail4"], sidney.Mail().Select(m => m.Id));

        state.Timeblock = new Timeblock(3, 3, IsAfternoon: true);
        Assert.Equal(4, sidney.Mail().Count);

        state.Timeblock = new Timeblock(3, 6, IsAfternoon: true);
        Assert.Equal(5, sidney.Mail().Count);
        Assert.DoesNotContain(sidney.Mail(), m => m.Id == "EMail6");

        state.SetFlag("Egg");
        Assert.Equal(6, sidney.Mail().Count);
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

    /// <summary>
    /// The Site shares Blanchefort's parking lot, so a ride to Blanchefort on Day 1 must
    /// not put Cardou on the map — that takes the noon it belongs to and ten signs of Le
    /// Serpent Rouge, and either the sign flags or the script's count may say so.
    /// </summary>
    [Fact]
    public void Having_been_to_Blanchefort_does_not_reveal_The_Site()
    {
        var state = new GameState { Ego = "GABRIEL", Timeblock = new Timeblock(1, 2, true) };

        state.EnterLocation("GABRIEL", "PLO");

        Assert.DoesNotContain(DrivingMap.Open(state), s => s.Code == "TRE");

        state.Timeblock = new Timeblock(3, 12, true);

        Assert.DoesNotContain(DrivingMap.Open(state), s => s.Code == "TRE");

        state.SetVariable("LSRState", 10);

        Assert.Contains(DrivingMap.Open(state), s => s.Code == "TRE");

        var flagged = new GameState { Ego = "GABRIEL", Timeblock = new Timeblock(3, 12, true) };

        foreach (string sign in new[] { "Aquarius", "Pisces", "Aries", "Taurus", "Gemini",
                     "Cancer", "Leo", "Virgo", "Libra", "Scorpio" })
        {
            flagged.SetFlag(sign);
        }

        Assert.Equal(10, DrivingMap.SerpentRougeSigns(flagged));
        Assert.Contains(DrivingMap.Open(flagged), s => s.Code == "TRE");

        // A gap in the sequence stops the count where the retail engine's does.
        flagged.ClearFlag("Leo");

        Assert.Equal(6, DrivingMap.SerpentRougeSigns(flagged));
        Assert.DoesNotContain(DrivingMap.Open(flagged), s => s.Code == "TRE");
    }

    /// <summary>The hexagram's arms are open for one noon, not for the rest of the day.</summary>
    [Fact]
    public void The_hexagram_arms_are_only_on_the_map_at_noon_on_Day_3()
    {
        var noon = new GameState { Ego = "GABRIEL", Timeblock = new Timeblock(3, 12, true) };

        Assert.Contains(DrivingMap.Open(noon), s => s.Code == "MCB");
        Assert.Contains(DrivingMap.Open(noon), s => s.Code == "BEC");

        var later = new GameState { Ego = "GABRIEL", Timeblock = new Timeblock(3, 3, true) };

        Assert.DoesNotContain(DrivingMap.Open(later), s => s.Code == "MCB");
        Assert.Contains(DrivingMap.Open(later), s => s.Code == "BMB");
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
