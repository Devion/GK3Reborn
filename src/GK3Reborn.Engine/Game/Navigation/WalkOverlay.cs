using System.Numerics;
using GK3Reborn.Formats.Scenes;

namespace GK3Reborn.Game.Navigation;

/// <summary>A patch of the overlay: the texels of one region, and the colour to draw them.</summary>
/// <param name="Region">The palette index these texels carry.</param>
/// <param name="Colour">What to draw it in, each channel from zero to one.</param>
/// <param name="Positions">Quad corners, four per texel.</param>
/// <param name="Indices">Triangles over <paramref name="Positions"/>.</param>
public readonly record struct WalkOverlayPatch(
    int Region,
    Vector3 Colour,
    Vector3[] Positions,
    uint[] Indices);

/// <summary>
/// The walk boundary as something you can look at.
/// </summary>
/// <remarks>
/// <para>
/// A boundary is a bitmap in its own coordinate space with a size and an offset, and every
/// part of that is easy to get subtly wrong: an inverted row order, a swapped offset sign
/// and a boundary half a room out all produce a mask that looks reasonable in isolation.
/// <c>Plan/04</c> makes overlay validation part of the phase's exit criteria for exactly
/// that reason — the only way to know a boundary is right is to see it lying on the floor
/// it describes.
/// </para>
/// <para>
/// Each open texel becomes a quad at the height of the floor beneath it, coloured by
/// region so the gradient away from the walls is visible and the scriptable regions stand
/// out from ordinary ground. Texels with no floor under them are left out: a boundary
/// covers a rectangle and a room is not one, so a good half of most bitmaps hangs over
/// nothing.
/// </para>
/// </remarks>
public static class WalkOverlay
{
    /// <summary>How far above the floor the overlay floats, in scene units.</summary>
    /// <remarks>
    /// About a centimetre. Enough to win the depth test everywhere without the quads
    /// visibly hovering, which at this scale would read as a bug in the overlay rather
    /// than as the overlay.
    /// </remarks>
    private const float Lift = 0.4f;

    /// <summary>How far above the floor a route floats, in scene units.</summary>
    /// <remarks>
    /// Higher than <see cref="Lift"/> so a route drawn over the region overlay wins the
    /// depth test against it rather than fighting with it texel by texel.
    /// </remarks>
    private const float RouteLift = 1.2f;

    /// <summary>Builds the overlay for a scene.</summary>
    /// <param name="bsp">The scene's geometry, for the floor to lie on.</param>
    /// <param name="floorObject">Which object in it is the floor; null tests all of them.</param>
    /// <param name="boundary">The boundary to draw.</param>
    /// <returns>One patch per region in use, in ascending region order.</returns>
    public static IReadOnlyList<WalkOverlayPatch> Build(
        BspFile bsp, string? floorObject, WalkBoundary boundary)
    {
        ArgumentNullException.ThrowIfNull(bsp);
        ArgumentNullException.ThrowIfNull(boundary);

        List<(Vector3 A, Vector3 B, Vector3 C)> floor = FloorTriangles(bsp, floorObject);
        Dictionary<int, (List<Vector3> Positions, List<uint> Indices)> patches = [];

        Vector2 texel = boundary.TexelSize;
        float halfX = texel.X * 0.5f;
        float halfZ = texel.Y * 0.5f;

        for (int y = 0; y < boundary.Height; y++)
        {
            for (int x = 0; x < boundary.Width; x++)
            {
                int region = boundary.RegionOf(x, y);
                if (!boundary.IsRegionOpen(region))
                {
                    continue;
                }

                Vector3 centre = boundary.ToWorld(x, y);
                if (HeightAt(floor, centre.X, centre.Z) is not { } height)
                {
                    continue;
                }

                if (!patches.TryGetValue(region, out (List<Vector3>, List<uint>) patch))
                {
                    patch = ([], []);
                    patches[region] = patch;
                }

                float top = height + Lift;
                uint at = (uint)patch.Item1.Count;

                patch.Item1.Add(new Vector3(centre.X - halfX, top, centre.Z - halfZ));
                patch.Item1.Add(new Vector3(centre.X + halfX, top, centre.Z - halfZ));
                patch.Item1.Add(new Vector3(centre.X + halfX, top, centre.Z + halfZ));
                patch.Item1.Add(new Vector3(centre.X - halfX, top, centre.Z + halfZ));

                patch.Item2.AddRange([at, at + 1, at + 2, at, at + 2, at + 3]);
            }
        }

        return
        [
            .. patches
                .OrderBy(p => p.Key)
                .Select(p => new WalkOverlayPatch(
                    p.Key, ColourOf(p.Key), [.. p.Value.Positions], [.. p.Value.Indices])),
        ];
    }

    /// <summary>Draws a route across the floor.</summary>
    /// <param name="bsp">The scene's geometry, for the floor to lie on.</param>
    /// <param name="floorObject">Which object in it is the floor; null tests all of them.</param>
    /// <param name="boundary">The boundary the route was found on, for its scale.</param>
    /// <param name="points">The route's corners, as <see cref="WalkPath"/> returned them.</param>
    /// <param name="colour">What to draw it in, each channel from zero to one.</param>
    /// <returns>
    /// One patch carrying the whole route, or null when it has no points or no floor
    /// beneath it. Its region is -1: a route is not a region of the boundary.
    /// </returns>
    /// <remarks>
    /// A ribbon of overlapping squares sampled along the route rather than one quad per
    /// segment, because each sample takes the height of the floor under it — so the ribbon
    /// climbs a step and follows a ramp instead of sinking through either. It is drawn one
    /// texel wide, the same as the cells it crosses, so a route reads at the scale of the
    /// boundary that produced it rather than as a hairline nobody can see. It is drawn
    /// above the region overlay so the two can be shown together, which is the check worth
    /// doing: a route that leaves the green is a bug in one of them.
    /// </remarks>
    public static WalkOverlayPatch? Route(
        BspFile bsp,
        string? floorObject,
        WalkBoundary boundary,
        IReadOnlyList<Vector3> points,
        Vector3 colour)
    {
        ArgumentNullException.ThrowIfNull(bsp);
        ArgumentNullException.ThrowIfNull(boundary);
        ArgumentNullException.ThrowIfNull(points);

        if (points.Count == 0)
        {
            return null;
        }

        List<(Vector3 A, Vector3 B, Vector3 C)> floor = FloorTriangles(bsp, floorObject);
        Vector2 texel = boundary.TexelSize;
        float half = MathF.Max(0.5f, MathF.Min(texel.X, texel.Y) * 0.5f);

        List<Vector3> positions = [];
        List<uint> indices = [];

        Mark(points[0]);

        for (int i = 1; i < points.Count; i++)
        {
            Vector3 from = points[i - 1];
            Vector3 to = points[i];
            Vector3 step = to - from;
            float distance = MathF.Sqrt((step.X * step.X) + (step.Z * step.Z));
            int samples = Math.Max(1, (int)MathF.Ceiling(distance / half));

            for (int sample = 1; sample <= samples; sample++)
            {
                Mark(Vector3.Lerp(from, to, (float)sample / samples));
            }
        }

        return positions.Count == 0
            ? null
            : new WalkOverlayPatch(-1, colour, [.. positions], [.. indices]);

        void Mark(Vector3 at)
        {
            if (HeightAt(floor, at.X, at.Z) is not { } height)
            {
                return;
            }

            float top = height + RouteLift;
            uint start = (uint)positions.Count;

            positions.Add(new Vector3(at.X - half, top, at.Z - half));
            positions.Add(new Vector3(at.X + half, top, at.Z - half));
            positions.Add(new Vector3(at.X + half, top, at.Z + half));
            positions.Add(new Vector3(at.X - half, top, at.Z + half));

            indices.AddRange([start, start + 1, start + 2, start, start + 2, start + 3]);
        }
    }

    /// <summary>
    /// What to draw a region in.
    /// </summary>
    /// <remarks>
    /// Green for open floor, darkening through the gradient towards the walls, and amber
    /// for the scriptable regions so a door that a script opens is not mistaken for
    /// ordinary ground.
    /// </remarks>
    public static Vector3 ColourOf(int region)
    {
        if (region is >= 128 and <= 254)
        {
            return new Vector3(0.95f, 0.62f, 0.15f);
        }

        float away = Math.Clamp(region / 8f, 0f, 1f);
        return new Vector3(0.15f + (0.5f * away), 0.85f - (0.45f * away), 0.2f);
    }

    /// <summary>The floor's triangles, in world space.</summary>
    private static List<(Vector3, Vector3, Vector3)> FloorTriangles(
        BspFile bsp, string? floorObject)
    {
        List<(Vector3, Vector3, Vector3)> triangles = [];

        foreach (BspPolygon polygon in bsp.Polygons)
        {
            if (polygon.SurfaceIndex < 0 || polygon.SurfaceIndex >= bsp.Surfaces.Count)
            {
                continue;
            }

            BspSurface surface = bsp.Surfaces[polygon.SurfaceIndex];

            if (floorObject is { Length: > 0 } &&
                (surface.ObjectIndex < 0 ||
                 surface.ObjectIndex >= bsp.ObjectNames.Count ||
                 !string.Equals(
                     bsp.ObjectNames[surface.ObjectIndex], floorObject, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            foreach ((ushort a, ushort b, ushort c) in bsp.Triangulate(polygon))
            {
                triangles.Add((bsp.Vertices[a], bsp.Vertices[b], bsp.Vertices[c]));
            }
        }

        return triangles;
    }

    /// <summary>The highest floor directly under a point, if there is one.</summary>
    /// <remarks>
    /// Highest rather than first, because a floor object can have geometry stacked over
    /// itself — a landing above a stair — and the overlay belongs on the surface an actor
    /// would be standing on.
    /// </remarks>
    private static float? HeightAt(
        List<(Vector3 A, Vector3 B, Vector3 C)> triangles, float x, float z)
    {
        float? best = null;

        foreach ((Vector3 a, Vector3 b, Vector3 c) in triangles)
        {
            // Barycentric coordinates on the X/Z plane; a triangle standing edge-on has
            // zero area there and cannot be underneath anything.
            float area = ((b.Z - c.Z) * (a.X - c.X)) + ((c.X - b.X) * (a.Z - c.Z));
            if (MathF.Abs(area) < 1e-6f)
            {
                continue;
            }

            float u = (((b.Z - c.Z) * (x - c.X)) + ((c.X - b.X) * (z - c.Z))) / area;
            float v = (((c.Z - a.Z) * (x - c.X)) + ((a.X - c.X) * (z - c.Z))) / area;
            float w = 1f - u - v;

            if (u < 0f || v < 0f || w < 0f)
            {
                continue;
            }

            float height = (u * a.Y) + (v * b.Y) + (w * c.Y);
            if (best is null || height > best)
            {
                best = height;
            }
        }

        return best;
    }
}
