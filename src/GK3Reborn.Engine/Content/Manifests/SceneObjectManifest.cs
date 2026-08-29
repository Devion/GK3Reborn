// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Text.Json.Serialization;

namespace GK3Reborn.Content.Manifests;

/// <summary>What the geometry pipeline should do with one of a room's objects.</summary>
/// <remarks>
/// <para>
/// A room is not one thing. <c>din_chandilier</c> and <c>din_walls</c> are in the same
/// file, are made of the same kind of data, and want opposite treatments: one is a curved
/// object whose whole character is its silhouette, the other is flat panels that have to
/// keep meeting exactly. So the disposition is decided per object, and it decides which
/// modifier stack — if any — the object is put through.
/// </para>
/// <para>
/// Names carry more weight here than they do for models. A <c>.MOD</c> file's name is a
/// filename and lies routinely (see <c>ModelRoleManifest</c>); an object name inside a
/// room is an artist's own label for a part of that room — <c>cem_fountain</c>,
/// <c>dinchair03</c>, <c>mop_moped</c> — and there is no other declaration channel for
/// them at all. Every name-derived disposition is still gated on what the geometry
/// contains, and the reason is recorded, so a wrong call is visible rather than silent.
/// </para>
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<SceneObjectDisposition>))]
public enum SceneObjectDisposition
{
    /// <summary>Statues, fountains, vases, lamps, signs: bevelled and curved.</summary>
    [JsonStringEnumMemberName("ornament")]
    Ornament,

    /// <summary>Chairs, tables, crates, cabinets: bevelled, with weighted normals.</summary>
    [JsonStringEnumMemberName("furniture")]
    Furniture,

    /// <summary>Mopeds, vans, carts: bevelled and curved, at a larger width.</summary>
    [JsonStringEnumMemberName("vehicle")]
    Vehicle,

    /// <summary>Rocks and boulders: curved, and given relief where the material has it.</summary>
    [JsonStringEnumMemberName("rock")]
    Rock,

    /// <summary>
    /// Walls, floors, doorways, stairs: the edges are bevelled and the flats left alone.
    /// </summary>
    /// <remarks>
    /// The conservative case, and the one where a modifier stack does the most damage:
    /// walls and floors have to keep meeting exactly, and rounding an edge a wall abuts
    /// opens a visible seam. An angle-limited bevel touches only the edges that are
    /// already sharp and adds nothing across a flat panel.
    /// </remarks>
    [JsonStringEnumMemberName("architecture")]
    Architecture,

    /// <summary>The room's own floor. The engine already cuts relief into it at load.</summary>
    [JsonStringEnumMemberName("terrain")]
    Terrain,

    /// <summary>Trees and bushes, which are replaced by grown geometry instead.</summary>
    [JsonStringEnumMemberName("foliage")]
    Foliage,

    /// <summary>
    /// A painted view of somewhere else: a distant hillside, a street through a window.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Flat"/> because a backdrop is not always one plane —
    /// <c>hal_r33_gbkg</c> is 83 orientations over 786 triangles — and separate from
    /// everything else because refining one is worse than leaving it: the geometry is a
    /// stage flat whose whole job is to disappear behind its own picture, and rounding its
    /// edges puts a lit rim on something that is meant to read as distance.
    /// </remarks>
    [JsonStringEnumMemberName("backdrop")]
    Backdrop,

    /// <summary>
    /// A single plane: a card, a painted backdrop, a decal. There is no edge to round.
    /// </summary>
    [JsonStringEnumMemberName("flat")]
    Flat,

    /// <summary>A hit test, a shadow decal or a blocker. Never drawn, never touched.</summary>
    [JsonStringEnumMemberName("collision")]
    Collision,

    /// <summary>Nothing decided it. Skipped unless a person asks for it.</summary>
    [JsonStringEnumMemberName("review")]
    Review,
}

/// <summary>One of a room's objects, and what is known about it.</summary>
public sealed record SceneObjectRole
{
    /// <summary>Which of the room's objects this is.</summary>
    public required int Index { get; init; }

    /// <summary>Its name, as the room's own name table records it.</summary>
    public required string Name { get; init; }

    /// <summary>The file it was written to, relative to the room's directory.</summary>
    public required string File { get; init; }

    /// <summary>Which of the room's surfaces it owns.</summary>
    public required IReadOnlyList<int> Surfaces { get; init; }

    /// <summary>The textures drawn on it, in the order they were met.</summary>
    public required IReadOnlyList<string> Textures { get; init; }

    /// <summary>The material classes those textures were sorted into, where known.</summary>
    public required IReadOnlyList<string> Materials { get; init; }

    /// <summary>Triangles, once its polygons are fanned.</summary>
    public required int TriangleCount { get; init; }

    /// <summary>
    /// How many distinct plane orientations its faces sit on.
    /// </summary>
    /// <remarks>
    /// The single most useful number here, and the one that says what not to spend
    /// triangles on. One means the object is a flat card. Four is a box. Twelve and up on
    /// a small object means something lathed, which is what subdivision was made for.
    /// </remarks>
    public required int PlaneCount { get; init; }

    /// <summary>The longest edge of its bounding box, in world units.</summary>
    public required float Size { get; init; }

    /// <summary>The union of its surfaces' flags.</summary>
    public required uint Flags { get; init; }

    /// <summary>What a scene file declared about it, where anything did.</summary>
    public required IReadOnlyList<string> Roles { get; init; }

    /// <summary>The recommended treatment.</summary>
    public required SceneObjectDisposition Disposition { get; init; }

    /// <summary>Why that treatment, in a form a person can argue with.</summary>
    public required string Reason { get; init; }
}

/// <summary>Every object of one room.</summary>
public sealed record SceneObjectRoom
{
    /// <summary>The room's name, which is its geometry file's name without extension.</summary>
    public required string Room { get; init; }

    /// <summary>Directory the objects were written to, relative to the workspace.</summary>
    public required string Directory { get; init; }

    /// <summary>SHA-256 of the original geometry the objects were cut out of.</summary>
    /// <remarks>
    /// What tells a set apart from the room it claims to replace. Surface indices are
    /// positions in a file; extract from a different build of that file and every index
    /// in every material name means something else.
    /// </remarks>
    public required string SourceSha256 { get; init; }

    /// <summary>Surfaces in the room.</summary>
    public required int SurfaceCount { get; init; }

    /// <summary>Triangles in the room.</summary>
    public required int TriangleCount { get; init; }

    /// <summary>Its objects, in the room's own order.</summary>
    public required IReadOnlyList<SceneObjectRole> Objects { get; init; }
}

/// <summary>The scene-object manifest.</summary>
public sealed record SceneObjectManifest
{
    /// <summary>Schema version.</summary>
    public required int SchemaVersion { get; init; }

    /// <summary>Pipeline stage that produced it.</summary>
    public required string Stage { get; init; }

    /// <summary>Directory the archives were read from.</summary>
    public required string SourceRoot { get; init; }

    /// <summary>The crease angle the extracted normals were reconstructed with.</summary>
    public required float Crease { get; init; }

    /// <summary>Object counts by disposition, across every room.</summary>
    public required IReadOnlyDictionary<string, int> DispositionCounts { get; init; }

    /// <summary>The rooms, ordered by name.</summary>
    public required IReadOnlyList<SceneObjectRoom> Rooms { get; init; }
}
