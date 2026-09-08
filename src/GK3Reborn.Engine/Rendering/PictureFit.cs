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
    public const float MostCropped = 1.34f;
}
