// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

namespace GK3Reborn.Game;

/// <summary>
/// What dusting one of the lobby's two glasses comes to.
/// </summary>
/// <param name="Says">What Gabriel says about it, as a dialogue licence plate, or null.</param>
/// <param name="Lifts">Whether the glass's own print is lifted, with its item and score.</param>
/// <param name="Mislabels">The wrongly labelled print he pockets instead, or null.</param>
/// <param name="Asks">
/// The noun whose two topics put the question — <c>CROW</c> for Wilkes's glass,
/// <c>DAGGER</c> for Buchelli's — or null when there is nothing to ask.
/// </param>
public sealed record GlassDusting(string? Says, bool Lifts, string? Mislabels, string? Asks);

/// <summary>
/// The two dirty glasses in the hotel lobby on the second afternoon, and whose print is
/// on which.
/// </summary>
public static class DirtyGlasses
{
    /// <summary>The variable the lobby's scripts read.</summary>
    public const string Variable = "DirtyGlassPrint";

    /// <summary>The glass Wilkes drank from.</summary>
    public const string WilkesGlass = "DIRTY_GLASS_WILKES";

    /// <summary>The glass Buchelli drank from.</summary>
    public const string BuchelliGlass = "DIRTY_GLASS_BUCHELLI";

    /// <summary>The noun whose topics ask about Wilkes's glass.</summary>
    public const string WilkesQuestion = "CROW";

    /// <summary>The noun whose topics ask about Buchelli's glass.</summary>
    public const string BuchelliQuestion = "DAGGER";

    /// <summary>The answer that names Wilkes.</summary>
    public const string SaidWilkes = "T_WILKES";

    /// <summary>The answer that names Buchelli.</summary>
    public const string SaidBuchelli = "T_BUCHELLI";

    /// <summary>"I got a print, but I have to think who was usin' this glass..."</summary>
    public const string Wondering = "1EK0259291";

    /// <summary>"Right. Okay." — said after either answer, not entirely convinced.</summary>
    public const string Conceding = "1EK0259292";

    /// <summary>"This print doesn't look anythin' like the one I got off Buchelli's suitcase."</summary>
    private const string KnowsWilkes = "1EK4259NS1";

    /// <summary>"That's Buchelli's print. It matches the one I already have."</summary>
    private const string KnowsBuchelli = "1EK0259NS1";

    /// <summary>The flag that says Buchelli's print came off his suitcase that morning.</summary>
    private const string SuitcasePrint = "GotSuitcaseBuchelliPrint";

    // The retail engine's own numbers for the variable.
    private const int None = 0;
    private const int Buchelli = 1;
    private const int Wilkes = 2;
    private const int BuchelliCalledWilkes = 3;
    private const int WilkesCalledBuchelli = 4;
    private const int Both = 5;

    /// <summary>Whether a thing dusted is one of the two glasses.</summary>
    /// <param name="noun">What the script asked to dust.</param>
    /// <returns>True for either glass.</returns>
    public static bool IsOne(string noun) =>
        string.Equals(noun, WilkesGlass, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(noun, BuchelliGlass, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Dusts a glass: what Gabriel makes of the print, written to the story.
    /// </summary>
    /// <param name="noun">Which glass.</param>
    /// <param name="story">The game.</param>
    /// <returns>What happens, or null when the noun is not a glass.</returns>
    public static GlassDusting? Dust(string noun, GameState story)
    {
        ArgumentNullException.ThrowIfNull(noun);
        ArgumentNullException.ThrowIfNull(story);

        if (!IsOne(noun))
        {
            return null;
        }

        bool wilkes = string.Equals(noun, WilkesGlass, StringComparison.OrdinalIgnoreCase);
        int state = story.GetVariable(Variable);

        // Recognised on sight, against the print off the suitcase. Wilkes's is lifted and
        // said to be his; Buchelli's he already has, and takes nothing off.
        if (story.GetFlag(SuitcasePrint))
        {
            story.SetVariable(Variable, state == None ? (wilkes ? Wilkes : Buchelli) : Both);

            return wilkes
                ? new GlassDusting(KnowsWilkes, Lifts: true, null, null)
                : new GlassDusting(KnowsBuchelli, Lifts: false, null, null);
        }

        // The first glass of the two, with nothing to compare it to: ask.
        if (state == None)
        {
            return new GlassDusting(
                Wondering, Lifts: false, null, wilkes ? WilkesQuestion : BuchelliQuestion);
        }

        // The second glass, deduced from what the first was taken for — rightly or not.
        story.SetVariable(Variable, Both);

        return (wilkes, state) switch
        {
            (true, Buchelli) or (false, Wilkes) => new GlassDusting(null, Lifts: true, null, null),
            (true, BuchelliCalledWilkes) => new GlassDusting(null, Lifts: false, MislabelledWilkes, null),
            (false, WilkesCalledBuchelli) => new GlassDusting(null, Lifts: false, MislabelledBuchelli, null),

            // Dusted again once both are done, which the scripts' own cases answer before
            // it gets here; nothing more comes off.
            _ => new GlassDusting(null, Lifts: false, null, null),
        };
    }

    /// <summary>
    /// Answers the question about the first glass.
    /// </summary>
    /// <param name="noun">Which glass was dusted.</param>
    /// <param name="answer">Which topic was chosen, <c>T_WILKES</c> or <c>T_BUCHELLI</c>.</param>
    /// <param name="story">The game.</param>
    /// <returns>What follows: the print lifted for the right name, a mislabelled one for the wrong.</returns>
    public static GlassDusting Answer(string noun, string answer, GameState story)
    {
        ArgumentNullException.ThrowIfNull(noun);
        ArgumentNullException.ThrowIfNull(answer);
        ArgumentNullException.ThrowIfNull(story);

        bool wilkes = string.Equals(noun, WilkesGlass, StringComparison.OrdinalIgnoreCase);
        bool saidWilkes = string.Equals(answer, SaidWilkes, StringComparison.OrdinalIgnoreCase);

        if (wilkes == saidWilkes)
        {
            story.SetVariable(Variable, wilkes ? Wilkes : Buchelli);

            return new GlassDusting(Conceding, Lifts: true, null, null);
        }

        story.SetVariable(Variable, wilkes ? WilkesCalledBuchelli : BuchelliCalledWilkes);

        return new GlassDusting(
            Conceding, Lifts: false, wilkes ? MislabelledWilkes : MislabelledBuchelli, null);
    }

    /// <summary>Wilkes's print, filed under Buchelli's name.</summary>
    public const string MislabelledWilkes = "WILKES_FINGERPRINT_LABELED_BUCHELLI";

    /// <summary>Buchelli's print, filed under Wilkes's name.</summary>
    public const string MislabelledBuchelli = "BUCHELLIS_FINGERPRINT_LABELED_WILKES";
}
