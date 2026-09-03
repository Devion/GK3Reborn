// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Numerics;
using GK3Reborn.Game.Sidney;

namespace GK3Reborn.UI.Sidney;

/// <summary>
/// The programs on Sidney's desktop.
/// </summary>
/// <remarks>
/// <para>
/// One method each, all drawing into the rectangle the window frame leaves them, and all of
/// them putting their lists inside a scrolling region rather than stopping at the bottom of
/// the glass. That last part is not a nicety: the suspects list held ten people and drew
/// nine, and the tenth is the one whose print is worth linking.
/// </para>
/// <para>
/// The words are the game's own throughout — <see cref="SidneyLibrary"/> reads them out of
/// <c>ESIDNEY.TXT</c> — so a screen says what the original said even where this arranges it
/// differently.
/// </para>
/// </remarks>
public static class SidneyApps
{
    /// <summary>The programs, in the order they sit on the desktop.</summary>
    private static readonly SidneyScreen[] Order =
    [
        SidneyScreen.Search,
        SidneyScreen.EMail,
        SidneyScreen.Files,
        SidneyScreen.Analyze,
        SidneyScreen.Translate,
        SidneyScreen.AddData,
        SidneyScreen.MakeId,
        SidneyScreen.Suspects,
    ];

    /// <summary>What the game's text calls each screen's section.</summary>
    private static string SectionOf(SidneyScreen screen) => screen switch
    {
        SidneyScreen.Search => "Search Screen",
        SidneyScreen.Analyze => "Analyze Screen",
        SidneyScreen.Translate => "Translate Screen",
        SidneyScreen.MakeId => "MakeID Screen",
        SidneyScreen.Suspects => "Suspects Screen",
        SidneyScreen.AddData => "AddData Screen",
        SidneyScreen.EMail => "EMail Screen",
        _ => "Main Screen",
    };

    /// <summary>The name plate the original drew for a screen.</summary>
    /// <param name="screen">Which screen.</param>
    /// <param name="lit">Whether to take the hovered state.</param>
    /// <returns>The bitmap's file name.</returns>
    public static string PlateOf(SidneyScreen screen, bool lit)
    {
        string name = screen switch
        {
            SidneyScreen.Search => "B_SEARCH",
            SidneyScreen.EMail => "B_EMAIL",
            SidneyScreen.Files => "B_FILES",
            SidneyScreen.Analyze => "B_ANALYZE",
            SidneyScreen.Translate => "B_TRANSL",
            SidneyScreen.AddData => "B_ADDATA",
            SidneyScreen.MakeId => "B_MAKEID",
            SidneyScreen.Suspects => "B_SUSPT",
            _ => "B_FILES",
        };

        return name + (lit ? "_H" : "_U") + ".BMP";
    }

    /// <summary>What a screen is called, in the game's own words where it has them.</summary>
    /// <param name="machine">The machine, for its text.</param>
    /// <param name="screen">Which screen.</param>
    /// <returns>The name.</returns>
    public static string NameOf(SidneyMachine machine, SidneyScreen screen)
    {
        ArgumentNullException.ThrowIfNull(machine);

        if (screen == SidneyScreen.Files)
        {
            return "FILES";
        }

        return machine.Library.Say("ScreenName", SectionOf(screen)) is { Length: > 0 } named
            ? named
            : screen.ToString().ToUpperInvariant();
    }

    /// <summary>The programs and what they are called.</summary>
    /// <param name="machine">The machine, for its text.</param>
    /// <returns>Each screen with its name.</returns>
    public static IReadOnlyList<(SidneyScreen Screen, string Label)> Programs(
        SidneyMachine machine) =>
        [.. Order.Select(screen => (screen, NameOf(machine, screen)))];

    /// <summary>
    /// Draws whichever program is open.
    /// </summary>
    /// <param name="surface">Where to draw.</param>
    /// <param name="machine">The machine.</param>
    /// <param name="view">What the screens know about the game.</param>
    /// <param name="body">The room the window frame leaves.</param>
    /// <returns>Where the map was drawn, when this screen drew one.</returns>
    public static Vector4 Draw(
        SidneySurface surface, SidneyMachine machine, ScreenView view, Vector4 body)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(machine);

        switch (machine.Screen)
        {
            case SidneyScreen.Files:
                Files(surface, machine, body);
                break;

            case SidneyScreen.EMail:
                Mail(surface, machine, body);
                break;

            case SidneyScreen.AddData:
                AddData(surface, machine, view, body);
                break;

            case SidneyScreen.Search:
                Search(surface, machine, body);
                break;

            case SidneyScreen.Suspects:
                Suspects(surface, machine, view, body);
                break;

            case SidneyScreen.MakeId:
                MakeId(surface, machine, body);
                break;

            case SidneyScreen.Translate:
                Translate(surface, machine, body);
                break;

            case SidneyScreen.Analyze:
                return Analyze(surface, machine, view, body);

            default:
                surface.Write("Nothing to show.", body.X, body.Y, SidneyPalette.Dim);
                break;
        }

        return default;
    }

    /// <summary>The file store: everything that has been scanned in.</summary>
    private static void Files(SidneySurface surface, SidneyMachine machine, Vector4 body)
    {
        IReadOnlyList<SidneyFile> files = machine.Files;

        if (files.Count == 0)
        {
            surface.Write(
                "Nothing scanned yet. Use ADD DATA to put something in.",
                body.X,
                body.Y,
                SidneyPalette.Dim);

            return;
        }

        float row = surface.Line + surface.Em(14);
        float step = row + surface.Em(5);
        float offset = surface.BeginScroll("files", body, files.Count * step);
        float width = surface.Room(body, files.Count * step);

        for (int i = 0; i < files.Count; i++)
        {
            var bounds = new Vector4(body.X, body.Y + (i * step) - offset, width, row);

            surface.Fill(bounds, SidneyPalette.Panel);
            surface.Fill(bounds.X, bounds.Y, bounds.Z, 1, SidneyPalette.Rule);

            string kind = Describe(files[i].Kind);
            float kindAt = bounds.X + bounds.Z - surface.Em(10) - surface.Measure(kind);

            surface.WriteIn(
                files[i].Label,
                bounds.X + surface.Em(10),
                bounds.Y + ((row - surface.Line) / 2),
                kindAt - bounds.X - surface.Em(20),
                SidneyPalette.Ink);

            surface.Write(
                kind, kindAt, bounds.Y + ((row - surface.Line) / 2), SidneyPalette.Dim);

            surface.Hit("sidney:file:" + files[i].Id, bounds);
        }

        surface.EndScroll();
    }

    /// <summary>
    /// The mail: a list of messages on the left and the one open beside it.
    /// </summary>
    /// <remarks>
    /// Sender, subject and when it arrived, which is what a mail program has shown since
    /// before this one was set. The first pass listed the six subjects alone, which is the
    /// data the file happens to be keyed on rather than the thing a reader wants.
    /// </remarks>
    private static void Mail(SidneySurface surface, SidneyMachine machine, Vector4 body)
    {
        IReadOnlyList<SidneyMail> inbox = machine.Library.Mail();

        if (inbox.Count == 0)
        {
            surface.Write("No messages.", body.X, body.Y, SidneyPalette.Dim);

            return;
        }

        float listWidth = MathF.Max(body.Z * 0.42f, MathF.Min(body.Z * 0.52f, surface.Em(170)));
        var list = new Vector4(body.X, body.Y, listWidth, body.W);
        float row = (surface.Line * 2) + surface.Em(14);
        float step = row + 1;

        float offset = surface.BeginScroll("mail", list, inbox.Count * step);
        float width = surface.Room(list, inbox.Count * step);

        for (int i = 0; i < inbox.Count; i++)
        {
            SidneyMail mail = inbox[i];
            bool open = machine.Reading?.Id == mail.Id;
            bool unread = !machine.HasRead(mail);

            var bounds = new Vector4(list.X, list.Y + (i * step) - offset, width, row);

            surface.Fill(
                bounds, open || surface.Over(bounds) ? SidneyPalette.PanelLit : SidneyPalette.Panel);

            surface.Fill(bounds.X, bounds.Y + row, bounds.Z, 1, SidneyPalette.Rule);

            // An unread message carries a mark down its edge, which is how a mail program
            // says so without a second column.
            if (unread)
            {
                surface.Fill(bounds.X, bounds.Y, surface.Em(3), row, SidneyPalette.Alert);
            }

            float text = bounds.X + surface.Em(10);
            float edge = bounds.X + bounds.Z - surface.Em(8);
            string when = mail.When;
            float whenAt = edge - surface.Measure(when);

            surface.WriteIn(
                mail.Sender,
                text,
                bounds.Y + surface.Em(5),
                whenAt - text - surface.Em(8),
                unread ? SidneyPalette.Amber : SidneyPalette.Ink);

            surface.Write(when, whenAt, bounds.Y + surface.Em(5), SidneyPalette.Dim);

            surface.WriteIn(
                mail.Subject,
                text,
                bounds.Y + surface.Em(5) + surface.Line,
                edge - text,
                open ? SidneyPalette.Amber : SidneyPalette.Dim);

            surface.Hit("sidney:mail:" + mail.Id, bounds);
        }

        surface.EndScroll();

        // The message itself.
        var pane = new Vector4(
            body.X + listWidth + surface.Em(10),
            body.Y,
            body.Z - listWidth - surface.Em(10),
            body.W);

        if (machine.Reading is not { } reading)
        {
            surface.Write("Select a message.", pane.X, pane.Y, SidneyPalette.Dim);

            return;
        }

        float wrap = pane.Z - surface.Em(20);
        float tall = surface.Line * 4.5f;

        foreach (string paragraph in reading.Body)
        {
            tall += paragraph.Length == 0
                ? surface.Line
                : (surface.Lines(paragraph, wrap) * surface.Line) + (surface.Line / 2);
        }

        // What Sidney attached to its own two messages: the symbol search and the temple.
        IReadOnlyList<SidneyLibrary.MailLine> attached = machine.Library.Attachment(reading.Id);
        float plate = MathF.Min(surface.Em(52), pane.Z * 0.22f);

        foreach (SidneyLibrary.MailLine line in attached)
        {
            tall += line.Picture is null
                ? surface.Lines(line.Text, wrap) * surface.Line
                : MathF.Max(plate, surface.Line) + surface.Em(6);
        }

        if (attached.Count > 0)
        {
            tall += surface.Line * 2;
        }

        float paneOffset = surface.BeginScroll("message", pane, tall);
        float y = pane.Y - paneOffset;

        surface.WriteIn(reading.Subject, pane.X, y, wrap, SidneyPalette.Amber);
        y += surface.Line + surface.Em(2);

        surface.WriteIn($"From: {reading.From}", pane.X, y, wrap, SidneyPalette.Dim);
        y += surface.Line;

        surface.WriteIn($"To: {reading.To}", pane.X, y, wrap, SidneyPalette.Dim);
        y += surface.Line;

        if (reading.Cc is { Length: > 0 } copied)
        {
            surface.WriteIn($"CC: {copied}", pane.X, y, wrap, SidneyPalette.Dim);
            y += surface.Line;
        }

        surface.Write(reading.Date, pane.X, y, SidneyPalette.Dim);
        y += surface.Line + surface.Em(4);

        surface.Fill(pane.X, y, wrap, 1, SidneyPalette.Rule);
        y += surface.Em(8);

        foreach (string paragraph in reading.Body)
        {
            if (paragraph.Length == 0)
            {
                y += surface.Line;

                continue;
            }

            y = surface.Paragraph(
                paragraph, pane.X, y, wrap, pane.Y + pane.W + tall, SidneyPalette.Ink);

            y += surface.Line / 2;
        }

        if (attached.Count > 0)
        {
            y += surface.Line / 2;
            surface.Fill(pane.X, y, wrap, 1, SidneyPalette.Rule);
            y += surface.Line;
        }

        foreach (SidneyLibrary.MailLine line in attached)
        {
            if (line.Picture is null)
            {
                y = surface.Paragraph(
                    line.Text, pane.X, y, wrap, pane.Y + pane.W + tall, SidneyPalette.Ink);

                continue;
            }

            // The symbol where the words would have started, which is why the line begins
            // at its equals sign.
            ItemIcon art = surface.Art(line.Picture + ".BMP");
            float band = MathF.Max(plate, surface.Line);

            if (art.Drawn)
            {
                surface.Draw(art, art.Fit(pane.X, y, plate), SidneyPalette.Alert);
            }

            surface.Write(
                line.Text,
                pane.X + plate + surface.Em(10),
                y + ((band - surface.Line) / 2),
                SidneyPalette.Ink);

            y += band + surface.Em(6);
        }

        surface.EndScroll();
    }

    /// <summary>The scanner: what in the player's pockets Sidney will take.</summary>
    private static void AddData(
        SidneySurface surface, SidneyMachine machine, ScreenView view, Vector4 body)
    {
        List<string> scannable = [.. view.Inventory.Where(machine.CanScan)];

        float half = (body.Z / 2) - surface.Em(10);
        var list = new Vector4(body.X, body.Y, half, body.W);

        if (scannable.Count == 0)
        {
            surface.Write(
                machine.Files.Count > 0
                    ? "Everything you are carrying is already in the machine."
                    : "Nothing here that the scanner will take.",
                body.X,
                body.Y,
                SidneyPalette.Dim);
        }
        else
        {
            float row = surface.Line + surface.Em(14);
            float step = row + surface.Em(5);
            float offset = surface.BeginScroll("scan", list, scannable.Count * step);
            float width = surface.Room(list, scannable.Count * step);

            for (int i = 0; i < scannable.Count; i++)
            {
                var bounds = new Vector4(list.X, list.Y + (i * step) - offset, width, row);
                bool over = surface.Over(bounds);

                surface.Fill(bounds, over ? SidneyPalette.PanelLit : SidneyPalette.Panel);
                surface.Fill(bounds.X, bounds.Y, bounds.Z, 1, over ? SidneyPalette.Amber : SidneyPalette.Rule);

                surface.WriteIn(
                    Pretty(scannable[i]),
                    bounds.X + surface.Em(10),
                    bounds.Y + ((row - surface.Line) / 2),
                    bounds.Z - surface.Em(20),
                    SidneyPalette.Ink);

                surface.Hit("sidney:scan:" + scannable[i], bounds);
            }

            surface.EndScroll();
        }

        if (machine.Showing is { } said)
        {
            surface.Paragraph(
                said.Text,
                body.X + (body.Z / 2) + surface.Em(10),
                body.Y,
                (body.Z / 2) - surface.Em(20),
                body.Y + body.W,
                SidneyPalette.Amber);
        }
    }

    /// <summary>
    /// Search: a box to type in, and a page of the encyclopedia.
    /// </summary>
    /// <remarks>
    /// The subject list is not shown. Three hundred and ninety-one pages offered as a menu
    /// is a walkthrough — the puzzle is knowing what to look up — so the player types, and
    /// what they type is checked against the spellings the game itself lists.
    /// </remarks>
    private static void Search(SidneySurface surface, SidneyMachine machine, Vector4 body)
    {
        float row = surface.Line + surface.Em(12);
        string go = machine.Library.Say("ScreenName", "Search Screen") is { Length: > 0 } named
            ? named
            : "SEARCH";

        float goWidth = surface.Measure(go) + surface.Em(24);
        var box = new Vector4(body.X, body.Y, body.Z - goWidth - surface.Em(8), row);

        surface.Fill(box, SidneyPalette.PanelLit);
        surface.Frame(box, SidneyPalette.Amber);

        surface.Write(
            machine.Typed.Length > 0 ? machine.Typed + "_" : "Type a subject...",
            box.X + surface.Em(10),
            box.Y + ((row - surface.Line) / 2),
            machine.Typed.Length > 0 ? SidneyPalette.Ink : SidneyPalette.Dim);

        surface.Hit("sidney:type", box);

        surface.Button(
            "sidney:look", new Vector4(body.X + body.Z - goWidth, body.Y, goWidth, row), go);

        var page = new Vector4(
            body.X, body.Y + row + surface.Em(10), body.Z, body.W - row - surface.Em(10));

        if (machine.Page is not { } found)
        {
            if (machine.Showing is { } said)
            {
                surface.Write(said.Text, page.X, page.Y, SidneyPalette.Dim);
            }

            return;
        }

        // The page's own markup opens by repeating its title as a heading, which under a
        // title the screen has already drawn reads as the word twice. Dropped here rather
        // than in the reader: what the file says is what the reader should report.
        List<SearchLine> lines = [.. found.Lines];

        while (lines.Count > 0 &&
            string.Equals(lines[0].Text, found.Title, StringComparison.OrdinalIgnoreCase))
        {
            lines.RemoveAt(0);
        }

        // How tall the whole article is, so the bar knows what it is scrolling.
        float wrap = page.Z - surface.Em(24);
        float tall = surface.Line * 2;

        foreach (SearchLine line in lines)
        {
            tall += line.Rule || line.Link is { Length: > 0 }
                ? surface.Line
                : surface.Lines(line.Text, wrap) * surface.Line;
        }

        float offset = surface.BeginScroll("page", page, tall);
        float y = page.Y - offset;

        surface.Write(found.Title, page.X, y, SidneyPalette.Amber);
        y += surface.Line * 2;

        foreach (SearchLine line in lines)
        {
            if (line.Rule)
            {
                surface.Fill(page.X, y + (surface.Line / 2), wrap, 1, SidneyPalette.Rule);
                y += surface.Line;

                continue;
            }

            if (line.Link is { Length: > 0 } target)
            {
                float width = MathF.Min(surface.Measure(line.Text) + surface.Em(8), wrap);
                var bounds = new Vector4(page.X, y, width, surface.Line);

                surface.Write(line.Text, page.X, y, SidneyPalette.Amber);
                surface.Fill(page.X, y + surface.Line - 1, width, 1, SidneyPalette.Amber);
                surface.Hit("sidney:page:" + target, bounds);

                y += surface.Line;

                continue;
            }

            y = surface.Paragraph(
                line.Text,
                page.X,
                y,
                wrap,
                page.Y + page.W + tall,
                line.Heading ? SidneyPalette.Amber : SidneyPalette.Ink);
        }

        surface.EndScroll();
    }

    /// <summary>The suspects: ten files, what is linked to each, and the match.</summary>
    private static void Suspects(
        SidneySurface surface, SidneyMachine machine, ScreenView view, Vector4 body)
    {
        IReadOnlyList<SidneySuspect> people = machine.Library.Suspects();

        // Wider than it was, because a face now sits in front of every name and the names
        // are people's: "Excelsior Montreaux" elided to "Excelsior Mo..." is worse than the
        // column being wide.
        float listWidth = MathF.Max(body.Z * 0.44f, MathF.Min(body.Z * 0.54f, surface.Em(190)));
        var list = new Vector4(body.X, body.Y, listWidth, body.W);

        // Tall enough for a face beside the name. The original's list is names alone.
        float row = MathF.Max(surface.Line + surface.Em(10), surface.Em(34));
        float step = row + surface.Em(3);
        float offset = surface.BeginScroll("suspects", list, people.Count * step);
        float width = surface.Room(list, people.Count * step);

        for (int i = 0; i < people.Count; i++)
        {
            SidneySuspect person = people[i];
            bool open = machine.Suspect?.Index == person.Index;

            var bounds = new Vector4(list.X, list.Y + (i * step) - offset, width, row);

            surface.Fill(
                bounds, open || surface.Over(bounds) ? SidneyPalette.PanelLit : SidneyPalette.Panel);

            surface.Fill(
                bounds.X, bounds.Y, bounds.Z, 1, open ? SidneyPalette.Amber : SidneyPalette.Rule);

            // The face, where one has been rendered. Square, from the top of the row, so
            // the names still line up whether a portrait is there or not.
            float face = row - surface.Em(4);
            int picture = view.Pictures?.Invoke(person.Portrait) ?? 0;
            float text = bounds.X + surface.Em(10);

            if (picture > 0)
            {
                var into = new Vector4(
                    bounds.X + surface.Em(2), bounds.Y + surface.Em(2), face, face);

                surface.Overlay.Picture(
                    picture, into.X, into.Y, into.Z, into.W, Vector4.One);

                surface.Frame(into, open ? SidneyPalette.Amber : SidneyPalette.Rule);

                text = into.X + face + surface.Em(8);
            }

            surface.WriteIn(
                person.Name,
                text,
                bounds.Y + ((row - surface.Line) / 2),
                bounds.X + bounds.Z - text - surface.Em(10),
                open ? SidneyPalette.Amber : SidneyPalette.Ink);

            surface.Hit($"sidney:suspect:{person.Index}", bounds);
        }

        surface.EndScroll();

        var pane = new Vector4(
            body.X + listWidth + surface.Em(12),
            body.Y,
            body.Z - listWidth - surface.Em(12),
            body.W);

        if (machine.Suspect is not { } suspect)
        {
            surface.Write("Open a suspect's file.", pane.X, pane.Y, SidneyPalette.Dim);

            return;
        }

        string match = machine.Library.Say("MatchAnalysis", "Suspects Screen") is { Length: > 0 } asked
            ? asked
            : "MATCH ANALYSIS";

        float button = surface.Line + surface.Em(12);
        float matchWidth = surface.Measure(match) + surface.Em(24);
        var below = new Vector4(
            pane.X, pane.Y + pane.W - button, pane.Z, button);

        var detail = new Vector4(pane.X, pane.Y, pane.Z, pane.W - button - surface.Em(8));

        IReadOnlyList<SidneyFile> linked = machine.LinkedTo(suspect);
        List<SidneyFile> loose =
        [
            .. machine.Files.Where(f =>
                f.Kind is SidneyKind.KnownPrint or SidneyKind.UnknownPrint or SidneyKind.Licence &&
                !linked.Any(l => l.Id == f.Id)),
        ];

        float lineRow = surface.Line + surface.Em(8);
        float tall = (surface.Line * 5) + ((linked.Count + loose.Count) * (lineRow + surface.Em(4)));

        float paneOffset = surface.BeginScroll("suspect", detail, tall);
        float width2 = surface.Room(detail, tall);
        float at = detail.Y - paneOffset;

        surface.Write(
            $"{machine.Library.Say("Name", "Suspects Screen")} {suspect.Name}",
            detail.X,
            at,
            SidneyPalette.Ink);

        at += surface.Line;

        surface.Write(
            $"{machine.Library.Say("Nationality", "Suspects Screen")} {suspect.Nationality}",
            detail.X,
            at,
            SidneyPalette.Dim);

        at += surface.Line;

        // A registration is only there once a plate has been linked to this suspect; a
        // description of the car is there from the start, because the player saw it. The
        // label stays either way — a blank line teaches nothing, and a line that says the
        // answer is not known yet says there is one to go and find.
        bool knows = !suspect.Registered || machine.KnowsVehicle(suspect);

        surface.WriteIn(
            $"{machine.Library.Say("Vehicle", "Suspects Screen")} "
            + (knows ? suspect.Vehicle : "Unknown"),
            detail.X,
            at,
            width2,
            knows ? SidneyPalette.Dim : SidneyPalette.Rule);

        at += surface.Line * 2;

        // Wrapped: the game's own "There are no linked files for this suspect." is longer
        // than this column, and a heading that stops at "for this sus" reads as a fault.
        at = surface.Paragraph(
            linked.Count > 0
                ? machine.Library.Say("FileList", "Suspects Screen")
                : machine.Library.Say("NoLinks", "Suspects Screen"),
            detail.X,
            at,
            width2,
            detail.Y + detail.W,
            SidneyPalette.Amber) + surface.Em(4);

        foreach (SidneyFile file in linked)
        {
            var bounds = new Vector4(detail.X, at, width2, lineRow);

            surface.Fill(bounds, SidneyPalette.PanelLit);
            surface.WriteIn(
                file.Label,
                detail.X + surface.Em(8),
                at + ((lineRow - surface.Line) / 2),
                width2 - surface.Em(16),
                SidneyPalette.Ink);

            surface.Hit("sidney:unlink:" + file.Id, bounds);

            at += lineRow + surface.Em(4);
        }

        foreach (SidneyFile file in loose)
        {
            var bounds = new Vector4(detail.X, at, width2, lineRow);

            surface.Fill(bounds, SidneyPalette.Panel);
            surface.Fill(bounds.X, bounds.Y, bounds.Z, 1, SidneyPalette.Rule);

            surface.WriteIn(
                $"link  {file.Label}",
                detail.X + surface.Em(8),
                at + ((lineRow - surface.Line) / 2),
                width2 - surface.Em(16),
                SidneyPalette.Dim);

            surface.Hit("sidney:link:" + file.Id, bounds);

            at += lineRow + surface.Em(4);
        }

        surface.EndScroll();

        surface.Button("sidney:match", new Vector4(below.X, below.Y, matchWidth, button), match);

        if (machine.Showing is { } result)
        {
            surface.Paragraph(
                result.Text,
                below.X + matchWidth + surface.Em(10),
                below.Y,
                below.Z - matchWidth - surface.Em(10),
                below.Y + below.W,
                SidneyPalette.Amber);
        }
    }

    /// <summary>The identity card: five trades, and printing one.</summary>
    private static void MakeId(SidneySurface surface, SidneyMachine machine, Vector4 body)
    {
        IReadOnlyList<SidneyIdentity> identities = machine.Library.Identities();

        float button = surface.Line + surface.Em(10);
        var chosen = new Vector4(
            body.X, body.Y + body.W - surface.Line, body.Z, surface.Line);

        var list = new Vector4(
            body.X, body.Y, body.Z, body.W - surface.Line - surface.Em(6));

        // Laid out twice: once to find how tall it is, and once to draw it. Cheaper than
        // being wrong about the height, which is what put the way home over the last row.
        float tall = Lay(surface, machine, identities, list, button, 0, measure: true);
        float offset = surface.BeginScroll("id", list, tall);

        Lay(surface, machine, identities, list, button, offset, measure: false);

        surface.EndScroll();

        if (machine.Identity is { } printed)
        {
            surface.Write(
                $"{machine.Library.Say("Print", "MakeID Screen")}: {printed.Category}, {printed.Title}",
                chosen.X,
                chosen.Y,
                SidneyPalette.Amber);
        }
    }

    /// <summary>Lays the identity menus out, and says how tall they came to.</summary>
    private static float Lay(
        SidneySurface surface,
        SidneyMachine machine,
        IReadOnlyList<SidneyIdentity> identities,
        Vector4 list,
        float button,
        float offset,
        bool measure)
    {
        float width = measure ? list.Z : surface.Room(list, float.MaxValue);
        float y = list.Y - offset;
        float x = list.X;
        string? category = null;

        foreach (SidneyIdentity identity in identities)
        {
            if (!string.Equals(category, identity.Category, StringComparison.Ordinal))
            {
                category = identity.Category;
                x = list.X;
                y += surface.Line + surface.Em(6);

                if (!measure)
                {
                    surface.Write(category, x, y, SidneyPalette.Dim);
                }

                y += surface.Line + surface.Em(4);
            }

            float across = surface.Measure(identity.Title) + surface.Em(20);

            if (x + across > list.X + width)
            {
                x = list.X;
                y += button + surface.Em(6);
            }

            if (!measure)
            {
                surface.Button(
                    "sidney:id:" + identity.Title,
                    new Vector4(x, y, across, button),
                    identity.Title,
                    machine.Identity?.Title == identity.Title);
            }

            x += across + surface.Em(6);
        }

        return y - list.Y + offset + button + surface.Em(8);
    }

    /// <summary>
    /// Translate: a file, what it is written in, and what it says in English.
    /// </summary>
    /// <remarks>
    /// The screen the port answered "Not implemented yet" with. Everything on it — the four
    /// languages, the refusal for choosing the wrong one, both halves of every piece of text
    /// and the exchange that finishes the Arcadia inscription — is in the game's own
    /// translate section and had simply never been read.
    /// </remarks>
    private static void Translate(SidneySurface surface, SidneyMachine machine, Vector4 body)
    {
        List<SidneyFile> can = [.. machine.Files.Where(machine.Translator.CanTranslate)];

        float listWidth = MathF.Max(body.Z * 0.30f, MathF.Min(body.Z * 0.42f, surface.Em(120)));
        var list = new Vector4(body.X, body.Y, listWidth, body.W);

        if (can.Count == 0)
        {
            surface.Write(
                machine.Library.Say("NotTranslatable", SidneyTranslator.Section) is { Length: > 0 } none
                    ? none
                    : "Nothing here can be translated.",
                body.X,
                body.Y,
                SidneyPalette.Dim);

            return;
        }

        float row = surface.Line + surface.Em(12);
        float step = row + surface.Em(4);
        float offset = surface.BeginScroll("translate", list, can.Count * step);
        float width = surface.Room(list, can.Count * step);

        for (int i = 0; i < can.Count; i++)
        {
            bool open = machine.Translating?.Id == can[i].Id;
            var bounds = new Vector4(list.X, list.Y + (i * step) - offset, width, row);

            surface.Fill(
                bounds, open || surface.Over(bounds) ? SidneyPalette.PanelLit : SidneyPalette.Panel);

            surface.Fill(
                bounds.X, bounds.Y, bounds.Z, 1, open ? SidneyPalette.Amber : SidneyPalette.Rule);

            surface.WriteIn(
                can[i].Label,
                bounds.X + surface.Em(10),
                bounds.Y + ((row - surface.Line) / 2),
                bounds.Z - surface.Em(20),
                open ? SidneyPalette.Amber : SidneyPalette.Ink);

            surface.Hit("sidney:open:" + can[i].Id, bounds);
        }

        surface.EndScroll();

        var pane = new Vector4(
            body.X + listWidth + surface.Em(12),
            body.Y,
            body.Z - listWidth - surface.Em(12),
            body.W);

        if (machine.Translating is null)
        {
            surface.Write(
                machine.Library.Say("MenuItem1", SidneyTranslator.Section) is { Length: > 0 } open
                    ? open
                    : "OPEN FILE",
                pane.X,
                pane.Y,
                SidneyPalette.Dim);

            return;
        }

        float y = pane.Y;

        surface.Write(
            machine.Library.Say("From", SidneyTranslator.Section), pane.X, y, SidneyPalette.Dim);

        y += surface.Line + surface.Em(4);

        float x = pane.X;

        foreach (string language in machine.Translator.Languages)
        {
            float across = surface.Measure(language) + surface.Em(20);

            if (x + across > pane.X + pane.Z)
            {
                x = pane.X;
                y += row + surface.Em(4);
            }

            surface.Button(
                "sidney:from:" + language,
                new Vector4(x, y, across, row),
                language,
                string.Equals(machine.From, language, StringComparison.OrdinalIgnoreCase));

            x += across + surface.Em(6);
        }

        y += row + surface.Em(10);

        string now = machine.Library.Say("TranslateNow", SidneyTranslator.Section) is { Length: > 0 } said
            ? said
            : "TRANSLATE NOW";

        if (machine.From is { Length: > 0 })
        {
            surface.Button(
                "sidney:translate",
                new Vector4(pane.X, y, surface.Measure(now) + surface.Em(24), row),
                now);
        }

        y += row + surface.Em(10);

        if (machine.Showing is not { } result)
        {
            return;
        }

        var text = new Vector4(pane.X, y, pane.Z, pane.Y + pane.W - y);
        float wrap = text.Z - surface.Em(20);
        float tall = 0;

        foreach (string line in result.Text.Split('\n'))
        {
            tall += line.Length == 0 ? surface.Line : surface.Lines(line, wrap) * surface.Line;
        }

        float read = surface.BeginScroll("translated", text, tall + (surface.Line * 4));
        float at = text.Y - read;

        foreach (string line in result.Text.Split('\n'))
        {
            if (line.Length == 0)
            {
                at += surface.Line;

                continue;
            }

            at = surface.Paragraph(
                line, text.X, at, wrap, text.Y + text.W + tall, SidneyPalette.Ink);
        }

        // The one exchange this screen has: an unfinished sentence, and a word to finish it.
        if (result.Asks is { Length: > 0 } question && result.Choices is { Count: > 0 } choices)
        {
            at += surface.Line / 2;

            surface.Write(question, text.X, at, SidneyPalette.Amber);
            at += surface.Line + surface.Em(6);

            float cx = text.X;

            foreach (string choice in choices)
            {
                float across = surface.Measure(choice) + surface.Em(20);

                surface.Button("sidney:complete:" + choice, new Vector4(cx, at, across, row), choice);

                cx += across + surface.Em(6);
            }

            at += row + surface.Em(8);
        }

        if (machine.Appending)
        {
            surface.Write(
                machine.Library.Say("Input", SidneyTranslator.Section),
                text.X,
                at,
                SidneyPalette.Dim);

            at += surface.Line + surface.Em(4);

            var box = new Vector4(text.X, at, MathF.Min(wrap, surface.Em(160)), row);

            surface.Fill(box, SidneyPalette.PanelLit);
            surface.Frame(box, SidneyPalette.Amber);

            surface.Write(
                machine.Typed + "_",
                box.X + surface.Em(8),
                box.Y + ((row - surface.Line) / 2),
                SidneyPalette.Ink);

            surface.Hit("sidney:type", box);

            surface.Button(
                "sidney:append",
                new Vector4(box.X + box.Z + surface.Em(6), at, surface.Em(70), row),
                "ADD");
        }

        surface.EndScroll();
    }

    /// <summary>
    /// Analyze: a file, what may be done to it, and what it said.
    /// </summary>
    /// <returns>Where the map was drawn, when the open file is the map.</returns>
    private static Vector4 Analyze(
        SidneySurface surface, SidneyMachine machine, ScreenView view, Vector4 body)
    {
        IReadOnlyList<SidneyFile> files = machine.Files;

        if (files.Count == 0)
        {
            surface.Write(
                "No files. Scan something first.", body.X, body.Y, SidneyPalette.Dim);

            return default;
        }

        float listWidth = MathF.Max(body.Z * 0.26f, MathF.Min(body.Z * 0.36f, surface.Em(110)));
        var list = new Vector4(body.X, body.Y, listWidth, body.W);

        float row = surface.Line + surface.Em(12);
        float step = row + surface.Em(4);
        float offset = surface.BeginScroll("analyze", list, files.Count * step);
        float width = surface.Room(list, files.Count * step);

        for (int i = 0; i < files.Count; i++)
        {
            SidneyFile file = files[i];
            bool open = machine.Open?.Id == file.Id;
            var bounds = new Vector4(list.X, list.Y + (i * step) - offset, width, row);

            surface.Fill(
                bounds, open || surface.Over(bounds) ? SidneyPalette.PanelLit : SidneyPalette.Panel);

            surface.Fill(
                bounds.X, bounds.Y, bounds.Z, 1, open ? SidneyPalette.Amber : SidneyPalette.Rule);

            surface.WriteIn(
                file.Label,
                bounds.X + surface.Em(10),
                bounds.Y + ((row - surface.Line) / 2),
                bounds.Z - surface.Em(20),
                open ? SidneyPalette.Amber : SidneyPalette.Ink);

            surface.Hit("sidney:file:" + file.Id, bounds);
        }

        surface.EndScroll();

        var pane = new Vector4(
            body.X + listWidth + surface.Em(12),
            body.Y,
            body.Z - listWidth - surface.Em(12),
            body.W);

        float y = pane.Y;
        float x = pane.X;

        // <b>The four menus the original has, on one row.</b> Laid out flat, the map's eight
        // operations wrapped onto three rows of a 640-pixel screen and pushed the picture
        // they act on off the bottom. ESIDNEY.TXT groups them into OPEN, TEXT, GRAPHIC and
        // MAP; only the menus with something applicable under them are shown, and only one
        // is open at a time.
        IReadOnlyList<SidneyAction> offered = machine.Available();
        var menus = new List<int>();

        foreach (SidneyAction action in offered)
        {
            int menu = SidneyMachine.MenuOf(action);

            if (!menus.Contains(menu))
            {
                menus.Add(menu);
            }
        }

        menus.Sort();

        foreach (int menu in menus)
        {
            string name = machine.MenuName(menu);
            float across = surface.Measure(name) + surface.Em(24);

            surface.Button(
                $"sidney:menu:{menu}",
                new Vector4(x, y, across, row),
                name,
                machine.Menu == menu);

            x += across + surface.Em(6);
        }

        y += row + surface.Em(6);

        // The open menu's items, under it rather than over the picture: this screen has
        // room to the right of a square map and none to spare below it.
        if (menus.Contains(machine.Menu))
        {
            float item = pane.X;

            foreach (SidneyAction action in offered)
            {
                if (SidneyMachine.MenuOf(action) != machine.Menu)
                {
                    continue;
                }

                string label = Label(action);
                float across = surface.Measure(label) + surface.Em(20);

                if (item + across > pane.X + pane.Z)
                {
                    item = pane.X;
                    y += row + surface.Em(4);
                }

                bool done = !Repeatable(action) &&
                    machine.Open is { } open && machine.HasDone(open, action);

                // ENTER POINTS is a toggle, so it shows whether it is on.
                bool armed = action == SidneyAction.EnterPoints && machine.Marking;

                var bounds = new Vector4(item, y, across, row);

                surface.Fill(bounds, done ? SidneyPalette.Panel : SidneyPalette.PanelLit);

                surface.Frame(
                    bounds,
                    armed ? SidneyPalette.Amber
                        : done ? SidneyPalette.Rule
                        : SidneyPalette.AmberDim);

                surface.Write(
                    label,
                    item + surface.Em(10),
                    y + ((row - surface.Line) / 2),
                    armed ? SidneyPalette.Amber : done ? SidneyPalette.Dim : SidneyPalette.Ink);

                surface.Hit($"sidney:do:{action}", bounds);

                item += across + surface.Em(6);
            }

            y += row + surface.Em(6);
        }

        y += row + surface.Em(12);

        if (machine.Open?.Kind == SidneyKind.Map)
        {
            return SidneyMapView.Draw(
                surface, machine, view, new Vector4(pane.X, y, pane.Z, pane.Y + pane.W - y));
        }

        // What the file actually looks like, and how far its analysis has got. The screen
        // talks about a device in the corner of an image and a break at line fourteen; it
        // had no image.
        var below = new Vector4(pane.X, y, pane.Z, pane.Y + pane.W - y);

        if (machine.Open is { } showing &&
            SidneyPictures.Showing(showing, action => machine.HasDone(showing, action))
                is { Length: > 0 } named &&
            surface.Art(named + ".BMP") is { Drawn: true } plate)
        {
            float side = MathF.Min(below.Z * 0.52f, below.W);
            Vector4 into = plate.Fit(below.X, below.Y, side);

            surface.Draw(plate, into);
            surface.Frame(into, SidneyPalette.Rule);

            below = new Vector4(
                below.X + side + surface.Em(12),
                below.Y,
                below.Z - side - surface.Em(12),
                below.W);
        }

        if (machine.Showing is not { } result)
        {
            return default;
        }

        var said = new Vector4(below.X, below.Y, below.Z, below.W);
        float wrap = said.Z - surface.Em(20);
        float tall = (surface.Lines(result.Text, wrap) * surface.Line) + (surface.Line * 3);

        float read = surface.BeginScroll("said", said, tall);
        float at = surface.Paragraph(
            result.Text, said.X, said.Y - read, wrap, said.Y + said.W + tall, SidneyPalette.Ink);

        if (result.Asks is { Length: > 0 } question && result.Choices is { Count: > 0 } choices)
        {
            at += surface.Line / 2;

            surface.Write(question, said.X, at, SidneyPalette.Amber);
            at += surface.Line + surface.Em(6);

            float cx = said.X;

            foreach (string choice in choices)
            {
                float across = surface.Measure(choice) + surface.Em(20);

                surface.Button("sidney:answer:" + choice, new Vector4(cx, at, across, row), choice);

                cx += across + surface.Em(6);
            }
        }

        surface.EndScroll();

        return default;
    }

    /// <summary>
    /// Whether an operation is one that may be run again and again.
    /// </summary>
    /// <param name="action">The operation.</param>
    /// <returns>True when running it once does not finish it.</returns>
    /// <remarks>
    /// The analyses are one-shot: a parchment's anomalies come out once and the note they
    /// produce is what the story reads afterwards. Everything the map does is not — points
    /// are entered and cleared and entered again, the grid comes and goes, a figure is
    /// turned a step at a time — and the screen has to keep offering them.
    /// </remarks>
    private static bool Repeatable(SidneyAction action) => action is
        SidneyAction.EnterPoints or
        SidneyAction.ClearPoints or
        SidneyAction.UndoPoint or
        SidneyAction.DrawGrid or
        SidneyAction.EraseGrid or
        SidneyAction.UseShape or
        SidneyAction.RotateShape or
        SidneyAction.EraseShape;

    /// <summary>What an operation is called on its button.</summary>
    private static string Label(SidneyAction action) => action switch
    {
        SidneyAction.Analyse => "START ANALYSIS",
        SidneyAction.ExtractAnomalies => "EXTRACT ANOMALIES",
        SidneyAction.AnalyseText => "ANALYZE TEXT",
        SidneyAction.Translate => "TRANSLATE",
        SidneyAction.ViewGeometry => "VIEW GEOMETRY",
        SidneyAction.RotateShape => "ROTATE SHAPE",
        SidneyAction.ZoomAndClarify => "ZOOM & CLARIFY",
        SidneyAction.EnterPoints => "ENTER POINTS",
        SidneyAction.ClearPoints => "CLEAR POINTS",
        SidneyAction.UndoPoint => "UNDO POINT",
        SidneyAction.DrawGrid => "DRAW GRID",
        SidneyAction.EraseGrid => "ERASE GRID",
        SidneyAction.UseShape => "USE SHAPE",
        SidneyAction.EraseShape => "ERASE SHAPE",
        _ => action.ToString().ToUpperInvariant(),
    };

    /// <summary>What the machine takes a file to be, in words rather than in enum spelling.</summary>
    private static string Describe(SidneyKind kind) => kind switch
    {
        SidneyKind.Parchment1 => "parchment",
        SidneyKind.Parchment2 => "parchment",
        SidneyKind.Map => "map",
        SidneyKind.Poussin => "painting",
        SidneyKind.Teniers => "painting",
        SidneyKind.Symbols => "symbols",
        SidneyKind.Note => "note",
        SidneyKind.KnownPrint => "fingerprint",
        SidneyKind.UnknownPrint => "fingerprint, unknown",
        SidneyKind.Tape => "recording",
        SidneyKind.Licence => "licence plate",
        _ => "file",
    };

    /// <summary>What the machine says while it has nothing better to say.</summary>
    private static string Pretty(string noun) =>
        string.Join(
            ' ',
            noun.Split('_', StringSplitOptions.RemoveEmptyEntries)
                .Select(word => word.Length switch
                {
                    0 => word,
                    1 => word.ToUpperInvariant(),
                    _ => char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant(),
                }));
}
