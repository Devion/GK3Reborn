// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Globalization;
using System.Reflection;
using GK3Reborn.Content;

namespace GK3Reborn.Game;

/// <summary>
/// What each of the game's score events is worth.
/// </summary>
public sealed class ScoreEvents
{
    private readonly Dictionary<string, int> _worth = new(StringComparer.OrdinalIgnoreCase);

    private ScoreEvents()
    {
    }

    /// <summary>How many events the table describes.</summary>
    public int Count => _worth.Count;

    /// <summary>The highest score the game can reach.</summary>
    public int Maximum => _worth.Values.Sum();

    /// <summary>Every event's name, in a stable order.</summary>
    public IReadOnlyList<string> Names =>
        [.. _worth.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase)];

    /// <summary>The table the engine ships.</summary>
    public static ScoreEvents Open()
    {
        using Stream? stream = typeof(ScoreEvents).Assembly
            .GetManifestResourceStream("GK3Reborn.Assets.Story.Scores.txt");

        if (stream is null)
        {
            return new ScoreEvents();
        }

        using var reader = new StreamReader(stream);

        return Parse(reader.ReadToEnd());
    }

    /// <summary>Reads a table.</summary>
    /// <param name="text">Its contents.</param>
    /// <returns>The events.</returns>
    public static ScoreEvents Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var events = new ScoreEvents();

        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();

            if (line.Length == 0 ||
                line.StartsWith("//", StringComparison.Ordinal) ||
                line.StartsWith('['))
            {
                continue;
            }

            // A line may carry more than one event, separated by a comma: four of them do,
            // and reading the line as a single pair threw all four away — the value came out
            // as "4, e_212p_cse_open_cellar_doors = 2", which is not a number, so the whole
            // line was skipped and two events simply did not exist. Nothing said so: a
            // missing event scores nothing and is indistinguishable from one the player has
            // not earned. The journal is what found it, by naming one of them.
            foreach (string pair in line.Split(','))
            {
                if (pair.IndexOf('=') is not (> 0 and { } equals))
                {
                    continue;
                }

                if (int.TryParse(
                        pair[(equals + 1)..].Trim(),
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out int points))
                {
                    events._worth[pair[..equals].Trim()] = points;
                }
            }
        }

        return events;
    }

    /// <summary>
    /// Which point in the story an event belongs to, from its name.
    /// </summary>
    /// <param name="name">The event, such as <c>e_110a_lby_read_register</c>.</param>
    /// <returns>The timeblock, or null when the name does not carry one.</returns>
    public static Timeblock? TimeblockOf(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        string[] parts = name.Split('_');

        return parts.Length >= 2 && Timeblock.TryParse(parts[1], out Timeblock when)
            ? when
            : null;
    }

    /// <summary>What an event is worth, or null when the table has no such event.</summary>
    /// <param name="name">The event's name, as a script writes it.</param>
    /// <returns>Its points.</returns>
    public int? Worth(string? name) =>
        name is { Length: > 0 } named && _worth.TryGetValue(named, out int points) ? points : null;

    /// <summary>Whether the table describes an event.</summary>
    /// <param name="name">The event's name.</param>
    /// <returns>True when it does.</returns>
    public bool Knows(string? name) => Worth(name) is not null;
}
