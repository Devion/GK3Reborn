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
/// <remarks>
/// <para>
/// <b>None of this is invented.</b> <c>RADIO</c> is one of the game's own 287 verbs —
/// <c>VERBS.TXT</c> line 138 — and the temple's action files write twenty-four rules for
/// it, exactly as they write <c>LOOK</c> and <c>PUSH</c>. The original reached them the way
/// it reached every verb: hold the button, wait for the ring, find the icon. There is also
/// a headset button on the original's own option bar (<c>rc_radio_std</c> in
/// <c>RC_LAYOUT.TXT</c>) which calls one function, <see cref="Call"/>, in the room's script.
/// </para>
/// <para>
/// So the port keeps both and puts them in one place: the button is on the top bar where it
/// can be seen, and what it opens is the list of things the room will actually answer to
/// right now. Nothing about what an answer <em>is</em> changes — each row runs the rule the
/// action files wrote, through the same resolver, with the same conditions and the same
/// scoring. See <c>Plan/03</c> section 2.3: modernise how a thing is reached, never what
/// reaching it does.
/// </para>
/// </remarks>
public static class Radio
{
    /// <summary>The verb, as the action files and <c>VERBS.TXT</c> spell it.</summary>
    public const string Verb = "RADIO";

    /// <summary>The function the original's own headset button calls in the room's script.</summary>
    /// <remarks>
    /// <para>
    /// <c>OptionBar::OnRadioButtonPressed</c> runs
    /// <c>CallSheep(&lt;location&gt;, "RadioButton$")</c> and nothing else. Four of the five
    /// temple rooms declare it; TE5 does not, and there the button has nothing general to
    /// say.
    /// </para>
    /// <para>
    /// <b>It is not a duplicate of the noun list and must not be dropped.</b> TE4's rules
    /// for radioing Grace about the Solomon statue are commented out in
    /// <c>TE4309P.NVC</c> — lines 117 and 118 — precisely because this function covers
    /// them. Without the general call that conversation, and the point it scores, cannot be
    /// reached at all.
    /// </para>
    /// </remarks>
    public const string Call = "RadioButton$";

    /// <summary>When Gabriel is wearing it.</summary>
    /// <remarks>
    /// <para>
    /// Day three, nine in the evening — the temple — and it is the reference engine's own
    /// gate: <c>mRadioButton-&gt;SetEnabled(gGameProgress.GetTimeblock() == Timeblock(3,
    /// 21))</c>.
    /// </para>
    /// <para>
    /// <b>The time rather than the room, and that is an anti-spoiler decision as much as a
    /// faithful one.</b> Gabriel is wearing the headset for the whole hour, so a button that
    /// came and went from room to room would be telling the player which rooms have
    /// something in them worth asking about. It shows throughout and dims where there is
    /// nothing to say, which is what <c>rc_radio_dis</c> is for.
    /// </para>
    /// </remarks>
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
    /// <remarks>
    /// <para>
    /// Resolved rather than listed: a rule whose case does not hold is not a topic, which is
    /// what keeps the list to what Grace will actually answer. The resolver applies exactly
    /// the conditions a right-click would.
    /// </para>
    /// <para>
    /// <b>Aliases are folded together, and the data says which name to keep.</b> The porch's
    /// tiles are four nouns — <c>TILES</c>, <c>CROSS_TILES</c>, <c>SKULL_TILES</c>,
    /// <c>SWORD_TILES</c> — with one radio conversation between them, and the scales in TE3
    /// are seven nouns with one. Listing them all would offer the player four rows that say
    /// the same thing. Two rules with the same script are one topic, and the survivor is the
    /// noun that rule's own script names in its <c>IncNounVerbCount</c> — <c>TILES</c> for
    /// the porch, <c>SCALE_ON_TABLE</c> for the scales. That is the data nominating its own
    /// canonical noun rather than a table kept here by hand, and it is right for both; file
    /// order is right for neither, since the tiles put the plain noun last and the scales
    /// put it first.
    /// </para>
    /// </remarks>
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
    /// <remarks>
    /// A quoted noun in the script is the rule pointing at itself: every one of the porch's
    /// four tile rules ends <c>IncNounVerbCount("TILES","RADIO")</c>, which is the authors
    /// saying that all four are the tiles. Where a script names none of them — every topic
    /// that is not an alias — there is only one noun and the question does not arise.
    /// </remarks>
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
