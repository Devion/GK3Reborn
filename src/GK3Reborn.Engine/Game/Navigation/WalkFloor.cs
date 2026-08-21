using System.Numerics;
using GK3Reborn.Formats.Scenes;

namespace GK3Reborn.Game.Navigation;

/// <summary>
/// How high the ground is under a point.
/// </summary>
/// <remarks>
/// <para>
/// The walk boundary is a picture of the floor seen from above: it says where an actor may
/// stand and nothing whatever about how high the floor is there. Nothing else did either,
/// so a walk held whatever height the actor set off at — which is right for a flat room and
/// wrong for every ramp, step and slope in the game.
/// </para>
/// <para>
/// The height comes from the room's own geometry. A scene names the object its floor is —
/// <c>floor=rc1_floor</c> — and every room's general <c>.SIF</c> does, so this is a lookup
/// rather than a guess. The named object's triangles are dropped into a uniform grid keyed
/// on X and Z, and a query tests the handful in one cell.
/// </para>
/// <para>
/// <b>Rooms are not single-storey.</b> A stairwell's floor object covers the same ground
/// twice, and a balcony over a hall covers it a third time, so "the triangle under this
/// point" is several triangles and which one is meant depends on where the actor already
/// is. The nearest one that is not an implausible climb wins; failing that, the nearest of
/// any. Following the wrong storey is the one failure that looks like a bug rather than a
/// wobble, and it is what a plain highest-or-lowest rule does at the top of every stair.
/// </para>
/// </remarks>
public sealed class WalkFloor
{
    /// <summary>How far above an actor the floor may be and still be theirs, in units.</summary>
    /// <remarks>
    /// A step up. Gabriel is 76 units tall, so this is a little under knee height: enough
    /// for a kerb or a stair tread, not enough to reach the landing above.
    /// </remarks>
    private const float Rise = 30f;

    /// <summary>How far below an actor the floor may be and still be theirs, in units.</summary>
    /// <remarks>
    /// Deliberately larger than <see cref="Rise"/>: walking off a step is ordinary and
    /// walking up onto one is not, and an actor whose feet are a shade above the ramp they
    /// are on must not be handed the storey below.
    /// </remarks>
    private const float Drop = 60f;

    /// <summary>How wide one bucket of the lookup grid is, in scene units.</summary>
    /// <remarks>
    /// About Gabriel's height. Floors are big flat triangles, so most cells hold a couple
    /// and the ones over a staircase hold a dozen — either way a query is a short loop
    /// rather than a sweep of the room.
    /// </remarks>
    private const float Cell = 80f;

    private readonly List<Vector3> _triangles;
    private readonly Dictionary<(int X, int Z), List<int>> _grid;

    private WalkFloor(List<Vector3> triangles, Dictionary<(int X, int Z), List<int>> grid)
    {
        _triangles = triangles;
        _grid = grid;
    }

    /// <summary>How many triangles the floor is made of.</summary>
    public int Triangles => _triangles.Count / 3;

    /// <summary>
    /// Builds the height lookup for a room's floor.
    /// </summary>
    /// <param name="geometry">The room's geometry.</param>
    /// <param name="floorObject">
    /// The BSP object the scene calls its floor, or null when it names none.
    /// </param>
    /// <returns>The lookup, or null when there is no floor to look up.</returns>
    /// <remarks>
    /// Returning null rather than falling back to the whole room, on purpose. Every surface
    /// in a BSP is a candidate floor if you let it be, and the ceiling of the room below is
    /// a perfectly good horizontal plane; a scene that names no floor is better left doing
    /// what it did before than confidently standing its actors on the furniture.
    /// </remarks>
    public static WalkFloor? From(BspFile? geometry, string? floorObject)
    {
        if (geometry is null || string.IsNullOrWhiteSpace(floorObject))
        {
            return null;
        }

        int wanted = -1;

        for (int i = 0; i < geometry.ObjectNames.Count; i++)
        {
            if (string.Equals(
                    geometry.ObjectNames[i], floorObject, StringComparison.OrdinalIgnoreCase))
            {
                wanted = i;
                break;
            }
        }

        if (wanted < 0)
        {
            return null;
        }

        List<Vector3> triangles = [];

        foreach (BspPolygon polygon in geometry.Polygons)
        {
            if (polygon.SurfaceIndex < 0 ||
                polygon.SurfaceIndex >= geometry.Surfaces.Count ||
                geometry.Surfaces[polygon.SurfaceIndex].ObjectIndex != wanted)
            {
                continue;
            }

            foreach ((ushort a, ushort b, ushort c) in geometry.Triangulate(polygon))
            {
                triangles.Add(geometry.Vertices[a]);
                triangles.Add(geometry.Vertices[b]);
                triangles.Add(geometry.Vertices[c]);
            }
        }

        if (triangles.Count == 0)
        {
            return null;
        }

        Dictionary<(int X, int Z), List<int>> grid = [];

        for (int i = 0; i < triangles.Count; i += 3)
        {
            Vector3 a = triangles[i];
            Vector3 b = triangles[i + 1];
            Vector3 c = triangles[i + 2];

            int fromX = Bucket(MathF.Min(a.X, MathF.Min(b.X, c.X)));
            int toX = Bucket(MathF.Max(a.X, MathF.Max(b.X, c.X)));
            int fromZ = Bucket(MathF.Min(a.Z, MathF.Min(b.Z, c.Z)));
            int toZ = Bucket(MathF.Max(a.Z, MathF.Max(b.Z, c.Z)));

            for (int x = fromX; x <= toX; x++)
            {
                for (int z = fromZ; z <= toZ; z++)
                {
                    if (!grid.TryGetValue((x, z), out List<int>? cell))
                    {
                        cell = [];
                        grid[(x, z)] = cell;
                    }

                    cell.Add(i);
                }
            }
        }

        return new WalkFloor(triangles, grid);
    }

    /// <summary>
    /// The height of the floor under a point.
    /// </summary>
    /// <param name="at">
    /// Where the actor is. Its X and Z say where to look; its Y says which storey they are
    /// already on, which is what settles a room that covers the same ground twice.
    /// </param>
    /// <returns>The floor's height there, or null when the point is off the floor.</returns>
    public float? Height(Vector3 at)
    {
        if (!_grid.TryGetValue((Bucket(at.X), Bucket(at.Z)), out List<int>? cell))
        {
            return null;
        }

        float? plausible = null;
        float? any = null;

        foreach (int i in cell)
        {
            if (Under(at, i) is not { } height)
            {
                continue;
            }

            // The nearest of any, so a walk that has already drifted off the storey it
            // belongs to is put back on something rather than left in the air.
            if (any is not { } bestAny || MathF.Abs(height - at.Y) < MathF.Abs(bestAny - at.Y))
            {
                any = height;
            }

            if (height > at.Y + Rise || height < at.Y - Drop)
            {
                continue;
            }

            if (plausible is not { } best || MathF.Abs(height - at.Y) < MathF.Abs(best - at.Y))
            {
                plausible = height;
            }
        }

        return plausible ?? any;
    }

    /// <summary>The height of one triangle under a point, when the point is over it.</summary>
    /// <remarks>
    /// Barycentric in the horizontal plane, which is both the containment test and the
    /// interpolation: the same three weights that say whether the point is inside say how
    /// to mix the corners' heights, so a slope reads as a slope rather than as steps.
    /// </remarks>
    private float? Under(Vector3 at, int i)
    {
        Vector3 a = _triangles[i];
        Vector3 b = _triangles[i + 1];
        Vector3 c = _triangles[i + 2];

        float x0 = c.X - a.X;
        float z0 = c.Z - a.Z;
        float x1 = b.X - a.X;
        float z1 = b.Z - a.Z;
        float x2 = at.X - a.X;
        float z2 = at.Z - a.Z;

        float denominator = (x0 * z1) - (x1 * z0);

        // Edge-on to the vertical. A wall's triangle has no "under", and dividing by this
        // would answer with an infinity that then wins every nearest-height comparison.
        if (MathF.Abs(denominator) < 1e-6f)
        {
            return null;
        }

        float u = ((x2 * z1) - (x1 * z2)) / denominator;
        float v = ((x0 * z2) - (x2 * z0)) / denominator;

        // A shade of slack, so a point exactly on the seam between two triangles lands on
        // one of them rather than falling between the pair.
        const float Slack = 1e-4f;

        if (u < -Slack || v < -Slack || u + v > 1f + Slack)
        {
            return null;
        }

        return a.Y + (u * (c.Y - a.Y)) + (v * (b.Y - a.Y));
    }

    /// <summary>Which bucket of the grid a coordinate falls in.</summary>
    private static int Bucket(float value) => (int)MathF.Floor(value / Cell);
}
