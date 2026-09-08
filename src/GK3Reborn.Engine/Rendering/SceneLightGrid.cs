// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Numerics;

namespace GK3Reborn.Rendering;

/// <summary>A light, as the grid needs to see one.</summary>
/// <param name="Position">Where it is, in world space.</param>
/// <param name="Reach">How far it carries. Ignored where <paramref name="Everywhere"/> is set.</param>
/// <param name="Everywhere">
/// Whether it lights the whole scene regardless of distance — a sun, a moon, a sky fill.
/// </param>
/// <param name="Weight">
/// How much it matters, for ordering. Brightest first inside a cell, because the passes
/// that can only afford a few rays spend them on the front of the list.
/// </param>
public readonly record struct GridLight(
    Vector3 Position, float Reach, bool Everywhere, float Weight);

/// <summary>
/// Which lights reach which part of a room.
/// </summary>
public sealed class SceneLightGrid
{
    /// <summary>The most cells a room may be divided into.</summary>
    public const int MostCells = 16_384;

    /// <summary>The smallest cell worth making, in world units.</summary>
    public const float SmallestCell = 100f;

    /// <summary>How many lights one cell may list.</summary>
    public const int MostPerCell = 96;

    /// <summary>How many light references the whole grid may hold.</summary>
    public const int MostIndices = 1 << 20;

    private SceneLightGrid(
        Vector3 origin, float cell, (int X, int Y, int Z) counts, int[] offsets, int[] indices)
    {
        Origin = origin;
        Cell = cell;
        Counts = counts;
        Offsets = offsets;
        Indices = indices;
    }

    /// <summary>The corner the grid starts at.</summary>
    public Vector3 Origin { get; }

    /// <summary>How wide one cell is, in world units.</summary>
    public float Cell { get; }

    /// <summary>How many cells along each axis.</summary>
    public (int X, int Y, int Z) Counts { get; }

    /// <summary>Where each cell's list starts in <see cref="Indices"/>, with a final end.</summary>
    public int[] Offsets { get; }

    /// <summary>The light indices, cell by cell.</summary>
    public int[] Indices { get; }

    /// <summary>How many cells there are.</summary>
    public int CellCount => Counts.X * Counts.Y * Counts.Z;

    /// <summary>The longest list any cell holds.</summary>
    public int Busiest { get; private set; }

    /// <summary>How many cells wanted more lights than one may hold.</summary>
    public int Overfull { get; private set; }

    /// <summary>The average list length, which is what the shading loop costs.</summary>
    public double Average => CellCount > 0 ? (double)Indices.Length / CellCount : 0;

    /// <summary>Builds the grid for a room.</summary>
    /// <param name="lights">The rig, in the order it will be uploaded.</param>
    /// <param name="minimum">Lower corner of the room.</param>
    /// <param name="maximum">Upper corner.</param>
    /// <returns>The grid, which is one cell holding everything when the room has no extent.</returns>
    public static SceneLightGrid Build(
        IReadOnlyList<GridLight> lights, Vector3 minimum, Vector3 maximum)
    {
        ArgumentNullException.ThrowIfNull(lights);

        Vector3 span = Vector3.Max(maximum - minimum, new Vector3(1f));

        // A cell size that lands the whole room inside the budget, never finer than the
        // spacing that separates anything.
        double volume = (double)span.X * span.Y * span.Z;
        float cell = Math.Max(SmallestCell, (float)Math.Cbrt(volume / MostCells));

        (int X, int Y, int Z) counts = (
            Math.Max(1, (int)MathF.Ceiling(span.X / cell)),
            Math.Max(1, (int)MathF.Ceiling(span.Y / cell)),
            Math.Max(1, (int)MathF.Ceiling(span.Z / cell)));

        // Ceiling three times can overshoot the budget on a room that is long in every
        // axis. Coarsen until it fits rather than truncating the grid, which would leave
        // part of the room outside it.
        while ((long)counts.X * counts.Y * counts.Z > MostCells)
        {
            cell *= 1.5f;
            counts = (
                Math.Max(1, (int)MathF.Ceiling(span.X / cell)),
                Math.Max(1, (int)MathF.Ceiling(span.Y / cell)),
                Math.Max(1, (int)MathF.Ceiling(span.Z / cell)));
        }

        int cells = counts.X * counts.Y * counts.Z;
        var offsets = new int[cells + 1];
        List<int> indices = [];

        // The ones that are everywhere, heaviest first. Every cell starts with these.
        int[] everywhere =
        [
            .. Enumerable.Range(0, lights.Count)
                .Where(i => lights[i].Everywhere)
                .OrderByDescending(i => lights[i].Weight),
        ];

        var reaching = new List<int>(MostPerCell);
        int busiest = 0;
        int overfull = 0;

        for (int z = 0; z < counts.Z; z++)
        {
            for (int y = 0; y < counts.Y; y++)
            {
                for (int x = 0; x < counts.X; x++)
                {
                    int index = (((z * counts.Y) + y) * counts.X) + x;
                    offsets[index] = indices.Count;

                    Vector3 low = minimum + (new Vector3(x, y, z) * cell);
                    Vector3 high = low + new Vector3(cell);

                    reaching.Clear();

                    for (int i = 0; i < lights.Count; i++)
                    {
                        GridLight light = lights[i];

                        if (light.Everywhere)
                        {
                            continue;
                        }

                        // Nearest point of the cell to the light: the standard
                        // sphere-against-box test, and exact rather than a bounding-sphere
                        // approximation, which over a room's worth of cells is the
                        // difference between a short list and a useless one.
                        Vector3 nearest = Vector3.Clamp(light.Position, low, high);

                        if (Vector3.DistanceSquared(nearest, light.Position) <=
                            light.Reach * light.Reach)
                        {
                            reaching.Add(i);
                        }
                    }

                    reaching.Sort((a, b) => lights[b].Weight.CompareTo(lights[a].Weight));

                    int room = MostPerCell - everywhere.Length;

                    if (reaching.Count > room)
                    {
                        overfull++;
                    }

                    // The whole grid has a budget as well as each cell. Reaching it means
                    // a room far outside anything measured, and the honest failure is a
                    // shorter list rather than a buffer overrun.
                    room = Math.Min(room, Math.Max(0, MostIndices - indices.Count - everywhere.Length));

                    indices.AddRange(everywhere);
                    indices.AddRange(reaching.Take(Math.Max(0, room)));

                    busiest = Math.Max(busiest, indices.Count - offsets[index]);
                }
            }
        }

        offsets[cells] = indices.Count;

        return new SceneLightGrid(minimum, cell, counts, offsets, [.. indices])
        {
            Busiest = busiest,
            Overfull = overfull,
        };
    }

    /// <summary>Which cell a point is in.</summary>
    /// <param name="point">The point, in world space.</param>
    /// <returns>The cell's index, clamped to the grid.</returns>
    public int CellAt(Vector3 point)
    {
        Vector3 local = (point - Origin) / Cell;

        int x = Math.Clamp((int)MathF.Floor(local.X), 0, Counts.X - 1);
        int y = Math.Clamp((int)MathF.Floor(local.Y), 0, Counts.Y - 1);
        int z = Math.Clamp((int)MathF.Floor(local.Z), 0, Counts.Z - 1);

        return (((z * Counts.Y) + y) * Counts.X) + x;
    }
}
