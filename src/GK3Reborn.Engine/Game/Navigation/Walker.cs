using System.Numerics;

namespace GK3Reborn.Game.Navigation;

/// <summary>
/// An actor crossing a room.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="WalkPath"/> finds a route across the walk boundary and nothing moved along
/// it. This does: given the elapsed time it advances a position along the route's corners,
/// turning to face the way it is going, and says when it has arrived.
/// </para>
/// <para>
/// Position and facing only. Playing the walk cycle needs the <c>.ACT</c> vertex animation
/// format, so what crosses the room at the moment is an actor in whatever pose they were
/// standing in — which looks odd and is still the difference between a game where the
/// player goes places and one where they do not.
/// </para>
/// <para>
/// Turning is separate from moving and faster, so an actor rounding a corner turns into it
/// over a few frames rather than snapping. Both are rates rather than durations, because a
/// route's length is not known until it is found and a fixed duration would make a long
/// walk and a short one take the same time.
/// </para>
/// </remarks>
public sealed class Walker
{
    /// <summary>How fast an actor walks, in scene units a second.</summary>
    /// <remarks>
    /// R25 is 369 by 386 units across, so this crosses it in about six seconds — near
    /// enough to the original's pace to feel like the same room.
    /// </remarks>
    public const float Speed = 65f;

    /// <summary>How fast an actor turns, in radians a second.</summary>
    public const float TurnRate = 6f;

    /// <summary>How near the first corner an actor may already be standing.</summary>
    /// <remarks>
    /// Only for the route's opening corner, which <see cref="WalkPath"/> usually puts where
    /// the actor already is. Walking to where you are is a wasted step and a turn towards
    /// nothing in particular.
    /// </remarks>
    private const float Close = 2f;

    private readonly List<Vector3> _route;
    private readonly float? _arrive;
    private readonly Vector3? _look;
    private int _at;

    /// <summary>Starts an actor walking.</summary>
    /// <param name="actor">Whose walk it is.</param>
    /// <param name="route">The corners to walk, in order.</param>
    /// <param name="from">Where they are standing now.</param>
    /// <param name="facing">Which way they are facing now, in radians.</param>
    /// <param name="arriveFacing">
    /// Which way to be facing on arrival, in radians, or null to keep whatever direction the
    /// last step was going.
    /// </param>
    /// <param name="arriveLookingAt">
    /// A point to be facing on arrival. Taken from wherever the actor actually stops, which
    /// is not where they were sent: the boundary stops them short of anything solid, so a
    /// heading worked out in advance from the requested destination is a heading towards a
    /// place they never reach — and when the two coincide, no heading at all.
    /// </param>
    /// <remarks>
    /// Arriving facing something is the point of most walks. A scene's named spots each
    /// carry a heading — the way somebody standing there is meant to face — and walking to
    /// look at a thing means ending up looking at it. Without this an actor arrives facing
    /// whichever way the last corner of the route happened to point, which is usually a
    /// wall.
    /// </remarks>
    public Walker(
        string actor,
        WalkRoute route,
        Vector3 from,
        float facing,
        float? arriveFacing = null,
        Vector3? arriveLookingAt = null)
    {
        ArgumentNullException.ThrowIfNull(actor);

        Actor = actor;
        Position = from;
        Facing = facing;
        Reaches = route.ReachedGoal;
        _arrive = arriveFacing;
        _look = arriveLookingAt;
        _route = [.. route.Points];

        // The first corner is usually where the actor already is, and walking to where you
        // are wastes a step and a turn.
        if (_route.Count > 0 && Flat(_route[0] - from).Length() < Close)
        {
            _at = 1;
        }
    }

    /// <summary>Whose walk it is.</summary>
    public string Actor { get; }

    /// <summary>Where they are now.</summary>
    public Vector3 Position { get; private set; }

    /// <summary>Which way they are facing, in radians about the vertical.</summary>
    public float Facing { get; private set; }

    /// <summary>Whether the route reached what was asked for.</summary>
    /// <remarks>
    /// A route that stopped short is still walked — getting as close as the floor allows is
    /// what the original does — but a caller that cares can tell the difference.
    /// </remarks>
    public bool Reaches { get; }

    /// <summary>Whether there is any walking left, or any turning after it.</summary>
    public bool Walking => _at < _route.Count || Turning;

    /// <summary>Whether the route is done and only the arrival turn is left.</summary>
    private bool Turning =>
        _at >= _route.Count &&
        Arrival is { } wanted &&
        MathF.Abs(Wrap(wanted - Facing)) > 0.01f;

    /// <summary>Which way to be facing once the walking is over, if it matters.</summary>
    private float? Arrival
    {
        get
        {
            if (_arrive is { } given)
            {
                return given;
            }

            if (_look is not { } at)
            {
                return null;
            }

            Vector3 towards = Flat(at - Position);

            return towards.LengthSquared() > 1e-4f ? Heading(towards) : null;
        }
    }

    /// <summary>How far there is still to go, in scene units.</summary>
    public float Remaining
    {
        get
        {
            // Walking now covers the turn at the end, which has no distance in it. Asking
            // how far is left once the corners are done used to index past the last one.
            if (_at >= _route.Count)
            {
                return 0;
            }

            float total = Flat(_route[_at] - Position).Length();

            for (int i = _at; i + 1 < _route.Count; i++)
            {
                total += Flat(_route[i + 1] - _route[i]).Length();
            }

            return total;
        }
    }

    /// <summary>How long the whole walk will take, in seconds.</summary>
    public double Seconds => Remaining / Speed;

    /// <summary>Moves along the route.</summary>
    /// <param name="seconds">How much time has passed.</param>
    /// <returns>True while there is still walking to do.</returns>
    /// <remarks>
    /// A frame long enough to cross more than one corner crosses more than one corner,
    /// rather than stopping at the first and losing the rest of the frame. A slow frame
    /// should cost smoothness, never distance.
    /// </remarks>
    public bool Advance(float seconds)
    {
        float budget = Math.Max(0, seconds) * Speed;

        while (budget > 0 && _at < _route.Count)
        {
            Vector3 toCorner = Flat(_route[_at] - Position);
            float distance = toCorner.Length();

            // Deliberately not "close enough": stepping to the next corner while still
            // short of this one cuts the corner, and by how much depends on how long the
            // frame was. Two runs stepped differently would then end up in different
            // places, which is exactly what a frame rate must not decide.
            if (distance <= float.Epsilon)
            {
                _at++;
                continue;
            }

            Face(toCorner, seconds);

            if (distance <= budget)
            {
                Position = new Vector3(_route[_at].X, Position.Y, _route[_at].Z);
                budget -= distance;
                _at++;
            }
            else
            {
                Position += toCorner / distance * budget;
                budget = 0;
            }
        }

        // The route is walked; now turn to face whatever was worth walking to.
        if (_at >= _route.Count && Arrival is { } wanted)
        {
            float most = TurnRate * Math.Max(0, seconds);

            Facing = Wrap(Facing + Math.Clamp(Wrap(wanted - Facing), -most, most));
        }

        return Walking;
    }

    /// <summary>Stops where they stand.</summary>
    /// <remarks>
    /// For leaving the room, and for a script that asked for a walk and then asked for
    /// something else. An actor left mid-stride is better than one who goes on walking
    /// towards a corner of a room nobody is in.
    /// </remarks>
    public void Stop()
    {
        _at = _route.Count;
        Facing = Arrival ?? Facing;
    }

    /// <summary>The transform to place the actor's model with.</summary>
    /// <param name="scale">The scale the model was placed at.</param>
    /// <returns>The transform.</returns>
    public Matrix4x4 Transform(float scale = 1f) =>
        Matrix4x4.CreateScale(scale) *
        Matrix4x4.CreateRotationY(Rotation(Facing)) *
        Matrix4x4.CreateTranslation(Position);

    /// <summary>The rotation that points a character's model along a heading.</summary>
    /// <param name="heading">A heading, in radians, as the game's data measures one.</param>
    /// <returns>The angle to turn the model about the vertical.</returns>
    /// <remarks>
    /// <para>
    /// GK3 measures a heading the way it measures a camera's — zero along +Z, increasing
    /// towards +X — and models its characters facing <b>−Z</b>. Every heading in the engine
    /// is the first kind, and this is the single place the second is applied.
    /// </para>
    /// <para>
    /// R25 is the case that settles it. The room stands an actor arriving from the hall at
    /// <c>FR_HALL, heading=180</c>, which under that convention faces −Z: away from the door,
    /// into the room. Its neighbour <c>TO_HALL</c> is at <c>heading=-5.12</c>, facing +Z at
    /// the door — and the door's own action is <c>approach=walkto, target=To_Hall</c>, so the
    /// pair say the same thing twice.
    /// </para>
    /// <para>
    /// Worth keeping in one place because the failure never looks like a convention problem.
    /// The same half turn stands an actor facing the door they just came through, walks them
    /// backwards, arrives them with their back to what they came to look at, and aims their
    /// head away from whatever they were told to watch — four bugs that get chased
    /// separately.
    /// </para>
    /// </remarks>
    public static float Rotation(float heading) => Wrap(heading + MathF.PI);

    /// <summary>Reads a heading back out of a placement.</summary>
    /// <param name="transform">A placement built as a turn about the vertical and a move.</param>
    /// <returns>The heading, as the game's data measures one.</returns>
    /// <remarks>
    /// The inverse of <see cref="Rotation"/>, and its own inverse: a half turn undoes a half
    /// turn. It lives here so that the two can never drift apart.
    /// </remarks>
    public static float HeadingOf(Matrix4x4 transform) =>
        Rotation(MathF.Atan2(transform.M31, transform.M33));

    /// <summary>Turns towards a direction, at most so far this frame.</summary>
    private void Face(Vector3 direction, float seconds)
    {
        float wanted = Heading(direction);
        float difference = Wrap(wanted - Facing);
        float most = TurnRate * Math.Max(0, seconds);

        Facing = Wrap(Facing + Math.Clamp(difference, -most, most));
    }

    /// <summary>
    /// The heading that looks along a direction.
    /// </summary>
    /// <param name="direction">Where they should be looking, in world space.</param>
    /// <returns>A heading, in radians, as the game's data measures one.</returns>
    /// <remarks>
    /// The game's own convention, which is <c>Heading::FromDirection</c>: zero along +Z,
    /// increasing towards +X. What a heading means for the <em>model</em> is
    /// <see cref="Rotation"/>'s business, and only its business — a value that comes out of
    /// here and one that comes out of a scene file mean the same thing and may be compared,
    /// subtracted and handed to the same places.
    /// </remarks>
    public static float Heading(Vector3 direction) =>
        MathF.Atan2(direction.X, direction.Z);

    /// <summary>Drops the vertical, because walking is a thing done on a floor.</summary>
    private static Vector3 Flat(Vector3 v) => new(v.X, 0, v.Z);

    /// <summary>Brings an angle into the range where the shorter way round is the smaller.</summary>
    private static float Wrap(float radians)
    {
        while (radians > MathF.PI)
        {
            radians -= MathF.Tau;
        }

        while (radians < -MathF.PI)
        {
            radians += MathF.Tau;
        }

        return radians;
    }
}
