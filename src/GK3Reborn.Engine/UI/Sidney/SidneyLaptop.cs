// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Numerics;
using GK3Reborn.Game.Sidney;

namespace GK3Reborn.UI.Sidney;

/// <summary>
/// The machine Sidney runs on: the laptop, and the screen inside it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Sidney is a thing in the room, not a page in front of it.</b> The original drew the
/// laptop and put its interface inside the screen, and the port's first pass drew a panel
/// over the room instead — which reads as the game's own menu rather than as Grace opening
/// her computer. The art for the laptop has been in the archives all along, unreferenced:
/// four pieces that assemble into a 1024x768 picture with a 640x480 hole where the screen
/// is.
/// </para>
/// <para>
/// <b>The four pieces carry the room behind them</b> — the wallpaper of Gabriel's room is
/// painted into their outer edges, which is what the original shipped and what makes the
/// laptop sit on a desk rather than float. They are drawn to fit the window's height and
/// centred, so the sides of a wide window show the room the player is actually standing in,
/// darkened. That is the compromise the art allows: the picture is not separable from its
/// backing.
/// </para>
/// </remarks>
public static class SidneyLaptop
{
    /// <summary>How wide the assembled picture is, in its own pixels.</summary>
    public const float ArtWidth = 1024f;

    /// <summary>How tall.</summary>
    public const float ArtHeight = 768f;

    /// <summary>How far in from the left the screen starts, in the picture's own pixels.</summary>
    public const float ScreenLeft = 192f;

    /// <summary>How far down.</summary>
    public const float ScreenTop = 144f;

    /// <summary>How wide the screen is.</summary>
    public const float ScreenWidth = 640f;

    /// <summary>How tall.</summary>
    public const float ScreenHeight = 480f;

    private static readonly string[] Pieces =
    [
        "S_SID_BKGD1024_TOP_A.BMP",
        "S_SID_BKGD1024_BOTTOM_A.BMP",
        "S_SID_BKGD1024_LEFT_A.BMP",
        "S_SID_BKGD1024_RIGHT_A.BMP",
    ];

    /// <summary>How much bezel to keep above the screen, in the picture's own pixels.</summary>
    private const float Above = 30f;

    /// <summary>And below, which is where the photograph propped against it starts.</summary>
    private const float Below = 96f;

    /// <summary>
    /// Where the laptop goes in a window.
    /// </summary>
    /// <param name="width">The window's width in pixels.</param>
    /// <param name="height">Its height.</param>
    /// <returns>The rectangle the whole picture occupies, which may be larger than the window.</returns>
    /// <remarks>
    /// <para>
    /// <b>The screen is what the window is fitted to, not the picture.</b> Fitting the whole
    /// 1024x768 into a 16:9 window spends a fifth of the height on the desk above the lid
    /// and the keyboard below it, and leaves the interface in a 600-pixel box in the middle
    /// of a large monitor. So what is fitted is the screen with a band of case around it —
    /// enough lid to read as a laptop and enough of the bottom for the photograph propped
    /// against it — and the rest is allowed to run off the edges, where the clip takes it.
    /// </para>
    /// <para>
    /// <b>Which middle it is centred on depends on whether anything is cropped.</b> When the
    /// window is wider than the picture's shape the height is what runs out, some of the
    /// case is lost off the top and bottom, and what should sit in the middle of the window
    /// is the band. When the window is taller — a portrait monitor, or a window dragged
    /// tall — the whole picture fits and centring on the band instead pushes it up the
    /// screen and leaves a dead strip of black under the keyboard. Reported as the interface
    /// going strange after a resize, and it is: nothing is cropped, so the picture is what
    /// wants centring.
    /// </para>
    /// </remarks>
    public static Vector4 Fit(int width, int height)
    {
        float band = MathF.Min(ArtHeight, ScreenTop + ScreenHeight + Below) - (ScreenTop - Above);
        float scale = MathF.Min(width / ArtWidth, height / band);
        float across = ArtWidth * scale;
        float down = ArtHeight * scale;
        float middle = ScreenTop - Above + (band / 2);

        return new Vector4(
            MathF.Round((width - across) / 2),
            MathF.Round(down <= height ? (height - down) / 2 : (height / 2f) - (middle * scale)),
            MathF.Round(across),
            MathF.Round(down));
    }

    /// <summary>Where the screen is inside the laptop.</summary>
    /// <param name="laptop">The whole picture's rectangle.</param>
    /// <returns>The screen's rectangle.</returns>
    public static Vector4 ScreenOf(Vector4 laptop)
    {
        float scale = laptop.Z / ArtWidth;

        return new Vector4(
            MathF.Round(laptop.X + (ScreenLeft * scale)),
            MathF.Round(laptop.Y + (ScreenTop * scale)),
            MathF.Round(ScreenWidth * scale),
            MathF.Round(ScreenHeight * scale));
    }

    /// <summary>
    /// Draws the laptop around a screen.
    /// </summary>
    /// <param name="surface">Where to draw.</param>
    /// <param name="laptop">Where the picture goes.</param>
    /// <returns>True when the game's own art was used.</returns>
    /// <remarks>
    /// Falls back to a drawn case when the art is not there, which is what a run against a
    /// half-copied installation looks like. Without one the interface would appear to float
    /// in the middle of the room with no explanation.
    /// </remarks>
    public static bool DrawShell(SidneySurface surface, Vector4 laptop)
    {
        ArgumentNullException.ThrowIfNull(surface);

        float scale = laptop.Z / ArtWidth;

        ItemIcon top = surface.Art(Pieces[0]);
        ItemIcon bottom = surface.Art(Pieces[1]);
        ItemIcon left = surface.Art(Pieces[2]);
        ItemIcon right = surface.Art(Pieces[3]);

        if (!top.Drawn || !bottom.Drawn || !left.Drawn || !right.Drawn)
        {
            DrawCase(surface, laptop);

            return false;
        }

        // Each piece where it was cut from, so the joins land exactly on the screen's edges
        // however big the window is. Half a pixel of rounding at a seam is a bright line
        // down the middle of a dark bezel.
        surface.Draw(top, At(laptop, scale, 0, 0, ArtWidth, ScreenTop));
        surface.Draw(
            bottom, At(laptop, scale, 0, ScreenTop + ScreenHeight, ArtWidth, ScreenTop));

        surface.Draw(left, At(laptop, scale, 0, ScreenTop, ScreenLeft, ScreenHeight));
        surface.Draw(
            right,
            At(laptop, scale, ScreenLeft + ScreenWidth, ScreenTop, ScreenLeft, ScreenHeight));

        return true;
    }

    /// <summary>A rectangle of the picture, in window pixels.</summary>
    private static Vector4 At(Vector4 laptop, float scale, float x, float y, float w, float h) =>
        new(
            MathF.Round(laptop.X + (x * scale)),
            MathF.Round(laptop.Y + (y * scale)),
            MathF.Ceiling(w * scale),
            MathF.Ceiling(h * scale));

    /// <summary>A plain case, for an installation whose art is missing.</summary>
    private static void DrawCase(SidneySurface surface, Vector4 laptop)
    {
        Vector4 screen = ScreenOf(laptop);
        var shell = new Vector4(
            laptop.X + (laptop.Z * 0.10f),
            laptop.Y + (laptop.W * 0.10f),
            laptop.Z * 0.80f,
            laptop.W * 0.80f);

        surface.Fill(shell, new Vector4(0.72f, 0.71f, 0.68f, 1f));
        surface.Frame(shell, new Vector4(0.45f, 0.44f, 0.42f, 1f), MathF.Max(1, surface.Em(2)));
        surface.Fill(screen, SidneyPalette.Screen);
    }
}
