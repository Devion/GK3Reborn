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

    /// <summary>
    /// Everything that has been scanned in, as a list.
    /// </summary>
    Files,
}

/// <summary>A message in Grace's inbox.</summary>
/// <param name="Id">Which message, as the file names it.</param>
/// <param name="Subject">Its subject line.</param>
/// <param name="From">Who sent it.</param>
/// <param name="To">Who it was sent to.</param>
/// <param name="Date">When, as the file writes it.</param>
/// <param name="Body">Its paragraphs.</param>
/// <param name="Cc">Who else it went to, which for most of them is nobody.</param>
public sealed record SidneyMail(
    string Id,
    string Subject,
    string From,
    string To,
    string Date,
    IReadOnlyList<string> Body,
    string Cc = "")
{
    /// <summary>
    /// Who sent it, as a name rather than an address.
    /// </summary>
    public string Sender
    {
        get
        {
            string local = From.Split('@')[0];

            return local.Length == 0 ? From : local.Replace('_', ' ').Trim();
        }
    }

    /// <summary>When it arrived, without the year, for a list that has one column for it.</summary>
    public string When =>
        Date.Split(',', StringSplitOptions.TrimEntries) is [string day, ..] ? day : Date;
}

/// <summary>Somebody Sidney keeps a file on.</summary>
/// <param name="Index">Which of the ten, from one.</param>
/// <param name="Name">Their name.</param>
/// <param name="Nationality">Where they are from.</param>
/// <param name="Vehicle">What they drive, as far as anybody knows.</param>
public sealed record SidneySuspect(int Index, string Name, string Nationality, string Vehicle)
{
    /// <summary>
    /// The picture of this suspect, or an empty string where there is none.
    /// </summary>
    public string Portrait => Index switch
    {
        1 => "PORTRAIT_MAD",   // Madeline Buthane
        2 => "PORTRAIT_VIT",   // Vittorio Buchelli
        3 => "PORTRAIT_EML",   // Emilio Baza
        4 => "PORTRAIT_ABE",   // Abbé Arnaud
        5 => "PORTRAIT_LMO",   // Lady Howard
        6 => "PORTRAIT_EST",   // Estelle Stiles
        7 => "PORTRAIT_WIL",   // John Wilkes
        8 => "PORTRAIT_LAR",   // Larry Chester
        9 => "PORTRAIT_MON",   // Excelsior Montreaux
        10 => "PORTRAIT_MOS",  // Franklin Mosely
        _ => string.Empty,
    };

    /// <summary>
    /// What the game calls this person: the noun its own scripts, items and flags use.
    /// </summary>
    public string Noun => Index switch
    {
        1 => "Buthane",     // Madeline Buthane
        2 => "Buchelli",    // Vittorio Buchelli
        3 => "Emilio",      // Emilio Baza, who leaves no print
        4 => "Abbe",        // Abbé Arnaud, known by his title
        5 => "Howard",      // Lady Howard
        6 => "Estelle",     // Estelle Stiles, known by her first name
        7 => "Wilkes",      // John Wilkes
        8 => "Larry",       // Larry Chester, known by his first name
        9 => "Montreaux",   // Excelsior Montreaux
        10 => "Mosely",     // Franklin Mosely
        _ => string.Empty,
    };

    /// <summary>
    /// Whether this suspect's vehicle is recorded as a registration rather than a description.
    /// </summary>
    public bool Registered =>
        Vehicle.Length == 7 &&
        char.IsAsciiLetterUpper(Vehicle[0]) &&
        char.IsAsciiLetterUpper(Vehicle[1]) &&
        char.IsAsciiLetterUpper(Vehicle[2]) &&
        char.IsAsciiDigit(Vehicle[3]) &&
        char.IsAsciiDigit(Vehicle[4]) &&
        char.IsAsciiDigit(Vehicle[5]) &&
        char.IsAsciiLetterUpper(Vehicle[6]);

    /// <summary>Every portrait there is, for whoever loads them.</summary>
    public static IReadOnlyList<string> Portraits =>
    [
        .. Enumerable.Range(1, 10)
            .Select(index => new SidneySuspect(index, string.Empty, string.Empty, string.Empty)
                .Portrait)
            .Where(name => name.Length > 0),
    ];
}

/// <summary>One of the identities Sidney can print.</summary>
/// <param name="Category">Which trade — MEDICAL, REPORTER, REPAIR, SALES, POLICE.</param>
/// <param name="Title">The job on the card.</param>
/// <param name="Key">
/// What <c>ESIDNEY.TXT</c> calls the row — <c>Menu2Item1</c> — which is the same in every
/// release while the title is not.
/// </param>
public sealed record SidneyIdentity(string Category, string Title, string Key = "")
{
    /// <summary>
    /// The fifteen jobs, by the menu and row that offer them. These names are not in the
    /// game's data anywhere: they are the retail engine's, and they are what the card's
    /// picture is filed under — <c>GAB_NYTIMES.BMP</c>, <c>GRA_DOC.BMP</c> — so they have to
    /// be spelt exactly this way.
    /// </summary>
    private static readonly string[][] Jobs =
    [
        ["DOC", "CORONER", "BLOOD"],                      // Medical
        ["NYTIMES", "FREELANCE", "EMONTHLY", "SPORTSI"],  // Reporter
        ["ELEC", "PLUMB"],                                // Repair
        ["ENCY", "SHOES", "AUTO", "DIAPER"],              // Sales
        ["NOPD", "SECURITY"],                             // Police
    ];

    /// <summary>
    /// What the retail engine calls this job, or an empty string for a row it does not know.
    /// </summary>
    public string Job
    {
        get
        {
            // Menu{n}Item{m}, which is the only thing about a row that is the same in every
            // release: the job on it is translated and its position is not.
            if (!Key.StartsWith("Menu", StringComparison.Ordinal) ||
                Key.IndexOf("Item", StringComparison.Ordinal) is not (> 4 and { } at) ||
                !int.TryParse(Key[4..at], out int menu) ||
                !int.TryParse(Key[(at + 4)..], out int item) ||
                menu < 1 || menu > Jobs.Length ||
                item < 1 || item > Jobs[menu - 1].Length)
            {
                return string.Empty;
            }

            return Jobs[menu - 1][item - 1];
        }
    }
}

/// <summary>
/// One of the answers Sidney offers to a question it has asked.
/// </summary>
/// <param name="Key">
/// What <c>ESIDNEY.TXT</c> calls it — <c>French</c>, <c>Yes</c> — which is the same in
/// every release.
/// </param>
/// <param name="Text">What the player reads, which is not.</param>
public sealed record SidneyChoice(string Key, string Text);

/// <summary>What one of Sidney's operations produced.</summary>
/// <param name="Text">What the machine says, which may be several paragraphs.</param>
/// <param name="Asks">A question the player has to answer, or null.</param>
/// <param name="Choices">The answers, where it asks one.</param>
/// <param name="Produced">A Sidney file this created, or null.</param>
public sealed record SidneyResult(
    string Text,
    string? Asks = null,
    IReadOnlyList<SidneyChoice>? Choices = null,
    string? Produced = null);

/// <summary>
/// Everything Sidney is told, read from the game's own text.
/// </summary>
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

    /// <summary>A line of a message's attachment, and the picture that goes beside it.</summary>
    /// <param name="Text">What the line says.</param>
    /// <param name="Picture">
    /// The bitmap to draw to its left, without extension, or null where the line is prose.
    /// </param>
    public readonly record struct MailLine(string Text, string? Picture = null);

    /// <summary>
    /// What a message from Sidney itself has attached to it.
    /// </summary>
    /// <param name="id">Which message, as <c>ESIDNEYEMAIL.TXT</c> keys it.</param>
    /// <returns>The attachment's lines, which is empty for a message that has none.</returns>
    public IReadOnlyList<MailLine> Attachment(string id)
    {
        ArgumentNullException.ThrowIfNull(id);

        if (id.Equals("EMail4", StringComparison.OrdinalIgnoreCase))
        {
            return [.. Lines("EMail Screen", "SolomonFile").Select(line => new MailLine(line))];
        }

        if (!id.Equals("EMail5", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        List<MailLine> lines = [];
        int symbol = 0;

        foreach (string line in Lines("EMail Screen", "HermFile"))
        {
            bool illustrated = line.StartsWith('=') && symbol < SidneyPictures.Symbols.Count;

            lines.Add(new MailLine(line, illustrated ? SidneyPictures.Symbols[symbol++] : null));
        }

        return lines;
    }

    /// <summary>The eight rows of the main menu, in order, without the separator.</summary>
    /// <summary>
    /// A numbered run of lines — <c>AbbeTape1</c>, <c>AbbeTape2</c> — in order.
    /// </summary>
    /// <param name="section">Which section they are in.</param>
    /// <param name="prefix">What the keys are called before their number.</param>
    /// <returns>The lines, which is empty when there are none.</returns>
    public IReadOnlyList<string> Lines(string section, string prefix) =>
        _text.Run(section, prefix);

    public IReadOnlyList<string> MainMenu() =>
        [.. _text.Run("Main Screen", "MenuItem").Where(item => item != "^")];

    /// <summary>
    /// The screens the main menu names, paired with what they are called.
    /// </summary>
    public IReadOnlyList<(SidneyScreen Screen, string Label)> Rows()
    {
        List<(SidneyScreen, string)> rows = [];
        IReadOnlyList<string> items = _text.Run("Main Screen", "MenuItem");

        for (int i = 0; i < items.Count; i++)
        {
            if (items[i] != "^" && items[i].Length > 0 && ScreenFor(i + 1) is { } screen)
            {
                rows.Add((screen, items[i]));
            }
        }

        return rows;
    }

    /// <summary>Which screen a menu row opens.</summary>
    /// <param name="item">The row's number, from one, as the file keys it.</param>
    /// <returns>The screen, or null for a row that is not one — the rule, and <c>EXIT</c>.</returns>
    private static SidneyScreen? ScreenFor(int item) => item switch
    {
        1 => SidneyScreen.Search,
        2 => SidneyScreen.Analyze,
        3 => SidneyScreen.Translate,
        4 => SidneyScreen.MakeId,
        5 => SidneyScreen.Suspects,
        6 => SidneyScreen.AddData,
        7 => SidneyScreen.EMail,
        _ => null,
    };

    /// <summary>
    /// The ten people Sidney keeps a file on.
    /// </summary>
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

            for (int row = 0; row < rows.Count; row++)
            {
                string title = rows[row];

                if (title.Length > 0 && title != "^")
                {
                    identities.Add(new SidneyIdentity(
                        category,
                        title,
                        string.Create(
                            System.Globalization.CultureInfo.InvariantCulture,
                            $"Menu{number}Item{row + 1}")));
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
                _mail.Run(id, "Body"),
                _mail.Value(id, "CC") ?? string.Empty));
        }

        return messages;
    }
}
