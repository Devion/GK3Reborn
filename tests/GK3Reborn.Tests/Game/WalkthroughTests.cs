using GK3Reborn.Game;
using GK3Reborn.Game.Story;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Tests for reading the walkthrough the journal is built out of.
/// </summary>
/// <remarks>
/// The file is the one piece of story data in the project that nothing else can check: a
/// scene file is checked by loading the scene, and a score name is checked against the
/// corpus, but a walkthrough is prose. What it does carry is a running total beside every
/// scored line, and that is enough to catch a line read twice or missed.
/// </remarks>
public sealed class WalkthroughTests
{
    [Fact]
    public void The_walkthrough_the_engine_ships_can_be_read()
    {
        Walkthrough guide = Walkthrough.Open();

        Assert.NotEmpty(guide.Steps);
        Assert.True(guide.Steps.Count > 250, $"only {guide.Steps.Count} steps were read");
    }

    /// <summary>Every running total is the sum of the points before it.</summary>
    [Fact]
    public void The_running_totals_agree_with_the_points()
    {
        Assert.True(Walkthrough.Open().Adds(out string? fault), fault);
    }

    /// <summary>Every point in the story the game has is walked through.</summary>
    /// <remarks>
    /// Seventeen headings, one per timeblock, in the order the story runs. A missing one is
    /// a stretch of the game the journal would have nothing to say about.
    /// </remarks>
    [Fact]
    public void Every_point_in_the_story_is_covered()
    {
        IReadOnlyList<Timeblock> covered = Walkthrough.Open().Timeblocks;

        Assert.Equal(17, covered.Count);
        Assert.Equal([.. covered.Order()], covered);
        Assert.Equal(new Timeblock(1, 10, false), covered[0]);
        Assert.Equal(new Timeblock(3, 9, true), covered[^1]);
    }

    /// <summary>A step keeps the location of the one above it when it names none.</summary>
    [Fact]
    public void A_step_that_names_no_location_stays_where_the_last_one_was()
    {
        Walkthrough guide = Walkthrough.Parse(
            "Location\tAction\tPoints\n" +
            "\n" +
            "Day 1: 10 AM - 12 PM\n" +
            "\n" +
            "Lobby\tAsk Jean about the two men.\t2/2\n" +
            "Look at the log book.\t2/4\n" +
            "Go outside\n");

        Assert.Equal(3, guide.Steps.Count);
        Assert.All(guide.Steps, s => Assert.Equal("Lobby", s.Location));
        Assert.Equal(new Timeblock(1, 10, false), guide.Steps[0].Timeblock);
    }

    /// <summary>Two fields with no score are a location and an action, not an action and one.</summary>
    [Fact]
    public void A_location_with_an_unscored_action_is_read_as_both()
    {
        Walkthrough guide = Walkthrough.Parse(
            "Day 1: 10 AM - 12 PM\n" +
            "Room 33\tKnock on the door and enter.\n");

        WalkthroughStep only = Assert.Single(guide.Steps);

        Assert.Equal("Room 33", only.Location);
        Assert.Equal("Knock on the door and enter.", only.Text);
        Assert.False(only.Scores);
    }

    /// <summary>A heading gives the day, the hour and which half of the day it is.</summary>
    [Theory]
    [InlineData("Day 1: 10 AM - 12 PM", 1, 10, false)]
    [InlineData("Day 1: 2 PM - 4 PM", 1, 2, true)]
    [InlineData("Day 2: 7 AM - 10 AM (Grace)", 2, 7, false)]
    [InlineData("Day 3: 9 PM - Midnight (Gabriel)", 3, 9, true)]
    public void A_heading_names_a_point_in_the_story(
        string heading, int day, int hour, bool afternoon)
    {
        Walkthrough guide = Walkthrough.Parse(heading + "\nSomewhere\tDo a thing.\n");

        Assert.Equal(new Timeblock(day, hour, afternoon), Assert.Single(guide.Steps).Timeblock);
    }
}
