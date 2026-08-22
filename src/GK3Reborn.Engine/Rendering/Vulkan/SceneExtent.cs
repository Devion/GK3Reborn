// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System;
using System.Numerics;

namespace GK3Reborn.Rendering.Vulkan;

/// <summary>What a scene occupies, in world space.</summary>
/// <remarks>
/// <para>
/// Carried alongside the rig for one purpose: telling a light that decays from a light that
/// was placed outside the room and given a range it could never span. A range is an
/// authored falloff when it reaches the geometry and leftover data when it cannot, and
/// nothing but the geometry's own extent can tell the two apart. See
/// <see cref="GpuLight.IsDistantKey"/>.
/// </para>
/// <para>
/// The default is deliberately "unknown" rather than an empty box at the origin. An empty
/// box would answer every question confidently and wrongly — every light in the game is
/// further from a point than its range, so every light would become a sun.
/// </para>
/// </remarks>
public readonly record struct SceneExtent
{
    private readonly Vector3 _minimum;
    private readonly Vector3 _maximum;

    /// <summary>Creates an extent.</summary>
    /// <param name="minimum">The low corner.</param>
    /// <param name="maximum">The high corner.</param>
    public SceneExtent(Vector3 minimum, Vector3 maximum)
    {
        _minimum = Vector3.Min(minimum, maximum);
        _maximum = Vector3.Max(minimum, maximum);
        IsKnown = true;
    }

    /// <summary>Whether any geometry was measured.</summary>
    public bool IsKnown { get; }

    /// <summary>The low corner; meaningless when nothing was measured.</summary>
    public Vector3 Minimum => _minimum;

    /// <summary>The high corner; meaningless when nothing was measured.</summary>
    public Vector3 Maximum => _maximum;

    /// <summary>How far a point lies outside the box.</summary>
    /// <param name="point">The point, in world space.</param>
    /// <returns>Zero for a point inside it, and the distance to the nearest face outside.</returns>
    public float DistanceTo(Vector3 point) =>
        Vector3.Distance(point, Vector3.Clamp(point, _minimum, _maximum));
}
