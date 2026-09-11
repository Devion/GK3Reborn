using GK3Reborn.Game;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Tests for the lobby's two dirty glasses, and whose print comes off which.
/// </summary>
public sealed class DirtyGlassesTests
{
    private static GameState Afternoon() =>
        new() { Ego = "GABRIEL", Timeblock = new Timeblock(2, 2, IsAfternoon: true), Location = "LBY" };

    [Fact]
    public void Only_the_two_glasses_are_a_question()
    {
        Assert.Null(DirtyGlasses.Dust("HAND_MIRROR", Afternoon()));
        Assert.NotNull(DirtyGlasses.Dust("dirty_glass_wilkes", Afternoon()));
    }

    /// <summary>
    /// With Buchelli's print off his suitcase there is nothing to ask: Wilkes's glass is
    /// recognised by not matching it, Buchelli's by matching it, and the variable the
    /// lobby's scripts read says both were done.
    /// </summary>
    [Fact]
    public void The_suitcase_print_settles_both_glasses_on_sight()
    {
        GameState story = Afternoon();
        story.SetFlag("GotSuitcaseBuchelliPrint");

        GlassDusting wilkes = DirtyGlasses.Dust(DirtyGlasses.WilkesGlass, story)!;

        Assert.True(wilkes.Lifts);
        Assert.Null(wilkes.Asks);
        Assert.Equal("1EK4259NS1", wilkes.Says);
        Assert.Equal(2, story.GetVariable(DirtyGlasses.Variable));

        GlassDusting buchelli = DirtyGlasses.Dust(DirtyGlasses.BuchelliGlass, story)!;

        Assert.False(buchelli.Lifts);
        Assert.Null(buchelli.Mislabels);
        Assert.Equal("1EK0259NS1", buchelli.Says);
        Assert.Equal(5, story.GetVariable(DirtyGlasses.Variable));
    }

    /// <summary>
    /// Without it, the first glass is a question and nothing is written until it is
    /// answered — so a bar put away leaves the glass to be dusted again.
    /// </summary>
    [Fact]
    public void The_first_glass_asks_and_writes_nothing_until_answered()
    {
        GameState story = Afternoon();

        GlassDusting first = DirtyGlasses.Dust(DirtyGlasses.BuchelliGlass, story)!;

        Assert.Equal(DirtyGlasses.Wondering, first.Says);
        Assert.Equal(DirtyGlasses.BuchelliQuestion, first.Asks);
        Assert.False(first.Lifts);
        Assert.Equal(0, story.GetVariable(DirtyGlasses.Variable));

        Assert.Equal(DirtyGlasses.WilkesQuestion, DirtyGlasses.Dust(DirtyGlasses.WilkesGlass, story)!.Asks);
    }

    /// <summary>The right name lifts the print; the second glass is then deduced.</summary>
    [Fact]
    public void The_right_answer_lifts_the_print_and_settles_the_other_glass()
    {
        GameState story = Afternoon();
        DirtyGlasses.Dust(DirtyGlasses.WilkesGlass, story);

        GlassDusting answered = DirtyGlasses.Answer(DirtyGlasses.WilkesGlass, DirtyGlasses.SaidWilkes, story);

        Assert.True(answered.Lifts);
        Assert.Null(answered.Mislabels);
        Assert.Equal(DirtyGlasses.Conceding, answered.Says);
        Assert.Equal(2, story.GetVariable(DirtyGlasses.Variable));

        GlassDusting other = DirtyGlasses.Dust(DirtyGlasses.BuchelliGlass, story)!;

        Assert.True(other.Lifts);
        Assert.Null(other.Asks);
        Assert.Equal(5, story.GetVariable(DirtyGlasses.Variable));
    }

    /// <summary>
    /// The wrong name pockets a print under the wrong name, and the deduction from it is
    /// wrong the same way, which is how the original punishes a guess.
    /// </summary>
    [Fact]
    public void The_wrong_answer_mislabels_both_prints()
    {
        GameState story = Afternoon();
        DirtyGlasses.Dust(DirtyGlasses.BuchelliGlass, story);

        GlassDusting answered = DirtyGlasses.Answer(DirtyGlasses.BuchelliGlass, DirtyGlasses.SaidWilkes, story);

        Assert.False(answered.Lifts);
        Assert.Equal(DirtyGlasses.MislabelledBuchelli, answered.Mislabels);
        Assert.Equal(3, story.GetVariable(DirtyGlasses.Variable));

        GlassDusting other = DirtyGlasses.Dust(DirtyGlasses.WilkesGlass, story)!;

        Assert.False(other.Lifts);
        Assert.Equal(DirtyGlasses.MislabelledWilkes, other.Mislabels);
        Assert.Equal(5, story.GetVariable(DirtyGlasses.Variable));
    }
}
