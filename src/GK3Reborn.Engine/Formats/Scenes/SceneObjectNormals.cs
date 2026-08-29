// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Numerics;

namespace GK3Reborn.Formats.Scenes;

/// <summary>
/// Reconstructs vertex normals for one of a room's objects, stopping at its creases.
/// </summary>
/// <remarks>
/// <para>
/// Scene geometry carries no normals. The renderer does not need any — it shades every
/// triangle by its own face normal, which is what the original did — but anything that
/// leaves the engine does: a modelling tool decides where to bevel from the angle between
/// faces, and a viewer showing a room with normals averaged across the whole of it makes
/// a doorway look like a sheet draped over furniture.
/// </para>
/// <para>
/// <b>Averaging every face that touches a vertex is the mistake to avoid.</b> A box's
/// corner is one vertex shared by three faces at right angles, and averaging them rounds
/// the corner off in the shading while the silhouette stays square — the surface reads as
/// soft plastic. So faces meeting at a vertex are gathered into groups by the angle
/// between them, a vertex carries one normal per group, and an edge between two groups
/// stays hard. That is the same rule <c>ObjectRounding</c> reaches for, and for the same
/// reason.
/// </para>
/// <para>
/// Grouping is transitive within a vertex, which is what lets a lathed object come out
/// smooth: each side face is gentle against its neighbour, so the whole ring joins, while
/// the cap sitting at ninety degrees to all of them stays out of it.
/// </para>
/// </remarks>
public sealed class SceneObjectNormals
{
    private readonly Dictionary<(int Weld, int Face), int> _groups;
    private readonly Dictionary<(int Weld, int Group), Vector3> _normals;
    private readonly int[] _weld;
    private readonly Vector3[] _faceNormals;

    private SceneObjectNormals(
        int[] weld,
        Vector3[] faceNormals,
        Dictionary<(int, int), int> groups,
        Dictionary<(int, int), Vector3> normals)
    {
        _weld = weld;
        _faceNormals = faceNormals;
        _groups = groups;
        _normals = normals;
    }

    /// <summary>Every triangle one of a room's objects is made of, numbered.</summary>
    /// <param name="scene">The room.</param>
    /// <param name="objectIndex">Which of its objects.</param>
    /// <returns>
    /// The triangles in a fixed order, each with the surface that owns it. The number is
    /// what ties a normal to a corner, so everything that walks an object's triangles must
    /// walk them through here.
    /// </returns>
    public static IEnumerable<(int Face, ushort A, ushort B, ushort C, int Surface)> Faces(
        BspFile scene, int objectIndex)
    {
        ArgumentNullException.ThrowIfNull(scene);

        int face = 0;

        foreach (BspPolygon polygon in scene.Polygons)
        {
            if (polygon.SurfaceIndex < 0 ||
                polygon.SurfaceIndex >= scene.Surfaces.Count ||
                scene.Surfaces[polygon.SurfaceIndex].ObjectIndex != objectIndex)
            {
                continue;
            }

            foreach ((ushort a, ushort b, ushort c) in scene.Triangulate(polygon))
            {
                yield return (face, a, b, c, polygon.SurfaceIndex);
                face++;
            }
        }
    }

    /// <summary>Works out an object's normals.</summary>
    /// <param name="scene">The room.</param>
    /// <param name="objectIndex">Which of its objects.</param>
    /// <param name="crease">The angle beyond which a shared edge is a crease, in degrees.</param>
    /// <returns>The normals, addressed by corner and face.</returns>
    public static SceneObjectNormals For(BspFile scene, int objectIndex, float crease)
    {
        ArgumentNullException.ThrowIfNull(scene);

        float limit = MathF.Cos(float.DegreesToRadians(Math.Clamp(crease, 0f, 180f)));

        // Welded by position rather than by index: an object's surfaces are separate runs
        // of the room's shared vertex array and the same corner can appear in it twice,
        // which would leave a seam shaded as though it were an edge of the object.
        Dictionary<(int, int, int), int> welds = [];
        int[] weld = new int[scene.Vertices.Length];
        Array.Fill(weld, -1);

        List<Vector3> areas = [];
        List<Vector3> units = [];
        List<(int A, int B, int C)> faces = [];

        foreach ((int _, ushort a, ushort b, ushort c, int _) in Faces(scene, objectIndex))
        {
            Vector3 pa = scene.Vertices[a];
            Vector3 pb = scene.Vertices[b];
            Vector3 pc = scene.Vertices[c];

            Vector3 area = Vector3.Cross(pb - pa, pc - pa);
            float length = area.Length();

            areas.Add(area);
            units.Add(length > 1e-9f ? area / length : Vector3.UnitY);
            faces.Add((Weld(a), Weld(b), Weld(c)));

            int Weld(ushort index)
            {
                if (weld[index] >= 0)
                {
                    return weld[index];
                }

                // A tenth of a world unit, which is far below anything the artists placed
                // and far above the drift between two copies of one corner.
                Vector3 at = scene.Vertices[index];
                (int, int, int) key = (
                    (int)MathF.Round(at.X * 10f),
                    (int)MathF.Round(at.Y * 10f),
                    (int)MathF.Round(at.Z * 10f));

                if (!welds.TryGetValue(key, out int id))
                {
                    id = welds.Count;
                    welds[key] = id;
                }

                weld[index] = id;
                return id;
            }
        }

        // Which faces meet at each welded position.
        Dictionary<int, List<int>> incident = [];

        for (int face = 0; face < faces.Count; face++)
        {
            foreach (int at in (ReadOnlySpan<int>)[faces[face].A, faces[face].B, faces[face].C])
            {
                if (!incident.TryGetValue(at, out List<int>? here))
                {
                    here = [];
                    incident[at] = here;
                }

                if (!here.Contains(face))
                {
                    here.Add(face);
                }
            }
        }

        Dictionary<(int, int), int> groups = [];
        Dictionary<(int, int), Vector3> normals = [];

        foreach ((int at, List<int> here) in incident)
        {
            // Union-find over the faces at this position: two of them join when they meet
            // gently enough, and joining is transitive so a ring of gentle steps comes out
            // as one smooth group.
            int[] parent = [.. Enumerable.Range(0, here.Count)];

            int Find(int index)
            {
                while (parent[index] != index)
                {
                    parent[index] = parent[parent[index]];
                    index = parent[index];
                }

                return index;
            }

            for (int i = 0; i < here.Count; i++)
            {
                for (int j = i + 1; j < here.Count; j++)
                {
                    if (Vector3.Dot(units[here[i]], units[here[j]]) >= limit)
                    {
                        parent[Find(i)] = Find(j);
                    }
                }
            }

            Dictionary<int, int> numbered = [];

            for (int i = 0; i < here.Count; i++)
            {
                int root = Find(i);

                if (!numbered.TryGetValue(root, out int group))
                {
                    group = numbered.Count;
                    numbered[root] = group;
                }

                groups[(at, here[i])] = group;

                normals[(at, group)] =
                    normals.TryGetValue((at, group), out Vector3 sum) ? sum + areas[here[i]] : areas[here[i]];
            }
        }

        foreach ((int Weld, int Group) key in normals.Keys.ToList())
        {
            Vector3 sum = normals[key];
            normals[key] = sum.LengthSquared() > 1e-12f ? Vector3.Normalize(sum) : Vector3.Zero;
        }

        return new SceneObjectNormals(weld, [.. units], groups, normals);
    }

    /// <summary>Which smoothing group a corner of a face belongs to.</summary>
    /// <param name="face">The face's number, as <see cref="Faces"/> gave it.</param>
    /// <param name="vertex">The corner's index in the room's vertex array.</param>
    /// <returns>The group, or zero when the corner is not one this was built from.</returns>
    public int GroupOf(int face, ushort vertex)
    {
        int at = vertex < _weld.Length ? _weld[vertex] : -1;

        return at >= 0 && _groups.TryGetValue((at, face), out int group) ? group : 0;
    }

    /// <summary>The normal at one corner of one face.</summary>
    /// <param name="vertex">The corner's index in the room's vertex array.</param>
    /// <param name="group">Its smoothing group, from <see cref="GroupOf"/>.</param>
    /// <param name="face">The face, used when the group's normal cancelled out.</param>
    /// <returns>A unit normal.</returns>
    /// <remarks>
    /// Falls back to the face's own normal rather than to an axis. A group whose faces
    /// sum to nothing is a fold — two sheets back to back — and shading it along the world
    /// vertical would light one of the two sheets from inside.
    /// </remarks>
    public Vector3 NormalOf(ushort vertex, int group, int face)
    {
        int at = vertex < _weld.Length ? _weld[vertex] : -1;

        if (at >= 0 && _normals.TryGetValue((at, group), out Vector3 normal) &&
            normal.LengthSquared() > 1e-12f)
        {
            return normal;
        }

        return face >= 0 && face < _faceNormals.Length ? _faceNormals[face] : Vector3.UnitY;
    }
}
