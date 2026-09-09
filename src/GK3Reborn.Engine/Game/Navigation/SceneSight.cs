using System.Numerics;
using GK3Reborn.Formats.Scenes;

namespace GK3Reborn.Game.Navigation;

/// <summary>
/// Something a walk is trying to get a look at.
/// </summary>
/// <param name="Name">
/// What the room's geometry calls it, or null when it is a prop standing in the room
/// rather than part of it. A named thing is seen when it is the first thing a look
/// lands on; an unnamed one when nothing is in the way.
/// </param>
/// <param name="Minimum">Its lower corner.</param>
/// <param name="Maximum">Its upper corner.</param>
public readonly record struct SightTarget(string? Name, Vector3 Minimum, Vector3 Maximum);

/// <summary>
/// Whether one point in a room can see another.
/// </summary>
public sealed class SceneSight
{
    /// <summary>How far anybody is taken to be able to see, in scene units.</summary>
    public const float Reach = 200f;

    private const int Across = 24;

    private readonly Vector3[] _triangles;
    private readonly int[] _objects;
    private readonly Dictionary<string, int> _named;
    private readonly int[][] _cells;
    private readonly float _minimumX;
    private readonly float _minimumZ;
    private readonly float _cellX;
    private readonly float _cellZ;

    private SceneSight(
        Vector3[] triangles,
        int[] objects,
        Dictionary<string, int> named,
        int[][] cells,
        float minimumX,
        float minimumZ,
        float cellX,
        float cellZ)
    {
        _triangles = triangles;
        _objects = objects;
        _named = named;
        _cells = cells;
        _minimumX = minimumX;
        _minimumZ = minimumZ;
        _cellX = cellX;
        _cellZ = cellZ;
    }

    /// <summary>How many triangles can block a line of sight.</summary>
    public int TriangleCount => _triangles.Length / 3;

    /// <summary>Prepares a room's geometry to be asked about.</summary>
    /// <param name="geometry">The room, or null when the scene has none.</param>
    /// <returns>The sight tester, or null when there is nothing to see through.</returns>
    public static SceneSight? For(BspFile? geometry)
    {
        if (geometry is not { } bsp || bsp.Polygons.Count == 0)
        {
            return null;
        }

        var triangles = new List<Vector3>(bsp.TriangleCount * 3);
        var objects = new List<int>(bsp.TriangleCount);

        foreach (BspPolygon polygon in bsp.Polygons)
        {
            BspSurface? surface = polygon.SurfaceIndex >= 0 && polygon.SurfaceIndex < bsp.Surfaces.Count
                ? bsp.Surfaces[polygon.SurfaceIndex]
                : null;

            // Glass. A translucent surface is looked through, not at: Larry's study window
            // is one, and counting it as a wall meant nothing inside the house could ever
            // be seen from outside it.
            if (surface is not null && (surface.Flags & BspSurface.ShadowTextureFlag) != 0)
            {
                continue;
            }

            foreach ((ushort a, ushort b, ushort c) in bsp.Triangulate(polygon))
            {
                if (a >= bsp.Vertices.Length || b >= bsp.Vertices.Length || c >= bsp.Vertices.Length)
                {
                    continue;
                }

                triangles.Add(bsp.Vertices[a]);
                triangles.Add(bsp.Vertices[b]);
                triangles.Add(bsp.Vertices[c]);
                objects.Add(surface?.ObjectIndex ?? -1);
            }
        }

        if (triangles.Count == 0)
        {
            return null;
        }

        var named = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < bsp.ObjectNames.Count; i++)
        {
            named.TryAdd(bsp.ObjectNames[i], i);
        }

        var minimum = new Vector3(float.MaxValue);
        var maximum = new Vector3(float.MinValue);

        foreach (Vector3 corner in triangles)
        {
            minimum = Vector3.Min(minimum, corner);
            maximum = Vector3.Max(maximum, corner);
        }

        float cellX = MathF.Max(1f, (maximum.X - minimum.X) / Across);
        float cellZ = MathF.Max(1f, (maximum.Z - minimum.Z) / Across);

        var buckets = new List<int>[Across * Across];

        for (int triangle = 0; triangle < triangles.Count; triangle += 3)
        {
            Vector3 first = triangles[triangle];
            Vector3 second = triangles[triangle + 1];
            Vector3 third = triangles[triangle + 2];

            int fromX = Column(MathF.Min(first.X, MathF.Min(second.X, third.X)), minimum.X, cellX);
            int toX = Column(MathF.Max(first.X, MathF.Max(second.X, third.X)), minimum.X, cellX);
            int fromZ = Column(MathF.Min(first.Z, MathF.Min(second.Z, third.Z)), minimum.Z, cellZ);
            int toZ = Column(MathF.Max(first.Z, MathF.Max(second.Z, third.Z)), minimum.Z, cellZ);

            for (int z = fromZ; z <= toZ; z++)
            {
                for (int x = fromX; x <= toX; x++)
                {
                    (buckets[(z * Across) + x] ??= []).Add(triangle);
                }
            }
        }

        var cells = new int[buckets.Length][];
        for (int i = 0; i < buckets.Length; i++)
        {
            cells[i] = buckets[i]?.ToArray() ?? [];
        }

        return new SceneSight(
            [.. triangles], [.. objects], named, cells, minimum.X, minimum.Z, cellX, cellZ);
    }

    /// <summary>
    /// Whether something with these bounds can be seen from a point.
    /// </summary>
    /// <param name="head">Where the looking is done from, at head height.</param>
    /// <param name="minimum">The thing's lower corner.</param>
    /// <param name="maximum">Its upper corner.</param>
    /// <returns>True when any part of it is both near enough and unobstructed.</returns>
    public bool InView(Vector3 head, Vector3 minimum, Vector3 maximum) =>
        InView(head, new SightTarget(null, minimum, maximum));

    /// <summary>
    /// Whether something can be seen from a point.
    /// </summary>
    /// <remarks>
    /// This is the retail engine's test: a line from the head to the thing, and the thing
    /// is seen when the line reaches it or the first surface it meets is the thing's own.
    /// Something hollow and large — the inside of a house, looked at through its window —
    /// is seen the moment a look lands on any part of it, which a test that only asks
    /// whether the line is clear all the way to the middle can never say.
    /// </remarks>
    /// <param name="head">Where the looking is done from, at head height.</param>
    /// <param name="target">The thing.</param>
    /// <returns>True when any part of it is both near enough and unobstructed.</returns>
    public bool InView(Vector3 head, SightTarget target)
    {
        Vector3 minimum = target.Minimum;
        Vector3 maximum = target.Maximum;
        Vector3 centre = (minimum + maximum) * 0.5f;

        int self = target.Name is { Length: > 0 } name && _named.TryGetValue(name, out int index)
            ? index
            : -1;

        if ((head - centre).LengthSquared() > Reach * Reach)
        {
            return false;
        }

        Vector3[] spots =
        [
            centre,
            new(centre.X, maximum.Y, centre.Z),
            new(maximum.X, centre.Y, centre.Z),
            new(centre.X, centre.Y, maximum.Z),
            new(centre.X, minimum.Y, centre.Z),
            new(minimum.X, centre.Y, centre.Z),
            new(centre.X, centre.Y, minimum.Z),
        ];

        foreach (Vector3 spot in spots)
        {
            if (self < 0 ? Clear(head, spot) : Lands(head, spot, self))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether a look from one point towards another meets nothing but a named object.</summary>
    /// <param name="from">The eye.</param>
    /// <param name="to">The point looked at, which is on or inside the object.</param>
    /// <param name="self">The object's index in the room's name table.</param>
    /// <returns>True when the first surface met, if any, is the object's own.</returns>
    private bool Lands(Vector3 from, Vector3 to, int self)
    {
        Vector3 along = to - from;
        float distance = along.Length();

        if (distance <= 0.001f)
        {
            return true;
        }

        Vector3 direction = along / distance;

        // Past the eye's own feet and floor, as Clear does, but all the way to the point
        // and a little beyond: the object's own face may be exactly there.
        const float Margin = 2f;

        float start = MathF.Min(Margin, distance * 0.25f);
        Vector3 origin = from + (direction * start);
        float span = distance - start + Margin;

        float nearest = float.MaxValue;
        int hit = -1;

        foreach (int cell in Crossed(origin, direction, span))
        {
            foreach (int triangle in _cells[cell])
            {
                if (Distance(origin, direction, span, triangle) is { } t && t < nearest)
                {
                    nearest = t;
                    hit = triangle;
                }
            }
        }

        return hit < 0 || _objects[hit / 3] == self;
    }

    /// <summary>Whether the room has nothing solid between two points.</summary>
    /// <param name="from">One end.</param>
    /// <param name="to">The other.</param>
    /// <returns>True when the segment reaches without crossing a triangle.</returns>
    public bool Clear(Vector3 from, Vector3 to)
    {
        Vector3 along = to - from;
        float distance = along.Length();

        if (distance <= 0.001f)
        {
            return true;
        }

        Vector3 direction = along / distance;

        // A margin at each end, in scene units. Two is about an inch of GK3's world: small
        // enough not to see through a wall, large enough to clear the surface being looked
        // at and the floor under the walker's own feet.
        const float Margin = 2f;

        float start = MathF.Min(Margin, distance * 0.25f);
        float end = MathF.Max(start, distance - MathF.Min(Margin, distance * 0.25f));

        Vector3 origin = from + (direction * start);
        float span = end - start;

        foreach (int cell in Crossed(origin, direction, span))
        {
            foreach (int triangle in _cells[cell])
            {
                if (Hits(origin, direction, span, triangle))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>Which buckets a segment passes through, in no particular order.</summary>
    private IEnumerable<int> Crossed(Vector3 origin, Vector3 direction, float span)
    {
        int x = Column(origin.X, _minimumX, _cellX);
        int z = Column(origin.Z, _minimumZ, _cellZ);

        Vector3 finish = origin + (direction * span);

        int lastX = Column(finish.X, _minimumX, _cellX);
        int lastZ = Column(finish.Z, _minimumZ, _cellZ);

        int stepX = Math.Sign(lastX - x);
        int stepZ = Math.Sign(lastZ - z);

        yield return (z * Across) + x;

        // Bounded by the grid's own size rather than by the geometry: a direction with no
        // horizontal component never advances, and a segment that starts outside the room
        // would otherwise walk for ever towards it.
        for (int guard = 0; guard < Across * 2 && (x != lastX || z != lastZ); guard++)
        {
            if (x != lastX)
            {
                x += stepX;
                yield return (z * Across) + x;
            }

            if (z != lastZ)
            {
                z += stepZ;
                yield return (z * Across) + x;
            }
        }
    }

    /// <summary>Whether a segment crosses one triangle.</summary>
    private bool Hits(Vector3 origin, Vector3 direction, float span, int triangle) =>
        Distance(origin, direction, span, triangle) is not null;

    /// <summary>How far along a segment one triangle is crossed, or null when it is not.</summary>
    private float? Distance(Vector3 origin, Vector3 direction, float span, int triangle)
    {
        Vector3 a = _triangles[triangle];
        Vector3 b = _triangles[triangle + 1];
        Vector3 c = _triangles[triangle + 2];

        Vector3 first = b - a;
        Vector3 second = c - a;

        Vector3 across = Vector3.Cross(direction, second);
        float determinant = Vector3.Dot(first, across);

        if (MathF.Abs(determinant) < 1e-6f)
        {
            return null;
        }

        float inverse = 1f / determinant;
        Vector3 offset = origin - a;

        float u = Vector3.Dot(offset, across) * inverse;
        if (u is < 0f or > 1f)
        {
            return null;
        }

        Vector3 edge = Vector3.Cross(offset, first);
        float v = Vector3.Dot(direction, edge) * inverse;

        if (v < 0f || u + v > 1f)
        {
            return null;
        }

        float t = Vector3.Dot(second, edge) * inverse;

        return t > 0f && t < span ? t : null;
    }

    /// <summary>Which column of the grid a coordinate falls in.</summary>
    private static int Column(float value, float minimum, float size) =>
        Math.Clamp((int)((value - minimum) / size), 0, Across - 1);
}
