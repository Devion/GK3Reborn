// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Numerics;
using GK3Reborn.Formats.Models;

namespace GK3Reborn.Game.Actors;

/// <summary>
/// Which way a character's model is built to face.
/// </summary>
public static class FacingArrow
{
    /// <summary>The characters whose arrow points from the other end.</summary>
    private static readonly string[] Reversed = ["MOS", "DEM"];

    /// <summary>What a model's arrow is called.</summary>
    /// <param name="model">The character's model name, such as <c>eml</c>.</param>
    /// <returns>The arrow's model name.</returns>
    public static string NameFor(string model)
    {
        ArgumentNullException.ThrowIfNull(model);

        return "DOR_" + model.ToUpperInvariant();
    }

    /// <summary>
    /// Which way a character's model is built to face, as a heading.
    /// </summary>
    /// <param name="arrow">The <c>DOR_</c> model, already parsed.</param>
    /// <param name="model">The character's model name, for the two that are read backwards.</param>
    /// <returns>
    /// The heading the model faces when its placement is the identity, or null when the
    /// arrow has no triangle to read.
    /// </returns>
    public static float? Of(ModFile arrow, string model)
    {
        ArgumentNullException.ThrowIfNull(arrow);
        ArgumentNullException.ThrowIfNull(model);

        if (arrow.Meshes is not [ModMesh mesh, ..] ||
            mesh.Submeshes is not [ModSubmesh triangle, ..] ||
            triangle.Positions.Length < 3)
        {
            return null;
        }

        Vector3 first = Vector3.Transform(triangle.Positions[0], mesh.MeshToLocal);
        Vector3 second = Vector3.Transform(triangle.Positions[1], mesh.MeshToLocal);
        Vector3 third = Vector3.Transform(triangle.Positions[2], mesh.MeshToLocal);

        // The point of the arrow, away from the middle of the edge opposite it.
        (Vector3 tip, Vector3 back) =
            Reversed.Contains(model, StringComparer.OrdinalIgnoreCase)
                ? (third, (first + second) / 2f)
                : (first, (second + third) / 2f);

        Vector3 along = (tip - back) with { Y = 0 };

        return along.LengthSquared() < 1e-6f ? null : Navigation.Walker.Heading(along);
    }

    /// <summary>
    /// How far to turn a character's model to point it along a heading.
    /// </summary>
    /// <param name="heading">Where they should be looking, as the game measures a heading.</param>
    /// <param name="built">
    /// Which way the model is built to face, from <see cref="Of"/>, or null when nothing
    /// says.
    /// </param>
    /// <returns>The angle to turn the model about the vertical.</returns>
    public static float Rotation(float heading, float? built) =>
        built is { } forward
            ? Navigation.Walker.Wrapped(heading - forward)
            : Navigation.Walker.Rotation(heading);

    /// <summary>
    /// Reads a heading back out of a placement <see cref="Rotation"/> built, which is not
    /// what <see cref="Navigation.Walker.HeadingOf"/> answers: that one inverts the half
    /// turn and knows nothing about a model whose own arrow was measured.
    /// </summary>
    /// <param name="transform">The placement: a turn about the vertical and a move.</param>
    /// <param name="built">Which way the model is built to face, or null for the half turn.</param>
    /// <returns>The heading, as the game's data measures one.</returns>
    public static float HeadingOf(System.Numerics.Matrix4x4 transform, float? built) =>
        built is { } forward
            ? Navigation.Walker.Wrapped(MathF.Atan2(transform.M31, transform.M33) + forward)
            : Navigation.Walker.HeadingOf(transform);
}
