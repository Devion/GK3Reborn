// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Numerics;

namespace GK3Reborn.Game.Navigation;

/// <summary>What the player is asking of the first-person controls this frame.</summary>
/// <param name="Move">Where to walk, in their own frame: X right, Y ahead, each -1 to 1.</param>
/// <param name="Look">How far to turn, in radians: X across, Y up.</param>
/// <param name="Fast">Whether they are asking to go faster.</param>
public readonly record struct FirstPersonInput(Vector2 Move, Vector2 Look, bool Fast);

/// <summary>What one frame of first-person walking came to.</summary>
/// <param name="Position">Where the feet ended up.</param>
/// <param name="Travelled">How far they got on the ground plan, in scene units.</param>
/// <param name="Blocked">Whether something refused part of the step.</param>
public readonly record struct FirstPersonStep(Vector3 Position, float Travelled, bool Blocked);

/// <summary>
/// The player as a body in the room: the keys and the left stick move it, the mouse and the
/// right stick turn it, and the walk boundary and the floor decide where it may go.
/// </summary>
public sealed class FirstPerson
{
    /// <summary>
    /// How far above the floor the eye sits, in scene units. The reference engine's own
    /// GameCamera::kDefaultHeight, which is what the game's rooms are furnished against;
    /// a character's WalkerHeight is the top of their head and puts the view a head too
    /// high over every table in the game.
    /// </summary>
    public const float Eyes = 60f;

    /// <summary>How fast the player walks, in scene units a second.</summary>
    public const float Pace = 160f;

    /// <summary>The slowest the pace may be set to.</summary>
    public const float SlowestPace = 80f;

    /// <summary>The fastest the pace may be set to.</summary>
    public const float FastestPace = 320f;

    /// <summary>How much faster the player goes while asking to hurry.</summary>
    public const float Sprint = 1.6f;

    /// <summary>How far apart footfalls are, in scene units.</summary>
    public const float Stride = 55f;

    /// <summary>How much the view turns per pixel of mouse movement, in radians.</summary>
    public const float Sensitivity = 0.0024f;

    /// <summary>The slowest the player may set looking to, as a multiple of the usual.</summary>
    public const float SlowestLook = 0.25f;

    /// <summary>And the fastest.</summary>
    public const float FastestLook = 3f;

    /// <summary>How fast a stick pushed all the way turns the view, in radians a second.</summary>
    public const float StickRate = 2.8f;

    /// <summary>How far a stick must be pushed before it counts.</summary>
    public const float DeadZone = 0.18f;

    /// <summary>How long the view takes to come back to the player's own eyes.</summary>
    public const float ReturnSeconds = 0.9f;

    private const float PitchLimit = (MathF.PI / 2f) - 0.02f;

    // The boundary is a bitmap, so a step longer than a texel can start and end on open
    // floor with a wall between. A quarter of the narrowest texel any room uses.
    private const float LongestStep = 10f;

    // A stair tread is climbed; the balcony above the room is not. Falls are the
    // boundary's business, or nobody could walk down a flight of steps.
    private const float Climb = 32f;

    private float _sinceStep;
    private Vector3 _fromPosition;
    private float _fromYaw;
    private float _fromPitch;
    private float _returning = ReturnSeconds;

    /// <summary>Where the player's feet are.</summary>
    public Vector3 Position { get; set; }

    /// <summary>Which way they face, in radians, as the game's data measures a heading.</summary>
    public float Yaw { get; set; }

    /// <summary>How far up or down they are looking, in radians.</summary>
    public float Pitch { get; set; }

    /// <summary>How fast they walk, in scene units a second.</summary>
    public float Speed { get; set; } = Pace;

    /// <summary>Whether they may stand at a point, or null to let them stand anywhere.</summary>
    public Func<Vector3, bool>? CanStand { get; set; }

    /// <summary>How high the ground is under a point, or null when the room cannot say.</summary>
    public Func<Vector3, float?>? Ground { get; set; }

    /// <summary>Which way the player is looking, as a unit vector.</summary>
    public Vector3 Forward => new(
        MathF.Cos(Pitch) * MathF.Sin(Yaw),
        MathF.Sin(Pitch),
        MathF.Cos(Pitch) * MathF.Cos(Yaw));

    /// <summary>Which way they are walking: the heading, with the tilt taken out.</summary>
    public Vector3 Ahead => new(MathF.Sin(Yaw), 0f, MathF.Cos(Yaw));

    /// <summary>Which way in the room a push of the controls asks them to walk.</summary>
    /// <param name="move">X to their right, Y ahead of them.</param>
    /// <returns>
    /// A direction on the ground plan whose length is how hard the controls were pushed,
    /// and zero when they were not.
    /// </returns>
    public Vector3 Direction(Vector2 move)
    {
        float reach = move.Length();

        if (reach <= 0.001f)
        {
            return Vector3.Zero;
        }

        // Normalised rather than clamped, so a diagonal is not half as fast again. A stick
        // pushed halfway still asks for half.
        Vector2 wanted = reach > 1f ? move / reach : move;

        // cross(up, forward), the left-handed order the rest of the port uses; see
        // FreeCamera, where the choice is argued out.
        var right = new Vector3(MathF.Cos(Yaw), 0f, -MathF.Sin(Yaw));

        return (Ahead * wanted.Y) + (right * wanted.X);
    }

    /// <summary>Where the player's eyes are.</summary>
    /// <param name="height">How far above the feet the eyes sit, in scene units.</param>
    /// <returns>The point to put the camera at.</returns>
    public Vector3 Eye(float height) => Position + (Vector3.UnitY * height);

    /// <summary>Puts the player somewhere, without walking them there.</summary>
    /// <param name="position">Where their feet are.</param>
    /// <param name="heading">Which way they face, in radians.</param>
    public void Stand(Vector3 position, float heading)
    {
        Position = position;
        Yaw = heading;
        _sinceStep = 0f;
    }

    /// <summary>Whether the view is still on its way back to the player's eyes.</summary>
    public bool Returning => _returning < ReturnSeconds;

    /// <summary>
    /// Starts the view moving back from wherever the story was holding it to the player's
    /// own eyes, rather than snapping into them.
    /// </summary>
    /// <param name="position">Where the view is now.</param>
    /// <param name="yaw">Which way it is pointed, in radians.</param>
    /// <param name="pitch">And how far up or down.</param>
    public void ReturnFrom(Vector3 position, float yaw, float pitch)
    {
        _fromPosition = position;
        _fromYaw = yaw;
        _fromPitch = pitch;
        _returning = 0f;
    }

    /// <summary>Where the camera goes this frame, and which way it points.</summary>
    /// <param name="height">How far above the feet the eyes sit.</param>
    /// <param name="seconds">How long the frame lasted.</param>
    /// <returns>The viewpoint, its heading and its tilt, all in radians.</returns>
    public (Vector3 Position, float Yaw, float Pitch) Shot(float height, float seconds)
    {
        Vector3 eye = Eye(height);

        if (!Returning)
        {
            return (eye, Yaw, Pitch);
        }

        _returning += MathF.Max(0f, seconds);

        float part = Math.Clamp(_returning / ReturnSeconds, 0f, 1f);

        // The same easing the story's own glides use, so a move out to a shot and the move
        // back from it are the same gesture in reverse.
        part = part * part * (3f - (2f * part));

        return (
            Vector3.Lerp(_fromPosition, eye, part),

            // The short way round, or a view that is turned a hair past half a turn comes
            // back the long way and spins the room.
            Walker.Wrapped(_fromYaw + (Walker.Wrapped(Yaw - _fromYaw) * part)),
            float.Lerp(_fromPitch, Pitch, part));
    }

    /// <summary>Turns the view without moving.</summary>
    /// <param name="look">How far to turn, in radians: X across, Y up.</param>
    public void Turn(Vector2 look)
    {
        Yaw = Walker.Wrapped(Yaw + look.X);
        Pitch = Math.Clamp(Pitch + look.Y, -PitchLimit, PitchLimit);
    }

    /// <summary>Walks the player for one frame.</summary>
    /// <param name="input">What they are asking for.</param>
    /// <param name="seconds">How long the frame lasted.</param>
    /// <returns>Where they ended up, and how the room treated the attempt.</returns>
    public FirstPersonStep Advance(FirstPersonInput input, float seconds)
    {
        Turn(input.Look);

        Vector3 from = Position;
        Vector2 push = input.Move;
        float reach = push.Length();

        if (seconds <= 0f || reach <= 0.001f)
        {
            return new FirstPersonStep(from, 0f, false);
        }

        Vector3 direction = Direction(push);
        float far = direction.Length();

        if (far <= 1e-4f)
        {
            return new FirstPersonStep(from, 0f, false);
        }

        direction /= far;

        float distance = far * Speed * (input.Fast ? Sprint : 1f) * seconds;
        bool blocked = false;

        for (float gone = 0f; gone < distance;)
        {
            float piece = MathF.Min(LongestStep, distance - gone);
            gone += piece;

            Vector3 step = direction * piece;

            if (Step(step) is { } moved)
            {
                Position = moved;
                continue;
            }

            blocked = true;

            // Up to the wall rather than a whole step short of it. The boundary is a bitmap
            // and a step is a length, so without this how near a wall a player may stand
            // would depend on the frame rate.
            if ((Step(step * 0.5f) ?? Step(step * 0.25f)) is { } nearer)
            {
                Position = nearer;
                continue;
            }

            // Along the wall rather than into it: whichever single axis is still open.
            Vector3? across = Step(new Vector3(step.X, 0f, 0f)) ??
                              Step(new Vector3(0f, 0f, step.Z));

            if (across is not { } slid)
            {
                break;
            }

            Position = slid;
        }

        float travelled = new Vector2(Position.X - from.X, Position.Z - from.Z).Length();
        _sinceStep += travelled;

        return new FirstPersonStep(Position, travelled, blocked);
    }

    /// <summary>What a stick is asking for, with the dead zone taken out.</summary>
    /// <param name="stick">Where it is pushed, each axis from -1 to 1.</param>
    /// <returns>The same direction, rescaled so the first movement out of the middle is
    /// a small one rather than a jump to a fifth of full speed.</returns>
    public static Vector2 Pushed(Vector2 stick)
    {
        float reach = stick.Length();

        if (reach <= DeadZone)
        {
            return Vector2.Zero;
        }

        return stick / reach * MathF.Min(1f, (reach - DeadZone) / (1f - DeadZone));
    }

    /// <summary>Whether a foot lands now, taking the stride off the tally when it does.</summary>
    /// <param name="every">How far apart footfalls are, in scene units.</param>
    /// <returns>True once per stride walked.</returns>
    public bool Footfall(float every = Stride)
    {
        if (every <= 0f || _sinceStep < every)
        {
            return false;
        }

        // Subtracted rather than zeroed, so a long frame does not swallow a stride.
        _sinceStep -= every;

        return true;
    }

    /// <summary>Where one piece of a step lands, or null when the room refuses it.</summary>
    private Vector3? Step(Vector3 by)
    {
        if (by.X == 0f && by.Z == 0f)
        {
            return null;
        }

        Vector3 to = Position + by;

        if (CanStand?.Invoke(to) == false)
        {
            return null;
        }

        // A room that names no floor cannot say, so anywhere the boundary allows will do.
        if (Ground is not { } under)
        {
            return to;
        }

        if (under(to) is { } height)
        {
            // Ground that has risen more than a stair under this step is a wall from below,
            // whatever the boundary bitmap says.
            return height > to.Y + Climb ? null : new Vector3(to.X, height, to.Z);
        }

        // Off the end of the floor the room did name. Refused while they are standing on
        // it, or a boundary bitmap that reaches further than the floor — most of the
        // outdoor ones do — lets the player walk out over open country at the height of
        // the last thing they were standing on. Allowed when they are already off it, so
        // that a room whose floor falls short of its boundary never traps anybody.
        return under(Position) is null ? to : null;
    }
}
