// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Numerics;
using GK3Reborn.Game.Sidney;

namespace GK3Reborn.UI.Sidney;

/// <summary>
/// Sidney's map, its marks and whatever has been laid over it.
/// </summary>
/// <remarks>
/// The survey of the Rennes country the whole puzzle is about, with the Paris meridian down
/// it. Clicking marks a place; four places that fall on a circle are what the story is
/// waiting to be told. The picture is drawn at whatever size the window affords and every
/// mark is kept in the map's own 1,368 pixels, so the marks and the hit test cannot drift
/// apart at any window size.
/// </remarks>
public static class SidneyMapView
{
    /// <summary>What the machine's longer notes are broken into lines by.</summary>
    private const char Newline = (char)10;

    /// <summary>Draws the map.</summary>
    /// <param name="surface">Where to draw.</param>
    /// <param name="machine">The machine.</param>
    /// <param name="view">What the screens know about the game, for the picture.</param>
    /// <param name="body">The room it has.</param>
    /// <returns>Where the map was drawn, so a click can be turned into a place.</returns>
    public static Vector4 Draw(
        SidneySurface surface, SidneyMachine machine, ScreenView view, Vector4 body)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(machine);

        int picture = view.Pictures?.Invoke(SidneyMap.Picture) ?? 0;

        // The picture on the left with the figures beside it and the note under those, all
        // inside the room the screen gives it. It used to put the note in the file list's
        // column, which meant that with more than two files scanned it was written straight
        // through their names.
        float column = MathF.Max(surface.Em(60), body.Z * 0.32f);
        float side = MathF.Min(body.Z - column, body.W);

        if (side < surface.Em(40))
        {
            return default;
        }

        float left = body.X;
        float top = body.Y;
        float scale = side / SidneyMap.Extent;
        var bounds = new Vector4(left, top, side, side);

        if (picture > 0)
        {
            surface.Overlay.Picture(picture, left, top, side, side, Vector4.One);
        }
        else
        {
            surface.Fill(bounds, SidneyPalette.PanelLit);
        }

        // <b>Nothing drawn on the map may leave the map.</b> A figure is fitted to places
        // the player chose and no arrangement of them keeps it inside the picture — a circle
        // through three marks near one edge is mostly somewhere else. Without this it was
        // drawn across the rest of Sidney and out over the title bar.
        surface.Overlay.PushClip(bounds);

        if (machine.Map.Grid > 0)
        {
            Rule(surface, machine, left, top, side, scale);
        }

        // <b>The line the analysis found, drawn right across the country.</b> Two places
        // joined is the first step of the whole map puzzle — the sunrise line from the
        // church at Rennes-le-Château to the tower at Blanchefort — and what makes it worth
        // anything is where it goes on past them: the meridian, Arques, the snake. The
        // machine has always recognised the line and never drawn it, so the player was told
        // their two points made one and shown nothing.
        if (machine.Map.Found is { Finding: MapFinding.Line } && machine.Map.Points.Count > 1)
        {
            Vector2 from = machine.Map.Points[0];
            Vector2 to = machine.Map.Points[^1];

            if (Across(from, to, SidneyMap.Extent) is [Vector2 a, Vector2 b])
            {
                surface.Stroke(
                    left + (a.X * scale),
                    top + (a.Y * scale),
                    left + (b.X * scale),
                    top + (b.Y * scale),
                    SidneyPalette.Finding,
                    MathF.Max(2, surface.Em(1.4f)));
            }
        }

        // The circle the analysis found, before the marks so they sit on top of it.
        if (machine.Map.Found is { Finding: MapFinding.Circle } found)
        {
            surface.Ring(
                left + (found.Centre.X * scale),
                top + (found.Centre.Y * scale),
                found.Radius * scale,
                SidneyPalette.Finding,
                MathF.Max(2, surface.Em(1.6f)));
        }

        // Every figure laid over the country, each in the colour that says whether the
        // marks confirm it. More than one at a time is the point: what the books this game
        // is built on do is lay one over another and read the country off where they cross.
        float weight = MathF.Max(2, surface.Em(1.8f));

        foreach (LaidShape laid in machine.Map.Laid)
        {
            Vector4 ink = laid.Locked ? SidneyPalette.Confirmed : SidneyPalette.Figure;

            if (laid.Shape == MapShape.Line)
            {
                float radians = laid.Turn * MathF.PI / 180f;
                var along = new Vector2(MathF.Cos(radians), MathF.Sin(radians)) * laid.Size;

                if (Across(laid.At - along, laid.At + along, SidneyMap.Extent)
                    is [Vector2 one, Vector2 other])
                {
                    surface.Stroke(
                        left + (one.X * scale),
                        top + (one.Y * scale),
                        left + (other.X * scale),
                        top + (other.Y * scale),
                        ink,
                        weight);
                }
            }
            else if (laid.Shape == MapShape.Circle)
            {
                surface.Ring(
                    left + (laid.At.X * scale),
                    top + (laid.At.Y * scale),
                    laid.Size * scale,
                    ink,
                    weight);
            }
            else if (laid.Shape == MapShape.Hexagram)
            {
                // Two triangles, which is how the analysis of Poussin's painting describes
                // finding it, rather than a twelve-sided outline.
                foreach (Vector2[] triangle in SidneyMap.Triangles(laid))
                {
                    Outline(surface, triangle, left, top, scale, ink, weight);
                }
            }
            else
            {
                Outline(surface, SidneyMap.Corners(laid), left, top, scale, ink, weight);
            }
        }

        // The picture is a target only while the map has been armed for it, and says so:
        // a click on it otherwise is a click on a picture, not a village.
        if (machine.Marking)
        {
            surface.Frame(bounds, SidneyPalette.Amber, MathF.Max(1, surface.Em(1.4f)));

            // Before the marks themselves, so that pressing on one of those picks it up
            // rather than putting another one down on top of it.
            surface.Hit("sidney:mark", bounds);
        }

        // A solid dot with a pale ring around it, which is how the original marks a place
        // and the only thing that reads at a glance on a shaded relief map. The cross that
        // was here was two single-pixel lines in the interface's amber: on pale green
        // country at the size the map is drawn, it was the same colour and about the same
        // size as the contour shading it was sitting on.
        float dot = MathF.Max(3, surface.Em(4.5f));

        // Every figure's own places, and then the ones not yet given to a figure. A
        // figure's are drawn a little smaller, because they are settled: what the player is
        // working on is the set that has not been laid over anything yet.
        for (int f = 0; f < machine.Map.Laid.Count; f++)
        {
            LaidShape laid = machine.Map.Laid[f];

            for (int i = 0; i < laid.Points.Count; i++)
            {
                Vector2 point = laid.Points[i];
                float x = left + (point.X * scale);
                float y = top + (point.Y * scale);
                bool held = machine.DraggingFigure == f && machine.Dragging == i;

                surface.Disc(
                    x,
                    y,
                    (dot * 0.85f) + MathF.Max(1, surface.Em(held ? 2.4f : 1f)),
                    held ? SidneyPalette.Amber : SidneyPalette.Halo);

                surface.Disc(x, y, dot * 0.85f, SidneyPalette.Mark);

                surface.Hit(
                    $"sidney:point:{f}:{i}",
                    new Vector4(x - (dot * 2), y - (dot * 2), dot * 4, dot * 4));
            }
        }

        for (int i = 0; i < machine.Map.Points.Count; i++)
        {
            Vector2 point = machine.Map.Points[i];
            float x = left + (point.X * scale);
            float y = top + (point.Y * scale);
            bool held = machine.DraggingFigure < 0 && machine.Dragging == i;

            surface.Disc(
                x,
                y,
                dot + MathF.Max(1, surface.Em(held ? 2.4f : 1.2f)),
                held ? SidneyPalette.Amber : SidneyPalette.Halo);

            surface.Disc(x, y, dot, SidneyPalette.Mark);

            // Registered after the picture, so that pressing on a place picks it up rather
            // than marking another one on top of it.
            surface.Hit(
                $"sidney:point:-1:{i}",
                new Vector4(x - (dot * 2), y - (dot * 2), dot * 4, dot * 4));
        }

        surface.Overlay.PopClip();

        float used = Figures(surface, machine, bounds, column);

        // The rulings the game offers, when one has been asked for.
        if (machine.Ruling)
        {
            float row = surface.Line + surface.Em(10);
            float wide = column - (column * 0.16f);
            float at = bounds.Y + used;
            float x = bounds.X + bounds.Z + (column * 0.08f);

            if (machine.Map.Laid.Count > 0)
            {
                surface.Button(
                    "sidney:fill:shape",
                    new Vector4(x, at, wide, row),
                    machine.Library.Say(
                        machine.RuleInShape ? "GridFillShape" : "GridFillScreen",
                        "Analyze Screen"),
                    machine.RuleInShape);

                at += row + surface.Em(4);
            }

            foreach ((int cells, string label) in machine.Grids)
            {
                surface.Button(
                    $"sidney:grid:{cells}", new Vector4(x, at, wide, row), label);

                at += row + surface.Em(4);
            }

            used = at - bounds.Y + surface.Em(6);
        }

        // What the machine says, in the column beside the picture — and scrolling, because
        // the notes that matter are the long ones: the note about the line through Arques
        // is four times the length of the note about a line through nothing.
        if (machine.Showing is { } note)
        {
            var said = new Vector4(
                bounds.X + bounds.Z + (column * 0.08f),
                bounds.Y + used,
                column - (column * 0.10f),
                body.Y + body.W - bounds.Y - used);

            if (said.W > surface.Line)
            {
                float wrap = said.Z - surface.Em(10);
                float tall = 0;

                foreach (string line in note.Text.Split(Newline))
                {
                    tall += line.Length == 0
                        ? surface.Line
                        : surface.Lines(line, wrap) * surface.Line;
                }

                float read = surface.BeginScroll("mapnote", said, tall);
                float at = said.Y - read;

                foreach (string line in note.Text.Split(Newline))
                {
                    at = line.Length == 0
                        ? at + surface.Line
                        : surface.Paragraph(
                            line, said.X, at, wrap, said.Y + said.W + tall, SidneyPalette.Ink);
                }

                surface.EndScroll();
            }
        }

        return bounds;
    }

    /// <summary>
    /// The figures that may be laid, as pictures down the side of the map.
    /// </summary>
    /// <remarks>
    /// <b>A shape is a thing, so it is drawn rather than named.</b> The first pass had a
    /// USE SHAPE button that opened a list of words over the map, which is two steps and a
    /// covered map to do what one look at a row of outlines does. Each one is the figure
    /// itself; clicking it lays that figure, and clicking it again takes it off, so several
    /// can be stacked and unstacked without a menu.
    /// </remarks>
    private static float Figures(
        SidneySurface surface, SidneyMachine machine, Vector4 map, float column)
    {
        IReadOnlyList<MapShape> offered = machine.Shapes;
        float side = MathF.Min(MathF.Max(surface.Em(26), map.W / 9), column * 0.34f);
        float gap = side * 0.22f;
        float x = map.X + map.Z + gap;
        float y = map.Y;

        // Nothing to offer yet, and a blank column teaches nobody that figures are coming.
        // The original left every menu item enabled and answered the ones that did not
        // apply; this says the same thing in the same place, without saying which picture
        // to read.
        if (offered.Count == 0)
        {
            var empty = new Vector4(x, y, side, side);

            surface.Fill(empty, SidneyPalette.Panel);
            surface.Frame(empty, SidneyPalette.Rule);

            surface.Paragraph(
                "no figures saved yet",
                x,
                y + side + gap,
                column - gap,
                map.Y + map.W,
                SidneyPalette.Dim);

            return side + gap + (surface.Line * 2.5f);
        }

        foreach (MapShape shape in offered)
        {
            var box = new Vector4(x, y, side, side);
            LaidShape? already = null;

            foreach (LaidShape laid in machine.Map.Laid)
            {
                if (laid.Shape == shape)
                {
                    already = laid;
                }
            }

            bool over = surface.Over(box);

            surface.Fill(
                box, already is not null || over ? SidneyPalette.PanelLit : SidneyPalette.Panel);

            surface.Frame(
                box,
                already is { Locked: true } ? SidneyPalette.Confirmed
                    : already is not null ? SidneyPalette.Figure
                    : over ? SidneyPalette.AmberDim
                    : SidneyPalette.Rule);

            Emblem(
                surface,
                shape,
                box,
                already is { Locked: true } ? SidneyPalette.Confirmed
                    : already is not null ? SidneyPalette.Figure
                    : SidneyPalette.Ink);

            surface.Hit("sidney:shape:" + SidneyMap.NameOf(shape), box);

            y += side + gap;
        }

        return y - map.Y + gap;
    }

    /// <summary>One figure drawn small, as the picture on its own button.</summary>
    private static void Emblem(
        SidneySurface surface, MapShape shape, Vector4 box, Vector4 colour)
    {
        float cx = box.X + (box.Z / 2);
        float cy = box.Y + (box.W / 2);
        float radius = MathF.Min(box.Z, box.W) * 0.32f;
        float weight = MathF.Max(1, MathF.Round(radius / 9));

        if (shape == MapShape.Line)
        {
            surface.Stroke(
                cx - radius, cy + (radius * 0.6f), cx + radius, cy - (radius * 0.6f), colour, weight);

            return;
        }

        if (shape == MapShape.Circle)
        {
            surface.Ring(cx, cy, radius, colour, weight);

            return;
        }

        int sides = shape switch
        {
            MapShape.Square => 4,
            MapShape.Triangle => 3,
            MapShape.Hexagram => 6,
            _ => 0,
        };

        if (sides == 0)
        {
            return;
        }

        var corners = new Vector2[sides];

        // Turned so that a square sits square and a triangle points upwards, which is what
        // each of them looks like when it is not standing on a corner.
        float turn = shape == MapShape.Square ? MathF.PI / 4 : -MathF.PI / 2;

        for (int i = 0; i < sides; i++)
        {
            float angle = turn + (i * MathF.Tau / sides);

            corners[i] = new Vector2(
                cx + (MathF.Cos(angle) * radius), cy + (MathF.Sin(angle) * radius));
        }

        if (shape == MapShape.Hexagram)
        {
            Ring(surface, [corners[0], corners[2], corners[4]], colour, weight);
            Ring(surface, [corners[1], corners[3], corners[5]], colour, weight);

            return;
        }

        Ring(surface, corners, colour, weight);
    }

    /// <summary>A closed outline through points already in screen pixels.</summary>
    private static void Ring(
        SidneySurface surface, Vector2[] corners, Vector4 colour, float weight)
    {
        for (int i = 0; i < corners.Length; i++)
        {
            Vector2 a = corners[i];
            Vector2 b = corners[(i + 1) % corners.Length];

            surface.Stroke(a.X, a.Y, b.X, b.Y, colour, weight);
        }
    }

    /// <summary>
    /// Rules the map, or the figure laid on it, into cells.
    /// </summary>
    /// <remarks>
    /// <b>Inside the figure it follows the figure.</b> The chessboard the Gemini and Cancer
    /// passages are about is eight by eight ruled inside the tilted square, and a grid that
    /// can only run north-south across the whole map cannot draw it. Ruled between opposite
    /// sides rather than in map coordinates, so it turns with the square it is in.
    /// </remarks>
    private static void Rule(
        SidneySurface surface,
        SidneyMachine machine,
        float left,
        float top,
        float side,
        float scale)
    {
        int cells = machine.Map.Grid;

        if (machine.Map.GridInShape && machine.Map.Laid.Count > 0)
        {
            LaidShape laid = machine.Map.Laid[^1];
            Vector2[] corners = SidneyMap.Corners(laid);

            if (corners.Length == 4)
            {
                Vector2 On(Vector2 map) =>
                    new(left + (map.X * scale), top + (map.Y * scale));

                for (int i = 1; i < cells; i++)
                {
                    float t = i / (float)cells;

                    // Between one pair of opposite sides, then the other.
                    Vector2 a = Vector2.Lerp(corners[0], corners[1], t);
                    Vector2 b = Vector2.Lerp(corners[3], corners[2], t);
                    Vector2 c = Vector2.Lerp(corners[0], corners[3], t);
                    Vector2 d = Vector2.Lerp(corners[1], corners[2], t);

                    Vector2 from = On(a);
                    Vector2 to = On(b);

                    surface.Stroke(from.X, from.Y, to.X, to.Y, SidneyPalette.Rule, 1);

                    from = On(c);
                    to = On(d);

                    surface.Stroke(from.X, from.Y, to.X, to.Y, SidneyPalette.Rule, 1);
                }

                return;
            }
        }

        float stepped = side / cells;

        for (int i = 1; i < cells; i++)
        {
            surface.Fill(left + (i * stepped), top, 1, side, SidneyPalette.Rule);
            surface.Fill(left, top + (i * stepped), side, 1, SidneyPalette.Rule);
        }
    }

    /// <summary>
    /// Where a line through two places crosses the edges of the map.
    /// </summary>
    /// <param name="from">One place.</param>
    /// <param name="to">The other.</param>
    /// <param name="extent">How big the map is, in its own pixels.</param>
    /// <returns>The two ends, or nothing when there is no line to draw.</returns>
    /// <remarks>
    /// The line is what matters, not the segment: what the puzzle asks is what else the join
    /// between two villages passes through, and a line stopping at the second of them
    /// answers nothing. Clipped by walking the four edges and keeping the two crossings that
    /// fall inside, which handles a line at any angle without four special cases.
    /// </remarks>
    private static Vector2[] Across(Vector2 from, Vector2 to, float extent)
    {
        Vector2 along = to - from;

        if (along.LengthSquared() < 1e-3f)
        {
            return [];
        }

        List<Vector2> ends = [];

        void Cross(float t)
        {
            if (!float.IsFinite(t))
            {
                return;
            }

            Vector2 at = from + (along * t);

            if (at.X >= -1 && at.X <= extent + 1 && at.Y >= -1 && at.Y <= extent + 1)
            {
                ends.Add(at);
            }
        }

        if (MathF.Abs(along.X) > 1e-4f)
        {
            Cross(-from.X / along.X);
            Cross((extent - from.X) / along.X);
        }

        if (MathF.Abs(along.Y) > 1e-4f)
        {
            Cross(-from.Y / along.Y);
            Cross((extent - from.Y) / along.Y);
        }

        // A line through a corner crosses two edges at the same place, so the far pair is
        // what is wanted rather than the first two found.
        if (ends.Count < 2)
        {
            return [];
        }

        Vector2 one = ends[0];
        Vector2 other = ends[0];
        float furthest = 0;

        foreach (Vector2 first in ends)
        {
            foreach (Vector2 second in ends)
            {
                float apart = Vector2.DistanceSquared(first, second);

                if (apart > furthest)
                {
                    furthest = apart;
                    one = first;
                    other = second;
                }
            }
        }

        return furthest > 1 ? [one, other] : [];
    }

    /// <summary>A closed outline through map points.</summary>
    private static void Outline(
        SidneySurface surface,
        Vector2[] corners,
        float left,
        float top,
        float scale,
        Vector4 colour,
        float weight)
    {
        for (int i = 0; i < corners.Length; i++)
        {
            Vector2 a = corners[i];
            Vector2 b = corners[(i + 1) % corners.Length];

            surface.Stroke(
                left + (a.X * scale),
                top + (a.Y * scale),
                left + (b.X * scale),
                top + (b.Y * scale),
                colour,
                weight);
        }
    }
}
