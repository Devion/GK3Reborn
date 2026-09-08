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
/// <param name="Shape">
/// What the sprite is, on one channel.
/// <list type="bullet">
/// <item>
/// <b>Nought to one</b> is a disc: nought hides what is behind it and one only adds to it.
/// Smoke is the first and an ember is the second, and both are drawn by one pass with one
/// blend — see <see cref="Shaders.ParticleShaders"/>.
/// </item>
/// <item>
/// <b><see cref="Bird"/> and above</b> is a bird, drawn as a silhouette rather than a
/// disc, with the fraction above it the point its wings have reached in their beat. See
/// <see cref="Game.BirdFlock"/>.
/// </item>
/// <item>
/// <b><see cref="Fire"/> and above</b> is an open flame, raymarched through the sprite
/// rather than drawn on it, with <see cref="Fire"/> carrying the plume. See
/// <see cref="Game.FlameParticles"/>.
/// </item>
/// </list>
/// One channel rather than a vector of its own, because what a sprite <em>is</em> is one
/// number and the things that need more than that carry it separately. The disc values
/// are exactly the numbers they always were, so a room with no birds and no fire in it is
/// drawn by the arithmetic that has always drawn it.
/// </param>
/// <param name="Plume">
/// What the flame is, for the one shape that is a volume rather than a picture: how tall
/// the plume is, how wide, where in its own cycle it is, and which of the game's three
/// fires it is. Zero for everything else. See <see cref="Game.Flame"/>.
/// </param>
public readonly record struct Particle(
    Vector3 Position, float Size, Vector4 Tint, float Spin, float Shape, Vector4 Plume = default)
{
    /// <summary>Where the bird silhouettes start on <see cref="Shape"/>.</summary>
    public const float Bird = 2f;

    /// <summary>A bird whose wings have reached a given point in their beat.</summary>
    /// <param name="beat">Where in the beat, from nought to one; wrapped rather than clamped.</param>
    /// <returns>The value to hand the pass as <see cref="Shape"/>.</returns>
    public static float Flapping(float beat) => Bird + (beat - MathF.Floor(beat));

    /// <summary>Where the open flames start on <see cref="Shape"/>.</summary>
    public const float Fire = 4f;
}

/// <summary>
/// One corner of one particle, in the form the vertex shader reads.
/// </summary>
/// <param name="PositionAndSize">Where the particle is, and how big.</param>
/// <param name="CornerAndShape">
/// Which corner of the sprite this is, from -1 to 1 on each axis; then the spin and what
/// kind of sprite it is. See <see cref="Particle.Shape"/>.
/// </param>
/// <param name="Tint">Colour and alpha.</param>
/// <param name="Plume">
/// The plume, for a sprite that is an open flame; zero for every other kind. See
/// <see cref="Particle.Fire"/>.
/// </param>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct ParticleVertex(
    Vector4 PositionAndSize, Vector4 CornerAndShape, Vector4 Tint, Vector4 Plume)
{
    /// <summary>How many particles one buffer holds.</summary>
    public const int Capacity = 800;

    /// <summary>How many vertices one particle takes.</summary>
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
                    particle.Shape),
                particle.Tint,
                particle.Plume);
        }
    }

    /// <summary>Turns a frame's particles into the vertices a pass draws.</summary>
    /// <param name="particles">The particles, already in the order they are to be drawn.</param>
    /// <param name="into">Where to write them; at least <see cref="Capacity"/> particles' worth.</param>
    /// <returns>How many vertices were written.</returns>
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
