using System.Numerics;
using GK3Reborn.Formats.Models;

namespace GK3Reborn.Formats.Scenes;

/// <summary>
/// Exports a parsed scene as glTF binary.
/// </summary>
/// <remarks>
/// <para>
/// Reuses the model exporter by presenting the scene as a set of meshes: one per named
/// object in the room, with a submesh per texture inside it. That grouping is the one
/// the original data already carries — several surfaces make up a "door" — so a room
/// opens in Blender as a named outliner tree rather than as one undifferentiated soup
/// of triangles.
/// </para>
/// <para>
/// Scene geometry has no per-vertex normals in the original. They are computed from
/// face winding and averaged across the vertices that share them, which is the best
/// available reconstruction and enough for the geometry to shade sensibly in a viewer.
/// </para>
/// </remarks>
public static class SceneGlbWriter
{
    /// <summary>Encodes a scene as a GLB file.</summary>
    /// <param name="scene">The scene to write.</param>
    /// <param name="texturePathPrefix">Relative path prepended to texture file names.</param>
    /// <returns>The complete GLB file.</returns>
    public static byte[] Encode(BspFile scene, string texturePathPrefix = "../textures/")
    {
        ArgumentNullException.ThrowIfNull(scene);

        Vector3[] normals = ComputeNormals(scene);

        // Group surfaces by the object they belong to, then by texture within it.
        var byObject = scene.Surfaces
            .Select((surface, index) => (Surface: surface, Index: index))
            .GroupBy(s => s.Surface.ObjectIndex)
            .OrderBy(g => g.Key);

        List<ModMesh> meshes = [];

        foreach (var group in byObject)
        {
            List<ModSubmesh> submeshes = [];

            foreach (var textureGroup in group.GroupBy(s => s.Surface.TextureName, StringComparer.OrdinalIgnoreCase))
            {
                HashSet<int> surfaceIndices = [.. textureGroup.Select(s => s.Index)];
                ModSubmesh? submesh = BuildSubmesh(scene, normals, surfaceIndices, textureGroup.Key);
                if (submesh is not null)
                {
                    submeshes.Add(submesh);
                }
            }

            if (submeshes.Count > 0)
            {
                meshes.Add(new ModMesh
                {
                    // Scene vertices are already in room space, so the node transform is
                    // identity rather than the per-mesh basis a model carries.
                    MeshToLocal = Matrix4x4.Identity,
                    BoundsMin = Vector3.Zero,
                    BoundsMax = Vector3.Zero,
                    Submeshes = submeshes,
                });
            }
        }

        return GlbWriter.Encode(ModFile.FromMeshes(scene.Name, meshes), texturePathPrefix);
    }

    private static ModSubmesh? BuildSubmesh(
        BspFile scene, Vector3[] normals, HashSet<int> surfaceIndices, string textureName)
    {
        // Vertices are shared across the whole room, so each submesh remaps only the ones
        // it uses. Exporting the full array per submesh would multiply a room's data by
        // the number of textures in it.
        Dictionary<ushort, ushort> remap = [];
        List<Vector3> positions = [];
        List<Vector3> submeshNormals = [];
        List<Vector2> texCoords = [];
        List<ushort> indices = [];

        ushort Map(ushort original)
        {
            if (remap.TryGetValue(original, out ushort mapped))
            {
                return mapped;
            }

            mapped = (ushort)positions.Count;
            remap[original] = mapped;
            positions.Add(scene.Vertices[original]);
            submeshNormals.Add(normals[original]);
            texCoords.Add(scene.TexCoordFor(original));
            return mapped;
        }

        foreach (BspPolygon polygon in scene.Polygons)
        {
            if (!surfaceIndices.Contains(polygon.SurfaceIndex))
            {
                continue;
            }

            foreach ((ushort a, ushort b, ushort c) in scene.Triangulate(polygon))
            {
                // A submesh cannot address more than 65,536 vertices with 16-bit indices.
                // No retail room comes close, but overflowing would corrupt silently.
                if (positions.Count > ushort.MaxValue - 3)
                {
                    break;
                }

                indices.Add(Map(a));
                indices.Add(Map(b));
                indices.Add(Map(c));
            }
        }

        if (indices.Count == 0)
        {
            return null;
        }

        return new ModSubmesh
        {
            TextureName = textureName,
            Color = (255, 255, 255),
            Positions = [.. positions],
            Normals = [.. submeshNormals],
            TexCoords = [.. texCoords],
            Indices = [.. indices],
        };
    }

    /// <summary>Reconstructs vertex normals by averaging the faces that meet at each one.</summary>
    private static Vector3[] ComputeNormals(BspFile scene)
    {
        Vector3[] normals = new Vector3[scene.Vertices.Length];

        foreach (BspPolygon polygon in scene.Polygons)
        {
            foreach ((ushort a, ushort b, ushort c) in scene.Triangulate(polygon))
            {
                Vector3 edge1 = scene.Vertices[b] - scene.Vertices[a];
                Vector3 edge2 = scene.Vertices[c] - scene.Vertices[a];
                Vector3 face = Vector3.Cross(edge1, edge2);

                // Unnormalised cross products weight each face by its area, which is the
                // usual and better-behaved way to average them.
                normals[a] += face;
                normals[b] += face;
                normals[c] += face;
            }
        }

        for (int i = 0; i < normals.Length; i++)
        {
            normals[i] = normals[i].LengthSquared() > 0
                ? Vector3.Normalize(normals[i])
                : new Vector3(0, 1, 0);
        }

        return normals;
    }
}
