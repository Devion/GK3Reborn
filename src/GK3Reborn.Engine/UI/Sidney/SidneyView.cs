// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Numerics;
using GK3Reborn.Game.Sidney;
using GK3Reborn.Rendering;

namespace GK3Reborn.UI.Sidney;

/// <summary>
/// The whole of Sidney, drawn: the laptop, its desktop, and whichever of its programs is open.
/// </summary>
/// <remarks>
/// <para>
/// <b>A desktop rather than a menu.</b> The original's front screen is a row of eight amber
/// buttons along the top of the display and a crest below them; this is the same eight
/// programs as icons on a desktop, with a taskbar under them, because that is what somebody
/// looking at a laptop in 1998 — or now — expects to be able to work out without being
/// told. The way out is a power button rather than the word EXIT, for the same reason.
/// </para>
/// <para>
/// Everything is laid out against the screen the laptop's own art frames, so it scales with
/// the window and never changes shape. Every list is inside a scrolling region, so nothing
/// the machine holds can be off the bottom of the glass with no way to reach it.
/// </para>
/// </remarks>
public sealed class SidneyView
{
    private readonly SidneyScrolls _scrolls = new();

    private List<(string Id, Vector4 Bounds)> _regions = [];
    private SidneyMachine? _machine;

    /// <summary>Where the map was drawn last, so a click can be turned into a place.</summary>
    public Vector4 MapBounds { get; private set; }

    /// <summary>
    /// Turns the wheel over whichever of Sidney's lists the pointer is on.
    /// </summary>
    /// <param name="at">Where the pointer is.</param>
    /// <param name="notches">How far the wheel turned, away from the player being positive.</param>
    /// <param name="step">How far one notch should move a list, in pixels.</param>
    /// <returns>True when something scrolled.</returns>
    /// <remarks>
    /// The innermost region under the pointer, which is the last one registered: a list
    /// inside a window is drawn after the window and is what the wheel should reach.
    /// </remarks>
    public bool Wheel(Vector2 at, float notches, float step)
    {
        // Over the map the wheel means "look closer", which is the one place in Sidney
        // where a list is not what is under the pointer.
        if (_machine is { Screen: SidneyScreen.Analyze } machine &&
            machine.Open?.Kind == SidneyKind.Map &&
            MapBounds is { Z: > 0 } map &&
            at.X >= map.X && at.X <= map.X + map.Z &&
            at.Y >= map.Y && at.Y <= map.Y + map.W)
        {
            float across = SidneyMapView.Shown / map.Z;

            machine.ZoomOn(
                SidneyMapView.Origin + new Vector2((at.X - map.X) * across, (at.Y - map.Y) * across),
                notches);

            return true;
        }

        for (int i = _regions.Count - 1; i >= 0; i--)
        {
            (string id, Vector4 bounds) = _regions[i];

            if (at.X >= bounds.X && at.X <= bounds.X + bounds.Z &&
                at.Y >= bounds.Y && at.Y <= bounds.Y + bounds.W)
            {
                _scrolls.Move(id, -notches * step);

                return true;
            }
        }

        return false;
    }

    /// <summary>Draws Sidney over the room.</summary>
    /// <param name="overlay">Where to draw.</param>
    /// <param name="hits">The painter's hit list.</param>
    /// <param name="view">What the screens know about the game.</param>
    /// <param name="width">The window's width in pixels.</param>
    /// <param name="height">Its height.</param>
    /// <param name="mouse">Where the pointer is.</param>
    /// <param name="unit">How much bigger than the letters everything else is.</param>
    public void Draw(
        Overlay overlay,
        List<(string Id, Vector4 Bounds)> hits,
        ScreenView view,
        int width,
        int height,
        Vector2 mouse,
        float unit)
    {
        ArgumentNullException.ThrowIfNull(overlay);

        var surface = new SidneySurface(overlay, hits, view.Artwork, mouse, unit, _scrolls);

        // The room goes dark behind the laptop rather than merely dimmed. Grace is looking
        // at a screen in her hands, and a room at three-quarters brightness behind it says
        // she is still looking at the room.
        surface.Fill(0, 0, width, height, new Vector4(0.01f, 0.01f, 0.012f, 0.88f));

        Vector4 laptop = SidneyLaptop.Fit(width, height);
        Vector4 screen = SidneyLaptop.ScreenOf(laptop);

        SidneyLaptop.DrawShell(surface, laptop);

        if (view.Sidney is not { } machine)
        {
            surface.Fill(screen, SidneyPalette.Screen);
            surface.Write(
                "Sidney is not switched on.",
                screen.X + surface.Em(16),
                screen.Y + surface.Em(16),
                SidneyPalette.Dim);

            _regions = [];

            return;
        }

        MapBounds = default;
        _machine = machine;

        // Nothing any program draws may leave the glass.
        overlay.PushClip(screen);
        surface.Fill(screen, SidneyPalette.Screen);

        // The photograph propped against the machine reaches the bottom left corner of the
        // glass, and a taskbar flush against the bottom edge runs into it. A strip of black
        // is left there instead — which reads as the screen's own border, because that is
        // what it is.
        var glass = new Vector4(
            screen.X, screen.Y, screen.Z, screen.W - MathF.Round(screen.W * 0.035f));

        float barHeight = MathF.Max(surface.Line + surface.Em(9), glass.W * 0.072f);
        var desk = new Vector4(glass.X, glass.Y, glass.Z, glass.W - barHeight);

        if (machine.Screen == SidneyScreen.Main)
        {
            Desktop(surface, machine, desk);
        }
        else
        {
            Vector4 body = Window(surface, machine, desk);

            MapBounds = SidneyApps.Draw(surface, machine, view, body);
        }

        Taskbar(
            surface,
            machine,
            new Vector4(glass.X, glass.Y + glass.W - barHeight, glass.Z, barHeight));

        Notification(surface, machine, desk);

        overlay.PopClip();

        _regions = [.. surface.Scrollables];
    }

    /// <summary>
    /// The desktop: the machine's own wallpaper, and the eight programs as icons on it.
    /// </summary>
    /// <remarks>
    /// Two columns down the left, filling downwards the way a desktop does, which leaves the
    /// Schattenjäger crest the wallpaper is mostly made of visible behind them. The
    /// wallpaper is dimmed because it was drawn to be the whole screen and is now behind
    /// something: at full strength the gold reads as another row of icons.
    /// </remarks>
    private static void Desktop(SidneySurface surface, SidneyMachine machine, Vector4 desk)
    {
        ItemIcon paper = surface.Art("S_MAIN_SCN.BMP");

        if (paper.Drawn)
        {
            surface.Draw(paper, desk, new Vector4(0.34f, 0.34f, 0.34f, 1f));
        }

        IReadOnlyList<(SidneyScreen Screen, string Label)> apps = SidneyApps.Programs(machine);

        // Sized so that four fit down the glass, which puts the eight in two columns and
        // leaves the crest the wallpaper is mostly made of visible beside them.
        float cell = MathF.Min(desk.Z / 6.4f, desk.W / 4.9f);
        float pad = cell * 0.14f;
        int down = Math.Max(1, (int)((desk.W - pad) / (cell + pad)));

        for (int i = 0; i < apps.Count; i++)
        {
            (SidneyScreen screen, string label) = apps[i];

            float x = desk.X + pad + ((i / down) * (cell + pad));
            float y = desk.Y + pad + ((i % down) * (cell + pad));
            var bounds = new Vector4(x, y, cell, cell);

            bool over = surface.Over(bounds);

            if (over)
            {
                surface.Fill(bounds, new Vector4(0.96f, 0.71f, 0.26f, 0.16f));
                surface.Frame(bounds, SidneyPalette.AmberDim);
            }

            // The picture on top and the game's own name plate under it, which is what that
            // art was drawn to be: a caption, not a button.
            float glyph = cell * 0.52f;

            SidneyGlyphs.Draw(
                surface,
                screen,
                new Vector4(x + ((cell - glyph) / 2), y + (cell * 0.10f), glyph, glyph),
                over ? SidneyPalette.Amber : SidneyPalette.Ink);

            Plate(surface, screen, label, bounds, over);

            surface.Hit($"sidney:screen:{screen}", bounds);
        }
    }

    /// <summary>An icon's caption: the game's own name plate where there is one.</summary>
    private static void Plate(
        SidneySurface surface, SidneyScreen screen, string label, Vector4 cell, bool over)
    {
        ItemIcon plate = surface.Art(SidneyApps.PlateOf(screen, over));

        float y = cell.Y + (cell.W * 0.70f);

        if (plate.Drawn)
        {
            // At its own proportions rather than stretched: these are 76 by 13 and a
            // caption squared up to a cell is a smear.
            float across = MathF.Min(cell.Z * 0.94f, plate.Width * surface.Unit * 1.15f);
            float down = across * plate.Height / plate.Width;

            surface.Draw(plate, new Vector4(cell.X + ((cell.Z - across) / 2), y, across, down));

            return;
        }

        surface.Write(
            label,
            cell.X + ((cell.Z - surface.Measure(label)) / 2),
            y,
            over ? SidneyPalette.Amber : SidneyPalette.Ink);
    }

    /// <summary>
    /// The frame around a running program, and the room left inside it.
    /// </summary>
    /// <returns>Where the program itself may draw.</returns>
    private static Vector4 Window(SidneySurface surface, SidneyMachine machine, Vector4 desk)
    {
        float title = surface.Line + surface.Em(10);
        var bar = new Vector4(desk.X, desk.Y, desk.Z, title);

        surface.Fill(desk, SidneyPalette.Screen);
        surface.Fill(bar, SidneyPalette.Bar);
        surface.Fill(bar.X, bar.Y + bar.W - 1, bar.Z, 1, SidneyPalette.AmberDim);

        float glyph = surface.Line;

        SidneyGlyphs.Draw(
            surface,
            machine.Screen,
            new Vector4(bar.X + surface.Em(8), bar.Y + ((title - glyph) / 2), glyph, glyph),
            SidneyPalette.Amber);

        surface.Write(
            SidneyApps.NameOf(machine, machine.Screen),
            bar.X + surface.Em(12) + glyph,
            bar.Y + ((title - surface.Line) / 2),
            SidneyPalette.Amber);

        // The way back to the desktop, where a window's close button is.
        float side = title * 0.72f;
        var close = new Vector4(
            bar.X + bar.Z - side - surface.Em(6), bar.Y + ((title - side) / 2), side, side);

        bool over = surface.Over(close);

        surface.Fill(close, over ? SidneyPalette.Alert : SidneyPalette.Panel);

        float inset = side * 0.30f;

        surface.Stroke(
            close.X + inset,
            close.Y + inset,
            close.X + side - inset,
            close.Y + side - inset,
            over ? SidneyPalette.Screen : SidneyPalette.Ink);

        surface.Stroke(
            close.X + side - inset,
            close.Y + inset,
            close.X + inset,
            close.Y + side - inset,
            over ? SidneyPalette.Screen : SidneyPalette.Ink);

        surface.Hit("sidney:home", close);

        return new Vector4(
            desk.X + surface.Em(8),
            desk.Y + title + surface.Em(8),
            desk.Z - surface.Em(16),
            desk.W - title - surface.Em(16));
    }

    /// <summary>
    /// The bar along the bottom: the way home, what is running, the mail, the clock and the
    /// way out.
    /// </summary>
    private static void Taskbar(SidneySurface surface, SidneyMachine machine, Vector4 bar)
    {
        surface.Fill(bar, SidneyPalette.Bar);
        surface.Fill(bar.X, bar.Y, bar.Z, 1, SidneyPalette.AmberDim);

        float inset = surface.Em(6);
        float row = bar.W - (inset * 2);

        // Home, on the left, which is where a desktop keeps it.
        var home = new Vector4(
            bar.X + inset,
            bar.Y + inset,
            surface.Measure("SIDNEY") + row + surface.Em(10),
            row);

        bool onHome = machine.Screen == SidneyScreen.Main;

        surface.Fill(home, onHome || surface.Over(home) ? SidneyPalette.PanelLit : SidneyPalette.Panel);
        surface.Frame(home, onHome ? SidneyPalette.Amber : SidneyPalette.Rule);

        float mark = row * 0.52f;

        surface.Disc(
            home.X + (row * 0.5f),
            home.Y + (row / 2),
            mark / 2,
            onHome ? SidneyPalette.Amber : SidneyPalette.AmberDim);

        surface.Write(
            "SIDNEY",
            home.X + row,
            home.Y + ((row - surface.Line) / 2),
            onHome ? SidneyPalette.Amber : SidneyPalette.Ink);

        surface.Hit("sidney:home", home);

        // The way out, on the right, and the clock beside it.
        var power = new Vector4(bar.X + bar.Z - inset - row, bar.Y + inset, row, row);
        bool leaving = surface.Over(power);

        surface.Fill(power, leaving ? SidneyPalette.Alert : SidneyPalette.Panel);
        SidneyGlyphs.Power(surface, power, leaving ? SidneyPalette.Screen : SidneyPalette.Ink);
        surface.Hit("close", power);

        string clock = machine.Now;
        float clockAt = power.X - surface.Em(10) - surface.Measure(clock);

        surface.Write(clock, clockAt, bar.Y + ((bar.W - surface.Line) / 2), SidneyPalette.Dim);

        // The mail light, which is the one thing in the bar that is ever news.
        int unread = machine.Unread;

        if (unread > 0)
        {
            var light = new Vector4(clockAt - surface.Em(10) - row, bar.Y + inset, row, row);

            SidneyGlyphs.Mail(surface, light, SidneyPalette.Alert);
            surface.Hit($"sidney:screen:{SidneyScreen.EMail}", light);
        }
    }

    /// <summary>
    /// The mail notification, in the corner, when something has not been read.
    /// </summary>
    /// <remarks>
    /// The original wrote NEW E-MAIL in the top right of its screen and left it there for
    /// ever, because nothing in the port marked a message read. This is the same news said
    /// the way a machine of that decade said it, in the corner it said it in, and it goes
    /// away when the mail is opened.
    /// </remarks>
    private static void Notification(SidneySurface surface, SidneyMachine machine, Vector4 desk)
    {
        int unread = machine.Unread;

        // Only on the desktop. Over a running program it would sit on top of whatever is in
        // the bottom right corner — which on the suspects screen is the button the
        // fingerprint puzzle ends on — and take its clicks. The taskbar's mail light is the
        // indicator everywhere else, and it is out of the way by construction.
        if (unread <= 0 || machine.Screen != SidneyScreen.Main)
        {
            return;
        }

        string said = unread == 1 ? "You've got mail" : $"You've got mail  ({unread})";

        float height = surface.Line + surface.Em(18);
        float width = surface.Measure(said) + height + surface.Em(24);
        var card = new Vector4(
            desk.X + desk.Z - width - surface.Em(12),
            desk.Y + desk.W - height - surface.Em(12),
            width,
            height);

        bool over = surface.Over(card);

        surface.Fill(card, over ? SidneyPalette.PanelLit : SidneyPalette.Panel);
        surface.Frame(card, SidneyPalette.Alert);

        float glyph = height * 0.58f;

        SidneyGlyphs.Mail(
            surface,
            new Vector4(card.X + surface.Em(8), card.Y + ((height - glyph) / 2), glyph, glyph),
            SidneyPalette.Alert);

        surface.Write(
            said,
            card.X + surface.Em(12) + glyph,
            card.Y + ((height - surface.Line) / 2),
            SidneyPalette.Ink);

        surface.Hit($"sidney:screen:{SidneyScreen.EMail}", card);
    }
}
