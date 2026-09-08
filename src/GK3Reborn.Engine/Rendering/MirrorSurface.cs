// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System;
using System.Collections.Generic;
using System.Numerics;
using GK3Reborn.Rendering.Geometry;

namespace GK3Reborn.Rendering;

/// <summary>One flat piece of mirror, in world space.</summary>
/// <param name="Plane">
/// Its plane: <c>xyz</c> a unit normal pointing out of the glass, <c>w</c> the offset, so
/// that a point on it satisfies <c>dot(xyz, point) + w == 0</c>.
/// </param>
/// <param name="Center">The middle of it, used to decide which mirror a frame is about.</param>
/// <param name="Radius">
/// How far the glass reaches from that middle. A size rather than an area, because it is
/// only ever compared against another mirror's.
/// </param>
public readonly record struct MirrorSurface(Vector4 Plane, Vector3 Center, float Radius);

/// <summary>
/// Finds the plane a mirror reflects about, and decides which mirror a frame is about.
/// </summary>
public static class MirrorSurfaces
{
    /// <summary>
    /// How far out of its plane a piece may wander and still count as flat, as a share of
    /// its own size.
    /// </summary>
    public const float Flatness = 0.01f;

    /// <summary>
    /// How square-on a mirror must be before it is worth rendering the room again for it,
    /// as the cosine between its normal and the way to the camera.
    /// </summary>
    public const float LeastFacing = 0.05f;

    /// <summary>
    /// Fits the plane of one piece of geometry, if it is flat enough to be glass.
    /// </summary>
    /// <param name="shape">The piece's vertices, in its own space.</param>
    /// <param name="transform">Where that space is in the room.</param>
    /// <returns>Its plane, or null if it is not flat, not big enough, or degenerate.</returns>
    public static MirrorSurface? Fit(ReadOnlySpan<MeshVertex> shape, Matrix4x4 transform)
    {
        if (shape.Length < 3)
        {
            return null;
        }

        Vector3 center = Vector3.Zero;
        Vector3 normal = Vector3.Zero;

        foreach (MeshVertex vertex in shape)
        {
            center += Vector3.Transform(vertex.Position, transform);
            normal += Vector3.TransformNormal(vertex.Normal, transform);
        }

        center /= shape.Length;

        if (normal.LengthSquared() <= 1e-12f)
        {
            // Normals that cancel. A closed box averages to nothing, and so does a piece
            // wound both ways — neither is a piece of glass.
            return null;
        }

        normal = Vector3.Normalize(normal);

        float offset = -Vector3.Dot(normal, center);
        float radius = 0f;
        float wander = 0f;

        foreach (MeshVertex vertex in shape)
        {
            Vector3 world = Vector3.Transform(vertex.Position, transform);

            radius = Math.Max(radius, (world - center).Length());
            wander = Math.Max(wander, Math.Abs(Vector3.Dot(normal, world) + offset));
        }

        if (radius <= 1e-3f || wander > Flatness * radius)
        {
            return null;
        }

        return new MirrorSurface(new Vector4(normal, offset), center, radius);
    }

    /// <summary>
    /// How thick a band of heights counts as one level of a floor, in world units.
    /// </summary>
    public const float Level = 2f;

    /// <summary>
    /// How much of a floor has to be at one height before that height is worth a pass.
    /// </summary>
    public const float Mostly = 1f / 3f;

    /// <summary>
    /// How wide a floor has to be before it is worth drawing the room again for, in units.
    /// </summary>
    public const float LeastFloor = 120f;

    /// <summary>
    /// Finds the level a room's floor mostly lies at, as a plane to reflect about.
    /// </summary>
    /// <param name="pieces">Every piece of the room's floor, each with where it stands.</param>
    /// <returns>The plane, or null if there is no floor worth a pass.</returns>
    public static MirrorSurface? Ground(
        IReadOnlyList<(MeshVertex[] Shape, Matrix4x4 Transform)> pieces)
    {
        ArgumentNullException.ThrowIfNull(pieces);

        // Which height most of it is at, by counting the vertices into bands. A dictionary
        // rather than a sort: a room's floor is thousands of vertices and this runs once a
        // frame over a list that only changes when the room does.
        Dictionary<int, int> bands = [];
        int commonest = 0;
        int most = 0;
        int total = 0;

        foreach ((MeshVertex[] shape, Matrix4x4 transform) in pieces)
        {
            foreach (MeshVertex vertex in shape)
            {
                int band = (int)MathF.Floor(
                    Vector3.Transform(vertex.Position, transform).Y / Level);

                int count = bands.GetValueOrDefault(band) + 1;
                bands[band] = count;
                total++;

                if (count > most)
                {
                    most = count;
                    commonest = band;
                }
            }
        }

        if (total < 3 || most < total * Mostly)
        {
            return null;
        }

        // The plane goes through the mean of the vertices actually at that level, not
        // through the middle of the band: a floor a hair above a band boundary would
        // otherwise be reflected about a plane up to a whole band below itself.
        Vector3 center = Vector3.Zero;
        float height = 0f;
        int counted = 0;

        foreach ((MeshVertex[] shape, Matrix4x4 transform) in pieces)
        {
            foreach (MeshVertex vertex in shape)
            {
                Vector3 world = Vector3.Transform(vertex.Position, transform);

                if ((int)MathF.Floor(world.Y / Level) != commonest)
                {
                    continue;
                }

                center += world;
                height += world.Y;
                counted++;
            }
        }

        center /= counted;
        height /= counted;
        center.Y = height;

        float radius = 0f;

        foreach ((MeshVertex[] shape, Matrix4x4 transform) in pieces)
        {
            foreach (MeshVertex vertex in shape)
            {
                Vector3 world = Vector3.Transform(vertex.Position, transform);

                if ((int)MathF.Floor(world.Y / Level) == commonest)
                {
                    radius = MathF.Max(radius, (world - center).Length());
                }
            }
        }

        return radius < LeastFloor
            ? null
            : new MirrorSurface(new Vector4(0f, 1f, 0f, -height), center, radius);
    }

    /// <summary>
    /// Picks the one mirror a frame is about, from everything in the room that is one.
    /// </summary>
    /// <param name="mirrors">Every piece of glass found, in any order.</param>
    /// <param name="eye">Where the camera is.</param>
    /// <param name="holding">
    /// The mirror the frame before was about, which keeps a margin over the rest. See
    /// <see cref="Stickiness"/>.
    /// </param>
    /// <returns>The mirror to render the room again for, or null if none is worth it.</returns>
    public static MirrorSurface? Facing(
        IReadOnlyList<MirrorSurface> mirrors, Vector3 eye, MirrorSurface? holding = null)
    {
        ArgumentNullException.ThrowIfNull(mirrors);

        MirrorSurface? best = null;
        float bestScore = 0f;

        foreach (MirrorSurface mirror in mirrors)
        {
            Vector3 toEye = eye - mirror.Center;
            float distance = toEye.Length();

            if (distance <= 1e-3f)
            {
                continue;
            }

            // In front of the glass, and not so nearly edge-on that what comes back is a
            // sliver. The camera being behind a mirror is the common case rather than an
            // odd one: a room has mirrors on more than one wall and most of them are facing
            // away at any moment.
            float facing = Vector3.Dot(mirror.Plane.AsVector3(), toEye / distance);

            if (facing < LeastFacing)
            {
                continue;
            }

            // The one already being reflected keeps a margin. TE4's two mirrors face each
            // other across the room, so from the middle of it they are very nearly tied —
            // and without this the frame alternates between them, which is a reflection
            // flickering between two rooms and a line of log for every frame of it.
            float score = mirror.Radius / distance * (mirror.Equals(holding) ? Stickiness : 1f);

            if (score > bestScore)
            {
                bestScore = score;
                best = mirror;
            }
        }

        return best;
    }

    /// <summary>
    /// How much better a mirror must be than the one already being reflected to take over.
    /// </summary>
    public const float Stickiness = 1.15f;

    /// <summary>Reflects a point through a plane.</summary>
    /// <param name="plane">The plane, normalised.</param>
    /// <param name="point">The point.</param>
    /// <returns>Its image on the other side.</returns>
    public static Vector3 Reflect(Vector4 plane, Vector3 point) =>
        point - (2f * (Vector3.Dot(plane.AsVector3(), point) + plane.W) * plane.AsVector3());

    /// <summary>Reflects a direction through a plane.</summary>
    /// <param name="plane">The plane, normalised.</param>
    /// <param name="direction">The direction.</param>
    /// <returns>Its image on the other side.</returns>
    public static Vector3 ReflectDirection(Vector4 plane, Vector3 direction) =>
        direction - (2f * Vector3.Dot(plane.AsVector3(), direction) * plane.AsVector3());

    /// <summary>The plane's normal.</summary>
    /// <param name="plane">The plane.</param>
    /// <returns>Its first three components.</returns>
    private static Vector3 AsVector3(this Vector4 plane) => new(plane.X, plane.Y, plane.Z);
}
