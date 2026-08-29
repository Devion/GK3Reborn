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
    /// <remarks>
    /// The whole of what a room adds to a shape it shares. A chair is surfaces 104, 105
    /// and 106 in one room and 88, 89 and 90 in another, while the triangles are the same
    /// triangles; putting the numbering here is what lets the triangles be shipped once.
    /// </remarks>
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
    /// <remarks>
    /// Checked at load, and a mismatch refuses the whole room. A surface index is a
    /// position in a file: an overlay built against a different build of that file puts
    /// every lightmap on the wrong surface, and the result draws perfectly and is lit by
    /// somebody else's lighting.
    /// </remarks>
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
/// <remarks>
/// <para>
/// Optional in the strongest sense: a game with no manifest, no manifest entry for a
/// room, or no shape for one of its objects draws that much of the room exactly as it
/// shipped. Nothing here is load-bearing for collision, navigation, camera bounds or
/// lighting, all of which stay with the original geometry however much of the picture is
/// replaced.
/// </para>
/// <para>
/// <b>Shapes are shared because the corpus repeats itself.</b> A location has a geometry
/// file per timeblock — <c>DIN</c>, <c>DIN_302A</c> and <c>DIN_303P</c> are one dining
/// room lit three ways — and the furniture in them is the same furniture at the same
/// coordinates with a different surface numbering. Measured over the corpus, 2,721
/// improved objects are 2,054 distinct shapes: a fifth of the set was being shipped more
/// than once. Addressing a shape by the hash of its own geometry ships it once, reads it
/// once per session, and costs nothing to keep honest — two objects that stop being
/// identical stop sharing, without anybody having to notice.
/// </para>
/// </remarks>
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
