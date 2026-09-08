// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Numerics;

namespace GK3Reborn.Rendering.Upscaling;

/// <summary>
/// Where each frame's camera is nudged to, so that a temporal upscaler has something new
/// to accumulate.
/// </summary>
public static class JitterSequence
{
    /// <summary>
    /// How many frames the sequence runs for before it repeats.
    /// </summary>
    /// <param name="renderWidth">Width the room is drawn at.</param>
    /// <param name="displayWidth">Width it is shown at.</param>
    /// <returns>The phase count, never below one.</returns>
    public static int PhaseCount(int renderWidth, int displayWidth)
    {
        if (renderWidth <= 0 || displayWidth <= 0)
        {
            return 1;
        }

        float ratio = displayWidth / (float)renderWidth;

        return Math.Max(1, (int)(8f * ratio * ratio));
    }

    /// <summary>Where inside its pixel frame <paramref name="index"/> samples.</summary>
    /// <param name="index">Which frame, counting from zero and never reset.</param>
    /// <param name="phaseCount">How long the sequence is.</param>
    /// <returns>An offset in pixels, each component within a half either way.</returns>
    public static Vector2 Offset(long index, int phaseCount)
    {
        int length = Math.Max(1, phaseCount);
        int at = (int)(((index % length) + length) % length) + 1;

        return new Vector2(Halton(at, 2) - 0.5f, Halton(at, 3) - 0.5f);
    }

    /// <summary>The offset as the projection matrix wants it.</summary>
    /// <param name="pixels">The offset in pixels.</param>
    /// <param name="width">Render width.</param>
    /// <param name="height">Render height.</param>
    /// <returns>The same offset in clip space, where the whole frame is two units across.</returns>
    public static Vector2 ToClip(Vector2 pixels, int width, int height) => new(
        width > 0 ? 2f * pixels.X / width : 0f,
        height > 0 ? 2f * pixels.Y / height : 0f);

    /// <summary>The <paramref name="index"/>th element of the Halton sequence.</summary>
    /// <param name="index">One-based position in the sequence.</param>
    /// <param name="numberBase">The base, which is 2 for X and 3 for Y.</param>
    /// <returns>A number in the half-open range zero to one.</returns>
    private static float Halton(int index, int numberBase)
    {
        float result = 0f;
        float fraction = 1f;
        int at = index;

        while (at > 0)
        {
            fraction /= numberBase;
            result += fraction * (at % numberBase);
            at /= numberBase;
        }

        return result;
    }
}
