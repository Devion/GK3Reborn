using GK3Reborn.Game;
using GK3Reborn.Game.Sidney;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Tests for Sidney's anagram parser, and for the twenty points solving it is worth.
/// </summary>
public sealed class SidneyAnagramTests
{
    /// <summary>
    /// The parser's own section of the game's text, cut down to what the puzzle needs: the
    /// two spellings of the inscription, the three words that solve it at the rows the
    /// retail engine names, some that do not, and the one the parser supplies itself.
    /// </summary>
    private const string Text = """
        [Main Screen]
        MenuItem1 = ANALYZE
        MenuItem4 = EXIT

        [Analyze Screen]
        Menu2Item3   = ANAGRAM PARSER
        AnalyzePous  = Painting analysed.
        ArcadiaText  = Et in Arcadia Ego Sum
        ArcadiaText2 = Et in Arcadia Ego
        Parsing      = Parsing:
        LatinMsg     = Original language of text is Latin.
        Latin2Msg    = Checking Latin database.
        SelectMsg    = Select words to move over to the phrase building area.
        CheckingMsg  = Checking all databases for match for remaining letters.
        MatchMsg     = Match found in Latin database.
        RebusMsg     = ** Complete rebus confirmed **
        TransMsg     = Translation: I touch the tomb of God, Jesus.
        NoMatchMsg   = ** No match found.  Rebus incomplete. **
        TryAgain     = Anagram answer incorrect.  Try again.
        Word1        = Academia (University)
        Word2        = Bellum (War)
        Word12       = Arca (Ark)
        Word13       = Arcam (Tomb)
        Word14       = Arcana (Secrets)
        Word41       = Dei (God)
        Word147      = Tango (I Touch)
        Word161      = Uter (Either one)
        FinalWord    = Iesu (Jesus)
        WordB1       = Acer (Pungent)
        WordB12      = Dei (God)
        WordB26      = I Tego (I Conceal)
        """;

    private static GameState Ready()
    {
        var state = new GameState { Ego = "GRACE" };

        // The poem as far as Scorpio, which is where the original lets the parser open, and
        // the inscription saved with its missing word supplied.
        foreach (string sign in new[]
        {
            "Aquarius", "Pisces", "Aries", "Taurus", "Gemini", "Cancer", "Leo",
            "Virgo", "Libra", "Scorpio",
        })
        {
            state.SetFlag(sign);
        }

        state.SetFlag("SavedArcadiaText");
        state.SetFlag("ArcadiaComplete");

        return state;
    }

    private static SidneyMachine Machine(GameState state) =>
        new(SidneyLibrary.From(Text), state) { Scores = ScoreEvents.Open() };

    /// <summary>Scans the painting in and opens the parser over it.</summary>
    private static SidneyMachine Parsing(GameState state)
    {
        SidneyMachine sidney = Machine(state);

        sidney.Scan("POUSSIN_POSTCARD");
        sidney.OpenFile(sidney.Files[0]);
        sidney.Perform(SidneyAction.AnagramParser);

        return sidney;
    }

    [Fact]
    public void The_parser_is_offered_on_the_painting_once_the_text_is_saved()
    {
        GameState state = Ready();
        SidneyMachine sidney = Machine(state);

        sidney.Scan("POUSSIN_POSTCARD");
        sidney.OpenFile(sidney.Files[0]);

        Assert.Contains(SidneyAction.AnagramParser, sidney.Available());
    }

    [Fact]
    public void It_is_not_offered_before_the_inscription_has_been_saved()
    {
        var state = new GameState { Ego = "GRACE" };
        SidneyMachine sidney = Machine(state);

        sidney.Scan("POUSSIN_POSTCARD");
        sidney.OpenFile(sidney.Files[0]);

        Assert.DoesNotContain(SidneyAction.AnagramParser, sidney.Available());
    }

    [Fact]
    public void It_will_not_open_before_the_poem_asks_the_question_it_answers()
    {
        // Ophiuchus is the eleventh sign, and the original refuses the parser until the ten
        // before it are done: Grace says she does not need it on this text.
        var state = new GameState { Ego = "GRACE" };
        state.SetFlag("SavedArcadiaText");
        state.SetFlag("ArcadiaComplete");

        SidneyMachine sidney = Parsing(state);

        Assert.Null(sidney.Anagram);
        Assert.True(sidney.HasCues);
    }

    [Fact]
    public void Opening_it_lists_only_words_the_letters_can_spell()
    {
        SidneyMachine sidney = Parsing(Ready());

        SidneyAnagram anagram = Assert.IsType<SidneyAnagram>(sidney.Anagram);

        Assert.Equal("ETINARCADIAEGOSUM", anagram.Letters);

        // Bellum wants a B and two Ls, and the inscription has neither.
        Assert.Contains(anagram.Available, w => w.Latin == "Arcam");
        Assert.DoesNotContain(anagram.Available, w => w.Latin == "Bellum");
    }

    [Fact]
    public void A_chosen_word_spends_its_letters()
    {
        SidneyMachine sidney = Parsing(Ready());
        SidneyAnagram anagram = sidney.Anagram!;

        sidney.ChooseAnagramWord(147);

        Assert.Equal(anagram.Letters.Length - "Tango".Length, anagram.Remaining.Length);
        Assert.DoesNotContain(anagram.Available, w => w.Latin == "Tango");

        // Arcana wants a second A beyond what Tango left, so it drops off the list.
        Assert.DoesNotContain(anagram.Available, w => w.Latin == "Arcana");
    }

    [Fact]
    public void Erasing_puts_the_letters_back()
    {
        SidneyMachine sidney = Parsing(Ready());
        SidneyAnagram anagram = sidney.Anagram!;

        sidney.ChooseAnagramWord(147);
        sidney.EraseAnagramWord();

        Assert.Empty(anagram.Chosen);
        Assert.Equal(anagram.Letters.Length, anagram.Remaining.Length);
    }

    [Fact]
    public void The_three_words_solve_it_in_any_order_and_are_worth_twenty()
    {
        GameState state = Ready();
        SidneyMachine sidney = Parsing(state);

        int before = state.Score;

        sidney.ChooseAnagramWord(41);
        sidney.ChooseAnagramWord(147);
        sidney.ChooseAnagramWord(13);

        Assert.True(sidney.Anagram!.Solved);

        // The one event in Sidney worth more than five, and the one the port could not
        // reach: there was no parser to solve. Counted as a difference, because scanning
        // the postcard in is worth a point of its own.
        Assert.Equal(20, state.Score - before);
        Assert.True(state.GetFlag("Ophiuchus"));

        // The phrase reads in the order the player built it, with the word the parser
        // supplies at the end — which is how the original prints it.
        Assert.Equal(
            "Dei Tango Arcam Iesu",
            string.Join(' ', sidney.Anagram.Reading().Select(w => w.Latin)));
    }

    [Fact]
    public void Three_wrong_words_leave_it_unsolved_and_unpaid()
    {
        GameState state = Ready();
        SidneyMachine sidney = Parsing(state);

        int before = state.Score;

        sidney.ChooseAnagramWord(1);
        sidney.ChooseAnagramWord(12);
        sidney.ChooseAnagramWord(14);

        Assert.False(sidney.Anagram!.Solved);
        Assert.Equal(before, state.Score);
        Assert.False(state.GetFlag("Ophiuchus"));
    }

    [Fact]
    public void A_fourth_word_is_refused()
    {
        SidneyMachine sidney = Parsing(Ready());

        sidney.ChooseAnagramWord(41);
        sidney.ChooseAnagramWord(147);
        sidney.ChooseAnagramWord(13);
        sidney.ChooseAnagramWord(1);

        Assert.Equal(3, sidney.Anagram!.Chosen.Count);
    }

    [Fact]
    public void Without_the_missing_word_the_parser_cannot_be_finished()
    {
        // Et in Arcadia Ego on its own is four letters short, and the shorter list the
        // original offers for it holds neither Arcam nor Tango. The player has to go back
        // to the translate screen and supply SUM.
        GameState state = Ready();
        state.ClearFlag("ArcadiaComplete");

        SidneyMachine sidney = Parsing(state);
        SidneyAnagram anagram = Assert.IsType<SidneyAnagram>(sidney.Anagram);

        Assert.Equal("ETINARCADIAEGO", anagram.Letters);
        Assert.DoesNotContain(anagram.Words, w => w.Latin == "Arcam");
        Assert.DoesNotContain(anagram.Words, w => w.Latin == "Tango");
    }

    [Fact]
    public void Leaving_the_parser_puts_it_away()
    {
        SidneyMachine sidney = Parsing(Ready());

        sidney.CloseAnagram();

        Assert.Null(sidney.Anagram);
    }
}
