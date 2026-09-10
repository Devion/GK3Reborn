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
/// The insects under a room's trees and the dust hanging in its air.
/// </summary>
/// <remarks>
/// <para>
/// A few midges dance in a knot under a crown near the camera, and one or two flies cross
/// the air on their own. Both are small, soft and few: they are there to make the air read
/// as air, not to be looked at. A speck of dust lives in a box that travels with the camera
/// and is born and dies on a sine, so a camera cut moves the box and every speck in it is
/// simply young again; a speck inside a shaft of daylight is lit by it, which is how a
/// shaft is seen at all.
/// </para>
/// <para>
/// Both are handed to the blended pass as soft discs.
/// </para>
/// </remarks>
public sealed class DriftField
{
    /// <summary>How long one step of the simulation is, in seconds.</summary>
    private const float Step = 1f / 60f;

    /// <summary>How many steps one call may take, however long it has been.</summary>
    private const int MostSteps = 15;

    /// <summary>How far from the eye a crown still has a knot of midges under it, in world units.</summary>
    private const float Reach = 900f;

    /// <summary>Where an insect is legible and where it has gone to a flicker, in world units.</summary>
    private const float Near = 320f;

    /// <inheritdoc cref="Near"/>
    private const float Far = 620f;

    /// <summary>How many knots of midges are up at once, at most.</summary>
    private const int MostKnots = 3;

    /// <summary>How many midges one knot has, least and most.</summary>
    private const int KnotLeast = 4;

    /// <inheritdoc cref="KnotLeast"/>
    private const int KnotMost = 7;

    /// <summary>How many flies cross the air on their own.</summary>
    private const int Flies = 2;

    /// <summary>How far in front of the eye the dust's box is centred, and its half extents.</summary>
    private const float DustAhead = 160f;

    /// <inheritdoc cref="DustAhead"/>
    private static readonly Vector3 DustBox = new(230f, 130f, 230f);

    /// <summary>How much brighter a speck is inside a shaft of daylight than outside.</summary>
    private const float Sunlit = 3.2f;

    private readonly Drift _drift;
    private readonly IReadOnlyList<Crown> _crowns;
    private readonly DeterministicRandom _random;
    private readonly Vector3 _wind;
    private readonly Knot[] _knots;
    private readonly Fly[] _flies;
    private readonly Mote[] _motes;
    private readonly List<Particle> _drawn = [];
    private IReadOnlyList<LightShaft> _shafts = [];
    private float _owed;
    private float _clock;
    private int _steps;
    private Vector3 _eye;
    private Vector3 _forward = Vector3.UnitZ;
    private bool _placed;

    /// <summary>Sets a room's air going.</summary>
    /// <param name="drift">What drifts, from <see cref="SceneDrift.For"/>.</param>
    /// <param name="crowns">The trees the midges gather under, from <see cref="SceneDrift.Crowns"/>.</param>
    /// <param name="seed">Something stable about the room, so every run of it is the same.</param>
    public DriftField(Drift drift, IReadOnlyList<Crown> crowns, ulong seed)
    {
        ArgumentNullException.ThrowIfNull(crowns);

        _drift = drift;
        _crowns = crowns;
        _random = new DeterministicRandom(seed);

        // Which way the room's air moves. One bearing, and it is the one thing about the
        // room that is allowed to be arbitrary.
        float bearing = (float)_random.NextDouble() * MathF.Tau;
        _wind = new Vector3(MathF.Sin(bearing), 0f, MathF.Cos(bearing));

        _knots = new Knot[drift.Insects > 0 && crowns.Count > 0 ? Math.Min(MostKnots, crowns.Count) : 0];
        _flies = new Fly[drift.Insects > 0 ? Flies : 0];
        _motes = new Mote[Math.Max(drift.Motes, 0)];

        for (int i = 0; i < _knots.Length; i++)
        {
            _knots[i] = new Knot { Crown = -1, Wanted = -1 };
        }

        for (int i = 0; i < _flies.Length; i++)
        {
            _flies[i] = new Fly
            {
                Phase = (float)_random.NextDouble() * MathF.Tau,
                Rate = 0.9f + (0.5f * (float)_random.NextDouble()),
            };
        }
    }

    /// <summary>How many insects there are, in knots and on their own.</summary>
    public int InsectCount
    {
        get
        {
            int count = _flies.Length;

            foreach (Knot knot in _knots)
            {
                count += knot.Midges?.Length ?? 0;
            }

            return count;
        }
    }

    /// <summary>How many specks of dust there are.</summary>
    public int MoteCount => _motes.Length;

    /// <summary>Which way the air moves, as a unit vector along the ground.</summary>
    public Vector3 Wind => _wind;

    /// <summary>Whether there is anything here at all.</summary>
    public bool Any => _knots.Length > 0 || _flies.Length > 0 || _motes.Length > 0;

    /// <summary>Tells the dust where the daylight is, so a speck in it shines.</summary>
    /// <param name="shafts">The room's shafts, from <see cref="SceneShafts.For"/>.</param>
    public void LitBy(IReadOnlyList<LightShaft> shafts)
    {
        ArgumentNullException.ThrowIfNull(shafts);
        _shafts = shafts;
    }

    /// <summary>Moves the air on.</summary>
    /// <param name="seconds">How long since the last call.</param>
    /// <param name="view">Where the room is being looked at from.</param>
    public void Advance(float seconds, Camera view)
    {
        ArgumentNullException.ThrowIfNull(view);

        if (!Any || !(seconds > 0f))
        {
            return;
        }

        _eye = view.Position;

        Vector3 forward = view.Target - view.Position;
        _forward = forward.LengthSquared() > 1e-6f ? Vector3.Normalize(forward) : _forward;

        if (!_placed)
        {
            // The first frame in a room, or the first with a camera: the dust is laid out
            // through its box straight away rather than born speck by speck into empty air,
            // and the flies and the midges are put where they are going to be.
            _placed = true;
            Vector3 centre = DustCentre();

            for (int i = 0; i < _motes.Length; i++)
            {
                Spawn(ref _motes[i], centre, settled: true);
            }

            for (int i = 0; i < _flies.Length; i++)
            {
                _flies[i].Anchor = Somewhere(centre);
            }

            Gather();
        }

        _owed += seconds;

        int steps = Math.Min((int)(_owed / Step), MostSteps);
        _owed -= steps * Step;

        if (_owed > Step * MostSteps)
        {
            _owed = 0f;
        }

        for (int i = 0; i < steps; i++)
        {
            _clock += Step;
            _steps++;
            Move();
        }
    }

    /// <summary>Everything in the air, furthest from the eye first.</summary>
    /// <param name="view">The camera the room is being drawn with.</param>
    /// <returns>The sprites, in the order they have to be drawn.</returns>
    public IReadOnlyList<Particle> Facing(Camera view)
    {
        ArgumentNullException.ThrowIfNull(view);

        _drawn.Clear();

        if (!Any)
        {
            return _drawn;
        }

        Vector3 eye = view.Position;

        foreach (Knot knot in _knots)
        {
            if (knot.Midges is null)
            {
                continue;
            }

            float fade = knot.Fade * (1f - Smoothstep(Near, Far, Vector3.Distance(knot.Centre, eye)));

            if (fade <= 0.01f)
            {
                continue;
            }

            foreach (Midge midge in knot.Midges)
            {
                Insect(midge.Position, midge.Size, fade * 0.34f);
            }
        }

        foreach (Fly fly in _flies)
        {
            float fade = 1f - Smoothstep(Near, Far, Vector3.Distance(fly.Position, eye));

            if (fade > 0.01f)
            {
                Insect(fly.Position, 0.55f, fade * 0.30f);
            }
        }

        foreach (Mote mote in _motes)
        {
            if (mote.Life <= 0f)
            {
                continue;
            }

            float away = Vector3.Distance(mote.Position, eye);

            // A speck an arm's length from the eye is a blob the size of a fist; it is let
            // go before it gets there.
            float ink = mote.Alpha * Envelope(mote.Age / mote.Life) * Smoothstep(18f, 50f, away);

            if (ink <= 0.005f)
            {
                continue;
            }

            // Lit where it hangs in a shaft of daylight, and the shaft's own colour there.
            var colour = new Vector3(0.62f, 0.58f, 0.46f);

            foreach (LightShaft shaft in _shafts)
            {
                float inside = shaft.Inside(mote.Position, out float along);

                if (inside < 1f && along > 0f && along < shaft.Length)
                {
                    float held = (1f - Smoothstep(0.75f, 1f, inside)) *
                                 MathF.Exp(-along / shaft.Length * 1.5f) * shaft.Strength;

                    ink = MathF.Min(ink * (1f + (Sunlit * held)), 0.9f);
                    colour = Vector3.Lerp(colour, shaft.Colour, Math.Clamp(held * 2f, 0f, 1f));
                    break;
                }
            }

            _drawn.Add(new Particle(
                mote.Position,
                mote.Size,
                new Vector4(colour, ink),
                0f,

                // Mostly light and barely a thing: it adds a little and hides almost nothing.
                0.85f));
        }

        _drawn.Sort((a, b) =>
            Vector3.DistanceSquared(b.Position, eye)
                .CompareTo(Vector3.DistanceSquared(a.Position, eye)));

        return _drawn;
    }

    private void Insect(Vector3 at, float size, float ink)
    {
        // A dark speck, soft-edged, that takes a little of the picture behind it away and
        // adds almost nothing: what a midge against the light is.
        _drawn.Add(new Particle(at, size, new Vector4(0.10f, 0.09f, 0.08f, ink), 0f, 0.35f));
    }

    private void Move()
    {
        float gust = 0.55f + (0.45f * (
            (0.5f * MathF.Sin(_clock * 0.31f)) +
            (0.3f * MathF.Sin((_clock * 0.73f) + 1.9f)) +
            (0.2f * MathF.Sin((_clock * 1.31f) + 0.7f)) + 0.5f));

        MoveKnots();
        MoveFlies(gust);
        MoveDust(gust);
    }

    /// <summary>Decides which crowns the knots belong under: the nearest to the eye.</summary>
    private void Gather()
    {
        if (_knots.Length == 0)
        {
            return;
        }

        // The nearest crowns, and only those within reach: a knot of midges under a tree
        // on the far side of the wood is not worth the arithmetic.
        var near = new List<(int Index, float Distance)>();

        for (int i = 0; i < _crowns.Count; i++)
        {
            Vector3 centre = _crowns[i].Centre;
            float dx = centre.X - _eye.X;
            float dz = centre.Z - _eye.Z;
            float distance = MathF.Sqrt((dx * dx) + (dz * dz));

            if (distance <= Reach)
            {
                near.Add((i, distance));
            }
        }

        near.Sort((a, b) => a.Distance.CompareTo(b.Distance));

        // A knot already under one of the chosen trees stays there; the others take the
        // trees left over. Nothing moves that does not have to.
        var taken = new HashSet<int>();

        for (int k = 0; k < _knots.Length; k++)
        {
            int crown = _knots[k].Crown;

            if (crown >= 0 && near.Exists(n => n.Index == crown))
            {
                _knots[k].Wanted = crown;
                taken.Add(crown);
            }
            else
            {
                _knots[k].Wanted = -1;
            }
        }

        int next = 0;

        for (int k = 0; k < _knots.Length; k++)
        {
            if (_knots[k].Wanted >= 0)
            {
                continue;
            }

            while (next < near.Count && taken.Contains(near[next].Index))
            {
                next++;
            }

            if (next < near.Count)
            {
                _knots[k].Wanted = near[next].Index;
                taken.Add(near[next].Index);
            }
        }
    }

    private void MoveKnots()
    {
        // Re-gathered now and then rather than every step: a camera glide should not have
        // three knots hopping from tree to tree behind it.
        if (_steps % 90 == 0)
        {
            Gather();
        }

        for (int k = 0; k < _knots.Length; k++)
        {
            ref Knot knot = ref _knots[k];

            // Fading out where it is before it is put under another tree, and fading in
            // there: nothing appears.
            if (knot.Wanted != knot.Crown || knot.Midges is null)
            {
                knot.Fade = MathF.Max(knot.Fade - (Step / 1.5f), 0f);

                if (knot.Fade <= 0f)
                {
                    if (knot.Wanted < 0)
                    {
                        knot.Midges = null;
                        knot.Crown = -1;
                        continue;
                    }

                    Settle(ref knot, knot.Wanted);
                }
            }
            else
            {
                knot.Fade = MathF.Min(knot.Fade + (Step / 2f), 1f);
            }

            if (knot.Midges is null)
            {
                continue;
            }

            // The knot itself wanders a little, slowly, and each midge dances about it on
            // three sines of its own that share no period: quick, short and never still,
            // which is the whole of what a midge is.
            Vector3 centre = knot.Anchor + new Vector3(
                MathF.Sin((_clock * 0.23f) + knot.Phase) * 9f,
                MathF.Sin((_clock * 0.31f) + (knot.Phase * 1.7f)) * 4f,
                MathF.Cos((_clock * 0.19f) + (knot.Phase * 0.6f)) * 9f);

            knot.Centre = centre;

            for (int i = 0; i < knot.Midges.Length; i++)
            {
                ref Midge midge = ref knot.Midges[i];
                float t = _clock * midge.Rate;

                midge.Position = centre + new Vector3(
                    (MathF.Sin(t + midge.Phase) * midge.Swing) +
                        (MathF.Sin((t * 2.3f) + (midge.Phase * 3f)) * midge.Swing * 0.35f),
                    (MathF.Sin((t * 1.7f) + (midge.Phase * 2f)) * midge.Swing * 0.6f) +
                        (MathF.Sin((t * 3.1f) + midge.Phase) * midge.Swing * 0.25f),
                    (MathF.Cos((t * 0.9f) + (midge.Phase * 1.3f)) * midge.Swing) +
                        (MathF.Cos((t * 2.7f) + midge.Phase) * midge.Swing * 0.35f));
            }
        }
    }

    private void Settle(ref Knot knot, int crown)
    {
        Crown under = _crowns[crown];
        Vector3 span = under.Most - under.Least;

        // Under the canopy and a little to one side of the trunk, at about head height:
        // where midges actually hang under a tree.
        knot.Crown = crown;
        knot.Anchor = new Vector3(
            under.Least.X + (span.X * (0.25f + (0.5f * (float)_random.NextDouble()))),
            under.Least.Y + MathF.Min(span.Y * 0.25f, 45f) + 30f,
            under.Least.Z + (span.Z * (0.25f + (0.5f * (float)_random.NextDouble()))));
        knot.Phase = (float)_random.NextDouble() * MathF.Tau;
        knot.Fade = 0f;

        int count = _random.NextInt32(KnotLeast, KnotMost + 1);
        knot.Midges = new Midge[count];

        for (int i = 0; i < count; i++)
        {
            knot.Midges[i] = new Midge
            {
                Phase = (float)_random.NextDouble() * MathF.Tau,
                Rate = 2.2f + (2.2f * (float)_random.NextDouble()),
                Swing = 5f + (7f * (float)_random.NextDouble()),
                Size = 0.32f + (0.22f * (float)_random.NextDouble()),
            };
        }
    }

    private void MoveFlies(float gust)
    {
        Vector3 centre = DustCentre();

        for (int i = 0; i < _flies.Length; i++)
        {
            ref Fly fly = ref _flies[i];

            // A fly crosses the air in loops and darts, carried a little on the wind, and
            // when it has wandered out of the box round the camera it is somewhere else in
            // it, which nobody sees because a fly is a pixel.
            Vector3 off = fly.Anchor - centre;

            if (MathF.Abs(off.X) > DustBox.X || MathF.Abs(off.Y) > DustBox.Y || MathF.Abs(off.Z) > DustBox.Z)
            {
                fly.Anchor = Somewhere(centre);
            }

            fly.Anchor += (_wind * 6f * gust * Step) + (new Vector3(
                MathF.Sin((_clock * 0.37f) + fly.Phase),
                MathF.Sin((_clock * 0.29f) + (fly.Phase * 2f)) * 0.4f,
                MathF.Cos((_clock * 0.41f) + fly.Phase)) * 14f * Step);

            float t = _clock * fly.Rate * 3f;

            fly.Position = fly.Anchor + new Vector3(
                MathF.Sin(t + fly.Phase) * 10f,
                (MathF.Sin((t * 1.9f) + fly.Phase) * 4f) + (MathF.Sin(t * 0.3f) * 6f),
                MathF.Cos((t * 1.3f) + (fly.Phase * 2f)) * 10f);
        }
    }

    private Vector3 Somewhere(Vector3 centre) => centre + new Vector3(
        (((float)_random.NextDouble() * 2f) - 1f) * DustBox.X * 0.7f,
        (((float)_random.NextDouble() * 2f) - 1f) * DustBox.Y * 0.4f,
        (((float)_random.NextDouble() * 2f) - 1f) * DustBox.Z * 0.7f);

    private Vector3 DustCentre()
    {
        var level = new Vector3(_forward.X, 0f, _forward.Z);

        if (level.LengthSquared() < 1e-6f)
        {
            level = Vector3.UnitZ;
        }

        return _eye + (Vector3.Normalize(level) * DustAhead);
    }

    private void MoveDust(float gust)
    {
        if (_motes.Length == 0)
        {
            return;
        }

        Vector3 centre = DustCentre();

        for (int i = 0; i < _motes.Length; i++)
        {
            ref Mote mote = ref _motes[i];

            mote.Age += Step;

            // Out of its life or out of the box — the camera has cut to somewhere else — it
            // is born again somewhere in the box, at nought, and fades in from there.
            Vector3 off = mote.Position - centre;

            if (mote.Age >= mote.Life ||
                MathF.Abs(off.X) > DustBox.X ||
                MathF.Abs(off.Y) > DustBox.Y ||
                MathF.Abs(off.Z) > DustBox.Z)
            {
                Spawn(ref mote, centre, settled: false);
                continue;
            }

            // Carried a little by the wind and stirred by an eddy of its own: dust hangs,
            // it does not fall, and it never goes anywhere in a straight line.
            float t = _clock + mote.Phase;

            Vector3 velocity =
                (_wind * 0.3f * gust * mote.Carry) +
                new Vector3(
                    MathF.Sin((t * 0.83f) + mote.Phase) * 5f,
                    (MathF.Sin((t * 0.61f) + (mote.Phase * 2.1f)) * 2.5f) - 0.8f,
                    MathF.Cos((t * 0.71f) + (mote.Phase * 1.3f)) * 5f);

            mote.Position += velocity * Step;
        }
    }

    private void Spawn(ref Mote mote, Vector3 centre, bool settled)
    {
        mote.Position = centre + new Vector3(
            (((float)_random.NextDouble() * 2f) - 1f) * DustBox.X,
            (((float)_random.NextDouble() * 2f) - 1f) * DustBox.Y,
            (((float)_random.NextDouble() * 2f) - 1f) * DustBox.Z);

        mote.Life = 5f + (6f * (float)_random.NextDouble());

        // Laid out at the start of a room: spread through their lives rather than all born
        // at once, or the whole box would breathe in and out together.
        mote.Age = settled ? mote.Life * (float)_random.NextDouble() : 0f;
        mote.Size = 0.5f + (0.8f * (float)_random.NextDouble());
        mote.Alpha = 0.14f + (0.20f * (float)_random.NextDouble());
        mote.Carry = 6f + (10f * (float)_random.NextDouble());
        mote.Phase = (float)_random.NextDouble() * MathF.Tau;
    }

    private static float Envelope(float through)
    {
        float s = MathF.Sin(Math.Clamp(through, 0f, 1f) * MathF.PI);

        return MathF.Pow(s, 0.7f);
    }

    private static float Smoothstep(float from, float to, float at)
    {
        float t = Math.Clamp((at - from) / (to - from), 0f, 1f);

        return t * t * (3f - (2f * t));
    }

    private struct Knot
    {
        public int Crown;
        public int Wanted;
        public Vector3 Anchor;
        public Vector3 Centre;
        public float Phase;
        public float Fade;
        public Midge[]? Midges;
    }

    private struct Midge
    {
        public Vector3 Position;
        public float Phase;
        public float Rate;
        public float Swing;
        public float Size;
    }

    private struct Fly
    {
        public Vector3 Anchor;
        public Vector3 Position;
        public float Phase;
        public float Rate;
    }

    private struct Mote
    {
        public Vector3 Position;
        public float Age;
        public float Life;
        public float Size;
        public float Alpha;
        public float Carry;
        public float Phase;
    }
}
