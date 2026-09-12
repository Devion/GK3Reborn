// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Numerics;

namespace GK3Reborn.Rendering;

/// <summary>
/// The waver of hot air over distant ground, as the output pass is told it.
/// </summary>
public static class HeatHaze
{
    /// <summary>How far the haze bends the picture at its fullest, in pixels at 1080 lines.</summary>
    public const float Pixels = 0.5f;

    /// <summary>Where the haze begins, in world units from the eye.</summary>
    public const float Begins = 700f;

    /// <summary>Where it is at its fullest.</summary>
    public const float Fullest = 2200f;

    /// <summary>The block the output pass takes.</summary>
    /// <param name="strength">How strongly to draw it; nought is off.</param>
    /// <param name="seconds">The clock.</param>
    /// <param name="camera">The camera the room was drawn with, for its near and far planes, or null.</param>
    /// <param name="height">The picture's height in pixels, which the bend is scaled with.</param>
    /// <returns>Amplitude in pixels, the clock, and the two depths the haze ramps between.</returns>
    public static Vector4 Constants(float strength, float seconds, Camera? camera, int height)
    {
        if (strength <= 0f || camera is null)
        {
            return Vector4.Zero;
        }

        return new Vector4(
            Pixels * strength * Math.Max(height, 1) / 1080f,
            seconds,
            Depth(camera, Begins),
            Depth(camera, Fullest));
    }

    /// <summary>The depth buffer's value for a distance in front of the camera.</summary>
    /// <param name="camera">The camera, for its planes.</param>
    /// <param name="distance">How far in front of it, in world units.</param>
    /// <returns>The value the room writes there, from nought at the near plane to one at the far.</returns>
    public static float Depth(Camera camera, float distance)
    {
        ArgumentNullException.ThrowIfNull(camera);

        float near = camera.NearPlane;
        float far = camera.FarPlane;
        float z = Math.Clamp(distance, near, far);

        // A left-handed perspective projection's depth, as the room's own matrix makes it:
        // f / (f - n) * (1 - n / z).
        return far / (far - near) * (1f - (near / z));
    }
}
