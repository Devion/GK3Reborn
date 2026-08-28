// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Numerics;

namespace GK3Reborn.Formats.Models;

/// <summary>
/// Which space a model's authored normals are written in.
/// </summary>
/// <remarks>
/// <para>
/// <b>A <c>.MOD</c> does not say, and the corpus is not of one mind.</b> Positions are
/// always in the mesh's own space and are placed by <see cref="ModMesh.MeshToLocal"/>. The
/// normals beside them are in mesh space for a prop and in the model's <em>local</em> space
/// for a character — already placed — so transforming them a second time turns them.
/// </para>
/// <para>
/// It is not a small turn. Every character mesh group carries a rotation of about ninety
/// degrees, which is 3ds Max's Z-up world written out into GK3's Y-up one, baked per limb.
/// Applied to a normal that has already had it, a normal pointing out of Gabriel's chest
/// comes out pointing at the sky: measured in the renderer, his chest read
/// (-0.01, +0.98, +0.23). Every character in the game was then shaded almost entirely by
/// the vertical part of the rig, so the sun lit them the same however they were turned and
/// their fronts never lit at all.
/// </para>
/// <para>
/// So it is measured rather than declared. <c>CHARACTERS.TXT</c> would name most of them —
/// the engine already reads it — but it lists the forty-five characters who <em>walk</em>,
/// and the day-3 baby (<c>BAB</c>, 1,704 triangles across eight groups) is a character that
/// does not. Over the shipped corpus this selects 27 models of 1,878: the twenty-two
/// characters, the baby, the chicken, and three flat cards.
/// </para>
/// <para>
/// <b>The model decides and a group may overrule it.</b> Read alone, a group is often
/// mute — Vitorio's legs separate the two readings by 0.011, and the Lady's by nothing that
/// means anything — and a character whose limbs disagreed about this would be lit in
/// pieces. So the model is asked first, by counting the groups that do have an opinion, and
/// only a group with a clear opinion of its own departs from it. That is not a concession:
/// it is the case that matters, because <c>HeadRefinement</c> rebuilds a subdivided head's
/// normals from its mesh-space positions, so one group of a character legitimately needs
/// the transform its other twelve do not.
/// </para>
/// </remarks>
public static class ModNormals
{
    /// <summary>
    /// How well an authored normal has to match the surface before a group is allowed an
    /// opinion of its own.
    /// </summary>
    /// <remarks>
    /// A smooth normal is not a face normal — that is the point of one — so agreement is
    /// never total on a curved surface. Across the corpus a character's groups read 0.73 to
    /// 0.98 the right way round, props read 1.000, and the hit-test boxes read under 0.65
    /// whichever way they are taken, because their normals describe nothing.
    /// </remarks>
    private const double Confident = 0.75;

    /// <summary>How far apart the two readings must be for a group to have decided.</summary>
    /// <remarks>
    /// The two are half a turn apart where the question is real, so a group that knows the
    /// answer wins by a wide margin: a character's beat the other reading by 0.3 and more,
    /// a prop's by as much the other way. This sits far under that and far over the noise,
    /// so a group whose readings are close keeps whatever the model as a whole concluded
    /// rather than being flipped on a rounding error.
    /// </remarks>
    private const double Margin = 0.15;

    /// <summary>What one mesh group's own geometry says about its normals.</summary>
    /// <param name="Local">Mean agreement reading the normals as already placed.</param>
    /// <param name="Transformed">Mean agreement reading them as needing the transform.</param>
    /// <param name="Count">How many triangles were read.</param>
    private readonly record struct Reading(double Local, double Transformed, int Count)
    {
        /// <summary>Whether this group knows its own answer.</summary>
        internal bool Decisive =>
            Count > 0 &&
            Math.Max(Local, Transformed) >= Confident &&
            Math.Abs(Local - Transformed) >= Margin;

        /// <summary>Which way it leans, meaningful only where it is decisive.</summary>
        internal bool PrefersLocal => Local > Transformed;
    }

    /// <summary>
    /// Whether a model's normals are already in its local space.
    /// </summary>
    /// <param name="model">The model.</param>
    /// <returns>True when its normals must be used as they stand.</returns>
    /// <remarks>
    /// By counting the groups that have an opinion rather than by pooling every triangle,
    /// because a subdivided head is twenty times the rest of the character put together —
    /// <c>GAB</c> goes from 1,750 triangles to 41,904 — and pooling would let that one group
    /// answer for the whole body it disagrees with.
    /// </remarks>
    public static bool AreLocal(ModFile model)
    {
        ArgumentNullException.ThrowIfNull(model);

        int local = 0;
        int placed = 0;

        foreach (ModMesh mesh in model.Meshes)
        {
            Reading reading = Read(mesh);

            if (!reading.Decisive)
            {
                continue;
            }

            if (reading.PrefersLocal)
            {
                local++;
            }
            else
            {
                placed++;
            }
        }

        return local > placed;
    }

    /// <summary>
    /// Whether one mesh group's normals are already in the model's local space.
    /// </summary>
    /// <param name="mesh">The mesh group.</param>
    /// <param name="model">What the model it belongs to concluded, from
    /// <see cref="AreLocal(ModFile)"/>.</param>
    /// <returns>True when its normals must be used as they stand.</returns>
    public static bool AreLocal(ModMesh mesh, bool model)
    {
        ArgumentNullException.ThrowIfNull(mesh);

        Reading reading = Read(mesh);

        return reading.Decisive ? reading.PrefersLocal : model;
    }

    /// <summary>
    /// The basis a mesh group's normals must be put through before the renderer's own
    /// <see cref="ModMesh.MeshToLocal"/> is applied to them.
    /// </summary>
    /// <param name="mesh">The mesh group.</param>
    /// <param name="model">What the model it belongs to concluded.</param>
    /// <returns>
    /// The identity for normals in mesh space, and the inverse of the mesh's transform for
    /// normals already in local space, so that the renderer's multiply cancels out.
    /// </returns>
    /// <remarks>
    /// Expressed as a correction rather than as a flag so that animation keeps working. The
    /// renderer shades with the transform the mesh is posed by <em>now</em>, not the one it
    /// was authored with, so cancelling the authored transform leaves the clip's own turn
    /// on the normal — which is what a limb's normals should do when the limb moves. At
    /// rest the two are the same and the normal is left as the file wrote it.
    /// </remarks>
    public static Matrix4x4 CorrectionFor(ModMesh mesh, bool model) =>
        AreLocal(mesh, model) && Matrix4x4.Invert(mesh.MeshToLocal, out Matrix4x4 inverse)
            ? inverse
            : Matrix4x4.Identity;

    /// <summary>Reads one mesh group both ways.</summary>
    /// <remarks>
    /// Against each triangle's own winding, which is the one statement about the surface
    /// that needs no normal to make. Compared as an absolute dot product because the
    /// winding's <em>sign</em> is not a reliable reference here: every mesh transform in the
    /// corpus has a determinant of -1, so the cross product flips under it while the normal
    /// does not. What is being asked is only whether the normal lies along the surface or
    /// across it.
    /// </remarks>
    private static Reading Read(ModMesh mesh)
    {
        Matrix4x4 meshToLocal = mesh.MeshToLocal;
        double asIs = 0;
        double transformed = 0;
        int counted = 0;

        foreach (ModSubmesh submesh in mesh.Submeshes)
        {
            for (int i = 0; i + 2 < submesh.Indices.Length; i += 3)
            {
                int a = submesh.Indices[i];
                int b = submesh.Indices[i + 1];
                int c = submesh.Indices[i + 2];

                if (a >= submesh.Positions.Length ||
                    b >= submesh.Positions.Length ||
                    c >= submesh.Positions.Length ||
                    a >= submesh.Normals.Length ||
                    b >= submesh.Normals.Length ||
                    c >= submesh.Normals.Length)
                {
                    continue;
                }

                // The triangle where it is actually drawn, so that the two readings differ
                // by the transform and by nothing else.
                Vector3 pa = Vector3.Transform(submesh.Positions[a], meshToLocal);
                Vector3 pb = Vector3.Transform(submesh.Positions[b], meshToLocal);
                Vector3 pc = Vector3.Transform(submesh.Positions[c], meshToLocal);

                Vector3 face = Vector3.Cross(pb - pa, pc - pa);
                Vector3 authored = submesh.Normals[a] + submesh.Normals[b] + submesh.Normals[c];

                if (face.LengthSquared() < 1e-12f || authored.LengthSquared() < 1e-12f)
                {
                    continue;
                }

                Vector3 placed = Vector3.TransformNormal(authored, meshToLocal);

                if (placed.LengthSquared() < 1e-12f)
                {
                    continue;
                }

                face = Vector3.Normalize(face);

                asIs += Math.Abs(Vector3.Dot(face, Vector3.Normalize(authored)));
                transformed += Math.Abs(Vector3.Dot(face, Vector3.Normalize(placed)));
                counted++;
            }
        }

        return counted == 0
            ? new Reading(0, 0, 0)
            : new Reading(asIs / counted, transformed / counted, counted);
    }
}
