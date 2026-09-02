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
/// <remarks>
/// <para>
/// A planar reflection is the scene rendered a second time from the camera reflected
/// through the mirror's plane, and what makes it cheap to sample is that <b>a point on the
/// plane lands on the same pixel in both renders</b>. Reflection fixes the plane pointwise,
/// so for a point on it the mirrored view matrix and the real one agree exactly; the glass
/// can therefore read the reflection at its own screen position and needs no matrix, no
/// second set of texture coordinates and nothing per-mirror in the shader at all.
/// </para>
/// <para>
/// <b>The plane is not in the data and has to be measured.</b> GK3's mirrors are not
/// planes: <c>MIRRORL.MOD</c> is a box twenty-one by thirty by three, and all five of its
/// pieces — front, back, both sides, top and bottom — carry the same texture. Marking the
/// texture a mirror marks the whole slab, so which of those faces is the glass has to come
/// out of the geometry.
/// </para>
/// <para>
/// <b>The vertices' own normals are what settle it</b>, not the plane fit. A fit gives a
/// plane and no sign — the front and back of that box are the same flat rectangle three
/// units apart, and turning each fitted normal towards the camera makes both of them look
/// like the glass. Averaging the shading normals gives a real outward direction, and the
/// back of the slab is then simply facing away.
/// </para>
/// </remarks>
public static class MirrorSurfaces
{
    /// <summary>
    /// How far out of its plane a piece may wander and still count as flat, as a share of
    /// its own size.
    /// </summary>
    /// <remarks>
    /// A mirror is glass and glass is flat, so this is tight enough to throw out the pieces
    /// of a slab that are not the glass while leaving room for the last bit of a coordinate
    /// that has been through a model transform. It is a share and not a length because the
    /// same absolute tolerance is generous on a hand mirror and meaningless on a wall.
    /// </remarks>
    public const float Flatness = 0.01f;

    /// <summary>
    /// How square-on a mirror must be before it is worth rendering the room again for it,
    /// as the cosine between its normal and the way to the camera.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nearly nothing, deliberately: a mirror seen at a glancing angle still shows a
    /// reflection, and the one thing that must not happen is for it to change back to its
    /// painted texture partway through a camera glide. What this rejects is the mirror
    /// edge-on and the mirror behind the camera, where the reflection would be a sliver of
    /// noise or nothing at all.
    /// </para>
    /// <para>
    /// It is measured to the mirror's middle rather than to the plane, because a wall-sized
    /// mirror the camera is standing beside passes a plane test while showing the camera
    /// nothing.
    /// </para>
    /// </remarks>
    public const float LeastFacing = 0.05f;

    /// <summary>
    /// Fits the plane of one piece of geometry, if it is flat enough to be glass.
    /// </summary>
    /// <param name="shape">The piece's vertices, in its own space.</param>
    /// <param name="transform">Where that space is in the room.</param>
    /// <returns>Its plane, or null if it is not flat, not big enough, or degenerate.</returns>
    /// <remarks>
    /// The plane comes from the vertices' averaged normal rather than from a covariance
    /// fit. It is the same plane on anything flat, it costs a pass rather than an
    /// eigenvector, and it is the one that carries a <em>side</em>: see the remarks on this
    /// class for why the side is the whole difference between the front of a mirror and the
    /// back of the same slab.
    /// </remarks>
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
    /// Picks the one mirror a frame is about, from everything in the room that is one.
    /// </summary>
    /// <param name="mirrors">Every piece of glass found, in any order.</param>
    /// <param name="eye">Where the camera is.</param>
    /// <param name="holding">
    /// The mirror the frame before was about, which keeps a margin over the rest. See
    /// <see cref="Stickiness"/>.
    /// </param>
    /// <returns>The mirror to render the room again for, or null if none is worth it.</returns>
    /// <remarks>
    /// <para>
    /// <b>One a frame, and the biggest wins.</b> Each mirror is another whole pass over the
    /// room, and a second one would buy a reflection in a mirror the player is not looking
    /// at. TE4 is the room that has more than one — two hanging mirrors and a third in the
    /// wall — and its cameras are placed at one mirror at a time, which is what makes the
    /// biggest-on-screen rule agree with the one the player means.
    /// </para>
    /// <para>
    /// Biggest is size over distance rather than size alone, which is the same ordering as
    /// how much of the screen it covers, without needing the projection to say so.
    /// </para>
    /// </remarks>
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
    /// <remarks>
    /// Enough to settle a tie and not enough to hold a mirror the camera has turned away
    /// from: at a sixth, a mirror has to be appreciably larger on the screen before the
    /// frame changes its mind, and one that is genuinely the subject is far past that.
    /// </remarks>
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
    /// <remarks>
    /// A direction has no position, so the plane's offset does not enter into it. Reflecting
    /// the up vector along with the eye is what keeps the mirrored camera's basis a basis;
    /// reflecting only the two points leaves it upside down on any mirror that is not
    /// vertical.
    /// </remarks>
    public static Vector3 ReflectDirection(Vector4 plane, Vector3 direction) =>
        direction - (2f * Vector3.Dot(plane.AsVector3(), direction) * plane.AsVector3());

    /// <summary>The plane's normal.</summary>
    /// <param name="plane">The plane.</param>
    /// <returns>Its first three components.</returns>
    private static Vector3 AsVector3(this Vector4 plane) => new(plane.X, plane.Y, plane.Z);
}
