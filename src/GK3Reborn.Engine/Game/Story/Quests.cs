// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using GK3Reborn.Game;

namespace GK3Reborn.Game.Story;

/// <summary>How an objective is known to be done.</summary>
public enum QuestTest
{
    /// <summary>Every score event named has been earned.</summary>
    Every,

    /// <summary>At least one of the score events named has been earned.</summary>
    Any,

    /// <summary>
    /// Nothing to detect. The game awards no points for it, so it counts as done once the
    /// story has moved past the point it belongs to.
    /// </summary>
    Story,
}

/// <summary>
/// One thing the player is trying to do.
/// </summary>
/// <param name="Timeblock">The point in the story it belongs to.</param>
/// <param name="Title">
/// What to do, in the journal's own words. <b>Never how.</b> Several of this game's puzzles
/// are the best things in it and printing the answer where nobody asked takes them away.
/// </param>
/// <param name="Test">How it is known to be done.</param>
/// <param name="Scores">The score events it is measured by, which may be none.</param>
/// <param name="Hints">
/// Which lines of the walkthrough answer "how", counting from one within this point in the
/// story. Shown one at a time and only on request.
/// </param>
public sealed record Quest(
    Timeblock Timeblock,
    string Title,
    QuestTest Test,
    IReadOnlyList<string> Scores,
    IReadOnlyList<int> Hints)
{
    /// <summary>Whether this objective has been achieved.</summary>
    /// <param name="scored">Whether a given score event has been earned.</param>
    /// <param name="past">Whether the story has moved past this point.</param>
    /// <returns>True when it is done.</returns>
    /// <remarks>
    /// A <see cref="QuestTest.Story"/> objective is done when its timeblock is behind the
    /// player, which is the honest answer for a beat the game measures nothing about: it was
    /// either done or it was not possible to leave.
    /// </remarks>
    public bool Done(Func<string, bool> scored, bool past)
    {
        ArgumentNullException.ThrowIfNull(scored);

        return Test switch
        {
            QuestTest.Every => Scores.Count > 0 && Scores.All(scored),
            QuestTest.Any => Scores.Any(scored),
            _ => past,
        };
    }

    /// <summary>How far through it the player is, from nought to one.</summary>
    /// <remarks>
    /// For an objective measured by several score events, which most are. It is what lets
    /// the journal say "asked about three of five" rather than only "not done", and that is
    /// most of the difference between a list that helps and a list that nags.
    /// </remarks>
    public float Progress(Func<string, bool> scored, bool past)
    {
        ArgumentNullException.ThrowIfNull(scored);

        if (Test == QuestTest.Story)
        {
            return past ? 1f : 0f;
        }

        if (Scores.Count == 0)
        {
            return 0f;
        }

        return Test == QuestTest.Any
            ? Scores.Any(scored) ? 1f : 0f
            : (float)Scores.Count(scored) / Scores.Count;
    }
}

/// <summary>
/// The journal's objectives, read as data.
/// </summary>
/// <remarks>
/// <para>
/// The original shipped no journal, and a 1999 adventure game will happily let a player
/// wander for an hour with no idea what it wants of them. This is the table that says.
/// </para>
/// <para>
/// It is kept apart from the walkthrough deliberately. The walkthrough answers "how" and is
/// a spoiler from end to end; this answers "what", which a player can be told for free. The
/// two are joined only by the hint numbers, so asking for help is always something the
/// player does on purpose.
/// </para>
/// </remarks>
public sealed class Quests
{
    private readonly List<Quest> _quests = [];

    private Quests()
    {
    }

    /// <summary>Every objective, in the order the table gives them.</summary>
    public IReadOnlyList<Quest> All => _quests;

    /// <summary>The objectives of one point in the story.</summary>
    public IReadOnlyList<Quest> Of(Timeblock timeblock) =>
        [.. _quests.Where(q => q.Timeblock == timeblock)];

    /// <summary>The points in the story it covers, in order.</summary>
    public IReadOnlyList<Timeblock> Timeblocks =>
        [.. _quests.Select(q => q.Timeblock).Distinct().Order()];

    /// <summary>The table the engine ships.</summary>
    public static Quests Open()
    {
        using Stream? stream = typeof(Quests).Assembly
            .GetManifestResourceStream("GK3Reborn.Assets.Story.Quests.txt");

        if (stream is null)
        {
            return new Quests();
        }

        using var reader = new StreamReader(stream);

        return Parse(reader.ReadToEnd());
    }

    /// <summary>
    /// Reads a table.
    /// </summary>
    /// <param name="text">Its contents.</param>
    /// <returns>The objectives.</returns>
    /// <remarks>
    /// A heading in brackets is a point in the story; every line under it is an objective,
    /// as a title, a condition and a list of hint lines separated by bars. A malformed line
    /// is skipped rather than thrown over — but nothing should ever reach a player that way,
    /// because the tests read this file and check every part of every line.
    /// </remarks>
    public static Quests Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var quests = new Quests();
        Timeblock? at = null;

        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();

            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                at = Timeblock.TryParse(line[1..^1], out Timeblock parsed) ? parsed : null;
                continue;
            }

            if (at is not { } timeblock)
            {
                continue;
            }

            string[] fields = line.Split('|', StringSplitOptions.TrimEntries);

            if (fields.Length < 2 || fields[0].Length == 0)
            {
                continue;
            }

            (QuestTest test, IReadOnlyList<string> scores) = Condition(fields[1]);

            quests._quests.Add(new Quest(
                timeblock,
                fields[0],
                test,
                scores,
                fields.Length > 2 ? Numbers(fields[2]) : []));
        }

        return quests;
    }

    /// <summary>Reads a completion condition.</summary>
    private static (QuestTest Test, IReadOnlyList<string> Scores) Condition(string text)
    {
        if (text.Equals("story", StringComparison.OrdinalIgnoreCase))
        {
            return (QuestTest.Story, []);
        }

        int colon = text.IndexOf(':');

        if (colon <= 0)
        {
            return (QuestTest.Story, []);
        }

        QuestTest test = text[..colon].Trim().Equals("any", StringComparison.OrdinalIgnoreCase)
            ? QuestTest.Any
            : QuestTest.Every;

        return (test, Names(text[(colon + 1)..]));
    }

    private static IReadOnlyList<string> Names(string text) =>
        [.. text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    private static IReadOnlyList<int> Numbers(string text) =>
    [
        .. text
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(n => int.TryParse(n, out int at) ? at : 0)
            .Where(n => n > 0),
    ];
}
