using GK3Reborn.Game;
using GK3Reborn.Game.Story;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Tests for the quest log.
/// </summary>
/// <remarks>
/// Most of these check the shipped table rather than the code that reads it, and that is
/// deliberate. An objective naming a score event that does not exist can never be completed,
/// so the journal would tell a player to do something and then never admit they had done it
/// — which is worse than having no journal at all. A typo has to fail a build.
/// </remarks>
public sealed class JournalTests
{
    private static readonly Quests Table = Quests.Open();
    private static readonly Walkthrough Guide = Walkthrough.Open();
    private static readonly ScoreEvents Points = ScoreEvents.Open();

    [Fact]
    public void The_table_the_engine_ships_can_be_read()
    {
        Assert.NotEmpty(Table.All);
        Assert.True(Table.All.Count > 100, $"only {Table.All.Count} objectives were read");
    }

    /// <summary>Every score event an objective names is a real one.</summary>
    [Fact]
    public void Every_objective_is_measured_by_a_score_event_that_exists()
    {
        List<string> missing =
        [
            .. Table.All
                .SelectMany(q => q.Scores.Select(s => (q, s)))
                .Where(pair => Points.Worth(pair.s) is null)
                .Select(pair => $"{pair.q.Timeblock} \"{pair.q.Title}\" names {pair.s}"),
        ];

        Assert.Empty(missing);
    }

    /// <summary>An objective's score events belong to the point in the story it is filed under.</summary>
    /// <remarks>
    /// A score event carries its timeblock in its name, so this catches an objective written
    /// under the wrong heading — which would show a player the right thing to do on the wrong
    /// day, and never tick it off.
    /// </remarks>
    [Fact]
    public void Every_objective_is_filed_under_the_day_its_events_belong_to()
    {
        List<string> wrong =
        [
            .. Table.All
                .SelectMany(q => q.Scores.Select(s => (q, s)))
                .Where(pair =>
                    ScoreEvents.TimeblockOf(pair.s) is { } when &&
                    when != pair.q.Timeblock &&

                    // Two exceptions, written down rather than waved through. The abbe's
                    // telephone call can be recorded in either half of the evening, and the
                    // score table spells the two under different blocks.
                    pair.q.Test != QuestTest.Any &&

                    // And the score table has no 306P prefix at all: Day 3's evening, which
                    // is Grace's, is filed under 303P with Gabriel's afternoon. That is the
                    // shipped data's own arrangement and the journal follows it rather than
                    // renaming events to suit itself.
                    !(pair.q.Timeblock == new Timeblock(3, 6, true) &&
                      when == new Timeblock(3, 3, true)))
                .Select(pair => $"{pair.q.Timeblock} \"{pair.q.Title}\" names {pair.s}"),
        ];

        Assert.Empty(wrong);
    }

    /// <summary>Every hint an objective points at is a line the walkthrough has.</summary>
    [Fact]
    public void Every_hint_points_at_a_line_that_exists()
    {
        List<string> missing = [];

        foreach (Quest quest in Table.All)
        {
            int available = Guide.Of(quest.Timeblock).Count;

            missing.AddRange(quest.Hints
                .Where(n => n < 1 || n > available)
                .Select(n =>
                    $"{quest.Timeblock} \"{quest.Title}\" wants line {n} of {available}"));
        }

        Assert.Empty(missing);
    }

    /// <summary>Every objective can be finished somehow.</summary>
    /// <remarks>
    /// A <c>score:</c> condition with no events would never be true, which is the one way to
    /// write an objective that is impossible rather than merely hard.
    /// </remarks>
    [Fact]
    public void No_objective_is_impossible()
    {
        List<string> stuck =
        [
            .. Table.All
                .Where(q => q.Test != QuestTest.Story && q.Scores.Count == 0)
                .Select(q => $"{q.Timeblock} \"{q.Title}\""),
        ];

        Assert.Empty(stuck);
    }

    /// <summary>Every point in the story the walkthrough covers has objectives.</summary>
    [Fact]
    public void Every_point_in_the_story_has_something_to_do()
    {
        Assert.Equal(Guide.Timeblocks, Table.Timeblocks);
    }

    /// <summary>No objective gives the answer away in its title.</summary>
    /// <remarks>
    /// A crude check and worth having anyway. The titles are the one thing shown unasked, and
    /// the walkthrough's own giveaway words — the mechanics of a puzzle rather than its aim —
    /// have no business in them. It has caught two.
    /// </remarks>
    [Theory]
    [InlineData("dumbwaiter")]
    [InlineData("combine")]
    [InlineData("coordinate")]
    [InlineData("anagram")]
    [InlineData("pentagram")]
    [InlineData("hexagram")]
    public void No_objective_title_gives_a_puzzle_away(string giveaway)
    {
        List<string> loose =
        [
            .. Table.All
                .Where(q => q.Title.Contains(giveaway, StringComparison.OrdinalIgnoreCase))
                .Select(q => $"{q.Timeblock} \"{q.Title}\""),
        ];

        Assert.Empty(loose);
    }

    /// <summary>The journal shows nothing of a point in the story the player has not reached.</summary>
    /// <remarks>
    /// A list of what is coming is a table of contents for the plot, which is a spoiler of a
    /// larger kind than any single hint.
    /// </remarks>
    [Fact]
    public void The_journal_does_not_show_what_has_not_happened_yet()
    {
        var story = new GameState { Timeblock = new Timeblock(1, 10, false) };
        var journal = new Journal(story);

        IReadOnlyList<Timeblock> shown =
        [
            .. journal.Read().SelectMany(d => d.Chapters).Select(c => c.Timeblock),
        ];

        Assert.Equal([new Timeblock(1, 10, false)], shown);
    }

    /// <summary>An objective ticks itself off when its score events are earned.</summary>
    [Fact]
    public void An_objective_is_done_once_its_events_are_earned()
    {
        var story = new GameState { Timeblock = new Timeblock(1, 10, false) };
        var journal = new Journal(story);

        Assert.Contains(journal.Now(), e => e.Quest.Title.StartsWith("Telephone", StringComparison.Ordinal));

        story.AwardScore("e_110a_pho_phone_prince_james", Points.Worth("e_110a_pho_phone_prince_james"));

        Assert.DoesNotContain(
            journal.Now(), e => e.Quest.Title.StartsWith("Telephone", StringComparison.Ordinal));
    }

    /// <summary>An objective of several parts reports how far through it the player is.</summary>
    [Fact]
    public void A_part_finished_objective_reports_its_progress()
    {
        var story = new GameState { Timeblock = new Timeblock(1, 10, false) };
        var journal = new Journal(story);

        story.AwardScore("e_110a_r25_tape", Points.Worth("e_110a_r25_tape"));

        JournalEntry entry = journal.Now().Single(e => e.Quest.Scores.Contains("e_110a_r25_tape"));

        Assert.False(entry.Done);
        Assert.Equal(0.5f, entry.Progress, 3);
    }

    /// <summary>Hints arrive one at a time and only when asked for.</summary>
    [Fact]
    public void Hints_are_given_out_one_at_a_time()
    {
        var story = new GameState { Timeblock = new Timeblock(1, 10, false) };

        // The journal is given the same tables the test looks objectives up in. A Quest
        // carries lists, and a record compares those by reference, so an objective read from
        // a second copy of the file is never the same object as one read from the first.
        var journal = new Journal(story, Table, Guide);

        Quest telephone = Table.Of(new Timeblock(1, 10, false))
            .Single(q => q.Title.StartsWith("Telephone", StringComparison.Ordinal));

        Assert.Empty(journal.Now().Single(e => e.Quest == telephone).Hints);

        string? first = journal.Reveal(telephone);

        Assert.NotNull(first);
        Assert.Single(journal.Now().Single(e => e.Quest == telephone).Hints);

        // And it runs out rather than repeating itself.
        while (journal.Reveal(telephone) is not null)
        {
        }

        Assert.False(journal.Now().Single(e => e.Quest == telephone).MoreHints);
    }

    /// <summary>Which hints were asked for survives a save.</summary>
    [Fact]
    public void Asking_for_a_hint_is_remembered_across_a_save()
    {
        var story = new GameState { Timeblock = new Timeblock(1, 10, false) };
        var journal = new Journal(story, Table, Guide);

        Quest first = Table.Of(new Timeblock(1, 10, false))[0];

        journal.Reveal(first);
        story.AwardScore("e_110a_r25_tape", Points.Worth("e_110a_r25_tape"));

        var loaded = new GameState();
        loaded.Restore(story.Capture());

        Assert.Equal(1, loaded.HintsAsked(JournalKey(first)));
        Assert.True(loaded.HasScored("e_110a_r25_tape"));
    }

    /// <summary>What an objective is filed under, mirroring the journal's own key.</summary>
    private static string JournalKey(Quest quest) => $"{quest.Timeblock}|{quest.Title}";

    /// <summary>Which score events were earned survives a save, which it never used to.</summary>
    /// <remarks>
    /// A save carried the player's total and never what made it up, so loading one and doing
    /// the same thing again paid for it twice. The journal is what made that visible.
    /// </remarks>
    [Fact]
    public void An_event_already_earned_does_not_score_again_after_loading()
    {
        var story = new GameState();

        story.AwardScore("e_110a_r25_tape", Points.Worth("e_110a_r25_tape"));

        var loaded = new GameState();
        loaded.Restore(story.Capture());

        int before = loaded.Score;
        loaded.AwardScore("e_110a_r25_tape", Points.Worth("e_110a_r25_tape"));

        Assert.Equal(before, loaded.Score);
    }
}
