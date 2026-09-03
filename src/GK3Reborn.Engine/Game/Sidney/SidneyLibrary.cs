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
    /// <remarks>
    /// The game's own main menu does not carry a row for this — its file list lives on the
    /// front screen — but the original's screen bar does, and the art for the button is in
    /// the archives beside the other seven. On a desktop the file store is a place you go
    /// to rather than something the desktop itself shows, so it is a screen here.
    /// </remarks>
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
    /// <remarks>
    /// <para>
    /// The file gives an address and nothing else — <c>RT_Nakimura@aol.com</c> — and a list
    /// of addresses is a list nobody reads. What is in front of the at-sign is what the
    /// sender called themselves, so underscores become spaces and the result is offered as
    /// their name, with the address still shown in the message itself.
    /// </para>
    /// <para>
    /// <b>Full stops are left alone</b>, because the sixth message is from
    /// <c>s.pam@easteregg.com</c> and turning that into "s pam" throws the joke away.
    /// </para>
    /// </remarks>
    public string Sender
    {
        get
        {
            string local = From.Split('@')[0];

            return local.Length == 0 ? From : local.Replace('_', ' ').Trim();
        }
    }

    /// <summary>When it arrived, without the year, for a list that has one column for it.</summary>
    /// <remarks>
    /// The file writes "Jul 1, 1998, 7:25am", which is a date, a year and a time. A list
    /// with one narrow column for it shows the day: the year is the same for all six, and
    /// the header inside the message still carries the whole thing.
    /// </remarks>
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
    /// <remarks>
    /// <para>
    /// <b>Rendered from the character's own head</b> rather than cut from anything the game
    /// ships: the original's suspect screen has no portraits at all, and the pictures that
    /// exist elsewhere are inventory-sized. <c>render-model --portrait</c> frames the head,
    /// turns it three-eighths of a turn and subdivides it, which is a far better face than
    /// any 1999 asset of one.
    /// </para>
    /// <para>
    /// Which model is whom comes out of the scene files, not out of a guess at the initials:
    /// <c>model=lad</c> is Girard rather than Lady Howard, and Lady Howard is <c>lmo</c>.
    /// Read off <c>model=X, noun=Y</c> across every SIF.
    /// </para>
    /// </remarks>
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
    /// <remarks>
    /// <para>
    /// <b>Not the surname.</b> Three of them are known to the game by something else — the
    /// Abbé by his title, Estelle Stiles and Larry Chester by their first names — and every
    /// piece of evidence is named after the noun, not the name on the suspect list. Reading
    /// a surname off <c>Name</c> made <c>ABBE_FINGERPRINT</c>, <c>ESTELLES_FINGERPRINT</c>
    /// and <c>LARRYS_FINGERPRINT</c> match nobody at all, so three suspects could never be
    /// convicted of anything.
    /// </para>
    /// <para>
    /// Read off the game's own names: the nine <c>*_FINGERPRINT</c> and five
    /// <c>*_LICENSE</c> items, the actors its conversations address, and the four
    /// <c>Matched…</c> flags its scripts write — <c>MatchedButhane</c>,
    /// <c>MatchedBuchelli</c>, <c>MatchedEstelle</c>, <c>MatchedMosely</c> — which agree
    /// with each other exactly.
    /// </para>
    /// </remarks>
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
    /// <remarks>
    /// <b>The data draws the line itself.</b> Five of the ten carry a plate — VDG945F,
    /// HJK841J, FKS427G, FED039A, ASD257K — and those five are exactly the five
    /// <c>*_LICENSE</c> items the player can photograph. The other five carry what one could
    /// tell by looking: "Van", "Blue Sedan", "Auto?", and for the Abbé the game's own
    /// "Unknown". So a registration is something learned by linking a plate; a description
    /// is not, and hiding it would hide something the player already saw.
    /// </remarks>
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
    /// <remarks>
    /// <para>
    /// <b>Two of the six messages are illustrated and neither carries its own content.</b>
    /// Their bodies say only that a result was "added to files group"; what the original
    /// draws under that is kept in <c>ESIDNEY.TXT</c>'s e-mail section as <c>HermFile1</c>
    /// and <c>SolomonFile1</c> onwards — the symbol search with its four alchemical signs,
    /// and the layout of the Temple of Solomon.
    /// </para>
    /// <para>
    /// The four lines of the symbol search that begin at an equals sign — "=  'to mix'" —
    /// are written that way because the symbol is drawn where the words would start. They
    /// take <c>SID_SYMB_1</c> to <c>SID_SYMB_4</c> in order.
    /// </para>
    /// <para>
    /// Keyed on the message's own identifier rather than on its subject, which is translated
    /// in every localisation the game shipped in.
    /// </para>
    /// </remarks>
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
    /// <remarks>
    /// The file writes a caret for the rule between ADD DATA and E-MAIL. It is a separator
    /// rather than a row, and offering it as one is offering the player a menu item called
    /// <c>^</c>.
    /// </remarks>
    /// <summary>
    /// A numbered run of lines — <c>AbbeTape1</c>, <c>AbbeTape2</c> — in order.
    /// </summary>
    /// <param name="section">Which section they are in.</param>
    /// <param name="prefix">What the keys are called before their number.</param>
    /// <returns>The lines, which is empty when there are none.</returns>
    /// <remarks>
    /// The file's own way of writing anything longer than a line: a message's paragraphs, a
    /// telephone call's turns, a list of suspects. It stops at the first gap, because the
    /// files number from one and a gap is a mistake rather than a signal.
    /// </remarks>
    public IReadOnlyList<string> Lines(string section, string prefix) =>
        _text.Run(section, prefix);

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
                _mail.Run(id, "Body"),
                _mail.Value(id, "CC") ?? string.Empty));
        }

        return messages;
    }
}
