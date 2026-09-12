// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Numerics;
using GK3Reborn.Formats.Scenes;

namespace GK3Reborn.Rendering;

/// <summary>Somewhere the ground may not move, because something is standing on it.</summary>
/// <param name="At">Where it stands, on X and Z.</param>
/// <param name="Radius">How much ground it holds still around that, in world units.</param>
public readonly record struct GroundAnchor(Vector2 At, float Radius);

/// <summary>
/// What the weather would have done to a room's ground, as a depth to take off it.
/// </summary>
public sealed class GroundErosion
{
    /// <summary>The finest a field cell is worth making, in world units.</summary>
    private const float FinestCell = 12f;

    /// <summary>The most cells a field may be across, either way.</summary>
    private const int WidestGrid = 640;

    /// <summary>How many cells the carve takes to come back after something holding it.</summary>
    private const int HeldFor = 3;

    /// <summary>
    /// How near the ground something has to be to hold it still, in world units.
    /// </summary>
    private const float Reaches = 80f;

    /// <summary>
    /// How level a piece of ground has to be to weather at all, as the rise of its normal.
    /// </summary>
    private const float TooSteep = 0.78f;

    /// <summary>How much of the carve is water finding its way down the slope.</summary>
    private const float FromFlow = 0.5f;

    /// <summary>How much of it is a slope losing what will not stay on it.</summary>
    private const float FromSlope = 0.6f;

    /// <summary>And how much is a hollow going on being a hollow.</summary>
    private const float FromHollow = 0.4f;

    /// <summary>The slope at which weathering starts, as a rise over run.</summary>
    private const float BarelySteep = 0.08f;

    /// <summary>And the slope at which it is in full.</summary>
    private const float FullySteep = 0.34f;

    /// <summary>The gradient past which the ground holds rather than weathers.</summary>
    private const float HoldsFrom = 0.80f;

    /// <summary>And the gradient past which it gives up nothing at all.</summary>
    private const float HoldsFully = 2.67f;

    /// <summary>How much ground has to drain through a cell before it is a channel.</summary>
    private const float ChannelStarts = 2.2f;

    /// <summary>And how much before it is as deep a channel as this will cut.</summary>
    private const float ChannelFull = 6.5f;

    /// <summary>How far below its neighbours a cell is before it counts as a hollow.</summary>
    private const float HollowStarts = 2f;

    /// <summary>And how far below before it is as much of one as this reads.</summary>
    private const float HollowFull = 30f;

    /// <summary><b>Nothing is taken off level ground that is neither.</b></summary>
    private const float Nowhere = 0f;

    /// <summary>The three wavelengths the weathering is made of, in world units.</summary>
    private static readonly float[] Octaves = [320f, 130f, 56f];

    /// <summary>And what each contributes.</summary>
    private static readonly float[] Weights = [0.45f, 0.33f, 0.22f];

    private readonly float[] _carve;
    private readonly int _wide;
    private readonly int _high;
    private readonly float _cell;
    private readonly Vector2 _corner;

    private GroundErosion(float[] carve, int wide, int high, float cell, Vector2 corner)
    {
        _carve = carve;
        _wide = wide;
        _high = high;
        _cell = cell;
        _corner = corner;
    }

    /// <summary>How deep the deepest cell is cut, in world units.</summary>
    public float Deepest { get; private init; }

    /// <summary>How deep the average cut cell is cut, in world units.</summary>
    public float Typically { get; private init; }

    /// <summary>How many cells the field is.</summary>
    public int Cells => _wide * _high;

    /// <summary>How wide one cell is, in world units.</summary>
    public float Cell => _cell;

    /// <summary>How many of the field's cells are free to move at all.</summary>
    public int Free { get; private init; }

    /// <summary>How many were held still by something standing on them.</summary>
    public int Held { get; private init; }

    /// <summary>
    /// Works out what to take off a room's ground.
    /// </summary>
    /// <param name="ground">
    /// The ground triangles that will actually be cut, with the texture each carries. What
    /// is not in here cannot move, and is therefore what holds the rest still.
    /// </param>
    /// <param name="scene">The room, for everything standing on that ground.</param>
    /// <param name="erodes">
    /// How readily a texture's ground erodes, nought to one. <see cref="GroundVariation"/>
    /// answers this: a lawn and a bank of earth erode, a made road barely, a laid floor not
    /// at all.
    /// </param>
    /// <param name="anchors">Models the scene places, which hold the ground under them.</param>
    /// <param name="walkable">
    /// Whether an actor may stand at a point, or null where the room says nobody may.
    /// </param>
    /// <param name="depth">The most that may be taken off anywhere, in world units.</param>
    /// <param name="seed">Something stable about the room, so a room erodes the same way twice.</param>
    /// <returns>The field, or null when there is nothing here that may move.</returns>
    public static GroundErosion? For(
        IReadOnlyList<(Vector3 A, Vector3 B, Vector3 C, string Texture)> ground,
        BspFile scene,
        Func<string, float> erodes,
        IReadOnlyList<GroundAnchor> anchors,
        Func<float, float, bool>? walkable,
        float depth,
        int seed)
    {
        ArgumentNullException.ThrowIfNull(ground);
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(erodes);
        ArgumentNullException.ThrowIfNull(anchors);

        if (ground.Count == 0 || depth <= 0f)
        {
            return null;
        }

        var least = new Vector2(float.MaxValue);
        var most = new Vector2(float.MinValue);

        foreach ((Vector3 a, Vector3 b, Vector3 c, string _) in ground)
        {
            foreach (Vector3 point in (Span<Vector3>)[a, b, c])
            {
                least = Vector2.Min(least, new Vector2(point.X, point.Z));
                most = Vector2.Max(most, new Vector2(point.X, point.Z));
            }
        }

        Vector2 span = most - least;

        if (span.X <= 0f || span.Y <= 0f)
        {
            return null;
        }

        float cell = MathF.Max(FinestCell, MathF.Max(span.X, span.Y) / WidestGrid);

        // A cell of margin either side, so that the rim of the ground has somewhere to be
        // held from and a bilinear sample at the very edge has four cells to read.
        Vector2 corner = least - new Vector2(cell);
        int wide = (int)MathF.Ceiling(span.X / cell) + 3;
        int high = (int)MathF.Ceiling(span.Y / cell) + 3;

        int count = wide * high;

        float[] height = new float[count];
        float[] firmness = new float[count];
        bool[] moves = new bool[count];
        bool[] stays = new bool[count];

        Array.Fill(height, float.MinValue);

        // The ground itself: how high it is, and how readily it erodes. The highest of
        // whatever covers a cell, because where a room lays one piece of ground over
        // another — a bridge, a doorstep, a rock shelf — the one that is seen is the top.
        foreach ((Vector3 a, Vector3 b, Vector3 c, string texture) in ground)
        {
            float erodible = Math.Clamp(erodes(texture), 0f, 1f);

            Vector3 lie = Vector3.Cross(b - a, c - a);
            float length = lie.Length();
            bool steep = length < 1e-9f || MathF.Abs(lie.Y) / length < TooSteep;

            Cover(a, b, c, (index, y) =>
            {
                // Steep ground is ground: it is part of what may be cut, so that the
                // texture's own relief still reaches it, and part of the height field, so
                // that water runs down it. It is only the weathering it is kept out of, and
                // it keeps its own neighbours out too.
                if (steep)
                {
                    stays[index] = true;
                }

                if (y <= height[index])
                {
                    return;
                }

                height[index] = y;
                firmness[index] = erodible;
                moves[index] = true;
            });

            // And along its edges, because the face of a rock standing out of a meadow is
            // the case this whole rule exists for and is exactly the case Cover cannot
            // see: see Trace.
            if (steep)
            {
                Trace(a, b, c, index => stays[index] = true);
            }
        }

        // And everything else the room draws. A surface near the ground holds it: that is
        // one rule for walls, kerbs, steps, tomb slabs, the feet of buildings and the
        // shadow decals the artists laid flat on the floor, none of which is named here and
        // all of which would be left hanging by ground that fell away beneath them.
        HashSet<(int, int, int)> drawn = [];

        foreach ((Vector3 a, Vector3 b, Vector3 c, string _) in ground)
        {
            drawn.Add(Corners(a, b, c));
        }

        foreach (BspPolygon polygon in scene.Polygons)
        {
            foreach ((ushort ia, ushort ib, ushort ic) in scene.Triangulate(polygon))
            {
                Vector3 a = scene.Vertices[ia];
                Vector3 b = scene.Vertices[ib];
                Vector3 c = scene.Vertices[ic];

                if (drawn.Contains(Corners(a, b, c)))
                {
                    continue;
                }

                float lowest = MathF.Min(a.Y, MathF.Min(b.Y, c.Y));
                float highest = MathF.Max(a.Y, MathF.Max(b.Y, c.Y));

                Cover(a, b, c, (index, _) => Hold(index));

                // And along its edges. A wall is two triangles standing on end: it has no
                // area at all on X and Z, so Cover drops it and the rule above held
                // nothing. See Trace.
                Trace(a, b, c, Hold);

                void Hold(int index)
                {
                    if (height[index] <= float.MinValue)
                    {
                        return;
                    }

                    // Near the ground in the sense that matters: the thing's own extent
                    // reaches a couple of metres of the ground under it. A roof twenty feet
                    // up covers the same cells and holds nothing.
                    if (lowest - Reaches <= height[index] && highest + Reaches >= height[index])
                    {
                        stays[index] = true;
                    }
                }
            }
        }

        // What the scene stands on its ground. A prop, a tree, a tomb, one of the facades a
        // dressing table puts along a road: all of them were placed against the height this
        // mesh has now, and none of them is in the room's own geometry to be found above.
        foreach (GroundAnchor anchor in anchors)
        {
            float radius = MathF.Max(anchor.Radius, cell);

            int firstX = Column(anchor.At.X - radius);
            int lastX = Column(anchor.At.X + radius);
            int firstZ = Row(anchor.At.Y - radius);
            int lastZ = Row(anchor.At.Y + radius);

            for (int x = Math.Max(firstX, 0); x <= Math.Min(lastX, wide - 1); x++)
            {
                for (int z = Math.Max(firstZ, 0); z <= Math.Min(lastZ, high - 1); z++)
                {
                    if (Vector2.Distance(Middle(x, z), anchor.At) <= radius)
                    {
                        stays[(z * wide) + x] = true;
                    }
                }
            }
        }

        // And wherever an actor may put their feet. The walked height is read from the
        // original geometry — WalkFloor takes it off the BSP and knows nothing of any of
        // this — so ground that drops under the walk bitmap drops out from under everybody
        // in the room.
        if (walkable is not null)
        {
            for (int z = 0; z < high; z++)
            {
                for (int x = 0; x < wide; x++)
                {
                    Vector2 at = Middle(x, z);

                    if (walkable(at.X, at.Y))
                    {
                        stays[(z * wide) + x] = true;
                    }
                }
            }
        }

        // A cell of ground that touches something which cannot move is itself held.
        //
        // Without this the fade starts *at* the wall rather than one cell inside the
        // ground, so the very edge of the ground — the row of cells against a wall, against
        // the rim, against the foot of a cliff — still carves at a third of the depth.
        // A third of the depth at the one place the ground has to meet something exactly is
        // the whole of what the hold was for; measured on the tests, the rim of a hillside
        // was dropping half a unit and the foot of a cliff nearly two.
        //
        // Over a copy, so that the expansion is one cell from what was held rather than a
        // flood that eats inwards a cell per column.
        bool[] against = [.. stays];

        for (int z = 0; z < high; z++)
        {
            for (int x = 0; x < wide; x++)
            {
                int at = (z * wide) + x;

                if (!moves[at] || stays[at])
                {
                    continue;
                }

                // Eight ways, not four: a cell touching a wall only at its corner is as
                // much against it as one touching it along an edge, and a four-way test
                // leaves a diagonal staircase of cells carving into every corner.
                for (int dz = -1; dz <= 1 && !against[at]; dz++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx;
                        int nz = z + dz;

                        if (nx < 0 || nz < 0 || nx >= wide || nz >= high)
                        {
                            continue;
                        }

                        int next = (nz * wide) + nx;

                        if (!moves[next] || stays[next])
                        {
                            against[at] = true;
                            break;
                        }
                    }
                }
            }
        }

        stays = against;

        // How far each cell is from anything holding it, counted in cells and given up on
        // past the reach. Everything that is not ground counts as held, so this measures
        // the rim of the ground as well as what stands on it.
        int[] away = Distances(moves, stays, wide, high);

        float[] carve = new float[count];
        float deepest = 0f;
        double total = 0;
        int cut = 0;
        int free = 0;
        int held = 0;

        int[] order = Draining(height, moves, count);
        float[] flow = Accumulate(height, moves, order, wide, high);

        for (int z = 0; z < high; z++)
        {
            for (int x = 0; x < wide; x++)
            {
                int index = (z * wide) + x;

                if (!moves[index])
                {
                    continue;
                }

                free++;

                if (away[index] <= 0)
                {
                    held++;
                    continue;
                }

                // A channel where the ground above drains through, cut deeper the more of
                // it there is. Logarithmic, because a catchment doubles in area for every
                // step down a valley and a linear reading would make one gully out of the
                // lowest cell and nothing anywhere else.
                float drains = MathF.Log(1f + flow[index]);
                float channel = Smooth(ChannelStarts, ChannelFull, drains);

                float grade = Slope(height, moves, x, z, wide, high, cell);

                // And the slope weathering where it lies. The steeper it is the less soil
                // stays on it, which is the whole of why a 1999 ramp reads as a ramp: it is
                // as smooth at forty degrees as it is at nothing.
                float steep = Smooth(BarelySteep, FullySteep, grade);

                // And a hollow, which is where what came off the slope went. Measured as
                // how far a cell lies below the ground around it, so it deepens what the
                // artists already dished and does nothing at all to what they did not.
                float hollow = Smooth(
                    HollowStarts,
                    HollowFull,
                    Around(height, moves, x, z, wide, high) - height[index]);

                Vector2 at = Middle(x, z);
                float rough = Fractal(at, seed);

                float amount = Nowhere +
                    (FromFlow * channel) +
                    (FromSlope * steep * rough) +
                    (FromHollow * hollow * rough);

                // Up to a point. Past about forty degrees the ground holds what is on it
                // by being rock rather than soil, and past seventy it gives up nothing at
                // all: a cliff face is not weathered, it is undercut, and to take a depth
                // off it here is to move rock out of the hill it grows from. The
                // triangle-by-triangle rule above holds anything the artists modelled as a
                // cliff; this is the taper between that and a bank, and it belongs to the
                // cell and not to a vertex — a vertex cannot be asked about slope without
                // the surfaces meeting there disagreeing about the answer. See
                // <c>ReliefPlan.Weather</c>.
                float holds = 1f - Smooth(HoldsFrom, HoldsFully, grade);

                carve[index] =
                    -depth * Math.Clamp(amount, 0f, 1f) * firmness[index] * holds;
            }
        }

        // Once over a three-by-three, which takes the corners off a field the tessellation
        // is about to read between cells: its own cells are a few units where this one's
        // are twelve. Once and not twice — a second pass costs most of the finest octave,
        // which is the one that is a rut rather than a valley.
        carve = Soften(carve, moves, wide, high);

        // The hold, applied after the softening and not before it.
        //
        // A three-by-three mean does not respect a zero: run it over a field that has
        // already been held down and it carries a little of every neighbour back into the
        // cells that were held, which is exactly the cells where the answer has to be
        // nothing at all. Measured: the rim of a hillside came out at half a unit and the
        // foot of a cliff at nearly two, which is a gap under whatever stands there.
        for (int i = 0; i < count; i++)
        {
            if (!moves[i])
            {
                continue;
            }

            float hold = Math.Clamp(away[i] / (float)HeldFor, 0f, 1f);

            carve[i] *= hold;

            float taken = -carve[i];

            if (taken > 0f)
            {
                deepest = MathF.Max(deepest, taken);
                total += taken;
                cut++;
            }
        }

        return new GroundErosion(carve, wide, high, cell, corner)
        {
            Deepest = deepest,
            Typically = cut > 0 ? (float)(total / cut) : 0f,
            Free = free,
            Held = held,
        };

        Vector2 Middle(int x, int z) =>
            corner + new Vector2((x + 0.5f) * cell, (z + 0.5f) * cell);

        int Column(float x) => (int)MathF.Floor((x - corner.X) / cell);

        int Row(float z) => (int)MathF.Floor((z - corner.Y) / cell);

        // Every cell a triangle's edges pass through, whether or not any cell's middle is
        // under it.
        //
        // <see cref="Cover"/> works by cell middles, which is the right answer for how
        // high the ground is and which piece of ground owns a cell. It is the wrong answer
        // for what holds the ground still: a triangle narrower than a cell claims nothing,
        // and one standing on end has no area on X and Z at all and is dropped outright.
        // Those are precisely the triangles that hold — a wall, a kerb, the face of a rock
        // standing out of a meadow — so the meadow carved to full depth right up to the
        // rock's foot and left a sliver of the rock standing in the gap. Reported at
        // Château de Blanchefort as grey slivers along every ridge, and invisible to
        // reasoning because the rule reads as though it already covered them.
        void Trace(Vector3 a, Vector3 b, Vector3 c, Action<int> onto)
        {
            Edge(a, b);
            Edge(b, c);
            Edge(c, a);

            void Edge(Vector3 from, Vector3 to)
            {
                // Half a cell a step, so no cell the edge crosses is stepped over.
                float span = Vector2.Distance(
                    new Vector2(from.X, from.Z), new Vector2(to.X, to.Z));

                int steps = (int)MathF.Ceiling(span / (cell * 0.5f));

                for (int i = 0; i <= steps; i++)
                {
                    Vector3 at = steps == 0
                        ? from
                        : Vector3.Lerp(from, to, i / (float)steps);

                    int x = Column(at.X);
                    int z = Row(at.Z);

                    if (x >= 0 && z >= 0 && x < wide && z < high)
                    {
                        onto((z * wide) + x);
                    }
                }
            }
        }

        // Every cell whose middle lies under a triangle, with the height of the triangle
        // there. By the middle rather than by any overlap: two triangles sharing an edge
        // would otherwise both claim the cells along it, and the one that wins would be
        // whichever came last rather than whichever is on top.
        void Cover(Vector3 a, Vector3 b, Vector3 c, Action<int, float> onto)
        {
            int firstX = Math.Max(Column(MathF.Min(a.X, MathF.Min(b.X, c.X))), 0);
            int lastX = Math.Min(Column(MathF.Max(a.X, MathF.Max(b.X, c.X))), wide - 1);
            int firstZ = Math.Max(Row(MathF.Min(a.Z, MathF.Min(b.Z, c.Z))), 0);
            int lastZ = Math.Min(Row(MathF.Max(a.Z, MathF.Max(b.Z, c.Z))), high - 1);

            float area = ((b.X - a.X) * (c.Z - a.Z)) - ((c.X - a.X) * (b.Z - a.Z));

            if (MathF.Abs(area) < 1e-9f)
            {
                return;
            }

            for (int x = firstX; x <= lastX; x++)
            {
                for (int z = firstZ; z <= lastZ; z++)
                {
                    Vector2 at = Middle(x, z);

                    float towardsB =
                        (((at.X - a.X) * (c.Z - a.Z)) - ((c.X - a.X) * (at.Y - a.Z))) / area;
                    float towardsC =
                        (((b.X - a.X) * (at.Y - a.Z)) - ((at.X - a.X) * (b.Z - a.Z))) / area;
                    float towardsA = 1f - towardsB - towardsC;

                    if (towardsA < 0f || towardsB < 0f || towardsC < 0f)
                    {
                        continue;
                    }

                    onto(
                        (z * wide) + x,
                        (a.Y * towardsA) + (b.Y * towardsB) + (c.Y * towardsC));
                }
            }
        }
    }

    /// <summary>How much to take off the ground at a point, in world units.</summary>
    /// <param name="x">Where, on X.</param>
    /// <param name="z">Where, on Z.</param>
    /// <returns>Nought or less; nought everywhere outside the field.</returns>
    public float At(float x, float z)
    {
        float alongX = ((x - _corner.X) / _cell) - 0.5f;
        float alongZ = ((z - _corner.Y) / _cell) - 0.5f;

        int firstX = (int)MathF.Floor(alongX);
        int firstZ = (int)MathF.Floor(alongZ);

        float intoX = alongX - firstX;
        float intoZ = alongZ - firstZ;

        float near = Lerp(Read(firstX, firstZ), Read(firstX + 1, firstZ), intoX);
        float far = Lerp(Read(firstX, firstZ + 1), Read(firstX + 1, firstZ + 1), intoX);

        return Lerp(near, far, intoZ);
    }

    private static float Lerp(float from, float to, float by) => from + ((to - from) * by);

    private float Read(int x, int z) =>
        x < 0 || z < 0 || x >= _wide || z >= _high ? 0f : _carve[(z * _wide) + x];

    /// <summary>The corners of a triangle, in an order that does not depend on winding.</summary>
    /// <param name="a">First corner.</param>
    /// <param name="b">Second corner.</param>
    /// <param name="c">Third corner.</param>
    /// <returns>A key that two windings of the same triangle both produce.</returns>
    private static (int, int, int) Corners(Vector3 a, Vector3 b, Vector3 c)
    {
        int first = Key(a);
        int second = Key(b);
        int third = Key(c);

        if (first > second)
        {
            (first, second) = (second, first);
        }

        if (second > third)
        {
            (second, third) = (third, second);
        }

        if (first > second)
        {
            (first, second) = (second, first);
        }

        return (first, second, third);
    }

    /// <summary>A point, quantized to something that can be compared.</summary>
    /// <param name="at">The point.</param>
    /// <returns>A hash of it at a sixteenth of a unit.</returns>
    private static int Key(Vector3 at) =>
        HashCode.Combine(
            (int)MathF.Round(at.X * 16f),
            (int)MathF.Round(at.Y * 16f),
            (int)MathF.Round(at.Z * 16f));

    /// <summary>How far each movable cell is from one that is held, in cells.</summary>
    /// <param name="moves">Which cells are ground that may move.</param>
    /// <param name="stays">Which of them something is standing on.</param>
    /// <param name="wide">Cells across.</param>
    /// <param name="high">Cells down.</param>
    /// <returns>Distance in cells, saturating at <see cref="HeldFor"/>.</returns>
    private static int[] Distances(bool[] moves, bool[] stays, int wide, int high)
    {
        int[] away = new int[moves.Length];
        Queue<int> edge = new();

        for (int i = 0; i < moves.Length; i++)
        {
            // Held, or not ground at all. Both are a wall as far as the fade is concerned:
            // the second is the rim of the ground, past which there is nothing to tear away
            // from because there is nothing there.
            if (!moves[i] || stays[i])
            {
                away[i] = 0;
                edge.Enqueue(i);
            }
            else
            {
                away[i] = HeldFor;
            }
        }

        while (edge.Count > 0)
        {
            int at = edge.Dequeue();
            int step = away[at] + 1;

            if (step > HeldFor)
            {
                continue;
            }

            int x = at % wide;
            int z = at / wide;

            Reach(x - 1, z);
            Reach(x + 1, z);
            Reach(x, z - 1);
            Reach(x, z + 1);

            void Reach(int nx, int nz)
            {
                if (nx < 0 || nz < 0 || nx >= wide || nz >= high)
                {
                    return;
                }

                int next = (nz * wide) + nx;

                if (away[next] <= step)
                {
                    return;
                }

                away[next] = step;
                edge.Enqueue(next);
            }
        }

        return away;
    }

    /// <summary>The movable cells, highest first, which is the order water leaves them in.</summary>
    /// <param name="height">How high each cell is.</param>
    /// <param name="moves">Which cells are ground.</param>
    /// <param name="count">How many cells there are.</param>
    /// <returns>Their indices.</returns>
    private static int[] Draining(float[] height, bool[] moves, int count)
    {
        List<int> order = [];

        for (int i = 0; i < count; i++)
        {
            if (moves[i])
            {
                order.Add(i);
            }
        }

        int[] drain = [.. order];

        Array.Sort(drain, (left, right) => height[right].CompareTo(height[left]));

        return drain;
    }

    /// <summary>How much ground drains through each cell.</summary>
    /// <param name="height">How high each cell is.</param>
    /// <param name="moves">Which cells are ground.</param>
    /// <param name="order">The cells, highest first.</param>
    /// <param name="wide">Cells across.</param>
    /// <param name="high">Cells down.</param>
    /// <returns>One cell's worth of water for each cell, plus everything above it.</returns>
    private static float[] Accumulate(
        float[] height, bool[] moves, int[] order, int wide, int high)
    {
        float[] flow = new float[height.Length];

        foreach (int at in order)
        {
            flow[at] += 1f;

            int x = at % wide;
            int z = at / wide;

            int lowest = -1;
            float drop = 0f;

            for (int dz = -1; dz <= 1; dz++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dz == 0)
                    {
                        continue;
                    }

                    int nx = x + dx;
                    int nz = z + dz;

                    if (nx < 0 || nz < 0 || nx >= wide || nz >= high)
                    {
                        continue;
                    }

                    int next = (nz * wide) + nx;

                    if (!moves[next])
                    {
                        continue;
                    }

                    // Per unit of ground crossed, so that a diagonal step is not preferred
                    // for being longer. Water runs down the steepest way, not the longest.
                    float fall = (height[at] - height[next]) /
                                 (dx != 0 && dz != 0 ? 1.41421356f : 1f);

                    if (fall > drop)
                    {
                        drop = fall;
                        lowest = next;
                    }
                }
            }

            // Nowhere lower: a hollow, or the lip where the ground stops. The water stays,
            // which is what a hollow does, and nothing is passed on.
            if (lowest >= 0)
            {
                flow[lowest] += flow[at];
            }
        }

        return flow;
    }

    /// <summary>How high the ground is around a cell, not counting the cell itself.</summary>
    /// <param name="height">How high each cell is.</param>
    /// <param name="moves">Which cells are ground.</param>
    /// <param name="x">The cell's column.</param>
    /// <param name="z">Its row.</param>
    /// <param name="wide">Cells across.</param>
    /// <param name="high">Cells down.</param>
    /// <returns>The mean of the ring two cells out, or the cell's own height.</returns>
    private static float Around(float[] height, bool[] moves, int x, int z, int wide, int high)
    {
        float total = 0f;
        int seen = 0;

        // The ring rather than the block, and two cells out rather than one: what is wanted
        // is whether this cell sits below the ground *around* it, and a three-by-three mean
        // taken over a smooth slope is that slope's own height, which says nothing at all.
        for (int dz = -2; dz <= 2; dz++)
        {
            for (int dx = -2; dx <= 2; dx++)
            {
                if (Math.Abs(dx) != 2 && Math.Abs(dz) != 2)
                {
                    continue;
                }

                int nx = x + dx;
                int nz = z + dz;

                if (nx < 0 || nz < 0 || nx >= wide || nz >= high)
                {
                    continue;
                }

                int at = (nz * wide) + nx;

                if (!moves[at])
                {
                    continue;
                }

                total += height[at];
                seen++;
            }
        }

        return seen > 0 ? total / seen : height[(z * wide) + x];
    }

    /// <summary>How steep the ground is at a cell, as a rise over run.</summary>
    /// <param name="height">How high each cell is.</param>
    /// <param name="moves">Which cells are ground.</param>
    /// <param name="x">The cell's column.</param>
    /// <param name="z">Its row.</param>
    /// <param name="wide">Cells across.</param>
    /// <param name="high">Cells down.</param>
    /// <param name="cell">How wide a cell is, in world units.</param>
    /// <returns>Nought on the level.</returns>
    private static float Slope(
        float[] height, bool[] moves, int x, int z, int wide, int high, float cell)
    {
        float alongX = (Sample(x + 1, z) - Sample(x - 1, z)) / (2f * cell);
        float alongZ = (Sample(x, z + 1) - Sample(x, z - 1)) / (2f * cell);

        return MathF.Sqrt((alongX * alongX) + (alongZ * alongZ));

        float Sample(int nx, int nz)
        {
            if (nx < 0 || nz < 0 || nx >= wide || nz >= high)
            {
                return height[(z * wide) + x];
            }

            int at = (nz * wide) + nx;

            // A cell that is not ground has no height to compare against: reading the
            // sentinel would make every cell at the rim a cliff.
            return moves[at] ? height[at] : height[(z * wide) + x];
        }
    }

    /// <summary>Spreads a field over its neighbours once.</summary>
    /// <param name="field">What to soften.</param>
    /// <param name="moves">Which cells are ground; the rest hold nought and stay there.</param>
    /// <param name="wide">Cells across.</param>
    /// <param name="high">Cells down.</param>
    /// <returns>A new field.</returns>
    private static float[] Soften(float[] field, bool[] moves, int wide, int high)
    {
        float[] softened = new float[field.Length];

        for (int z = 0; z < high; z++)
        {
            for (int x = 0; x < wide; x++)
            {
                int at = (z * wide) + x;

                if (!moves[at])
                {
                    continue;
                }

                float total = 0f;
                int seen = 0;

                for (int dz = -1; dz <= 1; dz++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx;
                        int nz = z + dz;

                        if (nx < 0 || nz < 0 || nx >= wide || nz >= high)
                        {
                            continue;
                        }

                        // Cells that are not ground count as nought rather than being left
                        // out, so that the carve comes back to the authored plane at the
                        // rim instead of running off it at full depth.
                        total += field[(nz * wide) + nx];
                        seen++;
                    }
                }

                softened[at] = seen > 0 ? total / seen : field[at];
            }
        }

        return softened;
    }

    /// <summary>Three octaves of value noise over a point, in [0,1].</summary>
    /// <param name="at">Where, in world units.</param>
    /// <param name="seed">The room's own seed.</param>
    /// <returns>Nought to one.</returns>
    private static float Fractal(Vector2 at, int seed)
    {
        float total = 0f;

        for (int i = 0; i < Octaves.Length; i++)
        {
            total += Weights[i] * Value(at / Octaves[i], seed + i);
        }

        return Math.Clamp(total, 0f, 1f);
    }

    /// <summary>One octave: a number per cell of a grid, smoothed across it.</summary>
    /// <param name="at">Where, in cells of that octave.</param>
    /// <param name="seed">What to salt the hash with.</param>
    /// <returns>Nought to one.</returns>
    private static float Value(Vector2 at, int seed)
    {
        int x = (int)MathF.Floor(at.X);
        int z = (int)MathF.Floor(at.Y);

        float intoX = at.X - x;
        float intoZ = at.Y - z;

        intoX = intoX * intoX * (3f - (2f * intoX));
        intoZ = intoZ * intoZ * (3f - (2f * intoZ));

        float near = Lerp(Hash(x, z, seed), Hash(x + 1, z, seed), intoX);
        float far = Lerp(Hash(x, z + 1, seed), Hash(x + 1, z + 1, seed), intoX);

        return Lerp(near, far, intoZ);
    }

    /// <summary>A stable number in [0,1) for a cell.</summary>
    /// <param name="x">Column.</param>
    /// <param name="z">Row.</param>
    /// <param name="seed">The salt.</param>
    /// <returns>Nought to one.</returns>
    private static float Hash(int x, int z, int seed)
    {
        uint h = (uint)HashCode.Combine(x, z, seed);

        h ^= h >> 15;
        h *= 0x2c1b3c6dU;
        h ^= h >> 12;
        h *= 0x297a2d39U;
        h ^= h >> 15;

        return (h & 0xFFFFFFU) / (float)0x1000000U;
    }

    /// <summary>Smoothstep, since the shading language has one and this does not.</summary>
    /// <param name="from">Where it starts to rise.</param>
    /// <param name="to">Where it reaches one.</param>
    /// <param name="at">The value.</param>
    /// <returns>Nought to one.</returns>
    private static float Smooth(float from, float to, float at)
    {
        if (to <= from)
        {
            return at >= to ? 1f : 0f;
        }

        float t = Math.Clamp((at - from) / (to - from), 0f, 1f);

        return t * t * (3f - (2f * t));
    }
}
