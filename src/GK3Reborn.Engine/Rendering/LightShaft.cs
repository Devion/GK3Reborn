// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Numerics;

namespace GK3Reborn.Rendering;

/// <summary>A shaft of daylight coming in at a window: a box of lit air standing off the pane.</summary>
/// <param name="Origin">The middle of the pane, in world space, where the shaft starts.</param>
/// <param name="Direction">Which way the light travels into the room, a unit vector.</param>
/// <param name="HalfWidth">Half the shaft's width across, perpendicular to the direction.</param>
/// <param name="HalfHeight">Half its height, likewise.</param>
/// <param name="Length">How far it reaches before it meets the floor.</param>
/// <param name="Colour">What the light is, in linear light.</param>
/// <param name="Strength">
/// How bright it is drawn: one for the sun coming straight in, less for the diffuse light
/// of a window the sun does not reach this hour.
/// </param>
public readonly record struct LightShaft(
    Vector3 Origin,
    Vector3 Direction,
    float HalfWidth,
    float HalfHeight,
    float Length,
    Vector3 Colour,
    float Strength)
{
    /// <summary>How many shafts the pass can hold at once.</summary>
    public const int Capacity = 16;

    /// <summary>The horizontal axis of the shaft's cross-section, a unit vector.</summary>
    public Vector3 Across
    {
        get
        {
            Vector3 across = Vector3.Cross(Vector3.UnitY, Direction);

            return across.LengthSquared() > 1e-6f
                ? Vector3.Normalize(across)
                : Vector3.UnitX;
        }
    }

    /// <summary>The other axis of the cross-section, a unit vector.</summary>
    public Vector3 Upward => Vector3.Cross(Direction, Across);

    /// <summary>How far inside the shaft a point is, from nought on its axis to one at its edge.</summary>
    /// <param name="at">The point, in world space.</param>
    /// <param name="along">How far along the shaft it is, in world units; negative is behind the pane.</param>
    /// <returns>Less than one inside, more outside.</returns>
    public float Inside(Vector3 at, out float along)
    {
        Vector3 from = at - Origin;

        along = Vector3.Dot(from, Direction);

        Vector3 perpendicular = from - (Direction * along);

        float x = Vector3.Dot(perpendicular, Across) / MathF.Max(HalfWidth, 1e-3f);
        float y = Vector3.Dot(perpendicular, Upward) / MathF.Max(HalfHeight, 1e-3f);

        return MathF.Max(MathF.Abs(x), MathF.Abs(y));
    }
}
