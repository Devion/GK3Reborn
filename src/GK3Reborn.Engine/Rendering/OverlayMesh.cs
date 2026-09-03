// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using GK3Reborn.Rendering.Shaders;
using System.Numerics;

namespace GK3Reborn.Rendering;

/// <summary>A stretch of quads drawn from the same picture.</summary>
/// <param name="Picture">Which picture, where nought is the sheet of letters.</param>
/// <param name="First">The first vertex of the run.</param>
/// <param name="Count">How many vertices it has.</param>
public readonly record struct OverlayRun(int Picture, int First, int Count);

/// <summary>Turns the interface's display list into triangles.</summary>
/// <remarks>
/// <para>
/// The same arithmetic on either backend, so it is done once here rather than twice in the
/// two overlay passes. Both APIs put clip space's y downwards — Vulkan natively, Direct3D
/// because the transpiled vertex stage flips it — so the top of the screen is minus one in
/// both and there is no flip to get wrong in one of them.
/// </para>
/// <para>
/// Two triangles a rectangle, written straight out. Indexing them would save a third of the
/// space and cost a second buffer; at a few hundred rectangles a frame that trade is not
/// worth making.
/// </para>
/// <para>
/// <b>A list too long for the buffer is cut, and says so.</b> The cut takes the rectangles
/// added last, which are the ones drawn on top — so what disappears is the taskbar, the
/// buttons and the notification, and what remains looks like a screen that was drawn
/// correctly and then had its furniture removed. It cost an afternoon once. It is reported
/// once per run rather than per frame, because a frame that overruns is followed by sixty
/// more.
/// </para>
/// </remarks>
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

            var topLeft = new OverlayVertex(new Vector2(x0, y0), new Vector2(u0, v0), color);
            var topRight = new OverlayVertex(new Vector2(x1, y0), new Vector2(u1, v0), color);
            var bottomLeft = new OverlayVertex(new Vector2(x0, y1), new Vector2(u0, v1), color);
            var bottomRight = new OverlayVertex(new Vector2(x1, y1), new Vector2(u1, v1), color);

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

            if (runs.Count > 0 && runs[^1].Picture == picture)
            {
                runs[^1] = new OverlayRun(picture, runs[^1].First, runs[^1].Count + 6);
            }
            else
            {
                runs.Add(new OverlayRun(picture, at, 6));
            }
        }

        return vertices;
    }

    /// <summary>Converts an authored colour into the space the target is written in.</summary>
    /// <param name="color">The colour as a colour picker gives it.</param>
    /// <returns>The same colour as linear light.</returns>
    /// <remarks>
    /// The swapchain is sRGB, so the hardware encodes whatever the shader writes. An
    /// interface is authored in the numbers a colour picker gives — a dark panel is 0.06,
    /// not 0.005 — and handing those straight to an sRGB target turns 0.06 into a light
    /// grey. Converting here means the interface is written in the units it was designed in
    /// and comes out looking like it.
    /// </remarks>
    public static Vector4 Linear(Vector4 color) => new(
        Component(color.X), Component(color.Y), Component(color.Z), color.W);

    private static float Component(float value) => value <= 0.04045f
        ? value / 12.92f
        : MathF.Pow((value + 0.055f) / 1.055f, 2.4f);
}
