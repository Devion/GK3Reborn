// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

namespace GK3Reborn.Game.Sidney;

/// <summary>
/// What Sidney's own work is worth, and whose an unnamed fingerprint turns out to be.
/// Neither is in the game's data: the retail engine holds both tables in code, and this is
/// a reading of the reference engine's <c>SidneyFiles.cpp</c>, <c>SidneySuspects.h</c>,
/// <c>SidneyTranslate.cpp</c>, <c>SidneyEmail.cpp</c> and <c>SidneyAnalyze_Image.cpp</c>.
/// </summary>
internal static class SidneyScores
{
    /// <summary>What scanning an item into the machine is worth, by the item's noun.</summary>
    private static readonly Dictionary<string, string> Scans =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["ABBE_FINGERPRINT"] = "e_sidney_add_fingerprint_abbe",
            ["BUCHELLIS_FINGERPRINT"] = "e_sidney_add_fingerprint_buchelli",
            ["BUTHANES_FINGERPRINT"] = "e_sidney_add_fingerprint_buthane",
            ["ESTELLES_FINGERPRINT"] = "e_sidney_add_fingerprint_estelle",
            ["HOWARDS_FINGERPRINT"] = "e_sidney_add_fingerprint_howard",
            ["LARRYS_FINGERPRINT"] = "e_sidney_add_fingerprint_larry",
            ["MONTREAUX_FINGERPRINT"] = "e_sidney_add_fingerprint_montreaux",
            ["WILKES_FINGERPRINT"] = "e_sidney_add_fingerprint_wilkes",
            ["ESTELLES_FINGERPRINT_LSR"] = "e_sidney_add_fingerprint_lsr_unknown",

            // The three lifted off the manuscript. The retail engine's names for these do
            // not agree with who each one turns out to belong to — the print it calls
            // "buthane" here is Buchelli's in the match analysis — and they are copied as
            // they are spelt, because the score sheet is keyed on the name and not on who.
            ["UNKNOWN_PRINT_1"] = "e_sidney_add_manuscript_prints_buthane",
            ["UNKNOWN_PRINT_2"] = "e_sidney_add_manuscript_prints_mosley",
            ["UNKNOWN_PRINT_3"] = "e_sidney_add_manuscript_prints_buchelli",

            ["MAP"] = "e_sidney_add_map",
            ["PARCHMENT_1"] = "e_sidney_add_parch1",
            ["PARCHMENT_2"] = "e_sidney_add_parch2",
            ["POUSSIN_POSTCARD"] = "e_sidney_add_postcard_1",
            ["TENIERS_POSTCARD_TEMP"] = "e_sidney_add_postcard_2",
            ["TENIERS_POSTCARD_NO_TEMP"] = "e_sidney_add_postcard_3",
            ["HERM_SYMBOLS"] = "e_sidney_add_hermetical_symbols_from_serres",
            ["I_AM_WORDS"] = "e_sidney_add_sum_note",
            ["ABBE_TAPE"] = "e_sidney_add_tape_abbe",
            ["BUCHELLI_TAPE"] = "e_sidney_add_tape_buchelli",
            ["BUCHELLIS_LICENSE"] = "e_sidney_add_license_buchelli",
            ["EMILIOS_LICENSE"] = "e_sidney_add_license_emilio",
            ["HOWARDS_LICENSE"] = "e_sidney_add_license_howard",
            ["MOSELYS_LICENSE"] = "e_sidney_add_license_mosely",
            ["WILKES_LICENSE"] = "e_sidney_add_license_wilkes",
        };

    /// <summary>
    /// The one file that is each suspect's own, and what putting it on them is worth.
    /// Mosely's print is not here: it is filed for the player rather than scanned, so the
    /// retail engine gives nothing for either adding or linking it. Estelle shares Lady
    /// Howard's registration, and the same event with it.
    /// </summary>
    private static readonly (int Suspect, string Item, string Score)[] Links =
    [
        (1, "BUTHANES_FINGERPRINT", "e_sidney_suspect_link_fingerprint_buthane"),
        (2, "BUCHELLIS_FINGERPRINT", "e_sidney_suspect_link_fingerprint_buchelli"),
        (4, "ABBE_FINGERPRINT", "e_sidney_suspect_link_fingerprint_abbe"),
        (5, "HOWARDS_FINGERPRINT", "e_sidney_suspect_link_fingerprint_howard"),
        (6, "ESTELLES_FINGERPRINT", "e_sidney_suspect_link_fingerprint_estelle"),
        (7, "WILKES_FINGERPRINT", "e_sidney_suspect_link_fingerprint_wilkes"),
        (8, "LARRYS_FINGERPRINT", "e_sidney_suspect_link_fingerprint_larry"),
        (9, "MONTREAUX_FINGERPRINT", "e_sidney_suspect_link_fingerprint_montreaux"),
        (2, "BUCHELLIS_LICENSE", "e_sidney_suspect_link_license_buchelli"),
        (3, "EMILIOS_LICENSE", "e_sidney_suspect_link_license_emilio"),
        (5, "HOWARDS_LICENSE", "e_sidney_suspect_link_license_howard"),
        (6, "HOWARDS_LICENSE", "e_sidney_suspect_link_license_howard"),
        (7, "WILKES_LICENSE", "e_sidney_suspect_link_license_wilkes"),
        (10, "MOSELYS_LICENSE", "e_sidney_suspect_link_license_mosely"),
    ];

    /// <summary>
    /// Whose a print that names nobody actually is, and what the match is worth. Without
    /// this the four prints the whole suspects screen is for could never match anybody: the
    /// port compared the file's name against the suspect's, and these four carry neither.
    /// </summary>
    private static readonly Dictionary<string, (int Suspect, string Score)> Identifications =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["UNKNOWN_PRINT_1"] = (2, "e_sidney_analysis_link_manuscript_prints_buchelli"),
            ["UNKNOWN_PRINT_2"] = (1, "e_sidney_analysis_link_manuscript_prints_buthane"),
            ["UNKNOWN_PRINT_3"] = (10, "e_sidney_analysis_link_manuscript_prints_mosley"),
            ["ESTELLES_FINGERPRINT_LSR"] = (6, "e_sidney_suspect_link_fingerprint_lsr_unknown"),
        };

    /// <summary>What turning a file into English is worth, by the item it was scanned from.</summary>
    private static readonly Dictionary<string, string> Translations =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["ABBE_TAPE"] = "e_sidney_translate_tape_abbe",
            ["BUCHELLI_TAPE"] = "e_sidney_translate_tape_buchelli",
            ["I_AM_WORDS"] = "e_sidney_translate_sum",
            ["POUSSIN_POSTCARD"] = "e_sidney_translate_arcadia",
        };

    /// <summary>The two messages that are worth anything to open.</summary>
    private static readonly Dictionary<string, string> Mail =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["EMail4"] = "e_sidney_email_open_temple_diagram",
            ["EMail5"] = "e_sidney_email_open_symbols_from_serres",
        };

    /// <summary>What scanning an item in is worth, or null when it is worth nothing.</summary>
    /// <param name="item">The inventory item's noun.</param>
    /// <returns>The score event.</returns>
    internal static string? Scanned(string item) =>
        Scans.TryGetValue(item, out string? score) ? score : null;

    /// <summary>What linking a file to a suspect is worth.</summary>
    /// <param name="suspect">Which of the ten, from one.</param>
    /// <param name="item">The item the file was scanned from.</param>
    /// <returns>The score event, or null when the file is not that suspect's own.</returns>
    internal static string? Linked(int suspect, string item)
    {
        foreach ((int who, string owned, string score) in Links)
        {
            if (who == suspect && owned.Equals(item, StringComparison.OrdinalIgnoreCase))
            {
                return score;
            }
        }

        return null;
    }

    /// <summary>Who a print that names nobody belongs to.</summary>
    /// <param name="item">The item the file was scanned from.</param>
    /// <returns>The suspect's index from one, or zero when the print names its owner.</returns>
    internal static int Identifies(string item) =>
        Identifications.TryGetValue(item, out (int Suspect, string Score) found) ? found.Suspect : 0;

    /// <summary>What matching such a print to its owner is worth.</summary>
    /// <param name="item">The item the file was scanned from.</param>
    /// <returns>The score event, or null when the match is worth nothing.</returns>
    internal static string? Matched(string item) =>
        Identifications.TryGetValue(item, out (int Suspect, string Score) found) ? found.Score : null;

    /// <summary>What translating a file is worth.</summary>
    /// <param name="item">The item the file was scanned from.</param>
    /// <returns>The score event, or null.</returns>
    internal static string? Translated(string item) =>
        Translations.TryGetValue(item, out string? score) ? score : null;

    /// <summary>What opening a message is worth.</summary>
    /// <param name="mail">The message's id, as <c>ESIDNEYEMAIL.TXT</c> spells it.</param>
    /// <returns>The score event, or null.</returns>
    internal static string? Read(string mail) =>
        Mail.TryGetValue(mail, out string? score) ? score : null;

    /// <summary>
    /// What one of the analyze screen's operations is worth. The two that pull a hidden
    /// message out of a parchment are not here: they pay on the answer to the question they
    /// ask and not on the button, so <see cref="Extractions"/> has those.
    /// </summary>
    private static readonly Dictionary<(SidneyKind Kind, SidneyAction Action), string> Operations =
        new()
        {
            [(SidneyKind.Symbols, SidneyAction.Analyse)] = "e_sidney_analysis_symbols_from_serres",
            [(SidneyKind.Parchment1, SidneyAction.ViewGeometry)] = "e_sidney_analysis_gemoetry_parch1",
            [(SidneyKind.Parchment2, SidneyAction.ViewGeometry)] = "e_sidney_analysis_gemoetry_parch2",
            [(SidneyKind.Poussin, SidneyAction.ViewGeometry)] = "e_sidney_analysis_gemoetry_poussin",
            [(SidneyKind.Parchment2, SidneyAction.RotateShape)] = "e_sidney_analysis_view_rotation_parch2",
            [(SidneyKind.Poussin, SidneyAction.ZoomAndClarify)] = "e_sidney_analysis_view_words_poussin",
            [(SidneyKind.Teniers, SidneyAction.ZoomAndClarify)] = "e_sidney_analysis_view_words_tenier",
        };

    /// <summary>What saying a parchment's hidden message is French is worth.</summary>
    private static readonly Dictionary<SidneyKind, string> Extractions = new()
    {
        [SidneyKind.Parchment1] = "e_sidney_analysis_anomalies_parch1",
        [SidneyKind.Parchment2] = "e_sidney_analysis_anomalies_parch2",
    };

    /// <summary>
    /// What printing an identity card is worth, and the two rows it is worth it for. Only
    /// the press cards get Gabriel into the vineyard; the rows are named by position
    /// because the job behind each one is a translated string.
    /// </summary>
    private const string Card = "e_sidney_makeid_print";

    /// <summary>What one of the analyze screen's operations is worth.</summary>
    /// <param name="kind">What the open file is.</param>
    /// <param name="action">What was done to it.</param>
    /// <returns>The score event, or null.</returns>
    internal static string? Analysed(SidneyKind kind, SidneyAction action) =>
        Operations.TryGetValue((kind, action), out string? score) ? score : null;

    /// <summary>What saying a parchment's hidden message is French is worth.</summary>
    /// <param name="kind">Which parchment.</param>
    /// <returns>The score event, or null for anything else.</returns>
    internal static string? Extracted(SidneyKind kind) =>
        Extractions.TryGetValue(kind, out string? score) ? score : null;

    /// <summary>What printing an identity card is worth.</summary>
    /// <param name="key">The row, as <c>Menu2Item1</c>.</param>
    /// <returns>The score event, or null.</returns>
    internal static string? Printed(string key) =>
        key is "Menu2Item1" or "Menu2Item2" ? Card : null;

    /// <summary>
    /// Every event these tables can credit, for whoever checks them against the score
    /// sheet: an event misspelt here is worth nothing and looks exactly like one the player
    /// has not earned.
    /// </summary>
    internal static IEnumerable<string> Names =>
        Scans.Values
            .Concat(Links.Select(link => link.Score))
            .Concat(Identifications.Values.Select(found => found.Score))
            .Concat(Translations.Values)
            .Concat(Mail.Values)
            .Concat(Operations.Values)
            .Concat(Extractions.Values)
            .Append(Card)
            .Distinct(StringComparer.OrdinalIgnoreCase);
}
