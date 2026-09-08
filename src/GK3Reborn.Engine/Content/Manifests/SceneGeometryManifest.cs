// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

namespace GK3Reborn.Content.Manifests;

/// <summary>One of a room's objects, and which shipped shape draws it.</summary>
public sealed record SceneGeometryObject
{
    /// <summary>Which of the room's objects this replaces.</summary>
    public required int Index { get; init; }

    /// <summary>Its name, for a person reading the manifest.</summary>
    public required string Name { get; init; }

    /// <summary>The shape that draws it, by its content hash.</summary>
    public required string Shape { get; init; }

    /// <summary>
    /// This room's surface index for each of the shape's slots, in slot order.
    /// </summary>
    public required IReadOnlyList<int> Surfaces { get; init; }

    /// <summary>Triangles in the shape.</summary>
    public required int TriangleCount { get; init; }
}

/// <summary>One room that has improved geometry to draw instead of its own.</summary>
public sealed record SceneGeometryRoom
{
    /// <summary>The room's name, which is its geometry file's name without extension.</summary>
    public required string Room { get; init; }

    /// <summary>
    /// SHA-256 of the original geometry the replacement was cut from.
    /// </summary>
    public required string SourceSha256 { get; init; }

    /// <summary>What those objects came to before they were improved.</summary>
    public required int OriginalTriangles { get; init; }

    /// <summary>The objects it replaces, and the shape each of them draws.</summary>
    public required IReadOnlyList<SceneGeometryObject> Objects { get; init; }

    /// <summary>What they come to now.</summary>
    public int TriangleCount => Objects.Sum(o => o.TriangleCount);

    /// <summary>How many of the room's objects it replaces.</summary>
    public int ObjectCount => Objects.Count;
}

/// <summary>
/// What improved scene geometry exists, room by room, over a pool of shared shapes.
/// </summary>
public sealed record SceneGeometryManifest
{
    /// <summary>Schema version.</summary>
    public required int SchemaVersion { get; init; }

    /// <summary>Pipeline stage that produced it.</summary>
    public required string Stage { get; init; }

    /// <summary>How many distinct shapes the rooms below draw between them.</summary>
    public required int ShapeCount { get; init; }

    /// <summary>The rooms, in the order they were composed.</summary>
    public required IReadOnlyList<SceneGeometryRoom> Rooms { get; init; }
}
