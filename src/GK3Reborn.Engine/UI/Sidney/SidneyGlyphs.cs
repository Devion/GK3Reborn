// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Numerics;
using GK3Reborn.Game.Sidney;

namespace GK3Reborn.UI.Sidney;

/// <summary>
/// The pictures on Sidney's desktop icons.
/// </summary>
/// <remarks>
/// <para>
/// <b>The game has no icons.</b> Its own art for the eight screens is eight 76x13 amber
/// name plates — <c>B_SEARCH_U.BMP</c> and its hover and pressed states — which are labels,
/// not pictures, and a desktop of eight identical amber bars is a menu with extra steps. So
/// the plates stay as the captions they were drawn to be and the picture above each one is
/// drawn here, from the same rectangles, lines and circles the rest of the interface is
/// made of.
/// </para>
/// <para>
/// Every glyph is drawn inside a square and scales with it, so the same code covers a
/// desktop icon and the small mark beside a window's title.
/// </para>
/// <para>
/// <b>Thickness is asked for, never looped over.</b> Drawing a five-pixel ring as five
/// one-pixel rings costs five times the rectangles, and the display list has a cap: on a
/// large window the eight icons alone ran it out, and what fell off the end was the taskbar
/// at the bottom of the screen. Every curve and line here takes its thickness as an
/// argument and draws it once.
/// </para>
/// </remarks>
public static class SidneyGlyphs
{
    /// <summary>Draws the picture belonging to one of Sidney's screens.</summary>
    /// <param name="surface">Where to draw.</param>
    /// <param name="screen">Which screen's picture.</param>
    /// <param name="box">The square to draw it in.</param>
    /// <param name="colour">What colour to draw it.</param>
    public static void Draw(SidneySurface surface, SidneyScreen screen, Vector4 box, Vector4 colour)
    {
        ArgumentNullException.ThrowIfNull(surface);

        switch (screen)
        {
            case SidneyScreen.Search:
                Magnifier(surface, box, colour);
                break;

            case SidneyScreen.EMail:
                Envelope(surface, box, colour);
                break;

            case SidneyScreen.Files:
                Folder(surface, box, colour);
                break;

            case SidneyScreen.Analyze:
                Document(surface, box, colour);
                break;

            case SidneyScreen.Translate:
                Exchange(surface, box, colour);
                break;

            case SidneyScreen.AddData:
                Scanner(surface, box, colour);
                break;

            case SidneyScreen.MakeId:
                Card(surface, box, colour);
                break;

            case SidneyScreen.Suspects:
                Fingerprint(surface, box, colour);
                break;

            default:
                surface.Frame(Inset(box, 0.18f), colour, Thick(box));
                break;
        }
    }

    /// <summary>
    /// The power button: a ring broken at the top with a stroke through the gap.
    /// </summary>
    /// <param name="surface">Where to draw.</param>
    /// <param name="box">The square to draw it in.</param>
    /// <param name="colour">What colour.</param>
    /// <remarks>
    /// The way out of Sidney, and the one symbol on the whole desktop that needs no caption
    /// in any language — which is why it is here rather than the original's <c>EXIT</c>.
    /// </remarks>
    public static void Power(SidneySurface surface, Vector4 box, Vector4 colour)
    {
        ArgumentNullException.ThrowIfNull(surface);

        float cx = box.X + (box.Z / 2);
        float cy = box.Y + (box.W / 2);
        float radius = MathF.Min(box.Z, box.W) * 0.34f;
        float thick = Thick(box);

        // From a little past the top, all the way round to a little before it.
        surface.Arc(cx, cy, radius, MathF.PI * -0.42f, MathF.PI * 1.42f, colour, thick);

        surface.Fill(
            cx - (thick / 2), cy - radius - (radius * 0.35f), thick, radius * 0.95f, colour);
    }

    /// <summary>The envelope, for the notification that mail has arrived.</summary>
    /// <param name="surface">Where to draw.</param>
    /// <param name="box">The square to draw it in.</param>
    /// <param name="colour">What colour.</param>
    public static void Mail(SidneySurface surface, Vector4 box, Vector4 colour) =>
        Envelope(surface, box, colour);

    private static void Magnifier(SidneySurface surface, Vector4 box, Vector4 colour)
    {
        float side = MathF.Min(box.Z, box.W);
        float radius = side * 0.26f;
        float cx = box.X + (box.Z * 0.44f);
        float cy = box.Y + (box.W * 0.42f);
        float thick = Thick(box);

        surface.Ring(cx, cy, radius, colour, thick);

        // The handle, out of the ring's lower right at forty-five degrees.
        float from = radius * 0.72f;

        surface.Stroke(
            cx + from,
            cy + from,
            box.X + (box.Z * 0.82f),
            box.Y + (box.W * 0.80f),
            colour,
            thick);
    }

    private static void Envelope(SidneySurface surface, Vector4 box, Vector4 colour)
    {
        Vector4 body = Inset(box, 0.16f);

        // Not square: an envelope that is as tall as it is wide is a parcel.
        body.Y += body.W * 0.14f;
        body.W *= 0.72f;

        float thick = Thick(box);

        surface.Frame(body, colour, thick);

        // The flap, drawn as two lines from the top corners to the middle of the top edge.
        surface.Stroke(
            body.X,
            body.Y,
            body.X + (body.Z / 2),
            body.Y + (body.W * 0.55f),
            colour,
            thick);

        surface.Stroke(
            body.X + body.Z,
            body.Y,
            body.X + (body.Z / 2),
            body.Y + (body.W * 0.55f),
            colour,
            thick);
    }

    private static void Folder(SidneySurface surface, Vector4 box, Vector4 colour)
    {
        Vector4 body = Inset(box, 0.16f);
        float thick = Thick(box);
        float tab = body.W * 0.18f;

        // The tab along the top left, then the folder under it.
        surface.Fill(body.X, body.Y + (body.W * 0.12f), body.Z * 0.42f, tab, colour);

        var front = new Vector4(
            body.X, body.Y + (body.W * 0.12f) + tab - thick, body.Z, body.W * 0.70f);

        surface.Frame(front, colour, thick);
    }

    private static void Document(SidneySurface surface, Vector4 box, Vector4 colour)
    {
        Vector4 page = Inset(box, 0.18f);
        page.Z *= 0.78f;

        float thick = Thick(box);

        surface.Frame(page, colour, thick);

        // Three lines of writing on it, which is what makes it a document rather than a box.
        for (int i = 1; i <= 3; i++)
        {
            surface.Fill(
                page.X + (page.Z * 0.16f),
                page.Y + (page.W * (0.16f + (i * 0.16f))),
                page.Z * (i == 3 ? 0.42f : 0.68f),
                thick,
                colour);
        }

        // And the loupe over its corner, because analysis is looking at one closely.
        float radius = MathF.Min(box.Z, box.W) * 0.19f;
        float cx = box.X + (box.Z * 0.72f);
        float cy = box.Y + (box.W * 0.70f);

        surface.Ring(cx, cy, radius, colour, thick);

        surface.Stroke(
            cx + (radius * 0.72f),
            cy + (radius * 0.72f),
            cx + (radius * 1.6f),
            cy + (radius * 1.6f),
            colour,
            thick);
    }

    private static void Exchange(SidneySurface surface, Vector4 box, Vector4 colour)
    {
        Vector4 body = Inset(box, 0.18f);
        float thick = Thick(box);
        float head = body.Z * 0.16f;

        // An arrow to the right above one to the left: one language becoming another, which
        // is the only part of translation a picture can carry.
        float top = body.Y + (body.W * 0.30f);
        float low = body.Y + (body.W * 0.66f);
        float right = body.X + (body.Z * 0.88f);
        float left = body.X + (body.Z * 0.12f);

        surface.Fill(body.X, top, body.Z * 0.88f, thick, colour);
        surface.Stroke(right, top, right - head, top - head, colour, thick);
        surface.Stroke(right, top, right - head, top + head, colour, thick);

        surface.Fill(left, low, body.Z * 0.88f, thick, colour);
        surface.Stroke(left, low, left + head, low - head, colour, thick);
        surface.Stroke(left, low, left + head, low + head, colour, thick);
    }

    private static void Scanner(SidneySurface surface, Vector4 box, Vector4 colour)
    {
        Vector4 body = Inset(box, 0.16f);
        float thick = Thick(box);

        // The bed of the scanner, and the sheet going down into it.
        var bed = new Vector4(body.X, body.Y + (body.W * 0.62f), body.Z, body.W * 0.30f);

        surface.Frame(bed, colour, thick);
        surface.Fill(bed.X + (bed.Z * 0.12f), bed.Y + (bed.W / 2), bed.Z * 0.76f, thick, colour);

        var sheet = new Vector4(
            body.X + (body.Z * 0.24f), body.Y, body.Z * 0.52f, body.W * 0.34f);

        surface.Frame(sheet, colour, thick);

        float cx = body.X + (body.Z / 2);

        surface.Fill(cx - (thick / 2), sheet.Y + sheet.W, thick, body.W * 0.14f, colour);

        surface.Stroke(
            cx, bed.Y - (thick * 2), cx - (body.Z * 0.10f), bed.Y - (body.W * 0.12f), colour, thick);

        surface.Stroke(
            cx, bed.Y - (thick * 2), cx + (body.Z * 0.10f), bed.Y - (body.W * 0.12f), colour, thick);
    }

    private static void Card(SidneySurface surface, Vector4 box, Vector4 colour)
    {
        Vector4 body = Inset(box, 0.16f);

        body.Y += body.W * 0.16f;
        body.W *= 0.68f;

        float thick = Thick(box);

        surface.Frame(body, colour, thick);

        // A head and shoulders on the left, lines on the right: an identity card, and the
        // one shape on the desktop that has to read as a person.
        float cx = body.X + (body.Z * 0.26f);
        float cy = body.Y + (body.W * 0.36f);
        float head = body.W * 0.14f;

        surface.Ring(cx, cy, head, colour, thick);
        surface.Arc(cx, cy + (head * 2.3f), head * 1.7f, MathF.PI, MathF.Tau, colour, thick);

        for (int i = 0; i < 3; i++)
        {
            surface.Fill(
                body.X + (body.Z * 0.52f),
                body.Y + (body.W * (0.26f + (i * 0.18f))),
                body.Z * (i == 2 ? 0.20f : 0.34f),
                thick,
                colour);
        }
    }

    private static void Fingerprint(SidneySurface surface, Vector4 box, Vector4 colour)
    {
        float cx = box.X + (box.Z / 2);
        float cy = box.Y + (box.W * 0.54f);
        float outer = MathF.Min(box.Z, box.W) * 0.36f;
        float thick = Thick(box);

        // Four ridges, each a little short of a full turn and each opening at a different
        // place, which is what a print looks like and a set of concentric circles does not.
        surface.Arc(cx, cy, outer, MathF.PI * 0.80f, MathF.PI * 2.30f, colour, thick);
        surface.Arc(cx, cy, outer * 0.72f, MathF.PI * 0.62f, MathF.PI * 2.20f, colour, thick);
        surface.Arc(cx, cy, outer * 0.46f, MathF.PI * 0.90f, MathF.PI * 2.42f, colour, thick);
        surface.Arc(cx, cy, outer * 0.20f, MathF.PI * 0.70f, MathF.PI * 2.10f, colour, thick);
    }

    /// <summary>A rectangle inside another by a fraction of its size.</summary>
    private static Vector4 Inset(Vector4 box, float fraction) =>
        new(
            box.X + (box.Z * fraction),
            box.Y + (box.W * fraction),
            box.Z * (1 - (fraction * 2)),
            box.W * (1 - (fraction * 2)));

    /// <summary>How thick a stroke should be for a glyph this big.</summary>
    private static float Thick(Vector4 box) =>
        MathF.Max(1f, MathF.Round(MathF.Min(box.Z, box.W) / 22f));
}
