// Copyright (C) 2026 the GK3Reborn authors.

using System.Numerics;
using GK3Reborn.Formats.Models;

namespace GK3Reborn.Game.Actors;

/// <summary>Which way a character's model is built to face.</summary>
public static class FacingArrow
{
    /// <summary>The characters whose arrow points from the other end.</summary>
    private static readonly string[] Reversed = ["MOS", "DEM"];

    /// <summary>What a model's arrow is called.</summary>
    /// <returns>The arrow's model name.</returns>
    /// <param name="model">The character's model name, such as eml.</param>
    public static string NameFor(string model)
    {
        ArgumentNullException.ThrowIfNull(model);

        return "DOR_" + model.ToUpperInvariant();
    }

    /// <summary>Which way a character's model is built to face, as a heading.</summary>
    /// <returns>The heading the model faces when its placement is the identity, or null when the arrow has no triangle to read.</returns>
    /// <param name="arrow">The DOR_ model, already parsed.</param>
    /// <param name="model">The character's model name, for the two that are read backwards.</param>
    public static float? Of(ModFile arrow, string model)
    {
        ArgumentNullException.ThrowIfNull(arrow);
        ArgumentNullException.ThrowIfNull(model);

        if (arrow.Meshes is not [ModMesh mesh, ..] || mesh.Submeshes is not [ModSubmesh triangle, ..] || triangle.Positions.Length < 3)
        {
            return null;
        }

        Vector3 first = Vector3.Transform(triangle.Positions[0], mesh.MeshToLocal);
        Vector3 second = Vector3.Transform(triangle.Positions[1], mesh.MeshToLocal);
        Vector3 third = Vector3.Transform(triangle.Positions[2], mesh.MeshToLocal);

        // The point of the arrow, away from the middle of the edge opposite it.
        (Vector3 tip, Vector3 back) = Reversed.Contains(model, StringComparer.OrdinalIgnoreCase) ? (third, (first + second) / 2f)
                : (first, (second + third) / 2f);

        Vector3 along = (tip - back) with { Y = 0 };

        return along.LengthSquared() < 1e-6f ? null : Navigation.Walker.Heading(along);
    }

    /// <summary>How far to turn a character's model to point it along a heading.</summary>
    /// <returns>The angle to turn the model about the vertical.</returns>
    /// <param name="heading">Where they should be looking, as the game measures a heading.</param>
    /// <param name="built">Which way the model is built to face, from , or null when nothing says.</param>
    public static float Rotation(float heading, float? built) => built is { } forward ? Navigation.Walker.Wrapped(heading - forward)
            : Navigation.Walker.Rotation(heading);

    /// <summary>Reads a heading back out of a placement built, which is not what answers: that one inverts the half turn and knows.</summary>
    /// <returns>The heading, as the game's data measures one.</returns>
    /// <param name="transform">The placement: a turn about the vertical and a move.</param>
    /// <param name="built">Which way the model is built to face, or null for the half turn.</param>
    public static float HeadingOf(System.Numerics.Matrix4x4 transform, float? built) => built is { } forward
            ? Navigation.Walker.Wrapped(MathF.Atan2(transform.M31, transform.M33) + forward) : Navigation.Walker.HeadingOf(transform);
}
