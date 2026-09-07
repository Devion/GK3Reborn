// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Numerics;

namespace GK3Reborn.Rendering;

/// <summary>
/// How a whole picture — a frame of film, or the still behind a menu — sits in the window.
/// </summary>
/// <remarks>
/// <para>
/// Shared by both backends because it has to be. It was written twice, once each, and the
/// two answers were not the same: one letterboxed a cutscene and covered a backdrop by
/// stretching it, the other kept the shape in both cases. A 4:3 title screen was therefore
/// the right shape on one machine and short and wide on the next.
/// </para>
/// <para>
/// It is also the only way anything drawn <em>over</em> a picture can know where the
/// picture went. The card between two parts of the day has lettering that belongs at a
/// particular spot on its painting, and the spot is in the painting's own coordinates; see
/// <c>IRenderer.PictureRect</c>, which turns this into window pixels.
/// </para>
/// </remarks>
public static class PictureFit
{
    /// <summary>
    /// How much of the window the picture covers, in each direction.
    /// </summary>
    /// <param name="pictureWidth">The picture's width in pixels.</param>
    /// <param name="pictureHeight">Its height.</param>
    /// <param name="windowWidth">The window's width in pixels.</param>
    /// <param name="windowHeight">Its height.</param>
    /// <param name="cover">Whether to fill the window rather than fit inside it.</param>
    /// <returns>
    /// The share of the window the picture spans, horizontally and vertically. One means
    /// exactly the window; less leaves a bar; more runs off the edge and is cropped.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>The picture's shape is never changed.</b> Whatever comes back,
    /// <c>windowWidth * x</c> over <c>windowHeight * y</c> is the picture's own aspect —
    /// which is the one property of this worth testing, and the one nobody notices is
    /// broken until everybody in a cutscene is short and wide.
    /// </para>
    /// <para>
    /// Fitting puts the whole picture in the window and leaves bars. Covering fills the
    /// window and crops — <b>but only so far</b>. Past <see cref="MostCropped"/> it stops
    /// and lets the bars come back, because a 4:3 title screen on an ultrawide display
    /// would otherwise be cropped until the game's own name ran off the bottom of it.
    /// </para>
    /// </remarks>
    public static (float X, float Y) Fit(
        int pictureWidth, int pictureHeight, int windowWidth, int windowHeight, bool cover)
    {
        if (pictureWidth <= 0 || pictureHeight <= 0 || windowWidth <= 0 || windowHeight <= 0)
        {
            return (1f, 1f);
        }

        float picture = (float)pictureWidth / pictureHeight;
        float window = (float)windowWidth / windowHeight;

        // How much covering would have to crop, and how much of that is allowed.
        float needed = picture > window ? picture / window : window / picture;
        float allowed = cover ? Math.Clamp(needed, 1f, MostCropped) : 1f;

        // The axis that grows is whichever the picture has to spare; the other follows from
        // it, and the two together always describe the picture's own shape.
        return picture > window
            ? (allowed, allowed * window / picture)
            : (allowed * picture / window, allowed);
    }

    /// <summary>
    /// Where the picture lands in the window, in pixels.
    /// </summary>
    /// <param name="pictureWidth">The picture's width in pixels.</param>
    /// <param name="pictureHeight">Its height.</param>
    /// <param name="windowWidth">The window's width in pixels.</param>
    /// <param name="windowHeight">Its height.</param>
    /// <param name="cover">Whether it fills the window rather than fitting inside it.</param>
    /// <returns>Left, top, width and height, centred on the window.</returns>
    /// <remarks>
    /// A covered picture is larger than the window and the rectangle says so: its left and
    /// top go negative and its size overruns. That is the point — something placed against
    /// the picture has to move off the edge with the part of the picture it belongs to,
    /// not be clamped back into view on its own.
    /// </remarks>
    public static Vector4 Rectangle(
        int pictureWidth, int pictureHeight, int windowWidth, int windowHeight, bool cover)
    {
        (float x, float y) = Fit(pictureWidth, pictureHeight, windowWidth, windowHeight, cover);

        float wide = windowWidth * x;
        float tall = windowHeight * y;

        return new Vector4((windowWidth - wide) / 2f, (windowHeight - tall) / 2f, wide, tall);
    }

    /// <summary>
    /// How far a covering picture may be cropped before bars are preferred.
    /// </summary>
    /// <remarks>
    /// A third. It is enough to fill any ordinary display with the game's 4:3 title art —
    /// 16:9 needs exactly a third — and not enough for an ultrawide to cut the lettering
    /// off the bottom of it.
    /// </remarks>
    public const float MostCropped = 1.34f;
}
