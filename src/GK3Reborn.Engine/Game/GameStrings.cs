// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using GK3Reborn.Content;
using GK3Reborn.Formats.Ui;

namespace GK3Reborn.Game;

/// <summary>
/// <c>ESTRINGS.TXT</c> — what the game calls things, in the player's language.
/// </summary>
/// <remarks>
/// <para>
/// Three families matter here. <c>loc_lby = Hotel Lobby</c> names all 79 locations;
/// <c>Day110a = Day 1, 10am - 12pm</c> names the seventeen timeblocks; and <c>dm_rl1</c>
/// names the driving map's stops. The engine had been showing the codes — <c>LBY - 110A</c>
/// in the corner of the screen — and reading the third family with a hand-rolled parser
/// inside the driving map.
/// </para>
/// <para>
/// <b>It is what an exit should be called.</b> RC1's four ways out are nouns the artists
/// numbered rather than named: <c>EXIT</c>, <c>EXIT1</c>, <c>EXIT2</c>, <c>EXIT3</c>,
/// <c>EXIT4</c>, <c>EXIT5</c>. Those numbers mean nothing to anybody — they are not even
/// in a sensible order — and the interface was drawing them as "Exit3". What the player
/// wants to know is where the door goes, and the rule behind the door says: its script
/// calls <c>SetLocation("rc3")</c>, and this turns that into "Outside Church".
/// </para>
/// <para>
/// The file is Windows-1252 and full of French place names, which
/// <see cref="GameArchives.ReadText"/> already decodes correctly. Missing keys are simply
/// absent: an installation without the file gets the codes back, which is what the
/// interface drew before this existed.
/// </para>
/// </remarks>
public sealed partial class GameStrings
{
    private readonly KeyedText _text;

    private GameStrings(KeyedText text) => _text = text;

    /// <summary>An empty set, for a run with no archives.</summary>
    public static GameStrings None { get; } = new(KeyedText.Parse(string.Empty));

    /// <summary>How many names it holds.</summary>
    public int Count => _text.Section(string.Empty).Count + _text.Section("ToolTips").Count;

    /// <summary>Which file the names were read from.</summary>
    /// <remarks>
    /// Kept because it is the one asset whose <em>name</em> says what language an
    /// installation is, and because a run reading the wrong one is invisible: French names
    /// under an English table look like a table with a lot of missing keys.
    /// </remarks>
    public string File { get; private init; } = Names.English;

    /// <summary>Reads the file out of the archives.</summary>
    /// <param name="archives">The game's archives.</param>
    /// <returns>The names, empty when no archive holds the file.</returns>
    /// <remarks>
    /// <para>
    /// <b>The file's name carries the language.</b> Sierra renamed it for four
    /// localisations — <c>FSTRINGS.TXT</c> in French, <c>ISTRINGS</c> in Italian,
    /// <c>GSTRINGS</c> in German, <c>SSTRINGS</c> in Spanish — and left it as
    /// <c>ESTRINGS.TXT</c> for the rest, whose contents differ instead. So the language's
    /// own spelling is tried first and English is the fallback, which is both the right
    /// answer for Portuguese, Russian and Polish and the right answer for an installation
    /// whose language pack is not there.
    /// </para>
    /// <para>
    /// The archives are read through <see cref="GameArchives.Localization"/>, so a French
    /// game on an English installation finds <c>FSTRINGS.TXT</c> in the pack even though no
    /// English archive has ever held one.
    /// </para>
    /// </remarks>
    public static GameStrings Open(GameArchives archives)
    {
        ArgumentNullException.ThrowIfNull(archives);

        string file = TableFor(archives);

        return archives.ReadText(file) is { } text
            ? new GameStrings(KeyedText.Parse(text, file)) { File = file }
            : None;
    }

    /// <summary>Which string table a set of archives should be read with.</summary>
    /// <param name="archives">The game's archives.</param>
    /// <returns>A file name; <c>ESTRINGS.TXT</c> when the language has no table of its own.</returns>
    /// <remarks>
    /// Public and separate because <see cref="DrivingMap"/> reads the same file for the
    /// stops' names, and two places deciding which file that is would one day disagree.
    /// </remarks>
    public static string TableFor(GameArchives archives)
    {
        ArgumentNullException.ThrowIfNull(archives);

        return archives.Localization?.Language.StringTable is { } named && archives.Exists(named)
            ? named
            : Names.English;
    }

    /// <summary>The names the string table goes by.</summary>
    private static class Names
    {
        /// <summary>What every localisation that did not rename it calls it.</summary>
        internal const string English = "ESTRINGS.TXT";
    }

    /// <summary>Reads the file's text.</summary>
    /// <param name="text">Its contents.</param>
    /// <returns>The names.</returns>
    public static GameStrings Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new GameStrings(KeyedText.Parse(text, "ESTRINGS.TXT"));
    }

    /// <summary>What a location is called.</summary>
    /// <param name="location">Its three-letter code, in any case.</param>
    /// <returns>Its name, or null when the file does not give one.</returns>
    public string? Place(string? location) =>
        location is { Length: > 0 } code ? Named("loc_" + code) : null;

    /// <summary>What a point in the story is called.</summary>
    /// <param name="timeblock">Its code, such as <c>110A</c>.</param>
    /// <returns>Its name, or null when the file does not give one.</returns>
    public string? When(string? timeblock) =>
        timeblock is { Length: > 0 } code ? Named("Day" + code) : null;

    /// <summary>
    /// What one of the player's things is called.
    /// </summary>
    /// <param name="item">Its noun, as the action files spell it: <c>BLACK_MARKER</c>.</param>
    /// <returns>Its name, or null when the file does not give one.</returns>
    /// <remarks>
    /// <para>
    /// 293 of them, under <c>v_black_marker</c> in the tooltips section, and the port had
    /// never read one. What it drew instead was the identifier with its underscores taken
    /// out and its words capitalised — "Abbe Tape" for what the game calls "Tape of Abbé's
    /// phone call", "Black Fibers" for "Black fur".
    /// </para>
    /// <para>
    /// <b>It is also the only per-object text GK3 localised.</b> There is no table of noun
    /// or verb names anywhere in the data — the original drew verbs as icons and never
    /// named the thing under the cursor at all — so these 293 are the whole of what a
    /// French game can say about the player's things in French. Without them a French run
    /// carries a pocketful of English.
    /// </para>
    /// </remarks>
    public string? Item(string? item) =>
        item is { Length: > 0 } noun ? Named("v_" + noun) : null;

    /// <summary>What a stop on the driving map is called.</summary>
    /// <param name="stop">Its code.</param>
    /// <returns>Its name, or null when the file does not give one.</returns>
    public string? Destination(string? stop) =>
        stop is { Length: > 0 } code ? Named("dm_" + code) : null;

    /// <summary>
    /// Where this is, as one line.
    /// </summary>
    /// <param name="location">The location's code.</param>
    /// <param name="timeblock">The point in the story.</param>
    /// <returns>Something to draw in the corner of the screen.</returns>
    /// <remarks>
    /// Falls back to whichever half it has. A location with no name and a timeblock with
    /// one should say the time rather than nothing at all, and the codes are better than an
    /// empty corner.
    /// </remarks>
    public string Where(string? location, string? timeblock)
    {
        string place = Place(location) ?? location ?? string.Empty;
        string when = When(timeblock) ?? timeblock ?? string.Empty;

        if (place.Length == 0)
        {
            return when;
        }

        return when.Length == 0 ? place : $"{place} - {when}";
    }

    /// <summary>
    /// Whether a noun is one of the exits the artists numbered rather than named.
    /// </summary>
    /// <param name="noun">The noun a scene gives an object.</param>
    /// <returns>True for <c>EXIT</c> and <c>EXIT1</c> through <c>EXIT5</c>.</returns>
    /// <remarks>
    /// 33 of the corpus's ways out are one of these, and the number means nothing: RC1's
    /// six are <c>EXIT</c>, <c>EXIT1</c> and <c>EXIT2</c> for two different streets and a
    /// moped shop, in no order anybody could infer. The ones somebody troubled to name —
    /// <c>EXIT_TO_ROAD</c>, <c>EXIT_PATH</c>, <c>EXIT_CDB</c> — are left alone.
    /// </remarks>
    public static bool IsNumberedExit(string? noun) =>
        noun is { Length: >= 4 } name &&
        name.StartsWith("EXIT", StringComparison.OrdinalIgnoreCase) &&
        name.AsSpan(4).ToString().All(char.IsAsciiDigit);

    /// <summary>
    /// What to call an exit, given the script behind it.
    /// </summary>
    /// <param name="script">The rule's script, which is where the destination is written.</param>
    /// <returns>The place it leads to, or "Exit" when it does not lead to a named one.</returns>
    /// <remarks>
    /// <para>
    /// Derived rather than invented. <c>EXIT3, EXIT_RIGHT, ALL, script={SetLocation("rc3");}</c>
    /// says where the door goes and <c>loc_rc3</c> says what that place is called, so the
    /// interface can put the name of the street on the street.
    /// </para>
    /// <para>
    /// An exit that opens something other than a room gets the bare word. RC1's
    /// <c>EXIT5</c> is one — it raises the driving map — and so is any whose destination
    /// the string table has no name for.
    /// </para>
    /// </remarks>
    public string ExitName(string? script) =>
        script is { Length: > 0 } text &&
        Destination().Match(text) is { Success: true } sets &&
        Place(sets.Groups[1].Value) is { Length: > 0 } place
            ? place
            : "Exit";

    [System.Text.RegularExpressions.GeneratedRegex(
        "SetLocation\\s*\\(\\s*\"([A-Za-z0-9_]+)\"",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex Destination();

    /// <summary>
    /// The score, as the game writes it.
    /// </summary>
    /// <param name="score">What the player has.</param>
    /// <param name="most">What there is to get.</param>
    /// <returns>Something to draw, or null when the file gives no format for it.</returns>
    /// <remarks>
    /// <c>ScoreText = Score: %03d of %03d</c>, which is a C format string and the reason
    /// the numbers are padded to three digits in the original's toolbar. The placeholders
    /// are filled here rather than by a formatter, because there are exactly two of them
    /// and running arbitrary format strings from a data file is a larger thing to own.
    /// </remarks>
    public string? Score(int score, int most)
    {
        if (Named("ScoreText") is not { Length: > 0 } format)
        {
            return null;
        }

        int first = format.IndexOf("%03d", StringComparison.Ordinal);

        if (first < 0)
        {
            return format;
        }

        int second = format.IndexOf("%03d", first + 4, StringComparison.Ordinal);

        string filled = format[..first] +
            score.ToString("000", System.Globalization.CultureInfo.InvariantCulture) +
            (second < 0
                ? format[(first + 4)..]
                : format[(first + 4)..second] +
                  most.ToString("000", System.Globalization.CultureInfo.InvariantCulture) +
                  format[(second + 4)..]);

        return filled;
    }

    /// <summary>One value, from whichever section holds it.</summary>
    /// <remarks>
    /// The location and timeblock names are in the file's unnamed opening section and the
    /// tooltips are in <c>[ToolTips]</c>, and no caller cares which. A blank value counts
    /// as absent: several keys in the file are declared and empty.
    /// </remarks>
    private string? Named(string key)
    {
        string? found = _text.Value(string.Empty, key) ?? _text.Value("ToolTips", key);

        return found is { Length: > 0 } value && value.Trim().Length > 0 ? value.Trim() : null;
    }
}
