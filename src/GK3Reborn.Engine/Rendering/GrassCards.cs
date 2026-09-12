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
