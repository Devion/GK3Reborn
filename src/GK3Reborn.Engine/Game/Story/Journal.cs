// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using GK3Reborn.Game;

namespace GK3Reborn.Game.Story;

/// <summary>How an objective stands.</summary>
/// <param name="Quest">The objective.</param>
/// <param name="Done">Whether it has been achieved.</param>
/// <param name="Progress">How far through it the player is, from nought to one.</param>
/// <param name="Hints">
/// The hints already asked for, in order. Empty until the player asks, and never longer than
/// the objective has to give.
/// </param>
/// <param name="MoreHints">Whether there is another hint to ask for.</param>
public sealed record JournalEntry(
    Quest Quest,
    bool Done,
    float Progress,
    IReadOnlyList<string> Hints,
    bool MoreHints);

/// <summary>A day's worth of the journal.</summary>
/// <param name="Day">Which day.</param>
/// <param name="Chapters">Its points in the story, earliest first.</param>
public sealed record JournalDay(int Day, IReadOnlyList<JournalChapter> Chapters);

/// <summary>One point in the story, as the journal shows it.</summary>
/// <param name="Timeblock">Which one.</param>
/// <param name="Title">What to call it — "Day 1, 10 AM".</param>
/// <param name="Current">Whether it is the one the player is in.</param>
/// <param name="Past">Whether the story has moved beyond it.</param>
/// <param name="Entries">Its objectives.</param>
public sealed record JournalChapter(
    Timeblock Timeblock,
    string Title,
    bool Current,
    bool Past,
    IReadOnlyList<JournalEntry> Entries)
{
    /// <summary>How many of its objectives are done.</summary>
    public int Achieved => Entries.Count(e => e.Done);

    /// <summary>How many it has.</summary>
    public int Total => Entries.Count;
}

/// <summary>
/// The quest log.
/// </summary>
/// <remarks>
/// <para>
/// The original game has none, and the port is not obliged to reproduce that. What it
/// reproduces instead is 1999's problem: a player who has done everything they can think of,
/// with no way to find out what the game is waiting for, and an evening lost to walking
/// between the same four rooms. <c>Plan/03</c> section 3 asks for an interface easier than
/// the original's, and knowing where you are in a story is most of what that means.
/// </para>
/// <para>
/// <b>Two levels, and the player chooses which.</b> The journal says what to do and never
/// how — see <see cref="Quests"/>. A player who is stuck asks for a hint, and gets one line
/// of the walkthrough at a time. Nothing from the walkthrough is ever shown unasked, because
/// several of these puzzles are the best things in the game.
/// </para>
/// <para>
/// <b>Nothing here is state.</b> What is done is read from the score events the story has
/// already recorded, so the journal cannot drift out of step with the game and there is
/// nothing to migrate when it changes. The one thing it does own is which hints have been
/// asked for, which is a player's own business and is saved with the game.
/// </para>
/// </remarks>
public sealed class Journal
{
    private readonly Quests _quests;
    private readonly Walkthrough _walkthrough;
    private readonly GameState _story;

    /// <summary>Builds a journal over a game in progress.</summary>
    /// <param name="story">The game.</param>
    /// <param name="quests">The objectives, or the shipped table.</param>
    /// <param name="walkthrough">The hints, or the shipped walkthrough.</param>
    public Journal(GameState story, Quests? quests = null, Walkthrough? walkthrough = null)
    {
        ArgumentNullException.ThrowIfNull(story);

        _story = story;
        _quests = quests ?? Quests.Open();
        _walkthrough = walkthrough ?? Walkthrough.Open();
    }

    /// <summary>The whole journal, by day.</summary>
    /// <param name="includeFuture">
    /// Whether to include the points in the story the player has not reached. False by
    /// default and by every sensible default: a list of what is coming is a table of
    /// contents for the plot.
    /// </param>
    /// <returns>The days, earliest first.</returns>
    public IReadOnlyList<JournalDay> Read(bool includeFuture = false)
    {
        List<JournalChapter> chapters = [];

        foreach (Timeblock timeblock in _quests.Timeblocks)
        {
            bool past = timeblock < _story.Timeblock;
            bool current = timeblock == _story.Timeblock;

            if (!past && !current && !includeFuture)
            {
                continue;
            }

            chapters.Add(new JournalChapter(
                timeblock,
                Name(timeblock),
                current,
                past,
                [.. _quests.Of(timeblock).Select(q => Entry(q, past))]));
        }

        return
        [
            .. chapters
                .GroupBy(c => c.Timeblock.Day)
                .OrderBy(g => g.Key)
                .Select(g => new JournalDay(g.Key, [.. g])),
        ];
    }

    /// <summary>What the player should be doing now.</summary>
    /// <returns>The unfinished objectives of the current point in the story.</returns>
    /// <remarks>
    /// What a corner of the screen would show, and what the journal opens on. Finished
    /// objectives are left out here and kept in <see cref="Read"/>, because the question
    /// this answers is "what now" and the question that answers is "what have I done".
    /// </remarks>
    public IReadOnlyList<JournalEntry> Now() =>
    [
        .. _quests.Of(_story.Timeblock)
            .Select(q => Entry(q, past: false))
            .Where(e => !e.Done),
    ];

    /// <summary>Whether the player has anything left to do at this point in the story.</summary>
    public bool Adrift => Now().Count == 0 && _quests.Of(_story.Timeblock).Count > 0;

    /// <summary>
    /// Asks for one more hint about an objective.
    /// </summary>
    /// <param name="quest">The objective.</param>
    /// <returns>The hint, or null when there are none left.</returns>
    /// <remarks>
    /// One line at a time and always the next one. A player who is a little stuck usually
    /// needs the first, which says where to go; the one that gives a puzzle away is further
    /// down, and reaching it should take asking again.
    /// </remarks>
    public string? Reveal(Quest quest)
    {
        ArgumentNullException.ThrowIfNull(quest);

        IReadOnlyList<string> lines = Lines(quest);
        int shown = _story.HintsAsked(Key(quest));

        if (shown >= lines.Count)
        {
            return null;
        }

        _story.AskedForHint(Key(quest));
        return lines[shown];
    }

    /// <summary>How an objective stands.</summary>
    private JournalEntry Entry(Quest quest, bool past)
    {
        IReadOnlyList<string> lines = Lines(quest);
        int shown = Math.Min(_story.HintsAsked(Key(quest)), lines.Count);

        return new JournalEntry(
            quest,
            quest.Done(_story.HasScored, past),
            quest.Progress(_story.HasScored, past),
            [.. lines.Take(shown)],
            shown < lines.Count);
    }

    /// <summary>The walkthrough lines an objective points at.</summary>
    /// <remarks>
    /// By position within the point in the story, which is how the table writes them: a step
    /// number is only meaningful next to the timeblock it belongs to, and numbering the whole
    /// file would make every hint in the game shift when one line is added at the top.
    /// </remarks>
    private IReadOnlyList<string> Lines(Quest quest)
    {
        IReadOnlyList<WalkthroughStep> steps = _walkthrough.Of(quest.Timeblock);

        return
        [
            .. quest.Hints
                .Where(n => n >= 1 && n <= steps.Count)
                .Select(n => steps[n - 1].Text),
        ];
    }

    /// <summary>
    /// The objective a journal key names.
    /// </summary>
    /// <param name="key">What <see cref="Key"/> produced.</param>
    /// <returns>The objective, or null when the table no longer has one by that name.</returns>
    /// <remarks>
    /// How a click on the page becomes an objective again. Null rather than an exception when
    /// nothing matches, because the table is data and a click is a frame behind the state it
    /// was drawn from.
    /// </remarks>
    public Quest? Find(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        return _quests.All.FirstOrDefault(
            q => string.Equals(Key(q), key, StringComparison.Ordinal));
    }

    /// <summary>What an objective is filed under, for remembering its hints.</summary>
    /// <remarks>
    /// The timeblock and the title. Not the title alone, because "Collect your things" is an
    /// objective on three separate mornings and a player who asked about one has not asked
    /// about the others.
    /// </remarks>
    public static string Key(Quest quest) => $"{quest.Timeblock}|{quest.Title}";

    /// <summary>What to call a point in the story.</summary>
    private static string Name(Timeblock timeblock)
    {
        int hour = timeblock.Hour == 0 ? 12 : timeblock.Hour;

        return $"Day {timeblock.Day}, {hour} {(timeblock.IsAfternoon ? "PM" : "AM")}";
    }
}
