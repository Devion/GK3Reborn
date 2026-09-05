using GK3Reborn.Formats.Ui;
using GK3Reborn.Game.Sidney;
using Xunit;

namespace GK3Reborn.Tests.Formats;

/// <summary>
/// Tests for the game's prose files.
/// </summary>
/// <remarks>
/// The reason this reader exists rather than reusing the scene one is the first test here.
/// A <c>.SIF</c> line is a list of comma-separated settings; these files hold English
/// sentences, and English sentences have commas in them. Reading one with the other reader
/// turns a paragraph of Grace's mail into forty settings, silently.
/// </remarks>
public sealed class KeyedTextTests
{
    [Fact]
    public void A_value_runs_to_the_end_of_the_line_commas_and_all()
    {
        KeyedText text = KeyedText.Parse("""
            [EMail1]
            Body1 = Grace:  Your Father had a wonderful idea, and we're sending tickets, home.
            """);

        Assert.Equal(
            "Grace:  Your Father had a wonderful idea, and we're sending tickets, home.",
            text.Value("EMail1", "Body1"));
    }

    [Fact]
    public void Both_comment_markers_are_understood()
    {
        // Both appear in the corpus, sometimes in the same file.
        KeyedText text = KeyedText.Parse("""
            ; a semicolon comment
            // a slash comment
            [Main Screen]
            MenuName = SIDNEY
            ; MenuName = NOT THIS ONE
            """);

        Assert.Equal("SIDNEY", text.Value("Main Screen", "MenuName"));
    }

    [Fact]
    public void The_escapes_the_files_are_written_with_are_decoded()
    {
        KeyedText text = KeyedText.Parse("""
            [Analyze Screen]
            AnalyzeParch1 = 1. First.\n\n\t\t\tSee EXTRACT ANOMALIES.
            Spacer = <space>
            """);

        Assert.Equal("1. First.\n\n\t\t\tSee EXTRACT ANOMALIES.", text.Value("Analyze Screen", "AnalyzeParch1"));
        Assert.Equal(string.Empty, text.Value("Analyze Screen", "Spacer"));
    }

    [Fact]
    public void A_percent_placeholder_is_left_for_whoever_fills_it()
    {
        // The Sidney text is full of these and this is not their caller.
        KeyedText text = KeyedText.Parse("""
            [Analyze Screen]
            MapEnterPointNote = Point entered at %s.
            """);

        Assert.Equal("Point entered at %s.", text.Value("Analyze Screen", "MapEnterPointNote"));
    }

    [Fact]
    public void A_numbered_run_comes_back_in_order_and_stops_at_a_gap()
    {
        // A gap is a mistake rather than a signal, and gathering past one would silently
        // reorder somebody's paragraphs.
        KeyedText text = KeyedText.Parse("""
            [EMail1]
            Body1 = first
            Body2 = second
            Body4 = fourth
            """);

        Assert.Equal(["first", "second"], text.Run("EMail1", "Body"));
    }

    [Fact]
    public void Keys_before_any_section_are_still_readable()
    {
        KeyedText text = KeyedText.Parse("loose = value");

        Assert.Equal("value", text.Value(string.Empty, "loose"));
    }

    [Fact]
    public void A_missing_section_or_key_answers_rather_than_throws()
    {
        KeyedText text = KeyedText.Parse("[A]\nk = v");

        Assert.Null(text.Value("B", "k"));
        Assert.Null(text.Value("A", "nothing"));
        Assert.Empty(text.Section("B"));
        Assert.False(text.Has("B"));
        Assert.True(text.Has("A"));
    }
}

/// <summary>
/// Tests for what Sidney is told.
/// </summary>
public sealed class SidneyLibraryTests
{
    private const string Text = """
        [Main Screen]
        MenuName    = SIDNEY
        MenuItem1   = SEARCH
        MenuItem2   = ANALYZE
        MenuItem3   = TRANSLATE
        MenuItem4   = MAKE I.D.
        MenuItem5   = SUSPECTS
        MenuItem6   = ADD DATA
        MenuItem7   = E-MAIL
        MenuItem8   = ^
        MenuItem9   = EXIT
        CloseButton = CLOSE

        [Analyze Screen]
        AnalyzeParch1 = 1. Text appears to have irregularities in design.
        """;

    private const string Mail = """
        [EMail Files]
        EMail1 = Hello!
        EMail2 = Greetings

        [EMail1]
        From    = RT_Nakimura@aol.com
        To      = Grace.Nakimura@Euroserve.com
        Date    = Jul 1, 1998, 7:25am
        Subject = Hello!
        Body1   = Grace:  Your Father had a wonderful idea.
        Body2   = <space>
        Body3   = Let me know right away about the trip.

        [EMail2]
        From    = RMikoshi@hotmail.com
        Subject = Greetings
        Body1   = Hello, Grace.
        """;

    [Fact]
    public void The_menu_comes_from_the_games_own_text()
    {
        SidneyLibrary sidney = SidneyLibrary.From(Text, Mail);

        Assert.True(sidney.Loaded);
        Assert.Equal(
            ["SEARCH", "ANALYZE", "TRANSLATE", "MAKE I.D.", "SUSPECTS", "ADD DATA", "E-MAIL", "EXIT"],
            sidney.MainMenu());
    }

    [Fact]
    public void The_separator_is_not_offered_as_a_row()
    {
        // The file writes a caret for the rule between ADD DATA and E-MAIL. Offering it is
        // offering the player a menu item called "^".
        Assert.DoesNotContain("^", SidneyLibrary.From(Text, Mail).MainMenu());
    }

    [Fact]
    public void Every_row_but_the_way_out_opens_a_screen()
    {
        IReadOnlyList<(SidneyScreen Screen, string Label)> rows = SidneyLibrary.From(Text, Mail).Rows();

        Assert.Equal(7, rows.Count);
        Assert.Equal(SidneyScreen.Search, rows[0].Screen);
        Assert.Equal(SidneyScreen.EMail, rows[6].Screen);
        Assert.DoesNotContain(rows, r => r.Label == "EXIT");
    }

    [Fact]
    public void A_row_opens_its_screen_whatever_language_it_is_written_in()
    {
        // The rows are the same nine in every release and only their words change, so the
        // number is what says which screen a row opens. Matching the word meant a French
        // game's menu — RECHERCHER, ANALYSER, TRADUIRE — opened nothing at all.
        IReadOnlyList<(SidneyScreen Screen, string Label)> rows = SidneyLibrary.From(
            """
            [Main Screen]
            MenuItem1   = RECHERCHER
            MenuItem2   = ANALYSER
            MenuItem3   = TRADUIRE
            MenuItem4   = FAUX PAPIERS
            MenuItem5   = SUSPECTS
            MenuItem6   = AJOUTER DONNEES
            MenuItem7   = E-MAIL
            MenuItem8   = ^
            MenuItem9   = QUITTER
            """,
            Mail).Rows();

        Assert.Equal(7, rows.Count);
        Assert.Equal(SidneyScreen.Search, rows[0].Screen);
        Assert.Equal("RECHERCHER", rows[0].Label);
        Assert.Equal(SidneyScreen.MakeId, rows[3].Screen);
        Assert.Equal(SidneyScreen.EMail, rows[6].Screen);
        Assert.DoesNotContain(rows, r => r.Label == "QUITTER");
    }

    [Fact]
    public void The_inbox_reads_with_its_paragraphs_and_headers()
    {
        IReadOnlyList<SidneyMail> inbox = SidneyLibrary.From(Text, Mail).Mail();

        Assert.Equal(2, inbox.Count);
        Assert.Equal("Hello!", inbox[0].Subject);
        Assert.Equal("RT_Nakimura@aol.com", inbox[0].From);
        Assert.Equal("Jul 1, 1998, 7:25am", inbox[0].Date);

        // Three paragraphs, the middle one a deliberate blank.
        Assert.Equal(3, inbox[0].Body.Count);
        Assert.Equal(string.Empty, inbox[0].Body[1]);
    }

    [Fact]
    public void A_string_can_be_asked_for_by_section()
    {
        SidneyLibrary sidney = SidneyLibrary.From(Text, Mail);

        Assert.StartsWith("1. Text appears", sidney.Say("AnalyzeParch1", "Analyze Screen"), StringComparison.Ordinal);
        Assert.Equal("CLOSE", sidney.Say("CloseButton"));
    }

    [Fact]
    public void A_run_with_no_game_data_gets_an_empty_library_rather_than_a_crash()
    {
        Assert.False(SidneyLibrary.Empty.Loaded);
        Assert.Empty(SidneyLibrary.Empty.MainMenu());
        Assert.Empty(SidneyLibrary.Empty.Mail());
        Assert.Equal(string.Empty, SidneyLibrary.Empty.Say("MenuName"));
    }
}
