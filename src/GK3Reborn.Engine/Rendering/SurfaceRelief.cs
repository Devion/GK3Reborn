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
    /// <summary>Displaced, at a million triangles, and traced.</summary>
    /// <remarks>
    /// <para>
    /// A million against the ten to fifteen thousand a whole room has been until now. It
    /// sounds enormous and is not: this is a 1999 game running on hardware twenty-five years
    /// later, the vertex format is forty bytes, and the budget comes to about fifty
    /// megabytes of geometry for the one surface the camera spends its time looking along.
    /// Measured on the village, which is the largest paved area in the game: four hundred
    /// thousand buys seven and a half units a cell, a million buys four, and four million
    /// buys two — and the frame rate is 150 either way, because a static mesh of this size
    /// is nothing to draw. What it costs is about a second of the village's load.
    /// </para>
    /// <para>
    /// Four units a cell is about a third of a cobble, which is where a paved street stops
    /// reading as a painted plane. Every interior in the game is finer than that already:
    /// their floors are small enough to reach <see cref="ReliefPlan.FinestCell"/> and the
    /// budget never binds.
    /// </para>
    /// <para>
    /// Two million since relief stopped ending at the floor object: outdoors the verges,
    /// rock and roadside are cut too, and the same budget over twice the area would have
    /// meant coarser cobbles on the street that was already right. The measurements above
    /// scale — the frame rate does not notice and the load pays another second outdoors.
    /// </para>
    /// </remarks>
    public static ReliefSettings Default => new(true, 2_000_000, true);

    /// <summary>Nothing displaced.</summary>
    public static ReliefSettings Off => new(false, 1, false);
}

/// <summary>A vertex of a displaced surface, before it is given a lightmap coordinate.</summary>
/// <param name="Position">Where it ended up, in world space.</param>
/// <param name="Normal">The smoothed normal it was moved along.</param>
/// <param name="TexCoord">Its texture coordinate, interpolated across the source triangle.</param>
public readonly record struct ReliefVertex(Vector3 Position, Vector3 Normal, Vector2 TexCoord);

/// <summary>
/// The floor's triangles in buckets, so "does the floor go on past this edge?" is a local
/// question.
/// </summary>
/// <remarks>
/// Asked once per edge that no second triangle shares, which is two and a half thousand
/// times on the village and would otherwise be that many sweeps of three thousand
/// triangles. The buckets are square in the ground plane and a triangle goes in every one
/// its extent touches, because a village street is one triangle across several buckets.
/// </remarks>
internal sealed class TriangleGrid
{
    /// <summary>How far off the plane of a triangle a point may be and still be on it.</summary>
    /// <remarks>
    /// Two patches of ground that abut are not always at exactly the same height: the game's
    /// floors are laid by hand and a step of a unit between the street and the square in
    /// front of it is common. A unit is a couple of centimetres and well under the depth
    /// anything is displaced by, so accepting it costs nothing and refusing it would pin the
    /// join.
    /// </remarks>
    private const float Flush = 2f;

    /// <summary>How far past an edge to look for more floor.</summary>
    /// <remarks>
    /// Far enough to be inside the next triangle rather than on the line between them, and
    /// short enough not to step over a gap. Three quarters of a unit is under two
    /// centimetres.
    /// </remarks>
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
    /// <para>
    /// A backstop against a texture coordinate nobody can honour, not a budget: the budget
    /// bounds the floor as a whole and is solved before this is reached.
    /// </para>
    /// <para>
    /// It used to be sixty-five thousand, which a legitimate triangle can pass — a floor
    /// laid as one slab four hundred cells across is inside it and then over it — and
    /// passing it means the triangle comes out flat while its neighbours are displaced. It
    /// also made the cost of cutting a floor rise as the cells were made coarser, because a
    /// triangle refused at one cell size is cut into everything it asked for at the next,
    /// and a budget cannot be solved against that.
    /// </para>
    /// </remarks>
    private const int MostCells = 4_194_304;

    /// <summary>How flat a non-floor triangle must lie to have its relief cut.</summary>
    /// <remarks>
    /// The vertical component of the unit normal: 0.35 keeps ground and rocky slopes up
    /// to about seventy degrees and refuses walls, facades and steep roofs — which tear
    /// from their neighbours at every corner two lattices meet, and whose texture-space
    /// spans against a ground-solved lattice are what blew one village to thirty-six
    /// million triangles.
    /// </remarks>
    private const float LiesFlat = 0.35f;

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

    /// <summary>Triangles whose tiling disagrees with their texture's, left as they were.</summary>
    private readonly HashSet<((int, int, int), (int, int, int), (int, int, int))> _apart;
    private readonly Dictionary<string, Vector2> _steps;
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
    /// <remarks>
    /// Cutting a floor up and not moving it is a failure that looks like success from every
    /// other number the loader prints: a million triangles, a sensible cell, and a picture
    /// identical to the flat one. It has happened twice — once from height maps that were
    /// never loaded, once from a fade that held nine tenths of the floor down — and both
    /// times the evidence had to be dug for. This is that evidence, reported beside the
    /// count.
    /// </remarks>
    public float Moved { get; private set; }

    /// <summary>How far the average displaced vertex moved, in world units.</summary>
    public float MovedTypically => _movedCount > 0 ? (float)(_movedTotal / _movedCount) : 0f;

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
    /// <remarks>
    /// The same test the plan applied when it gathered, so the estimate and the cut
    /// count the same set. The floor object is never refused: its edges were solved
    /// for from the start.
    /// </remarks>
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
    /// <remarks>
    /// The floor was the whole story until the reconstructed horizon made the rooms the
    /// sharpest thing on screen: outdoors, the ground runs past the <c>floor=</c> object
    /// into verges, rock and roadside that carry the same displaced-class textures and
    /// were left flat for no reason a player can see.
    /// </remarks>
    private Func<BspSurface, bool>? Also { get; init; }

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
    /// <returns>The plan, or null when there is no floor to displace.</returns>
    /// <remarks>
    /// The cell is bought with the budget rather than fixed, which is what lets one number
    /// serve rooms an order of magnitude apart in paved area: the hotel lobby's four hundred
    /// and fifty thousand square units come out at the finest cell allowed and the village
    /// forecourt's two and a half million at about four, with nobody tuning a scene.
    /// </remarks>
    public static ReliefPlan? For(
        BspFile? scene, string? floorObject, Func<string, bool> deep, int budget,
        Func<BspSurface, bool>? also = null)
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

                if (blend > 0f)
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

                    Moved = MathF.Max(Moved, far);
                    _movedTotal += far;
                    _movedCount++;
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
    /// <remarks>
    /// A factor of three either way. Wide, because a texture stretched half again over one
    /// patch of ground is ordinary and its cells are only half again the size; the case
    /// this is for is the one off by a hundred.
    /// </remarks>
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

    /// <summary>How many triangles a cell size cuts a floor into.</summary>
    /// <remarks>
    /// <para>
    /// Counted in texture space, one source triangle at a time, because that is where the
    /// lattice is. A convex shape laid over a grid covers about its own area in cells plus
    /// one for every grid line it crosses; a cell wholly inside comes out as two triangles
    /// and a cell the shape's edge runs through as about three.
    /// </para>
    /// <para>
    /// <b>Both corrections matter and the village needed both.</b> The area term used the
    /// texture's average stretch, so triangles tiled finer than that average — a third of
    /// the ground outside the hotel — were cut into cells smaller than the one the budget
    /// bought. And the crossings term was a perimeter over the cell size, which is right
    /// for a triangle lying square to the lattice and half of the answer for one lying
    /// across it: a long thin strip of road at an angle steps through a line of the lattice
    /// in both directions at once. Together they made RC1 come out at 1,107,726 triangles
    /// against an estimate of 392,407, which is not a budget.
    /// </para>
    /// </remarks>
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
