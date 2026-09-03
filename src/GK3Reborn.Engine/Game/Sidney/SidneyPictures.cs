// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

namespace GK3Reborn.Game.Sidney;

/// <summary>
/// The pictures Sidney's analyze screen has of what it is analysing.
/// </summary>
/// <remarks>
/// <para>
/// <b>The analysis is about a picture and had none.</b> "Device in upper left corner, the
/// '+' symbol on line 4, and the text break on line 14 suggest this image" is a paragraph
/// about something the player cannot see, and the something is in the archives:
/// <c>PARCHMENT1_BASE.BMP</c>, Poussin's painting, the Teniers, and a finished geometry
/// plate for each of them.
/// </para>
/// <para>
/// <b>Which picture depends on what has been done to the file.</b> Extracting the anomalies
/// out of a parchment produces the plate of the raised letters; viewing its geometry
/// produces the plate with the figure drawn on it; zooming a painting produces the enlarged
/// corner. So the newest result the player has earned is what is shown, and the plain
/// picture before any of them.
/// </para>
/// <para>
/// The map is not here: it is not an illustration but a thing to mark places on, and
/// <see cref="SidneyMap"/> owns it.
/// </para>
/// </remarks>
public static class SidneyPictures
{
    /// <summary>What each kind of file looks like before anything has been done to it.</summary>
    private static readonly (SidneyKind Kind, string Picture)[] Plain =
    [
        (SidneyKind.Parchment1, "PARCHMENT1_BASE"),
        (SidneyKind.Parchment2, "PARCHMENT2_BASE"),
        (SidneyKind.Poussin, "POUSSIN"),
        (SidneyKind.Teniers, "TENIERS"),
        (SidneyKind.Symbols, "SYMBOLNOTE9"),
    ];

    /// <summary>What an operation on a kind of file produces a picture of.</summary>
    /// <remarks>
    /// Newest first within a kind, so the first match is the furthest the player has got.
    /// Extracting the letters out of parchment one and then reading its geometry should
    /// leave the geometry on the screen, not the letters.
    /// </remarks>
    private static readonly (SidneyKind Kind, SidneyAction Action, string Picture)[] Results =
    [
        (SidneyKind.Parchment1, SidneyAction.ViewGeometry, "GEOMPARCH1FINAL"),
        (SidneyKind.Parchment1, SidneyAction.ExtractAnomalies, "PARCH1TEXT"),
        (SidneyKind.Parchment2, SidneyAction.ViewGeometry, "GEOMPARCH2FINAL"),
        (SidneyKind.Parchment2, SidneyAction.AnalyseText, "PARCH2TEXT"),
        (SidneyKind.Poussin, SidneyAction.ViewGeometry, "GEOMPOUSSINFINAL"),
        (SidneyKind.Poussin, SidneyAction.ZoomAndClarify, "POUSSIN_ZOOM"),
        (SidneyKind.Teniers, SidneyAction.ViewGeometry, "GEOMTENNIERSFINAL"),
        (SidneyKind.Teniers, SidneyAction.ZoomAndClarify, "TENIERS_ZOOM"),
    ];

    /// <summary>The picture of a file before anything has been done to it.</summary>
    /// <param name="file">The file.</param>
    /// <returns>The bitmap's name without extension, or null when there is no picture.</returns>
    public static string? Of(SidneyFile? file)
    {
        if (file is null)
        {
            return null;
        }

        foreach ((SidneyKind kind, string picture) in Plain)
        {
            if (kind == file.Kind)
            {
                return picture;
            }
        }

        return null;
    }

    /// <summary>
    /// The picture that shows the furthest an analysis has got.
    /// </summary>
    /// <param name="file">The file.</param>
    /// <param name="done">Whether an operation has been run on it.</param>
    /// <returns>The bitmap's name, or the plain picture, or null.</returns>
    public static string? Showing(SidneyFile? file, Func<SidneyAction, bool> done)
    {
        ArgumentNullException.ThrowIfNull(done);

        if (file is null)
        {
            return null;
        }

        foreach ((SidneyKind kind, SidneyAction action, string picture) in Results)
        {
            if (kind == file.Kind && done(action))
            {
                return picture;
            }
        }

        return Of(file);
    }

    /// <summary>
    /// The four hermetic symbols, in the order the mail's own lines describe them.
    /// </summary>
    /// <remarks>
    /// <c>SID_SYMB_1</c> to <c>SID_SYMB_4</c>, 94 pixels square each. The message that
    /// reports them writes its lines starting at the equals sign — "=  'to mix'" — because
    /// the symbol goes where the words would have been.
    /// </remarks>
    public static IReadOnlyList<string> Symbols { get; } =
        ["SID_SYMB_1", "SID_SYMB_2", "SID_SYMB_3", "SID_SYMB_4"];
}
