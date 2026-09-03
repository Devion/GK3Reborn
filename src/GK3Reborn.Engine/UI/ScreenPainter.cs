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
/// <param name="Icons">
/// The game's own picture of an item, by item name. Null when nothing has been loaded, and
/// an item may answer with nothing, so both are ordinary.
/// </param>
/// <param name="CloseUps">
/// The bigger picture the artists painted of an item — the readable one. Only the close-up
/// screen wants it, and an item may have none, in which case the list picture stands in.
/// </param>
/// <param name="VerbIcons">
/// The picture belonging to a verb, resting or picked out. The close-up's buttons draw
/// these beside the word, which is how a returning player reads them at a glance.
/// </param>
/// <param name="Water">
/// The hose, where one is being aimed. Live state rather than a snapshot: the jet, the nest
/// and the clock all move every frame, and none of it is the screen stack's business.
/// </param>
/// <param name="Artwork">
/// The game's own bitmaps by file name — the laptop Sidney is drawn inside, and the eight
/// name plates on its desktop. Null where none have been loaded, and any one of them may
/// answer with nothing, so a screen that wants one has to be able to do without it.
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
    int Prints = -1,
    Func<string, ItemIcon>? Icons = null,
    Func<string, ItemIcon>? CloseUps = null,
    Func<string, bool, ItemIcon>? VerbIcons = null,
    Func<string, ItemIcon>? Artwork = null,
    Game.WaterAiming? Water = null);

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
    private readonly Sidney.SidneyView _sidney = new();

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

        // Nor is Sidney a page over the room. It is a laptop Grace is holding, drawn as
        // large as the window will carry it with the room dark behind, and everything it
        // shows is inside the screen its own art frames.
        if (view.Screen.Kind == ScreenKind.Sidney)
        {
            Sidney(view, width, height, unit);

            return;
        }

        // Nor is a thing held up close a page. It is one picture, as large as the window
        // will carry it, and the original gave it the whole screen for the same reason:
        // the picture *is* the content — two pages of a book meant to be read — and a
        // 94-pixel thumbnail in a dialog box is not a close-up of anything.
        if (view.Screen.Kind == ScreenKind.InventoryInspect)
        {
            Inspect(view, width, height, unit);

            return;
        }

        // The room stays visible behind everything but the driving map, which is the one
        // screen where the player is somewhere else entirely. Drawn with the body below,
        // because how much of the window the body takes decides where it goes.
        // How much of the window a screen takes. The inventory is a page — it is a list of
        // everything the player owns and wants the room — and one item held up to the light
        // is not. Asked for: a close-up of a single thing filling the screen reads as a
        // modal error box rather than as looking at something.
        bool page = view.Screen.Kind != ScreenKind.Fingerprint;

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

            case ScreenKind.Binoculars:
                // Its own frame rather than the shared panel: the binoculars are a way of
                // looking at the room, not a page in front of it.
                Binoculars(view, width, height, unit);
                break;

            case ScreenKind.Driving:
                Driving(view, body, top, unit);
                break;

            case ScreenKind.Journal:
                JournalPage(view, body, top, unit);
                break;

            case ScreenKind.Fingerprint:
                Fingerprint(view, body, top, unit);
                break;

            case ScreenKind.Water:
                // Its own frame, like the binoculars: the player is looking up a tree with
                // a hose, not reading a page in front of the room.
                Water(view, width, height, unit);
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
        ScreenKind.Water => "THE HOSE",
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

        // The item's own picture, where the game has one. Reserved whether it has or not,
        // so that the names down a column start in the same place — an inventory where
        // every other row is indented reads as a list that has gone wrong.
        float art = view.Icons is null ? 0 : 40 * unit;
        float cell = (210f * unit) + art;
        float rowHeight = MathF.Max(Overlay.LineHeight + (18 * unit), art + (12 * unit));
        int columns = Math.Max(1, (int)((body.Z - (32 * unit)) / cell));
        Vector4? open = null;

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

            if (view.Icons?.Invoke(item) is { Drawn: true } icon)
            {
                Vector4 at = icon.Fit(x + (8 * unit), y + ((rowHeight - art) / 2), art);

                Overlay.Picture(icon.Picture, at.X, at.Y, at.Z, at.W, Vector4.One);
            }

            Overlay.Text(
                Pretty(item),
                x + (10 * unit) + art,
                y + ((rowHeight - Overlay.LineHeight) / 2),
                held ? Accent : Ink);

            _hits.Add(("item:" + item, bounds));

            if (string.Equals(view.Subject, item, StringComparison.OrdinalIgnoreCase))
            {
                open = bounds;
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

        // The verbs for whichever item was clicked, beside it, exactly as a right click in
        // the room offers a noun's verbs where the pointer is. Asked for: clicking a thing
        // in your pocket used to open a page of its own to hold two words on.
        //
        // After every item rather than beside its own, which is the same thing said twice:
        // laid down last it is drawn over the rest of the page instead of under the next
        // row of it, and hit-tested before the page, so a click on a word is that word and
        // not the item the words are covering.
        if (open is { } slot)
        {
            Beside(view, slot, body, unit);
        }
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

    /// <summary>The hose, and what it is being aimed at.</summary>
    /// <param name="view">What to draw, including the hose being aimed.</param>
    /// <param name="width">Window width.</param>
    /// <param name="height">Window height.</param>
    /// <param name="unit">The interface's scale.</param>
    /// <remarks>
    /// Drawn over the room rather than instead of it — the player is standing in the street
    /// and should be able to see they are — so this is a reticle, a target and a bar, and
    /// no panel at all. What the water is doing is the whole of the interface: the jet
    /// trails the pointer, the nest sways, and the bar fills only while the two agree.
    /// </remarks>
    private void Water(ScreenView view, int width, int height, float unit)
    {
        if (view.Water is not { } water)
        {
            return;
        }

        float span = MathF.Min(width, height) * 0.72f;
        float left = (width - span) / 2f;
        float top = (height - span) / 2f;

        Vector2 nest = new(left + (water.Nest.X * span), top + (water.Nest.Y * span));
        Vector2 jet = new(left + (water.Jet.X * span), top + (water.Jet.Y * span));

        float ring = 14 * unit;

        // The nest: a ring the player is trying to keep the water inside. Lit while the
        // water is on it, because that is the only feedback the puzzle gives.
        Vector4 mark = water.OnTarget ? Accent : Ink;
        Overlay.Rect(nest.X - ring, nest.Y - 1, ring * 2, 2, mark);
        Overlay.Rect(nest.X - 1, nest.Y - ring, 2, ring * 2, mark);

        // The water, as a short fall of drops rather than a cursor: it is coming down out
        // of the air and it is heavy.
        for (int i = 0; i < 5; i++)
        {
            float fall = i * 5 * unit;
            float wide = MathF.Max(1f, (5 - i) * unit);

            Overlay.Rect(jet.X - (wide / 2f), jet.Y - fall, wide, 3 * unit, PanelLit);
        }

        Overlay.Rect(jet.X - (3 * unit), jet.Y - (3 * unit), 6 * unit, 6 * unit, Accent);

        // How long it has been on. Ten seconds is the game's own number.
        float barWide = span * 0.5f;
        float barLeft = (width - barWide) / 2f;
        float barTop = top + span + (18 * unit);
        float barTall = 10 * unit;

        Overlay.Rect(barLeft, barTop, barWide, barTall, Panel);
        Overlay.Rect(barLeft, barTop, barWide * water.Progress, barTall, Accent);
        Overlay.Rect(barLeft, barTop, barWide, 1, Ink);

        Overlay.Text(
            water.Progress >= 1f
                ? "The nest gives way."
                : water.OnTarget
                    ? "Hold it there."
                    : "The water goes wide.",
            barLeft,
            barTop + barTall + (6 * unit),
            Ink);

        // A way out, drawn rather than assumed. Every other screen here has one and the
        // rule in docs/screens.md is that an interface owes the player one; without it this
        // is a modal panel with a reticle on it and no visible way back to the street.
        const string Away = "Put the hose down";

        float away = Overlay.Measure(Away) + (24 * unit);
        var button = new Vector4(
            barLeft + barWide - away,
            barTop + barTall + Overlay.LineHeight + (12 * unit),
            away,
            Overlay.LineHeight + (12 * unit));

        Overlay.Rect(button.X, button.Y, button.Z, button.W, PanelLit);
        Overlay.Rect(button.X, button.Y, button.Z, 1, Accent);
        Overlay.Text(Away, button.X + (12 * unit), button.Y + (6 * unit), Accent);

        _hits.Add(("water:away", button));
    }

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
    /// One thing, close up: the picture the artists painted of it, and what can be done to it.
    /// </summary>
    /// <param name="view">What to draw.</param>
    /// <param name="width">Window width in pixels.</param>
    /// <param name="height">Window height.</param>
    /// <param name="unit">The interface's scale.</param>
    /// <remarks>
    /// <para>
    /// <b>The picture is the point.</b> Every item has two pictures in the archives: a
    /// 94-pixel square for lists, and a <c>6</c> that is the thing itself painted at the
    /// size it is meant to be looked at — the book of the immortals is 606 by 314 and its
    /// two pages are meant to be read. Drawing the list square here made a close-up that
    /// showed nothing, and made every multi-page document in the game illegible.
    /// </para>
    /// <para>
    /// <b>The verbs are the other point.</b> An item's own actions — look at it, think
    /// about it, read it, scan it into Sidney — are written in <c>INV_ALL.NVC</c> and
    /// every one of them is guarded by <c>ALL_INV</c>, which asks whether the inventory is
    /// on top. So this is the only place they can be reached.
    /// </para>
    /// <para>
    /// <b>Turning a page is a verb, so it is drawn as one — beside the page.</b> The book,
    /// the church pamphlet and the panels of Le Serpent Rouge all page through each other
    /// with <c>TURN_LEFT</c> and <c>TURN_RIGHT</c>, whose scripts un-inspect one item and
    /// inspect the next. Left in the row of verbs they read as two more things to do to a
    /// book; pulled out to arrows either side of it they read as what they are.
    /// <c>INSPECT_UNDO</c> goes the other way and is dropped: it is the way out, and the
    /// way out is already in the same corner it occupies on every other screen.
    /// </para>
    /// <para>
    /// The whole window rather than a panel in the middle of it. Reported: a close-up
    /// drawn as a card read as a modal error box rather than as looking at something, and
    /// carried a line telling the player to right-click in the room — which is a thing
    /// this screen has never let them do, because it takes the click itself.
    /// </para>
    /// </remarks>
    private void Inspect(ScreenView view, int width, int height, float unit)
    {
        string subject = view.Screen.Subject ?? view.Held ?? string.Empty;

        // Not a dim over the room. The player is looking at one thing, and this is the one
        // screen in the interface where what is behind it is nothing but a distraction.
        Overlay.Rect(0, 0, width, height, Backdrop);

        float margin = 28f * unit;
        float row = Overlay.LineHeight + (14 * unit);

        // The name, and the way out, in the corners they occupy on every other screen.
        Overlay.Text(Pretty(subject), margin, margin, Accent);

        const string close = "CLOSE";
        float closeWidth = Overlay.Measure(close) + (20 * unit);
        var closeAt = new Vector4(
            width - margin - closeWidth, margin - (6 * unit), closeWidth, row);

        Overlay.Rect(closeAt.X, closeAt.Y, closeAt.Z, closeAt.W, PanelLit);
        Overlay.Text(close, closeAt.X + (10 * unit), margin, Ink);
        _hits.Add(("close", closeAt));

        IReadOnlyList<string> offered = view.Verbs ?? [];

        // What can be done to it, minus the two kinds of verb this screen draws itself.
        List<string> verbs = [.. offered.Where(v => !IsPaging(v) && !IsTheWayOut(v))];

        bool back = offered.Any(v => v.Equals(TurnLeft, StringComparison.OrdinalIgnoreCase));
        bool forward = offered.Any(v => v.Equals(TurnRight, StringComparison.OrdinalIgnoreCase));

        float top = margin + row + (10 * unit);
        float bottom = height - margin - (verbs.Count > 0 ? row + (12 * unit) : 0);

        // The arrows take a gutter either side, so the page is never drawn under one.
        float gutter = back || forward ? 56 * unit : 0;

        var frame = new Vector4(
            margin + gutter,
            top,
            MathF.Max(row, width - ((margin + gutter) * 2)),
            MathF.Max(row, bottom - top));

        // The close-up where the item has one; the list picture where it has not, which is
        // most of what a player carries and is still better than a bare name.
        ItemIcon art = view.CloseUps?.Invoke(subject) ?? default;

        if (!art.Drawn)
        {
            art = view.Icons?.Invoke(subject) ?? default;
        }

        if (art.Drawn)
        {
            Vector4 at = Fitted(art, frame);

            Overlay.Picture(art.Picture, at.X, at.Y, at.Z, at.W, Vector4.One);
        }
        else
        {
            Overlay.Text(
                "Nobody painted a picture of this one.",
                frame.X,
                frame.Y + ((frame.W - Overlay.LineHeight) / 2),
                Dim);
        }

        // Either side of the page, level with the middle of it, and only where the item's
        // own rules offer the verb: page one of a two-page book has a right arrow, no left.
        if (back)
        {
            Arrow(TurnLeft, "<", margin, frame, unit);
        }

        if (forward)
        {
            Arrow(TurnRight, ">", width - margin - (44 * unit), frame, unit);
        }

        if (verbs.Count == 0)
        {
            return;
        }

        // A row along the foot rather than a column down the side: the picture wants the
        // middle of the window, and three or four words fit across it comfortably.
        float y = height - margin - row;
        float spacing = 8 * unit;
        float total = 0;

        List<float> widths = [];

        foreach (string verb in verbs)
        {
            float button = Overlay.Measure(Pretty(verb)) + (24 * unit) +
                (view.VerbIcons?.Invoke(verb, false).Drawn == true ? row : 0);

            widths.Add(button);
            total += button + spacing;
        }

        float x = MathF.Max(margin, (width - (total - spacing)) / 2f);

        for (int i = 0; i < verbs.Count; i++)
        {
            if (x + widths[i] > width - margin)
            {
                break;
            }

            var bounds = new Vector4(x, y, widths[i], row);
            bool over = Inside(_pointer, bounds);

            Overlay.Rect(bounds.X, bounds.Y, bounds.Z, bounds.W, over ? PanelLit : Panel);
            Overlay.Rect(bounds.X, bounds.Y, bounds.Z, 1, Rule);

            float text = bounds.X + (12 * unit);

            if (view.VerbIcons?.Invoke(verbs[i], over) is { Drawn: true } picture)
            {
                Vector4 into = picture.Fit(
                    bounds.X + (4 * unit), bounds.Y + (2 * unit), row - (4 * unit));

                Overlay.Picture(picture.Picture, into.X, into.Y, into.Z, into.W, Vector4.One);
                text += row - (8 * unit);
            }

            Overlay.Text(Pretty(verbs[i]), text, y + (7 * unit), over ? Accent : Ink);

            _hits.Add(("verb:" + verbs[i], bounds));
            x += widths[i] + spacing;
        }
    }

    /// <summary>What the room is covered with while one thing is held up to the light.</summary>
    private static readonly Vector4 Backdrop = new(0.03f, 0.03f, 0.04f, 0.98f);

    /// <summary>The verb that goes back a page.</summary>
    private const string TurnLeft = "TURN_LEFT";

    /// <summary>The verb that goes on a page.</summary>
    private const string TurnRight = "TURN_RIGHT";

    /// <summary>Whether a verb turns a page rather than doing something to the thing.</summary>
    private static bool IsPaging(string verb) =>
        verb.Equals(TurnLeft, StringComparison.OrdinalIgnoreCase) ||
        verb.Equals(TurnRight, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a verb only closes the close-up, which the chrome already does.</summary>
    private static bool IsTheWayOut(string verb) =>
        verb.Equals("INSPECT_UNDO", StringComparison.OrdinalIgnoreCase) ||
        verb.Equals("INSPECT", StringComparison.OrdinalIgnoreCase);

    /// <summary>One of the page arrows either side of a document.</summary>
    private void Arrow(string verb, string mark, float x, Vector4 frame, float unit)
    {
        float side = 44 * unit;
        var bounds = new Vector4(x, frame.Y + ((frame.W - side) / 2f), side, side);

        bool over = Inside(_pointer, bounds);

        Overlay.Rect(bounds.X, bounds.Y, bounds.Z, bounds.W, over ? PanelLit : Panel);
        Overlay.Text(
            mark,
            bounds.X + ((side - Overlay.Measure(mark)) / 2f),
            bounds.Y + ((side - Overlay.LineHeight) / 2f),
            over ? Accent : Ink);

        _hits.Add(("verb:" + verb, bounds));
    }

    /// <summary>Where a picture goes to fill a rectangle without changing shape.</summary>
    /// <param name="art">The picture and the shape it was painted at.</param>
    /// <param name="frame">The rectangle to fill.</param>
    /// <returns>Where to draw it, centred in the frame.</returns>
    /// <remarks>
    /// Unlike <see cref="ItemIcon.Fit"/>, which fits a square and never grows a picture:
    /// this one grows it. The close-ups were painted for a 640-pixel screen, and drawn at
    /// their own size on a modern one they are a postage stamp in the middle of it — which
    /// is exactly the complaint this screen exists to answer.
    /// </remarks>
    private static Vector4 Fitted(ItemIcon art, Vector4 frame)
    {
        float scale = MathF.Min(frame.Z / art.Width, frame.W / art.Height);
        float wide = art.Width * scale;
        float tall = art.Height * scale;

        return new Vector4(
            MathF.Round(frame.X + ((frame.Z - wide) / 2f)),
            MathF.Round(frame.Y + ((frame.W - tall) / 2f)),
            MathF.Round(wide),
            MathF.Round(tall));
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

    /// <summary>Sidney, whichever of its programs is showing.</summary>
    /// <remarks>
    /// Drawn by <see cref="Sidney.SidneyView"/>, which owns the laptop, the desktop and the eight
    /// programs on it. It is not a panel over the room like the screens around it — Sidney
    /// is a thing in Grace's hands — so it takes the window rather than the body rectangle
    /// this painter hands everything else.
    /// </remarks>
    private void Sidney(ScreenView view, int width, int height, float unit) =>
        _sidney.Draw(Overlay, _hits, view, width, height, _pointer, unit);

    /// <summary>Where the map was drawn last, so a click can be turned into a place.</summary>
    public Vector4 MapBounds => _sidney.MapBounds;

    /// <summary>
    /// Turns a point on the glass into a place on Sidney's map.
    /// </summary>
    /// <param name="at">Where the pointer is, in window pixels.</param>
    /// <returns>The place, in the map's own 1,368 pixels.</returns>
    /// <remarks>
    /// Through whatever the map was last drawn at, so it means the same place whether the
    /// view is at rest or zoomed into a corner of the country.
    /// </remarks>
    public Vector2 MapAt(Vector2 at)
    {
        Vector4 bounds = MapBounds;

        if (bounds.Z <= 0)
        {
            return Vector2.Zero;
        }

        float across = UI.Sidney.SidneyMapView.Shown / bounds.Z;

        return UI.Sidney.SidneyMapView.Origin +
            new Vector2((at.X - bounds.X) * across, (at.Y - bounds.Y) * across);
    }

    /// <summary>
    /// Turns the wheel over whichever of Sidney's lists the pointer is on.
    /// </summary>
    /// <param name="at">Where the pointer is.</param>
    /// <param name="notches">How far the wheel turned.</param>
    /// <returns>True when something scrolled.</returns>
    public bool SidneyWheel(Vector2 at, float notches) =>
        _sidney.Wheel(at, notches, Overlay.LineHeight * 3f);


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
