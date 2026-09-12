// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Numerics;

namespace GK3Reborn.Game;

/// <summary>
/// A lit cigarette somebody is holding, and the puff that goes with it.
/// </summary>
/// <param name="Lit">The cigarette itself, which the smoking clips carry to the mouth.</param>
/// <param name="Exhale">
/// The original's own puff, or null where the room places none. It is not drawn and is not
/// where the smoke is — see <see cref="Cigarettes"/> — it is read for <em>when</em>.
/// </param>
public readonly record struct Cigarette(PlacedModel Lit, PlacedModel? Exhale);

/// <summary>
/// Finds the lit cigarette in a room.
/// </summary>
public static class Cigarettes
{
    /// <summary>What the cigarette prop is called, in every room that has one.</summary>
    private const string LitName = "cigarette";

    /// <summary>And the original's puff.</summary>
    private const string ExhaleName = "smoke";

    /// <summary>
    /// The lit cigarettes a room places.
    /// </summary>
    /// <param name="models">The models the scene loaded.</param>
    /// <returns>One per cigarette, which is none in all but seven rooms.</returns>
    public static IReadOnlyList<Cigarette> In(IReadOnlyList<PlacedModel> models)
    {
        ArgumentNullException.ThrowIfNull(models);

        PlacedModel? exhale = Named(models, ExhaleName);
        List<Cigarette> found = [];

        foreach (PlacedModel model in models)
        {
            if (string.Equals(model.Name, LitName, StringComparison.OrdinalIgnoreCase))
            {
                found.Add(new Cigarette(model, exhale));
            }
        }

        return found;
    }

    /// <summary>
    /// Where the lit end is at this moment, and whether it is alight at all.
    /// </summary>
    /// <param name="cigarette">The cigarette.</param>
    /// <returns>Where it is, whether it is drawn, and whether it is being breathed out.</returns>
    public static (Vector3 At, bool Alight, bool Exhaling) Lit(Cigarette cigarette)
    {
        PlacedModel lit = cigarette.Lit;

        Vector3 minimum = new(float.MaxValue);
        Vector3 maximum = new(float.MinValue);

        for (int mesh = 0; mesh < lit.Model.Meshes.Count; mesh++)
        {
            Formats.Models.ModMesh group = lit.Model.Meshes[mesh];
            Matrix4x4 toWorld = lit.PoseOf(mesh) * lit.Standing;

            // Every corner, not the two stated ones: a mesh's own basis turns it as well as
            // moving it, so a box taken through the transform corner by corner is the only
            // one that still contains the thing afterwards. Same as Fountains.Bounds.
            for (int corner = 0; corner < 8; corner++)
            {
                var at = new Vector3(
                    (corner & 1) == 0 ? group.BoundsMin.X : group.BoundsMax.X,
                    (corner & 2) == 0 ? group.BoundsMin.Y : group.BoundsMax.Y,
                    (corner & 4) == 0 ? group.BoundsMin.Z : group.BoundsMax.Z);

                Vector3 world = Vector3.Transform(at, toWorld);

                minimum = Vector3.Min(minimum, world);
                maximum = Vector3.Max(maximum, world);
            }
        }

        return (
            (minimum + maximum) * 0.5f,
            lit.Visible,
            cigarette.Exhale?.Visible == true);
    }

    /// <summary>The model a room calls by a name, or null.</summary>
    private static PlacedModel? Named(IReadOnlyList<PlacedModel> models, string name)
    {
        foreach (PlacedModel model in models)
        {
            if (string.Equals(model.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return model;
            }
        }

        return null;
    }
}
