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
/// <param name="Ordinal">
/// Where it comes in its own point in the story, counting from one. It is the number
/// <c>Quests.txt</c> already writes when it points a hint at this line, and it is what the
/// line is called in the interface tables — see <see cref="TextKey"/>.
/// </param>
public sealed record WalkthroughStep(
    Timeblock Timeblock,
    string Location,
    string Text,
    int Points,
    int Running,
    int Ordinal)
{
    /// <summary>What this line is filed under in <c>interface-*.json</c>.</summary>
    public string TextKey => Key(Timeblock, Ordinal);

    /// <summary>What the line at a position in a point in the story is filed under.</summary>
    /// <param name="timeblock">Which point in the story.</param>
    /// <param name="ordinal">Its position within that, counting from one.</param>
    /// <returns>The key.</returns>
    public static string Key(Timeblock timeblock, int ordinal) =>
        string.Create(CultureInfo.InvariantCulture, $"hint.{timeblock}.{ordinal}");

    /// <summary>Whether the step scores anything.</summary>
    public bool Scores => Points > 0;
}

/// <summary>
/// The walkthrough, read as data.
/// </summary>
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
    public static Walkthrough Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var walkthrough = new Walkthrough();
        Timeblock? at = null;
        string location = string.Empty;

        // How far into its own point in the story each line is. Kept per block rather than
        // per file so that a line added to Day 1 does not renumber every hint after it —
        // which is the same reason Quests.txt counts its hints that way.
        var ordinals = new Dictionary<Timeblock, int>();

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

            ordinals.TryGetValue(timeblock, out int ordinal);
            ordinals[timeblock] = ++ordinal;

            walkthrough._steps.Add(
                new WalkthroughStep(timeblock, location, what, points, running, ordinal));
        }

        return walkthrough;
    }

    /// <summary>Whether the running totals agree with the points beside them.</summary>
    /// <param name="fault">Where they first stop agreeing.</param>
    /// <returns>True when every total is the sum of everything before it.</returns>
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
