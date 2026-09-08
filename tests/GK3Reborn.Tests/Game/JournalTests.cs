using GK3Reborn.Game;
using GK3Reborn.Game.Story;
using GK3Reborn.UI;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Tests for the quest log.
/// </summary>
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

    /// <summary>Every line of both tables has a key, and it is the line's own position.</summary>
    [Fact]
    public void Every_objective_and_every_line_is_numbered_by_where_it_comes()
    {
        foreach (Timeblock timeblock in Table.Timeblocks)
        {
            IReadOnlyList<Quest> quests = Table.Of(timeblock);

            for (int at = 0; at < quests.Count; at++)
            {
                Assert.Equal(at + 1, quests[at].Ordinal);
                Assert.Equal($"quest.{timeblock}.{at + 1}", quests[at].TitleKey);
            }

            IReadOnlyList<WalkthroughStep> steps = Guide.Of(timeblock);

            for (int at = 0; at < steps.Count; at++)
            {
                Assert.Equal(at + 1, steps[at].Ordinal);
                Assert.Equal($"hint.{timeblock}.{at + 1}", steps[at].TextKey);
            }
        }
    }

    /// <summary>The heading over a point in the story is the game's own words for it.</summary>
    [Fact]
    public void The_heading_over_a_point_in_the_story_comes_from_the_string_table()
    {
        var story = new GameState { Timeblock = new Timeblock(1, 10, false) };

        Assert.Equal(
            "Day 1, 10 AM",
            new Journal(story, Table, Guide).Read()[0].Chapters[0].Title);

        var journal = new Journal(story, Table, Guide)
        {
            Names = GameStrings.Parse("Day110a = Jour 1, 10.00 - 12.00\n"),
        };

        Assert.Equal("Jour 1, 10.00 - 12.00", journal.Read()[0].Chapters[0].Title);
    }

    /// <summary>An objective and its hints read in the language the game is being played in.</summary>
    [Fact]
    public void The_journal_reads_in_the_players_own_language()
    {
        var story = new GameState { Timeblock = new Timeblock(1, 10, false) };
        var journal = new Journal(story, Table, Guide) { Text = UiText.Carried("fr") };

        Quest telephone = Table.Of(new Timeblock(1, 10, false))
            .Single(q => q.Title.StartsWith("Telephone", StringComparison.Ordinal));

        JournalEntry entry = journal.Now().Single(e => e.Quest == telephone);

        Assert.NotEqual(telephone.Title, entry.Title);
        Assert.Equal(UiText.Carried("fr").Say(telephone.TitleKey, "?"), entry.Title);

        // And the hint behind it, which is the other half and the longer one.
        string? hint = journal.Reveal(telephone);

        Assert.NotNull(hint);
        Assert.NotEqual(Guide.Of(telephone.Timeblock)[telephone.Hints[0] - 1].Text, hint);
    }

    /// <summary>What a save files a hint under does not move with the language.</summary>
    [Fact]
    public void Asking_for_a_hint_is_remembered_whatever_language_it_was_asked_in()
    {
        var story = new GameState { Timeblock = new Timeblock(1, 10, false) };
        Quest first = Table.Of(new Timeblock(1, 10, false))[0];

        new Journal(story, Table, Guide) { Text = UiText.Carried("de") }.Reveal(first);

        var english = new Journal(story, Table, Guide);

        Assert.Single(english.Now().Single(e => e.Quest == first).Hints);
        Assert.Equal(1, story.HintsAsked(JournalKey(first)));
    }

    /// <summary>Which score events were earned survives a save, which it never used to.</summary>
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
