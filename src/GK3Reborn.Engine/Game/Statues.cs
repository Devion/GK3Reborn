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
public static class Statues
{
    /// <summary>How few triangles a thing can have and still be a card.</summary>
    public const int CardTriangles = 4;

    /// <summary>How many triangles a replacement must have to be worth the swap.</summary>
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
    public static bool IsSculpt(ModFile? sculpt) => sculpt is not null && sculpt.TriangleCount >= SculptTriangles;
}
