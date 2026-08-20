using System.Numerics;

namespace GK3Reborn.Game.Navigation;

/// <summary>A route across a scene's floor.</summary>
/// <param name="ReachedGoal">
/// Whether the route arrives where it was asked to. False routes are a best effort at
/// getting close, which is what the original does when a door is shut: the actor walks up
/// to it rather than refusing to move.
/// </param>
/// <param name="Points">
/// Corners of the route in world space, from where the walk starts to where it ends. Each
/// is the middle of a boundary texel with its Y left at zero, because the boundary knows
/// nothing about the height of the floor it covers.
/// </param>
public readonly record struct WalkRoute(bool ReachedGoal, IReadOnlyList<Vector3> Points)
{
    /// <summary>No route at all: nowhere to start from, or nowhere to stand.</summary>
    public static WalkRoute None { get; } = new(false, []);

    /// <summary>Whether the route has anything to walk.</summary>
    public bool IsEmpty => Points.Count == 0;

    /// <summary>How far the walk is, in scene units.</summary>
    /// <returns>The summed length of the route's segments, ignoring height.</returns>
    public float Length()
    {
        float total = 0f;

        for (int i = 1; i < Points.Count; i++)
        {
            Vector3 step = Points[i] - Points[i - 1];
            total += MathF.Sqrt((step.X * step.X) + (step.Z * step.Z));
        }

        return total;
    }
}

/// <summary>
/// Finds a way across a scene's walk boundary.
/// </summary>
/// <remarks>
/// <para>
/// The algorithm is G-Engine's <c>WalkerBoundary::FindPath</c>: a breadth-first search
/// over the boundary's texels, then two passes of conditioning to make the result look
/// like something a person would walk. G-Engine's note on why it is not A* is worth
/// keeping — the graph is enormous and its edges are effectively unweighted, so the
/// heuristic buys nothing that the queue was not already giving.
/// </para>
/// <para>
/// The search runs on a sparse lattice first, taking every fourth texel, and halves that
/// only when it fails. Most walks are across open floor and find their way at the coarsest
/// setting; a doorway three texels wide is what forces the full grid. Intermediate texels
/// are still tested when stepping between lattice nodes, so a sparse search cannot walk
/// through a wall — it can only fail to find a gap.
/// </para>
/// <para>
/// Then the gradient earns its keep. Regions 0-7 measure distance from a wall, so the
/// interior nodes of the route are nudged towards lower indices until they are comfortably
/// clear of one, and the route is string-pulled: where three consecutive nodes have open
/// floor between the first and the third, the middle one is not doing any work. Without
/// both, a BFS path scrapes the walls and turns on every texel.
/// </para>
/// </remarks>
public static class WalkPath
{
    /// <summary>
    /// The order neighbours are considered in: the four sides, then the four corners.
    /// </summary>
    /// <remarks>
    /// The order decides which of several equally short routes comes out, so it is part of
    /// the behaviour rather than an implementation detail. This is G-Engine's order.
    /// </remarks>
    private static readonly (int X, int Y)[] Neighbours =
        [(0, 1), (0, -1), (1, 0), (-1, 0), (1, 1), (1, -1), (-1, 1), (-1, -1)];

    /// <summary>How many texels the first, sparsest search steps by.</summary>
    private const int InitialNodeSkip = 4;

    /// <summary>The region a conditioned node is happy to sit on.</summary>
    /// <remarks>
    /// Far enough from the wall that an actor of ordinary width clears it. Pushing all the
    /// way to zero would drag every route to the middle of the room.
    /// </remarks>
    private const int ClearOfWalls = 4;

    /// <summary>The highest region a string-pulled shortcut may cross.</summary>
    /// <remarks>
    /// A shortcut is a straight line with no conditioning of its own, so it is held to a
    /// tighter standard than a node the search chose: 7 and 8 are close enough to a wall
    /// that cutting a corner across them clips it.
    /// </remarks>
    private const int ShortcutCeiling = 6;

    /// <summary>How many nodes may be dropped from one place before moving along.</summary>
    /// <remarks>
    /// String pulling with no limit collapses a route to a single straight line wherever
    /// it can, which loses the shape of a room — the way a corridor bends, or a route that
    /// goes around a table rather than at it.
    /// </remarks>
    private const int MaxErasures = 3;

    /// <summary>Finds a way from one point to another.</summary>
    /// <param name="boundary">Where actors may stand.</param>
    /// <param name="from">Where the walk starts. Only X and Z are read.</param>
    /// <param name="to">Where it is trying to get to. Only X and Z are read.</param>
    /// <returns>
    /// The route. Either end is snapped to the nearest open texel first, so a walk that
    /// starts inside a wall — an actor placed badly by a script — still leaves it, and a
    /// click on a wall walks up to the wall.
    /// </returns>
    public static WalkRoute Find(WalkBoundary boundary, Vector3 from, Vector3 to)
    {
        ArgumentNullException.ThrowIfNull(boundary);

        if (boundary.NearestWalkableTexel(from) is not { } start ||
            boundary.NearestWalkableTexel(to) is not { } goal)
        {
            return WalkRoute.None;
        }

        List<(int X, int Y)> texels = [];
        bool reached = false;

        for (int skip = InitialNodeSkip; skip >= 1 && !reached; skip /= 2)
        {
            reached = Search(boundary, start, goal, skip, texels);
        }

        if (texels.Count == 0)
        {
            return WalkRoute.None;
        }

        AwayFromWalls(boundary, texels);

        // Two nodes can be nudged onto the same texel, which leaves a leg of the route with
        // no length in it. Harmless to walk and confusing to read.
        for (int i = texels.Count - 1; i > 0; i--)
        {
            if (texels[i] == texels[i - 1])
            {
                texels.RemoveAt(i);
            }
        }

        PullString(boundary, texels);

        return new WalkRoute(reached, [.. texels.Select(t => boundary.ToWorld(t.X, t.Y))]);
    }

    /// <summary>Breadth-first search over the boundary's texels.</summary>
    /// <param name="boundary">Where actors may stand.</param>
    /// <param name="start">The texel to start from.</param>
    /// <param name="goal">The texel to reach.</param>
    /// <param name="skip">How many texels a step covers.</param>
    /// <param name="path">Receives the route, start first, replacing whatever it held.</param>
    /// <returns>True if the goal was reached.</returns>
    /// <remarks>
    /// A failed search still fills <paramref name="path"/>, with the route to whichever
    /// node came closest to the goal. That is the difference between an actor who walks as
    /// far as the locked door and one who stands still when you click past it.
    /// </remarks>
    private static bool Search(
        WalkBoundary boundary,
        (int X, int Y) start,
        (int X, int Y) goal,
        int skip,
        List<(int X, int Y)> path)
    {
        path.Clear();

        int width = boundary.Width;
        int height = boundary.Height;

        if (width <= 0 || height <= 0)
        {
            return false;
        }

        // Both ends are snapped onto the lattice the search steps across; the true texels
        // are put back on the ends of the route afterwards, so the walk still finishes
        // where it was asked to rather than at the nearest multiple of the step.
        (int X, int Y) origin = (start.X / skip * skip, start.Y / skip * skip);
        (int X, int Y) target = (goal.X / skip * skip, goal.Y / skip * skip);

        int startIndex = (origin.Y * width) + origin.X;
        int goalIndex = (target.Y * width) + target.X;

        if (startIndex == goalIndex)
        {
            // Both ends landed on the same lattice node, so the walk is shorter than a
            // step. Take it if the line between them is open — at the sparsest setting
            // that is five texels, which is enough room for a wall to be in the way, and
            // if there is one the caller's next pass will search around it properly.
            if (!IsClear(boundary, start.X, start.Y, goal.X, goal.Y))
            {
                return false;
            }

            path.Add(goal);
            return true;
        }

        int[] parents = new int[width * height];
        bool[] closed = new bool[width * height];
        Queue<int> open = new();

        closed[startIndex] = true;
        open.Enqueue(startIndex);

        int closest = -1;
        long closestDistance = 0;
        bool found = false;

        while (open.Count > 0)
        {
            int current = open.Dequeue();

            if (current == goalIndex)
            {
                found = true;
                break;
            }

            int x = current % width;
            int y = current / width;

            long dx = target.X - x;
            long dy = target.Y - y;
            long distance = (dx * dx) + (dy * dy);

            if (closest < 0 || distance < closestDistance)
            {
                closest = current;
                closestDistance = distance;
            }

            foreach ((int offsetX, int offsetY) in Neighbours)
            {
                int neighbourX = x + (offsetX * skip);
                int neighbourY = y + (offsetY * skip);

                if (neighbourX < 0 || neighbourY < 0 || neighbourX >= width || neighbourY >= height)
                {
                    continue;
                }

                int neighbour = (neighbourY * width) + neighbourX;

                if (closed[neighbour] || !IsClear(boundary, x, y, neighbourX, neighbourY))
                {
                    continue;
                }

                parents[neighbour] = current;
                closed[neighbour] = true;
                open.Enqueue(neighbour);
            }
        }

        int tail = found ? parents[goalIndex] : closest;

        if (tail < 0)
        {
            return false;
        }

        if (found)
        {
            path.Add(goal);
        }

        for (int node = tail; node != startIndex; node = parents[node])
        {
            path.Add((node % width, node / width));
        }

        path.Add(start);
        path.Reverse();

        return found;
    }

    /// <summary>Whether a straight step between two texels stays on open ground.</summary>
    /// <remarks>
    /// Only matters when the search is stepping several texels at a time: a lattice node
    /// on either side of a wall is no use if the wall is between them.
    /// </remarks>
    private static bool IsClear(WalkBoundary boundary, int fromX, int fromY, int toX, int toY)
    {
        int x = fromX;
        int y = fromY;

        while (x != toX || y != toY)
        {
            Step(ref x, ref y, toX, toY);

            if (!boundary.IsTexelWalkable(x, y))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Moves one texel towards a target, diagonally where both axes differ.</summary>
    private static void Step(ref int x, ref int y, int toX, int toY)
    {
        if (x < toX)
        {
            x++;
        }
        else if (x > toX)
        {
            x--;
        }

        if (y < toY)
        {
            y++;
        }
        else if (y > toY)
        {
            y--;
        }
    }

    /// <summary>Nudges the route's interior off the walls.</summary>
    /// <remarks>
    /// The ends are left exactly where they were. They are where an actor is standing and
    /// where it was told to go, and moving either of those is a bug rather than a polish
    /// — an actor asked to stand on a mark has to stand on the mark.
    /// </remarks>
    private static void AwayFromWalls(WalkBoundary boundary, List<(int X, int Y)> path)
    {
        for (int i = 1; i < path.Count - 1; i++)
        {
            int region = boundary.RegionOf(path[i].X, path[i].Y);

            // A scriptable region carries no gradient — its index says which door it is,
            // not how near a wall it is — so there is nothing to climb down.
            if (region >= 128)
            {
                continue;
            }

            while (Downhill(boundary, path[i], region) is { } better)
            {
                path[i] = better;
                region = boundary.RegionOf(better.X, better.Y);

                if (region < ClearOfWalls)
                {
                    break;
                }
            }
        }
    }

    /// <summary>The first neighbouring texel that is further from a wall, if any.</summary>
    private static (int X, int Y)? Downhill(WalkBoundary boundary, (int X, int Y) at, int region)
    {
        foreach ((int offsetX, int offsetY) in Neighbours)
        {
            int x = at.X + offsetX;
            int y = at.Y + offsetY;

            if (boundary.RegionOf(x, y) < region && boundary.IsTexelWalkable(x, y))
            {
                return (x, y);
            }
        }

        return null;
    }

    /// <summary>Drops nodes the route does not need.</summary>
    /// <remarks>
    /// Given three consecutive nodes, if the walk from the first to the third is clear then
    /// the second was an artefact of the grid. G-Engine's limit on how many may go at once
    /// is kept, and reset each time the window moves, so the route keeps the shape of the
    /// room instead of collapsing onto the longest straight line available.
    /// </remarks>
    private static void PullString(WalkBoundary boundary, List<(int X, int Y)> path)
    {
        int first = 0;
        int last = 2;
        int erased = 0;

        while (last < path.Count)
        {
            if (IsWalkableLine(boundary, path[first], path[last]))
            {
                path.RemoveAt(first + 1);
                erased++;

                if (erased <= MaxErasures)
                {
                    continue;
                }
            }

            first++;
            last = first + 2;
            erased = 0;
        }
    }

    /// <summary>Whether a straight walk between two texels is comfortable.</summary>
    /// <remarks>
    /// Stricter than <see cref="IsClear"/>: a shortcut also has to keep clear of the walls,
    /// or string pulling would undo the conditioning that just moved the route off them.
    /// </remarks>
    private static bool IsWalkableLine(WalkBoundary boundary, (int X, int Y) from, (int X, int Y) to)
    {
        int x = from.X;
        int y = from.Y;

        while (x != to.X || y != to.Y)
        {
            Step(ref x, ref y, to.X, to.Y);

            if (!boundary.IsTexelWalkable(x, y))
            {
                return false;
            }

            int region = boundary.RegionOf(x, y);

            if (region is > ShortcutCeiling and < 128)
            {
                return false;
            }
        }

        return true;
    }
}
