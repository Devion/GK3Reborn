// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Globalization;
using System.Numerics;
using GK3Reborn.Formats.Models;
using GK3Reborn.Foundation.Diagnostics;

namespace GK3Reborn.Formats.Scenes;

/// <summary>One corner of a replacement triangle, with everything needed to draw it.</summary>
/// <param name="Position">Where it is, in room space.</param>
/// <param name="Normal">The surface normal there.</param>
/// <param name="TexCoord">Its texture coordinate, in the original surface's mapping.</param>
public readonly record struct SceneVertex(Vector3 Position, Vector3 Normal, Vector2 TexCoord);

/// <summary>One triangle of replacement geometry, tagged with the surface it stands for.</summary>
/// <param name="A">First corner.</param>
/// <param name="B">Second corner.</param>
/// <param name="C">Third corner.</param>
/// <param name="Surface">
/// Index of the original surface this triangle belongs to, which decides its texture, its
/// lightmap, its flags and whether it is drawn at all.
/// </param>
public readonly record struct SceneTriangle(
    SceneVertex A, SceneVertex B, SceneVertex C, int Surface);

/// <summary>Replacement geometry for one of a room's named objects.</summary>
public sealed record SceneObjectGeometry
{
    /// <summary>The object's index in the room's name table.</summary>
    public required int ObjectIndex { get; init; }

    /// <summary>The object's name, as the room records it.</summary>
    public required string Name { get; init; }

    /// <summary>Its triangles.</summary>
    public required IReadOnlyList<SceneTriangle> Triangles { get; init; }

    /// <summary>Which of the room's surfaces this replaces.</summary>
    public required IReadOnlySet<int> Surfaces { get; init; }
}

/// <summary>
/// Replacement geometry for some of a room's objects, and nothing else about the room.
/// </summary>
/// <remarks>
/// Deliberately partial. An overlay says "draw these objects from here instead"; every
/// object it does not mention is drawn from the original geometry exactly as before, and
/// so is every other thing a room is made of — its collision, its walk boundary, its
/// camera bounds, its lightmaps and its flags. See <c>docs/scene-geometry.md</c>.
/// </remarks>
public sealed record SceneOverlay
{
    /// <summary>The room this belongs to.</summary>
    public required string Room { get; init; }

    /// <summary>The objects it replaces, in the order they were read.</summary>
    public required IReadOnlyList<SceneObjectGeometry> Objects { get; init; }

    /// <summary>Total triangles across every object.</summary>
    public int TriangleCount => Objects.Sum(o => o.Triangles.Count);

    /// <summary>An overlay that replaces nothing.</summary>
    public static SceneOverlay Empty { get; } = new() { Room = string.Empty, Objects = [] };

    /// <summary>Whether it would change anything.</summary>
    public bool IsEmpty => Objects.Count == 0;
}

/// <summary>
/// Reads and writes a room's geometry as one glTF file per object, or one per room.
/// </summary>
/// <remarks>
/// <para>
/// <b>What the surface index is doing in a material name.</b> A room's geometry can be
/// improved outside the engine — bevelled, subdivided, remodelled — but only if every
/// triangle that comes back can still be matched to the surface it came from. The surface
/// is what carries the texture, the lightmap's offset and scale, and the flags that say
/// whether the thing is self-lit or casts a shadow; a triangle that has lost its surface
/// has lost its lighting, and a room lit by nothing is not an improvement.
/// </para>
/// <para>
/// glTF has several places to hang an identifier and only one of them survives a
/// modelling tool: <b>the material name</b>. Face-to-material assignment is preserved
/// through every operation that matters — bevel, subdivision, decimation, separating a
/// mesh, joining two — because that assignment is what a modeller is manipulating.
/// Custom vertex attributes are interpolated into nonsense by the first bevel, node
/// extras are dropped by several exporters, and face attributes have nowhere to live in
/// glTF at all. So a surface is written as <c>TEXTURE#index</c>, and the picture itself
/// is shared between every material that names it.
/// </para>
/// <para>
/// Object identity is <em>derived</em> rather than carried: every surface knows which
/// object owns it, so grouping the triangles by surface recovers the objects even when a
/// tool has renamed, split or joined the meshes. Node names are written for a person to
/// read and nothing reads them back.
/// </para>
/// </remarks>
public static class SceneObjectGlb
{
    /// <summary>What separates a texture's name from the surface index after it.</summary>
    /// <remarks>
    /// Not a character any of the corpus's 1,786 texture names contains, and legal in a
    /// material name in every tool this has to pass through.
    /// </remarks>
    public const char SurfaceSeparator = '#';

    /// <summary>
    /// The angle beyond which two faces meeting at a vertex are a crease, in degrees.
    /// </summary>
    /// <remarks>
    /// Scene geometry has no normals of its own — the original shades every triangle flat
    /// — so they are reconstructed here, and reconstructing them badly is worse than not
    /// reconstructing them. Forty degrees keeps a box a box and lets a lathed vase read as
    /// a curve, which is the same threshold <c>ObjectRounding</c> reaches
    /// for and one step below the 60° an eight-sided bell needs.
    /// </remarks>
    public const float DefaultCrease = 40f;

    /// <summary>Encodes one of a room's objects as a glTF binary.</summary>
    /// <param name="scene">The room.</param>
    /// <param name="objectIndex">Which of its objects.</param>
    /// <param name="texturePathPrefix">Relative path prepended to texture file names.</param>
    /// <param name="crease">Angle beyond which a shared edge shades as a crease.</param>
    /// <returns>The complete GLB file, or null when the object draws nothing.</returns>
    public static byte[]? Encode(
        BspFile scene,
        int objectIndex,
        string texturePathPrefix = "../../../textures/",
        float crease = DefaultCrease)
    {
        ArgumentNullException.ThrowIfNull(scene);

        ModMesh? mesh = MeshFor(scene, objectIndex, crease);

        return mesh is null
            ? null
            : GlbWriter.Encode(
                ModFile.FromMeshes(NameOf(scene, objectIndex), [mesh]), texturePathPrefix);
    }

    /// <summary>Encodes a whole room, one node per object.</summary>
    /// <param name="scene">The room.</param>
    /// <param name="objects">
    /// Which objects to write, by index, or null for every object that draws anything.
    /// </param>
    /// <param name="texturePathPrefix">Relative path prepended to texture file names.</param>
    /// <param name="crease">Angle beyond which a shared edge shades as a crease.</param>
    /// <returns>The complete GLB file.</returns>
    public static byte[] EncodeRoom(
        BspFile scene,
        IReadOnlyCollection<int>? objects = null,
        string texturePathPrefix = "../textures/",
        float crease = DefaultCrease)
    {
        ArgumentNullException.ThrowIfNull(scene);

        List<ModMesh> meshes = [];

        for (int index = 0; index < scene.ObjectNames.Count; index++)
        {
            if (objects is not null && !objects.Contains(index))
            {
                continue;
            }

            if (MeshFor(scene, index, crease) is { } mesh)
            {
                meshes.Add(mesh);
            }
        }

        return GlbWriter.Encode(ModFile.FromMeshes(scene.Name, meshes), texturePathPrefix);
    }

    /// <summary>Writes geometry that has already been read back, keeping its surfaces.</summary>
    /// <param name="room">Name for the produced file.</param>
    /// <param name="scene">The room the surfaces belong to, for their texture names.</param>
    /// <param name="objects">The geometry to write.</param>
    /// <param name="texturePathPrefix">Relative path prepended to texture file names.</param>
    /// <returns>The complete GLB file.</returns>
    /// <remarks>
    /// What the composer uses: it reads a directory of objects that a modelling tool has
    /// been over and writes the one file per room the game reads. Triangles are grouped
    /// back into a primitive per surface, which is the form they were handed out in.
    /// </remarks>
    public static byte[] EncodeOverlay(
        string room,
        BspFile scene,
        IReadOnlyList<SceneObjectGeometry> objects,
        string texturePathPrefix = "../textures/")
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(objects);

        List<ModMesh> meshes = [];

        foreach (SceneObjectGeometry piece in objects)
        {
            List<ModSubmesh> submeshes = [];

            foreach (IGrouping<int, SceneTriangle> group in
                     piece.Triangles.GroupBy(t => t.Surface).OrderBy(g => g.Key))
            {
                if (group.Key < 0 || group.Key >= scene.Surfaces.Count)
                {
                    continue;
                }

                List<Vector3> positions = [];
                List<Vector3> normals = [];
                List<Vector2> texCoords = [];
                List<ushort> indices = [];
                Dictionary<SceneVertex, ushort> seen = [];

                foreach (SceneTriangle triangle in group)
                {
                    foreach (SceneVertex corner in
                             (ReadOnlySpan<SceneVertex>)[triangle.A, triangle.B, triangle.C])
                    {
                        if (!seen.TryGetValue(corner, out ushort at))
                        {
                            if (positions.Count > ushort.MaxValue - 3)
                            {
                                continue;
                            }

                            at = (ushort)positions.Count;
                            seen[corner] = at;
                            positions.Add(corner.Position);
                            normals.Add(corner.Normal);
                            texCoords.Add(corner.TexCoord);
                        }

                        indices.Add(at);
                    }
                }

                if (indices.Count == 0)
                {
                    continue;
                }

                submeshes.Add(new ModSubmesh
                {
                    TextureName = scene.Surfaces[group.Key].TextureName,
                    MaterialName = MaterialNameFor(scene.Surfaces[group.Key].TextureName, group.Key),
                    Color = (255, 255, 255),
                    Positions = [.. positions],
                    Normals = [.. normals],
                    TexCoords = [.. texCoords],
                    Indices = [.. indices],
                });
            }

            if (submeshes.Count > 0)
            {
                meshes.Add(new ModMesh
                {
                    Name = piece.Name,
                    MeshToLocal = Matrix4x4.Identity,
                    BoundsMin = Vector3.Zero,
                    BoundsMax = Vector3.Zero,
                    Submeshes = submeshes,
                });
            }
        }

        return GlbWriter.Encode(ModFile.FromMeshes(room, meshes), texturePathPrefix);
    }

    /// <summary>Reads replacement geometry back, matching it to the room it belongs to.</summary>
    /// <param name="glb">The file's bytes.</param>
    /// <param name="scene">The room whose surfaces the material names index.</param>
    /// <param name="name">Name used in diagnostics.</param>
    /// <param name="diagnostics">Receives what could not be matched.</param>
    /// <returns>The overlay, which is empty when nothing in the file could be matched.</returns>
    /// <remarks>
    /// Triangles whose material names no surface of this room, or names a surface that
    /// belongs to a different room's numbering, are dropped and counted rather than
    /// guessed at. A wrong surface index is a wrong lightmap, and a wrong lightmap is a
    /// wall lit like a floor.
    /// </remarks>
    public static SceneOverlay Read(
        ReadOnlySpan<byte> glb, BspFile scene, string name, DiagnosticBag? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(scene);

        ModFile? model = GlbReader.TryParse(glb, name, diagnostics);

        return model is null ? SceneOverlay.Empty : From(model, scene, name, diagnostics);
    }

    /// <summary>Turns already-parsed glTF into an overlay.</summary>
    /// <param name="model">The parsed file.</param>
    /// <param name="scene">The room whose surfaces the material names index.</param>
    /// <param name="name">Name used in diagnostics.</param>
    /// <param name="diagnostics">Receives what could not be matched.</param>
    /// <returns>The overlay.</returns>
    public static SceneOverlay From(
        ModFile model, BspFile scene, string name, DiagnosticBag? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(scene);

        // Grouped by the object each surface belongs to rather than by the node the
        // triangles arrived in: a tool is free to split a chair into its legs, and the
        // surfaces still say it is one chair.
        Dictionary<int, List<SceneTriangle>> byObject = [];
        Dictionary<int, HashSet<int>> surfacesOf = [];
        int unmatched = 0;

        foreach (ModMesh mesh in model.Meshes)
        {
            foreach (ModSubmesh submesh in mesh.Submeshes)
            {
                if (!TrySurfaceOf(submesh.TextureName, out int surface) ||
                    surface < 0 || surface >= scene.Surfaces.Count)
                {
                    unmatched += submesh.Indices.Length / 3;
                    continue;
                }

                int owner = scene.Surfaces[surface].ObjectIndex;

                if (!byObject.TryGetValue(owner, out List<SceneTriangle>? triangles))
                {
                    triangles = [];
                    byObject[owner] = triangles;
                    surfacesOf[owner] = [];
                }

                surfacesOf[owner].Add(surface);

                for (int at = 0; at + 2 < submesh.Indices.Length; at += 3)
                {
                    triangles.Add(new SceneTriangle(
                        CornerOf(submesh, mesh.MeshToLocal, submesh.Indices[at]),
                        CornerOf(submesh, mesh.MeshToLocal, submesh.Indices[at + 1]),
                        CornerOf(submesh, mesh.MeshToLocal, submesh.Indices[at + 2]),
                        surface));
                }
            }
        }

        if (unmatched > 0)
        {
            diagnostics?.Add(new Diagnostic(
                "GK3R1140",
                DiagnosticSeverity.Warning,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{unmatched} triangle(s) in {name} name no surface of {scene.Name} and " +
                    $"were dropped. A material must be named TEXTURE{SurfaceSeparator}index " +
                    $"for the room to know which lightmap and flags the triangle carries; " +
                    $"see docs/scene-geometry.md."),
                name));
        }

        List<SceneObjectGeometry> objects = [];

        foreach ((int owner, List<SceneTriangle> triangles) in byObject.OrderBy(p => p.Key))
        {
            if (triangles.Count == 0)
            {
                continue;
            }

            objects.Add(new SceneObjectGeometry
            {
                ObjectIndex = owner,
                Name = NameOf(scene, owner),
                Triangles = triangles,
                Surfaces = surfacesOf[owner],
            });
        }

        return new SceneOverlay { Room = scene.Name, Objects = objects };
    }

    /// <summary>
    /// A name for one object's geometry that depends on the geometry and nothing else.
    /// </summary>
    /// <param name="piece">The geometry.</param>
    /// <returns>The hash, lower-case hex.</returns>
    /// <remarks>
    /// <para>
    /// What lets a room share a shape with the eight other rooms that are the same room at
    /// a different hour. It covers positions, normals and texture coordinates exactly, and
    /// covers which of the object's own surfaces each triangle belongs to as an ordinal
    /// rather than as an index — the surface numbering is the thing that differs between
    /// those rooms, and it lives in the placement instead.
    /// </para>
    /// <para>
    /// <b>Order-invariant and quantised, because the tool that produces the geometry is
    /// neither.</b> Blender given byte-identical input twice writes two meshes that agree
    /// on every position and normal and differ in the last bit of an interpolated texture
    /// coordinate — 1.55678988 against 1.55678999 — which is float arithmetic and not a
    /// defect. Hashing the exact bytes therefore reported nine copies of one chafing dish
    /// as nine distinct shapes. So each triangle is rotated to start at its own lowest
    /// corner, which keeps its winding, the triangles are sorted, and every number is
    /// rounded to a step below what can be seen: a thousandth of a world unit, a
    /// hundredth of a degree, and a fiftieth of a texel on the largest texture in the set.
    /// Two shapes that agree to that are the same shape, and shipping either for both is
    /// a difference nobody can be shown.
    /// </para>
    /// </remarks>
    public static string ShapeOf(SceneObjectGeometry piece)
    {
        ArgumentNullException.ThrowIfNull(piece);

        int[] order = [.. piece.Surfaces.Order()];
        Dictionary<int, int> slots = [];

        for (int i = 0; i < order.Length; i++)
        {
            slots[order[i]] = i;
        }

        List<byte[]> rows = [];

        foreach (SceneTriangle triangle in piece.Triangles)
        {
            byte[] row = new byte[sizeof(int) + (3 * CornerBytes)];
            BitConverter.TryWriteBytes(row, slots.GetValueOrDefault(triangle.Surface, -1));

            SceneVertex[] corners = [triangle.A, triangle.B, triangle.C];
            byte[][] written = [.. corners.Select(Written)];

            // Rotate to the lowest corner rather than sort the three: sorting them would
            // make a triangle equal to its own mirror image, and a mirrored triangle faces
            // the other way.
            int first = 0;

            for (int i = 1; i < 3; i++)
            {
                if (written[i].AsSpan().SequenceCompareTo(written[first]) < 0)
                {
                    first = i;
                }
            }

            for (int i = 0; i < 3; i++)
            {
                written[(first + i) % 3].CopyTo(row, sizeof(int) + (i * CornerBytes));
            }

            rows.Add(row);
        }

        rows.Sort(static (a, b) => a.AsSpan().SequenceCompareTo(b));

        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(
            System.Security.Cryptography.HashAlgorithmName.SHA256);

        foreach (byte[] row in rows)
        {
            hash.AppendData(row);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>Bytes one corner contributes to a shape's hash.</summary>
    private const int CornerBytes = 8 * sizeof(long);

    /// <summary>Steps a position, a normal and a texture coordinate are rounded to.</summary>
    /// <remarks>
    /// A thousandth of a world unit is a thousandth of the smallest thing anybody
    /// modelled; a normal to four decimal places is a hundredth of a degree; and a
    /// sixty-five-thousandth of a texture unit is a fiftieth of a texel on a 2048 map,
    /// which is the largest anything in the enhanced set is packed at.
    /// </remarks>
    private const float PositionStep = 1024f;

    private const float NormalStep = 4096f;

    private const float TexCoordStep = 65536f;

    private static byte[] Written(SceneVertex corner)
    {
        byte[] bytes = new byte[CornerBytes];
        Span<long> cells =
        [
            Quantised(corner.Position.X, PositionStep),
            Quantised(corner.Position.Y, PositionStep),
            Quantised(corner.Position.Z, PositionStep),
            Quantised(corner.Normal.X, NormalStep),
            Quantised(corner.Normal.Y, NormalStep),
            Quantised(corner.Normal.Z, NormalStep),
            Quantised(corner.TexCoord.X, TexCoordStep),
            Quantised(corner.TexCoord.Y, TexCoordStep),
        ];

        for (int i = 0; i < cells.Length; i++)
        {
            BitConverter.TryWriteBytes(bytes.AsSpan(i * sizeof(long)), cells[i]);
        }

        return bytes;
    }

    /// <summary>One number, rounded to a step and made exact.</summary>
    private static long Quantised(float value, float step) =>
        float.IsFinite(value) ? (long)MathF.Round(value * step) : long.MinValue;

    /// <summary>What a shared shape's material is called.</summary>
    /// <param name="slot">Which of the object's own surfaces, counted from zero.</param>
    /// <returns>The material name.</returns>
    /// <remarks>
    /// A shape is geometry that several rooms draw and that nothing in particular owns, so
    /// its materials cannot name a surface: the same chair is surface 104 in one room and
    /// 88 in another. They name a position in the object's own surface list instead, and
    /// the room supplies the list. The separator is the same one, so one parser reads both.
    /// </remarks>
    public static string SlotNameFor(int slot) =>
        string.Create(CultureInfo.InvariantCulture, $"slot{SurfaceSeparator}{slot:D3}");

    /// <summary>Encodes one object's geometry as a shape no room owns.</summary>
    /// <param name="name">Name for the produced file.</param>
    /// <param name="piece">The geometry.</param>
    /// <param name="surfaces">
    /// The object's surfaces in the order the slots count them; receives the order used.
    /// </param>
    /// <returns>The complete GLB file.</returns>
    /// <remarks>
    /// No textures and no surface numbers: everything that varies between the rooms
    /// sharing this shape is left to the rooms. What is left is the thing that is actually
    /// the same — positions, normals and texture coordinates — which is why it can be
    /// shipped once and read once.
    /// </remarks>
    public static byte[] EncodeShape(
        string name, SceneObjectGeometry piece, out IReadOnlyList<int> surfaces)
    {
        ArgumentNullException.ThrowIfNull(piece);

        int[] order = [.. piece.Surfaces.Order()];
        surfaces = order;

        Dictionary<int, int> slots = [];

        for (int i = 0; i < order.Length; i++)
        {
            slots[order[i]] = i;
        }

        List<ModSubmesh> submeshes = [];

        foreach (IGrouping<int, SceneTriangle> group in
                 piece.Triangles.GroupBy(t => t.Surface).OrderBy(g => slots.GetValueOrDefault(g.Key, int.MaxValue)))
        {
            if (!slots.TryGetValue(group.Key, out int slot))
            {
                continue;
            }

            List<Vector3> positions = [];
            List<Vector3> normals = [];
            List<Vector2> texCoords = [];
            List<ushort> indices = [];
            Dictionary<SceneVertex, ushort> seen = [];

            foreach (SceneTriangle triangle in group)
            {
                foreach (SceneVertex corner in
                         (ReadOnlySpan<SceneVertex>)[triangle.A, triangle.B, triangle.C])
                {
                    if (!seen.TryGetValue(corner, out ushort at))
                    {
                        if (positions.Count > ushort.MaxValue - 3)
                        {
                            continue;
                        }

                        at = (ushort)positions.Count;
                        seen[corner] = at;
                        positions.Add(corner.Position);
                        normals.Add(corner.Normal);
                        texCoords.Add(corner.TexCoord);
                    }

                    indices.Add(at);
                }
            }

            if (indices.Count == 0)
            {
                continue;
            }

            submeshes.Add(new ModSubmesh
            {
                TextureName = string.Empty,
                MaterialName = SlotNameFor(slot),
                Color = (255, 255, 255),
                Positions = [.. positions],
                Normals = [.. normals],
                TexCoords = [.. texCoords],
                Indices = [.. indices],
            });
        }

        return GlbWriter.Encode(
            ModFile.FromMeshes(name, submeshes.Count == 0 ? [] : [new ModMesh
            {
                Name = piece.Name,
                MeshToLocal = Matrix4x4.Identity,
                BoundsMin = Vector3.Zero,
                BoundsMax = Vector3.Zero,
                Submeshes = submeshes,
            }]),
            texturePathPrefix: string.Empty);
    }

    /// <summary>Puts a shared shape back into the room that is drawing it.</summary>
    /// <param name="shape">The shape, as glTF read it.</param>
    /// <param name="scene">The room drawing it.</param>
    /// <param name="objectIndex">Which of the room's objects it stands for.</param>
    /// <param name="surfaces">
    /// The room's surface index for each of the shape's slots, in slot order.
    /// </param>
    /// <returns>The geometry, or null when a slot names no surface of this room.</returns>
    public static SceneObjectGeometry? Place(
        ModFile shape, BspFile scene, int objectIndex, IReadOnlyList<int> surfaces)
    {
        ArgumentNullException.ThrowIfNull(shape);
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(surfaces);

        List<SceneTriangle> triangles = [];
        HashSet<int> used = [];

        foreach (ModMesh mesh in shape.Meshes)
        {
            foreach (ModSubmesh submesh in mesh.Submeshes)
            {
                if (!TrySurfaceOf(submesh.TextureName, out int slot) ||
                    slot < 0 || slot >= surfaces.Count)
                {
                    return null;
                }

                int surface = surfaces[slot];

                if (surface < 0 || surface >= scene.Surfaces.Count ||
                    scene.Surfaces[surface].ObjectIndex != objectIndex)
                {
                    return null;
                }

                used.Add(surface);

                for (int at = 0; at + 2 < submesh.Indices.Length; at += 3)
                {
                    triangles.Add(new SceneTriangle(
                        CornerOf(submesh, mesh.MeshToLocal, submesh.Indices[at]),
                        CornerOf(submesh, mesh.MeshToLocal, submesh.Indices[at + 1]),
                        CornerOf(submesh, mesh.MeshToLocal, submesh.Indices[at + 2]),
                        surface));
                }
            }
        }

        return triangles.Count == 0
            ? null
            : new SceneObjectGeometry
            {
                ObjectIndex = objectIndex,
                Name = NameOf(scene, objectIndex),
                Triangles = triangles,
                Surfaces = used,
            };
    }

    /// <summary>What a surface's material is called.</summary>
    /// <param name="texture">The texture drawn on it.</param>
    /// <param name="surface">Its index in the room.</param>
    /// <returns>The material name.</returns>
    public static string MaterialNameFor(string texture, int surface) =>
        string.Create(CultureInfo.InvariantCulture, $"{texture}{SurfaceSeparator}{surface:D5}");

    /// <summary>Reads a surface index back out of a material name.</summary>
    /// <param name="material">The material name, which may carry a tool's own suffix.</param>
    /// <param name="surface">Receives the index.</param>
    /// <returns>True when the name carries one.</returns>
    /// <remarks>
    /// Tolerant of what tools do to a name on the way through: a duplicated datablock
    /// comes back as <c>NAME.001</c>, and an importer may have upper-cased it. Anything
    /// after the index that is not a plain suffix is refused rather than parsed loosely.
    /// </remarks>
    public static bool TrySurfaceOf(string? material, out int surface)
    {
        surface = -1;

        if (material is null)
        {
            return false;
        }

        int at = material.LastIndexOf(SurfaceSeparator);

        if (at < 0 || at + 1 >= material.Length)
        {
            return false;
        }

        ReadOnlySpan<char> tail = material.AsSpan(at + 1);

        // A tool that had to rename a duplicate appends `.001`; nothing else is allowed.
        int dot = tail.IndexOf('.');

        if (dot >= 0)
        {
            ReadOnlySpan<char> suffix = tail[(dot + 1)..];
            tail = tail[..dot];

            if (suffix.Length == 0 ||
                !int.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            {
                return false;
            }
        }

        // Reset on failure, because TryParse does not: it writes zero, and a caller that
        // trusted the index without the bool would put the triangle on surface zero — the
        // wrong lightmap, silently, on whichever surface the room happens to list first.
        if (int.TryParse(tail, NumberStyles.None, CultureInfo.InvariantCulture, out surface))
        {
            return true;
        }

        surface = -1;
        return false;
    }

    /// <summary>The name of one of a room's objects, or the empty string.</summary>
    /// <param name="scene">The room.</param>
    /// <param name="objectIndex">Which object.</param>
    /// <returns>Its name.</returns>
    public static string NameOf(BspFile scene, int objectIndex)
    {
        ArgumentNullException.ThrowIfNull(scene);

        return objectIndex >= 0 && objectIndex < scene.ObjectNames.Count
            ? scene.ObjectNames[objectIndex]
            : string.Empty;
    }

    /// <summary>Every surface one of a room's objects owns, in index order.</summary>
    /// <param name="scene">The room.</param>
    /// <param name="objectIndex">Which object.</param>
    /// <returns>The surface indices.</returns>
    public static IReadOnlyList<int> SurfacesOf(BspFile scene, int objectIndex)
    {
        ArgumentNullException.ThrowIfNull(scene);

        List<int> found = [];

        for (int index = 0; index < scene.Surfaces.Count; index++)
        {
            if (scene.Surfaces[index].ObjectIndex == objectIndex)
            {
                found.Add(index);
            }
        }

        return found;
    }

    private static SceneVertex CornerOf(ModSubmesh submesh, Matrix4x4 meshToLocal, ushort at)
    {
        Vector3 position = at < submesh.Positions.Length ? submesh.Positions[at] : Vector3.Zero;
        Vector3 normal = at < submesh.Normals.Length ? submesh.Normals[at] : Vector3.UnitY;

        if (meshToLocal != Matrix4x4.Identity)
        {
            position = Vector3.Transform(position, meshToLocal);
            normal = Vector3.TransformNormal(normal, meshToLocal);
        }

        return new SceneVertex(
            position,
            normal.LengthSquared() > 1e-12f ? Vector3.Normalize(normal) : Vector3.UnitY,
            at < submesh.TexCoords.Length ? submesh.TexCoords[at] : Vector2.Zero);
    }

    /// <summary>Builds one object's mesh, a submesh per surface.</summary>
    private static ModMesh? MeshFor(BspFile scene, int objectIndex, float crease)
    {
        IReadOnlyList<int> owned = SurfacesOf(scene, objectIndex);

        if (owned.Count == 0)
        {
            return null;
        }

        SceneObjectNormals normals = SceneObjectNormals.For(scene, objectIndex, crease);

        // Built in one pass over the object's triangles rather than one pass per surface,
        // because the face numbering the normals are addressed by only exists in that one
        // order. See SceneObjectNormals.Faces.
        Dictionary<int, Surfacing> building = [];

        foreach ((int face, ushort a, ushort b, ushort c, int surface) in
                 SceneObjectNormals.Faces(scene, objectIndex))
        {
            if (!building.TryGetValue(surface, out Surfacing? into))
            {
                into = new Surfacing();
                building[surface] = into;
            }

            // 16-bit indices are the model format's own limit, and a single surface of a
            // single object has never come near it. Refusing the triangle keeps the face
            // numbering intact, which breaking out of the loop would not.
            if (into.Positions.Count > ushort.MaxValue - 3)
            {
                continue;
            }

            foreach (ushort corner in (ReadOnlySpan<ushort>)[a, b, c])
            {
                int group = normals.GroupOf(face, corner);
                (ushort, int) key = (corner, group);

                if (!into.Remap.TryGetValue(key, out ushort at))
                {
                    at = (ushort)into.Positions.Count;
                    into.Remap[key] = at;
                    into.Positions.Add(scene.Vertices[corner]);
                    into.Normals.Add(normals.NormalOf(corner, group, face));
                    into.TexCoords.Add(scene.TexCoordFor(corner));
                }

                into.Indices.Add(at);
            }
        }

        List<ModSubmesh> submeshes = [];

        foreach (int surface in owned)
        {
            if (!building.TryGetValue(surface, out Surfacing? built) || built.Indices.Count == 0)
            {
                continue;
            }

            submeshes.Add(new ModSubmesh
            {
                TextureName = scene.Surfaces[surface].TextureName,
                MaterialName = MaterialNameFor(scene.Surfaces[surface].TextureName, surface),
                Color = (255, 255, 255),
                Positions = [.. built.Positions],
                Normals = [.. built.Normals],
                TexCoords = [.. built.TexCoords],
                Indices = [.. built.Indices],
            });
        }

        if (submeshes.Count == 0)
        {
            return null;
        }

        return new ModMesh
        {
            Name = NameOf(scene, objectIndex),
            MeshToLocal = Matrix4x4.Identity,
            BoundsMin = Vector3.Zero,
            BoundsMax = Vector3.Zero,
            Submeshes = submeshes,
        };
    }

    /// <summary>One surface's vertices while they are being gathered.</summary>
    private sealed class Surfacing
    {
        public Dictionary<(ushort Vertex, int Group), ushort> Remap { get; } = [];

        public List<Vector3> Positions { get; } = [];

        public List<Vector3> Normals { get; } = [];

        public List<Vector2> TexCoords { get; } = [];

        public List<ushort> Indices { get; } = [];
    }
}
