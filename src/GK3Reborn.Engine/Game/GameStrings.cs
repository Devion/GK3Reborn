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
public sealed partial class GameStrings
{
    private readonly KeyedText _text;

    private GameStrings(KeyedText text) => _text = text;

    /// <summary>An empty set, for a run with no archives.</summary>
    public static GameStrings None { get; } = new(KeyedText.Parse(string.Empty));

    /// <summary>How many names it holds.</summary>
    public int Count => _text.Section(string.Empty).Count + _text.Section("ToolTips").Count;

    /// <summary>Which file the names were read from.</summary>
    public string File { get; private init; } = Names.English;

    /// <summary>Reads the file out of the archives.</summary>
    /// <param name="archives">The game's archives.</param>
    /// <returns>The names, empty when no archive holds the file.</returns>
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
    public static bool IsNumberedExit(string? noun) =>
        noun is { Length: >= 4 } name &&
        name.StartsWith("EXIT", StringComparison.OrdinalIgnoreCase) &&
        name.AsSpan(4).ToString().All(char.IsAsciiDigit);

    /// <summary>
    /// What to call an exit, given the script behind it.
    /// </summary>
    /// <param name="script">The rule's script, which is where the destination is written.</param>
    /// <returns>The place it leads to, or "Exit" when it does not lead to a named one.</returns>
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
    private string? Named(string key)
    {
        string? found = _text.Value(string.Empty, key) ?? _text.Value("ToolTips", key);

        return found is { Length: > 0 } value && value.Trim().Length > 0 ? value.Trim() : null;
    }
}
