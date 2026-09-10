// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

namespace GK3Reborn.Rendering;

/// <summary>
/// What the renderer knows about grass: how to recognise a blade card by its texture, and
/// how it moves.
/// </summary>
/// <remarks>
/// A tree's leaves sway as a fraction of the model's own height, which works because a
/// grown tree is one unit tall in its own space. Grass is thousands of clumps baked into one
/// mesh in world space, so there is no model height to be a fraction of; a blade bends from
/// its root by how far up the blade a vertex is, which the card's own texture coordinate
/// says, and by a fixed distance at the tip. The renderer tells the two apart by the texture
/// name, which is the one thing the loader and the batch both hold. See
/// <see cref="Game.Grass"/> for where the clumps come from.
/// </remarks>
public static class GrassCards
{
    /// <summary>What every generated grass texture is called, before the ground it stands on.</summary>
    public const string TexturePrefix = "RBN_GRASS_";

    /// <summary>How far the tip of a blade travels in the wind, in world units.</summary>
    public const float Sway = 0.9f;

    /// <summary>How fast the wind runs through it, in radians a second. Quicker than a crown's.</summary>
    public const float Speed = 1.7f;

    /// <summary>Whether a texture is a generated grass card.</summary>
    /// <param name="texture">The texture's name, with or without an extension.</param>
    /// <returns>True for grass.</returns>
    public static bool IsGrass(string texture)
    {
        ArgumentNullException.ThrowIfNull(texture);

        return texture.StartsWith(TexturePrefix, StringComparison.OrdinalIgnoreCase);
    }
}
