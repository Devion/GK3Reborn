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
/// <remarks>
/// <para>
/// <c>ChangeScore</c> takes the <em>name</em> of an event — <c>ChangeScore("e_110a_lby_read_register")</c>
/// — and the points are the engine's business. Nothing in the shipped data says what one
/// is worth: there is no such file in any of the eight barns, because the table was
/// compiled into the original executable. So the engine carries it, restored from
/// G-Engine's reconstruction; see <c>Assets/Story/Scores.txt</c> and NOTICE.
/// </para>
/// <para>
/// <b>An event scores once.</b> The same call is made every time the player does the thing,
/// and the second time is worth nothing — which is also what makes the set of events
/// achieved a reasonable record of what the player has done.
/// </para>
/// <para>
/// A name the table does not have scores nothing and is reported. The original logs
/// "Illegal score name" and carries on, which is right: a typo in a script must not stop
/// the game, and it must not silently award points either.
/// </para>
/// </remarks>
public sealed class ScoreEvents
{
    private readonly Dictionary<string, int> _worth = new(StringComparer.OrdinalIgnoreCase);

    private ScoreEvents()
    {
    }

    /// <summary>How many events the table describes.</summary>
    public int Count => _worth.Count;

    /// <summary>The highest score the game can reach.</summary>
    /// <remarks>
    /// The sum of every event, which is what the interface's "of 965" is. Derived rather
    /// than written down, so that correcting one event's points cannot leave the total
    /// disagreeing with the parts.
    /// </remarks>
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
                line.StartsWith('[') ||
                line.IndexOf('=') is not (> 0 and { } equals))
            {
                continue;
            }

            if (int.TryParse(
                    line[(equals + 1)..].Trim(),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int points))
            {
                events._worth[line[..equals].Trim()] = points;
            }
        }

        return events;
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
