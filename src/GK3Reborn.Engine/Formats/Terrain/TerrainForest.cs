// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Buffers.Binary;

namespace GK3Reborn.Formats.Terrain;

/// <summary>
/// The backdrop forest's instance stream, as <c>publish_terrain.py</c> writes it.
/// </summary>
public static class TerrainForest
{
    /// <summary>How many floats one tree occupies: x, y, z, scale, rotation, kind.</summary>
    public const int FloatsPerTree = 6;

    /// <summary>How many bytes one tree occupies.</summary>
    public const int BytesPerTree = FloatsPerTree * sizeof(float);

    /// <summary>Reads an instance stream.</summary>
    /// <param name="stream">The file's bytes.</param>
    /// <returns>
    /// Six floats a tree, or null when the length is not a whole number of trees — which
    /// is the one thing a headerless format can check, and enough to catch a truncated
    /// file or something that is not a forest at all.
    /// </returns>
    public static float[]? Read(ReadOnlySpan<byte> stream)
    {
        if (stream.Length % BytesPerTree != 0)
        {
            return null;
        }

        float[] trees = new float[stream.Length / sizeof(float)];

        for (int i = 0; i < trees.Length; i++)
        {
            trees[i] = BinaryPrimitives.ReadSingleLittleEndian(
                stream[(i * sizeof(float))..]);
        }

        return trees;
    }
}
