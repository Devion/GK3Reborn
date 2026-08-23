// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

namespace GK3Reborn.Game.Sidney;

/// <summary>What kind of thing a scanned file is, which decides what may be done to it.</summary>
public enum SidneyKind
{
    /// <summary>Something the machine recognises and can say nothing more about.</summary>
    Unknown,

    /// <summary>The first parchment: raised letters and a hidden shape.</summary>
    Parchment1,

    /// <summary>The second: Greek with letters inserted, and two shapes.</summary>
    Parchment2,

    /// <summary>The map of the area.</summary>
    Map,

    /// <summary>Poussin's painting.</summary>
    Poussin,

    /// <summary>One of the two Teniers postcards.</summary>
    Teniers,

    /// <summary>The hermetic symbols.</summary>
    Symbols,

    /// <summary>The note whose text may be translated.</summary>
    Note,

    /// <summary>A fingerprint belonging to somebody named.</summary>
    KnownPrint,

    /// <summary>A fingerprint belonging to nobody yet.</summary>
    UnknownPrint,

    /// <summary>A recording.</summary>
    Tape,

    /// <summary>A licence plate.</summary>
    Licence,
}

/// <summary>A file in Sidney's store.</summary>
/// <param name="Id">
/// What the story calls it. Eight of these are named in the game's conditions —
/// <c>fileParchment1</c>, <c>fileMap</c> — and <c>DoesSidneyFileExist</c> asks about them
/// by name.
/// </param>
/// <param name="Item">The inventory item it was scanned from.</param>
/// <param name="Label">What to show the player.</param>
/// <param name="Kind">What it is, which decides what may be done to it.</param>
public sealed record SidneyFile(string Id, string Item, string Label, SidneyKind Kind);

/// <summary>
/// What may be scanned into Sidney, and what it becomes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scanning is already an ordinary action.</b> The noun is the inventory item, the verb
/// is <c>SCANNER</c>, and the case is <c>IN_SIDNEY_ADD_DATA</c>; the game's own
/// <c>INV_ALL.NVC</c> carries the script, which marks the item used and sets a
/// <c>SidScanner</c> variable to a number. Twenty-nine items can be scanned and the numbers
/// run from 1 to 35.
/// </para>
/// <para>
/// What the original did with that number lives in its executable. What it has to mean here
/// is the file the scan produces, and that is what this table is: the item's own name, which
/// is where the meaning actually is — <c>PARCHMENT_1</c> becomes <c>fileParchment1</c>, and
/// the eight names the story asks about are all accounted for.
/// </para>
/// <para>
/// The prints, tapes and licences do not get story-visible file names because nothing asks
/// <c>DoesSidneyFileExist</c> about one. They are still files: they appear in the store, the
/// analysis recognises them, and the suspects screen is where they are meant to be used.
/// </para>
/// </remarks>
public static class SidneyFiles
{
    /// <summary>What the story may ask about by name, and what produces each.</summary>
    private static readonly (string Item, string Id, string Label, SidneyKind Kind)[] Named =
    [
        ("PARCHMENT_1", "fileParchment1", "Parchment 1", SidneyKind.Parchment1),
        ("PARCHMENT_2", "fileParchment2", "Parchment 2", SidneyKind.Parchment2),
        ("MAP", "fileMap", "Map", SidneyKind.Map),
        ("POUSSIN_POSTCARD", "filePainting1", "Poussin", SidneyKind.Poussin),
        ("TENIERS_POSTCARD_TEMP", "filePainting2", "Teniers", SidneyKind.Teniers),
        ("TENIERS_POSTCARD_NO_TEMP", "filePainting3", "Teniers, no temple", SidneyKind.Teniers),
        ("HERM_SYMBOLS", "fileHermNote", "Hermetic symbols", SidneyKind.Symbols),
        ("I_AM_WORDS", "fileSUMNote", "The words", SidneyKind.Note),
    ];

    /// <summary>The file an inventory item becomes when it is scanned.</summary>
    /// <param name="item">The item's noun.</param>
    /// <returns>The file, or null when the item is not something the scanner takes.</returns>
    public static SidneyFile? For(string? item)
    {
        if (item is not { Length: > 0 })
        {
            return null;
        }

        string name = item.Trim().ToUpperInvariant();

        foreach ((string known, string id, string label, SidneyKind kind) in Named)
        {
            if (string.Equals(known, name, StringComparison.Ordinal))
            {
                return new SidneyFile(id, name, label, kind);
            }
        }

        // The rest are recognised by what their names say. A fingerprint labelled with
        // somebody's name is a known print and one numbered is not, which is exactly the
        // distinction the analysis draws between AnalyzeKPrint and AnalyzeUPrint.
        //
        // Both spellings, because the game uses both: ABBE_FINGERPRINT is somebody's and
        // UNKNOWN_PRINT_1 is nobody's, and matching only the longer word left the six
        // unknown prints unrecognised — which is the half of the suspects screen that has
        // anything to find out.
        if (name.Contains("FINGERPRINT", StringComparison.Ordinal) ||
            name.Contains("_PRINT", StringComparison.Ordinal))
        {
            bool unknown = name.StartsWith("UNKNOWN", StringComparison.Ordinal);

            return new SidneyFile(
                "file" + Pretty(name).Replace(" ", string.Empty, StringComparison.Ordinal),
                name,
                Pretty(name),
                unknown ? SidneyKind.UnknownPrint : SidneyKind.KnownPrint);
        }

        if (name.EndsWith("_TAPE", StringComparison.Ordinal))
        {
            return new SidneyFile("file" + Pretty(name).Replace(" ", string.Empty, StringComparison.Ordinal),
                name, Pretty(name), SidneyKind.Tape);
        }

        if (name.EndsWith("_LICENSE", StringComparison.Ordinal))
        {
            return new SidneyFile("file" + Pretty(name).Replace(" ", string.Empty, StringComparison.Ordinal),
                name, Pretty(name), SidneyKind.Licence);
        }

        return null;
    }

    /// <summary>Whether the scanner will take this item at all.</summary>
    /// <param name="item">The item's noun.</param>
    /// <returns>True when scanning it produces a file.</returns>
    public static bool Scannable(string? item) => For(item) is not null;

    /// <summary>Every file the story may ask about by name.</summary>
    public static IReadOnlyList<string> StoryFiles => [.. Named.Select(n => n.Id)];

    /// <summary>An item's noun as a person would write it.</summary>
    internal static string Pretty(string item)
    {
        string[] words = item.Split('_', StringSplitOptions.RemoveEmptyEntries);

        return string.Join(
            ' ',
            words.Select(w => w.Length <= 1
                ? w.ToUpperInvariant()
                : char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant()));
    }
}
