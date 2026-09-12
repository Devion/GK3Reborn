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
/// The spray a fountain throws
/// </summary>
public sealed class FountainSpray
{
    private const int PerRing = 120;
    private const int MistPerRing = 18;
    private const float Near = 1400f;
    private const float Gravity = 392f;
    private const float Rebound = 0.48f;
    private readonly List<Ring> _rings = [];
    private readonly List<Particle> _drawn = [];

    /// <summary>Sets up the spray for a room's fountains.</summary>
    /// <param name="sources">What feeds what; see <see cref="Fountains.In"/>.</param>
    public FountainSpray(IReadOnlyList<FountainSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);

        foreach (FountainSource source in sources)
        {
            _rings.Add(new Ring(source));
        }
    }

    /// <summary>How many rings of spray the room has.</summary>
    public int Rings => _rings.Count;

    /// <summary>Whether there is anything to draw at all.</summary>
    public bool Any => _rings.Count > 0;

    /// <summary>How many droplets are in the air.</summary>
    public int Count { get; private set; }

    /// <summary>Moves every droplet on, and throws whatever is due.</summary>
    /// <param name="seconds">How long since the last call.</param>
    /// <param name="eye">Where the camera is, so a fountain across the map costs nothing.</param>
    public void Advance(float seconds, Vector3 eye)
    {
        if (_rings.Count == 0)
        {
            return;
        }

        float step = Math.Clamp(seconds, 0f, 0.1f);
        int alive = 0;

        foreach (Ring ring in _rings)
        {
            alive += ring.Advance(step, Vector3.Distance(ring.Where.Centre, eye) <= Near);
        }

        Count = alive;
    }

    /// <summary>Every droplet and mote, furthest from the eye first.</summary>
    /// <param name="eye">Where the camera is.</param>
    /// <returns>The particles, in the order the blend needs them.</returns>
    public IReadOnlyList<Particle> Facing(Vector3 eye)
    {
        _drawn.Clear();

        foreach (Ring ring in _rings)
        {
            ring.Collect(_drawn);
        }

        _drawn.Sort((a, b) =>
            Vector3.DistanceSquared(b.Position, eye)
                .CompareTo(Vector3.DistanceSquared(a.Position, eye)));

        return _drawn;
    }

    /// <summary>One ring of impact and what it has thrown.</summary>
    private sealed class Ring
    {
        private readonly Droplet[] _droplets = new Droplet[PerRing];
        private readonly Mote[] _mist = new Mote[MistPerRing];
        private readonly DeterministicRandom _random;
        private readonly FountainSource _source;
        private float _speed;

        private float Speed => MathF.Sqrt(2f * Gravity * Where.Fall) * Rebound;
        private bool Seen { get; set; } = true;

        public Ring(FountainSource source)
        {
            _source = source;
            Where = Fountains.Ring(source);
            _random = new DeterministicRandom((ulong)HashCode.Combine(source.Model.Name, source.Pool, source.Sheet) | 1UL);
            _speed = Speed;

            for (int i = 0; i < _droplets.Length; i++)
            {
                Throw(ref _droplets[i]);
                _droplets[i].Age = (float)_random.NextDouble() * _droplets[i].Life;
            }

            for (int i = 0; i < _mist.Length; i++)
            {
                Raise(ref _mist[i]);
                _mist[i].Age = (float)_random.NextDouble() * _mist[i].Life;
            }
        }

        /// <summary>Where this ring is, as of the last <see cref="Advance"/>.</summary>
        public Fountain Where { get; private set; }


        /// <summary>Moves everything on.</summary>
        /// <param name="step">How long, in seconds.</param>
        /// <param name="near">Whether the camera is close enough to draw it at all.</param>
        /// <returns>How many droplets are in the air.</returns>
        public int Advance(float step, bool near)
        {
            Seen = near;

            if (!near)
            {
                return 0;
            }

            // Where the water is coming down now. The clip that carries the fountain into
            // the room is still running, and the jet inside the top basin rises and falls
            // through its own loop, so the ring is not a fact settled at load.
            Fountain was = Where;

            Where = Fountains.Ring(_source);
            _speed = Speed;

            // And if it has moved further than its own width in one step, the whole
            // fountain has been put somewhere else rather than having wobbled: that is
            // exactly what the first frame does, where the clip picks the model up from
            // where it was built and sets it down in the room. Everything in the air goes
            // with it, or the first half second of the square has a handful of droplets
            // hanging over open ground four thousand units away.
            Vector3 moved = Where.Centre - was.Centre;

            if (moved.LengthSquared() > Where.Radius * Where.Radius)
            {
                for (int i = 0; i < _droplets.Length; i++)
                {
                    _droplets[i].From += moved;
                }

                for (int i = 0; i < _mist.Length; i++)
                {
                    _mist[i].Position += moved;
                }
            }

            for (int i = 0; i < _droplets.Length; i++)
            {
                _droplets[i].Age += step;

                if (_droplets[i].Age >= _droplets[i].Life)
                {
                    Throw(ref _droplets[i]);
                }
            }

            for (int i = 0; i < _mist.Length; i++)
            {
                _mist[i].Age += step;

                if (_mist[i].Age >= _mist[i].Life)
                {
                    Raise(ref _mist[i]);
                }
            }

            return _droplets.Length;
        }

        /// <summary>Adds what this ring has in the air to a list.</summary>
        public void Collect(List<Particle> into)
        {
            if (!Seen)
            {
                return;
            }

            // The mist first: it is the largest and softest thing here and everything else
            // is meant to read in front of it.
            foreach (Mote mote in _mist)
            {
                float part = mote.Age / mote.Life;

                // Up and out of nothing, and back to nothing: a mote that appeared and
                // vanished at full strength would blink.
                float fade = MathF.Sin(part * MathF.PI);

                into.Add(new Particle(
                    mote.Position + new Vector3(0f, mote.Rise * part, 0f),
                    mote.Size,
                    new Vector4(0.84f, 0.90f, 0.94f, 0.16f * fade),
                    mote.Spin,

                    // Nearly all of the way to additive. Mist over water is a brightening
                    // of what is behind it, not a thing in front of it.
                    0.88f));
            }

            foreach (Droplet drop in _droplets)
            {
                float part = drop.Age / drop.Life;

                into.Add(new Particle(
                    drop.At,
                    drop.Size,

                    // Bright where it leaves the water and gone by the time it is back:
                    // a droplet that stayed solid would land as a hard dot.
                    new Vector4(0.93f, 0.96f, 1.00f, 0.75f * (1f - (part * part))),
                    0f,
                    0.92f));
            }
        }

        /// <summary>Throws one droplet off the ring.</summary>
        private void Throw(ref Droplet drop)
        {
            float angle = (float)_random.NextDouble() * MathF.Tau;
            float radius = Where.Radius + (((float)_random.NextDouble() * 2f) - 1f) * Where.Spread;

            var outward = new Vector3(MathF.Sin(angle), 0f, MathF.Cos(angle));

            drop.From = Where.Centre + (outward * radius);

            // Mostly up, a little out: water landing throws a crown, and a crown leans
            // outward because that is the way the sheet was moving.
            float up = _speed * (0.55f + ((float)_random.NextDouble() * 0.75f));
            float away = _speed * (((float)_random.NextDouble() * 0.5f) - 0.15f);

            drop.Launch = new Vector3(outward.X * away, up, outward.Z * away);

            // As long as it takes to come back down, which is what makes it look like it
            // fell rather than faded.
            drop.Life = MathF.Max(0.12f, 2f * up / Gravity);
            drop.Age = 0f;
            drop.Size = 1.1f + ((float)_random.NextDouble() * 1.9f);
        }

        /// <summary>Puts one mote of mist over the ring.</summary>
        private void Raise(ref Mote mote)
        {
            float angle = (float)_random.NextDouble() * MathF.Tau;
            float radius = Where.Radius * (0.75f + ((float)_random.NextDouble() * 0.45f));

            mote.Position = Where.Centre + new Vector3(MathF.Sin(angle) * radius,(float)_random.NextDouble() * Where.Spread,MathF.Cos(angle) * radius);
            mote.Size = Where.Spread * (1.6f + ((float)_random.NextDouble() * 1.8f));
            mote.Rise = Where.Spread * (1.5f + ((float)_random.NextDouble() * 2f));
            mote.Spin = (float)_random.NextDouble() * MathF.Tau;
            mote.Life = 1.4f + ((float)_random.NextDouble() * 1.6f);
            mote.Age = 0f;
        }
    }

    private struct Droplet
    {
        public Vector3 From;
        public Vector3 Launch;
        public float Size;
        public float Age;
        public float Life;

        public readonly Vector3 At => From + (Launch * Age) + new Vector3(0f, -0.5f * Gravity * Age * Age, 0f);
    }

    private struct Mote
    {
        public Vector3 Position;
        public float Size;
        public float Rise;
        public float Spin;
        public float Age;
        public float Life;
    }
}
