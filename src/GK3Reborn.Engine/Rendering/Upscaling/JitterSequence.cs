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
/// <remarks>
/// <para>
/// A temporal upscaler works because consecutive frames are not the same picture. Left
/// alone they would be — a still camera in a still room samples the same point inside each
/// pixel every frame, and averaging a hundred copies of one sample gives back that sample.
/// Moving the sample point around inside the pixel is what turns a sequence of frames into
/// a denser sampling of the same image, and this is the sequence it is moved along.
/// </para>
/// <para>
/// Halton, base 2 and 3. It is what FidelityFX and NGX both expect, which matters: their
/// accumulation is tuned against a low-discrepancy sequence whose partial sums are evenly
/// spread, and feeding one a random offset instead makes the picture take longer to settle
/// and never settle as far. It is also reproducible, which is what lets a headless render
/// of frame 40 be compared against another one.
/// </para>
/// <para>
/// The length of the sequence grows with the square of the ratio. At Performance there are
/// four render pixels to a screen pixel's worth of area and the sequence has to be four
/// times as long to cover it, or the accumulation converges to a picture with holes in it.
/// This is FSR's own formula, and DLSS asks for the same thing.
/// </para>
/// </remarks>
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
    /// <remarks>
    /// The index is taken modulo the phase count here rather than by the caller, so a
    /// frame counter that runs for the length of a session — which is what the renderer
    /// has — is a valid argument. Halton is one-based: element nought of both bases is
    /// zero, and starting there would spend the first frame of every sequence sampling
    /// exactly the pixel centre it was trying to get away from.
    /// </remarks>
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
    /// <remarks>
    /// Y is not flipped here. The projection this is added to has already been flipped for
    /// Vulkan's clip space, so a positive Y offset moves the sample down the screen — which
    /// is the same direction the pixel offset means, and is what the upscalers are told the
    /// jitter was.
    /// </remarks>
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
