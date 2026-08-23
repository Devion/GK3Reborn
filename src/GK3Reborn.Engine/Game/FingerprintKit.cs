// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

namespace GK3Reborn.Game;

/// <summary>One print waiting on one surface.</summary>
/// <param name="Item">The inventory item lifting it gives, or empty for none.</param>
/// <param name="Flag">The flag lifting it sets, or empty for none.</param>
/// <param name="Score">The score event lifting it earns, or empty for none.</param>
public sealed record Fingerprint(string Item, string Flag, string Score);

/// <summary>
/// What the fingerprint kit finds on each thing it can be used on.
/// </summary>
/// <remarks>
/// <para>
/// The one piece of the game's story that lives in neither the scripts nor the scene files:
/// the original's fingerprint screen carries this table in its own code, the same way the
/// executable carried the score table and the starting inventory. The scripts call
/// <c>ShowFingerPrintInterface("HAND_MIRROR")</c> and everything after that — which prints
/// are on the mirror, what lifting one gives, what it scores — was compiled in. Adapted
/// from G-Engine's <c>FingerprintScreen.cpp</c>; see NOTICE.
/// </para>
/// <para>
/// Thirteen of the game's score events are awarded from here and nowhere else, which is why
/// <c>check-story</c> reported every fingerprint in the game as unreachable: no script names
/// them, and none is missing.
/// </para>
/// </remarks>
public static class FingerprintKit
{
    private static readonly Dictionary<string, Fingerprint[]> Objects = new(StringComparer.OrdinalIgnoreCase)
    {
        // Day 2, 7AM, as Grace. Her own book: dusting it finds nothing worth keeping.
        ["HBHG_BOOK"] = [],

        // Day 2, 10AM, sneaking the hotel as Gabriel.
        ["HAND_MIRROR"] =
            [new("HOWARDS_FINGERPRINT", "GotMirrorHowardPrint", "e_210a_r31_fingerprint_kit_mirror")],
        ["GUN_IN_CASE"] =
            [new("BUTHANES_FINGERPRINT", "GotGunButhanePrint", "e_210a_r29_fingerprint_kit_on_gun")],
        ["CIG_PACK_IN_DRAWER"] =
            [new("ABBE_FINGERPRINT", "GotCigPackAbbePrint", "e_210a_fingerprint_kit_cigarette_pack")],
        ["SUITCASE"] =
            [new("BUCHELLIS_FINGERPRINT", "GotSuitcaseBuchelliPrint", "e_210a_r21_fingerprint_kit_suitcase")],
        ["JESUS_PICTURE"] = [],

        // Day 2, 12PM, the Chateau de Serres office.
        ["BOOK_IN_DRAWER"] =
            [new("MONTREAUX_FINGERPRINT", "GotImmortalsMontreauxPrint", "e_212p_cs2_fingerprint_kit_immortals_book")],

        // Day 2, 2PM, the lobby glasses and Mosely's bottle.
        ["DIRTY_GLASS_WILKES"] =
            [new("WILKES_FINGERPRINT", "", "e_202p_lby_fingerprint_kit_wilke_glass")],
        // The reference carries this score commented out and never awards it, which makes
        // the objective it belongs to impossible. The game's own score sheet lists it at
        // two points, so it is awarded here: an event the sheet names and nothing can earn
        // is a defect wherever the comment came from.
        ["DIRTY_GLASS_BUCHELLI"] =
            [new("BUCHELLIS_FINGERPRINT", "", "e_202p_lby_fingerprint_kit_buchelli_glass")],
        ["POP_BOTTLE"] =
            [new("", "GotPMoselyPrint", "e_202p_r25_fingerprint_kit_soda_bottle")],

        // Day 2, 5PM, the folder from the museum door.
        ["LSR_ENVELOPE_INV"] =
            [new("ESTELLES_FINGERPRINT_LSR", "GotLEstellePrint", "e_205p_inventory_fingerprint_kit_envelope")],
        ["DIRTY_WINE_GLASS_BUCHELLI"] =
            [new("BUCHELLIS_FINGERPRINT", "GotWineBuchelliPrint", "")],
        ["GLASS"] = [],

        // Day 3. The manuscript is a different object each time it is dusted, which is why
        // the noun alone is not the key: the scripts say BLOODLINE_MANUSCRIPT and the point
        // in the story says whose prints are on it by then.
        ["BLOODLINE_MANUSCRIPT_202A"] =
            [new("LARRYS_FINGERPRINT", "GotMLarryPrint", "e_302a_inventory_fingerprint_kit_manuscript")],
        ["BLOODLINE_MANUSCRIPT_312P"] =
        [
            new("UNKNOWN_PRINT_1", "GotMMoselyPrint", "e_312p_bmb_fingerprint_kit_manuscript1"),
            new("UNKNOWN_PRINT_2", "GotMButhanePrint", "e_312p_bmb_fingerprint_kit_manuscript2"),
            new("UNKNOWN_PRINT_3", "GotMBuchelliPrint", "e_312p_bmb_fingerprint_kit_manuscript3"),
        ],
        ["WATER_BOTTLE_ON_MOPED"] =
            [new("ESTELLES_FINGERPRINT", "GotWaterBottleEstellePrint", "e_303p_wod_fingerprint_kit_water_bottle")],
    };

    /// <summary>Every score event the kit can award, for the story check to count.</summary>
    public static IReadOnlyList<string> Scores =>
    [
        .. Objects.Values
            .SelectMany(prints => prints)
            .Select(print => print.Score)
            .Where(score => score.Length > 0),
    ];

    /// <summary>
    /// The prints on a thing, at this point in the story.
    /// </summary>
    /// <param name="noun">What the script asked to dust.</param>
    /// <param name="timeblock">Where the story is, for the things dusted more than once.</param>
    /// <returns>The prints, empty for a surface with nothing on it, or null for no entry.</returns>
    /// <remarks>
    /// The noun first and the noun with the timeblock after it, which is the reference's own
    /// lookup: <c>BLOODLINE_MANUSCRIPT</c> is Larry's prints at 2am and three unknowns by
    /// the afternoon of day three.
    /// </remarks>
    public static IReadOnlyList<Fingerprint>? On(string noun, Timeblock timeblock)
    {
        ArgumentNullException.ThrowIfNull(noun);

        if (Objects.TryGetValue(noun, out Fingerprint[]? prints))
        {
            return prints;
        }

        return Objects.TryGetValue($"{noun}_{timeblock}", out Fingerprint[]? dated)
            ? dated
            : null;
    }

    /// <summary>
    /// Lifts everything a surface has, into the story.
    /// </summary>
    /// <param name="noun">What was dusted.</param>
    /// <param name="state">The game.</param>
    /// <param name="scores">What each score event is worth.</param>
    /// <returns>The items gained, for the screen to say so.</returns>
    /// <remarks>
    /// Award, flag and pocket in one step per print. The original walks the player through
    /// brushing and taping each one; what the story records at the end is exactly this, and
    /// it is the part that was blocking six of the journal's objectives.
    /// </remarks>
    public static IReadOnlyList<string> Lift(string noun, GameState state, ScoreEvents scores)
    {
        ArgumentNullException.ThrowIfNull(noun);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(scores);

        List<string> gained = [];

        foreach (Fingerprint print in On(noun, state.Timeblock) ?? [])
        {
            if (print.Score is { Length: > 0 } earned)
            {
                state.AwardScore(earned, scores.Worth(earned));
            }

            if (print.Flag is { Length: > 0 } flag)
            {
                state.SetFlag(flag);
            }

            if (print.Item is { Length: > 0 } item)
            {
                state.Inventory.Add(state.Ego, item);
                gained.Add(item);
            }
        }

        return gained;
    }
}
