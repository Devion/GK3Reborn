using GK3Reborn.Game;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Tests for the one moment whose topics are answers rather than questions.
/// </summary>
public sealed class TopicWordingTests
{
    private static GameState Denouement(int question)
    {
        var state = new GameState { Timeblock = new Timeblock(3, 3, IsAfternoon: true) };

        state.SetVariable("DonutCurrentQuestion", question);

        return state;
    }

    [Fact]
    public void The_dining_room_at_303P_reads_its_topics_as_answers()
    {
        Assert.Equal(TopicWording.Denouement, TopicWording.For(Denouement(1), "DIN"));
        Assert.Equal(TopicWording.Denouement, TopicWording.For(Denouement(4), "din"));
    }

    [Fact]
    public void Before_the_first_question_and_after_the_last_they_are_questions_again()
    {
        // The game's own counter: nought until the denouement starts, five once the
        // fourth answer is in, and the rest of the scene is an ordinary conversation.
        Assert.Null(TopicWording.For(Denouement(0), "DIN"));
        Assert.Null(TopicWording.For(Denouement(5), "DIN"));
    }

    [Fact]
    public void No_other_room_and_no_other_hour_reads_them_that_way()
    {
        Assert.Null(TopicWording.For(Denouement(2), "LBY"));

        var elsewhen = new GameState { Timeblock = new Timeblock(3, 10, IsAfternoon: false) };

        elsewhen.SetVariable("DonutCurrentQuestion", 2);

        Assert.Null(TopicWording.For(elsewhen, "DIN"));
        Assert.Null(TopicWording.For(null, "DIN"));
        Assert.Null(TopicWording.For(Denouement(2), null));
    }
}
