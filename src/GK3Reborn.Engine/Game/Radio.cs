// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using GK3Reborn.Formats.Actions;

namespace GK3Reborn.Game;

/// <summary>One thing Gabriel can raise with Grace over the radio.</summary>
/// <param name="Noun">
/// What the action files call it, which is what the verb is performed against. The empty
/// string for the room's own general call; see <see cref="Radio.Call"/>.
/// </param>
/// <param name="Label">What to show the player, already through the room's own naming.</param>
public readonly record struct RadioTopic(string Noun, string Label)
{
    /// <summary>Whether this is the room's general call rather than a thing in it.</summary>
    public bool IsGeneral => Noun.Length == 0;
}

/// <summary>
/// The headset Gabriel wears in the temple, and what he can ask Grace through it.
/// </summary>
public static class Radio
{
    /// <summary>The verb, as the action files and <c>VERBS.TXT</c> spell it.</summary>
    public const string Verb = "RADIO";

    /// <summary>The function the original's own headset button calls in the room's script.</summary>
    public const string Call = "RadioButton$";

    /// <summary>When Gabriel is wearing it.</summary>
    public static Timeblock Worn { get; } = new(3, 9, IsAfternoon: true);

    /// <summary>Whether Gabriel has the headset on.</summary>
    /// <param name="now">Where the story stands.</param>
    /// <returns>True in the one timeblock he wears it.</returns>
    public static bool WornAt(Timeblock now) => now == Worn;

    /// <summary>
    /// What the room will answer to over the radio, here and now.
    /// </summary>
    /// <param name="actions">The room's action files.</param>
    /// <param name="ego">Who the player currently is.</param>
    /// <param name="name">
    /// What to call a noun on screen, or null to use the noun itself. The room's own naming
    /// goes through here so that a topic list obeys the same rules a hover label does.
    /// </param>
    /// <returns>The topics, in the order the files list them.</returns>
    public static IReadOnlyList<RadioTopic> Topics(
        ActionResolver actions, string ego = "GABRIEL", Func<string, string>? name = null)
    {
        ArgumentNullException.ThrowIfNull(actions);

        List<(string Script, List<string> Nouns)> groups = [];

        foreach (string noun in actions.NounsFor(Verb))
        {
            if (actions.Find(noun, Verb, ego) is not { } rule)
            {
                continue;
            }

            string script = rule.Script ?? string.Empty;
            int at = groups.FindIndex(g => string.Equals(g.Script, script, StringComparison.Ordinal));

            if (at < 0)
            {
                groups.Add((script, [noun]));
            }
            else
            {
                groups[at].Nouns.Add(noun);
            }
        }

        List<RadioTopic> topics = [];

        foreach ((string script, List<string> nouns) in groups)
        {
            string chosen = Canonical(script, nouns);
            topics.Add(new RadioTopic(chosen, name?.Invoke(chosen) ?? chosen));
        }

        return topics;
    }

    /// <summary>Which of a set of nouns sharing one rule is the one to show.</summary>
    /// <param name="script">The rule's script.</param>
    /// <param name="nouns">The nouns that resolved to it, in file order.</param>
    /// <returns>The noun the script names, or the first.</returns>
    private static string Canonical(string script, List<string> nouns)
    {
        if (nouns.Count == 1)
        {
            return nouns[0];
        }

        foreach (string noun in nouns)
        {
            if (script.Contains('"' + noun + '"', StringComparison.OrdinalIgnoreCase))
            {
                return noun;
            }
        }

        return nouns[0];
    }
}
