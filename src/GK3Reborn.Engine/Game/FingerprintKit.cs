// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

namespace GK3Reborn.Game;

/// <summary>Where a dusted thing's picture sits in the kit's right-hand panel.</summary>
public enum FingerprintAnchor
{
    /// <summary>Against the left edge, halfway down. What most of them use.</summary>
    Left,

    /// <summary>In the middle of the panel.</summary>
    Centre,
}

/// <summary>One print waiting on one surface.</summary>
/// <param name="Item">The inventory item lifting it gives, or empty for none.</param>
/// <param name="Flag">The flag lifting it sets, or empty for none.</param>
/// <param name="Score">The score event lifting it earns, or empty for none.</param>
/// <param name="Picture">
/// The game's own picture of the print, white powder on black, or empty in a test.
/// </param>
/// <param name="X">Where its left edge goes, from the panel's bottom-left corner.</param>
/// <param name="Y">And its bottom edge, measured upwards.</param>
/// <param name="Uncovers">What is said the moment the brush has it fully out, or empty.</param>
/// <param name="GraceFlag">
/// The flag Grace sets instead, where she has her own. One print does: the water bottle on
/// Lady Howard's moped, which Gabriel can take on the third afternoon and Grace again that
/// evening, and whose two rooms read two different flags.
/// </param>
public sealed record Fingerprint(
    string Item,
    string Flag,
    string Score,
    string Picture = "",
    float X = 0,
    float Y = 0,
    string Uncovers = "",
    string GraceFlag = "");

/// <summary>
/// One thing the kit can be used on: its picture, and what is on it.
/// </summary>
/// <param name="Picture">The game's own photograph of it.</param>
/// <param name="Prints">What the powder brings out, in the order the game lists them.</param>
/// <param name="Nothing">
/// What is said once the brush has been over a bare surface long enough to be sure, or
/// empty. Only the three things with no prints on them have one.
/// </param>
/// <param name="Lifted">
/// What is said as each print is taken, in the order they are taken rather than the order
/// they are listed — which is how the retail engine plays them.
/// </param>
/// <param name="Anchor">Where the picture goes in the panel.</param>
/// <param name="OffsetX">How far right of that, in the panel's own pixels.</param>
/// <param name="OffsetY">And how far down.</param>
public sealed record DustedThing(
    string Picture,
    IReadOnlyList<Fingerprint> Prints,
    string Nothing = "",
    IReadOnlyList<string>? Lifted = null,
    FingerprintAnchor Anchor = FingerprintAnchor.Left,
    float OffsetX = 0,
    float OffsetY = 0);

/// <summary>
/// What the fingerprint kit finds on each thing it can be used on.
/// </summary>
/// <remarks>
/// <b>None of this is in the game data.</b> No script says what a surface has on it, where
/// on it the print is, or what is said about it: the retail engine holds the whole table in
/// code, and this is it, read out of the reference engine's <c>FingerprintScreen.cpp</c>.
/// The pictures and the coordinates are as much a part of it as the items are — a kit that
/// knows what it will find without the player dusting for it is not the puzzle the game
/// shipped. See <see cref="FingerprintDusting"/> for the dusting itself.
/// </remarks>
public static class FingerprintKit
{
    /// <summary>"I've already done that." — a print taken twice.</summary>
    public const string AlreadyTaken = "2FL8S27SG1";

    /// <summary>
    /// The line three of these things share as each print comes off.
    /// </summary>
    private const string Lifting = "0A89N052H2";

    private static readonly Dictionary<string, DustedThing> Objects =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // Day 2, 7AM, as Grace. Her own book: dusting it finds nothing worth keeping.
            ["HBHG_BOOK"] = new("FP_HOLYGBOOK.BMP", [], "10LCQ59291"),

            // Day 2, 10AM, sneaking the hotel as Gabriel.
            ["HAND_MIRROR"] = new(
                "FP_LHOMIR.BMP",
                [
                    new("HOWARDS_FINGERPRINT", "GotMirrorHowardPrint",
                        "e_210a_r31_fingerprint_kit_mirror",
                        "FP_LHOMIR_P1.BMP", 159, 134, "2O8A805PF1"),
                ],
                Anchor: FingerprintAnchor.Centre),

            ["GUN_IN_CASE"] = new(
                "FP_COLT45.BMP",
                [
                    new("BUTHANES_FINGERPRINT", "GotGunButhanePrint",
                        "e_210a_r29_fingerprint_kit_on_gun",
                        "FP_COLT45_P1.BMP", 65, 279, "0J8ES05291"),
                ],
                Lifted: [Lifting],
                OffsetY: -12),

            ["CIG_PACK_IN_DRAWER"] = new(
                "FP_CIGS.BMP",
                [
                    new("ABBE_FINGERPRINT", "GotCigPackAbbePrint",
                        "e_210a_fingerprint_kit_cigarette_pack",
                        "FP_CIGS_P1.BMP", 101, 201, "0D83M05ML1"),
                ],
                Lifted: [Lifting]),

            ["SUITCASE"] = new(
                "FP_SUITCA.BMP",
                [
                    new("BUCHELLIS_FINGERPRINT", "GotSuitcaseBuchelliPrint",
                        "e_210a_r21_fingerprint_kit_suitcase",
                        "FP_SUITCA_P1.BMP", 111, 370, "0A89N052H1"),
                ],
                Lifted: [Lifting],
                OffsetY: 8),

            ["JESUS_PICTURE"] = new("FP_JESUS.BMP", [], "0K86F05PF1"),

            // Day 2, 12PM, the Chateau de Serres office.
            ["BOOK_IN_DRAWER"] = new(
                "FP_BOOKIMMORTALS.BMP",
                [
                    new("MONTREAUX_FINGERPRINT", "GotImmortalsMontreauxPrint",
                        "e_212p_cs2_fingerprint_kit_immortals_book",
                        "FP_BOOKIMMORTALS_P1.BMP", 113, 144, "08BBY59411"),
                ]),

            // Day 2, 2PM, the lobby glasses and Mosely's bottle. Neither glass's print
            // carries a flag, and whose it is decided outside this table: see DirtyGlasses.
            ["DIRTY_GLASS_WILKES"] = new(
                "FP_OCTSHOT.BMP",
                [
                    new("WILKES_FINGERPRINT", "", "e_202p_lby_fingerprint_kit_wilke_glass",
                        "FP_OCTSHOT_P1.BMP", 81, 214, Lifting),
                ],
                Anchor: FingerprintAnchor.Centre,
                OffsetY: -8),

            // The reference carries this score commented out and never awards it, which
            // makes the objective it belongs to impossible. The game's own score sheet
            // lists it at two points, so it is awarded here: an event the sheet names and
            // nothing can earn is a defect wherever the comment came from.
            ["DIRTY_GLASS_BUCHELLI"] = new(
                "FP_SQRSHOT.BMP",
                [
                    new("BUCHELLIS_FINGERPRINT", "",
                        "e_202p_lby_fingerprint_kit_buchelli_glass",
                        "FP_SQRSHOT_P1.BMP", 150, 202, Lifting),
                ],
                Anchor: FingerprintAnchor.Centre),

            ["POP_BOTTLE"] = new(
                "FP_SODA.BMP",
                [
                    new("", "GotPMoselyPrint", "e_202p_r25_fingerprint_kit_soda_bottle",
                        "FP_SODA_P1.BMP", 147, 150),
                ],
                Anchor: FingerprintAnchor.Centre),

            // Day 2, 5PM, the folder from the museum door.
            ["LSR_ENVELOPE_INV"] = new(
                "FP_LSRENV.BMP",
                [
                    new("ESTELLES_FINGERPRINT_LSR", "GotLEstellePrint",
                        "e_205p_inventory_fingerprint_kit_envelope",
                        "FP_LSRENV_P1.BMP", 178, 305, "10LEM59291"),
                ]),

            ["DIRTY_WINE_GLASS_BUCHELLI"] = new(
                "FP_BUCHGLASS.BMP",
                [
                    new("BUCHELLIS_FINGERPRINT", "GotWineBuchelliPrint", "",
                        "FP_BUCHGLASS_P1.BMP", 82, 149),
                ],
                Lifted: ["1077H59291"]),

            ["GLASS"] = new("FP_GLASS.BMP", [], "1EPXH59291"),

            // Day 3. The manuscript is a different object each time it is dusted, which is
            // why the noun alone is not the key: the scripts say BLOODLINE_MANUSCRIPT and
            // the point in the story says whose prints are on it by then.
            ["BLOODLINE_MANUSCRIPT_202A"] = new(
                "FP_BLOMAN.BMP",
                [
                    new("LARRYS_FINGERPRINT", "GotMLarryPrint",
                        "e_302a_inventory_fingerprint_kit_manuscript",
                        "FP_BLOMAN_P6.BMP", 206, 301),
                ],
                Lifted: ["1037H59291"]),

            ["BLOODLINE_MANUSCRIPT_312P"] = new(
                "FP_BLOMAN.BMP",
                [
                    new("UNKNOWN_PRINT_1", "GotMMoselyPrint",
                        "e_312p_bmb_fingerprint_kit_manuscript1",
                        "FP_BLOMAN_P1.BMP", 200, 243, "1EP02593L1"),
                    new("UNKNOWN_PRINT_2", "GotMButhanePrint",
                        "e_312p_bmb_fingerprint_kit_manuscript2",
                        "FP_BLOMAN_P2.BMP", 205, 98, "1077H59291"),
                    new("UNKNOWN_PRINT_3", "GotMBuchelliPrint",
                        "e_312p_bmb_fingerprint_kit_manuscript3",
                        "FP_BLOMAN_P3.BMP", 199, 344, "1077H59291"),
                ],
                Lifted: ["1077H59292", "1077H59293", "1077H59294"]),

            // Day 3, 3PM as Gabriel or 6PM as Grace — and the two rooms read two different
            // flags, so hers is not his.
            ["WATER_BOTTLE_ON_MOPED"] = new(
                "FP_WATBTL.BMP",
                [
                    new("ESTELLES_FINGERPRINT", "GotWaterBottleEstellePrint",
                        "e_303p_wod_fingerprint_kit_water_bottle",
                        "FP_WATBTL_P1.BMP", 132, 278,
                        GraceFlag: "GotWaterBottleEstellePrintGrace"),
                ],
                Lifted: [Lifting],
                Anchor: FingerprintAnchor.Centre),
        };

    /// <summary>Every score event the kit can award, for the story check to count.</summary>
    public static IReadOnlyList<string> Scores =>
    [
        .. Objects.Values
            .SelectMany(thing => thing.Prints)
            .Select(print => print.Score)
            .Where(score => score.Length > 0),
    ];

    /// <summary>
    /// The thing being dusted, at this point in the story.
    /// </summary>
    /// <param name="noun">What the script asked to dust.</param>
    /// <param name="timeblock">Where the story is, for the things dusted more than once.</param>
    /// <returns>The thing, or null when the kit has no entry for that noun.</returns>
    public static DustedThing? Thing(string noun, Timeblock timeblock)
    {
        ArgumentNullException.ThrowIfNull(noun);

        if (Objects.TryGetValue(noun, out DustedThing? thing))
        {
            return thing;
        }

        return Objects.GetValueOrDefault($"{noun}_{timeblock}");
    }

    /// <summary>
    /// Puts one print into the story: its item, its flag and its score.
    /// </summary>
    /// <param name="print">The print.</param>
    /// <param name="state">The game.</param>
    /// <param name="scores">What each score event is worth.</param>
    /// <returns>The item it gave, or null when it gives none.</returns>
    public static string? Collect(Fingerprint print, GameState state, ScoreEvents scores)
    {
        ArgumentNullException.ThrowIfNull(print);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(scores);

        if (print.Score is { Length: > 0 } earned)
        {
            state.AwardScore(earned, scores.Worth(earned));
        }

        // Grace's own flag where she has one, which is how the moped's two rooms tell her
        // visit apart from his.
        string flag = print.GraceFlag is { Length: > 0 } hers &&
            string.Equals(state.Ego, "GRACE", StringComparison.OrdinalIgnoreCase)
                ? hers
                : print.Flag;

        if (flag is { Length: > 0 })
        {
            state.SetFlag(flag);
        }

        if (print.Item is not { Length: > 0 } item)
        {
            return null;
        }

        state.Inventory.Add(state.Ego, item);

        return item;
    }

    /// <summary>
    /// Lifts everything a surface has, into the story.
    /// </summary>
    /// <param name="noun">What was dusted.</param>
    /// <param name="state">The game.</param>
    /// <param name="scores">What each score event is worth.</param>
    /// <returns>The items gained, for the screen to say so.</returns>
    public static IReadOnlyList<string> Lift(string noun, GameState state, ScoreEvents scores)
    {
        ArgumentNullException.ThrowIfNull(noun);
        ArgumentNullException.ThrowIfNull(state);

        List<string> gained = [];

        foreach (Fingerprint print in Thing(noun, state.Timeblock)?.Prints ?? [])
        {
            if (Collect(print, state, scores) is { } item)
            {
                gained.Add(item);
            }
        }

        return gained;
    }
}
