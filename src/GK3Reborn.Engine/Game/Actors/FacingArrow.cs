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
/// <remarks>
/// <para>
/// GK3 ships an invisible arrow per character — <c>DOR_EML</c>, <c>DOR_EST</c>,
/// <c>DOR_LH2</c>, 23 of them — whose three vertices point the way that model faces. The
/// original loads one beside every actor, hides it, keeps it turned with them, and reads the
/// character's facing straight off it: <c>GKActor::GetModelFacingDirection</c>.
/// </para>
/// <para>
/// <b>Why it matters more than it looks.</b> Everything else in this engine assumes a
/// character's model is built facing −Z, and turns it by "heading plus a half turn" —
/// <see cref="Navigation.Walker.Rotation"/>. That is true of most of them and is not a rule.
/// The reference never assumes it: <c>SetModelRotationToActorRotation</c> measures where the
/// model is currently facing and rotates it by the <em>difference</em> from where it should
/// be. A model built the other way round is turned correctly there and is turned end for end
/// here, which is the museum's Estelle and Lady Howard standing back to back.
/// </para>
/// <para>
/// The tip is the first vertex for nearly every character. The reference's own comment on
/// the exception is worth keeping: <em>"OF COURSE these points aren't consistent...Mosely
/// uses different ones"</em> — <c>MOS</c> and <c>DEM</c> put the tip third.
/// </para>
/// </remarks>
public static class FacingArrow
{
    /// <summary>The characters whose arrow points from the other end.</summary>
    /// <remarks>
    /// Two of them, named in the reference and nowhere in the data. An arrow read from the
    /// wrong end is a character facing exactly backwards, which is the failure this whole
    /// class exists to stop, so the exception is worth carrying by name.
    /// </remarks>
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
    /// <remarks>
    /// In the model's own space, with no placement applied, so what comes back is a property
    /// of the character rather than of where they are standing. Turning that model to a
    /// heading is then a matter of the difference between the two, which is what the
    /// reference does every frame and what this lets a placement do once.
    /// </remarks>
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
    /// <remarks>
    /// The difference between the two, which is the reference's own arithmetic. Without an
    /// arrow it falls back to the half turn every other part of this engine assumes, so a
    /// character with no <c>DOR_</c> model behaves exactly as before rather than differently.
    /// </remarks>
    public static float Rotation(float heading, float? built) =>
        built is { } forward
            ? Navigation.Walker.Wrapped(heading - forward)
            : Navigation.Walker.Rotation(heading);
}
