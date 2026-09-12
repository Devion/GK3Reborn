// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Numerics;
using GK3Reborn.Formats.Models;

namespace GK3Reborn.Game;

/// <summary>
/// A ring of falling water meeting still water, which is where a fountain throws spray.
/// </summary>
/// <param name="Centre">The middle of the still water it lands on, in the room's space.</param>
/// <param name="Radius">How far out from that middle the water comes down.</param>
/// <param name="Spread">How wide the ring of impact is, either side of that radius.</param>
/// <param name="Fall">
/// How far the water fell to get here, in world units. Spray is thrown as high as the drop
/// deserves: the jet dropping into the top basin makes less of it than the sheets coming
/// off that basin into the pool below.
/// </param>
public readonly record struct Fountain(Vector3 Centre, float Radius, float Spread, float Fall);

/// <summary>
/// Which two mesh groups of which model make one ring of spray.
/// </summary>
/// <param name="Model">The fountain.</param>
/// <param name="Pool">The mesh group that is the still water.</param>
/// <param name="Sheet">The mesh group whose water falls onto it.</param>
public readonly record struct FountainSource(PlacedModel Model, int Pool, int Sheet);

/// <summary>
/// Finds the fountains in a room, and where their water lands.
/// </summary>
public static class Fountains
{
    /// <summary>The still-water texture the fountain model is painted with.</summary>
    private static readonly string[] Still = ["WATER"];

    /// <summary>The two falling-water textures it is painted with.</summary>
    private static readonly string[] Falling = ["WATERFTN", "WATERRUNOFF"];

    /// <summary>Finds every ring of spray in a room.</summary>
    /// <param name="models">The models the scene loaded.</param>
    /// <returns>One entry per surface water falls onto; empty in every room but six.</returns>
    public static IReadOnlyList<FountainSource> In(IReadOnlyList<PlacedModel> models)
    {
        ArgumentNullException.ThrowIfNull(models);

        List<FountainSource> found = [];

        foreach (PlacedModel placed in models)
        {
            // Characters are never fountains, and asking would read the whole cast's
            // bounds in every room.
            if (placed.Kind != PlacedModelKind.Prop)
            {
                continue;
            }

            List<int> still = [];
            List<int> falling = [];

            for (int mesh = 0; mesh < placed.Model.Meshes.Count; mesh++)
            {
                foreach (ModSubmesh submesh in placed.Model.Meshes[mesh].Submeshes)
                {
                    if (Named(submesh.TextureName, Still))
                    {
                        still.Add(mesh);
                    }
                    else if (Named(submesh.TextureName, Falling))
                    {
                        falling.Add(mesh);
                    }
                }
            }

            if (still.Count == 0 || falling.Count == 0)
            {
                continue;
            }

            // Paired on the rest pose, which is where the artist built the thing and so
            // the one place the meshes are certainly in the arrangement they were designed
            // in. A clip carries the whole fountain about; it does not rearrange it.
            foreach (int pool in still)
            {
                if (Feeds(placed, pool, falling) is { } sheet)
                {
                    found.Add(new FountainSource(placed, pool, sheet));
                }
            }
        }

        return found;
    }

    /// <summary>Where a source's water is landing at this moment.</summary>
    /// <param name="source">The two mesh groups.</param>
    /// <returns>The ring.</returns>
    public static Fountain Ring(FountainSource source)
    {
        Box pool = Live(source.Model, source.Pool);
        Box sheet = Live(source.Model, source.Sheet);

        return new Fountain(
            new Vector3(pool.Centre.X, pool.Maximum.Y, pool.Centre.Z),
            sheet.Reach,

            // A tenth of the radius either side. The sheets are not perfect cylinders and
            // the water does not land on a line, and a ring with no width reads as a drawn
            // circle rather than as water coming down.
            MathF.Max(1f, sheet.Reach * 0.1f),
            MathF.Max(1f, sheet.Maximum.Y - pool.Maximum.Y));
    }

    /// <summary>A mesh group's extent in the room's space.</summary>
    private readonly record struct Box(Vector3 Minimum, Vector3 Maximum)
    {
        /// <summary>The middle of it.</summary>
        public Vector3 Centre => (Minimum + Maximum) * 0.5f;

        /// <summary>How far it reaches out from that middle, across the ground.</summary>
        public float Reach => ((Maximum.X - Minimum.X) + (Maximum.Z - Minimum.Z)) * 0.25f;
    }

    /// <summary>
    /// Which sheet of falling water comes down onto a surface of still water.
    /// </summary>
    /// <param name="placed">The fountain.</param>
    /// <param name="pool">The still-water mesh group.</param>
    /// <param name="falling">Every falling-water mesh group on the same model.</param>
    /// <returns>The mesh group that feeds it, or null when nothing comes down here.</returns>
    private static int? Feeds(PlacedModel placed, int pool, List<int> falling)
    {
        Box surface = Rest(placed, pool);

        int? best = null;
        float tightest = float.MaxValue;

        foreach (int mesh in falling)
        {
            Box sheet = Rest(placed, mesh);

            if (sheet.Minimum.Y > surface.Maximum.Y ||
                sheet.Maximum.Y <= surface.Maximum.Y ||
                sheet.Reach > surface.Reach)
            {
                continue;
            }

            // The tightest one, where a pool is under more than one: the jet inside the top
            // basin is narrower than the sheets falling past it.
            if (sheet.Reach < tightest)
            {
                tightest = sheet.Reach;
                best = mesh;
            }
        }

        return best;
    }

    /// <summary>A mesh group's bounds as the model was built.</summary>
    private static Box Rest(PlacedModel placed, int mesh) =>
        Bounds(placed.Model.Meshes[mesh], placed.Model.Meshes[mesh].MeshToLocal * placed.Transform);

    /// <summary>A mesh group's bounds where it is now.</summary>
    private static Box Live(PlacedModel placed, int mesh) =>
        Bounds(placed.Model.Meshes[mesh], placed.PoseOf(mesh) * placed.Standing);

    /// <summary>A mesh group's bounds, carried into the room's space.</summary>
    private static Box Bounds(ModMesh mesh, Matrix4x4 toWorld)
    {
        Vector3 minimum = new(float.MaxValue);
        Vector3 maximum = new(float.MinValue);

        // Every corner, because the mesh's own transform turns as well as moves it: the
        // fountain's water is authored lying down and stood up by its basis, so taking the
        // two stated corners through it would give a box with its height in the wrong axis.
        for (int corner = 0; corner < 8; corner++)
        {
            var at = new Vector3(
                (corner & 1) == 0 ? mesh.BoundsMin.X : mesh.BoundsMax.X,
                (corner & 2) == 0 ? mesh.BoundsMin.Y : mesh.BoundsMax.Y,
                (corner & 4) == 0 ? mesh.BoundsMin.Z : mesh.BoundsMax.Z);

            Vector3 world = Vector3.Transform(at, toWorld);

            minimum = Vector3.Min(minimum, world);
            maximum = Vector3.Max(maximum, world);
        }

        return new Box(minimum, maximum);
    }

    /// <summary>Whether a texture is one of a set, however the artists cased it.</summary>
    private static bool Named(string? texture, string[] names) =>
        texture is { Length: > 0 } painted &&
        Array.Exists(names, n => n.Equals(painted, StringComparison.OrdinalIgnoreCase));
}
