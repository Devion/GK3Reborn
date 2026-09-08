// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using GK3Reborn.Rendering.Shaders;
using System.Numerics;

namespace GK3Reborn.Rendering;

/// <summary>A stretch of quads drawn from the same picture, the same way.</summary>
/// <param name="Picture">Which picture, where nought is the sheet of letters.</param>
/// <param name="First">The first vertex of the run.</param>
/// <param name="Count">How many vertices it has.</param>
/// <param name="Blend">
/// How the run is combined with what is already on the screen. A pipeline each, so a change
/// of blend breaks the run exactly as a change of picture does.
/// </param>
public readonly record struct OverlayRun(
    int Picture, int First, int Count, OverlayBlend Blend = OverlayBlend.Alpha);

/// <summary>Turns the interface's display list into triangles.</summary>
public static class OverlayMesh
{
    private static bool _saidSo;
    /// <summary>Builds the vertices for a display list.</summary>
    /// <param name="overlay">What to draw.</param>
    /// <param name="capacity">The most rectangles the vertex buffer holds.</param>
    /// <param name="pictures">How many of the screens' own pictures are loaded.</param>
    /// <param name="runs">Filled with the stretches drawn from each picture.</param>
    /// <returns>Six vertices a rectangle.</returns>
    public static OverlayVertex[] Build(
        Overlay overlay, int capacity, int pictures, List<OverlayRun> runs)
    {
        ArgumentNullException.ThrowIfNull(overlay);
        ArgumentNullException.ThrowIfNull(runs);

        runs.Clear();

        int rectangles = Math.Min(overlay.Quads.Count, capacity);

        if (overlay.Quads.Count > capacity && !_saidSo)
        {
            _saidSo = true;

            Foundation.Diagnostics.Log.Warning(
                $"GK3R3610: the interface asked for {overlay.Quads.Count} rectangles and " +
                $"the buffer holds {capacity}. What is drawn last is what is lost, which " +
                "is whatever sits on top.");
        }

        if (rectangles <= 0)
        {
            return [];
        }

        var vertices = new OverlayVertex[rectangles * 6];

        float sx = 2f / Math.Max(1, overlay.Width);
        float sy = 2f / Math.Max(1, overlay.Height);

        for (int i = 0; i < rectangles; i++)
        {
            OverlayQuad quad = overlay.Quads[i];

            // Pixels from the top-left to clip space, where the top of the screen is minus
            // one and y runs downwards.
            float x0 = (quad.Destination.X * sx) - 1f;
            float y0 = (quad.Destination.Y * sy) - 1f;
            float x1 = ((quad.Destination.X + quad.Destination.Z) * sx) - 1f;
            float y1 = ((quad.Destination.Y + quad.Destination.W) * sy) - 1f;

            float u0 = quad.Source.X;
            float v0 = quad.Source.Y;
            float u1 = u0 + quad.Source.Z;
            float v1 = v0 + quad.Source.W;

            Vector4 color = Linear(quad.Color);

            // The far edge's colour, where there is one. Two corners take it and two take
            // the near edge's, so what the hardware interpolates across the face is the
            // fade -- which is what lets a wall drawn in slices have no lines in it.
            Vector4 far = quad.Gradient is { } end ? Linear(end) : color;

            // Turned about the rectangle's own middle, in clip space, where a pixel of
            // height is not a pixel of width -- so the turn is applied in pixels and
            // converted afterwards, or a sigil on a 21:9 monitor comes out an ellipse.
            (Vector2 a, Vector2 b, Vector2 c, Vector2 d) = quad.Turn == 0f
                ? (new Vector2(x0, y0), new Vector2(x1, y0),
                   new Vector2(x0, y1), new Vector2(x1, y1))
                : Turned(quad, sx, sy);

            Vector4 rightTop = quad.GradientDown ? color : far;
            Vector4 leftBottom = quad.GradientDown ? far : color;
            Vector4 rightBottom = far;

            var topLeft = new OverlayVertex(a, new Vector2(u0, v0), color);
            var topRight = new OverlayVertex(b, new Vector2(u1, v0), rightTop);
            var bottomLeft = new OverlayVertex(c, new Vector2(u0, v1), leftBottom);
            var bottomRight = new OverlayVertex(d, new Vector2(u1, v1), rightBottom);

            int at = i * 6;
            vertices[at] = topLeft;
            vertices[at + 1] = bottomLeft;
            vertices[at + 2] = topRight;
            vertices[at + 3] = topRight;
            vertices[at + 4] = bottomLeft;
            vertices[at + 5] = bottomRight;

            // The interface is nearly all letters, so a screen showing a map costs three
            // runs rather than one and everything else still costs exactly one.
            int picture = quad.Picture >= 0 && quad.Picture <= pictures ? quad.Picture : 0;

            // A glyph has no blend of its own to ask for: the sheet is a stencil and it is
            // always drawn over what is behind it. Saying so here rather than at the call
            // sites is what keeps one stray Screen on a text quad from cutting the whole
            // interface into runs.
            OverlayBlend blend = picture > 0 ? quad.Blend : OverlayBlend.Alpha;

            if (runs.Count > 0 && runs[^1].Picture == picture && runs[^1].Blend == blend)
            {
                runs[^1] = runs[^1] with { Count = runs[^1].Count + 6 };
            }
            else
            {
                runs.Add(new OverlayRun(picture, at, 6, blend));
            }
        }

        return vertices;
    }

    /// <summary>The four corners of a turned rectangle, in clip space.</summary>
    /// <param name="quad">The rectangle, in pixels, with the turn it asked for.</param>
    /// <param name="sx">Clip-space units per pixel across.</param>
    /// <param name="sy">Clip-space units per pixel down.</param>
    /// <returns>Top left, top right, bottom left and bottom right, in that order.</returns>
    private static (Vector2 TopLeft, Vector2 TopRight, Vector2 BottomLeft, Vector2 BottomRight)
        Turned(OverlayQuad quad, float sx, float sy)
    {
        float halfWide = quad.Destination.Z / 2f;
        float halfTall = quad.Destination.W / 2f;
        float middleX = quad.Destination.X + halfWide;
        float middleY = quad.Destination.Y + halfTall;

        float cos = MathF.Cos(quad.Turn);
        float sin = MathF.Sin(quad.Turn);

        Vector2 At(float dx, float dy) => new(
            (((middleX + (dx * cos) - (dy * sin)) * sx) - 1f),
            (((middleY + (dx * sin) + (dy * cos)) * sy) - 1f));

        return (
            At(-halfWide, -halfTall),
            At(halfWide, -halfTall),
            At(-halfWide, halfTall),
            At(halfWide, halfTall));
    }

    /// <summary>Converts an authored colour into the space the target is written in.</summary>
    /// <param name="color">The colour as a colour picker gives it.</param>
    /// <returns>The same colour as linear light.</returns>
    public static Vector4 Linear(Vector4 color) => new(
        Component(color.X), Component(color.Y), Component(color.Z), color.W);

    private static float Component(float value) => value <= 0.04045f
        ? value / 12.92f
        : MathF.Pow((value + 0.055f) / 1.055f, 2.4f);
}
