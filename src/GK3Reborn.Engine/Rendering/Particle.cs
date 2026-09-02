// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Numerics;
using System.Runtime.InteropServices;

namespace GK3Reborn.Rendering;

/// <summary>
/// One particle, as whichever backend is drawing takes it.
/// </summary>
/// <param name="Position">Where it is, in world space.</param>
/// <param name="Size">Half the width of the square it draws as, in world units.</param>
/// <param name="Tint">Its colour and how opaque it is, straight alpha.</param>
/// <param name="Spin">How far the sprite is turned about the view axis, in radians.</param>
/// <param name="Additive">
/// Nought for something that hides what is behind it and one for something that only adds
/// to it. Smoke is the first and an ember is the second, and both are drawn by one pass
/// with one blend: see <see cref="Shaders.ParticleShaders"/>.
/// </param>
public readonly record struct Particle(
    Vector3 Position, float Size, Vector4 Tint, float Spin, float Additive);

/// <summary>
/// One corner of one particle, in the form the vertex shader reads.
/// </summary>
/// <param name="PositionAndSize">Where the particle is, and how big.</param>
/// <param name="CornerAndShape">
/// Which corner of the sprite this is, from -1 to 1 on each axis; then the spin and how
/// additive it is.
/// </param>
/// <param name="Tint">Colour and alpha.</param>
/// <remarks>
/// <para>
/// Six of these per particle in a plain vertex buffer, rather than a storage buffer
/// expanded by vertex index. Both backends already know how to bind a vertex buffer, and
/// neither has to agree about anything else for this to draw the same picture on both.
/// </para>
/// <para>
/// The particle's own position is on every corner rather than the corner's world position,
/// because the corner is not in world space until the camera is known — a sprite faces the
/// viewer, and the viewer moves after the buffer is written.
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct ParticleVertex(
    Vector4 PositionAndSize, Vector4 CornerAndShape, Vector4 Tint)
{
    /// <summary>How many particles one buffer holds.</summary>
    /// <remarks>
    /// A busy room is twelve fires, and a fire drawing more than sixty particles at once is
    /// drawing more smoke than a room in this game has ever contained. Eight hundred at six
    /// corners and forty-eight bytes apiece is 230 KB, rewritten once a frame.
    /// </remarks>
    public const int Capacity = 800;

    /// <summary>How many vertices one particle takes.</summary>
    /// <remarks>
    /// Six rather than four and an index buffer, which is what the interface's quads do and
    /// for the same reason: indexing saves a third of the space and costs a second buffer,
    /// and at a few hundred sprites a frame that is not a trade worth making.
    /// </remarks>
    public const int Corners = 6;

    /// <summary>Writes one particle's two triangles.</summary>
    /// <param name="into">Where to write them.</param>
    /// <param name="at">The index of the first of the six.</param>
    /// <param name="particle">The particle.</param>
    public static void Write(Span<ParticleVertex> into, int at, Particle particle)
    {
        var position = new Vector4(particle.Position, particle.Size);

        // Two triangles over the corners of the square, anticlockwise from the bottom left.
        // Which way round they are wound decides nothing: the pass culls neither face,
        // because a sprite is turned to face the camera and has no back.
        ReadOnlySpan<float> corners =
        [
            -1f, -1f, 1f, -1f, 1f, 1f,
            -1f, -1f, 1f, 1f, -1f, 1f,
        ];

        for (int corner = 0; corner < Corners; corner++)
        {
            into[at + corner] = new ParticleVertex(
                position,
                new Vector4(
                    corners[corner * 2],
                    corners[(corner * 2) + 1],
                    particle.Spin,
                    particle.Additive),
                particle.Tint);
        }
    }

    /// <summary>Turns a frame's particles into the vertices a pass draws.</summary>
    /// <param name="particles">The particles, already in the order they are to be drawn.</param>
    /// <param name="into">Where to write them; at least <see cref="Capacity"/> particles' worth.</param>
    /// <returns>How many vertices were written.</returns>
    /// <remarks>
    /// Shared, because both backends want exactly the same vertices and the arithmetic that
    /// makes them is the one place a sprite can come out inside out on one API and not the
    /// other.
    /// </remarks>
    public static int Build(IReadOnlyList<Particle> particles, Span<ParticleVertex> into)
    {
        ArgumentNullException.ThrowIfNull(particles);

        int drawn = Math.Min(Math.Min(particles.Count, Capacity), into.Length / Corners);

        for (int i = 0; i < drawn; i++)
        {
            Write(into, i * Corners, particles[i]);
        }

        return drawn * Corners;
    }
}
