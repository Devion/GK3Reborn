// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Globalization;
using System.Text.RegularExpressions;
using GK3Reborn.Game;

namespace GK3Reborn.Game.Story;

/// <summary>One line of the walkthrough.</summary>
/// <param name="Timeblock">Which point in the story it belongs to.</param>
/// <param name="Location">Where it happens, in the walkthrough's own words.</param>
/// <param name="Text">What to do, verbatim. <b>This is a spoiler.</b></param>
/// <param name="Points">What it scores, or zero for a step that scores nothing.</param>
/// <param name="Running">
/// The running total the walkthrough prints beside it, or zero. Kept because it is the one
/// checkable number in the file: an off-by-one in the parse shows up as a total that stops
/// agreeing with the sum of the parts.
/// </param>
public sealed record WalkthroughStep(
    Timeblock Timeblock,
    string Location,
    string Text,
    int Points,
    int Running)
{
    /// <summary>Whether the step scores anything.</summary>
    /// <remarks>
    /// A step that does not is a transition or an observation — "Go outside", "You'll see
    /// Buchelli argue with Arnaud". They are worth keeping: half of what a player needs to
    /// know is where to go next, and the game awards nothing for walking there.
    /// </remarks>
    public bool Scores => Points > 0;
}

/// <summary>
/// The walkthrough, read as data.
/// </summary>
/// <remarks>
/// <para>
/// A tab-separated file of location, action and points, under a heading per point in the
/// story. It has two jobs and they pull in opposite directions.
/// </para>
/// <para>
/// <b>It is the source of the journal's hints.</b> A player who is stuck asks for one and
/// gets a line of this, which is why nothing here is ever shown unasked: every line is a
/// spoiler, and several are the answer to a puzzle the game expects the player to enjoy
/// working out. What the journal shows by default is written separately — see
/// <c>Quests.txt</c> — and says what to do without saying how.
/// </para>
/// <para>
/// <b>And it is a test.</b> Every action it names is something the shipped scripts have to
/// allow at that point in the story, so reading it against the corpus finds the places where
/// they do not, before a player finds them by being unable to finish the game.
/// </para>
/// </remarks>
public sealed partial class Walkthrough
{
    private readonly List<WalkthroughStep> _steps = [];

    private Walkthrough()
    {
    }

    /// <summary>Every step, in the order the walkthrough gives them.</summary>
    public IReadOnlyList<WalkthroughStep> Steps => _steps;

    /// <summary>The points in the story it covers, in order.</summary>
    public IReadOnlyList<Timeblock> Timeblocks =>
        [.. _steps.Select(s => s.Timeblock).Distinct().Order()];

    /// <summary>The steps of one point in the story.</summary>
    /// <param name="timeblock">Which one.</param>
    /// <returns>Its steps, in order.</returns>
    public IReadOnlyList<WalkthroughStep> Of(Timeblock timeblock) =>
        [.. _steps.Where(s => s.Timeblock == timeblock)];

    /// <summary>The walkthrough the engine ships.</summary>
    public static Walkthrough Open()
    {
        using Stream? stream = typeof(Walkthrough).Assembly
            .GetManifestResourceStream("GK3Reborn.Assets.Story.Walkthrough.txt");

        if (stream is null)
        {
            return new Walkthrough();
        }

        using var reader = new StreamReader(stream);

        return Parse(reader.ReadToEnd());
    }

    /// <summary>
    /// Reads a walkthrough.
    /// </summary>
    /// <param name="text">Its contents.</param>
    /// <returns>The steps.</returns>
    /// <remarks>
    /// <para>
    /// Four shapes of line, told apart by how many tabs are on them and whether the last
    /// field is a score. Three fields is a location, an action and its points. Two is either
    /// an action and its points, continuing at the location above, or a location and an
    /// action that scores nothing — which is why the score is matched rather than counted
    /// to. One field is a step in its own right that names neither, and is usually the
    /// sentence telling the player where to walk.
    /// </para>
    /// <para>
    /// A heading is <c>Day 2: 10 AM - 12 PM (Gabriel)</c>, and the code the rest of the
    /// engine uses falls straight out of it: the day, the first hour, and which half of the
    /// day it is. Nothing before the first heading is a step, which is what skips the
    /// column titles at the top of the file.
    /// </para>
    /// </remarks>
    public static Walkthrough Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var walkthrough = new Walkthrough();
        Timeblock? at = null;
        string location = string.Empty;

        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r', ' ', '\t');

            if (line.Length == 0)
            {
                continue;
            }

            if (Heading().Match(line) is { Success: true } heading)
            {
                at = new Timeblock(
                    int.Parse(heading.Groups[1].ValueSpan, CultureInfo.InvariantCulture),
                    int.Parse(heading.Groups[2].ValueSpan, CultureInfo.InvariantCulture),
                    heading.Groups[3].Value.Equals("PM", StringComparison.OrdinalIgnoreCase));

                location = string.Empty;
                continue;
            }

            if (at is not { } timeblock)
            {
                // The column titles, and anything else above the first heading.
                continue;
            }

            string[] fields = line.Split('\t');
            string? scored = fields.Length > 1 ? fields[^1].Trim() : null;
            bool hasPoints = scored is not null && Score().IsMatch(scored);

            (string where, string what) = fields.Length switch
            {
                >= 3 => (fields[0].Trim(), fields[1].Trim()),
                2 when hasPoints => (location, fields[0].Trim()),
                2 => (fields[0].Trim(), fields[1].Trim()),
                _ => (location, fields[0].Trim()),
            };

            if (what.Length == 0)
            {
                continue;
            }

            if (where.Length > 0)
            {
                location = where;
            }

            (int points, int running) = hasPoints && scored is not null
                ? Split(scored)
                : (0, 0);

            walkthrough._steps.Add(
                new WalkthroughStep(timeblock, location, what, points, running));
        }

        return walkthrough;
    }

    /// <summary>Whether the running totals agree with the points beside them.</summary>
    /// <param name="fault">Where they first stop agreeing.</param>
    /// <returns>True when every total is the sum of everything before it.</returns>
    /// <remarks>
    /// The file's own check on the parse. Each scored line prints what it is worth and what
    /// the player has by then, so a step read twice or missed shows up as a total that no
    /// longer follows — which is a great deal easier to find than the wrong hint appearing
    /// in the journal three days into the story.
    /// </remarks>
    public bool Adds(out string? fault)
    {
        int running = 0;

        foreach (WalkthroughStep step in _steps.Where(s => s.Scores))
        {
            running += step.Points;

            if (running != step.Running)
            {
                fault =
                    $"{step.Timeblock}: after \"{Shorten(step.Text)}\" the walkthrough says " +
                    $"{step.Running} and the points add to {running}.";

                return false;
            }
        }

        fault = null;
        return true;
    }

    /// <summary>What everything in the walkthrough scores together.</summary>
    public int Points => _steps.Sum(s => s.Points);

    /// <summary>Enough of a step to recognise it in a message.</summary>
    internal static string Shorten(string text) =>
        text.Length <= 60 ? text : text[..57] + "...";

    private static (int Points, int Running) Split(string scored)
    {
        string[] parts = scored.Split('/');

        return (
            int.Parse(parts[0], CultureInfo.InvariantCulture),
            int.Parse(parts[1], CultureInfo.InvariantCulture));
    }

    [GeneratedRegex(@"^Day\s+(\d+)\s*:\s*(\d+)\s*(AM|PM)", RegexOptions.IgnoreCase)]
    private static partial Regex Heading();

    [GeneratedRegex(@"^\d+\s*/\s*\d+$")]
    private static partial Regex Score();
}
