using GK3Reborn.Game;
using GK3Reborn.Game.Sidney;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Tests for Sidney's translate screen and its mail.
/// </summary>
/// <remarks>
/// The translate screen answered "Not implemented yet" for every file, and every one of the
/// strings it needed was already in <c>ESIDNEY.TXT</c>. The tests here are written against
/// the same shape of data the real file has, so that a screen which claims to translate the
/// Abbé's telephone call is checked against the game's own French and the game's own English
/// rather than against something plausible.
/// </remarks>
public sealed class SidneyTranslateTests
{
    private const string Text = """
        [Main Screen]
        MenuItem1 = TRANSLATE
        MenuItem2 = E-MAIL

        [Translate Screen]
        ScreenName      = TRANSLATE
        English         = English
        Latin           = Latin
        French          = French
        Italian         = Italian
        From            = FROM:
        TranslateNow    = TRANSLATE NOW
        NotTranslatable = I am unable to translate this file.
        WrongFrom       = The item to be translated is not recognized in the 'from' language selected.
        NoFurther       = No further translation available.
        Subject         = INCOMPLETE SENTENCE.
        Question        = Do you want to add text?
        Yes             = YES
        No              = NO
        Input           = String to append:
        BadInput1       = Sentence completion not recognized with that string.
        BadInput2       = Would you like to try another string?
        AbbeTape1       = Allo! C'est Arnaud a l'appareil.
        AbbeTape2       = Il faut que je parle au Grand Maitre.
        AbbeTapeT1      = Hello. Arnaud here.
        AbbeTapeT2      = I must speak with the Grand Master.
        ArcadiaText1    = Et in Arcadia Ego
        ArcadiaTextT1   = And (while) in Arcadia I...
        ArcSUMText1     = Et in Arcadia Ego Sum
        ArcSUMTextT1    = I am also (even) in Arcadia.
        """;

    private const string Mail = """
        [EMail Files]
        EMail1 = Hello!
        EMail2 = Greetings

        [EMail1]
        From    = RT_Nakimura@aol.com
        To      = Grace.Nakimura@Euroserve.com
        CC      =
        Date    = Jul 1, 1998, 7:25am
        Subject = Hello!
        Body1   = Grace: your father had a wonderful idea.

        [EMail2]
        From    = s.pam@easteregg.com
        To      = Grace.Nakimura@Euroserve.com
        Date    = Jul 9, 1998, 3:38pm
        Subject = Greetings
        Body1   = Lose thirty pounds.
        """;

    private static SidneyMachine Machine(out GameState state)
    {
        state = new GameState { Ego = "GRACE" };

        return new SidneyMachine(SidneyLibrary.From(Text, Mail), state);
    }

    [Fact]
    public void Only_the_files_with_something_to_translate_are_offered()
    {
        SidneyMachine sidney = Machine(out _);

        sidney.Scan("ABBE_TAPE");
        sidney.Scan("PARCHMENT_1");

        Assert.Equal(
            ["ABBE_TAPE"],
            sidney.Files.Where(sidney.Translator.CanTranslate).Select(f => f.Item));
    }

    [Fact]
    public void The_tape_is_french_and_says_what_the_game_says_it_says()
    {
        SidneyMachine sidney = Machine(out _);

        sidney.Scan("ABBE_TAPE");
        sidney.OpenForTranslation(sidney.Files[0]);

        SidneyTranslation found = sidney.Translator.Find(sidney.Files[0])!;

        Assert.Equal("French", found.Language);
        Assert.Equal(2, found.Original.Count);
        Assert.Contains("Arnaud", found.Original[0], StringComparison.Ordinal);

        sidney.From = "French";

        Assert.Contains("Arnaud here", sidney.Translate().Text, StringComparison.Ordinal);
        Assert.Contains("Grand Master", sidney.Translate().Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Choosing_the_wrong_language_is_refused_in_the_games_own_words()
    {
        // The from-language is a real choice, which is the reason the screen has a menu of
        // four rather than one button.
        SidneyMachine sidney = Machine(out _);

        sidney.Scan("ABBE_TAPE");
        sidney.OpenForTranslation(sidney.Files[0]);
        sidney.From = "Italian";

        Assert.Contains("not recognized", sidney.Translate().Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Arnaud here", sidney.Translate().Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Arcadia_comes_back_unfinished_and_offers_to_be_added_to()
    {
        SidneyMachine sidney = Machine(out _);

        sidney.Scan("POUSSIN_POSTCARD");
        sidney.OpenForTranslation(sidney.Files[0]);
        sidney.From = "Latin";

        SidneyResult said = sidney.Translate();

        Assert.Contains("Arcadia I", said.Text, StringComparison.Ordinal);
        Assert.Contains("INCOMPLETE", said.Text, StringComparison.Ordinal);
        Assert.Equal("Do you want to add text?", said.Asks);
        Assert.Equal(["YES", "NO"], said.Choices?.Select(choice => choice.Text));
        Assert.Equal(["Yes", "No"], said.Choices?.Select(choice => choice.Key));
    }

    [Fact]
    public void Only_the_word_that_finishes_the_sentence_finishes_it()
    {
        // The player has to have found "Sum" somewhere. Accepting anything that merely looks
        // Latin would hand the puzzle over.
        SidneyMachine sidney = Machine(out _);

        sidney.Scan("POUSSIN_POSTCARD");
        sidney.OpenForTranslation(sidney.Files[0]);
        sidney.From = "Latin";
        sidney.Translate();
        sidney.Complete(yes: true);

        Assert.True(sidney.Appending);

        sidney.Typed = "Ego";

        Assert.Contains("not recognized", sidney.Append().Text, StringComparison.Ordinal);
        Assert.True(sidney.Appending);

        sidney.Typed = "  sum  ";

        Assert.Contains("I am also", sidney.Append().Text, StringComparison.Ordinal);
        Assert.False(sidney.Appending);
    }

    [Fact]
    public void Finishing_it_is_recorded_where_the_story_can_read_it()
    {
        SidneyMachine sidney = Machine(out GameState state);

        sidney.Scan("POUSSIN_POSTCARD");
        sidney.OpenForTranslation(sidney.Files[0]);
        sidney.From = "Latin";
        sidney.Translate();
        sidney.Complete(yes: true);
        sidney.Typed = "Sum";
        sidney.Append();

        Assert.True(state.GetFlag("SidneyText:ArcSUMText"));

        // And under the names the game itself asks about: R25307A will not end its
        // timeblock without SavedArcadiaText, and three conditions read ArcadiaComplete.
        Assert.True(state.GetFlag("SavedArcadiaText"));
        Assert.True(state.GetFlag("ArcadiaComplete"));
    }

    [Fact]
    public void A_file_with_nothing_to_translate_says_so_rather_than_nothing()
    {
        SidneyMachine sidney = Machine(out _);

        sidney.Scan("PARCHMENT_1");
        sidney.OpenForTranslation(sidney.Files[0]);
        sidney.From = "Latin";

        Assert.Contains("unable to translate", sidney.Translate().Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Mail_is_unread_until_it_is_opened_and_then_stays_read()
    {
        // The original's NEW E-MAIL light had nothing behind it here to turn it off.
        SidneyMachine sidney = Machine(out GameState state);

        Assert.Equal(2, sidney.Unread);

        sidney.ReadMail(sidney.Library.Mail()[0]);

        Assert.Equal(1, sidney.Unread);
        Assert.True(sidney.HasRead(sidney.Library.Mail()[0]));

        // A flag on the story, so a save keeps it.
        Assert.True(state.GetFlag("SidneyRead:EMail1"));

        sidney.ReadMail(null);

        Assert.Equal(1, sidney.Unread);
    }

    [Fact]
    public void A_message_offers_a_sender_and_a_date_a_list_can_show()
    {
        IReadOnlyList<SidneyMail> inbox = Machine(out _).Library.Mail();

        Assert.Equal("RT Nakimura", inbox[0].Sender);
        Assert.Equal("Jul 1", inbox[0].When);
        Assert.Equal("Jul 1, 1998, 7:25am", inbox[0].Date);

        // The joke in the sixth address survives: s.pam, not "s pam".
        Assert.Equal("s.pam", inbox[1].Sender);
    }
}
