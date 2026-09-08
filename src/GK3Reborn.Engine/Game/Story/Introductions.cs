// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using GK3Reborn.Formats;
using GK3Reborn.Sheep;

namespace GK3Reborn.Game.Story;

/// <summary>
/// Who the player has been introduced to.
/// </summary>
public sealed class Introductions
{
    private readonly Dictionary<string, string> _conditions =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly List<(Timeblock When, string Noun)> _roster = [];

    private Introductions()
    {
    }

    /// <summary>Nobody is a stranger, for a run with no table.</summary>
    public static Introductions None { get; } = new();

    /// <summary>How many people the table has a rule for.</summary>
    public int Count => _conditions.Count;

    /// <summary>Everybody the table has a rule for.</summary>
    public IReadOnlyCollection<string> Nouns => _conditions.Keys;

    /// <summary>Reads the table the engine ships.</summary>
    /// <returns>The rules, empty when the resource is missing.</returns>
    public static Introductions Open()
    {
        using Stream? stream = typeof(Introductions).Assembly
            .GetManifestResourceStream("GK3Reborn.Assets.Story.Introductions.txt");

        if (stream is null)
        {
            return None;
        }

        using var reader = new StreamReader(stream);

        return Parse(reader.ReadToEnd());
    }

    /// <summary>Reads a table.</summary>
    /// <param name="text">Its contents.</param>
    /// <returns>The rules.</returns>
    public static Introductions Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var table = new Introductions();

        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();

            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            int bar = line.IndexOf('|', StringComparison.Ordinal);

            if (bar <= 0 || bar == line.Length - 1)
            {
                continue;
            }

            string left = line[..bar].Trim();
            string right = line[(bar + 1)..].Trim();

            if (left.Length == 0 || right.Length == 0)
            {
                continue;
            }

            // A line whose left side is a timeblock is a roster line — who the story has
            // put in front of the player by then — and everything else is a person and the
            // condition they are known by. Nobody's noun can be read as a block: a block is
            // a day, an hour and an A or a P, and the parse refuses everything else.
            if (Timeblock.TryParse(left, out Timeblock when))
            {
                foreach (string noun in right.Split(
                    ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    table._roster.Add((when, noun));
                }
            }
            else
            {
                table._conditions[left] = right;
            }
        }

        return table;
    }

    /// <summary>
    /// Who a save standing at a point in the story cannot still be a stranger to.
    /// </summary>
    /// <param name="when">The timeblock the save stands in.</param>
    /// <returns>The nouns to treat as introduced, whatever the story can show.</returns>
    public IReadOnlyList<string> MetBy(Timeblock when)
    {
        if (when.Day > 1)
        {
            return [.. _conditions.Keys.OrderBy(noun => noun, StringComparer.Ordinal)];
        }

        // A roster line naming somebody the table has no condition for is left out rather
        // than carried: taking them as met is only ever an answer to a question about them,
        // and there is no question.
        return
        [
            .. _roster
                .Where(entry => entry.When <= when && _conditions.ContainsKey(entry.Noun))
                .Select(entry => entry.Noun)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(noun => noun, StringComparer.Ordinal),
        ];
    }

    /// <summary>Whether the player already knows what to call somebody.</summary>
    /// <param name="noun">The noun the scene gives them.</param>
    /// <param name="api">The host the condition is asked of.</param>
    /// <returns>
    /// True for anybody the table says has been introduced, and for anybody it does not
    /// mention at all.
    /// </returns>
    public bool Knows(string? noun, Gk3SheepApi? api)
    {
        if (noun is not { Length: > 0 } ||
            api is null ||
            !_conditions.TryGetValue(noun, out string? condition))
        {
            return true;
        }

        // A restored game may know somebody the state can no longer show it knows: see
        // MetBy, and GameState.Introduce.
        if (api.State.WasIntroduced(noun))
        {
            return true;
        }

        try
        {
            return SheepExpression.IsTrue(condition, api);
        }
        catch (FormatParseException)
        {
            return true;
        }
    }
}
