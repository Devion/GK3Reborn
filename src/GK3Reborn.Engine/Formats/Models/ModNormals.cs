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
public static class ModNormals
{
    /// <summary>
    /// How well an authored normal has to match the surface before a group is allowed an
    /// opinion of its own.
    /// </summary>
    private const double Confident = 0.75;

    /// <summary>How far apart the two readings must be for a group to have decided.</summary>
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
    public static Matrix4x4 CorrectionFor(ModMesh mesh, bool model) =>
        AreLocal(mesh, model) && Matrix4x4.Invert(mesh.MeshToLocal, out Matrix4x4 inverse)
            ? inverse
            : Matrix4x4.Identity;

    /// <summary>Reads one mesh group both ways.</summary>
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
