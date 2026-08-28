using System.Numerics;
using GK3Reborn.Formats.Scenes;

namespace GK3Reborn.Rendering;

/// <summary>
/// Gives a room's zero-thickness cards a thickness, so a depth buffer can tell their two
/// sides apart.
/// </summary>
/// <remarks>
/// <para>
/// A great deal of GK3's scenery is a single quad with a different picture on each side:
/// the Mt Cardou signpost at Blanchefort is one flat card with the lettering on the front
/// and bare wood on the back, and the corpus holds 3,086 such pairs across 98 of its 110
/// rooms — mostly foliage, whose two sides carry the same texture and so cannot show what
/// is wrong with them.
/// </para>
/// <para>
/// <b>Both faces are drawn and they are at exactly the same depth</b>, so which one a pixel
/// shows is decided by the last bit of an interpolated float. The two faces are triangulated
/// from different vertices, so that bit differs from pixel to pixel and the two pictures
/// interleave in stripes. It is not a precision problem in the ordinary sense and no near
/// plane, no far plane and no wider depth buffer moves it: the surfaces are coincident.
/// </para>
/// <para>
/// <b>The original avoids this by culling back faces</b> — <c>Renderer::Render</c> sets
/// <c>CullMode::Back</c> for opaque world geometry — so only the side facing the camera is
/// ever drawn. That is not available here: GK3's winding is not consistent enough to cull
/// on, and switching it on removes every foliage card in the game along with the backs of
/// the signs. So the cards are given a thickness instead. Each face moves a twentieth of a
/// unit along its own normal, which puts a tenth of a unit between them — half a millimetre
/// at the game's scale, where a character is 72 units tall — and that is enough for the
/// depth test to answer the same way over the whole surface.
/// </para>
/// </remarks>
public static class CoplanarCards
{
    /// <summary>How far each face of a card moves along its own normal, in scene units.</summary>
    /// <remarks>
    /// A tenth of a unit between the two, which is about half a millimetre of a real sign
    /// and some four hundred depth quanta at the distance a sign is read from. Larger would
    /// start to be a thickness somebody could see at a grazing angle; smaller stops
    /// separating them across a room.
    /// </remarks>
    public const float Separation = 0.05f;

    /// <summary>
    /// How far each of a room's surfaces has to move to stop coinciding with another.
    /// </summary>
    /// <param name="scene">The room.</param>
    /// <param name="separation">How far a face of a card moves.</param>
    /// <returns>
    /// One offset per surface, in world units, zero for every surface that coincides with
    /// nothing — which is nearly all of them.
    /// </returns>
    public static Vector3[] Apart(BspFile scene, float separation = Separation)
    {
        ArgumentNullException.ThrowIfNull(scene);

        var offsets = new Vector3[scene.Surfaces.Count];
        Face?[] faces = Faces(scene);

        // Bucketed by the plane's distance from the origin, rounded to a unit. A card's two
        // faces have the same distance and opposite normals, so they land in the same
        // bucket or in the one next door, and nothing outside those two can coincide with
        // either. Without this the search is every surface against every other, which on a
        // room of three thousand is nine million comparisons of a dozen vertices each.
        Dictionary<int, List<int>> buckets = [];

        for (int s = 0; s < faces.Length; s++)
        {
            if (faces[s] is { } face)
            {
                int bucket = (int)MathF.Round(MathF.Abs(face.Distance));
                (buckets.TryGetValue(bucket, out List<int>? found)
                    ? found
                    : buckets[bucket] = []).Add(s);
            }
        }

        foreach ((int bucket, List<int> here) in buckets)
        {
            List<int> near = buckets.TryGetValue(bucket + 1, out List<int>? above) ? above : [];

            foreach (int a in here)
            {
                foreach (int b in here.Concat(near))
                {
                    if (a != b && Coincide(faces[a]!, faces[b]!))
                    {
                        offsets[a] = faces[a]!.Normal * separation;
                        offsets[b] = faces[b]!.Normal * separation;
                    }
                }
            }
        }

        return offsets;
    }

    /// <summary>A surface's plane and the points it covers.</summary>
    private sealed record Face(Vector3 Normal, float Distance, Vector3[] Points);

    /// <summary>Reads every surface's plane, or null where it has no polygon to take one from.</summary>
    private static Face?[] Faces(BspFile scene)
    {
        var points = new List<Vector3>[scene.Surfaces.Count];
        var first = new BspPolygon?[scene.Surfaces.Count];

        foreach (BspPolygon polygon in scene.Polygons)
        {
            int surface = polygon.SurfaceIndex;

            if (surface < 0 || surface >= points.Length)
            {
                continue;
            }

            points[surface] ??= [];
            first[surface] ??= polygon;

            for (int i = 0; i < polygon.VertexIndexCount; i++)
            {
                points[surface].Add(scene.Vertices[scene.VertexIndices[polygon.VertexIndexOffset + i]]);
            }
        }

        var faces = new Face?[scene.Surfaces.Count];

        for (int s = 0; s < faces.Length; s++)
        {
            if (first[s] is not { VertexIndexCount: >= 3 } polygon)
            {
                continue;
            }

            Vector3 At(int i) => scene.Vertices[scene.VertexIndices[polygon.VertexIndexOffset + i]];

            Vector3 normal = Vector3.Cross(At(1) - At(0), At(2) - At(0));

            if (normal.LengthSquared() <= 1e-12f)
            {
                continue;
            }

            normal = Vector3.Normalize(normal);
            faces[s] = new Face(normal, Vector3.Dot(normal, At(0)), [.. points[s]]);
        }

        return faces;
    }

    /// <summary>How far apart two planes may be and still count as the same one.</summary>
    /// <remarks>
    /// A hundredth of a unit. These are not surfaces that are nearly coincident — they are
    /// the same quad exported twice — so the tolerance is for the arithmetic rather than for
    /// the artists. A wall standing a tenth of a unit off another wall is two surfaces and
    /// is left alone: it is already far enough apart to be drawn.
    /// </remarks>
    private const float Tolerance = 0.01f;

    /// <summary>Whether two surfaces are the two sides of one card.</summary>
    /// <remarks>
    /// Facing opposite ways, on the same plane, and covering some of the same ground. The
    /// last is what tells a card from two surfaces that merely lie in one plane — a floor
    /// and the ceiling of the room below it, or the two halves of a wall.
    /// </remarks>
    private static bool Coincide(Face a, Face b)
    {
        if (Vector3.Dot(a.Normal, b.Normal) > -0.9998f ||
            MathF.Abs(a.Distance + b.Distance) > Tolerance)
        {
            return false;
        }

        foreach (Vector3 point in b.Points)
        {
            if (MathF.Abs(Vector3.Dot(a.Normal, point) - a.Distance) > Tolerance)
            {
                return false;
            }
        }

        return Overlap(a, b);
    }

    /// <summary>Whether two coplanar surfaces cover any of the same ground.</summary>
    /// <remarks>
    /// Measured as boxes in the plane's own two directions rather than as polygons. It
    /// decides only whether to move a surface half a millimetre, so a box that is generous
    /// at a corner costs nothing; a polygon test would cost a great deal more and buy
    /// nothing that can be seen.
    /// </remarks>
    private static bool Overlap(Face a, Face b)
    {
        Vector3 across = Vector3.Normalize(Vector3.Cross(
            MathF.Abs(a.Normal.Y) > 0.9f ? Vector3.UnitX : Vector3.UnitY, a.Normal));

        Vector3 up = Vector3.Cross(a.Normal, across);

        (float Low, float High) Span(Vector3[] points, Vector3 axis)
        {
            float low = float.MaxValue;
            float high = float.MinValue;

            foreach (Vector3 point in points)
            {
                float at = Vector3.Dot(point, axis);
                low = MathF.Min(low, at);
                high = MathF.Max(high, at);
            }

            return (low, high);
        }

        (float Low, float High) au = Span(a.Points, across);
        (float Low, float High) bu = Span(b.Points, across);
        (float Low, float High) av = Span(a.Points, up);
        (float Low, float High) bv = Span(b.Points, up);

        return MathF.Min(au.High, bu.High) - MathF.Max(au.Low, bu.Low) > 0f &&
               MathF.Min(av.High, bv.High) - MathF.Max(av.Low, bv.Low) > 0f;
    }
}
