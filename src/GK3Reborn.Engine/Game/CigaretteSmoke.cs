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
/// The smoke off a lit cigarette.
/// </summary>
public sealed class CigaretteSmoke
{
    /// <summary>How many puffs one cigarette may have in the air at once.</summary>
    private const int PerCigarette = 32;

    /// <summary>How far a cigarette's smoke can be seen from, in world units.</summary>
    private const float Near = 900f;

    private readonly List<Smoulder> _lit = [];
    private readonly List<Particle> _drawn = [];

    /// <summary>Sets up the smoke for whatever a room has alight.</summary>
    /// <param name="cigarettes">The cigarettes; see <see cref="Cigarettes.In"/>.</param>
    public CigaretteSmoke(IReadOnlyList<Cigarette> cigarettes)
    {
        ArgumentNullException.ThrowIfNull(cigarettes);

        foreach (Cigarette cigarette in cigarettes)
        {
            _lit.Add(new Smoulder(cigarette));
        }
    }

    /// <summary>How many cigarettes the room has.</summary>
    public int Count => _lit.Count;

    /// <summary>Whether there is anything to draw at all.</summary>
    public bool Any => _lit.Count > 0;

    /// <summary>How many puffs are in the air.</summary>
    public int Puffs { get; private set; }

    /// <summary>Moves every puff on, and releases whatever is due.</summary>
    /// <param name="seconds">How long since the last call.</param>
    /// <param name="eye">Where the camera is, so a smoker in the next room costs nothing.</param>
    public void Advance(float seconds, Vector3 eye)
    {
        if (_lit.Count == 0)
        {
            return;
        }

        // The same clamp every other emitter uses: a frame that took a second — a door, a
        // movie, a window dragged — would otherwise put a whole cigarette's smoke on the
        // ceiling at once.
        float step = Math.Clamp(seconds, 0f, 0.1f);
        int alive = 0;

        foreach (Smoulder smoulder in _lit)
        {
            alive += smoulder.Advance(step, eye);
        }

        Puffs = alive;
    }

    /// <summary>Every puff in the air, furthest from the eye first.</summary>
    /// <param name="eye">Where the camera is.</param>
    /// <returns>The particles, in the order the blend needs them.</returns>
    public IReadOnlyList<Particle> Facing(Vector3 eye)
    {
        _drawn.Clear();

        foreach (Smoulder smoulder in _lit)
        {
            smoulder.Collect(_drawn);
        }

        _drawn.Sort((a, b) =>
            Vector3.DistanceSquared(b.Position, eye)
                .CompareTo(Vector3.DistanceSquared(a.Position, eye)));

        return _drawn;
    }

    /// <summary>One cigarette and what is rising off it.</summary>
    private sealed class Smoulder
    {
        /// <summary>How often the burning end lets a wisp go, in seconds.</summary>
        private const float WispEvery = 0.55f;

        /// <summary>And how often a breath out does, while it lasts.</summary>
        private const float BreathEvery = 0.16f;

        private readonly Puff[] _puffs = new Puff[PerCigarette];
        private readonly DeterministicRandom _random;
        private readonly Cigarette _cigarette;
        private float _wispDue;
        private float _breathDue;

        public Smoulder(Cigarette cigarette)
        {
            _cigarette = cigarette;

            // Seeded from the model's name, so the same room smokes the same way on every
            // run. There is only ever one of these alight, so nothing has to be told apart.
            _random = new DeterministicRandom(
                (ulong)HashCode.Combine(cigarette.Lit.Name, cigarette.Lit.Noun) | 1UL);

            _wispDue = (float)_random.NextDouble() * WispEvery;
        }

        /// <summary>Moves everything on and releases what is due.</summary>
        /// <param name="step">How long, in seconds.</param>
        /// <param name="eye">Where the camera is.</param>
        /// <returns>How many puffs are in the air.</returns>
        public int Advance(float step, Vector3 eye)
        {
            int alive = 0;

            for (int i = 0; i < _puffs.Length; i++)
            {
                ref Puff puff = ref _puffs[i];

                if (puff.Life <= 0f)
                {
                    continue;
                }

                puff.Life -= step;

                if (puff.Life <= 0f)
                {
                    continue;
                }

                puff.Position += puff.Velocity * step;

                // Cigarette smoke keeps almost none of the speed it left with and then
                // rises on its own, which is why a breath out is a cloud that hangs where
                // it was made rather than a jet that crosses the room.
                puff.Velocity *= MathF.Pow(0.22f, step);
                puff.Velocity += new Vector3(0f, 11f, 0f) * step;

                alive++;
            }

            (Vector3 at, bool alight, bool exhaling) = Cigarettes.Lit(_cigarette);

            if (!alight || Vector3.Distance(at, eye) > Near)
            {
                return alive;
            }

            _wispDue -= step;

            while (_wispDue <= 0f)
            {
                _wispDue += WispEvery;
                Release(at, breath: false);
            }

            // And the exhale, on the original's own thirty frames. Its clock is not run
            // between breaths: the first puff of a breath has to arrive on the frame the
            // animation says it does, not up to forty-five milliseconds later.
            if (!exhaling)
            {
                _breathDue = 0f;

                return alive;
            }

            _breathDue -= step;

            while (_breathDue <= 0f)
            {
                _breathDue += BreathEvery;
                Release(at, breath: true);
            }

            return alive;
        }

        /// <summary>Collects what is in the air.</summary>
        /// <param name="into">The list the pass draws.</param>
        public void Collect(List<Particle> into)
        {
            foreach (Puff puff in _puffs)
            {
                if (puff.Life > 0f)
                {
                    into.Add(Draw(puff));
                }
            }
        }

        /// <summary>Turns a puff into what the pass draws.</summary>
        private static Particle Draw(Puff puff)
        {
            // Nought when it left the cigarette and one when it has gone.
            float age = Math.Clamp(1f - (puff.Life / MathF.Max(puff.Span, 1e-4f)), 0f, 1f);

            // Faded in over the first fifth of its life so that nothing appears out of
            // nothing, and out over the square so that the last of it is a haze rather than
            // a disc that blinks off. The same curve a fire's smoke uses.
            float thickness = MathF.Min(age / 0.2f, 1f) * (1f - age) * (1f - age);

            // Tobacco smoke is warm and grey-blue, not the brown-black of a wood fire, and
            // it is thin: this is a few per cent of an opaque sprite even at its thickest.
            var fresh = new Vector3(0.62f, 0.60f, 0.56f);
            var stale = new Vector3(0.48f, 0.50f, 0.55f);

            return new Particle(
                puff.Position,

                // Spreading as it goes, which is most of what tells a viewer it is smoke
                // rather than a mote of dust.
                puff.Size * (1f + (2.6f * age)),
                new Vector4(
                    Vector3.Lerp(fresh, stale, MathF.Min(age * 2f, 1f)),
                    thickness * puff.Alpha),
                puff.Spin + (age * puff.Turn),
                0f);
        }

        /// <summary>Releases one puff, in the first slot that has cleared.</summary>
        /// <param name="at">Where the lit end is now.</param>
        /// <param name="breath">Whether this is a breath out rather than the smoulder.</param>
        private void Release(Vector3 at, bool breath)
        {
            for (int i = 0; i < _puffs.Length; i++)
            {
                if (_puffs[i].Life > 0f)
                {
                    continue;
                }

                _puffs[i] = breath ? Breath(at) : Wisp(at);

                return;
            }

            // Every slot in use. The new one is dropped rather than replacing the oldest,
            // because replacing one is a puff that vanishes in mid-air.
        }

        /// <summary>A wisp off the burning end, between breaths.</summary>
        private Puff Wisp(Vector3 at)
        {
            float span = 2.6f + (1.4f * Spread());

            return new Puff
            {
                Position = at + new Vector3(Either(), 1f + Spread(), Either()),
                Velocity = new Vector3(3f * Either(), 14f + (7f * Spread()), 3f * Either()),
                Size = 1.1f + (0.5f * Spread()),
                Life = span,
                Span = span,

                // Barely there. A cigarette left alone in an ashtray is a line of smoke you
                // have to look for, and this is that line and not a chimney.
                Alpha = 0.09f + (0.05f * Spread()),
                Spin = Spread() * MathF.Tau,
                Turn = Either() * 0.7f,
            };
        }

        /// <summary>One of the puffs that make up a breath out.</summary>
        private Puff Breath(Vector3 at)
        {
            float span = 1.9f + (1.6f * Spread());

            return new Puff
            {
                // Out and a little up from the cigarette, because a breath leaves the mouth
                // and the cigarette is at it: started exactly on the model and the cloud
                // draws over her face from the first frame.
                Position = at + new Vector3(2.5f * Either(), 2f + (2f * Spread()), 2.5f * Either()),

                Velocity = new Vector3(
                    16f * Either(), 20f + (14f * Spread()), 16f * Either()),

                Size = 1.8f + (1.3f * Spread()),
                Life = span,
                Span = span,

                // Three times the wisp and still thin. A breath of smoke greys what is
                // behind it; it does not hide it.
                Alpha = 0.24f + (0.12f * Spread()),
                Spin = Spread() * MathF.Tau,
                Turn = Either() * 1.1f,
            };
        }

        /// <summary>Nought to one.</summary>
        private float Spread() => (float)_random.NextDouble();

        /// <summary>Minus one to one.</summary>
        private float Either() => ((float)_random.NextDouble() * 2f) - 1f;
    }

    /// <summary>One puff of smoke while it lasts.</summary>
    private struct Puff
    {
        public Vector3 Position;
        public Vector3 Velocity;
        public float Size;
        public float Life;
        public float Span;
        public float Alpha;
        public float Spin;
        public float Turn;
    }
}
