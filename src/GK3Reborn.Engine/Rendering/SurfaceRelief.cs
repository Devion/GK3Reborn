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
/// <param name="Erosion">
/// The most an outdoor room's ground may be weathered away, in world units, or nought to
/// leave it the shape the artists modelled. This is the band between the texture's own
/// relief and the three-to-five-metre triangles the ground is built from — the ruts and
/// scours and hollows nothing in a 1999 room describes. See <see cref="GroundErosion"/>.
/// </param>
public readonly record struct ReliefSettings(
    bool Displace, int TriangleBudget, bool Trace, float Erosion)
{
    /// <summary>
    /// Displaced, at a million triangles, traced, and weathered by up to twenty-four units.
    /// </summary>
    public static ReliefSettings Default => new(true, 2_000_000, true, 24f);

    /// <summary>Nothing displaced.</summary>
    public static ReliefSettings Off => new(false, 1, false, 0f);
}

/// <summary>What a room needs to say before its ground can be weathered.</summary>
/// <param name="Erodes">
/// How readily a texture's ground erodes, nought to one. See <see cref="GroundVariation"/>.
/// </param>
/// <param name="Anchors">
/// Where the scene stands models on its ground, which holds the ground under them.
/// </param>
/// <param name="Walkable">
/// Whether an actor may stand at a point on X and Z, or null where none may anywhere.
/// </param>
/// <param name="Depth">The most that may be taken off, in world units.</param>
/// <param name="Seed">Something stable about the room.</param>
public readonly record struct ErosionRequest(
    Func<string, float> Erodes,
    IReadOnlyList<GroundAnchor> Anchors,
    Func<float, float, bool>? Walkable,
    float Depth,
    int Seed);

/// <summary>A vertex of a displaced surface, before it is given a lightmap coordinate.</summary>
/// <param name="Position">Where it ended up, in world space.</param>
/// <param name="Normal">The smoothed normal it was moved along.</param>
/// <param name="TexCoord">Its texture coordinate, interpolated across the source triangle.</param>
public readonly record struct ReliefVertex(Vector3 Position, Vector3 Normal, Vector2 TexCoord);

/// <summary>
/// The floor's triangles in buckets, so "does the floor go on past this edge?" is a local
/// question.
/// </summary>
internal sealed class TriangleGrid
{
    /// <summary>How far off the plane of a triangle a point may be and still be on it.</summary>
    private const float Flush = 2f;

    /// <summary>How far past an edge to look for more floor.</summary>
    private const float Beyond = 0.75f;

    private const float Bucket = 128f;

    private readonly Dictionary<(int X, int Z), List<int>> _buckets = [];
    private readonly IReadOnlyList<(Vector3 A, Vector3 B, Vector3 C, string Texture)> _triangles;

    /// <summary>Buckets the triangles.</summary>
    /// <param name="triangles">Every triangle the floor's displacement covers.</param>
    public TriangleGrid(IReadOnlyList<(Vector3 A, Vector3 B, Vector3 C, string Texture)> triangles)
    {
        ArgumentNullException.ThrowIfNull(triangles);

        _triangles = triangles;

        for (int i = 0; i < triangles.Count; i++)
        {
            (Vector3 a, Vector3 b, Vector3 c, _) = triangles[i];

            int firstX = (int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X)) / Bucket);
            int lastX = (int)MathF.Floor(MathF.Max(a.X, MathF.Max(b.X, c.X)) / Bucket);
            int firstZ = (int)MathF.Floor(MathF.Min(a.Z, MathF.Min(b.Z, c.Z)) / Bucket);
            int lastZ = (int)MathF.Floor(MathF.Max(a.Z, MathF.Max(b.Z, c.Z)) / Bucket);

            for (int x = firstX; x <= lastX; x++)
            {
                for (int z = firstZ; z <= lastZ; z++)
                {
                    if (!_buckets.TryGetValue((x, z), out List<int>? bucket))
                    {
                        bucket = [];
                        _buckets[(x, z)] = bucket;
                    }

                    bucket.Add(i);
                }
            }
        }
    }

    /// <summary>Whether more of the same floor lies immediately past an edge.</summary>
    /// <param name="from">One end of the edge.</param>
    /// <param name="to">The other end.</param>
    /// <param name="third">The far corner of the triangle the edge belongs to.</param>
    /// <param name="texture">Its texture, since only the same texture's lattice agrees.</param>
    /// <returns>True when the surface carries on, so the edge need not be held down.</returns>
    public bool Continues(Vector3 from, Vector3 to, Vector3 third, string texture)
    {
        ArgumentNullException.ThrowIfNull(texture);

        Vector3 along = to - from;

        if (along.LengthSquared() < 1e-9f)
        {
            return false;
        }

        along = Vector3.Normalize(along);

        Vector3 middle = (from + to) * 0.5f;

        // Straight out from the edge, in the surface, away from the triangle behind it.
        Vector3 outward = middle - third;
        outward -= along * Vector3.Dot(outward, along);

        if (outward.LengthSquared() < 1e-9f)
        {
            return false;
        }

        Vector3 point = middle + (Vector3.Normalize(outward) * Beyond);

        if (!_buckets.TryGetValue(
                ((int)MathF.Floor(point.X / Bucket), (int)MathF.Floor(point.Z / Bucket)),
                out List<int>? bucket))
        {
            return false;
        }

        foreach (int index in bucket)
        {
            (Vector3 a, Vector3 b, Vector3 c, string other) = _triangles[index];

            if (!string.Equals(other, texture, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Vector3 face = Vector3.Cross(b - a, c - a);

            if (face.LengthSquared() < 1e-9f)
            {
                continue;
            }

            face = Vector3.Normalize(face);

            float height = Vector3.Dot(point - a, face);

            if (MathF.Abs(height) > Flush)
            {
                continue;
            }

            if (Inside(point - (face * height), a, b, c, face))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether a point on a triangle's plane is within the triangle.</summary>
    private static bool Inside(Vector3 point, Vector3 a, Vector3 b, Vector3 c, Vector3 face)
    {
        return Beside(a, b) && Beside(b, c) && Beside(c, a);

        bool Beside(Vector3 from, Vector3 to)
        {
            Vector3 edge = to - from;
            Vector3 outward = Vector3.Cross(edge, face);
            float length = outward.Length();

            // Wound the same way round as the scene's own faces, so "inside" is the side
            // the winding says it is.
            return length < 1e-9f || Vector3.Dot(point - from, outward / length) <= 0f;
        }
    }
}

/// <summary>
/// Turns a floor's height map into geometry.
/// </summary>
public sealed class ReliefPlan
{
    /// <summary>Positions are matched to a sixteenth of a unit, which is a millimetre and a half.</summary>
    private const float Grain = 16f;

    /// <summary>The finest cell worth cutting, in world units.</summary>
    public const float FinestCell = 2f;

    /// <summary>How many lattice cells one source triangle may be cut against.</summary>
    private const int MostCells = 4_194_304;

    /// <summary>How flat a non-floor triangle must lie to have its relief cut.</summary>
    private const float LiesFlat = 0.35f;

    private readonly Dictionary<(int X, int Y, int Z), Vector3> _normals;
    private readonly HashSet<((int X, int Y, int Z) From, (int X, int Y, int Z) To)> _pinned;

    /// <summary>Corners that lie on a pinned edge, whichever triangle is asking.</summary>
    private readonly HashSet<(int X, int Y, int Z)> _held;

    /// <summary>Triangles whose tiling disagrees with their texture's, left as they were.</summary>
    private readonly HashSet<((int, int, int), (int, int, int), (int, int, int))> _apart;
    private readonly Dictionary<string, Vector2> _steps;
    private readonly Lock _tally = new();
    private double _movedTotal;
    private int _movedCount;

    private ReliefPlan(
        Dictionary<(int X, int Y, int Z), Vector3> normals,
        HashSet<((int X, int Y, int Z), (int X, int Y, int Z))> pinned,
        HashSet<(int X, int Y, int Z)> held,
        HashSet<((int, int, int), (int, int, int), (int, int, int))> apart,
        Dictionary<string, Vector2> steps,
        int floorObject,
        float cell,
        int triangles,
        int sources)
    {
        _normals = normals;
        _pinned = pinned;
        _held = held;
        _apart = apart;
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

    /// <summary>The furthest any vertex was moved, in world units.</summary>
    public float Moved { get; private set; }

    /// <summary>How far the average displaced vertex moved, in world units.</summary>
    public float MovedTypically => _movedCount > 0 ? (float)(_movedTotal / _movedCount) : 0f;

    /// <summary>What one <see cref="Tessellate"/> call moved, folded into the total.</summary>
    /// <param name="furthest">The furthest any of its vertices moved.</param>
    /// <param name="total">Those distances added up.</param>
    /// <param name="count">How many of them there were.</param>
    private void Record(float furthest, double total, int count)
    {
        if (count == 0)
        {
            return;
        }

        lock (_tally)
        {
            Moved = MathF.Max(Moved, furthest);
            _movedTotal += total;
            _movedCount += count;
        }
    }

    /// <summary>How many of the floor's own edges were held down, and how many were freed.</summary>
    public (int Pinned, int Continued) Boundary { get; private set; }

    /// <summary>How many triangles were left uncut because their tiling stood apart.</summary>
    public int SetApart { get; private set; }

    /// <summary>Whether a surface of the scene is part of what this displaces.</summary>
    /// <param name="surface">The surface.</param>
    /// <param name="deep">Whether its texture's relief is to be cut into the geometry.</param>
    /// <returns>True when its triangles should go through <see cref="Tessellate"/>.</returns>
    public bool Covers(BspSurface surface, bool deep)
    {
        ArgumentNullException.ThrowIfNull(surface);

        return deep && (surface.ObjectIndex == FloorObject || Also?.Invoke(surface) == true);
    }

    /// <summary>Whether one triangle of a covered surface lies flat enough to cut.</summary>
    /// <param name="surface">The surface it belongs to.</param>
    /// <param name="a">First corner.</param>
    /// <param name="b">Second corner.</param>
    /// <param name="c">Third corner.</param>
    /// <returns>True to cut it; false to leave it the flat triangle it was.</returns>
    public bool Lies(BspSurface surface, Vector3 a, Vector3 b, Vector3 c)
    {
        ArgumentNullException.ThrowIfNull(surface);

        if (surface.ObjectIndex == FloorObject)
        {
            return true;
        }

        Vector3 lie = Vector3.Cross(b - a, c - a);

        return MathF.Abs(lie.Y) >= LiesFlat * lie.Length();
    }

    /// <summary>Surfaces beyond the floor whose relief is cut, or null for floor-only.</summary>
    private Func<BspSurface, bool>? Also { get; init; }

    /// <summary>What the weather takes off this room's ground, or null for none.</summary>
    public GroundErosion? Erosion { get; private init; }

    /// <summary>
    /// Works out how finely a scene's floor can afford to be cut, and what must not move.
    /// </summary>
    /// <param name="scene">The room.</param>
    /// <param name="floorObject">The object the scene's <c>floor=</c> line names.</param>
    /// <param name="deep">Whether a texture's relief is to be cut into the geometry.</param>
    /// <param name="budget">The most triangles the floor may become.</param>
    /// <param name="also">
    /// Surfaces beyond the floor whose relief is cut too, or null for floor-only —
    /// outdoors, the ground runs past the <c>floor=</c> object and the loader says how far.
    /// </param>
    /// <param name="erosion">
    /// What the room needs to weather its ground, or null to leave it the shape it was
    /// modelled in.
    /// </param>
    /// <returns>The plan, or null when there is no floor to displace.</returns>
    public static ReliefPlan? For(
        BspFile? scene, string? floorObject, Func<string, bool> deep, int budget,
        Func<BspSurface, bool>? also = null,
        ErosionRequest? erosion = null)
    {
        ArgumentNullException.ThrowIfNull(deep);
        ArgumentOutOfRangeException.ThrowIfLessThan(budget, 1);

        if (scene is null || (string.IsNullOrWhiteSpace(floorObject) && also is null))
        {
            return null;
        }

        int wanted = string.IsNullOrWhiteSpace(floorObject) ? -1 : Named(scene, floorObject);

        if (wanted < 0 && also is null)
        {
            return null;
        }

        List<(Vector3 A, Vector3 B, Vector3 C, string Texture)> triangles = [];
        List<(Vector2 A, Vector2 B, Vector2 C)> coordinates = [];

        // How much world one unit of texture coordinate is worth along each axis, per
        // texture. One answer for a whole texture rather than one per triangle, because it
        // decides where the lattice lines fall and two triangles either side of an edge
        // have to agree about that.
        //
        // Gathered as samples and settled below by an area-weighted *median*, not a mean.
        // The mean is what a stray triangle poisons: `rc1Coblston` is laid at a clean 120
        // units to the texture over the whole village square, and a handful of triangles
        // whose texture coordinates are all but collapsed — the same texture drawn onto
        // something it was never meant to tile across — carried the average to 42,641. Every
        // cobble then asked for a lattice a thousand times too fine, was refused as
        // impossible, and came out flat. The median does not care how far away an outlier
        // is, only that it is outnumbered.
        Dictionary<string, List<(double U, double V, double Weight)>> samples =
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

            if ((surface.ObjectIndex != wanted && also?.Invoke(surface) != true) ||
                !deep(surface.TextureName))
            {
                continue;
            }

            foreach ((ushort a, ushort b, ushort c) in scene.Triangulate(polygon))
            {
                Vector3 pa = scene.Vertices[a];
                Vector3 pb = scene.Vertices[b];
                Vector3 pc = scene.Vertices[c];

                Vector3 lie = Vector3.Cross(pb - pa, pc - pa);
                float one = 0.5f * lie.Length();

                if (one <= 1e-6f)
                {
                    continue;
                }

                // Surfaces reached through `also` are ground, not architecture: displacing
                // a facade, a roof or a window frame tears it from whatever it abuts at
                // every corner two lattices meet. Anything steeper than about seventy
                // degrees stays flat; the floor object itself is never filtered, because
                // its edges were solved for from the start.
                if (surface.ObjectIndex != wanted && MathF.Abs(lie.Y) < LiesFlat * lie.Length())
                {
                    continue;
                }

                triangles.Add((pa, pb, pc, surface.TextureName));
                coordinates.Add((
                    scene.TexCoordFor(a), scene.TexCoordFor(b), scene.TexCoordFor(c)));
                area += one;
                perimeter += (pb - pa).Length() + (pc - pb).Length() + (pa - pc).Length();

                if (!Gradients(
                        pa, pb, pc,
                        scene.TexCoordFor(a), scene.TexCoordFor(b), scene.TexCoordFor(c),
                        out Vector3 alongU, out Vector3 alongV))
                {
                    continue;
                }

                if (!samples.TryGetValue(surface.TextureName, out List<(double, double, double)>? seen))
                {
                    seen = [];
                    samples[surface.TextureName] = seen;
                }

                seen.Add((alongU.Length(), alongV.Length(), one));
            }
        }

        if (triangles.Count == 0 || area <= 0 || samples.Count == 0)
        {
            return null;
        }

        var tiling = new Dictionary<string, (double U, double V, double Weight)>(
            StringComparer.OrdinalIgnoreCase);

        foreach ((string texture, List<(double U, double V, double Weight)> seen) in samples)
        {
            tiling[texture] = (Middle(seen, true), Middle(seen, false), 1);
        }

        // A lattice is one answer for a whole texture, and a triangle whose own tiling is
        // nothing like that answer cannot be cut on it: the cells come out a fraction of
        // the size the budget bought, by the square, and the relief they carry is a
        // fraction of the depth. The village has a handful — a texture laid across a slope
        // at one scale and along a wall foot at another — and left in they made the cost of
        // cutting the floor rise as the cells were made coarser, because a triangle asking
        // for more cells than <see cref="MostCells"/> is left whole and one asking for
        // slightly fewer is cut into all of them. A budget cannot be solved against a cost
        // that goes the wrong way.
        //
        // Left whole, and their edges held: what borders them is the same case as what
        // borders a wall.
        var apart = new HashSet<((int, int, int), (int, int, int), (int, int, int))>();

        for (int i = triangles.Count - 1; i >= 0; i--)
        {
            (Vector3 pa, Vector3 pb, Vector3 pc, string texture) = triangles[i];

            if (!tiling.TryGetValue(texture, out (double U, double V, double W) rate) ||
                rate.W <= 0 ||
                !Gradients(
                    pa, pb, pc, coordinates[i].A, coordinates[i].B, coordinates[i].C,
                    out Vector3 alongU, out Vector3 alongV))
            {
                continue;
            }

            double ownU = alongU.Length();
            double ownV = alongV.Length();
            double sharedU = rate.U / rate.W;
            double sharedV = rate.V / rate.W;

            if (Agrees(ownU, sharedU) && Agrees(ownV, sharedV))
            {
                continue;
            }

            apart.Add(Corners(pa, pb, pc));
            triangles.RemoveAt(i);
            coordinates.RemoveAt(i);
        }

        if (triangles.Count == 0)
        {
            return null;
        }

        // Recomputed over what is left, because both are sums over the set that changed.
        area = 0;
        perimeter = 0;

        foreach ((Vector3 pa, Vector3 pb, Vector3 pc, string _) in triangles)
        {
            area += 0.5f * Vector3.Cross(pb - pa, pc - pa).Length();
            perimeter += (pb - pa).Length() + (pc - pb).Length() + (pa - pc).Length();
        }

        float cell = Afforded(triangles, coordinates, tiling, budget);


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
        int continued = 0;

        // The far corner of whichever triangle owns an edge, for the test below. An edge
        // used once has exactly one.
        Dictionary<((int, int, int), (int, int, int)), Vector3> across = [];

        foreach ((Vector3 a, Vector3 b, Vector3 c, string _) in triangles)
        {
            across[Ordered(Key(a), Key(b))] = c;
            across[Ordered(Key(b), Key(c))] = a;
            across[Ordered(Key(c), Key(a))] = b;
        }

        var neighbours = new TriangleGrid(triangles);

        foreach ((((int X, int Y, int Z) from, (int X, int Y, int Z) to) edge,
                  (int uses, string texture)) in edges)
        {
            if (uses == 2 && texture.Length > 0)
            {
                continue;
            }

            // An edge used once is not necessarily an edge of the floor. GK3's ground is
            // laid as separate flat patches that abut without being welded — the street
            // against the square, the square against the verge — and a stitch of stairs or
            // a doorway leaves a long edge with a vertex partway along it, which is a
            // T-junction and so used once from either side. Measured on the village: 2,201
            // of 2,674 once-used edges have more floor of the same texture lying against
            // them, and holding all of them down left nine tenths of the relief unbuilt.
            //
            // Nothing has to be welded for those to be safe, because the lattice is what
            // makes the two sides agree: it is laid out in texture space, so two triangles
            // carrying the same texture put vertices at the same texture coordinates along
            // the line they meet on, whether or not they share a single vertex. What has to
            // stay still is where the floor stops — at a wall, at a kerb, or at the next
            // texture along, whose lattice is its own.
            if (uses == 1 &&
                texture.Length > 0 &&
                across.TryGetValue(edge, out Vector3 third) &&
                neighbours.Continues(At(edge.from), At(edge.to), third, texture))
            {
                continued++;

                continue;
            }

            pinned.Add(edge);
            held.Add(edge.from);
            held.Add(edge.to);
        }

        // Over exactly the triangles that survived every filter above, because those are
        // the ones that will move. Anything that dropped out on the way — a facade, a
        // triangle whose tiling stands apart, a surface with no lattice — is ground that
        // stays where it is, and the field has to hold its neighbours to it.
        GroundErosion? weather = erosion is { } asked && scene is not null
            ? GroundErosion.For(
                triangles,
                scene,
                asked.Erodes,
                asked.Anchors,
                asked.Walkable,
                asked.Depth,
                asked.Seed)
            : null;

        return new ReliefPlan(
            normals,
            pinned,
            held,
            apart,
            steps,
            wanted,
            cell,
            Estimate(triangles, coordinates, tiling, cell),
            triangles.Count)
        {
            Boundary = (pinned.Count, continued),
            SetApart = apart.Count,
            Also = also,
            Erosion = weather,
        };
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
            step.Y <= 0f ||
            _apart.Contains(Corners(a, b, c)))
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
        // that holds them. Measured as a distance from the corner rather than as a share of
        // the triangle: a barycentric weight is only a distance when the triangle is
        // roughly equilateral, and a village's ground is mostly long thin strips, where the
        // weight ran out long before the world distance did and damped the whole strip.
        bool heldA = _held.Contains(Key(a));
        bool heldB = _held.Contains(Key(b));
        bool heldC = _held.Contains(Key(c));

        Vector3 normalA = NormalAt(Key(a), a, b, c);
        Vector3 normalB = NormalAt(Key(b), a, b, c);
        Vector3 normalC = NormalAt(Key(c), a, b, c);

        // How wide a cell is in texture coordinates, for averaging the field over one.
        float span = (step.X + step.Y) * 0.5f;
        bool marching = field is not null && depth > 0f;
        bool weathering = Erosion is not null;
        bool displacing = marching || weathering;

        Span<Vector2> polygon = stackalloc Vector2[16];
        Span<Vector2> clipped = stackalloc Vector2[16];

        var made = new Dictionary<(int U, int V), int>();

        // This call's own share of the "how far did it actually move" evidence, kept local
        // and merged once at the end. It is written once a vertex and there are a million
        // of them in an outdoor room, so touching the shared fields here would be a
        // contended write per vertex across every worker cutting the same floor.
        float cutFurthest = 0f;
        double cutTotal = 0;
        int cutCount = 0;

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

        Record(cutFurthest, cutTotal, cutCount);

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

                if (heldA)
                {
                    blend = MathF.Min(blend, Away(position, a));
                }

                if (heldB)
                {
                    blend = MathF.Min(blend, Away(position, b));
                }

                if (heldC)
                {
                    blend = MathF.Min(blend, Away(position, c));
                }

                if (marching && blend > 0f)
                {
                    // Downwards only: the lower half of the signed field cuts into the
                    // modelled surface and its upper half remains on that surface. `Over`
                    // has already moved mid grey to zero, so subtracting another half here
                    // would turn a level map into a half-depth depression. Besides sinking
                    // the whole material, that made every pinned edge climb back to the
                    // authored plane as a conspicuous one-cell ramp.
                    //
                    // Not a stylistic choice. A floor is the one surface other things rest
                    // on — a rug, a shadow decal, the foot of a chair — and the game
                    // places them flush with the plane the 1999 geometry describes. Relief
                    // that rises above that plane punches through every one of them, which
                    // in the hotel lobby was three thousand pixels of tile flickering
                    // through the rug at the resolution it was measured at. Remapping the
                    // signed field's lower half over the full depth keeps that depth for
                    // mortar and grooves without moving a neutral surface or raising a
                    // crest through something resting on it.
                    float cut = MathF.Min(field!.Over(uv.X, uv.Y, span) * 2f, 0f);
                    float shift = cut * depth * blend;

                    position += normal * shift;

                    float far = MathF.Abs(shift);

                    // Into this call's own tally, merged once at the end. See Tessellate:
                    // the callers run concurrently and this runs once a vertex.
                    cutFurthest = MathF.Max(cutFurthest, far);
                    cutTotal += far;
                    cutCount++;
                }

                // And what the weather has taken off here, straight down, and not subject
                // to the fade above.
                //
                // That fade holds a vertex to the authored plane wherever two lattices meet
                // — at every change of texture, and the ground of an outdoor room changes
                // texture every few metres, since its five steps of grass-to-dirt are five
                // separate pictures chosen per triangle. A field faded out at all of those
                // would be a field of bumps with a crease around each. It does not need the
                // fade, because it is not a lattice: it is one smooth function of where a
                // point is in the world, so two lattices either side of a seam are reading
                // the same answer, and what holds it to the authored plane at a wall, at a
                // rim, under a model and under the walk bitmap is held in the field itself.
                if (weathering)
                {
                    float weather = Weather(position);

                    if (weather < 0f)
                    {
                        position.Y += weather;

                        float far = -weather;

                        cutFurthest = MathF.Max(cutFurthest, far);
                        cutTotal += far;
                        cutCount++;
                    }
                }
            }

            made[key] = vertices.Count;
            vertices.Add(new ReliefVertex(position, normal, uv));

            return vertices.Count - 1;
        }
    }

    /// <summary>How far down the weather takes a point, in world units.</summary>
    /// <param name="at">Where, in world space.</param>
    /// <returns>Nought or less; nought where the room has no weather.</returns>
    private float Weather(Vector3 at) =>
        Erosion is null ? 0f : MathF.Min(Erosion.At(at.X, at.Z), 0f);

    /// <summary>How much of the displacement survives this far from something pinned.</summary>
    private static float Held(float weight, float fade) =>
        fade <= 0f ? 1f : Math.Clamp(weight * fade, 0f, 1f);

    /// <summary>How much survives this far from a held corner: none at it, all a cell away.</summary>
    private float Away(Vector3 point, Vector3 corner) =>
        Math.Clamp((point - corner).Length() / Cell, 0f, 1f);

    /// <summary>The area-weighted median of one axis of a texture's tiling samples.</summary>
    /// <param name="seen">Every triangle's own rate and how much surface it stands for.</param>
    /// <param name="acrossU">Which axis.</param>
    /// <returns>The rate half the surface is finer than.</returns>
    private static double Middle(List<(double U, double V, double Weight)> seen, bool acrossU)
    {
        List<(double Rate, double Weight)> sorted =
            [.. seen.Select(one => (acrossU ? one.U : one.V, one.Weight)).OrderBy(one => one.Item1)];

        double whole = sorted.Sum(one => one.Weight);
        double running = 0;

        foreach ((double rate, double weight) in sorted)
        {
            running += weight;

            if (running >= whole / 2)
            {
                return rate;
            }
        }

        return sorted.Count > 0 ? sorted[^1].Rate : 1;
    }

    /// <summary>Whether one rate is close enough to another to share a lattice.</summary>
    private static bool Agrees(double own, double shared) =>
        own > 1e-9 && shared > 1e-9 && own / shared is > (1.0 / 3.0) and < 3.0;

    /// <summary>A triangle's three corners, in an order that does not depend on winding.</summary>
    private static ((int, int, int), (int, int, int), (int, int, int)) Corners(
        Vector3 a, Vector3 b, Vector3 c)
    {
        (int, int, int)[] keys = [Key(a), Key(b), Key(c)];

        Array.Sort(keys);

        return (keys[0], keys[1], keys[2]);
    }

    /// <summary>A quantized key back to the point it stands for.</summary>
    private static Vector3 At((int X, int Y, int Z) key) =>
        new(key.X / Grain, key.Y / Grain, key.Z / Grain);

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
        vertices.Add(new ReliefVertex(Down(a), NormalAt(Key(a), a, b, c), ua));
        vertices.Add(new ReliefVertex(Down(b), NormalAt(Key(b), a, b, c), ub));
        vertices.Add(new ReliefVertex(Down(c), NormalAt(Key(c), a, b, c), uc));

        indices.Add(0);
        indices.Add(1);
        indices.Add(2);
    }

    /// <summary>A corner with the weather taken off it.</summary>
    private Vector3 Down(Vector3 at) => at with { Y = at.Y + Weather(at) };

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

    /// <summary>How many triangles a cell size cuts a floor into.</summary>
    private static int Estimate(
        IReadOnlyList<(Vector3 A, Vector3 B, Vector3 C, string Texture)> triangles,
        IReadOnlyList<(Vector2 A, Vector2 B, Vector2 C)> coordinates,
        IReadOnlyDictionary<string, (double U, double V, double Weight)> tiling,
        float cell)
    {
        double total = 0;

        for (int i = 0; i < triangles.Count; i++)
        {
            if (!tiling.TryGetValue(triangles[i].Texture, out (double U, double V, double W) rate) ||
                rate.W <= 0)
            {
                continue;
            }

            double stepU = cell / Math.Max(rate.U / rate.W, 1e-6);
            double stepV = cell / Math.Max(rate.V / rate.W, 1e-6);

            if (stepU <= 0 || stepV <= 0)
            {
                continue;
            }

            (Vector2 ua, Vector2 ub, Vector2 uc) = coordinates[i];

            double across = Math.Abs(
                ((ub.X - ua.X) * (uc.Y - ua.Y)) - ((uc.X - ua.X) * (ub.Y - ua.Y))) / 2.0;

            double spanU = Math.Max(ua.X, Math.Max(ub.X, uc.X)) - Math.Min(ua.X, Math.Min(ub.X, uc.X));
            double spanV = Math.Max(ua.Y, Math.Max(ub.Y, uc.Y)) - Math.Min(ua.Y, Math.Min(ub.Y, uc.Y));

            // The same refusal the cut itself makes: a surface whose texture coordinates
            // ask for a lattice with more cells in it than this is left as the one triangle
            // it already was. Counting the lattice it asked for instead is what made the
            // village's estimate twenty-nine million.
            if (((spanU / stepU) + 1) * ((spanV / stepV) + 1) > MostCells)
            {
                total += 1;

                continue;
            }

            double inside = across / (stepU * stepV);
            double crossed = (spanU / stepU) + (spanV / stepV) + 1;

            total += (2 * inside) + (3 * crossed);
        }

        return (int)Math.Min(int.MaxValue, total);
    }

    /// <summary>The finest cell a budget affords over a floor.</summary>
    private static float Afforded(
        IReadOnlyList<(Vector3 A, Vector3 B, Vector3 C, string Texture)> triangles,
        IReadOnlyList<(Vector2 A, Vector2 B, Vector2 C)> coordinates,
        IReadOnlyDictionary<string, (double U, double V, double Weight)> tiling,
        int budget)
    {
        float cell = FinestCell;

        // Walk coarser until the estimate fits. A hundred steps of a twentieth is a factor
        // of a hundred and thirty, which covers the corpus from a bathroom to a village.
        for (int attempt = 0; attempt < 100; attempt++)
        {
            if (Estimate(triangles, coordinates, tiling, cell) <= budget)
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
