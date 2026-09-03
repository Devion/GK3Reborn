// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Numerics;
using GK3Reborn.Rendering;

namespace GK3Reborn.UI.Sidney;

/// <summary>
/// The colours the machine draws itself in.
/// </summary>
/// <remarks>
/// Amber on a warm black, which is what the original's screen is and what every piece of
/// its art was drawn to sit on. The neutrals are warm for the same reason: a cold grey
/// panel over the gold crest reads as a dialog box from another program.
/// </remarks>
public static class SidneyPalette
{
    /// <summary>The screen itself, behind everything.</summary>
    public static readonly Vector4 Screen = new(0.035f, 0.031f, 0.028f, 1f);

    /// <summary>Ordinary text.</summary>
    public static readonly Vector4 Ink = new(0.92f, 0.89f, 0.82f, 1f);

    /// <summary>Text that is there for reference rather than to be read.</summary>
    public static readonly Vector4 Dim = new(0.60f, 0.56f, 0.48f, 1f);

    /// <summary>The machine's own colour: headings, selection, anything live.</summary>
    public static readonly Vector4 Amber = new(0.96f, 0.71f, 0.26f, 1f);

    /// <summary>The same, banked down, for a border that should not shout.</summary>
    public static readonly Vector4 AmberDim = new(0.52f, 0.38f, 0.15f, 1f);

    /// <summary>A raised surface — a window, a button at rest.</summary>
    public static readonly Vector4 Panel = new(0.085f, 0.076f, 0.066f, 0.97f);

    /// <summary>The same, picked out: hovered, selected, open.</summary>
    public static readonly Vector4 PanelLit = new(0.17f, 0.15f, 0.12f, 0.98f);

    /// <summary>A divider.</summary>
    public static readonly Vector4 Rule = new(0.29f, 0.25f, 0.19f, 0.9f);

    /// <summary>Something confirmed: a locked shape, a matched print.</summary>
    public static readonly Vector4 Good = new(0.45f, 0.90f, 0.55f, 1f);

    /// <summary>Something that wants attention: unread mail, a refusal.</summary>
    public static readonly Vector4 Alert = new(0.95f, 0.48f, 0.36f, 1f);

    /// <summary>The taskbar and the window furniture, which sit under the content.</summary>
    public static readonly Vector4 Bar = new(0.055f, 0.050f, 0.045f, 1f);

    /// <summary>
    /// What is drawn on the map, which is not a screen but a photograph.
    /// </summary>
    /// <remarks>
    /// Amber on black is right for a screen and wrong for a survey map: the original marks
    /// its places in solid blue and draws the figures it finds in the same blue over pale
    /// green country, because that is what reads on it. A mark in the interface's own amber
    /// on that map is a fleck of the same colour as the contour shading.
    /// </remarks>
    public static readonly Vector4 Mark = new(0.09f, 0.12f, 0.82f, 1f);

    /// <summary>The figure laid over the country, in the same blue.</summary>
    public static readonly Vector4 Figure = new(0.11f, 0.15f, 0.86f, 1f);

    /// <summary>What the analysis itself found, which the original draws in black.</summary>
    public static readonly Vector4 Finding = new(0.04f, 0.04f, 0.05f, 1f);

    /// <summary>A pale ring around a mark, so that it reads on dark ground as well.</summary>
    public static readonly Vector4 Halo = new(1f, 1f, 1f, 0.92f);

    /// <summary>
    /// A figure the machine has confirmed passes through every mark.
    /// </summary>
    /// <remarks>
    /// Deeper than the green the rest of the interface confirms things in, because the
    /// country under it is pale green and a light green line on it is the one thing on the
    /// map that cannot be seen.
    /// </remarks>
    public static readonly Vector4 Confirmed = new(0.02f, 0.55f, 0.12f, 1f);
}

/// <summary>
/// A place to draw Sidney, and where a click in it lands.
/// </summary>
/// <remarks>
/// <para>
/// Sidney is eight screens, a desktop, a mail client and a map, and drawing all of it as
/// private methods on the painter that also draws the inventory and the binoculars made one
/// file nobody could hold in their head. This is the surface those screens draw on: the
/// overlay, the pointer, the scale, the game's own art by file name, and the handful of
/// shapes that are not rectangles.
/// </para>
/// <para>
/// The hit list belongs to the painter rather than to this, because a click is answered
/// against every screen's rectangles at once and Sidney is one screen among them.
/// </para>
/// </remarks>
public sealed class SidneySurface
{
    private readonly List<(string Id, Vector4 Bounds)> _hits;
    private readonly Func<string, ItemIcon>? _artwork;
    private readonly SidneyScrolls _scrolls;

    /// <summary>Creates a surface.</summary>
    /// <param name="overlay">Where it draws.</param>
    /// <param name="hits">The painter's hit list, which this adds to.</param>
    /// <param name="artwork">The game's own art by file name, or null when there is none.</param>
    /// <param name="mouse">Where the mouse is.</param>
    /// <param name="unit">How much bigger than the letters everything else is.</param>
    /// <param name="scrolls">Where each list had been scrolled to.</param>
    public SidneySurface(
        Overlay overlay,
        List<(string Id, Vector4 Bounds)> hits,
        Func<string, ItemIcon>? artwork,
        Vector2 mouse,
        float unit,
        SidneyScrolls scrolls)
    {
        ArgumentNullException.ThrowIfNull(overlay);
        ArgumentNullException.ThrowIfNull(hits);
        ArgumentNullException.ThrowIfNull(scrolls);

        Overlay = overlay;
        _hits = hits;
        _artwork = artwork;
        Mouse = mouse;
        Unit = unit;
        _scrolls = scrolls;
    }

    /// <summary>
    /// Every region drawn this frame that scrolls, and where it is.
    /// </summary>
    /// <remarks>
    /// So that the wheel can find the one under the pointer. It cannot be answered from the
    /// hit list: the rows inside a list are registered over it and are what a click means,
    /// and the wheel wants the list rather than the row.
    /// </remarks>
    public List<(string Id, Vector4 Bounds)> Scrollables { get; } = [];

    /// <summary>Where it draws.</summary>
    public Overlay Overlay { get; }

    /// <summary>How much bigger than the letters everything else is.</summary>
    public float Unit { get; }

    /// <summary>Where the mouse is.</summary>
    public Vector2 Mouse { get; }

    /// <summary>The height of a line of text.</summary>
    public float Line => Overlay.LineHeight;

    /// <summary>A number of interface units, in pixels.</summary>
    /// <param name="units">How many.</param>
    /// <returns>The pixels.</returns>
    public float Em(float units) => units * Unit;

    /// <summary>One of the game's own bitmaps, by file name.</summary>
    /// <param name="file">Its name with extension, as the archives spell it.</param>
    /// <returns>The picture, which may be nothing.</returns>
    public ItemIcon Art(string file) => _artwork?.Invoke(file) ?? default;

    /// <summary>Fills a rectangle.</summary>
    /// <param name="bounds">Where, as x, y, width, height.</param>
    /// <param name="colour">What colour.</param>
    public void Fill(Vector4 bounds, Vector4 colour) =>
        Overlay.Rect(bounds.X, bounds.Y, bounds.Z, bounds.W, colour);

    /// <summary>Fills a rectangle.</summary>
    /// <param name="x">Left.</param>
    /// <param name="y">Top.</param>
    /// <param name="width">Across.</param>
    /// <param name="height">Down.</param>
    /// <param name="colour">What colour.</param>
    public void Fill(float x, float y, float width, float height, Vector4 colour) =>
        Overlay.Rect(x, y, width, height, colour);

    /// <summary>Draws a one-pixel outline around a rectangle.</summary>
    /// <param name="bounds">What to outline.</param>
    /// <param name="colour">What colour.</param>
    /// <param name="thickness">How thick, in pixels.</param>
    public void Frame(Vector4 bounds, Vector4 colour, float thickness = 1f)
    {
        Overlay.Rect(bounds.X, bounds.Y, bounds.Z, thickness, colour);
        Overlay.Rect(bounds.X, bounds.Y + bounds.W - thickness, bounds.Z, thickness, colour);
        Overlay.Rect(bounds.X, bounds.Y, thickness, bounds.W, colour);
        Overlay.Rect(bounds.X + bounds.Z - thickness, bounds.Y, thickness, bounds.W, colour);
    }

    /// <summary>Writes a line of text.</summary>
    /// <param name="text">What to write.</param>
    /// <param name="x">Left of the first character.</param>
    /// <param name="y">Top of the line.</param>
    /// <param name="colour">What colour.</param>
    /// <returns>Where the next character would start.</returns>
    public float Write(string text, float x, float y, Vector4 colour) =>
        Overlay.Text(text, x, y, colour);

    /// <summary>
    /// Writes a line of text, shortened with an ellipsis if it will not fit.
    /// </summary>
    /// <param name="text">What to write.</param>
    /// <param name="x">Left of the first character.</param>
    /// <param name="y">Top of the line.</param>
    /// <param name="width">How much room it has.</param>
    /// <param name="colour">What colour.</param>
    /// <remarks>
    /// Every list here is one column of a two-column screen, and a suspect with a long name
    /// or a mail with a long subject would otherwise run under the scrollbar and out the
    /// side. The clip would cut it mid-letter, which reads as a rendering fault rather than
    /// as a name that did not fit.
    /// </remarks>
    public void WriteIn(string text, float x, float y, float width, Vector4 colour)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (Measure(text) <= width)
        {
            Write(text, x, y, colour);

            return;
        }

        float room = width - Measure("...");

        for (int i = text.Length - 1; i > 0; i--)
        {
            if (Measure(text[..i]) <= room)
            {
                Write(text[..i].TrimEnd() + "...", x, y, colour);

                return;
            }
        }
    }

    /// <summary>How wide a line of text is.</summary>
    /// <param name="text">The line.</param>
    /// <returns>Its width in pixels.</returns>
    public int Measure(string text) => Overlay.Measure(text);

    /// <summary>Draws one of the game's pictures into a rectangle.</summary>
    /// <param name="art">The picture.</param>
    /// <param name="bounds">Where to put it.</param>
    /// <param name="tint">What to multiply it by.</param>
    public void Draw(ItemIcon art, Vector4 bounds, Vector4? tint = null)
    {
        if (art.Drawn && bounds.Z > 0 && bounds.W > 0)
        {
            Overlay.Picture(
                art.Picture, bounds.X, bounds.Y, bounds.Z, bounds.W, tint ?? Vector4.One);
        }
    }

    /// <summary>Says that a click in a rectangle means something.</summary>
    /// <param name="id">What it means.</param>
    /// <param name="bounds">Where.</param>
    public void Hit(string id, Vector4 bounds) => _hits.Add((id, bounds));

    /// <summary>Whether the pointer is inside a rectangle.</summary>
    /// <param name="bounds">Which one.</param>
    /// <returns>True when it is.</returns>
    public bool Over(Vector4 bounds) =>
        Mouse.X >= bounds.X && Mouse.X <= bounds.X + bounds.Z &&
        Mouse.Y >= bounds.Y && Mouse.Y <= bounds.Y + bounds.W;

    /// <summary>
    /// Draws a button and says whether the pointer is on it.
    /// </summary>
    /// <param name="id">What clicking it means.</param>
    /// <param name="bounds">Where it is.</param>
    /// <param name="label">What it says.</param>
    /// <param name="on">Whether it is the one already chosen.</param>
    /// <returns>True when the pointer is over it.</returns>
    public bool Button(string id, Vector4 bounds, string label, bool on = false)
    {
        ArgumentNullException.ThrowIfNull(label);

        bool over = Over(bounds);

        Fill(bounds, on || over ? SidneyPalette.PanelLit : SidneyPalette.Panel);
        Frame(bounds, on ? SidneyPalette.Amber : over ? SidneyPalette.AmberDim : SidneyPalette.Rule);

        Write(
            label,
            bounds.X + ((bounds.Z - Measure(label)) / 2),
            bounds.Y + ((bounds.W - Line) / 2),
            on || over ? SidneyPalette.Amber : SidneyPalette.Ink);

        Hit(id, bounds);

        return over;
    }

    /// <summary>Draws a straight line between two points.</summary>
    /// <param name="ax">First point's x.</param>
    /// <param name="ay">First point's y.</param>
    /// <param name="bx">Second point's x.</param>
    /// <param name="by">Second point's y.</param>
    /// <param name="colour">What colour.</param>
    /// <param name="thickness">How thick, in pixels.</param>
    /// <remarks>
    /// <para>
    /// One rectangle per row for a steep line and one per column for a shallow one, rather
    /// than one per pixel along it. The overlay draws rectangles and a rectangle covering a
    /// diagonal's bounding box is a filled block, so a diagonal has to be broken up — but it
    /// only has to be broken up along the axis it moves fastest in.
    /// </para>
    /// <para>
    /// <b>The count matters.</b> The display list is capped, and drawing a line a pixel at a
    /// time is how an interface of a few hundred shapes becomes one of a few thousand
    /// rectangles — which on a large window pushed the taskbar off the end of the list and
    /// out of the picture.
    /// </para>
    /// </remarks>
    public void Stroke(float ax, float ay, float bx, float by, Vector4 colour, float thickness = 1f)
    {
        float dx = bx - ax;
        float dy = by - ay;
        float thick = MathF.Max(1, thickness);

        // Axis-aligned, which most of them are, is one rectangle.
        if (MathF.Abs(dy) < 1)
        {
            Overlay.Rect(
                MathF.Min(ax, bx), MathF.Round(ay), MathF.Abs(dx) + thick, thick, colour);

            return;
        }

        if (MathF.Abs(dx) < 1)
        {
            Overlay.Rect(
                MathF.Round(ax), MathF.Min(ay, by), thick, MathF.Abs(dy) + thick, colour);

            return;
        }

        // Walked in increasing order along the axis it moves fastest in, which means the
        // ends may need swapping — and swapping them without swapping the slope with them
        // sends the line off in the wrong direction from the wrong corner. That drew the
        // envelope's two flap lines as one diagonal across the whole icon.
        if (MathF.Abs(dy) >= MathF.Abs(dx))
        {
            float fromY = MathF.Min(ay, by);
            float toY = MathF.Max(ay, by);
            float x = ay <= by ? ax : bx;
            float step = (ay <= by ? dx : -dx) / MathF.Abs(dy);
            float span = MathF.Max(thick, MathF.Abs(step) + 1);

            for (float y = fromY; y <= toY; y++)
            {
                Overlay.Rect(MathF.Round(x), MathF.Round(y), span, thick, colour);

                x += step;
            }

            return;
        }

        float fromX = MathF.Min(ax, bx);
        float toX = MathF.Max(ax, bx);
        float at = ax <= bx ? ay : by;
        float rise = (ax <= bx ? dy : -dy) / MathF.Abs(dx);
        float tall = MathF.Max(thick, MathF.Abs(rise) + 1);

        for (float x = fromX; x <= toX; x++)
        {
            Overlay.Rect(MathF.Round(x), MathF.Round(at), thick, tall, colour);

            at += rise;
        }
    }

    /// <summary>Draws a circle's outline.</summary>
    /// <param name="x">Centre's x.</param>
    /// <param name="y">Centre's y.</param>
    /// <param name="radius">How big.</param>
    /// <param name="colour">What colour.</param>
    /// <param name="thickness">How thick the line is, in pixels.</param>
    public void Ring(float x, float y, float radius, Vector4 colour, float thickness = 1f) =>
        Arc(x, y, radius, 0, MathF.Tau, colour, thickness);

    /// <summary>
    /// Draws part of a circle, from one angle to another.
    /// </summary>
    /// <param name="x">Centre's x.</param>
    /// <param name="y">Centre's y.</param>
    /// <param name="radius">How big.</param>
    /// <param name="from">Where to start, in radians, zero being to the right.</param>
    /// <param name="to">Where to stop.</param>
    /// <param name="colour">What colour.</param>
    /// <param name="thickness">How thick the line is, in pixels.</param>
    /// <remarks>
    /// One rectangle a step, each big enough to close the gap to the next, rather than one
    /// rectangle per pixel of arc for every pixel of thickness. A thick ring drawn the
    /// second way costs its circumference times its thickness in rectangles, which for the
    /// four ridges of the fingerprint icon on a large window was some thousands of them.
    /// </remarks>
    public void Arc(
        float x,
        float y,
        float radius,
        float from,
        float to,
        Vector4 colour,
        float thickness = 1f)
    {
        // Nothing sane comes of an arc with no size, and a radius that is not a number at
        // all is what three marked places in a straight line make of a circle through them.
        if (!float.IsFinite(radius) || radius <= 0 || radius > 100000f)
        {
            return;
        }

        float thick = MathF.Max(1, MathF.Round(thickness));
        float sweep = MathF.Abs(to - from);
        float length = radius * sweep;

        // <b>The rectangle never grows with the arc; the number of them does.</b> Sizing
        // each step to close the gap left by a fixed number of steps means an enormous
        // radius draws enormous squares: a circle fitted through three nearly straight
        // places came out as a few hundred blue blocks across the whole screen. Anything
        // off the edge is thrown away by the clip before it reaches the display list, so
        // the cost of a big circle is bounded by what can actually be seen.
        float side = MathF.Max(thick, 2);
        int steps = Math.Clamp((int)MathF.Ceiling(length / (side * 0.9f)), 12, 8192);

        for (int i = 0; i <= steps; i++)
        {
            float angle = from + ((to - from) * (i / (float)steps));

            Overlay.Rect(
                MathF.Round(x + (MathF.Cos(angle) * radius) - (side / 2)),
                MathF.Round(y + (MathF.Sin(angle) * radius) - (side / 2)),
                side,
                side,
                colour);
        }
    }

    /// <summary>Draws a filled circle.</summary>
    /// <param name="x">Centre's x.</param>
    /// <param name="y">Centre's y.</param>
    /// <param name="radius">How big.</param>
    /// <param name="colour">What colour.</param>
    public void Disc(float x, float y, float radius, Vector4 colour)
    {
        for (float row = -radius; row <= radius; row++)
        {
            float half = MathF.Sqrt(MathF.Max(0, (radius * radius) - (row * row)));

            Overlay.Rect(
                MathF.Round(x - half), MathF.Round(y + row), MathF.Round(half * 2), 1, colour);
        }
    }

    /// <summary>
    /// Fills a closed shape given by its corners.
    /// </summary>
    /// <param name="points">The corners, in order; the last joins back to the first.</param>
    /// <param name="colour">What colour.</param>
    /// <remarks>
    /// <para>
    /// The overlay draws axis-aligned rectangles and nothing else, which is right for an
    /// interface of panels and rules and is why every icon here would otherwise be a box.
    /// A scanline fill turns that one primitive into any shape: for each row of pixels,
    /// find where the outline crosses it, sort the crossings and fill between them in
    /// pairs. Non-zero winding is not worth the extra bookkeeping — nothing drawn here
    /// self-intersects, and even-odd gives holes for free, which is what a ring needs.
    /// </para>
    /// <para>
    /// A row at a time rather than a pixel at a time, so a filled glyph costs about as many
    /// rectangles as it is tall rather than as many as it has pixels.
    /// </para>
    /// </remarks>
    public void Polygon(ReadOnlySpan<Vector2> points, Vector4 colour)
    {
        if (points.Length < 3)
        {
            return;
        }

        float top = points[0].Y;
        float bottom = points[0].Y;

        foreach (Vector2 point in points)
        {
            top = MathF.Min(top, point.Y);
            bottom = MathF.Max(bottom, point.Y);
        }

        Span<float> crossings = stackalloc float[points.Length];

        for (float y = MathF.Floor(top); y <= MathF.Ceiling(bottom); y++)
        {
            float middle = y + 0.5f;
            int found = 0;

            for (int i = 0; i < points.Length; i++)
            {
                Vector2 a = points[i];
                Vector2 b = points[(i + 1) % points.Length];

                // A horizontal edge crosses no scanline, and counting a vertex twice is
                // what puts a seam down a filled shape.
                if (a.Y == b.Y || middle < MathF.Min(a.Y, b.Y) || middle >= MathF.Max(a.Y, b.Y))
                {
                    continue;
                }

                crossings[found++] = a.X + ((middle - a.Y) / (b.Y - a.Y) * (b.X - a.X));
            }

            if (found < 2)
            {
                continue;
            }

            crossings[..found].Sort();

            for (int i = 0; i + 1 < found; i += 2)
            {
                float left = MathF.Round(crossings[i]);
                float width = MathF.Round(crossings[i + 1]) - left;

                if (width > 0)
                {
                    Overlay.Rect(left, y, width, 1, colour);
                }
            }
        }
    }

    /// <summary>
    /// Confines drawing to a region, scrolls it, and draws its bar.
    /// </summary>
    /// <param name="id">What the region is called, so the wheel can find it again.</param>
    /// <param name="region">Where it is on the screen.</param>
    /// <param name="content">How tall its content is in full.</param>
    /// <returns>How far to shift the content up, which is never more than it has to be.</returns>
    /// <remarks>
    /// <para>
    /// <b>Every list in Sidney goes through this.</b> The first pass drew lists until they
    /// reached the bottom of the panel and then stopped, which quietly dropped the tenth
    /// suspect at ordinary window sizes — and with him the only way to link the print that
    /// names him. A list that cannot be reached is worse than one that is ugly.
    /// </para>
    /// <para>
    /// The offset is clamped whether the bar is needed or not, so a list that shrinks — a
    /// file un-linked, a search that found less — cannot stay scrolled past its own end and
    /// show an empty page.
    /// </para>
    /// <para>
    /// Call <see cref="EndScroll"/> once the content has been drawn.
    /// </para>
    /// </remarks>
    public float BeginScroll(string id, Vector4 region, float content)
    {
        ArgumentNullException.ThrowIfNull(id);

        float hidden = MathF.Max(0, content - region.W);
        float offset = Math.Clamp(_scrolls.Of(id), 0, hidden);

        _scrolls.Set(id, offset);
        Scrollables.Add((id, region));
        Overlay.PushClip(region);

        if (hidden <= 0)
        {
            return 0;
        }

        float width = MathF.Max(4, Em(6));
        var track = new Vector4(region.X + region.Z - width, region.Y, width, region.W);

        Fill(track, SidneyPalette.Bar);

        float span = MathF.Max(Em(16), region.W * (region.W / content));
        float at = region.Y + ((region.W - span) * (offset / hidden));

        Fill(
            new Vector4(track.X + 1, at, width - 2, span),
            Over(region) ? SidneyPalette.Amber : SidneyPalette.AmberDim);

        return offset;
    }

    /// <summary>Ends the region begun by <see cref="BeginScroll"/>.</summary>
    public void EndScroll() => Overlay.PopClip();

    /// <summary>How much width a scrolling region leaves its content.</summary>
    /// <param name="region">The region.</param>
    /// <param name="content">How tall the content is in full.</param>
    /// <returns>The width left once the bar has taken its share, when one is needed.</returns>
    public float Room(Vector4 region, float content) =>
        content > region.W ? region.Z - MathF.Max(4, Em(6)) - Em(4) : region.Z;

    /// <summary>Writes a paragraph inside a width, and says where the next one starts.</summary>
    /// <param name="text">What to write.</param>
    /// <param name="x">Left edge.</param>
    /// <param name="y">Top of the first line.</param>
    /// <param name="width">How wide it may run.</param>
    /// <param name="bottom">Where to stop.</param>
    /// <param name="colour">What colour.</param>
    /// <returns>Where the next paragraph starts.</returns>
    public float Paragraph(
        string text, float x, float y, float width, float bottom, Vector4 colour)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (text.Length == 0)
        {
            return y + Line;
        }

        var line = new System.Text.StringBuilder();

        foreach (string word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string next = line.Length == 0 ? word : line + " " + word;

            if (Measure(next) > width && line.Length > 0)
            {
                if (y > bottom)
                {
                    return y;
                }

                Write(line.ToString(), x, y, colour);
                y += Line;
                line.Clear();
                line.Append(word);

                continue;
            }

            line.Clear();
            line.Append(next);
        }

        if (line.Length > 0 && y <= bottom)
        {
            Write(line.ToString(), x, y, colour);
            y += Line;
        }

        return y;
    }

    /// <summary>How many lines a paragraph would take at a width.</summary>
    /// <param name="text">The paragraph.</param>
    /// <param name="width">How wide it may run.</param>
    /// <returns>The count, at least one.</returns>
    public int Lines(string text, float width)
    {
        ArgumentNullException.ThrowIfNull(text);

        int count = 1;
        var line = new System.Text.StringBuilder();

        foreach (string word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string next = line.Length == 0 ? word : line + " " + word;

            if (Measure(next) > width && line.Length > 0)
            {
                count++;
                line.Clear();
                line.Append(word);

                continue;
            }

            line.Clear();
            line.Append(next);
        }

        return count;
    }
}

/// <summary>
/// How far each of Sidney's lists is scrolled.
/// </summary>
/// <remarks>
/// <para>
/// The screens are drawn from the machine's state every frame and keep nothing of their
/// own, so this is where a scroll position lives. Keyed by region rather than by screen
/// because a screen may have two — the mail list and the message beside it — and they
/// scroll separately.
/// </para>
/// <para>
/// It belongs to the machine rather than to a save: where somebody had scrolled to is not
/// part of the story, and a save that restored it would be restoring the wrong thing.
/// </para>
/// </remarks>
public sealed class SidneyScrolls
{
    private readonly Dictionary<string, float> _offsets = new(StringComparer.Ordinal);

    /// <summary>How far a region is scrolled.</summary>
    /// <param name="id">Which region.</param>
    /// <returns>The offset in pixels.</returns>
    public float Of(string id) => _offsets.GetValueOrDefault(id);

    /// <summary>Puts a region's offset back, after it has been clamped.</summary>
    /// <param name="id">Which region.</param>
    /// <param name="offset">Where it now is.</param>
    public void Set(string id, float offset) => _offsets[id] = offset;

    /// <summary>Moves a region by a number of pixels.</summary>
    /// <param name="id">Which region.</param>
    /// <param name="by">How far, positive to move down the content.</param>
    public void Move(string id, float by)
    {
        ArgumentNullException.ThrowIfNull(id);

        _offsets[id] = MathF.Max(0, Of(id) + by);
    }

    /// <summary>Forgets every offset, for a screen that has just been opened.</summary>
    public void Clear() => _offsets.Clear();
}