// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Numerics;
using GK3Reborn.Foundation;
using GK3Reborn.Rendering;

namespace GK3Reborn.Game;

/// <summary>
/// The birds wheeling over an outdoor room.
/// </summary>
public sealed class BirdFlock
{
    /// <summary>How long one step of the simulation is, in seconds.</summary>
    private const float Step = 1f / 60f;

    /// <summary>How many steps one call may take, however long it has been.</summary>
    private const int MostSteps = 15;

    /// <summary>Where a bird is dark and where it has gone to haze, in world units.</summary>
    private const float Near = 1800f;

    /// <inheritdoc cref="Near"/>
    private const float Far = 5500f;

    /// <summary>
    /// How short the wings may project before the body is what the sprite is laid along.
    /// </summary>
    private const float Edge = 0.34f;

    private readonly Bird[] _birds;
    private readonly Flock _flock;
    private readonly BirdWheel _wheel;
    private readonly Vector3 _centre;
    private readonly float _sense;
    private readonly List<Particle> _drawn = [];
    private float _owed;

    /// <summary>Puts a room's birds up.</summary>
    /// <param name="flock">What is flying, from <see cref="SceneBirds.For"/>.</param>
    /// <param name="wheel">Where it flies, from <see cref="SceneBirds.Over"/>.</param>
    public BirdFlock(Flock flock, BirdWheel wheel)
    {
        _flock = flock;
        _wheel = wheel;
        _centre = wheel.Centre;

        if (!flock.Any || !(wheel.Radius > 0f))
        {
            _birds = [];
            return;
        }

        // Seeded from where the flock wheels, the way a fire's smoke is seeded from where
        // the fire stands: two rooms differ from one another and every run of one room is
        // the same.
        var random = new DeterministicRandom(unchecked((ulong)HashCode.Combine(
            MathF.Round(_centre.X), MathF.Round(_centre.Y), MathF.Round(_centre.Z), flock.Birds)));

        // Which way round they all go. One bit, and it is the last thing about the flock
        // that is allowed to be arbitrary: birds sharing a thermal share its direction, and
        // half of them going the other way is the one arrangement that reads as neither.
        _sense = random.NextDouble() < 0.5 ? -1f : 1f;

        _birds = new Bird[flock.Birds];

        for (int i = 0; i < _birds.Length; i++)
        {
            // Spread round the wheel to start, rather than released together from a point:
            // a flock that has to disperse before it looks like one spends its first two
            // seconds looking like a firework.
            float about = (float)random.NextDouble() * MathF.Tau;
            float out_ = wheel.Radius * (0.35f + (0.6f * (float)random.NextDouble()));

            var at = new Vector3(
                _centre.X + (MathF.Cos(about) * out_),
                _centre.Y + ((((float)random.NextDouble() * 2f) - 1f) * wheel.Rise),
                _centre.Z + (MathF.Sin(about) * out_));

            Vector3 tangent = Tangent(at - _centre);

            _birds[i] = new Bird
            {
                Position = at,
                Velocity = tangent * flock.Speed,

                // Each bird's own wingbeat, and they must not agree. Eleven birds beating
                // in step is a shutter rather than a flock; the spread here is what makes
                // the group shimmer instead.
                Beat = (float)random.NextDouble(),
                Rate = FlapsPerSecond(flock.Wingspan) * (0.85f + (0.3f * (float)random.NextDouble())),

                // And its own place in the wheel: a little above or below the middle, and
                // drifting up and down through it on its own slow cycle.
                Bob = (float)random.NextDouble() * MathF.Tau,
                BobRate = 0.10f + (0.13f * (float)random.NextDouble()),

                // And its own mind, at its own rate. Both spread, so that no two birds
                // ever wander the same way for long.
                Wander = (float)random.NextDouble() * MathF.Tau,
                WanderRate = 0.17f + (0.26f * (float)random.NextDouble()),
            };
        }
    }

    /// <summary>How many birds are up.</summary>
    public int Count => _birds.Length;

    /// <summary>Where the flock wheels.</summary>
    public BirdWheel Wheel => _wheel;

    /// <summary>What is flying.</summary>
    public Flock Kind => _flock;

    /// <summary>How fast a bird of a given size beats its wings.</summary>
    /// <param name="wingspan">Tip to tip, in world units.</param>
    /// <returns>Beats a second.</returns>
    public static float FlapsPerSecond(float wingspan) => 140f / MathF.Max(wingspan, 8f);

    /// <summary>Moves the flock on.</summary>
    /// <param name="seconds">How long since the last call.</param>
    public void Advance(float seconds)
    {
        if (_birds.Length == 0 || !(seconds > 0f))
        {
            return;
        }

        _owed += seconds;

        int steps = Math.Min((int)(_owed / Step), MostSteps);

        _owed -= steps * Step;

        // Behind by more than the cap allows: the time is dropped rather than carried, or
        // the flock would spend the next second catching up at fifteen steps a frame.
        if (_owed > Step * MostSteps)
        {
            _owed = 0f;
        }

        for (int i = 0; i < steps; i++)
        {
            Move();
        }
    }

    /// <summary>Every bird, furthest from the eye first.</summary>
    /// <param name="view">The camera the room is being drawn with.</param>
    /// <returns>The sprites, in the order they have to be drawn.</returns>
    public IReadOnlyList<Particle> Facing(Camera view)
    {
        ArgumentNullException.ThrowIfNull(view);

        _drawn.Clear();

        if (_birds.Length == 0)
        {
            return _drawn;
        }

        Vector3 forward = Vector3.Normalize(view.Target - view.Position);
        Vector3 right = Vector3.Normalize(Vector3.Cross(view.Up, forward));
        Vector3 up = Vector3.Cross(forward, right);

        foreach (Bird bird in _birds)
        {
            float away = Vector3.Distance(bird.Position, view.Position);

            // Beyond the haze it is a grey nothing a fraction of a pixel across, and a
            // sprite smaller than a pixel does not fade out, it flickers.
            float ink = 0.95f * (1f - Smoothstep(Near, Far, away));

            if (ink <= 0.02f)
            {
                continue;
            }

            _drawn.Add(new Particle(
                bird.Position,

                // Half the square it draws as, and the silhouette spans the whole of it.
                _flock.Wingspan * 0.5f,

                // Not black. A bird against a bright sky is very dark and slightly blue,
                // because the only light on the underside of it is the sky itself; pure
                // black reads as a hole in the picture rather than as a thing in front of
                // it.
                new Vector4(0.055f, 0.065f, 0.085f, ink),
                Turned(bird.Velocity, bird.Bank, right, up),
                Particle.Flapping(bird.Beat)));
        }

        _drawn.Sort((a, b) =>
            Vector3.DistanceSquared(b.Position, view.Position)
                .CompareTo(Vector3.DistanceSquared(a.Position, view.Position)));

        return _drawn;
    }

    /// <summary>How far a bird's sprite is turned, and why that is not its heading.</summary>
    /// <param name="velocity">Which way the bird is going, and how fast.</param>
    /// <param name="bank">How far over it is leaning into its turn, in radians.</param>
    /// <param name="right">The camera's right, in world space.</param>
    /// <param name="up">Its up.</param>
    /// <returns>The turn to hand the pass, in radians.</returns>
    public static float Turned(Vector3 velocity, float bank, Vector3 right, Vector3 up)
    {
        if (velocity.LengthSquared() < 1e-6f)
        {
            return 0f;
        }

        Vector3 heading = Vector3.Normalize(velocity);
        Vector3 wing = Vector3.Cross(heading, Vector3.UnitY);

        // Straight up or straight down, which no bird here ever is; the wings may then lie
        // any way at all and one of them has to be picked.
        wing = wing.LengthSquared() > 1e-6f
            ? Vector3.Normalize(wing)
            : Vector3.Normalize(Vector3.Cross(heading, Vector3.UnitX));

        // Rolled into the turn. The axis of the roll is the flight direction, so the wing
        // stays at right angles to it however far it goes over.
        Vector3 lift = Vector3.Cross(wing, heading);

        // How much of the span the eye is actually offered, before the lean is put on it.
        // Measured unbanked on purpose: the lean tips the wings up and down the frame
        // without saying anything about which way they point, so a bird crossing the view
        // with a lean on has a long projection made entirely of lean — and taking that as
        // the wing direction is how a bird ends up drawn vertical with the degenerate case
        // never noticed.
        float offered = new Vector2(
            Vector3.Dot(wing, right), Vector3.Dot(wing, up)).Length();

        wing = (wing * MathF.Cos(bank)) + (lift * MathF.Sin(bank));

        // Both directions on the screen: the wings, and the way it is going.
        var wings = new Vector2(Vector3.Dot(wing, right), Vector3.Dot(wing, up));
        var along = new Vector2(Vector3.Dot(heading, right), Vector3.Dot(heading, up));

        Vector2 axis = wings;

        // **A bird crossing the view has its wings pointed at you.** They project to
        // almost nothing, so the angle taken from them is noise — and the sprite, which
        // cannot foreshorten, gets drawn stood on its wingtip. What is actually seen there
        // is a bird side-on, which is a dash: so the sprite is laid along the flight
        // direction instead, and the two answers are blended across the band where the
        // wings are within about twenty degrees of edge-on so that it turns rather than
        // flips.
        if (offered < Edge && along.LengthSquared() > 1e-6f)
        {
            Vector2 flat = Vector2.Normalize(along);
            Vector2 span = wings.LengthSquared() > 1e-8f ? Vector2.Normalize(wings) : flat;

            // The nearer of the two ways round, so the blend sweeps a right angle and not
            // three of them.
            if (Vector2.Dot(flat, span) < 0f)
            {
                flat = -flat;
            }

            axis = Vector2.Lerp(flat, span, Smoothstep(Edge * 0.35f, Edge, offered));
        }

        if (axis.LengthSquared() < 1e-8f)
        {
            return 0f;
        }

        // The vertex stage turns the sprite's (1, 0) into (cos, sin) along the camera's
        // right and up, so the angle wanted is the one that lands on that axis.
        float spin = MathF.Atan2(axis.Y, axis.X);

        // Which end of the sprite the head is at. The wings are symmetric so turning it
        // half round costs nothing there, and it is the difference between a bird flying
        // and the same bird flying backwards.
        Vector3 forward = (up * MathF.Cos(spin)) - (right * MathF.Sin(spin));

        return Vector3.Dot(heading, forward) < 0f ? spin + MathF.PI : spin;
    }

    private static float Smoothstep(float from, float to, float at)
    {
        float t = Math.Clamp((at - from) / MathF.Max(to - from, 1e-4f), 0f, 1f);

        return t * t * (3f - (2f * t));
    }

    /// <summary>The way round the wheel at a point offset from its middle.</summary>
    private static Vector3 Tangent(Vector3 from)
    {
        var flat = new Vector3(from.X, 0f, from.Z);

        if (flat.LengthSquared() < 1e-4f)
        {
            return Vector3.UnitX;
        }

        // Level, always: a bird's turn is a turn and not a climb, and the climbing is the
        // bob's business.
        return Vector3.Normalize(Vector3.Cross(Vector3.UnitY, Vector3.Normalize(flat)));
    }

    /// <summary>One step of the flock.</summary>
    private void Move()
    {
        // How hard a bird may push against where it is going. Scaled off its own speed, so
        // that the turn it can make is a shape rather than a number: at these figures the
        // tightest circle a bird can hold is about a third of the wheel it is flying in,
        // which is a bird and not a fighter.
        float push = _flock.Speed * 2.4f;

        // Where the flock is and where it is going, which are what the wheel is measured
        // from. Together, apart and along with the rest are then about the group rather
        // than about a circle, and that is the difference between a swarm going round a
        // village and eleven birds on a carousel.
        Vector3 middle = Vector3.Zero;
        Vector3 heading = Vector3.Zero;

        foreach (Bird bird in _birds)
        {
            middle += bird.Position;
            heading += bird.Velocity;
        }

        middle /= _birds.Length;
        heading /= _birds.Length;

        // Two thirds of the way out, so there is wheel on both sides of it. A flock held at
        // the rim goes round the outside of a hole.
        float want = _wheel.Radius * 0.66f;

        // Nearer than three wingspans is too near. Measured in wingspans rather than in
        // world units because it is a statement about birds: two of them a metre apart is
        // a pair of buzzards nearly touching and a pair of swifts with room to spare.
        float room = _flock.Wingspan * 3.2f;

        for (int i = 0; i < _birds.Length; i++)
        {
            ref Bird bird = ref _birds[i];

            // Round, at this bird's own place in the wheel. The tangent taken here rather
            // than at the flock's middle is what keeps the group strung out round the
            // circle instead of orbiting it as one lump — and strung out is what has to
            // happen: these rooms have five fixed cameras apiece pointing different ways,
            // and a lump is a sky with eleven birds in it or none, minute after minute.
            Vector3 offset = bird.Position - _centre;
            var flat = new Vector3(offset.X, 0f, offset.Z);
            float out_ = flat.Length();

            Vector3 outward = out_ > 1e-3f ? flat / out_ : Vector3.UnitX;
            Vector3 tangent = Tangent(offset) * _sense;

            Vector3 steer =
                (tangent * (0.55f + (0.85f * _flock.Turning)) * push) +
                (outward * Math.Clamp((want - out_) / _wheel.Radius, -1.4f, 1.4f) * push * 0.85f);

            // Apart. Weighted by how close, so a bird that is nearly touching another
            // leaves and one merely nearby does not swerve for it.
            for (int j = 0; j < _birds.Length; j++)
            {
                if (j == i)
                {
                    continue;
                }

                Vector3 between = bird.Position - _birds[j].Position;
                float gap = between.Length();

                if (gap > room || gap < 1e-3f)
                {
                    continue;
                }

                steer += between / gap * (1f - (gap / room)) * push * 1.2f;
            }

            // Together, and it is what makes a ring of birds read as a flock rather than
            // as a fence: they gather into two or three loose knots that drift round the
            // wheel and change size, which is what a village's swifts actually do. It
            // grows with how far the bird has strayed rather than being constant, so the
            // group has a size instead of only a centre.
            Vector3 toward = middle - bird.Position;
            float strayed = toward.Length();

            if (strayed > 1e-3f)
            {
                steer += toward / strayed *
                    Math.Clamp(strayed / (room * 4f), 0f, 1.2f) * push * 0.55f;
            }

            // And along with the rest.
            Vector3 apart = heading - bird.Velocity;

            if (apart.LengthSquared() > 1e-4f)
            {
                steer += apart / _flock.Speed * push * 0.30f;
            }

            // Its own mind. Three sines whose rates share no common multiple, which is a
            // wander that never repeats and never has to be stored: without it the flock
            // settles into a formation and flies it, and a formation is geese at best and
            // a diagram at worst.
            bird.Wander += bird.WanderRate * MathF.Tau * Step;

            steer += new Vector3(
                MathF.Sin(bird.Wander),
                MathF.Sin(bird.Wander * 0.61f) * 0.35f,
                MathF.Cos(bird.Wander * 1.37f)) * push * 0.55f;

            // Up and down: its own slow cycle through the band, and a spring that will not
            // let it out of the top or the bottom of it. Birds climb and sink far more
            // slowly than they turn, so this is the one weak term here.
            bird.Bob += bird.BobRate * MathF.Tau * Step;

            float wants = _centre.Y + (MathF.Sin(bird.Bob) * _wheel.Rise);

            steer.Y += Math.Clamp((wants - bird.Position.Y) / _wheel.Rise, -1.5f, 1.5f)
                * push * 0.35f;

            Vector3 was = bird.Velocity;

            bird.Velocity += steer * Step;

            // A bird that is not moving is not a bird. Held between two thirds and a third
            // again of the flock's own speed, which is what lets one cut a corner and
            // another fall behind without the group coming apart.
            float speed = bird.Velocity.Length();

            bird.Velocity = speed > 1e-3f
                ? bird.Velocity / speed * Math.Clamp(speed, _flock.Speed * 0.65f, _flock.Speed * 1.35f)
                : tangent * _flock.Speed;

            // **And it does not go straight up.** Five terms of steering are summed above
            // and two of them are vertical, so the climb could take the whole of a bird's
            // speed — and one that flies vertically has its wings edge-on to a camera
            // beside it, which is a sprite drawn as a vertical mark. Held to about a third
            // of its speed, which is a steep climb for something that has to keep flying,
            // and the rest is given back to the horizontal so it does not slow down for it.
            float climb = _flock.Speed * 0.36f;

            if (MathF.Abs(bird.Velocity.Y) > climb)
            {
                var level = new Vector3(bird.Velocity.X, 0f, bird.Velocity.Z);
                float over = bird.Velocity.Length();

                bird.Velocity = level.LengthSquared() > 1e-6f
                    ? (Vector3.Normalize(level)
                        * MathF.Sqrt(MathF.Max((over * over) - (climb * climb), 0f)))
                        + (Vector3.UnitY * MathF.CopySign(climb, bird.Velocity.Y))
                    : (tangent * MathF.Sqrt(MathF.Max((over * over) - (climb * climb), 0f)))
                        + (Vector3.UnitY * MathF.CopySign(climb, bird.Velocity.Y));
            }

            bird.Position += bird.Velocity * Step;

            Lean(ref bird, was);
            Flap(ref bird);
        }
    }

    /// <summary>Leans one bird into whatever turn it has just made.</summary>
    /// <param name="bird">The bird.</param>
    /// <param name="was">Which way it was going before this step.</param>
    private static void Lean(ref Bird bird, Vector3 was)
    {
        var before = new Vector2(was.X, was.Z);
        var after = new Vector2(bird.Velocity.X, bird.Velocity.Z);

        if (before.LengthSquared() < 1e-6f || after.LengthSquared() < 1e-6f)
        {
            return;
        }

        before = Vector2.Normalize(before);
        after = Vector2.Normalize(after);

        // The sine of the angle between them, signed by which way round it went.
        float turned = (before.X * after.Y) - (before.Y * after.X);

        // Into radians a second, and then into a lean. At the wheel's own radius this is
        // about a fifth of a turn over, which is what a bird circling looks like; the clamp
        // is a bird hauling itself round a corner and no further. Half again as much put
        // two of a flock of fourteen on their wingtips at once, which reads as a stunt.
        float want = Math.Clamp(turned / Step * -1.15f, -0.78f, 0.78f);

        bird.Bank += (want - bird.Bank) * MathF.Min(Step * 5f, 1f);
    }

    /// <summary>Moves one bird's wings on.</summary>
    private void Flap(ref Bird bird)
    {
        float climb = bird.Velocity.Y / (_flock.Speed * 0.30f);

        // Small birds beat almost all the time and large ones almost none, and the size is
        // the only thing that has to be said: a swift over a village is flying by flapping
        // and a buzzard over a hillside is flying by not.
        float always = Math.Clamp(1f - (_flock.Wingspan / 60f), 0.12f, 0.9f);
        float effort = MathF.Max(always, Math.Clamp(climb, 0f, 1f));

        if (effort > 0.35f)
        {
            bird.Beat += bird.Rate * (0.55f + (0.75f * effort)) * Step;

            return;
        }

        // Gliding: run the stroke out and hold there.
        float rest = MathF.Ceiling(bird.Beat);

        bird.Beat = MathF.Min(rest, bird.Beat + (bird.Rate * 0.45f * Step));
    }

    /// <summary>One bird while the room stands.</summary>
    private struct Bird
    {
        public Vector3 Position;
        public Vector3 Velocity;

        /// <summary>Where its wings are, in whole beats.</summary>
        public float Beat;

        /// <summary>How many of those a second it makes when it is working.</summary>
        public float Rate;

        /// <summary>Where it is in its own climb and sink through the wheel.</summary>
        public float Bob;

        /// <summary>How long that takes, in cycles a second.</summary>
        public float BobRate;

        /// <summary>How far over it is leaning into its turn, in radians.</summary>
        public float Bank;

        /// <summary>Where it is in its own wander.</summary>
        public float Wander;

        /// <summary>How fast that runs, in cycles a second.</summary>
        public float WanderRate;
    }
}
