// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using GK3Reborn.Formats.Models;

namespace GK3Reborn.Game;

/// <summary>
/// Which of a room's flat props have been carved into something with a back to it.
/// </summary>
/// <remarks>
/// <para>
/// GK3 draws a few dozen things as <b>billboards</b>: one quad, flagged in the
/// <c>.MOD</c> header, which the 1999 engine turned about its own vertical every frame so
/// that it always presented its face to the camera. It is the trick that makes a painted
/// pine read as a tree from anywhere on the path, and it is how the five saints in the
/// church at Rennes-le-Château are drawn.
/// </para>
/// <para>
/// A statue is where the trick shows. A tree seen from another angle is a tree; a
/// life-size figure standing in a niche that swivels to follow the player is a cardboard
/// cut-out, and that is what the player reported. So the five are sculpted outside the
/// engine — <c>PbrLab/make_statues.py</c> for the shape, <c>tools/blender/carve_statues.py</c>
/// for everything else — and the carved model stands in for the card.
/// </para>
/// <para>
/// <b>The swap is decided by evidence and not by a list.</b> Nothing here names a saint.
/// What it asks is whether the archives hold a billboard card under this name and whether
/// the content has real geometry to put in its place, which is the same question
/// <see cref="Foliage"/> asks before it grows a tree over a foliage card. A name the
/// content has nothing for keeps its card, and a name whose card is not a card is left
/// alone — so this cannot quietly replace a modelled prop that happens to share a name
/// with something in <c>enhanced/models</c>, which is the boundary
/// <see cref="Content.ModelLibrary"/> exists to hold.
/// </para>
/// </remarks>
public static class Statues
{
    /// <summary>How few triangles a thing can have and still be a card.</summary>
    /// <remarks>
    /// Two, in every one of the five. Four allows for a card that was split, and is still
    /// far below anything that could be called a shape.
    /// </remarks>
    public const int CardTriangles = 4;

    /// <summary>How many triangles a replacement must have to be worth the swap.</summary>
    /// <remarks>
    /// A carved saint is six thousand. Sixty-four is not a threshold anything real is near
    /// — it is there so that a truncated or half-written file cannot pass for a sculpt.
    /// </remarks>
    public const int SculptTriangles = 64;

    /// <summary>Whether a prop the archives supplied is a billboard card.</summary>
    /// <param name="card">The model as the archives hold it.</param>
    /// <returns>True when it is one flat quad drawn as a billboard.</returns>
    public static bool IsCard(ModFile? card) =>
        card is { IsBillboard: true } &&
        card.Meshes.Count == 1 &&
        card.Meshes[0].Submeshes.Count == 1 &&
        card.TriangleCount is > 0 and <= CardTriangles;

    /// <summary>Whether what the content offers is a statue rather than another card.</summary>
    /// <param name="sculpt">The model the library answered with, or null.</param>
    /// <returns>True when it has enough geometry to be worth standing up.</returns>
    public static bool IsSculpt(ModFile? sculpt) =>
        sculpt is not null && sculpt.TriangleCount >= SculptTriangles;
}
