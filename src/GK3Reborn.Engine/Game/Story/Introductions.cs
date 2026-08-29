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
/// <remarks>
/// <para>
/// A question the original never had to answer, because it drew no label under the
/// pointer. This one does, and a scene names its people by their surnames — <c>BUTHANE</c>,
/// <c>BUCHELLI</c>, <c>WILKES</c> — so a label that reads them back names every suspect in
/// the game the moment the player first sees one. It is the leak the second-floor doors
/// had, in a place where there is no room number to fall back on.
/// </para>
/// <para>
/// <b>Every condition is the game's own.</b> They are copied out of the <c>[LOGIC]</c>
/// sections of the action files — <c>MET_BUTHANE</c>, <c>MET_WILKES</c>,
/// <c>INTRODUCED_EMILIO</c> — and evaluated exactly as an action's case is, against the
/// same host. Which is what bounds the list: a character the shipped data never asks "have
/// we met" about is not in it and keeps their name. See
/// <c>Assets/Story/Introductions.txt</c>, which says which file each line came from.
/// </para>
/// <para>
/// Anyone not listed is treated as known. That is the safe way round: a name shown early
/// is a small spoiler, and a name withheld from somebody the player has been talking to
/// for two days is a bug they cannot work around.
/// </para>
/// <para>
/// <b>The table also says when each of them enters the story</b>, which is what a save the
/// original game wrote needs. See <see cref="MetBy"/>.
/// </para>
/// </remarks>
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
    /// <remarks>
    /// <para>
    /// For the saves the 1999 game wrote, which answer none of the conditions above: a
    /// <c>.gk3</c> carries the timeblock, the room and the score, and not one topic count,
    /// so an import two days into the story would draw <c>Woman</c> under Madeleine
    /// Buthane. The reasoning is the one the score events are recovered by — a point in the
    /// story implies everything behind it — and it errs the way the table errs, towards
    /// showing a name rather than withholding one.
    /// </para>
    /// <para>
    /// A day beyond the first is everybody, rather than everybody the roster happens to
    /// name. Every introduction in the game is on day one, so the two answers are the same
    /// today; they would stop being the same the moment somebody adds a line, and the wrong
    /// one to be left holding then is the shorter.
    /// </para>
    /// </remarks>
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
    /// <remarks>
    /// A condition that cannot be evaluated answers "known" rather than being reported. It
    /// is a label, and the worst a wrong answer here can do is show a name a little early;
    /// raising a diagnostic every frame the pointer rests on somebody would cost more.
    /// </remarks>
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
