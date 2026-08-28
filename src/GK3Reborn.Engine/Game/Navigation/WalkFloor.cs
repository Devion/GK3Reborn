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
/// twice, a balcony over a hall covers it a third time, and an outdoor room's floor often
/// carries the hillside it stands on as well as the ground walked on, so "the triangle
/// under this point" is several triangles. The <b>highest</b> one within a step up and a
/// fall down of the actor wins — the reference drops a ray from the sky and keeps the
/// first surface it meets, and this is that with the storeys above and below rejected.
/// Failing all of them, the nearest of any. See <see cref="Choose"/>.
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

    /// <summary>What each triangle of the floor is painted with, in triangle order.</summary>
    private List<string> Textures { get; init; } = [];

    /// <summary>
    /// What the floor is made of under a point.
    /// </summary>
    /// <param name="at">Where the actor is standing.</param>
    /// <returns>The texture's name, or null when the point is off the floor.</returns>
    /// <remarks>
    /// The same search <see cref="Height"/> makes, answering with the surface rather than
    /// with its height. A room's floor is one object painted with a dozen textures — the
    /// lobby's is eight — and which one is underfoot is the whole of what decides whether a
    /// step sounds like carpet or like tile. It has to be the same triangle the height came
    /// from, or a footstep on the ruins at CD1 is answered by the hillside underneath them.
    /// </remarks>
    public string? Surface(Vector3 at) =>
        Choose(at) is { } chosen && chosen.Triangle / 3 < Textures.Count
            ? Textures[chosen.Triangle / 3]
            : null;

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
        List<string> textures = [];

        foreach (BspPolygon polygon in geometry.Polygons)
        {
            if (polygon.SurfaceIndex < 0 ||
                polygon.SurfaceIndex >= geometry.Surfaces.Count ||
                geometry.Surfaces[polygon.SurfaceIndex].ObjectIndex != wanted)
            {
                continue;
            }

            string texture = geometry.Surfaces[polygon.SurfaceIndex].TextureName;

            foreach ((ushort a, ushort b, ushort c) in geometry.Triangulate(polygon))
            {
                triangles.Add(geometry.Vertices[a]);
                triangles.Add(geometry.Vertices[b]);
                triangles.Add(geometry.Vertices[c]);

                // What the floor is made of, kept per triangle. It is what a footstep
                // sounds like: FLOORMAP.TXT maps a texture to carpet, tile, wood, concrete,
                // dirt or grass, and FOOTSTEPS.TXT maps that and a shoe to a sound.
                textures.Add(texture);
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

        return new WalkFloor(triangles, grid) { Textures = textures };
    }

    /// <summary>
    /// The height of the floor under a point.
    /// </summary>
    /// <param name="at">
    /// Where the actor is. Its X and Z say where to look; its Y says which storey they are
    /// already on, which is what settles a room that covers the same ground twice.
    /// </param>
    /// <returns>The floor's height there, or null when the point is off the floor.</returns>
    public float? Height(Vector3 at) => Choose(at)?.Height;

    /// <summary>Which triangle of the floor an actor at a point is standing on.</summary>
    /// <remarks>
    /// <para>
    /// <b>The highest surface they could have climbed onto, not the nearest.</b> The
    /// reference walker asks this by dropping a ray from ten thousand units up and keeping
    /// the first thing it meets, so what an actor stands on is always the topmost floor
    /// over their feet. The rule here is that with the storey rejected: a surface more than
    /// a step above them is not theirs, and neither is one a fall below.
    /// </para>
    /// <para>
    /// Nearest-to-their-feet was the rule before, and it is wrong wherever a room's floor
    /// object carries the ground it is built on as well as the ground walked on. CD1 — the
    /// ruins of Chateau de Blanchefort — is 35% such: the hillside runs on underneath the
    /// paved ruins about eleven units below them and forty below the tower platform, and
    /// nearest handed an actor stepping off the path the hillside every time, because it
    /// was the nearer of the two. Gabriel walked the ruins knee-deep in them and the tower
    /// up to his chest, sinking further the higher the floor above him rose.
    /// </para>
    /// </remarks>
    private (int Triangle, float Height)? Choose(Vector3 at)
    {
        if (!_grid.TryGetValue((Bucket(at.X), Bucket(at.Z)), out List<int>? cell))
        {
            return null;
        }

        (int Triangle, float Height)? standing = null;
        (int Triangle, float Height)? any = null;

        foreach (int i in cell)
        {
            if (Under(at, i) is not { } height)
            {
                continue;
            }

            // The nearest of any, so a walk that has already drifted off the storey it
            // belongs to is put back on something rather than left in the air.
            if (any is not { } nearest ||
                MathF.Abs(height - at.Y) < MathF.Abs(nearest.Height - at.Y))
            {
                any = (i, height);
            }

            if (height > at.Y + Rise || height < at.Y - Drop)
            {
                continue;
            }

            if (standing is not { } best || height > best.Height)
            {
                standing = (i, height);
            }
        }

        return standing ?? any;
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
