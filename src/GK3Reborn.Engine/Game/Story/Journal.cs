// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using GK3Reborn.Game;
using GK3Reborn.UI;

namespace GK3Reborn.Game.Story;

/// <summary>How an objective stands.</summary>
/// <param name="Quest">The objective.</param>
/// <param name="Title">
/// What to draw for it, in the language the game is being played in.
/// </param>
/// <param name="Done">Whether it has been achieved.</param>
/// <param name="Progress">How far through it the player is, from nought to one.</param>
/// <param name="Hints">
/// The hints already asked for, in order and in the player's own language. Empty until they
/// ask, and never longer than the objective has to give.
/// </param>
/// <param name="MoreHints">Whether there is another hint to ask for.</param>
public sealed record JournalEntry(
    Quest Quest,
    string Title,
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
public sealed class Journal
{
    private readonly Quests _quests;
    private readonly Walkthrough _walkthrough;
    private readonly GameState _story;

    /// <summary>
    /// The port's own words, in the language being played.
    /// </summary>
    public UiText Text { get; init; } = UiText.English;

    /// <summary>
    /// GK3's own string table, for the heading over each point in the story.
    /// </summary>
    public GameStrings? Names { get; init; }

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
    public string? Reveal(Quest quest)
    {
        ArgumentNullException.ThrowIfNull(quest);

        IReadOnlyList<WalkthroughStep> lines = Steps(quest);
        int shown = _story.HintsAsked(Key(quest));

        if (shown >= lines.Count)
        {
            return null;
        }

        _story.AskedForHint(Key(quest));

        return Text.Say(lines[shown].TextKey, lines[shown].Text);
    }

    /// <summary>How an objective stands.</summary>
    private JournalEntry Entry(Quest quest, bool past)
    {
        IReadOnlyList<string> lines = Said(quest);
        int shown = Math.Min(_story.HintsAsked(Key(quest)), lines.Count);

        return new JournalEntry(
            quest,
            Text.Say(quest.TitleKey, quest.Title),
            quest.Done(_story.HasScored, past),
            quest.Progress(_story.HasScored, past),
            [.. lines.Take(shown)],
            shown < lines.Count);
    }

    /// <summary>The lines an objective points at, in the language being played.</summary>
    private IReadOnlyList<string> Said(Quest quest) =>
        [.. Steps(quest).Select(step => Text.Say(step.TextKey, step.Text))];

    /// <summary>The walkthrough lines an objective points at.</summary>
    private IReadOnlyList<WalkthroughStep> Steps(Quest quest)
    {
        IReadOnlyList<WalkthroughStep> steps = _walkthrough.Of(quest.Timeblock);

        return
        [
            .. quest.Hints
                .Where(n => n >= 1 && n <= steps.Count)
                .Select(n => steps[n - 1]),
        ];
    }

    /// <summary>
    /// The objective a journal key names.
    /// </summary>
    /// <param name="key">What <see cref="Key"/> produced.</param>
    /// <returns>The objective, or null when the table no longer has one by that name.</returns>
    public Quest? Find(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        return _quests.All.FirstOrDefault(
            q => string.Equals(Key(q), key, StringComparison.Ordinal));
    }

    /// <summary>What an objective is filed under, for remembering its hints.</summary>
    public static string Key(Quest quest) => $"{quest.Timeblock}|{quest.Title}";

    /// <summary>What to call a point in the story.</summary>
    private string Name(Timeblock timeblock)
    {
        if (Names?.When(timeblock.ToString()) is { Length: > 0 } called)
        {
            return called;
        }

        int hour = timeblock.Hour == 0 ? 12 : timeblock.Hour;

        return $"Day {timeblock.Day}, {hour} {(timeblock.IsAfternoon ? "PM" : "AM")}";
    }
}
