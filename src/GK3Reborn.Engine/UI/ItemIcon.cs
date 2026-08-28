using System.Numerics;

namespace GK3Reborn.UI;

/// <summary>
/// An inventory item's picture, ready to draw.
/// </summary>
/// <param name="Picture">
/// What number the interface holds it under, or nought when the item has no picture.
/// </param>
/// <param name="Width">Its width in pixels, as painted.</param>
/// <param name="Height">Its height.</param>
/// <remarks>
/// The size travels with the number because the pictures are not all one shape — a
/// passport is wider than it is tall and a dagger is the other way about — and a screen
/// that squares them up hands the player a squashed picture of something they are trying
/// to recognise at a glance.
/// </remarks>
public readonly record struct ItemIcon(int Picture, int Width, int Height)
{
    /// <summary>Whether there is anything to draw.</summary>
    public bool Drawn => Picture > 0 && Width > 0 && Height > 0;

    /// <summary>Where to draw it so that it fills a square without changing shape.</summary>
    /// <param name="x">Left of the square, in pixels.</param>
    /// <param name="y">Top of the square.</param>
    /// <param name="side">How big the square is.</param>
    /// <returns>The rectangle to draw into, centred in the square.</returns>
    public Vector4 Fit(float x, float y, float side)
    {
        if (!Drawn || side <= 0)
        {
            return default;
        }

        float scale = side / Math.Max(Width, Height);
        float width = Width * scale;
        float height = Height * scale;

        return new Vector4(x + ((side - width) / 2), y + ((side - height) / 2), width, height);
    }
}
