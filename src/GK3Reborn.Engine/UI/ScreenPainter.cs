// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Globalization;
using System.Numerics;
using GK3Reborn.Game;
using GK3Reborn.Game.Story;
using GK3Reborn.Game.Sidney;
using GK3Reborn.Rendering;

namespace GK3Reborn.UI;

/// <summary>What the screens need to know about the game to draw themselves.</summary>
/// <param name="Screen">Which screen is on top.</param>
/// <param name="Inventory">What the player is carrying.</param>
/// <param name="Held">Which of it is in hand.</param>
/// <param name="Sidney">Sidney, when that is what is showing.</param>
/// <param name="Places">
/// Where the player may go from here: destinations on the driving map, or the places the
/// binoculars can see.
/// </param>
/// <param name="Subject">What the screen is about, where it is about something.</param>
/// <param name="Map">The driving map's own art and roads, when there is any.</param>
/// <param name="Stops">Where the moped may be ridden to.</param>
/// <param name="Pictures">
/// What number the interface holds each of the map's pictures under, by name. Zero means
/// the picture is not loaded, and the map falls back to drawing itself.
/// </param>
/// <param name="Panorama">What the binoculars can see from here, when they are up.</param>
/// <param name="Aim">Where the camera is looking, in degrees: heading, then pitch.</param>
/// <param name="Journal">
/// The quest log, by day, when that is what is being shown. Read fresh each frame from the
/// score events the story already records, so nothing here can drift out of step with it.
/// </param>
/// <param name="Prints">
/// How many prints the fingerprint kit has revealed on what it is dusting, or minus one
/// while nothing has been brushed yet.
/// </param>
/// <param name="Verbs">
/// What can be done to whatever the screen is about. Only the close-up of one thing uses
/// it, and it is what makes the inventory worth opening: 619 of the game's actions are
/// written about an item rather than about the room, and every one of them is guarded by
/// a case that asks whether the inventory is what the player is looking at.
/// </param>
public readonly record struct ScreenView(
    Screen Screen,
    IReadOnlyList<string> Inventory,
    string? Held,
    SidneyMachine? Sidney = null,
    IReadOnlyList<string>? Places = null,
    string? Subject = null,
    DrivingMap? Map = null,
    IReadOnlyList<DrivingStop>? Stops = null,
    Func<string, int>? Pictures = null,
    Panorama? Panorama = null,
    Vector2 Aim = default,
    IReadOnlyList<string>? Verbs = null,
    IReadOnlyList<JournalDay>? Journal = null,
    int Prints = -1);

/// <summary>
/// The screens that go in front of the room.
/// </summary>
/// <remarks>
/// <para>
/// The inventory, an item held up close, the binoculars, the driving map and Sidney. In the
/// original each arrived with its own way in and its own way out; <c>Plan/03</c> section 3
/// asks for the opposite, so they share their chrome, their way back and their scaling, and
/// the player learns the way out once.
/// </para>
/// <para>
/// <b>Drawn rather than blitted.</b> The house style since the interface stopped using
/// GK3's bitmap sheets: rectangles and text, laid out fresh every frame. It costs the
/// original's art and buys a screen that is legible at any resolution, scales with the
/// font, and needs no second texture in a pipeline built around one. Sidney gains most —
/// it is a computer terminal, which is what this style draws best.
/// </para>
/// <para>
/// <b>Nothing here is retained</b>, exactly like <see cref="GameHud"/>: a function from what
/// the game is doing to a list of rectangles, with hit testing reading back the same layout
/// pass that drew it. There is no widget tree to keep in step with the world, and no way for
/// a screen to be showing something that stopped being true.
/// </para>
/// <para>
/// <b>What a click means is a string.</b> The painter knows where things are and the caller
/// knows what they do — <c>item:PARCHMENT_1</c>, <c>sidney:do:Analyse</c>, <c>close</c> —
/// which keeps every rule about the game out of the drawing and every rule about drawing
/// out of the game.
/// </para>
/// </remarks>
public sealed class ScreenPainter
{
    private static readonly Vector4 Shade = new(0.02f, 0.02f, 0.03f, 0.72f);
    private static readonly Vector4 Panel = new(0.06f, 0.07f, 0.09f, 0.96f);
    private static readonly Vector4 PanelLit = new(0.16f, 0.18f, 0.22f, 0.96f);
    private static readonly Vector4 Ink = new(0.88f, 0.87f, 0.83f, 1f);
    private static readonly Vector4 Dim = new(0.55f, 0.55f, 0.52f, 1f);
    private static readonly Vector4 Accent = new(0.95f, 0.76f, 0.35f, 1f);
    private static readonly Vector4 Rule = new(0.30f, 0.32f, 0.36f, 0.9f);

    /// <summary>A shape the marked places confirm, told apart from one merely laid.</summary>
    private static readonly Vector4 Locked = new(0.45f, 0.90f, 0.55f, 1f);

    /// <summary>What is outside the binoculars: not quite black, so the room shows through.</summary>
    private static readonly Vector4 Eyepiece = new(0.01f, 0.01f, 0.015f, 0.97f);

    /// <summary>The crosshairs.</summary>
    private static readonly Vector4 Reticle = new(0.85f, 0.84f, 0.80f, 0.55f);

    private readonly List<(string Id, Vector4 Bounds)> _hits = [];

    /// <summary>Creates the painter.</summary>
    /// <param name="overlay">Where it draws.</param>
    public ScreenPainter(Overlay overlay)
    {
        ArgumentNullException.ThrowIfNull(overlay);

        Overlay = overlay;
    }

    /// <summary>Where it draws.</summary>
    public Overlay Overlay { get; private set; }

    /// <summary>How much bigger than the letters everything else is.</summary>
    public float Scale => Math.Max(1f, Overlay.LineHeight / 19f);

    /// <summary>Points at a fresh sheet of letters, after the window changed size.</summary>
    /// <param name="atlas">The new sheet.</param>
    public void Retarget(OverlayAtlas atlas)
    {
        ArgumentNullException.ThrowIfNull(atlas);

        Overlay = new Overlay(atlas);
    }

    /// <summary>Lays a screen out.</summary>
    /// <param name="view">What to draw.</param>
    /// <param name="width">Window width in pixels.</param>
    /// <param name="height">Window height.</param>
    /// <param name="at">Where the pointer is, for hovering a row.</param>
    public void Build(ScreenView view, int width, int height, Vector2 at = default)
    {
        Overlay.Begin(width, height);
        _hits.Clear();
        _pointer = at;

        float unit = Scale;

        // The binoculars are not a panel over the room; they are a way of looking at it.
        // Everything else dims the room and puts a page in front.
        if (view.Screen.Kind == ScreenKind.Binoculars)
        {
            Binoculars(view, width, height, unit);

            return;
        }

        // The room stays visible behind everything but the driving map, which is the one
        // screen where the player is somewhere else entirely. Drawn with the body below,
        // because how much of the window the body takes decides where it goes.
        // How much of the window a screen takes. The inventory is a page — it is a list of
        // everything the player owns and wants the room — and one item held up to the light
        // is not. Asked for: a close-up of a single thing filling the screen reads as a
        // modal error box rather than as looking at something.
        bool page = view.Screen.Kind is not
            (ScreenKind.InventoryInspect or ScreenKind.Fingerprint);

        float margin = 40f * unit;

        var body = page
            ? new Vector4(margin, margin, width - (margin * 2), height - (margin * 2))
            : Card(width, height, unit);

        Overlay.Rect(0, 0, width, height, view.Screen.TakesOverInput ? Panel : Shade);

        Overlay.Rect(body.X, body.Y, body.Z, body.W, Panel);
        Overlay.Rect(body.X, body.Y, body.Z, 1, Rule);

        float top = Chrome(view, body, unit);

        switch (view.Screen.Kind)
        {
            case ScreenKind.Inventory:
                Inventory(view, body, top, unit);
                break;

            case ScreenKind.InventoryInspect:
                Inspect(view, body, top, unit);
                break;

            case ScreenKind.Binoculars:
                // Its own frame rather than the shared panel: the binoculars are a way of
                // looking at the room, not a page in front of it.
                Binoculars(view, width, height, unit);
                break;

            case ScreenKind.Driving:
                Driving(view, body, top, unit);
                break;

            case ScreenKind.Sidney:
                Sidney(view, body, top, unit);
                break;

            case ScreenKind.Journal:
                JournalPage(view, body, top, unit);
                break;

            case ScreenKind.Fingerprint:
                Fingerprint(view, body, top, unit);
                break;

            default:
                Overlay.Text("Nothing to show.", body.X + (20 * unit), top, Dim);
                break;
        }
    }

    /// <summary>What a click at a point means, or null for nothing.</summary>
    /// <param name="point">Where the pointer is.</param>
    /// <returns>The hit's identifier.</returns>
    public string? HitAt(Vector2 point)
    {
        // Backwards: later rectangles are drawn over earlier ones, so they are what the
        // player is pointing at.
        for (int i = _hits.Count - 1; i >= 0; i--)
        {
            (string id, Vector4 bounds) = _hits[i];

            if (point.X >= bounds.X && point.X <= bounds.X + bounds.Z &&
                point.Y >= bounds.Y && point.Y <= bounds.Y + bounds.W)
            {
                return id;
            }
        }

        return null;
    }

    /// <summary>The title bar every screen shares, and the way out.</summary>
    /// <returns>Where the body may start.</returns>
    private float Chrome(ScreenView view, Vector4 body, float unit)
    {
        float row = Overlay.LineHeight;
        float y = body.Y + (12 * unit);

        Overlay.Text(Title(view), body.X + (20 * unit), y, Accent);

        // One way out, in the same place on every screen, and Escape does the same thing.
        string close = "CLOSE";
        float closeWidth = Overlay.Measure(close) + (20 * unit);
        var closeAt = new Vector4(
            body.X + body.Z - closeWidth - (16 * unit), y - (6 * unit), closeWidth, row + (12 * unit));

        Overlay.Rect(closeAt.X, closeAt.Y, closeAt.Z, closeAt.W, PanelLit);
        Overlay.Text(close, closeAt.X + (10 * unit), y, Ink);
        _hits.Add(("close", closeAt));

        float rule = y + row + (10 * unit);
        Overlay.Rect(body.X + (16 * unit), rule, body.Z - (32 * unit), 1, Rule);

        return rule + (14 * unit);
    }

    private static string Title(ScreenView view) => view.Screen.Kind switch
    {
        ScreenKind.Inventory => "CARRYING",
        ScreenKind.InventoryInspect => Pretty(view.Screen.Subject ?? view.Held ?? "ITEM"),
        ScreenKind.Binoculars => "BINOCULARS",
        ScreenKind.Driving => "WHERE TO?",
        ScreenKind.Fingerprint => "FINGERPRINT KIT",
        ScreenKind.Sidney => "SIDNEY",
        ScreenKind.Journal => "JOURNAL",
        _ => "SCREEN",
    };

    /// <summary>Everything the player is carrying, as a grid.</summary>
    /// <remarks>
    /// A grid rather than the strip along the bottom of the room, because this is the
    /// screen for when there is more of it than the strip can show — and the strip already
    /// covers the common case, which is why the original's separate inventory screen was
    /// worth replacing rather than reproducing.
    /// </remarks>
    private void Inventory(ScreenView view, Vector4 body, float top, float unit)
    {
        if (view.Inventory.Count == 0)
        {
            Overlay.Text("Nothing.", body.X + (20 * unit), top, Dim);

            return;
        }

        float cell = 210f * unit;
        float rowHeight = Overlay.LineHeight + (18 * unit);
        int columns = Math.Max(1, (int)((body.Z - (32 * unit)) / cell));

        for (int i = 0; i < view.Inventory.Count; i++)
        {
            string item = view.Inventory[i];
            bool held = string.Equals(item, view.Held, StringComparison.OrdinalIgnoreCase);

            float x = body.X + (16 * unit) + ((i % columns) * cell);
            float y = top + ((i / columns) * (rowHeight + (6 * unit)));

            if (y + rowHeight > body.Y + body.W)
            {
                break;
            }

            var bounds = new Vector4(x, y, cell - (8 * unit), rowHeight);

            Overlay.Rect(bounds.X, bounds.Y, bounds.Z, bounds.W, held ? PanelLit : Panel);
            Overlay.Rect(bounds.X, bounds.Y, bounds.Z, 1, held ? Accent : Rule);
            Overlay.Text(Pretty(item), x + (10 * unit), y + (8 * unit), held ? Accent : Ink);

            _hits.Add(("item:" + item, bounds));

            // The verbs for whichever item was clicked, beside it, exactly as a right click
            // in the room offers a noun's verbs where the pointer is. Asked for: clicking a
            // thing in your pocket used to open a page of its own to hold two words on.
            if (string.Equals(view.Subject, item, StringComparison.OrdinalIgnoreCase))
            {
                Beside(view, bounds, body, unit);
            }
        }

        // What a click does, rather than a rule about holding. "Click to hold, click again
        // to look at it closely" described the interface's own mechanics and not the
        // player's intention — holding a thing is not something anybody sets out to do, and
        // an item with one action now simply performs it.
        Overlay.Text(
            "Click an item to use it.",
            body.X + (20 * unit),
            body.Y + body.W - Overlay.LineHeight - (12 * unit),
            Dim);
    }

    /// <summary>
    /// The verbs for one item, beside the item.
    /// </summary>
    /// <param name="view">What to draw, including the verbs.</param>
    /// <param name="slot">Where the item is drawn.</param>
    /// <param name="body">The panel it is in, so the menu stays inside it.</param>
    /// <param name="unit">The interface's scale.</param>
    /// <remarks>
    /// The same shape a right click gives in the room: a short column of words where the
    /// pointer is. The alternative, and what this replaces, was a screen of its own holding
    /// two options — which is a page for a thing that fits in a corner.
    /// </remarks>
    private void Beside(ScreenView view, Vector4 slot, Vector4 body, float unit)
    {
        if (view.Verbs is not { Count: > 0 } verbs)
        {
            return;
        }

        float row = Overlay.LineHeight + (10 * unit);
        float wide = 0;

        foreach (string verb in verbs)
        {
            wide = MathF.Max(wide, Overlay.Measure(Pretty(verb)));
        }

        wide += 28 * unit;

        float x = MathF.Min(slot.X + (12 * unit), body.X + body.Z - wide - (8 * unit));
        float y = MathF.Min(
            slot.Y + slot.W - (4 * unit),
            body.Y + body.W - (row * verbs.Count) - (8 * unit));

        Overlay.Rect(x, y, wide, row * verbs.Count, PanelLit);
        Overlay.Rect(x, y, wide, 1, Accent);

        for (int i = 0; i < verbs.Count; i++)
        {
            var bounds = new Vector4(x, y + (row * i), wide, row);
            bool under = Inside(_pointer, bounds);

            if (under)
            {
                Overlay.Rect(bounds.X, bounds.Y, bounds.Z, bounds.W, Panel);
            }

            Overlay.Text(
                Pretty(verbs[i]),
                bounds.X + (12 * unit),
                bounds.Y + (5 * unit),
                under ? Accent : Ink);

            _hits.Add(("verb:" + verbs[i], bounds));
        }
    }

    /// <summary>Whether a point is inside a rectangle.</summary>
    private static bool Inside(Vector2 point, Vector4 bounds) =>
        point.X >= bounds.X && point.X <= bounds.X + bounds.Z &&
        point.Y >= bounds.Y && point.Y <= bounds.Y + bounds.W;

    /// <summary>Where the pointer is, for hovering a row.</summary>
    private Vector2 _pointer;

    /// <summary>
    /// The fingerprint kit, over one surface.
    /// </summary>
    /// <param name="view">What to draw, including how many prints the brush has found.</param>
    /// <param name="body">The card it goes in.</param>
    /// <param name="top">Where the chrome ends.</param>
    /// <param name="unit">The interface's scale.</param>
    /// <remarks>
    /// Two steps, which is the ritual reduced to what the story records: brush the surface,
    /// and lift what shows with the tape. A card rather than a page, for the same reason the
    /// item close-up is one — the room the object is in should stay most of the screen.
    /// </remarks>
    private void Fingerprint(ScreenView view, Vector4 body, float top, float unit)
    {
        float x = body.X + (20 * unit);
        float y = top + (8 * unit);
        float line = Overlay.LineHeight;

        Overlay.Text(
            view.Prints < 0
                ? "A fine brush, and a roll of tape."
                : view.Prints == 0
                    ? "The powder settles. Nothing shows up."
                    : view.Prints == 1
                        ? "The powder settles on a clear print."
                        : string.Create(
                            CultureInfo.InvariantCulture,
                            $"The powder settles on {view.Prints} distinct prints."),
            x,
            y,
            Ink);

        y += (line * 2) + (6 * unit);

        (string id, string label) = view.Prints < 0
            ? ("fp:brush", "Brush for prints")
            : view.Prints > 0
                ? ("fp:lift", "Lift with tape")
                : ("close", "Put the kit away");

        float wide = Overlay.Measure(label) + (24 * unit);
        var button = new Vector4(x, y, wide, line + (12 * unit));

        Overlay.Rect(button.X, button.Y, button.Z, button.W, PanelLit);
        Overlay.Rect(button.X, button.Y, button.Z, 1, Accent);
        Overlay.Text(label, button.X + (12 * unit), button.Y + (6 * unit), Accent);

        _hits.Add((id, button));
    }

    /// <summary>A panel for one thing rather than a page for everything.</summary>
    /// <param name="width">Window width.</param>
    /// <param name="height">Window height.</param>
    /// <param name="unit">The interface's scale.</param>
    /// <returns>Where to draw it.</returns>
    /// <remarks>
    /// Just over a third of the window, a little above centre, so the room it belongs to is
    /// still most of what is on the screen. An object held up to the light is a small thing
    /// and should look like one.
    /// </remarks>
    private static Vector4 Card(int width, int height, float unit)
    {
        float wide = MathF.Min(width - (80 * unit), MathF.Max(320 * unit, width * 0.38f));
        float tall = MathF.Min(height - (80 * unit), MathF.Max(200 * unit, height * 0.46f));

        return new Vector4(
            MathF.Round((width - wide) / 2f),
            MathF.Round((height - tall) / 2.4f),
            MathF.Round(wide),
            MathF.Round(tall));
    }

    /// <summary>
    /// The quest log.
    /// </summary>
    /// <param name="view">What to draw, including the journal itself.</param>
    /// <param name="body">The panel it goes in.</param>
    /// <param name="top">Where the chrome ends.</param>
    /// <param name="unit">The interface's scale.</param>
    /// <remarks>
    /// <para>
    /// By day and then by point in the story, newest last. <b>Only the block the player is
    /// in lists its objectives</b>; the ones behind it keep their heading and their tally and
    /// give up their list. The question the journal answers is "what now", and a morning's
    /// worth of ticked lines buries it. The tally is what is left of "how far have I come",
    /// which is worth keeping and is not worth eleven lines.
    /// </para>
    /// <para>
    /// <b>Nothing here says how.</b> The titles are written to say what, and a player who
    /// wants more asks for it: every unfinished objective carries a button that reveals one
    /// line of the walkthrough, and asking again reveals the next. Several of this game's
    /// puzzles are the best things in it, and printing the answer where nobody asked would
    /// take them away.
    /// </para>
    /// </remarks>
    private void JournalPage(ScreenView view, Vector4 body, float top, float unit)
    {
        IReadOnlyList<JournalDay> days = view.Journal ?? [];

        if (days.Count == 0)
        {
            Overlay.Text("Nothing yet.", body.X + (20 * unit), top, Dim);

            return;
        }

        float x = body.X + (20 * unit);
        float y = top;
        float line = Overlay.LineHeight;
        float bottom = body.Y + body.W - (16 * unit);
        float width = body.Z - (40 * unit);

        foreach (JournalDay day in days)
        {
            foreach (JournalChapter chapter in day.Chapters)
            {
                if (y + (line * 2) > bottom)
                {
                    Overlay.Text("...", x, y, Dim);

                    return;
                }

                // The heading carries the tally, because "4 of 11" answers "am I nearly
                // done here" without the player counting ticks.
                Overlay.Text(
                    chapter.Current ? chapter.Title + "  (now)" : chapter.Title,
                    x,
                    y,
                    chapter.Current ? Accent : Dim);

                Overlay.Text(
                    $"{chapter.Achieved} of {chapter.Total}",
                    x + width - (90 * unit),
                    y,
                    Dim);

                y += line + (4 * unit);
                Overlay.Rect(x, y, width, 1, Rule);
                y += 8 * unit;

                // Only what the player is in the middle of. A point in the story they have
                // finished with keeps its heading and its tally and gives up its list: the
                // question the journal answers is "what now", and eleven ticked lines from
                // this morning bury it. Asked for, having watched the list grow into
                // something nobody could read at a glance.
                if (!chapter.Current)
                {
                    y += 6 * unit;
                    continue;
                }

                foreach (JournalEntry entry in chapter.Entries)
                {
                    if (y + line > bottom)
                    {
                        Overlay.Text("...", x, y, Dim);

                        return;
                    }

                    // A box, ticked or not. Drawn from ASCII rather than from a tick and a
                    // bullet: the interface font has an em dash and not those, so both marks
                    // came out as blank columns and the list read as unmarked throughout.
                    //
                    // Part-finished objectives say so in numbers rather than with a bar. A
                    // bar two thirds through a conversation is a stranger idea than "2 of 3".
                    string mark = entry.Done ? "[x]" : "[ ]";

                    Overlay.Text(
                        $"  {mark}  {entry.Quest.Title}",
                        x,
                        y,
                        entry.Done ? Dim : Ink);

                    if (!entry.Done && entry.Quest.Scores.Count > 1)
                    {
                        int of = entry.Quest.Scores.Count;
                        int done = (int)MathF.Round(entry.Progress * of);

                        Overlay.Text($"{done} of {of}", x + width - (90 * unit), y, Dim);
                    }

                    // The way to ask for help, offered only where there is help left to give
                    // and only on something still unfinished.
                    if (!entry.Done && entry.MoreHints && chapter.Current)
                    {
                        var hint = new Vector4(
                            x + width - (170 * unit), y - (2 * unit), 64 * unit, line + (4 * unit));

                        Overlay.Rect(hint.X, hint.Y, hint.Z, hint.W, PanelLit);
                        Overlay.Rect(hint.X, hint.Y, hint.Z, 1, Rule);
                        Overlay.Text("hint", hint.X + (10 * unit), y, Accent);

                        _hits.Add(("hint:" + Journal.Key(entry.Quest), hint));
                    }

                    y += line + (4 * unit);

                    foreach (string revealed in entry.Hints)
                    {
                        foreach (string wrapped in Wrapped(revealed, width - (48 * unit), unit))
                        {
                            if (y + line > bottom)
                            {
                                return;
                            }

                            Overlay.Text("      " + wrapped, x, y, Dim);
                            y += line;
                        }
                    }
                }

                y += 12 * unit;
            }
        }
    }

    /// <summary>Breaks a line of prose to fit a width.</summary>
    /// <remarks>
    /// A walkthrough line can run to three sentences and the panel is not that wide. Broken
    /// on words, and a word longer than the whole width is left to overrun rather than cut
    /// in half, because there is no such word in the file and inventing a hyphenation rule
    /// for a case that cannot happen is work spent on nothing.
    /// </remarks>
    private static IEnumerable<string> Wrapped(string text, float width, float unit)
    {
        float perCharacter = 9f * unit;
        int fits = Math.Max(16, (int)(width / MathF.Max(perCharacter, 1f)));

        var line = new System.Text.StringBuilder();

        foreach (string word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > fits)
            {
                yield return line.ToString();
                line.Clear();
            }

            if (line.Length > 0)
            {
                line.Append(' ');
            }

            line.Append(word);
        }

        if (line.Length > 0)
        {
            yield return line.ToString();
        }
    }

    /// <summary>
    /// One thing, close up, and what can be done to it.
    /// </summary>
    /// <remarks>
    /// The verbs are the point. An item's own actions — look at it, think about it, read
    /// it, scan it into Sidney — are written in <c>INV_ALL.NVC</c> and every one of them is
    /// guarded by <c>ALL_INV</c>, which asks whether the inventory is on top. So this is
    /// the only place they can be reached, and a close-up with nothing on it but the item's
    /// name was a screen with 619 actions behind it and no way to any of them.
    /// </remarks>
    private void Inspect(ScreenView view, Vector4 body, float top, float unit)
    {
        string subject = view.Screen.Subject ?? view.Held ?? string.Empty;

        Overlay.Text(Pretty(subject), body.X + (20 * unit), top, Ink);

        float y = top + (Overlay.LineHeight * 2);

        if (view.Verbs is not { Count: > 0 } verbs)
        {
            Overlay.Text(
                "Nothing to do with it here. Right-click in the room to use it on something.",
                body.X + (20 * unit),
                y,
                Dim);

            return;
        }

        float row = Overlay.LineHeight + (12 * unit);
        float width = Math.Min(body.Z - (40 * unit), 320f * unit);

        foreach (string verb in verbs)
        {
            if (y + row > body.Y + body.W - row)
            {
                break;
            }

            var bounds = new Vector4(body.X + (16 * unit), y, width, row);

            Overlay.Rect(bounds.X, bounds.Y, bounds.Z, bounds.W, Panel);
            Overlay.Rect(bounds.X, bounds.Y, 2 * unit, bounds.W, Rule);
            Overlay.Text(Pretty(verb), bounds.X + (12 * unit), y + (6 * unit), Ink);

            _hits.Add(("verb:" + verb, bounds));
            y += row + (4 * unit);
        }

        Overlay.Text(
            "Right-click in the room to use it on something.",
            body.X + (20 * unit),
            body.Y + body.W - Overlay.LineHeight - (12 * unit),
            Dim);
    }

    /// <summary>
    /// A list of places, for the binoculars and for a map with no art to draw.
    /// </summary>
    /// <remarks>
    /// <b>What is clicked is not what is written.</b> A row shows a place's name — "Larry
    /// Chester's House" — and carries its location code. Carrying the name instead sent the
    /// game looking for a room called <c>Larry Chester's House</c>, which it did once.
    /// </remarks>
    private void Places(
        IReadOnlyList<(string Id, string Label)> places,
        Vector4 body,
        float top,
        float unit,
        string verb,
        string empty)
    {
        if (places.Count == 0)
        {
            Overlay.Text(empty, body.X + (20 * unit), top, Dim);

            return;
        }

        float rowHeight = Overlay.LineHeight + (16 * unit);

        for (int i = 0; i < places.Count; i++)
        {
            float y = top + (i * (rowHeight + (4 * unit)));

            if (y + rowHeight > body.Y + body.W)
            {
                break;
            }

            var bounds = new Vector4(body.X + (16 * unit), y, body.Z - (32 * unit), rowHeight);

            Overlay.Rect(bounds.X, bounds.Y, bounds.Z, bounds.W, Panel);
            Overlay.Rect(bounds.X, bounds.Y, bounds.Z, 1, Rule);
            Overlay.Text(places[i].Label, bounds.X + (12 * unit), y + (7 * unit), Ink);

            _hits.Add(($"{verb}:{places[i].Id}", bounds));
        }
    }

    /// <summary>
    /// The binoculars: the room, seen through two circles.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The panorama is the room itself. The binoculars narrow the view and let the player
    /// pan the camera they already have, so what this draws is a mask over the picture
    /// rather than a picture of its own — which is also what the data says they are: each
    /// thing worth seeing is a rectangle in <em>degrees</em>, heading across and pitch up
    /// and down.
    /// </para>
    /// <para>
    /// <b>The mask is drawn in strips.</b> The overlay draws rectangles, so the two circles
    /// are cut out of the dark by covering each band of rows either side of them. Four
    /// pixels to a band, which is invisible against a flat mask and keeps the count bounded
    /// at any resolution — a row-per-pixel mask on a 4K display is nine thousand rectangles
    /// and the overlay holds four.
    /// </para>
    /// </remarks>
    private void Binoculars(ScreenView view, int width, int height, float unit)
    {
        // Two circles side by side, overlapping in the middle, filling most of the height.
        float radius = height * 0.40f;
        float gap = radius * 0.88f;
        var left = new Vector2((width / 2f) - gap, height / 2f);
        var right = new Vector2((width / 2f) + gap, height / 2f);

        const float band = 4f;

        for (float y = 0; y < height; y += band)
        {
            float middle = y + (band / 2f);

            float leftHalf = Span(middle - left.Y, radius);
            float rightHalf = Span(middle - right.Y, radius);

            if (leftHalf <= 0 && rightHalf <= 0)
            {
                Overlay.Rect(0, y, width, band, Eyepiece);

                continue;
            }

            // The union of the two circles on this row, which is one span because they
            // overlap.
            float from = leftHalf > 0 ? left.X - leftHalf : right.X - rightHalf;
            float to = rightHalf > 0 ? right.X + rightHalf : left.X + leftHalf;

            Overlay.Rect(0, y, MathF.Max(0, from), band, Eyepiece);
            Overlay.Rect(to, y, MathF.Max(0, width - to), band, Eyepiece);
        }

        // Crosshairs, at the middle, where a sight has to be to be zoomed into.
        float arm = 14f * unit;
        float centreX = width / 2f;
        float centreY = height / 2f;

        Overlay.Rect(centreX - arm, centreY, arm * 2, 1, Reticle);
        Overlay.Rect(centreX, centreY - arm, 1, arm * 2, Reticle);

        Panorama? panorama = view.Panorama;
        Sight? sighted = panorama?.At(view.Aim.X, view.Aim.Y);

        float readout = height - (46 * unit);

        Overlay.Text(
            string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{view.Aim.X:0} deg  {view.Aim.Y:+0;-0;0} deg"),
            (20 * unit),
            readout,
            Dim);

        if (sighted is not null)
        {
            string label = view.Map?.NameOf(
                DrivingMap.All.FirstOrDefault(s => s.Scene == sighted.Scene) ??
                new DrivingStop("dm_" + sighted.Scene.ToLowerInvariant(), sighted.Scene, 0, 0, false))
                ?? sighted.Scene;

            const string zoom = "LOOK CLOSER";
            float w = Overlay.Measure(zoom) + (24 * unit);
            var bounds = new Vector4(centreX - (w / 2), centreY + (radius * 0.55f), w, Overlay.LineHeight + (12 * unit));

            Overlay.Rect(bounds.X, bounds.Y, bounds.Z, bounds.W, PanelLit);
            Overlay.Rect(bounds.X, bounds.Y, bounds.Z, 1, Accent);
            Overlay.Text(zoom, bounds.X + (12 * unit), bounds.Y + (6 * unit), Ink);

            _hits.Add(("zoom:" + sighted.Location, bounds));

            float nameWidth = Overlay.Measure(label);
            Overlay.Text(label, centreX - (nameWidth / 2), centreY - (radius * 0.62f), Accent);
        }
        else if (panorama is { Any: true })
        {
            const string hint = "Pan to find something worth a closer look.";
            float hintWidth = Overlay.Measure(hint);

            Overlay.Text(hint, centreX - (hintWidth / 2), readout, Dim);
        }

        // The way out, in the same corner it is on every other screen.
        const string close = "LOWER";
        float closeWidth = Overlay.Measure(close) + (20 * unit);
        var closeAt = new Vector4(
            width - closeWidth - (20 * unit), (20 * unit), closeWidth, Overlay.LineHeight + (12 * unit));

        Overlay.Rect(closeAt.X, closeAt.Y, closeAt.Z, closeAt.W, PanelLit);
        Overlay.Text(close, closeAt.X + (10 * unit), closeAt.Y + (6 * unit), Ink);
        _hits.Add(("close", closeAt));
    }

    /// <summary>Half the width of a circle at a distance from its middle.</summary>
    private static float Span(float fromMiddle, float radius)
    {
        float squared = (radius * radius) - (fromMiddle * fromMiddle);

        return squared <= 0 ? 0 : MathF.Sqrt(squared);
    }

    /// <summary>
    /// The driving map: the game's own painting of the countryside, with the places on it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Drawn from the art rather than as a list, because the map <em>is</em> the content —
    /// a 640-by-480 painting of the Rennes-le-Château valley — and a list of place names is
    /// a table of contents for it rather than the thing itself. Each place's marker is a
    /// lit copy of that patch of the map, so drawing one over the base is what the original
    /// does and why the markers look like part of the picture.
    /// </para>
    /// <para>
    /// Scaled to fit the panel and centred, keeping its own proportions: the map is a
    /// painting and stretching it to an ultrawide window would be visible immediately.
    /// Everything on it is placed in map pixels and multiplied through, so the hit test and
    /// the picture cannot drift apart at any window size.
    /// </para>
    /// <para>
    /// <b>The places are named.</b> A marker is a patch of the painting lit a little
    /// brighter, which tells the player that something is there and nothing whatever about
    /// what — the original left them to hover each one in turn to find out, and sixteen
    /// unlabelled smudges of countryside is exactly the interface <c>Plan/03</c> section 3
    /// asks this port to be better than. So the open places are listed down the side, the
    /// one under the pointer is named on the map itself, and pointing at either the row or
    /// the marker lights up both. The names are the game's own, out of <c>ESTRINGS.TXT</c>.
    /// </para>
    /// <para>
    /// The list is dropped when the panel is too narrow to hold one without taking the map
    /// down to a thumbnail: a window that shape is one where the map wants every pixel, and
    /// the names on hover are still there.
    /// </para>
    /// <para>
    /// Falls back to a list of names when the art is not loaded — a run against archives
    /// that do not have it, or before the pictures have been handed over.
    /// </para>
    /// </remarks>
    private void Driving(ScreenView view, Vector4 body, float top, float unit)
    {
        IReadOnlyList<DrivingStop> stops = view.Stops ?? [];
        int background = view.Pictures?.Invoke(DrivingMap.Background) ?? 0;

        if (background <= 0)
        {
            // No art: the names, still carrying their scene codes.
            Places(
                [.. stops.Select(s => (s.Scene, Named(view, s)))],
                body,
                top,
                unit,
                "drive",
                "Nowhere to ride to yet.");

            return;
        }

        float room = body.Y + body.W - top - (Overlay.LineHeight * 2) - (16 * unit);

        // The names down the side, wide enough for the longest of them rather than for a
        // round number — "Southwest Arm of the Hexagram" is what the column has to hold —
        // and never worth more than a third of the panel. Dropped altogether when what is
        // left would take the map down to a thumbnail: at that shape the painting wants
        // every pixel, and the name of whatever is under the pointer is still drawn on it.
        //
        // Nested rather than clamped: a panel narrower than the smallest useful column
        // makes the floor larger than the ceiling, and the answer there is the ceiling and
        // then the test below, not an exception.
        float listWidth = stops.Count == 0
            ? 0
            : Math.Min(
                Math.Max(
                    stops.Max(s => Overlay.Measure(Named(view, s))) + (28 * unit),
                    120 * unit),
                body.Z * 0.34f);

        if (body.Z - listWidth < 260 * unit)
        {
            listWidth = 0;
        }

        float across = body.Z - (32 * unit) - listWidth;
        float scale = MathF.Min(across / DrivingMap.MapWidth, room / DrivingMap.MapHeight);

        float mapWidth = DrivingMap.MapWidth * scale;
        float mapHeight = DrivingMap.MapHeight * scale;
        float left = body.X + ((body.Z - listWidth - mapWidth) / 2);

        Overlay.Picture(background, left, top, mapWidth, mapHeight, Vector4.One);

        // Which place the pointer is on, whether it found it on the map or in the list.
        // Decided before anything is named so that the marker and its row agree, and so
        // that the name can be drawn over its neighbours rather than under them.
        DrivingStop? under = null;
        List<(DrivingStop Stop, Vector4 Bounds)> marked = [];

        foreach (DrivingStop stop in stops)
        {
            int marker = view.Pictures?.Invoke(stop.Sprite) ?? 0;

            // The marker's own size in map pixels is the picture's; without it there is
            // nothing to draw and nothing to click, so the place is simply not offered.
            if (marker <= 0 || view.Pictures is null)
            {
                continue;
            }

            (int w, int h) = Sizes.TryGetValue(stop.Sprite, out (int, int) size) ? size : (0, 0);

            if (w <= 0 || h <= 0)
            {
                continue;
            }

            var bounds = new Vector4(
                left + (stop.X * scale), top + (stop.Y * scale), w * scale, h * scale);

            Overlay.Picture(marker, bounds.X, bounds.Y, bounds.Z, bounds.W, Vector4.One);

            marked.Add((stop, bounds));
            _hits.Add(("drive:" + stop.Scene, bounds));

            if (Inside(_pointer, bounds))
            {
                under = stop;
            }
        }

        if (listWidth > 0)
        {
            under = DrivingList(
                view,
                stops,
                new Vector4(body.X + body.Z - listWidth - (16 * unit), top, listWidth, room),
                unit,
                under) ?? under;
        }

        // The one being pointed at, ringed and named. Last, so its name is legible where
        // the markers crowd together — Blanchefort, the dig and Larry's house are within
        // sixty of the map's own pixels of each other.
        if (under is { } chosen &&
            marked.Find(m => m.Stop == chosen) is { Stop: not null } pointed)
        {
            Ring(pointed.Bounds, unit);
            MarkerName(Named(view, chosen), pointed.Bounds, body, unit);
        }

        Overlay.Text(
            "Click a place to ride there.",
            body.X + (20 * unit),
            top + mapHeight + (10 * unit),
            Dim);
    }

    /// <summary>What a place is called, falling back to its code.</summary>
    private static string Named(ScreenView view, DrivingStop stop) =>
        view.Map?.NameOf(stop) ?? stop.Code;

    /// <summary>Draws a box around the marker the pointer is on.</summary>
    /// <remarks>
    /// Four lines rather than a tint over it: a marker is already a lit copy of the map
    /// underneath, and lighting it further is a change the eye has nothing to compare
    /// against, while a box around it is unambiguous over any part of the painting.
    /// </remarks>
    private void Ring(Vector4 bounds, float unit)
    {
        float thick = MathF.Max(1f, unit);

        Overlay.Rect(bounds.X, bounds.Y, bounds.Z, thick, Accent);
        Overlay.Rect(bounds.X, bounds.Y + bounds.W - thick, bounds.Z, thick, Accent);
        Overlay.Rect(bounds.X, bounds.Y, thick, bounds.W, Accent);
        Overlay.Rect(bounds.X + bounds.Z - thick, bounds.Y, thick, bounds.W, Accent);
    }

    /// <summary>Names the marker the pointer is on, beside it and inside the panel.</summary>
    /// <remarks>
    /// Under the marker where there is room and over it where there is not, and pushed back
    /// inside the panel either way: several of the sixteen places sit within a marker's
    /// width of an edge of the painting, and a name that runs off it is no name at all.
    /// </remarks>
    private void MarkerName(string name, Vector4 marker, Vector4 body, float unit)
    {
        float padding = 6 * unit;
        float wide = Overlay.Measure(name) + (padding * 2);
        float high = Overlay.LineHeight + padding;

        float x = Math.Clamp(
            marker.X + ((marker.Z - wide) / 2),
            body.X + (8 * unit),
            MathF.Max(body.X + (8 * unit), body.X + body.Z - wide - (8 * unit)));

        float below = marker.Y + marker.W + (4 * unit);
        float y = below + high > body.Y + body.W
            ? marker.Y - high - (4 * unit)
            : below;

        Overlay.Rect(x, y, wide, high, PanelLit);
        Overlay.Rect(x, y, wide, MathF.Max(1f, unit), Accent);
        Overlay.Text(name, x + padding, y + (padding / 2), Ink);
    }

    /// <summary>
    /// The open places, listed beside the map.
    /// </summary>
    /// <param name="view">What is being drawn.</param>
    /// <param name="stops">The places, in the order the map draws them.</param>
    /// <param name="column">Where the list goes.</param>
    /// <param name="unit">How much bigger than the letters everything else is.</param>
    /// <param name="lit">The place the pointer found on the map, if it found one.</param>
    /// <returns>The place the pointer is on in the list, or null.</returns>
    /// <remarks>
    /// Every row is a way to ride there, so a player who knows the name they want never has
    /// to find it on the painting first. In map order rather than alphabetical: the list is
    /// a reading of the picture beside it, and two orderings of the same sixteen things is
    /// one more thing to learn.
    /// </remarks>
    private DrivingStop? DrivingList(
        ScreenView view,
        IReadOnlyList<DrivingStop> stops,
        Vector4 column,
        float unit,
        DrivingStop? lit)
    {
        // Tightened rather than truncated where the column is short: a place missing from
        // the list is a place the player has no name for, which is the fault this list
        // exists to fix. The floor is a row that still fits its own letters.
        float row = Math.Clamp(
            column.W / Math.Max(1, stops.Count),
            Overlay.LineHeight + (2 * unit),
            Overlay.LineHeight + (10 * unit));

        DrivingStop? under = null;

        for (int i = 0; i < stops.Count; i++)
        {
            float y = column.Y + (i * row);

            if (y + row > column.Y + column.W)
            {
                break;
            }

            var bounds = new Vector4(column.X, y, column.Z, row - (2 * unit));
            bool pointed = Inside(_pointer, bounds);

            if (pointed)
            {
                under = stops[i];
            }

            bool hot = pointed || stops[i] == lit;

            Overlay.Rect(bounds.X, bounds.Y, bounds.Z, bounds.W, hot ? PanelLit : Panel);
            Overlay.Rect(
                bounds.X, bounds.Y, MathF.Max(1f, 2 * unit), bounds.W, hot ? Accent : Rule);

            Overlay.Text(
                Named(view, stops[i]),
                bounds.X + (12 * unit),
                bounds.Y + (5 * unit),
                hot ? Accent : Ink);

            _hits.Add(("drive:" + stops[i].Scene, bounds));
        }

        return under;
    }

    /// <summary>How big each of the map's markers is, in the map's own pixels.</summary>
    /// <remarks>
    /// The pictures know their own size and the painter cannot ask them — it draws through
    /// an overlay that takes numbers, not textures — so whoever loads them says so here.
    /// </remarks>
    public IDictionary<string, (int Width, int Height)> Sizes { get; } =
        new Dictionary<string, (int, int)>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Sidney, whichever of its screens is showing.</summary>
    private void Sidney(ScreenView view, Vector4 body, float top, float unit)
    {
        if (view.Sidney is not { } machine)
        {
            Overlay.Text("Sidney is not switched on.", body.X + (20 * unit), top, Dim);

            return;
        }

        switch (machine.Screen)
        {
            case SidneyScreen.Main:
                SidneyMain(machine, body, top, unit);
                break;

            case SidneyScreen.EMail:
                SidneyMail(machine, body, top, unit);
                break;

            case SidneyScreen.AddData:
                SidneyAdd(machine, view, body, top, unit);
                break;

            case SidneyScreen.Analyze:
                SidneyAnalyze(machine, body, top, unit, view);
                break;

            case SidneyScreen.Search:
                SidneySearchScreen(machine, body, top, unit);
                break;

            case SidneyScreen.Suspects:
                SidneySuspects(machine, body, top, unit);
                break;

            case SidneyScreen.MakeId:
                SidneyMakeId(machine, body, top, unit);
                break;

            default:
                Overlay.Text(
                    machine.Library.Say("NotImplemented", "Main Screen") is { Length: > 0 } note
                        ? note
                        : "Not implemented yet.",
                    body.X + (20 * unit),
                    top,
                    Dim);

                Back(body, unit);
                break;
        }
    }

    /// <summary>Sidney's front screen: the eight rows of its own menu.</summary>
    private void SidneyMain(SidneyMachine machine, Vector4 body, float top, float unit)
    {
        float rowHeight = Overlay.LineHeight + (16 * unit);
        int i = 0;

        foreach ((SidneyScreen screen, string label) in machine.Library.Rows())
        {
            float y = top + (i++ * (rowHeight + (4 * unit)));
            var bounds = new Vector4(body.X + (16 * unit), y, (body.Z / 2) - (16 * unit), rowHeight);

            Overlay.Rect(bounds.X, bounds.Y, bounds.Z, bounds.W, Panel);
            Overlay.Rect(bounds.X, bounds.Y, bounds.Z, 1, Rule);
            Overlay.Text(label, bounds.X + (12 * unit), y + (7 * unit), Ink);

            _hits.Add(($"sidney:screen:{screen}", bounds));
        }

        // What is in the machine, beside the menu, because "what have I scanned" is the
        // question the player actually arrives with.
        float x = body.X + (body.Z / 2) + (16 * unit);

        Overlay.Text("FILES", x, top, Accent);

        IReadOnlyList<SidneyFile> files = machine.Files;

        if (files.Count == 0)
        {
            Overlay.Text("Nothing scanned yet.", x, top + (Overlay.LineHeight * 2), Dim);

            return;
        }

        for (int f = 0; f < files.Count; f++)
        {
            Overlay.Text(
                files[f].Label,
                x,
                top + (Overlay.LineHeight * (2 + f)) + (f * 2 * unit),
                Ink);
        }
    }

    /// <summary>Grace's mail.</summary>
    private void SidneyMail(SidneyMachine machine, Vector4 body, float top, float unit)
    {
        IReadOnlyList<SidneyMail> inbox = machine.Library.Mail();
        float rowHeight = Overlay.LineHeight + (12 * unit);
        float listWidth = (body.Z / 3) - (16 * unit);

        for (int i = 0; i < inbox.Count; i++)
        {
            SidneyMail mail = inbox[i];
            bool open = machine.Reading?.Id == mail.Id;

            float y = top + (i * (rowHeight + (4 * unit)));
            var bounds = new Vector4(body.X + (16 * unit), y, listWidth, rowHeight);

            Overlay.Rect(bounds.X, bounds.Y, bounds.Z, bounds.W, open ? PanelLit : Panel);
            Overlay.Rect(bounds.X, bounds.Y, bounds.Z, 1, open ? Accent : Rule);
            Overlay.Text(mail.Subject, bounds.X + (10 * unit), y + (5 * unit), open ? Accent : Ink);

            _hits.Add(("sidney:mail:" + mail.Id, bounds));
        }

        if (machine.Reading is { } reading)
        {
            float x = body.X + listWidth + (32 * unit);
            float wrap = body.Z - listWidth - (56 * unit);
            float y = top;

            Overlay.Text($"From: {reading.From}", x, y, Dim);
            y += Overlay.LineHeight;
            Overlay.Text($"Date: {reading.Date}", x, y, Dim);
            y += Overlay.LineHeight * 2;

            foreach (string paragraph in reading.Body)
            {
                y = Wrapped(paragraph, x, y, wrap, body.Y + body.W - (16 * unit), Ink);

                if (y > body.Y + body.W - (16 * unit))
                {
                    break;
                }
            }
        }

        Back(body, unit);
    }

    /// <summary>The scanner: what in the player's pockets Sidney will take.</summary>
    private void SidneyAdd(SidneyMachine machine, ScreenView view, Vector4 body, float top, float unit)
    {
        List<string> scannable = [.. view.Inventory.Where(machine.CanScan)];

        if (scannable.Count == 0)
        {
            Overlay.Text(
                "Nothing here that the scanner will take.", body.X + (20 * unit), top, Dim);

            Back(body, unit);

            return;
        }

        float rowHeight = Overlay.LineHeight + (14 * unit);

        for (int i = 0; i < scannable.Count; i++)
        {
            float y = top + (i * (rowHeight + (4 * unit)));
            var bounds = new Vector4(body.X + (16 * unit), y, (body.Z / 2) - (16 * unit), rowHeight);

            Overlay.Rect(bounds.X, bounds.Y, bounds.Z, bounds.W, Panel);
            Overlay.Rect(bounds.X, bounds.Y, bounds.Z, 1, Rule);
            Overlay.Text(Pretty(scannable[i]), bounds.X + (12 * unit), y + (6 * unit), Ink);

            _hits.Add(("sidney:scan:" + scannable[i], bounds));
        }

        if (machine.Showing is { } said)
        {
            Wrapped(
                said.Text,
                body.X + (body.Z / 2) + (16 * unit),
                top,
                (body.Z / 2) - (32 * unit),
                body.Y + body.W - (16 * unit),
                Accent);
        }

        Back(body, unit);
    }

    /// <summary>The analyze screen: a file, what may be done to it, and what it said.</summary>
    /// <summary>
    /// Sidney's map, its marks and whatever is laid over it.
    /// </summary>
    /// <remarks>
    /// The survey of the Rennes country the whole puzzle is about, with the Paris meridian
    /// down it. Clicking marks a place; four places that fall on a circle are what the story
    /// is waiting to be told. The picture is drawn at whatever size the panel affords and
    /// every mark is kept in the map's own 1,368 pixels, so the marks and the hit test
    /// cannot drift apart at any window size.
    /// </remarks>
    private void SidneyMap(
        SidneyMachine machine, Vector4 body, float top, float unit, ScreenView view)
    {
        int picture = view.Pictures?.Invoke(Game.Sidney.SidneyMap.Picture) ?? 0;

        float room = body.Y + body.W - top - (60 * unit);
        float side = MathF.Min(body.Z - (32 * unit), room);

        if (side < 40 * unit)
        {
            return;
        }

        float left = body.X + ((body.Z - side) / 2);
        float scale = side / Game.Sidney.SidneyMap.Extent;

        if (picture > 0)
        {
            Overlay.Picture(picture, left, top, side, side, Vector4.One);
        }
        else
        {
            Overlay.Rect(left, top, side, side, PanelLit);
        }

        // The ruling, where one has been drawn.
        if (machine.Map.Grid > 0)
        {
            float step = side / machine.Map.Grid;

            for (int i = 1; i < machine.Map.Grid; i++)
            {
                Overlay.Rect(left + (i * step), top, 1, side, Rule);
                Overlay.Rect(left, top + (i * step), side, 1, Rule);
            }
        }

        // The circle the analysis found, before the marks so they sit on top of it.
        if (machine.Map.Found is { Finding: MapFinding.Circle } found)
        {
            Ring(
                left + (found.Centre.X * scale),
                top + (found.Centre.Y * scale),
                found.Radius * scale,
                Accent);
        }

        // The shape laid over the country, in the colour that says whether it is confirmed.
        if (machine.Map.Shape != MapShape.None)
        {
            Vector4 ink = machine.Map.Locked ? Locked : Accent;

            if (machine.Map.Shape == MapShape.Circle)
            {
                Ring(
                    left + (machine.Map.ShapeAt.X * scale),
                    top + (machine.Map.ShapeAt.Y * scale),
                    machine.Map.ShapeSize * scale,
                    ink);
            }
            else if (machine.Map.Shape == MapShape.Hexagram)
            {
                // Two triangles, which is how the analysis of Poussin's painting describes
                // finding it, rather than a twelve-sided outline.
                foreach (System.Numerics.Vector2[] triangle in machine.Map.Triangles())
                {
                    Outline(triangle, left, top, scale, ink);
                }
            }
            else
            {
                Outline(machine.Map.Corners(), left, top, scale, ink);
            }
        }

        foreach (System.Numerics.Vector2 point in machine.Map.Points)
        {
            float x = left + (point.X * scale);
            float y = top + (point.Y * scale);
            float arm = 5f * unit;

            Overlay.Rect(x - arm, y, arm * 2, 1, Accent);
            Overlay.Rect(x, y - arm, 1, arm * 2, Accent);
        }

        // The whole picture is the target: a click anywhere on it marks that place.
        MapBounds = new Vector4(left, top, side, side);
        _hits.Add(("sidney:mark", MapBounds));

        // The shape list, when one has been asked for, drawn over the map rather than
        // beside it: it is a short list and it belongs where the player is looking.
        if (machine.Choosing)
        {
            float rowHeight = Overlay.LineHeight + (10 * unit);
            float width = 160 * unit;
            float y = top + (12 * unit);

            foreach (MapShape shape in machine.Shapes)
            {
                string name = Game.Sidney.SidneyMap.NameOf(shape);
                var bounds = new Vector4(left + (12 * unit), y, width, rowHeight);

                Overlay.Rect(bounds.X, bounds.Y, bounds.Z, bounds.W, Panel);
                Overlay.Rect(bounds.X, bounds.Y, bounds.Z, 1, Accent);
                Overlay.Text(name, bounds.X + (10 * unit), y + (5 * unit), Ink);

                _hits.Add(("sidney:shape:" + name, bounds));

                y += rowHeight + (4 * unit);
            }
        }

        if (machine.Showing is { } said)
        {
            Wrapped(
                said.Text,
                body.X + (20 * unit),
                top + side + (8 * unit),
                body.Z - (40 * unit),
                body.Y + body.W - (20 * unit),
                Ink);
        }
    }

    /// <summary>
    /// A closed outline through map points, drawn as a run of short marks.
    /// </summary>
    /// <remarks>
    /// Not one rectangle per side: a rectangle covering a diagonal side's bounding box is a
    /// filled block, which is right for the axis-aligned grid and wrong for everything a
    /// turned shape draws.
    /// </remarks>
    private void Outline(
        System.Numerics.Vector2[] corners, float left, float top, float scale, Vector4 colour)
    {
        for (int i = 0; i < corners.Length; i++)
        {
            System.Numerics.Vector2 a = corners[i];
            System.Numerics.Vector2 b = corners[(i + 1) % corners.Length];

            Streak(
                left + (a.X * scale),
                top + (a.Y * scale),
                left + (b.X * scale),
                top + (b.Y * scale),
                colour);
        }
    }

    /// <summary>A straight line, drawn as a run of single pixels along it.</summary>
    private void Streak(float ax, float ay, float bx, float by, Vector4 colour)
    {
        float run = MathF.Max(MathF.Abs(bx - ax), MathF.Abs(by - ay));
        int steps = (int)MathF.Min(MathF.Max(run, 1), 4096);

        for (int i = 0; i <= steps; i++)
        {
            float t = i / (float)steps;

            Overlay.Rect(ax + ((bx - ax) * t), ay + ((by - ay) * t), 1, 1, colour);
        }
    }

    /// <summary>A circle, drawn as short chords because the overlay draws rectangles.</summary>
    private void Ring(float x, float y, float radius, Vector4 colour)
    {
        const int steps = 72;

        for (int i = 0; i < steps; i++)
        {
            float angle = i * MathF.Tau / steps;
            float next = (i + 1) * MathF.Tau / steps;

            float ax = x + (MathF.Cos(angle) * radius);
            float ay = y + (MathF.Sin(angle) * radius);
            float bx = x + (MathF.Cos(next) * radius);
            float by = y + (MathF.Sin(next) * radius);

            // One rectangle covering the chord's bounding box, which at this many steps is
            // a segment two pixels long and reads as a curve.
            Overlay.Rect(
                MathF.Min(ax, bx),
                MathF.Min(ay, by),
                MathF.Max(1, MathF.Abs(bx - ax)),
                MathF.Max(1, MathF.Abs(by - ay)),
                colour);
        }
    }

    /// <summary>Where the map was drawn last, so a click can be turned into a place.</summary>
    public Vector4 MapBounds { get; private set; }

    private void SidneyAnalyze(
        SidneyMachine machine, Vector4 body, float top, float unit, ScreenView view)
    {
        float rowHeight = Overlay.LineHeight + (12 * unit);
        float listWidth = (body.Z / 3) - (16 * unit);
        IReadOnlyList<SidneyFile> files = machine.Files;

        if (files.Count == 0)
        {
            Overlay.Text("No files. Scan something first.", body.X + (20 * unit), top, Dim);

            Back(body, unit);

            return;
        }

        for (int i = 0; i < files.Count; i++)
        {
            SidneyFile file = files[i];
            bool open = machine.Open?.Id == file.Id;

            float y = top + (i * (rowHeight + (4 * unit)));
            var bounds = new Vector4(body.X + (16 * unit), y, listWidth, rowHeight);

            Overlay.Rect(bounds.X, bounds.Y, bounds.Z, bounds.W, open ? PanelLit : Panel);
            Overlay.Rect(bounds.X, bounds.Y, bounds.Z, 1, open ? Accent : Rule);
            Overlay.Text(file.Label, bounds.X + (10 * unit), y + (5 * unit), open ? Accent : Ink);

            _hits.Add(("sidney:file:" + file.Id, bounds));
        }

        float x = body.X + listWidth + (32 * unit);
        float rest = body.Z - listWidth - (56 * unit);
        float at = top;

        // What may be done to the open file, offered rather than left to be found by
        // exhaustion — the original enabled every menu item and answered most of them with
        // a note about why not.
        foreach (SidneyAction action in machine.Available())
        {
            string label = Label(action);
            float w = Overlay.Measure(label) + (20 * unit);
            var bounds = new Vector4(x, at, w, Overlay.LineHeight + (10 * unit));

            bool done = machine.Open is { } open && machine.HasDone(open, action);

            Overlay.Rect(bounds.X, bounds.Y, bounds.Z, bounds.W, done ? Panel : PanelLit);
            Overlay.Rect(bounds.X, bounds.Y, bounds.Z, 1, done ? Rule : Accent);
            Overlay.Text(label, x + (10 * unit), at + (5 * unit), done ? Dim : Ink);

            _hits.Add(($"sidney:do:{action}", bounds));

            x += w + (8 * unit);

            // Wrap the row of operations rather than running off the panel.
            if (x + (120 * unit) > body.X + body.Z - (16 * unit))
            {
                x = body.X + listWidth + (32 * unit);
                at += Overlay.LineHeight + (16 * unit);
            }
        }

        // The map gets the panel rather than a paragraph: it is a picture to mark places
        // on, and reading coordinates off a list is not the puzzle.
        if (machine.Open?.Kind == SidneyKind.Map)
        {
            SidneyMap(machine, body, at + Overlay.LineHeight + (20 * unit), unit, view);

            Back(body, unit);

            return;
        }

        float y2 = at + Overlay.LineHeight + (26 * unit);
        float left = body.X + listWidth + (32 * unit);

        if (machine.Showing is { } result)
        {
            y2 = Wrapped(result.Text, left, y2, rest, body.Y + body.W - (60 * unit), Ink);

            if (result.Asks is { Length: > 0 } question && result.Choices is { Count: > 0 } choices)
            {
                y2 += Overlay.LineHeight;
                Overlay.Text(question, left, y2, Accent);
                y2 += Overlay.LineHeight + (6 * unit);

                float cx = left;

                foreach (string choice in choices)
                {
                    float w = Overlay.Measure(choice) + (20 * unit);
                    var bounds = new Vector4(cx, y2, w, Overlay.LineHeight + (10 * unit));

                    Overlay.Rect(bounds.X, bounds.Y, bounds.Z, bounds.W, PanelLit);
                    Overlay.Rect(bounds.X, bounds.Y, bounds.Z, 1, Accent);
                    Overlay.Text(choice, cx + (10 * unit), y2 + (5 * unit), Ink);

                    _hits.Add(("sidney:answer:" + choice, bounds));

                    cx += w + (8 * unit);
                }
            }
        }

        Back(body, unit);
    }

    /// <summary>
    /// Sidney's search: a box to type in and a page of the encyclopedia.
    /// </summary>
    /// <remarks>
    /// The subject list is not shown. Three hundred and ninety-one pages offered as a menu
    /// is a walkthrough — the puzzle is knowing what to look up — so the player types, and
    /// what they type is checked against the spellings the game itself lists.
    /// </remarks>
    private void SidneySearchScreen(SidneyMachine machine, Vector4 body, float top, float unit)
    {
        float row = Overlay.LineHeight + (12 * unit);
        var box = new Vector4(body.X + (16 * unit), top, (body.Z / 2) - (16 * unit), row);

        Overlay.Rect(box.X, box.Y, box.Z, box.W, PanelLit);
        Overlay.Rect(box.X, box.Y, box.Z, 1, Accent);

        Overlay.Text(
            machine.Typed.Length > 0 ? machine.Typed : "Type a subject...",
            box.X + (10 * unit),
            top + (5 * unit),
            machine.Typed.Length > 0 ? Ink : Dim);

        _hits.Add(("sidney:type", box));

        const string go = "SEARCH";
        float goWidth = Overlay.Measure(go) + (20 * unit);
        var goAt = new Vector4(box.X + box.Z + (8 * unit), top, goWidth, row);

        Overlay.Rect(goAt.X, goAt.Y, goAt.Z, goAt.W, PanelLit);
        Overlay.Text(go, goAt.X + (10 * unit), top + (5 * unit), Ink);
        _hits.Add(("sidney:look", goAt));

        float y = top + row + (16 * unit);
        float wrap = body.Z - (40 * unit);
        float left = body.X + (20 * unit);
        float bottom = body.Y + body.W - (48 * unit);

        if (machine.Page is not { } page)
        {
            if (machine.Showing is { } said)
            {
                Overlay.Text(said.Text, left, y, Dim);
            }

            Back(body, unit);

            return;
        }

        Overlay.Text(page.Title, left, y, Accent);
        y += Overlay.LineHeight * 2;

        foreach (SearchLine line in page.Lines)
        {
            if (y > bottom)
            {
                break;
            }

            if (line.Rule)
            {
                Overlay.Rect(left, y + (Overlay.LineHeight / 2f), wrap, 1, Rule);
                y += Overlay.LineHeight;

                continue;
            }

            if (line.Link is { Length: > 0 } target)
            {
                // A link is a word in the middle of a sentence, so it is drawn on its own
                // line rather than inline: laying text around a hit rectangle is a
                // typesetter, and this is a list of paragraphs.
                float width = Overlay.Measure(line.Text) + (12 * unit);
                var bounds = new Vector4(left, y, MathF.Min(width, wrap), Overlay.LineHeight);

                Overlay.Text(line.Text, left, y, Accent);
                Overlay.Rect(left, y + Overlay.LineHeight - 1, MathF.Min(width, wrap), 1, Accent);

                _hits.Add(("sidney:page:" + target, bounds));

                y += Overlay.LineHeight;

                continue;
            }

            y = Wrapped(line.Text, left, y, wrap, bottom, line.Heading ? Accent : Ink);
        }

        Back(body, unit);
    }

    /// <summary>
    /// The suspects: ten files, what has been linked to each, and the match.
    /// </summary>
    private void SidneySuspects(SidneyMachine machine, Vector4 body, float top, float unit)
    {
        IReadOnlyList<SidneySuspect> people = machine.Library.Suspects();
        float row = Overlay.LineHeight + (10 * unit);
        float listWidth = (body.Z / 3) - (16 * unit);

        for (int i = 0; i < people.Count; i++)
        {
            SidneySuspect person = people[i];
            bool open = machine.Suspect?.Index == person.Index;

            float y = top + (i * (row + (3 * unit)));

            if (y + row > body.Y + body.W - (48 * unit))
            {
                break;
            }

            var bounds = new Vector4(body.X + (16 * unit), y, listWidth, row);

            Overlay.Rect(bounds.X, bounds.Y, bounds.Z, bounds.W, open ? PanelLit : Panel);
            Overlay.Rect(bounds.X, bounds.Y, bounds.Z, 1, open ? Accent : Rule);
            Overlay.Text(person.Name, bounds.X + (10 * unit), y + (4 * unit), open ? Accent : Ink);

            _hits.Add(($"sidney:suspect:{person.Index}", bounds));
        }

        float x = body.X + listWidth + (32 * unit);
        float rest = body.Z - listWidth - (56 * unit);

        if (machine.Suspect is not { } suspect)
        {
            Overlay.Text("Open a suspect's file.", x, top, Dim);

            Back(body, unit);

            return;
        }

        float at = top;

        Overlay.Text($"{machine.Library.Say("Name", "Suspects Screen")} {suspect.Name}", x, at, Ink);
        at += Overlay.LineHeight;
        Overlay.Text(
            $"{machine.Library.Say("Nationality", "Suspects Screen")} {suspect.Nationality}", x, at, Dim);
        at += Overlay.LineHeight;
        Overlay.Text(
            $"{machine.Library.Say("Vehicle", "Suspects Screen")} {suspect.Vehicle}", x, at, Dim);
        at += Overlay.LineHeight * 2;

        // What is linked, and what may be.
        IReadOnlyList<SidneyFile> linked = machine.LinkedTo(suspect);

        Overlay.Text(
            linked.Count > 0
                ? machine.Library.Say("FileList", "Suspects Screen")
                : machine.Library.Say("NoLinks", "Suspects Screen"),
            x,
            at,
            Accent);

        at += Overlay.LineHeight + (4 * unit);

        foreach (SidneyFile file in linked)
        {
            var bounds = new Vector4(x, at, rest, Overlay.LineHeight + (6 * unit));

            Overlay.Rect(bounds.X, bounds.Y, bounds.Z, bounds.W, PanelLit);
            Overlay.Text(file.Label, x + (8 * unit), at + (2 * unit), Ink);

            _hits.Add(("sidney:unlink:" + file.Id, bounds));

            at += Overlay.LineHeight + (10 * unit);
        }

        at += Overlay.LineHeight / 2;

        foreach (SidneyFile file in machine.Files)
        {
            if (file.Kind is not (SidneyKind.KnownPrint or SidneyKind.UnknownPrint or SidneyKind.Licence) ||
                linked.Any(l => l.Id == file.Id))
            {
                continue;
            }

            if (at > body.Y + body.W - (64 * unit))
            {
                break;
            }

            var bounds = new Vector4(x, at, rest, Overlay.LineHeight + (6 * unit));

            Overlay.Rect(bounds.X, bounds.Y, bounds.Z, bounds.W, Panel);
            Overlay.Rect(bounds.X, bounds.Y, bounds.Z, 1, Rule);
            Overlay.Text($"link  {file.Label}", x + (8 * unit), at + (2 * unit), Dim);

            _hits.Add(("sidney:link:" + file.Id, bounds));

            at += Overlay.LineHeight + (10 * unit);
        }

        const string match = "MATCH ANALYSIS";
        float matchWidth = Overlay.Measure(match) + (20 * unit);
        var matchAt = new Vector4(x, body.Y + body.W - (44 * unit), matchWidth, Overlay.LineHeight + (10 * unit));

        Overlay.Rect(matchAt.X, matchAt.Y, matchAt.Z, matchAt.W, PanelLit);
        Overlay.Rect(matchAt.X, matchAt.Y, matchAt.Z, 1, Accent);
        Overlay.Text(match, matchAt.X + (10 * unit), matchAt.Y + (5 * unit), Ink);
        _hits.Add(("sidney:match", matchAt));

        if (machine.Showing is { } result)
        {
            Wrapped(
                result.Text,
                matchAt.X + matchWidth + (16 * unit),
                matchAt.Y,
                rest - matchWidth - (16 * unit),
                body.Y + body.W,
                Accent);
        }

        Back(body, unit);
    }

    /// <summary>The identity card: five trades, and printing one.</summary>
    private void SidneyMakeId(SidneyMachine machine, Vector4 body, float top, float unit)
    {
        IReadOnlyList<SidneyIdentity> identities = machine.Library.Identities();

        Overlay.Text(machine.Library.Say("Select", "MakeID Screen"), body.X + (20 * unit), top, Accent);

        float y = top + Overlay.LineHeight + (8 * unit);
        float x = body.X + (16 * unit);
        string? category = null;

        foreach (SidneyIdentity identity in identities)
        {
            if (!string.Equals(category, identity.Category, StringComparison.Ordinal))
            {
                category = identity.Category;
                x = body.X + (16 * unit);
                y += Overlay.LineHeight + (6 * unit);

                Overlay.Text(category, x, y, Dim);

                y += Overlay.LineHeight + (4 * unit);
            }

            float width = Overlay.Measure(identity.Title) + (20 * unit);

            if (x + width > body.X + body.Z - (16 * unit))
            {
                x = body.X + (16 * unit);
                y += Overlay.LineHeight + (14 * unit);
            }

            bool chosen = machine.Identity?.Title == identity.Title;
            var bounds = new Vector4(x, y, width, Overlay.LineHeight + (10 * unit));

            Overlay.Rect(bounds.X, bounds.Y, bounds.Z, bounds.W, chosen ? PanelLit : Panel);
            Overlay.Rect(bounds.X, bounds.Y, bounds.Z, 1, chosen ? Accent : Rule);
            Overlay.Text(identity.Title, x + (10 * unit), y + (5 * unit), chosen ? Accent : Ink);

            _hits.Add(("sidney:id:" + identity.Title, bounds));

            x += width + (8 * unit);
        }

        if (machine.Identity is { } printed)
        {
            Overlay.Text(
                $"{machine.Library.Say("Print", "MakeID Screen")}: {printed.Category}, {printed.Title}",
                body.X + (20 * unit),
                body.Y + body.W - (44 * unit),
                Accent);
        }

        Back(body, unit);
    }

    /// <summary>The way back to Sidney's own front screen.</summary>
    private void Back(Vector4 body, float unit)
    {
        const string label = "MAIN MENU";

        float w = Overlay.Measure(label) + (20 * unit);
        var bounds = new Vector4(
            body.X + (16 * unit),
            body.Y + body.W - Overlay.LineHeight - (20 * unit),
            w,
            Overlay.LineHeight + (10 * unit));

        Overlay.Rect(bounds.X, bounds.Y, bounds.Z, bounds.W, PanelLit);
        Overlay.Text(label, bounds.X + (10 * unit), bounds.Y + (5 * unit), Ink);

        _hits.Add(("sidney:home", bounds));
    }

    /// <summary>Draws a paragraph inside a width, and says where the next one starts.</summary>
    private float Wrapped(string text, float x, float y, float width, float bottom, Vector4 colour)
    {
        if (text.Length == 0)
        {
            return y + Overlay.LineHeight;
        }

        foreach (string paragraph in text.Split('\n'))
        {
            string line = string.Empty;

            foreach (string word in paragraph.Replace('\t', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                string longer = line.Length == 0 ? word : line + " " + word;

                if (Overlay.Measure(longer) > width && line.Length > 0)
                {
                    if (y > bottom)
                    {
                        return y;
                    }

                    Overlay.Text(line, x, y, colour);
                    y += Overlay.LineHeight;
                    line = word;
                }
                else
                {
                    line = longer;
                }
            }

            if (line.Length > 0)
            {
                if (y > bottom)
                {
                    return y;
                }

                Overlay.Text(line, x, y, colour);
            }

            y += Overlay.LineHeight;
        }

        return y;
    }

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
        SidneyAction.DrawGrid => "DRAW GRID",
        SidneyAction.EraseGrid => "ERASE GRID",
        SidneyAction.UseShape => "USE SHAPE",
        SidneyAction.EraseShape => "ERASE SHAPE",
        _ => action.ToString().ToUpperInvariant(),
    };

    /// <summary>A noun as a person would write it.</summary>
    private static string Pretty(string noun) =>
        Game.Sidney.SidneyFiles.Pretty(noun);
}
