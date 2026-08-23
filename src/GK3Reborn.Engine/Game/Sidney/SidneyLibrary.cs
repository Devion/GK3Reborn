// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using GK3Reborn.Content;
using GK3Reborn.Formats.Ui;

namespace GK3Reborn.Game.Sidney;

/// <summary>One of Sidney's own screens.</summary>
public enum SidneyScreen
{
    /// <summary>The list of the other seven.</summary>
    Main,

    /// <summary>Look something up.</summary>
    Search,

    /// <summary>Open a file and run operations on it.</summary>
    Analyze,

    /// <summary>Turn a language into English.</summary>
    Translate,

    /// <summary>Compose an identity card.</summary>
    MakeId,

    /// <summary>Who is who, and which fingerprint is whose.</summary>
    Suspects,

    /// <summary>Put something from the player's pocket into the machine.</summary>
    AddData,

    /// <summary>Grace's mail.</summary>
    EMail,
}

/// <summary>A message in Grace's inbox.</summary>
/// <param name="Id">Which message, as the file names it.</param>
/// <param name="Subject">Its subject line.</param>
/// <param name="From">Who sent it.</param>
/// <param name="To">Who it was sent to.</param>
/// <param name="Date">When, as the file writes it.</param>
/// <param name="Body">Its paragraphs.</param>
public sealed record SidneyMail(
    string Id,
    string Subject,
    string From,
    string To,
    string Date,
    IReadOnlyList<string> Body);

/// <summary>Somebody Sidney keeps a file on.</summary>
/// <param name="Index">Which of the ten, from one.</param>
/// <param name="Name">Their name.</param>
/// <param name="Nationality">Where they are from.</param>
/// <param name="Vehicle">What they drive, as far as anybody knows.</param>
public sealed record SidneySuspect(int Index, string Name, string Nationality, string Vehicle);

/// <summary>One of the identities Sidney can print.</summary>
/// <param name="Category">Which trade — MEDICAL, REPORTER, REPAIR, SALES, POLICE.</param>
/// <param name="Title">The job on the card.</param>
public sealed record SidneyIdentity(string Category, string Title);

/// <summary>What one of Sidney's operations produced.</summary>
/// <param name="Text">What the machine says, which may be several paragraphs.</param>
/// <param name="Asks">A question the player has to answer, or null.</param>
/// <param name="Choices">The answers, where it asks one.</param>
/// <param name="Produced">A Sidney file this created, or null.</param>
public sealed record SidneyResult(
    string Text,
    string? Asks = null,
    IReadOnlyList<string>? Choices = null,
    string? Produced = null);

/// <summary>
/// Everything Sidney is told, read from the game's own text.
/// </summary>
/// <remarks>
/// <para>
/// Sidney is a portable computer Grace carries, and the story runs through it: parchments
/// are scanned into it, analysed, and translated, and the results are what let the next
/// scene happen. It is not decoration and it cannot be skipped.
/// </para>
/// <para>
/// <b>Almost all of it is data.</b> <c>ESIDNEY.TXT</c> holds the menus, the screen names,
/// and — the part that matters — the actual output of every analysis, keyed by what was
/// analysed: <c>AnalyzeParch1</c>, <c>ExtractParch1</c>, <c>Parch1French</c>. What the
/// engine has to supply is not the words but the machine: which files exist, which
/// operation applies to which, what each one unlocks, and what the story is allowed to ask
/// about afterwards. That is what this is.
/// </para>
/// <para>
/// <b>Files are named by the story.</b> <c>DoesSidneyFileExist("fileParchment1")</c> is a
/// real condition in <c>R31210A.NVC</c>, and there are eight such names in the whole game.
/// Scanning is an ordinary action — the noun is the inventory item, the verb is
/// <c>SCANNER</c>, and the game's own action files carry the scripts — so what Sidney adds
/// is the mapping from the item that was scanned to the file it becomes.
/// </para>
/// </remarks>
public sealed class SidneyLibrary
{
    private readonly KeyedText _text;
    private readonly KeyedText _mail;

    private SidneyLibrary(KeyedText text, KeyedText mail)
    {
        _text = text;
        _mail = mail;
    }

    /// <summary>An empty library, for a run with no game data.</summary>
    public static SidneyLibrary Empty { get; } =
        new(KeyedText.Parse(string.Empty), KeyedText.Parse(string.Empty));

    /// <summary>Whether the game's own text was found.</summary>
    public bool Loaded => _text.Has("Main Screen");

    /// <summary>Reads Sidney's text out of the archives.</summary>
    /// <param name="archives">The game's data.</param>
    /// <returns>The library, which is empty when the files are not there.</returns>
    public static SidneyLibrary Open(GameArchives archives)
    {
        ArgumentNullException.ThrowIfNull(archives);

        return new SidneyLibrary(
            KeyedText.Parse(archives.ReadText("ESIDNEY.TXT") ?? string.Empty, "ESIDNEY.TXT"),
            KeyedText.Parse(archives.ReadText("ESIDNEYEMAIL.TXT") ?? string.Empty, "ESIDNEYEMAIL.TXT"));
    }

    /// <summary>Reads Sidney's text from strings, for tests.</summary>
    /// <param name="text">The contents of <c>ESIDNEY.TXT</c>.</param>
    /// <param name="mail">The contents of <c>ESIDNEYEMAIL.TXT</c>.</param>
    /// <returns>The library.</returns>
    public static SidneyLibrary From(string text, string mail = "") =>
        new(KeyedText.Parse(text, "ESIDNEY.TXT"), KeyedText.Parse(mail, "ESIDNEYEMAIL.TXT"));

    /// <summary>One of the strings in <c>ESIDNEY.TXT</c>.</summary>
    /// <param name="key">Its key, in the main screen's section unless one is named.</param>
    /// <param name="section">Which section to look in.</param>
    /// <returns>The string, or an empty one.</returns>
    public string Say(string key, string section = "Main Screen") =>
        _text.Value(section, key) ?? string.Empty;

    /// <summary>The eight rows of the main menu, in order, without the separator.</summary>
    /// <remarks>
    /// The file writes a caret for the rule between ADD DATA and E-MAIL. It is a separator
    /// rather than a row, and offering it as one is offering the player a menu item called
    /// <c>^</c>.
    /// </remarks>
    public IReadOnlyList<string> MainMenu() =>
        [.. _text.Run("Main Screen", "MenuItem").Where(item => item != "^")];

    /// <summary>The screens the main menu names, paired with what they are called.</summary>
    public IReadOnlyList<(SidneyScreen Screen, string Label)> Rows()
    {
        List<(SidneyScreen, string)> rows = [];

        foreach (string label in MainMenu())
        {
            if (ScreenFor(label) is { } screen)
            {
                rows.Add((screen, label));
            }
        }

        return rows;
    }

    /// <summary>Which screen a menu row opens.</summary>
    /// <param name="label">The row's label, as the game's text writes it.</param>
    /// <returns>The screen, or null for a row that is not one — <c>EXIT</c>.</returns>
    private static SidneyScreen? ScreenFor(string label) => label.ToUpperInvariant() switch
    {
        "SEARCH" => SidneyScreen.Search,
        "ANALYZE" => SidneyScreen.Analyze,
        "TRANSLATE" => SidneyScreen.Translate,
        "MAKE I.D." => SidneyScreen.MakeId,
        "SUSPECTS" => SidneyScreen.Suspects,
        "ADD DATA" => SidneyScreen.AddData,
        "E-MAIL" => SidneyScreen.EMail,
        _ => null,
    };

    /// <summary>
    /// The ten people Sidney keeps a file on.
    /// </summary>
    /// <remarks>
    /// Names, nationalities and vehicle identifications all come out of the game's own
    /// text, which lists them in parallel numbered runs.
    /// </remarks>
    public IReadOnlyList<SidneySuspect> Suspects()
    {
        List<SidneySuspect> people = [];

        IReadOnlyList<string> names = _text.Run("Suspects Screen", "Name");
        IReadOnlyList<string> nations = _text.Run("Suspects Screen", "Nationality");
        IReadOnlyList<string> vehicles = _text.Run("Suspects Screen", "VehicleID");

        for (int i = 0; i < names.Count; i++)
        {
            people.Add(new SidneySuspect(
                i + 1,
                names[i],
                i < nations.Count ? nations[i] : string.Empty,
                i < vehicles.Count ? vehicles[i] : string.Empty));
        }

        return people;
    }

    /// <summary>
    /// The identities Sidney can print, by trade.
    /// </summary>
    /// <remarks>
    /// Five menus of two to four jobs each, written as <c>Menu1Item1</c> under the make-ID
    /// screen. Grace uses one of these to get somebody to open a door.
    /// </remarks>
    public IReadOnlyList<SidneyIdentity> Identities()
    {
        List<SidneyIdentity> identities = [];

        for (int menu = 1; menu <= 5; menu++)
        {
            string number = menu.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string category = _text.Value("MakeID Screen", "Menu" + number + "Name") ?? string.Empty;

            if (category.Length == 0)
            {
                continue;
            }

            // The first menu writes its rows as MenuItemN and the rest as MenuNItemM,
            // which is the same inconsistency the analyze screen has.
            IReadOnlyList<string> rows = _text.Run("MakeID Screen", "Menu" + number + "Item");

            foreach (string title in rows)
            {
                if (title.Length > 0 && title != "^")
                {
                    identities.Add(new SidneyIdentity(category, title));
                }
            }
        }

        return identities;
    }

    /// <summary>Grace's inbox, in the order the file lists it.</summary>
    public IReadOnlyList<SidneyMail> Mail()
    {
        List<SidneyMail> messages = [];

        foreach ((string id, string subject) in _mail.Section("EMail Files"))
        {
            messages.Add(new SidneyMail(
                id,
                subject,
                _mail.Value(id, "From") ?? string.Empty,
                _mail.Value(id, "To") ?? string.Empty,
                _mail.Value(id, "Date") ?? string.Empty,
                _mail.Run(id, "Body")));
        }

        return messages;
    }
}
