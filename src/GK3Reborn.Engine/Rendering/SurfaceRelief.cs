// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Numerics;
using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Formats.Scenes;

namespace GK3Reborn.Rendering;

/// <summary>Whether and how far a floor's height map is allowed to become geometry.</summary>
/// <param name="Displace">
/// Whether to cut the floor up and move it at all. Off, a height map is read by the shader
/// alone and every batch is the geometry the 1999 files describe.
/// </param>
/// <param name="TriangleBudget">
/// The most triangles a room's floor may become. It buys the cell size rather than being
/// spent to a fixed one — see <see cref="ReliefPlan.For"/> — so a small room comes out finer
/// than a large one for the same number, which is what a fixed cell gets backwards.
/// </param>
/// <param name="Trace">
/// Whether the cut-up floor is what rays see, rather than the flat triangles under it.
/// Without this a cobble does not shadow its own gutter, which is most of what displacing it
/// was for; with it, the acceleration structure carries the whole budget.
/// </param>
public readonly record struct ReliefSettings(bool Displace, int TriangleBudget, bool Trace)
{
    /// <summary>Displaced, at four hundred thousand triangles, and traced.</summary>
    /// <remarks>
    /// Four hundred thousand against the ten to fifteen thousand a whole room has been
    /// until now. It sounds enormous and is not: this is a 1999 game running on hardware
    /// twenty-five years later, the vertex format is forty bytes, and the budget comes to
    /// about twenty megabytes of geometry for the one surface the camera spends its time
    /// looking along. It buys about four units a cell — one cobble — over the largest paved
    /// area in the game.
    /// </remarks>
    public static ReliefSettings Default => new(true, 400_000, true);

    /// <summary>Nothing displaced.</summary>
    public static ReliefSettings Off => new(false, 1, false);
}

/// <summary>A vertex of a displaced surface, before it is given a lightmap coordinate.</summary>
/// <param name="Position">Where it ended up, in world space.</param>
/// <param name="Normal">The smoothed normal it was moved along.</param>
/// <param name="TexCoord">Its texture coordinate, interpolated across the source triangle.</param>
public readonly record struct ReliefVertex(Vector3 Position, Vector3 Normal, Vector2 TexCoord);

/// <summary>
/// Turns a floor's height map into geometry.
/// </summary>
/// <remarks>
/// <para>
/// Parallax cannot make a silhouette. It moves the texel a ray lands on, so a cobbled
/// street reads as cobbles from above and as a painted plane the moment the camera drops to
/// eye level and looks along it — which, this being an adventure game about walking down
/// streets, is most of the time anybody looks at one. The fix is the obvious one: move the
/// vertices.
/// </para>
/// <para>
/// <b>There are no vertices to move.</b> The game's floors are enormous flat triangles —
/// PL6's stretch of road is ninety-six of them over 1.15 million square units, an average
/// triangle four metres across — so this subdivides before it displaces, and everything
/// here follows from having to do that without opening a crack.
/// </para>
/// <para>
/// <b>The cut is a lattice in texture space, not a subdivision of the triangle.</b> Each
/// triangle is clipped against a grid of lines at fixed texture coordinates, and every piece
/// that falls out is one cell of that grid. Three things follow, and they are the reason for
/// doing it this way rather than the obvious way.
/// </para>
/// <para>
/// First, <b>no cracks and nothing to reconcile</b>. Two triangles that share an edge share
/// its texture coordinates, so the lattice crosses that edge in the same places from both
/// sides and both put vertices there. There is no per-triangle subdivision level for
/// neighbours to disagree about, and no stitching.
/// </para>
/// <para>
/// Second, <b>the cells are the size they need to be</b>. Cutting a triangle into an N by N
/// barycentric grid takes N from its longest edge, so a long thin strip of road — which is
/// what a road is — comes out cut far finer across than along; measured over the corpus that
/// wastes between two and four triangles in every five. A lattice spends them where there is
/// area.
/// </para>
/// <para>
/// Third, the cells line up with the height field, because that is what texture space is.
/// </para>
/// <para>
/// <b>What does not move.</b> An edge no second displaced triangle shares — the floor
/// meeting a wall, a kerb, the end of the world, or simply the next texture along, whose
/// lattice is its own — stays exactly where the 1999 geometry put it, and the displacement
/// fades in over the first cell. Lifting one opens a gap under the skirting board.
/// </para>
/// <para>
/// <b>What is left for the shader.</b> The geometry can only carry relief coarser than its
/// own cells, so the field is averaged over a cell before a vertex is moved — see
/// <see cref="HeightField.Over"/> — and the finer part of the same field stays with the
/// parallax march and the normal map, at a reduced depth. Displacing at full depth and
/// marching at full depth counts the same bump twice.
/// </para>
/// </remarks>
public sealed class ReliefPlan
{
    /// <summary>Positions are matched to a sixteenth of a unit, which is a millimetre and a half.</summary>
    private const float Grain = 16f;

    /// <summary>The finest cell worth cutting, in world units.</summary>
    /// <remarks>
    /// Two units is five centimetres. Below that there is little left to resolve — a shipped
    /// height map is 512 texels across a tile the game stretches over a couple of hundred
    /// units, so a cell this size is already down to a handful of texels — and a small room
    /// would otherwise spend its whole budget cutting a lobby floor into confetti.
    /// </remarks>
    public const float FinestCell = 2f;

    /// <summary>How many lattice cells one source triangle may be cut against.</summary>
    /// <remarks>
    /// A cap on the work one triangle can ask for. It cannot bind at any cell the budget
    /// will choose — a whole floor is inside the budget by construction — and it is here so
    /// that a surface with a broken texture coordinate cannot ask for a lattice with a
    /// hundred million cells in it.
    /// </remarks>
    private const int MostCells = 65_536;

    private readonly Dictionary<(int X, int Y, int Z), Vector3> _normals;
    private readonly HashSet<((int X, int Y, int Z) From, (int X, int Y, int Z) To)> _pinned;

    /// <summary>Corners that lie on a pinned edge, whichever triangle is asking.</summary>
    /// <remarks>
    /// Pinning edges alone leaves a pinhole. A corner where the floor meets a wall belongs
    /// to the boundary edge along that wall and also to triangles that have no boundary
    /// edge of their own — the one behind it, sharing only the diagonal — and those have no
    /// reason not to lift it. Both then own that single point and disagree about where it
    /// is, which is a hole rather than a crack and shows as a speck of skybox at the
    /// skirting.
    /// </remarks>
    private readonly HashSet<(int X, int Y, int Z)> _held;
    private readonly Dictionary<string, Vector2> _steps;

    private ReliefPlan(
        Dictionary<(int X, int Y, int Z), Vector3> normals,
        HashSet<((int X, int Y, int Z), (int X, int Y, int Z))> pinned,
        HashSet<(int X, int Y, int Z)> held,
        Dictionary<string, Vector2> steps,
        int floorObject,
        float cell,
        int triangles,
        int sources)
    {
        _normals = normals;
        _pinned = pinned;
        _held = held;
        _steps = steps;
        FloorObject = floorObject;
        Cell = cell;
        Triangles = triangles;
        SourceTriangles = sources;
    }

    /// <summary>Which BSP object the scene calls its floor.</summary>
    public int FloorObject { get; }

    /// <summary>How long a side of one lattice cell is, in world units.</summary>
    public float Cell { get; }

    /// <summary>Roughly how many triangles the floor will come to once cut.</summary>
    public int Triangles { get; }

    /// <summary>How many triangles it was before.</summary>
    public int SourceTriangles { get; }

    /// <summary>Whether a surface of the scene is part of what this displaces.</summary>
    /// <param name="surface">The surface.</param>
    /// <param name="deep">Whether its texture's relief is to be cut into the geometry.</param>
    /// <returns>True when its triangles should go through <see cref="Tessellate"/>.</returns>
    public bool Covers(BspSurface surface, bool deep)
    {
        ArgumentNullException.ThrowIfNull(surface);

        return deep && surface.ObjectIndex == FloorObject;
    }

    /// <summary>
    /// Works out how finely a scene's floor can afford to be cut, and what must not move.
    /// </summary>
    /// <param name="scene">The room.</param>
    /// <param name="floorObject">The object the scene's <c>floor=</c> line names.</param>
    /// <param name="deep">Whether a texture's relief is to be cut into the geometry.</param>
    /// <param name="budget">The most triangles the floor may become.</param>
    /// <returns>The plan, or null when there is no floor to displace.</returns>
    /// <remarks>
    /// The cell is bought with the budget rather than fixed, which is what lets one number
    /// serve rooms an order of magnitude apart in paved area: the hotel lobby's four hundred
    /// and fifty thousand square units come out at the finest cell allowed and the village
    /// forecourt's two and a half million at about four, with nobody tuning a scene.
    /// </remarks>
    public static ReliefPlan? For(
        BspFile? scene, string? floorObject, Func<string, bool> deep, int budget)
    {
        ArgumentNullException.ThrowIfNull(deep);
        ArgumentOutOfRangeException.ThrowIfLessThan(budget, 1);

        if (scene is null || string.IsNullOrWhiteSpace(floorObject))
        {
            return null;
        }

        int wanted = Named(scene, floorObject);

        if (wanted < 0)
        {
            return null;
        }

        List<(Vector3 A, Vector3 B, Vector3 C, string Texture)> triangles = [];

        // Area-weighted, per texture: how much world one unit of texture coordinate is
        // worth along each axis. One answer for a whole texture rather than one per
        // triangle, because it decides where the lattice lines fall and two triangles
        // either side of an edge have to agree about that.
        Dictionary<string, (double U, double V, double Weight)> tiling =
            new(StringComparer.OrdinalIgnoreCase);

        double area = 0;
        double perimeter = 0;

        foreach (BspPolygon polygon in scene.Polygons)
        {
            if (polygon.SurfaceIndex < 0 || polygon.SurfaceIndex >= scene.Surfaces.Count)
            {
                continue;
            }

            BspSurface surface = scene.Surfaces[polygon.SurfaceIndex];

            if (surface.ObjectIndex != wanted || !deep(surface.TextureName))
            {
                continue;
            }

            foreach ((ushort a, ushort b, ushort c) in scene.Triangulate(polygon))
            {
                Vector3 pa = scene.Vertices[a];
                Vector3 pb = scene.Vertices[b];
                Vector3 pc = scene.Vertices[c];

                float one = 0.5f * Vector3.Cross(pb - pa, pc - pa).Length();

                if (one <= 1e-6f)
                {
                    continue;
                }

                triangles.Add((pa, pb, pc, surface.TextureName));
                area += one;
                perimeter += (pb - pa).Length() + (pc - pb).Length() + (pa - pc).Length();

                if (!Gradients(
                        pa, pb, pc,
                        scene.TexCoordFor(a), scene.TexCoordFor(b), scene.TexCoordFor(c),
                        out Vector3 alongU, out Vector3 alongV))
                {
                    continue;
                }

                tiling.TryGetValue(surface.TextureName, out (double U, double V, double W) sum);

                tiling[surface.TextureName] = (
                    sum.U + (alongU.Length() * one),
                    sum.V + (alongV.Length() * one),
                    sum.W + one);
            }
        }

        if (triangles.Count == 0 || area <= 0 || tiling.Count == 0)
        {
            return null;
        }

        float cell = Afforded(area, perimeter, triangles.Count, budget);

        var steps = new Dictionary<string, Vector2>(StringComparer.OrdinalIgnoreCase);

        foreach ((string texture, (double u, double v, double weight)) in tiling)
        {
            // A lattice line every cell's width of world, expressed in texture coordinates.
            // Anisotropic where the texture is stretched, which several of the floors are.
            steps[texture] = new Vector2(
                (float)(cell / Math.Max(u / weight, 1e-6)),
                (float)(cell / Math.Max(v / weight, 1e-6)));
        }

        // Adjacency, over the triangles that will actually be displaced. An edge used once
        // is the floor's outer boundary; an edge whose two triangles carry different
        // textures is a seam between two lattices with no reason to line up. Neither moves.
        Dictionary<((int, int, int), (int, int, int)), (int Uses, string Texture)> edges = [];
        Dictionary<(int X, int Y, int Z), Vector3> normals = [];

        foreach ((Vector3 a, Vector3 b, Vector3 c, string texture) in triangles)
        {
            Vector3 cross = Vector3.Cross(b - a, c - a);

            // Area-weighted, which is the average that does not let a room's worth of
            // slivers outvote the surface they lie along.
            Accumulate(normals, a, cross);
            Accumulate(normals, b, cross);
            Accumulate(normals, c, cross);

            Use(edges, Key(a), Key(b), texture);
            Use(edges, Key(b), Key(c), texture);
            Use(edges, Key(c), Key(a), texture);
        }

        foreach ((int X, int Y, int Z) key in normals.Keys.ToArray())
        {
            Vector3 sum = normals[key];

            normals[key] = sum.LengthSquared() > 1e-12f
                ? Vector3.Normalize(sum)
                : Vector3.UnitY;
        }

        HashSet<((int, int, int), (int, int, int))> pinned = [];
        HashSet<(int X, int Y, int Z)> held = [];

        foreach ((((int X, int Y, int Z) from, (int X, int Y, int Z) to) edge,
                  (int uses, string texture)) in edges)
        {
            if (uses != 2 || texture.Length == 0)
            {
                pinned.Add(edge);
                held.Add(edge.from);
                held.Add(edge.to);
            }
        }

        return new ReliefPlan(
            normals,
            pinned,
            held,
            steps,
            wanted,
            cell,
            Estimate(area, perimeter, triangles.Count, cell),
            triangles.Count);
    }

    /// <summary>
    /// Cuts one triangle along the lattice and lifts the pieces onto the height field.
    /// </summary>
    /// <param name="a">First corner.</param>
    /// <param name="b">Second corner.</param>
    /// <param name="c">Third corner.</param>
    /// <param name="ua">First corner's texture coordinate.</param>
    /// <param name="ub">Second corner's texture coordinate.</param>
    /// <param name="uc">Third corner's texture coordinate.</param>
    /// <param name="texture">Which texture, since the lattice is that texture's.</param>
    /// <param name="field">The height field, or null to cut without displacing.</param>
    /// <param name="depth">How deep the field goes, in world units.</param>
    /// <param name="vertices">Receives the pieces' vertices. Cleared first.</param>
    /// <param name="indices">Receives their triangles, as offsets into the vertices. Cleared first.</param>
    public void Tessellate(
        Vector3 a,
        Vector3 b,
        Vector3 c,
        Vector2 ua,
        Vector2 ub,
        Vector2 uc,
        string texture,
        HeightField? field,
        float depth,
        List<ReliefVertex> vertices,
        List<int> indices)
    {
        ArgumentNullException.ThrowIfNull(texture);
        ArgumentNullException.ThrowIfNull(vertices);
        ArgumentNullException.ThrowIfNull(indices);

        vertices.Clear();
        indices.Clear();

        Vector2 edgeB = ub - ua;
        Vector2 edgeC = uc - ua;

        // Twice the triangle's area in texture space, which is also what inverts the map
        // from a texture coordinate back to a point on it. Zero means there is no texture
        // area to lay a lattice over — a coordinate somebody collapsed, a surface with none
        // — and there is nothing to do but leave the triangle as it was.
        float determinant = (edgeB.X * edgeC.Y) - (edgeC.X * edgeB.Y);

        if (!_steps.TryGetValue(texture, out Vector2 step) ||
            MathF.Abs(determinant) < 1e-12f ||
            step.X <= 0f ||
            step.Y <= 0f)
        {
            Whole(a, b, c, ua, ub, uc, vertices, indices);
            return;
        }

        int firstU = (int)MathF.Floor(MathF.Min(ua.X, MathF.Min(ub.X, uc.X)) / step.X);
        int lastU = (int)MathF.Floor(MathF.Max(ua.X, MathF.Max(ub.X, uc.X)) / step.X);
        int firstV = (int)MathF.Floor(MathF.Min(ua.Y, MathF.Min(ub.Y, uc.Y)) / step.Y);
        int lastV = (int)MathF.Floor(MathF.Max(ua.Y, MathF.Max(ub.Y, uc.Y)) / step.Y);

        if ((long)(lastU - firstU + 1) * (lastV - firstV + 1) > MostCells)
        {
            Whole(a, b, c, ua, ub, uc, vertices, indices);
            return;
        }

        // Which of this triangle's own edges may not move, and how quickly the fade lets go
        // of them. Distance from an edge is the opposite corner's barycentric weight times
        // twice the area over that edge's length, so a fade of one cell is a number of
        // weights — which is what Fade returns, and zero where the edge is free to move.
        float twiceArea = Vector3.Cross(b - a, c - a).Length();
        float alongA = (c - b).Length();
        float alongB = (a - c).Length();
        float alongC = (b - a).Length();

        float fadeA = Fade(Key(b), Key(c), twiceArea, alongA);
        float fadeB = Fade(Key(c), Key(a), twiceArea, alongB);
        float fadeC = Fade(Key(a), Key(b), twiceArea, alongC);

        // And the corners, which a triangle can be holding down without owning the edge
        // that holds them. A weight of one is the corner itself and zero is the far edge,
        // so the fade runs over the shorter of the two sides that meet there.
        float cornerA = Corner(Key(a), MathF.Min(alongB, alongC));
        float cornerB = Corner(Key(b), MathF.Min(alongC, alongA));
        float cornerC = Corner(Key(c), MathF.Min(alongA, alongB));

        Vector3 normalA = NormalAt(Key(a), a, b, c);
        Vector3 normalB = NormalAt(Key(b), a, b, c);
        Vector3 normalC = NormalAt(Key(c), a, b, c);

        // How wide a cell is in texture coordinates, for averaging the field over one.
        float span = (step.X + step.Y) * 0.5f;
        bool displacing = field is not null && depth > 0f;

        Span<Vector2> polygon = stackalloc Vector2[16];
        Span<Vector2> clipped = stackalloc Vector2[16];

        var made = new Dictionary<(int U, int V), int>();

        for (int i = firstU; i <= lastU; i++)
        {
            for (int j = firstV; j <= lastV; j++)
            {
                polygon[0] = ua;
                polygon[1] = ub;
                polygon[2] = uc;

                int count = Clip(polygon, 3, clipped, 0, i * step.X, keepPast: true);
                count = Clip(clipped, count, polygon, 0, (i + 1) * step.X, keepPast: false);
                count = Clip(polygon, count, clipped, 1, j * step.Y, keepPast: true);
                count = Clip(clipped, count, polygon, 1, (j + 1) * step.Y, keepPast: false);

                if (count < 3)
                {
                    continue;
                }

                int first = Vertex(polygon[0]);
                int previous = Vertex(polygon[1]);

                for (int k = 2; k < count; k++)
                {
                    int index = Vertex(polygon[k]);

                    // A fan from the cell's first corner. The piece is convex — it is a
                    // triangle clipped by four half-planes — so a fan is a triangulation
                    // of it and needs no test.
                    if (first != previous && previous != index && index != first)
                    {
                        indices.Add(first);
                        indices.Add(previous);
                        indices.Add(index);
                    }

                    previous = index;
                }
            }
        }

        // A vertex of one of the cells, made once however many cells meet at it. Keyed on
        // the texture coordinate rounded fine, because two neighbouring cells work out
        // where this triangle's edge crosses their shared boundary from different clipped
        // segments, and the two answers agree to within the last bits of a float rather
        // than exactly.
        int Vertex(Vector2 uv)
        {
            (int, int) key = ((int)MathF.Round(uv.X * 65_536f), (int)MathF.Round(uv.Y * 65_536f));

            if (made.TryGetValue(key, out int already))
            {
                return already;
            }

            // Back to a point on the triangle. A texture coordinate is affine in the
            // barycentric weights, so inverting it is a two-by-two solve.
            Vector2 offset = uv - ua;
            float towardsB = ((offset.X * edgeC.Y) - (edgeC.X * offset.Y)) / determinant;
            float towardsC = ((edgeB.X * offset.Y) - (offset.X * edgeB.Y)) / determinant;
            float towardsA = 1f - towardsB - towardsC;

            Vector3 position = (a * towardsA) + (b * towardsB) + (c * towardsC);
            Vector3 normal = (normalA * towardsA) + (normalB * towardsB) + (normalC * towardsC);
            normal = normal.LengthSquared() > 1e-12f ? Vector3.Normalize(normal) : normalA;

            if (displacing)
            {
                float blend = MathF.Min(
                    MathF.Min(Held(towardsA, fadeA), Held(towardsB, fadeB)),
                    Held(towardsC, fadeC));

                blend = MathF.Min(
                    blend,
                    MathF.Min(
                        MathF.Min(
                            Held(1f - towardsA, cornerA),
                            Held(1f - towardsB, cornerB)),
                        Held(1f - towardsC, cornerC)));

                if (blend > 0f)
                {
                    // Downwards only: the modelled surface is the *top* of the relief and
                    // the field cuts into it, rather than sitting in the middle of it with
                    // half above.
                    //
                    // Not a stylistic choice. A floor is the one surface other things rest
                    // on — a rug, a shadow decal, the foot of a chair — and the game
                    // places them flush with the plane the 1999 geometry describes. Relief
                    // that rises above that plane punches through every one of them, which
                    // in the hotel lobby was three thousand pixels of tile flickering
                    // through the rug at the resolution it was measured at. Carving keeps
                    // the same peak-to-trough depth and cannot intersect anything that was
                    // not already intersecting.
                    float cut = field!.Over(uv.X, uv.Y, span) - 0.5f;

                    position += normal * (cut * depth * blend);
                }
            }

            made[key] = vertices.Count;
            vertices.Add(new ReliefVertex(position, normal, uv));

            return vertices.Count - 1;
        }
    }

    /// <summary>How much of the displacement survives this far from something pinned.</summary>
    private static float Held(float weight, float fade) =>
        fade <= 0f ? 1f : Math.Clamp(weight * fade, 0f, 1f);

    /// <summary>How quickly the fade lets go of a held corner, in weights.</summary>
    /// <returns>Zero where the corner is free to move.</returns>
    private float Corner((int X, int Y, int Z) at, float nearest) =>
        _held.Contains(at) ? MathF.Max(nearest, 1e-6f) / Cell : 0f;

    /// <summary>The triangle as it was, when there is no lattice to cut it with.</summary>
    private void Whole(
        Vector3 a,
        Vector3 b,
        Vector3 c,
        Vector2 ua,
        Vector2 ub,
        Vector2 uc,
        List<ReliefVertex> vertices,
        List<int> indices)
    {
        vertices.Add(new ReliefVertex(a, NormalAt(Key(a), a, b, c), ua));
        vertices.Add(new ReliefVertex(b, NormalAt(Key(b), a, b, c), ub));
        vertices.Add(new ReliefVertex(c, NormalAt(Key(c), a, b, c), uc));

        indices.Add(0);
        indices.Add(1);
        indices.Add(2);
    }

    /// <summary>
    /// Clips a convex polygon against one axis-aligned line, in texture space.
    /// </summary>
    /// <param name="source">The polygon.</param>
    /// <param name="count">How many of its vertices are in use.</param>
    /// <param name="destination">Receives the clipped polygon.</param>
    /// <param name="axis">Zero for a line of constant U, one for constant V.</param>
    /// <param name="at">Where the line is.</param>
    /// <param name="keepPast">Whether to keep what is past the line or short of it.</param>
    /// <returns>How many vertices the result has.</returns>
    /// <remarks>
    /// Sutherland and Hodgman, one half-plane at a time. The clipped coordinate is taken
    /// from the line rather than interpolated, so that two neighbouring cells put their
    /// shared vertices at exactly the same place along the axis they share.
    /// </remarks>
    private static int Clip(
        ReadOnlySpan<Vector2> source,
        int count,
        Span<Vector2> destination,
        int axis,
        float at,
        bool keepPast)
    {
        int made = 0;

        for (int i = 0; i < count && made + 2 <= destination.Length; i++)
        {
            Vector2 from = source[i];
            Vector2 to = source[(i + 1) % count];

            float here = axis == 0 ? from.X : from.Y;
            float there = axis == 0 ? to.X : to.Y;

            bool insideHere = keepPast ? here >= at : here <= at;
            bool insideThere = keepPast ? there >= at : there <= at;

            if (insideHere)
            {
                destination[made++] = from;
            }

            if (insideHere != insideThere)
            {
                float t = (at - here) / (there - here);

                destination[made++] = axis == 0
                    ? new Vector2(at, from.Y + ((to.Y - from.Y) * t))
                    : new Vector2(from.X + ((to.X - from.X) * t), at);
            }
        }

        return made;
    }

    /// <summary>How many barycentric weights of an edge one cell's fade reaches.</summary>
    /// <returns>Zero where the edge is free to move, so that nothing is held down.</returns>
    private float Fade(
        (int X, int Y, int Z) from, (int X, int Y, int Z) to, float twiceArea, float length)
    {
        if (!_pinned.Contains(Ordered(from, to)) || twiceArea <= 1e-9f)
        {
            return 0f;
        }

        return Math.Clamp(twiceArea / (MathF.Max(length, 1e-6f) * Cell), 1e-6f, 1e6f);
    }

    /// <summary>How many triangles a cell size cuts an area into, with its ragged edges.</summary>
    /// <remarks>
    /// Two triangles a cell over the area, plus the part-cells along every source
    /// triangle's own edges — a small correction on a village street and a large one in a
    /// room whose floor is already a thousand small triangles.
    /// </remarks>
    private static int Estimate(double area, double perimeter, int triangles, float cell) =>
        (int)Math.Min(
            int.MaxValue,
            (2 * area / (cell * cell)) + (2 * perimeter / cell) + (4.0 * triangles));

    /// <summary>The finest cell a budget affords over an area.</summary>
    private static float Afforded(double area, double perimeter, int triangles, int budget)
    {
        float cell = FinestCell;

        // Walk coarser until the estimate fits. A hundred steps of a twentieth is a factor
        // of a hundred and thirty, which covers the corpus from a bathroom to a village.
        for (int attempt = 0; attempt < 100; attempt++)
        {
            if (Estimate(area, perimeter, triangles, cell) <= budget)
            {
                break;
            }

            cell *= 1.05f;
        }

        return cell;
    }

    private static int Named(BspFile scene, string floorObject)
    {
        for (int i = 0; i < scene.ObjectNames.Count; i++)
        {
            if (string.Equals(
                    scene.ObjectNames[i], floorObject, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>How far a point moves for one unit of each texture coordinate.</summary>
    private static bool Gradients(
        Vector3 a,
        Vector3 b,
        Vector3 c,
        Vector2 ua,
        Vector2 ub,
        Vector2 uc,
        out Vector3 alongU,
        out Vector3 alongV)
    {
        Vector2 edgeB = ub - ua;
        Vector2 edgeC = uc - ua;

        float determinant = (edgeB.X * edgeC.Y) - (edgeC.X * edgeB.Y);

        if (MathF.Abs(determinant) < 1e-12f)
        {
            alongU = Vector3.Zero;
            alongV = Vector3.Zero;

            return false;
        }

        Vector3 toB = b - a;
        Vector3 toC = c - a;

        alongU = ((toB * edgeC.Y) - (toC * edgeB.Y)) / determinant;
        alongV = ((toC * edgeB.X) - (toB * edgeC.X)) / determinant;

        return true;
    }

    private static void Use(
        Dictionary<((int, int, int), (int, int, int)), (int Uses, string Texture)> edges,
        (int, int, int) from,
        (int, int, int) to,
        string texture)
    {
        ((int, int, int), (int, int, int)) key = Ordered(from, to);

        if (!edges.TryGetValue(key, out (int Uses, string Texture) seen))
        {
            edges[key] = (1, texture);
            return;
        }

        // An empty name marks an edge two different textures meet along, whose lattices
        // have no reason to line up.
        edges[key] = (
            seen.Uses + 1,
            string.Equals(seen.Texture, texture, StringComparison.OrdinalIgnoreCase)
                ? seen.Texture
                : string.Empty);
    }

    private static void Accumulate(
        Dictionary<(int X, int Y, int Z), Vector3> normals, Vector3 at, Vector3 cross)
    {
        (int X, int Y, int Z) key = Key(at);

        normals[key] = normals.TryGetValue(key, out Vector3 sum) ? sum + cross : cross;
    }

    /// <summary>An edge, with its ends in an order that does not depend on which triangle asked.</summary>
    private static ((int, int, int), (int, int, int)) Ordered(
        (int X, int Y, int Z) from, (int X, int Y, int Z) to) =>
        (from.X, from.Y, from.Z).CompareTo((to.X, to.Y, to.Z)) <= 0 ? (from, to) : (to, from);

    private static (int X, int Y, int Z) Key(Vector3 position) =>
        ((int)MathF.Round(position.X * Grain),
         (int)MathF.Round(position.Y * Grain),
         (int)MathF.Round(position.Z * Grain));

    private Vector3 NormalAt((int X, int Y, int Z) key, Vector3 a, Vector3 b, Vector3 c)
    {
        if (_normals.TryGetValue(key, out Vector3 smoothed))
        {
            return smoothed;
        }

        Vector3 cross = Vector3.Cross(b - a, c - a);

        return cross.LengthSquared() > 1e-12f ? Vector3.Normalize(cross) : Vector3.UnitY;
    }
}
