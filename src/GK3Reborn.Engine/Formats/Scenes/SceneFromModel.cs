// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Numerics;
using GK3Reborn.Formats.Models;
using GK3Reborn.Foundation.Diagnostics;

namespace GK3Reborn.Formats.Scenes;

/// <summary>
/// Builds a room out of a model, for rooms the game never had.
/// </summary>
/// <remarks>
/// <para>
/// A room in GK3 is a <c>.BSP</c>, and there is no writer for one here — nor should there
/// be. What the rest of the engine actually asks a room for is what
/// <see cref="BspFile.FromParts"/> takes: named objects, surfaces that name a texture and
/// belong to an object, and polygons over shared vertices. That is a shape glTF can carry,
/// so a room can be built from a model without anybody writing 1999's file format.
/// </para>
/// <para>
/// This exists because the temple's second room — the elemental puzzle, cut before release —
/// still has its object list, its light rig, its textures and sixty-two lines of dialogue on
/// the disc, and no geometry at all. Everything except the shape survived. See
/// <c>docs/cut-content.md</c>.
/// </para>
/// <para>
/// <b>A room built this way is lit by its light rig, not by a bake.</b> There are no
/// lightmaps for a room that never shipped, and a surface that expects one and has none is
/// drawn black. Every surface is therefore marked
/// <see cref="BspSurface.IgnoreLightmapFlag"/>, which is the same thing the game's own
/// self-lit surfaces say about themselves.
/// </para>
/// </remarks>
public static class SceneFromModel
{
    /// <summary>The most vertices a room built this way may have.</summary>
    /// <remarks>
    /// A BSP indexes its vertices with 16-bit indices, and that is not an accident of the
    /// file format but the shape every consumer here expects. A model with more than this
    /// is refused whole rather than silently truncated: half a room drawn and the other
    /// half missing is the sort of failure nobody reports as a format problem.
    /// </remarks>
    public const int MostVertices = ushort.MaxValue;

    /// <summary>Builds a room from a model.</summary>
    /// <param name="model">The geometry, as read from glTF.</param>
    /// <param name="name">What to call the room.</param>
    /// <param name="diagnostics">Receives the reason when a model cannot be one.</param>
    /// <returns>The room, or null when the model cannot make one.</returns>
    /// <remarks>
    /// One glTF node becomes one object, and one primitive becomes one surface. That is the
    /// mapping the rest of the engine is written against: a scene file binds a noun to an
    /// <em>object</em> by name, so the node names in the model are what decide what the
    /// player can click on, and a room exported with everything joined into one node is a
    /// room with exactly one clickable thing in it.
    /// </remarks>
    public static BspFile? Build(ModFile model, string name, DiagnosticBag? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(name);

        if (model.VertexCount > MostVertices)
        {
            diagnostics?.Add(new Diagnostic(
                "GK3R1196",
                DiagnosticSeverity.Error,
                $"{name} has {model.VertexCount} vertices and a room may have at most " +
                $"{MostVertices}, so it is not built.",
                name,
                null,
                $"at most {MostVertices} vertices",
                model.VertexCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "Split the room into fewer vertices, or into more than one room."));

            return null;
        }

        List<string> objectNames = [];
        List<BspSurface> surfaces = [];
        List<BspPolygon> polygons = [];
        List<Vector3> vertices = [];
        List<Vector2> texCoords = [];
        List<ushort> indices = [];

        for (int meshAt = 0; meshAt < model.Meshes.Count; meshAt++)
        {
            ModMesh mesh = model.Meshes[meshAt];

            // Unnamed nodes still become objects, because leaving them out would leave
            // their triangles undrawn. They are named for their position so that two of
            // them are still two things.
            objectNames.Add(mesh.Name is { Length: > 0 } named
                ? named
                : $"object{meshAt.ToString(System.Globalization.CultureInfo.InvariantCulture)}");

            foreach (ModSubmesh submesh in mesh.Submeshes)
            {
                int surfaceAt = surfaces.Count;
                int baseVertex = vertices.Count;

                for (int i = 0; i < submesh.Positions.Length; i++)
                {
                    // The node's transform is baked in here. A .MOD keeps it separate so
                    // that a scene can pose the parts of a model; a room is not posed, and
                    // every consumer of a BSP expects its vertices to be where they are.
                    vertices.Add(Vector3.Transform(submesh.Positions[i], mesh.MeshToLocal));

                    texCoords.Add(i < submesh.TexCoords.Length
                        ? submesh.TexCoords[i]
                        : Vector2.Zero);
                }

                surfaces.Add(new BspSurface
                {
                    ObjectIndex = meshAt,
                    TextureName = submesh.TextureName,
                    LightmapUvOffset = Vector2.Zero,
                    LightmapUvScale = Vector2.One,
                    Flags = BspSurface.IgnoreLightmapFlag,
                });

                for (int i = 0; i + 2 < submesh.Indices.Length; i += 3)
                {
                    polygons.Add(new BspPolygon
                    {
                        VertexIndexOffset = indices.Count,
                        VertexIndexCount = 3,
                        SurfaceIndex = surfaceAt,
                    });

                    indices.Add((ushort)(baseVertex + submesh.Indices[i]));
                    indices.Add((ushort)(baseVertex + submesh.Indices[i + 1]));
                    indices.Add((ushort)(baseVertex + submesh.Indices[i + 2]));
                }
            }
        }

        if (polygons.Count == 0)
        {
            diagnostics?.Add(new Diagnostic(
                "GK3R1197",
                DiagnosticSeverity.Error,
                $"{name} has no triangles, so there is no room to build.",
                name,
                null,
                "at least one triangle",
                "none"));

            return null;
        }

        return BspFile.FromParts(
            name,
            objectNames,
            surfaces,
            polygons,
            [.. vertices],
            [.. texCoords],
            [.. indices]);
    }
}
