// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

namespace GK3Reborn.Rendering;

/// <summary>
/// How the traced world is divided, and what a ray is allowed to see of it.
/// </summary>
public static class TracedWorld
{
    /// <summary>The room's own geometry, for a ray that wants to skip what stands in it.</summary>
    public const uint WorldMask = 0x01;

    /// <summary>The models standing in the room.</summary>
    public const uint ModelMask = 0x02;

    /// <summary>
    /// The room's keyed cards, given a silhouette to cast at load.
    /// </summary>
    public const uint UnbakedMask = 0x04;

    /// <summary>The part every card occluder in a room belongs to.</summary>
    public const int CardPart = -1;

    /// <summary>Which mask an instance carries.</summary>
    /// <param name="part">The part key; zero is the room.</param>
    /// <returns>The mask.</returns>
    public static uint MaskFor(int part) => part switch
    {
        0 => WorldMask,
        CardPart => UnbakedMask,
        _ => ModelMask,
    };

    /// <summary>
    /// Whether a part's triangles may <em>not</em> be told apart by which side they are met
    /// from.
    /// </summary>
    /// <param name="part">The part key; zero is the room.</param>
    /// <returns>True where facing culling must be disabled for the whole instance.</returns>
    public static bool FacesBothWays(int part) => part <= 0;

    /// <summary>Whether a part's vertices may be rewritten after the structure is built.</summary>
    /// <param name="part">The part key.</param>
    /// <returns>True for the things a clip or a walk can reshape.</returns>
    public static bool Posable(int part) => part > 0;
}
