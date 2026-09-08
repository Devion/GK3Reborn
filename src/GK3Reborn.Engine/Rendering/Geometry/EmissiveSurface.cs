// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Numerics;

namespace GK3Reborn.Rendering.Geometry;

/// <summary>
/// A patch of the room that gives off light.
/// </summary>
/// <param name="Owner">
/// What the room calls it — a BSP object name for the room's own geometry, a model name for
/// a prop. One entry per object, however many surfaces it is made of: a lamp shade is six.
/// </param>
/// <param name="Texture">Which picture makes it emissive, for the log and for diagnosis.</param>
/// <param name="Centre">The middle of it, in world space.</param>
/// <param name="Radius">
/// How big it is: the mean distance of its vertices from that middle. A size rather than a
/// bound, because it is used to decide how far the light it stands for reaches, and one
/// stray vertex should not double that.
/// </param>
/// <param name="Emission">
/// What colour it gives off and how strongly, as the material library has it.
/// </param>
public readonly record struct EmissiveSurface(
    string Owner, string Texture, Vector3 Centre, float Radius, Vector3 Emission);
