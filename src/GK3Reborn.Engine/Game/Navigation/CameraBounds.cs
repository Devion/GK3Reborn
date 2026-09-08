using System.Numerics;
using GK3Reborn.Formats.Models;

namespace GK3Reborn.Game.Navigation;

/// <summary>
/// The shell that keeps the camera inside the room.
/// </summary>
public sealed class CameraBounds
{
    /// <summary>How wide a berth the camera keeps, in scene units.</summary>
    public const float Radius = 16f;

    /// <summary>How many times a blocked move is redirected before what is left is dropped.</summary>
    private const int Passes = 2;

    /// <summary>How far clear of a surface a freed sphere is left, in scene units.</summary>
    private const float Skin = 0.025f;

    /// <summary>How many times a trapped sphere is pushed before best effort is accepted.</summary>
    private const int Nudges = 4;

    /// <summary>Below this a move is not worth resolving.</summary>
    private const float Still = 1e-6f;

    private Vector3[] _triangles;

    /// <summary>Builds bounds from the shells a scene names.</summary>
    /// <param name="models">The bounds models, already loaded.</param>
    public CameraBounds(IEnumerable<ModFile> models)
    {
        ArgumentNullException.ThrowIfNull(models);

        List<Vector3> triangles = [];

        foreach (ModFile model in models)
        {
            foreach (ModMesh mesh in model.Meshes)
            {
                Matrix4x4 toWorld = mesh.MeshToLocal;

                foreach (ModSubmesh submesh in mesh.Submeshes)
                {
                    for (int i = 0; i + 2 < submesh.Indices.Length; i += 3)
                    {
                        triangles.Add(Vector3.Transform(submesh.Positions[submesh.Indices[i]], toWorld));
                        triangles.Add(Vector3.Transform(submesh.Positions[submesh.Indices[i + 1]], toWorld));
                        triangles.Add(Vector3.Transform(submesh.Positions[submesh.Indices[i + 2]], toWorld));
                    }
                }
            }
        }

        _triangles = [.. triangles];
        _shell = _triangles.Length;
    }

    /// <summary>How many triangles the scene's own shell contributed.</summary>
    private readonly int _shell;

    private readonly Dictionary<string, List<Vector3>> _blocked =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Fences the camera out of a model as well.
    /// </summary>
    /// <param name="name">What to file it under, so it can be taken back off.</param>
    /// <param name="model">The model, in the space the room places it.</param>
    /// <param name="placement">Where the room places it.</param>
    /// <returns>True when it added anything.</returns>
    public bool Block(string name, ModFile model, Matrix4x4 placement)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(model);

        if (_blocked.ContainsKey(name))
        {
            return false;
        }

        List<Vector3> added = [];

        foreach (ModMesh mesh in model.Meshes)
        {
            Matrix4x4 toWorld = mesh.MeshToLocal * placement;

            foreach (ModSubmesh submesh in mesh.Submeshes)
            {
                for (int i = 0; i + 2 < submesh.Indices.Length; i += 3)
                {
                    added.Add(Vector3.Transform(submesh.Positions[submesh.Indices[i]], toWorld));
                    added.Add(Vector3.Transform(submesh.Positions[submesh.Indices[i + 1]], toWorld));
                    added.Add(Vector3.Transform(submesh.Positions[submesh.Indices[i + 2]], toWorld));
                }
            }
        }

        if (added.Count == 0)
        {
            return false;
        }

        _blocked[name] = added;
        Rebuild();

        return true;
    }

    /// <summary>Takes a model back out of the shell.</summary>
    /// <param name="name">What it was filed under.</param>
    /// <returns>True when there was one to take out.</returns>
    public bool Unblock(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (!_blocked.Remove(name))
        {
            return false;
        }

        Rebuild();
        return true;
    }

    /// <summary>Puts the shell back together after something was added or removed.</summary>
    private void Rebuild()
    {
        List<Vector3> all = [.. _triangles[.._shell]];

        foreach (List<Vector3> added in _blocked.Values)
        {
            all.AddRange(added);
        }

        _triangles = [.. all];
    }

    /// <summary>How many triangles the camera is fenced in by.</summary>
    public int TriangleCount => _triangles.Length / 3;

    /// <summary>Whether there is anything to collide with.</summary>
    public bool IsEmpty => _triangles.Length == 0;

    /// <summary>Moves the camera as far along a step as the shell allows.</summary>
    /// <param name="from">Where it is now.</param>
    /// <param name="movement">Where it is trying to go, as an offset.</param>
    /// <returns>Where it ends up.</returns>
    public Vector3 Resolve(Vector3 from, Vector3 movement)
    {
        if (_triangles.Length == 0)
        {
            return from + movement;
        }

        // Out of anything it is already inside, before deciding where it may go next. A
        // sphere that overlaps a wall is refused every step towards that wall for as long as
        // it overlaps, so without this it slides along inside the wall for ever and the
        // player sees straight through it.
        Vector3 at = Free(from);

        if (movement.LengthSquared() <= Still)
        {
            return at;
        }

        Vector3 left = movement;

        for (int pass = 0; pass < Passes && left.LengthSquared() > Still; pass++)
        {
            if (Nearest(at, left) is not { } hit)
            {
                return at + left;
            }

            Vector3 was = at;
            at += left * hit.Fraction;

            // What is left of the move, laid flat against the surface that stopped it. The
            // point aimed at is dropped onto the plane through where the camera now stands,
            // so the redirected move runs along the wall rather than into it.
            Vector3 wanted = was + left;
            float beyond = Vector3.Dot(wanted - at, hit.Normal);

            left = wanted - (hit.Normal * beyond) - at;
        }

        return Free(at);
    }

    /// <summary>
    /// Pushes the camera out of any surface it has ended up inside.
    /// </summary>
    /// <param name="centre">Where it is.</param>
    /// <returns>Where it should be, clear of the shell.</returns>
    public Vector3 Free(Vector3 centre)
    {
        Vector3 at = centre;

        for (int nudge = 0; nudge < Nudges; nudge++)
        {
            if (Deepest(at) is not { } push)
            {
                break;
            }

            at += push;
        }

        return at;
    }

    /// <summary>The push out of whichever surface the sphere is furthest inside.</summary>
    /// <returns>The offset that clears it, or null when nothing is overlapping.</returns>
    private Vector3? Deepest(Vector3 centre)
    {
        Vector3 push = Vector3.Zero;
        float worst = 0f;

        for (int i = 0; i + 2 < _triangles.Length; i += 3)
        {
            Vector3 a = _triangles[i];
            Vector3 b = _triangles[i + 1];
            Vector3 c = _triangles[i + 2];

            Vector3 normal = Normal(a, b, c);

            if (normal == Vector3.Zero || Vector3.Dot(centre - a, normal) < 0f)
            {
                // No area, or the camera is behind this surface — outside it, where it is
                // allowed to be and from where it is allowed to come back.
                continue;
            }

            Vector3 away = centre - Closest(centre, a, b, c, normal);
            float distance = away.Length();
            float depth = Radius - distance;

            if (depth <= worst)
            {
                continue;
            }

            // Along the line out of the surface where there is one, and along the normal
            // where the centre sits exactly on it and there is no line to take.
            worst = depth;
            push = (distance > Still ? away / distance : normal) * (depth + Skin);
        }

        return worst > 0f ? push : null;
    }

    /// <summary>The point of a triangle nearest to somewhere else.</summary>
    private static Vector3 Closest(Vector3 point, Vector3 a, Vector3 b, Vector3 c, Vector3 normal)
    {
        Vector3 on = point - (normal * Vector3.Dot(point - a, normal));

        if (Inside(on, a, b, c, normal))
        {
            return on;
        }

        Vector3 nearest = OnSegment(point, a, b);
        float best = (point - nearest).LengthSquared();

        foreach ((Vector3 from, Vector3 to) in new[] { (b, c), (c, a) })
        {
            Vector3 candidate = OnSegment(point, from, to);
            float distance = (point - candidate).LengthSquared();

            if (distance < best)
            {
                best = distance;
                nearest = candidate;
            }
        }

        return nearest;
    }

    /// <summary>The point of a segment nearest to somewhere else.</summary>
    private static Vector3 OnSegment(Vector3 point, Vector3 from, Vector3 to)
    {
        Vector3 edge = to - from;
        float length = Vector3.Dot(edge, edge);

        if (length <= Still)
        {
            return from;
        }

        return from + (edge * Math.Clamp(Vector3.Dot(point - from, edge) / length, 0f, 1f));
    }

    /// <summary>Whether a point is inside the shell.</summary>
    /// <param name="point">The point, in world space.</param>
    /// <returns>True when it is enclosed.</returns>
    public bool Contains(Vector3 point)
    {
        if (_triangles.Length == 0)
        {
            return false;
        }

        Vector3 direction = Vector3.Normalize(new Vector3(0.5773f, 0.3313f, 0.7449f));
        int crossings = 0;

        for (int i = 0; i + 2 < _triangles.Length; i += 3)
        {
            if (Crosses(point, direction, _triangles[i], _triangles[i + 1], _triangles[i + 2]))
            {
                crossings++;
            }
        }

        return (crossings & 1) == 1;
    }

    /// <summary>Whether a ray meets a triangle in front of where it starts.</summary>
    private static bool Crosses(Vector3 from, Vector3 direction, Vector3 a, Vector3 b, Vector3 c)
    {
        Vector3 edge1 = b - a;
        Vector3 edge2 = c - a;
        Vector3 across = Vector3.Cross(direction, edge2);
        float determinant = Vector3.Dot(edge1, across);

        if (MathF.Abs(determinant) <= Still)
        {
            return false;
        }

        float inverse = 1f / determinant;
        Vector3 offset = from - a;
        float u = Vector3.Dot(offset, across) * inverse;

        if (u is < 0f or > 1f)
        {
            return false;
        }

        Vector3 other = Vector3.Cross(offset, edge1);
        float v = Vector3.Dot(direction, other) * inverse;

        if (v < 0f || u + v > 1f)
        {
            return false;
        }

        return Vector3.Dot(edge2, other) * inverse > Still;
    }

    /// <summary>The first surface a step runs into, if any.</summary>
    private (float Fraction, Vector3 Normal)? Nearest(Vector3 centre, Vector3 movement)
    {
        float nearest = 1f;
        Vector3 normal = Vector3.Zero;
        bool found = false;

        for (int i = 0; i + 2 < _triangles.Length; i += 3)
        {
            if (Sweep(centre, movement, _triangles[i], _triangles[i + 1], _triangles[i + 2])
                is not { } fraction || fraction >= nearest)
            {
                continue;
            }

            nearest = fraction;
            normal = Normal(_triangles[i], _triangles[i + 1], _triangles[i + 2]);
            found = true;
        }

        return found ? (nearest, normal) : null;
    }

    /// <summary>
    /// How far along a step a sphere gets before it meets one triangle.
    /// </summary>
    /// <returns>A fraction of the step, or null when it never meets it.</returns>
    private static float? Sweep(Vector3 centre, Vector3 movement, Vector3 a, Vector3 b, Vector3 c)
    {
        Vector3 normal = Normal(a, b, c);

        if (normal == Vector3.Zero)
        {
            return null;
        }

        float approach = Vector3.Dot(normal, movement);

        // Away from the surface's front is always allowed, so a camera that starts outside
        // can get back in and one inside is never pushed through by its own bounds.
        if (approach >= 0f)
        {
            return null;
        }

        float distance = Vector3.Dot(normal, centre - a);

        // Behind the surface and further off than the radius: the sphere is on the outside
        // of this triangle and moving parallel enough to it never to arrive.
        if (distance < -Radius)
        {
            return null;
        }

        float? nearest = null;

        // The face. Where the sphere's leading point crosses the plane, and whether that
        // crossing is inside the triangle rather than out past one of its edges.
        if (distance >= 0f)
        {
            float fraction = (distance - Radius) / -approach;

            if (fraction <= 1f)
            {
                Vector3 on = centre - (normal * Radius) + (movement * MathF.Max(fraction, 0f));

                if (Inside(on, a, b, c, normal))
                {
                    nearest = MathF.Max(fraction, 0f);
                }
            }
            else
            {
                // The plane itself is out of reach this step, so no part of the triangle
                // can be met either.
                return null;
            }
        }

        // The corners.
        if (Ball(centre, movement, a) is { } atA && atA < (nearest ?? 1f))
        {
            nearest = atA;
        }

        if (Ball(centre, movement, b) is { } atB && atB < (nearest ?? 1f))
        {
            nearest = atB;
        }

        if (Ball(centre, movement, c) is { } atC && atC < (nearest ?? 1f))
        {
            nearest = atC;
        }

        // The edges.
        if (Cylinder(centre, movement, a, b) is { } ab && ab < (nearest ?? 1f))
        {
            nearest = ab;
        }

        if (Cylinder(centre, movement, b, c) is { } bc && bc < (nearest ?? 1f))
        {
            nearest = bc;
        }

        if (Cylinder(centre, movement, c, a) is { } ca && ca < (nearest ?? 1f))
        {
            nearest = ca;
        }

        return nearest;
    }

    /// <summary>Where a moving sphere first touches a point.</summary>
    private static float? Ball(Vector3 centre, Vector3 movement, Vector3 point)
    {
        Vector3 offset = centre - point;

        float a = Vector3.Dot(movement, movement);
        float b = 2f * Vector3.Dot(movement, offset);
        float c = Vector3.Dot(offset, offset) - (Radius * Radius);

        return Root(a, b, c);
    }

    /// <summary>Where a moving sphere first touches a line segment.</summary>
    private static float? Cylinder(Vector3 centre, Vector3 movement, Vector3 from, Vector3 to)
    {
        Vector3 edge = to - from;
        Vector3 offset = centre - from;

        float edgeLength = Vector3.Dot(edge, edge);

        if (edgeLength <= Still)
        {
            return null;
        }

        float edgeMovement = Vector3.Dot(edge, movement);
        float edgeOffset = Vector3.Dot(edge, offset);

        float a = edgeLength * Vector3.Dot(movement, movement) - (edgeMovement * edgeMovement);
        float b = 2f * ((edgeLength * Vector3.Dot(movement, offset)) - (edgeMovement * edgeOffset));
        float c = (edgeLength * (Vector3.Dot(offset, offset) - (Radius * Radius)))
            - (edgeOffset * edgeOffset);

        if (Root(a, b, c) is not { } fraction)
        {
            return null;
        }

        float along = (edgeMovement * fraction) + edgeOffset;

        return along >= 0f && along <= edgeLength ? fraction : null;
    }

    /// <summary>The first root of a quadratic that lies within the step.</summary>
    /// <returns>The root, clamped up to zero when the sphere already overlaps, or null.</returns>
    private static float? Root(float a, float b, float c)
    {
        if (MathF.Abs(a) <= Still)
        {
            return null;
        }

        float discriminant = (b * b) - (4f * a * c);

        if (discriminant < 0f)
        {
            return null;
        }

        float root = MathF.Sqrt(discriminant);
        float first = (-b - root) / (2f * a);
        float second = (-b + root) / (2f * a);

        if (first > second)
        {
            (first, second) = (second, first);
        }

        // Already overlapping when the earlier root is behind the step and the later one is
        // ahead of it. The sphere stops where it stands rather than being sent backwards,
        // and the slide afterwards is what gets it out.
        if (first < 0f)
        {
            return second >= 0f ? 0f : null;
        }

        return first <= 1f ? first : null;
    }

    /// <summary>Whether a point on a triangle's plane is inside the triangle.</summary>
    private static bool Inside(Vector3 point, Vector3 a, Vector3 b, Vector3 c, Vector3 normal) =>
        Vector3.Dot(Vector3.Cross(b - a, point - a), normal) >= 0f &&
        Vector3.Dot(Vector3.Cross(c - b, point - b), normal) >= 0f &&
        Vector3.Dot(Vector3.Cross(a - c, point - c), normal) >= 0f;

    /// <summary>A triangle's unit normal, or zero when it has no area.</summary>
    private static Vector3 Normal(Vector3 a, Vector3 b, Vector3 c)
    {
        Vector3 cross = Vector3.Cross(b - a, c - a);

        return cross.LengthSquared() <= Still ? Vector3.Zero : Vector3.Normalize(cross);
    }
}
