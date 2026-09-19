using GK3Reborn.Game;
using GK3Reborn.Game.Sidney;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Tests for the inscription Sidney lifts off Poussin's tomb.
/// </summary>
public sealed class ArcadiaTextTests
{
    /// <summary>The rows the two screens read, as every release writes them.</summary>
    private const string Text = """
        [Analyze Screen]
        ArcadiaText     = Et in Arcadia Ego Sum
        ArcadiaText2    = Et in Arcadia Ego
        SaveArcadia     = Do you want to save the text to a new file?
        SavingArcadia   = Text saved to a new file.
        YesButton       = YES
        NoButton        = NO

        [Translate Screen]
        Latin           = Latin
        ArcadiaText1    = Et in Arcadia Ego
        ArcadiaTextT1   = And (while) in Arcadia I...
        ArcSUMText1     = Et in Arcadia Ego Sum
        ArcSUMTextT1    = I am also (even) in Arcadia.
        Subject         = INCOMPLETE SENTENCE.
        Question        = Do you want to add text?
        Yes             = YES
        No              = NO
        """;

    private static SidneyMachine Machine(out GameState state)
    {
        state = new GameState { Ego = "GRACE" };

        return new SidneyMachine(SidneyLibrary.From(Text), state);
    }

    [Fact]
    public void The_saved_inscription_is_a_file_of_its_own()
    {
        // The original keeps the zoom's text in a file and offers the translate screen and
        // the anagram parser on that; the port kept it on the painting it came off, so the
        // only way in was to open a postcard and remember why. Reported as such.
        SidneyMachine sidney = Machine(out GameState state);

        Assert.DoesNotContain(sidney.Files, f => f.Id == SidneyFiles.ArcadiaFile);

        state.SetFlag("SavedArcadiaText");

        SidneyFile file = Assert.Single(sidney.Files, f => f.Id == SidneyFiles.ArcadiaFile);

        Assert.Equal("Et in Arcadia Ego", file.Label);
        Assert.True(sidney.Translator.CanTranslate(file));

        // And the parser hangs off it, which is what the file is for.
        sidney.OpenFile(file);
        Assert.Contains(SidneyAction.AnagramParser, sidney.Available());
    }

    [Fact]
    public void Its_name_gains_the_missing_word_once_the_sentence_is_finished()
    {
        SidneyMachine sidney = Machine(out GameState state);

        state.SetFlag("SavedArcadiaText");
        state.SetFlag("ArcadiaComplete");

        Assert.Equal(
            "Et in Arcadia Ego Sum",
            Assert.Single(sidney.Files, f => f.Id == SidneyFiles.ArcadiaFile).Label);
    }

    [Fact]
    public void Opening_it_offers_the_missing_word_while_it_is_still_latin()
    {
        // "Sum" is a Latin word and the sentence it finishes is in Latin. The machine made
        // the offer only after the English had been asked for, so a player reading the
        // inscription in the language it is written in was never invited. Reported as such.
        SidneyMachine sidney = Machine(out GameState state);

        state.SetFlag("SavedArcadiaText");
        sidney.OpenForTranslation(sidney.Files.Single(f => f.Id == SidneyFiles.ArcadiaFile));

        Assert.NotNull(sidney.Showing);
        Assert.Contains("Et in Arcadia Ego", sidney.Showing!.Text, StringComparison.Ordinal);
        Assert.Contains("INCOMPLETE SENTENCE.", sidney.Showing.Text, StringComparison.Ordinal);
        Assert.Equal("Do you want to add text?", sidney.Showing.Asks);
        Assert.Equal(["YES", "NO"], sidney.Showing.Choices!.Select(c => c.Text));

        // The English is still there for whoever asks for it, and still asks the same.
        sidney.From = "Latin";
        Assert.Contains(
            "And (while) in Arcadia I...", sidney.Translate().Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_finished_sentence_is_not_asked_about_again()
    {
        SidneyMachine sidney = Machine(out GameState state);

        state.SetFlag("SavedArcadiaText");
        state.SetFlag("ArcadiaComplete");
        sidney.OpenForTranslation(sidney.Files.Single(f => f.Id == SidneyFiles.ArcadiaFile));

        Assert.Null(sidney.Showing);

        // And it translates as the whole sentence rather than as the half of it the player
        // has already solved.
        sidney.From = "Latin";

        Assert.Equal("I am also (even) in Arcadia.", sidney.Translate().Text);
    }

    [Fact]
    public void Gabriel_leaves_it_to_grace_whichever_file_it_is_in()
    {
        SidneyMachine sidney = Machine(out GameState state);

        state.SetFlag("SavedArcadiaText");
        state.Ego = "GABRIEL";

        sidney.OpenForTranslation(sidney.Files.Single(f => f.Id == SidneyFiles.ArcadiaFile));

        Assert.Null(sidney.Translating);
    }
}
