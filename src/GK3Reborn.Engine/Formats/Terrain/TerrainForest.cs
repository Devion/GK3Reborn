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
/// <remarks>
/// <para>
/// Six little-endian <c>float32</c> a tree — x, y, z, scale, rotation, kind — and no
/// header, the count being the length. That is the same arrangement
/// <c>&lt;set&gt;.heights.r32</c> takes, which carries its grid in the set's
/// <c>terrain.json</c> rather than in itself.
/// </para>
/// <para>
/// <b>It replaced JSON for the load time.</b> The offline scatter writes objects, and
/// deserialising them was the most expensive thing in an outdoor scene load: 92,000 trees
/// cost 95 ms for L'Ermitage and 129 ms for the worst set in the corpus, against 4 ms for
/// the same forest as floats, and 196 MB across the corpus against about 36 MB. The load
/// runs inside the screen fade and offers it no frame for the length of that.
/// </para>
/// <para>
/// The scatter's JSON is still what a person opens when a forest looks wrong; it stays in
/// the pipeline's working layout, and the publish step is what turns it into this.
/// </para>
/// </remarks>
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
    /// <remarks>
    /// Read a field at a time rather than block-copied, because the file is little-endian
    /// wherever it was written and wherever it is read. The cost of saying so is nothing
    /// beside what this replaced: on the machine that measured 95 ms of JSON, this is 4.
    /// </remarks>
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
