// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Numerics;
using GK3Reborn.Formats.Models;
using GK3Reborn.Formats.Scenes;

namespace GK3Reborn.Game;

/// <summary>What is in the air of a room besides the birds, and how much of it.</summary>
/// <param name="Insects">How many insects are about, at most. Nought for a room with no trees.</param>
/// <param name="Motes">How many specks of dust drift round the camera. Nought where no sun reaches.</param>
public readonly record struct Drift(int Insects, int Motes)
{
    /// <summary>Nothing drifts.</summary>
    public static Drift None { get; }

    /// <summary>Whether anything does.</summary>
    public bool Any => Insects > 0 || Motes > 0;
}

/// <summary>A tree's crown, as a box the insects gather under.</summary>
/// <param name="Least">Its lower corner, in world space.</param>
/// <param name="Most">Its upper corner.</param>
/// <param name="Conifer">
/// Whether it is a pine or a cypress rather than a broadleaf.
/// </param>
public readonly record struct Crown(Vector3 Least, Vector3 Most, bool Conifer)
{
    /// <summary>How much ground it stands over, in square units.</summary>
    public float Footprint =>
        MathF.Max(Most.X - Least.X, 1f) * MathF.Max(Most.Z - Least.Z, 1f);

    /// <summary>The middle of it.</summary>
    public Vector3 Centre => (Least + Most) / 2f;
}

/// <summary>
/// Which rooms have insects and dust in the air, and where the insects gather.
/// </summary>
public static class SceneDrift
{
    /// <summary>How many insects a wooded room may have about at once.</summary>
    private const int InsectsAbout = 24;

    /// <summary>How many specks of dust an outdoor room keeps round the camera.</summary>
    private const int DustAloft = 180;

    /// <summary>The smallest card that is a tree, in scene units across.</summary>
    private const float SmallestCrown = 30f;

    /// <summary>
    /// The sprites that are broadleaf trees. The same names <c>enhanced/trees/trees.json</c>
    /// grows a broadleaf, a maple or a dark broadleaf over; kept here so a room with no tree
    /// library still knows where its trees are.
    /// </summary>
    private static readonly HashSet<string> Broadleaves = new(StringComparer.OrdinalIgnoreCase)
    {
        "TREE00", "TREE01", "TREE02", "BUSHYTREESIDE1", "BUSHYTREETOP1",
        "MAPLESIDE1", "MAPLETOP1", "MAPLE", "MAPLE1TRILEAF",
        "WOODTREE3", "MAGENTREE",
    };

    /// <summary>The sprites that are conifers, by the same manifest.</summary>
    private static readonly HashSet<string> Conifers = new(StringComparer.OrdinalIgnoreCase)
    {
        "PINE2", "PINE2FLAT", "TALLPINE", "ARMPINE", "TREE03", "TREE04", "TREE05", "TREE06",
    };

    /// <summary>What drifts through a room at a point in the story.</summary>
    /// <param name="when">
    /// Where the story stands. A caller with no story state gets the daylight answer, for
    /// the reason <see cref="SceneFog.For"/> gives.
    /// </param>
    /// <param name="crowns">How many tree crowns the room has, from <see cref="Crowns"/>.</param>
    /// <param name="sunlit">Whether the sun reaches the room: it has one, over it or through a window.</param>
    /// <returns>What drifts, or <see cref="Drift.None"/>.</returns>
    public static Drift For(Timeblock? when, int crowns, bool sunlit)
    {
        if (when is { } hour && !SceneBirds.IsDaylight(hour))
        {
            return Drift.None;
        }

        return new Drift(crowns > 0 ? InsectsAbout : 0, sunlit ? DustAloft : 0);
    }

    /// <summary>Something stable about a room, for seeding what drifts through it.</summary>
    /// <param name="scene">The scene's name.</param>
    /// <returns>A hash of the name, the same on every machine and every run.</returns>
    public static ulong Seed(string? scene)
    {
        ulong hash = 14695981039346656037UL;

        foreach (char c in (scene ?? string.Empty).ToUpperInvariant())
        {
            hash ^= c;
            hash *= 1099511628211UL;
        }

        return hash ^ 0x4C454146UL;
    }

    /// <summary>Whether a texture is a picture of foliage, and of which kind.</summary>
    /// <param name="texture">The texture's name, with or without an extension.</param>
    /// <param name="conifer">Whether it is a pine or a cypress rather than a broadleaf.</param>
    /// <returns>True for foliage, false for anything else.</returns>
    public static bool IsFoliage(string texture, out bool conifer)
    {
        string plain = Path.GetFileNameWithoutExtension(texture);

        conifer = Conifers.Contains(plain);

        return conifer || Broadleaves.Contains(plain);
    }

    /// <summary>
    /// The tree crowns in a room: every foliage card the room draws itself, and every prop
    /// that is a picture of a tree.
    /// </summary>
    /// <param name="room">The room's geometry, or null.</param>
    /// <param name="props">The models placed in it, or null.</param>
    /// <returns>The crowns, largest first. A room's copy and a prop's copy of the same tree
    /// both count, which does no harm.</returns>
    public static IReadOnlyList<Crown> Crowns(BspFile? room, IReadOnlyList<PlacedModel>? props)
    {
        var found = new List<Crown>();

        if (room is not null)
        {
            // The drawn face each polygon came off, put back together by surface: a card
            // arrives from the BSP splitter in pieces, and each piece is not a tree.
            var pieces = new Dictionary<int, (Vector3 Least, Vector3 Most, bool Conifer)>();

            foreach (BspPolygon polygon in room.Polygons)
            {
                if (polygon.SurfaceIndex < 0 || polygon.SurfaceIndex >= room.Surfaces.Count)
                {
                    continue;
                }

                if (!IsFoliage(room.Surfaces[polygon.SurfaceIndex].TextureName, out bool conifer))
                {
                    continue;
                }

                var least = new Vector3(float.MaxValue);
                var most = new Vector3(float.MinValue);

                for (int at = 0; at < polygon.VertexIndexCount; at++)
                {
                    int index = polygon.VertexIndexOffset + at;

                    if (index < 0 || index >= room.VertexIndices.Length)
                    {
                        continue;
                    }

                    ushort vertex = room.VertexIndices[index];

                    if (vertex < room.Vertices.Length)
                    {
                        least = Vector3.Min(least, room.Vertices[vertex]);
                        most = Vector3.Max(most, room.Vertices[vertex]);
                    }
                }

                if (least.X > most.X)
                {
                    continue;
                }

                pieces[polygon.SurfaceIndex] = pieces.TryGetValue(polygon.SurfaceIndex, out var had)
                    ? (Vector3.Min(had.Least, least), Vector3.Max(had.Most, most), conifer)
                    : (least, most, conifer);
            }

            foreach ((Vector3 least, Vector3 most, bool conifer) in pieces.Values)
            {
                Keep(found, least, most, conifer);
            }
        }

        if (props is not null)
        {
            foreach (PlacedModel prop in props)
            {
                if (prop.Kind != PlacedModelKind.Prop)
                {
                    continue;
                }

                var least = new Vector3(float.MaxValue);
                var most = new Vector3(float.MinValue);
                bool conifer = false;
                bool any = false;

                foreach (ModMesh mesh in prop.Model.Meshes)
                {
                    foreach (ModSubmesh submesh in mesh.Submeshes)
                    {
                        if (submesh.Positions.Length == 0 ||
                            !IsFoliage(submesh.TextureName, out bool needles))
                        {
                            continue;
                        }

                        Matrix4x4 standing = mesh.MeshToLocal * prop.Transform;

                        foreach (Vector3 position in submesh.Positions)
                        {
                            Vector3 placed = Vector3.Transform(position, standing);
                            least = Vector3.Min(least, placed);
                            most = Vector3.Max(most, placed);
                        }

                        conifer |= needles;
                        any = true;
                    }
                }

                if (any)
                {
                    Keep(found, least, most, conifer);
                }
            }
        }

        found.Sort((a, b) => b.Footprint.CompareTo(a.Footprint));

        return found;
    }

    private static void Keep(List<Crown> into, Vector3 least, Vector3 most, bool conifer)
    {
        // Too small to be a tree, or a strip laid flat on the ground: a backdrop of painted
        // hillside is wide, thin and has nothing under it.
        if (most.X - least.X < SmallestCrown && most.Z - least.Z < SmallestCrown)
        {
            return;
        }

        if (most.Y - least.Y < SmallestCrown * 0.5f)
        {
            return;
        }

        into.Add(new Crown(least, most, conifer));
    }
}
