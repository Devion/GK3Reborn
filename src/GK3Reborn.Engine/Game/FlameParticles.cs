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
/// The smoke and the embers a room's fires give off.
/// </summary>
public sealed class FlameParticles
{
    /// <summary>How many particles one fire may have alight at once.</summary>
    private const int PerFlame = 56;

    /// <summary>How far a fire's smoke and embers can be seen from, in world units.</summary>
    private const float Near = 1200f;

    private readonly List<Emitter> _emitters = [];
    private readonly List<Particle> _drawn = [];
    private readonly List<(Flame Fire, string Object, Vector3 Centre)> _glints = [];
    private readonly bool _volumes;
    private float _clock;

    /// <summary>Sets up the emitters for a room's fires.</summary>
    /// <param name="flames">The fires; see <see cref="Flames.In"/>.</param>
    /// <param name="volumes">
    /// Whether the fire itself is drawn as a volume. False leaves the room's painted flame
    /// cards to do it, which is what <c>--no-shader-fire</c> asks for and what the game
    /// shipped with.
    /// </param>
    public FlameParticles(IReadOnlyList<Flame> flames, bool volumes = true)
    {
        ArgumentNullException.ThrowIfNull(flames);

        _volumes = volumes;

        foreach (Flame flame in flames)
        {
            _emitters.Add(new Emitter(flame));
        }
    }

    /// <summary>How many fires are burning.</summary>
    public int Emitters => _emitters.Count;

    /// <summary>Whether the fires themselves are drawn, and not just what rises off them.</summary>
    public bool Volumes => _volumes;

    /// <summary>
    /// The things lying in the room's fires, which are given a glint of their own.
    /// </summary>
    /// <param name="held">What <see cref="Flames.Holding"/> found; empty in every room but one.</param>
    public void Holds(IReadOnlyList<(Flame Fire, string Object, Vector3 Centre)> held)
    {
        ArgumentNullException.ThrowIfNull(held);

        _glints.Clear();
        _glints.AddRange(held);
    }

    /// <summary>How many things in this room's fires are glinting.</summary>
    public int Glints => _glints.Count;

    /// <summary>How many particles are alight.</summary>
    public int Count { get; private set; }

    /// <summary>
    /// Whether a fire is drawing, by the model it belongs to.
    /// </summary>
    /// <param name="model">The flame model's name.</param>
    /// <param name="alight">Whether the scene is drawing it.</param>
    public void Show(string model, bool alight)
    {
        ArgumentNullException.ThrowIfNull(model);

        for (int i = 0; i < _emitters.Count; i++)
        {
            if (string.Equals(_emitters[i].Flame.Model, model, StringComparison.OrdinalIgnoreCase))
            {
                _emitters[i].Alight = alight;
            }
        }
    }

    /// <summary>
    /// Ties each fire to the model the scene is drawing it as.
    /// </summary>
    /// <param name="models">The models the scene loaded.</param>
    public void Follow(IReadOnlyList<PlacedModel> models)
    {
        ArgumentNullException.ThrowIfNull(models);

        foreach (Emitter emitter in _emitters)
        {
            foreach (PlacedModel model in models)
            {
                if (string.Equals(model.Name, emitter.Flame.Model, StringComparison.OrdinalIgnoreCase))
                {
                    emitter.Drawn = model;
                    break;
                }
            }
        }
    }

    /// <summary>Moves everything on, and lights whatever is due.</summary>
    /// <param name="seconds">How long since the last call.</param>
    /// <param name="eye">Where the camera is, so that distant fires cost nothing.</param>
    public void Advance(float seconds, Vector3 eye)
    {
        if (_emitters.Count == 0)
        {
            return;
        }

        // The same clamp the game's own loop uses. A frame that took a second — a scene
        // load, a movie, a window dragged — would otherwise teleport every ember to the
        // ceiling and light a second's worth of them at once.
        float step = Math.Clamp(seconds, 0f, 0.1f);
        int alive = 0;

        _clock += step;

        foreach (Emitter emitter in _emitters)
        {
            alive += emitter.Advance(step, Vector3.Distance(emitter.Flame.Position, eye) <= Near);
        }

        Count = alive;
    }

    /// <summary>
    /// Every particle alight, furthest from the eye first.
    /// </summary>
    /// <param name="eye">Where the camera is.</param>
    /// <returns>The particles, in the order they have to be drawn.</returns>
    public IReadOnlyList<Particle> Facing(Vector3 eye)
    {
        _drawn.Clear();

        foreach (Emitter emitter in _emitters)
        {
            emitter.Collect(_drawn);

            // And the fire itself, which is the one thing this pass draws that is a volume
            // rather than a picture. Before the sort, because the smoke a fire makes is in
            // front of it as often as behind it and the blend cares which.
            if (_volumes && emitter.Alight)
            {
                _drawn.Add(Body(emitter.Flame, _clock));
            }
        }

        _drawn.Sort((a, b) =>
            Vector3.DistanceSquared(b.Position, eye)
                .CompareTo(Vector3.DistanceSquared(a.Position, eye)));

        // Last, and after the sort: a glint is additive and does not care what it is drawn
        // over, and it must not be hidden by the smoke of the fire it is lying in.
        foreach ((Flame fire, _, Vector3 centre) in _glints)
        {
            _drawn.Add(Glint(fire, centre, eye));
        }

        return _drawn;
    }

    /// <summary>
    /// The fire itself, as the sprite the shader marches a plume through.
    /// </summary>
    /// <param name="flame">The fire.</param>
    /// <param name="clock">How long the room has been standing, in seconds.</param>
    /// <returns>One sprite, big enough to cover the plume from any angle.</returns>
    private static Particle Body(Flame flame, float clock)
    {
        // What the shader marches: a cylinder from the foot of the plume to a little over
        // the top of it, half again as wide, so that the lean and the tongues at the tip
        // have somewhere to go. See ParticleShaders.
        //
        // The plume and not the card. A flame card is nearly always larger than the flame
        // painted on it — the bar's fire is a quad twenty-four units tall with a low fire
        // across the bottom third — and reading the card as the fire puts a bonfire in a
        // fireplace. See Flame.Paint.
        // Half the plume below the middle and three quarters above it: the shader's
        // cylinder reaches a quarter past the top of the plume so that the flame has room
        // to lengthen without being cut off at its tip. See ParticleShaders.
        float lift = flame.Plume * 0.75f;
        float reach = flame.Radius * 1.5f;

        return new Particle(
            // The middle of the plume: the shader takes half its height back off to find
            // the foot again.
            flame.Foot + new Vector3(0f, flame.Plume / 2f, 0f),
            MathF.Sqrt((lift * lift) + (reach * reach)) * 1.04f,

            // White, because the colour of a fire is the fire's business and not a tint
            // laid over it. Kept here so that a room could dim one without a new shape.
            Vector4.One,
            0f,
            Particle.Fire,
            new Vector4(
                flame.Plume,
                flame.Radius,
                (clock * flame.Churn) + flame.Phase,
                (float)flame.Kind));
    }

    /// <summary>The spark held over something lying in a fire.</summary>
    /// <param name="fire">The fire it is lying in.</param>
    /// <param name="centre">The middle of the thing, in world space.</param>
    /// <param name="eye">Where the camera is.</param>
    /// <returns>One additive sprite.</returns>
    private Particle Glint(Flame fire, Vector3 centre, Vector3 eye)
    {
        Vector3 towards = eye - centre;

        Vector3 forward = towards.LengthSquared() > 1e-6f
            ? Vector3.Normalize(towards)
            : Vector3.UnitY;

        // Over the fire rather than at the object. The object is at the bottom of whatever
        // holds the fire — TE4's is a stone bowl ten units deep — so a sprite anywhere near
        // it is behind the near wall of that as well as behind the flame. Lifted to the top
        // of the flame, above the object, and then moved towards the camera far enough to
        // clear a card that turns to face it.
        var over = new Vector3(centre.X, fire.Position.Y + (fire.Height * 0.42f), centre.Z);
        Vector3 at = over + (forward * (fire.Width * 0.35f));

        // One breath every 1.5 seconds, between two thirds and full brightness.
        float breath = 0.45f + (0.45f * (0.5f + (0.5f * MathF.Sin(_clock * 4.19f))));

        return new Particle(
            at,
            MathF.Max(fire.Width * 0.045f, 0.3f) * (0.85f + (0.15f * breath)),

            // The white-gold of something metal that has been in a fire, kept just off
            // white so it is not mistaken for a specular highlight on the stone above it.
            new Vector4(1f, 0.93f, 0.72f, breath),
            0f,

            // Wholly additive, as an ember is: this is light, not a thing.
            1f);
    }

    /// <summary>One fire, and what is currently rising off it.</summary>
    private sealed class Emitter
    {
        private readonly Mote[] _motes = new Mote[PerFlame];
        private readonly DeterministicRandom _random;
        private float _emberDue;
        private float _smokeDue;

        public Emitter(Flame flame)
        {
            Flame = flame;
            Alight = flame.Visible;

            // Seeded from where the fire stands, so that a room's fires differ from one
            // another and every run of the same room is the same.
            _random = new DeterministicRandom(unchecked((ulong)HashCode.Combine(
                MathF.Round(flame.Position.X),
                MathF.Round(flame.Position.Y),
                MathF.Round(flame.Position.Z))));

            // Fires are not lit at the same instant. Without this every candle in a room
            // throws its first ember on the same frame, which is a visible pulse in the
            // first second after a door.
            _emberDue = (float)_random.NextDouble() * EmberEvery;
            _smokeDue = (float)_random.NextDouble() * SmokeEvery;
        }

        public Flame Flame { get; }

        public bool Alight { get; set; }

        /// <summary>The model the scene is drawing this fire as, once it is known.</summary>
        public PlacedModel? Drawn { get; set; }

        /// <summary>How often an ember leaves, in seconds.</summary>
        private float EmberEvery => 1f / (3f + (11f * Flame.Size));

        /// <summary>How often a puff of smoke leaves, in seconds.</summary>
        private float SmokeEvery => 1f / (1.5f + (5.5f * Flame.Size));

        public int Advance(float seconds, bool near)
        {
            int alive = 0;

            for (int i = 0; i < _motes.Length; i++)
            {
                ref Mote mote = ref _motes[i];

                if (mote.Life <= 0f)
                {
                    continue;
                }

                mote.Life -= seconds;

                if (mote.Life <= 0f)
                {
                    continue;
                }

                mote.Position += mote.Velocity * seconds;

                // Rising air, and the drag that stops an ember before it reaches the
                // ceiling. Smoke keeps far more of its speed than an ember does, which is
                // why it is the thing that gets high and the ember is the thing that dies
                // in the first foot.
                mote.Velocity *= MathF.Pow(mote.Smoke ? 0.72f : 0.16f, seconds);
                mote.Velocity += new Vector3(0f, mote.Smoke ? Flame.Height * 0.6f : 0f, 0f) * seconds;

                alive++;
            }

            if (Drawn is { } model)
            {
                Alight = model.Visible;
            }

            if (!Alight || !near)
            {
                return alive;
            }

            _emberDue -= seconds;
            _smokeDue -= seconds;

            while (_emberDue <= 0f)
            {
                _emberDue += EmberEvery;
                Light(smoke: false);
            }

            while (_smokeDue <= 0f)
            {
                _smokeDue += SmokeEvery;
                Light(smoke: true);
            }

            return alive;
        }

        public void Collect(List<Particle> into)
        {
            foreach (Mote mote in _motes)
            {
                if (mote.Life > 0f)
                {
                    into.Add(Draw(mote));
                }
            }
        }

        /// <summary>Turns a mote into what the pass draws.</summary>
        private static Particle Draw(Mote mote)
        {
            // Nought when it was lit and one when it goes out.
            float age = Math.Clamp(1f - (mote.Life / MathF.Max(mote.Span, 1e-4f)), 0f, 1f);

            if (!mote.Smoke)
            {
                // An ember cools as it flies: yellow-white at the fire, orange in the
                // middle of its arc, a dull red as it goes out. Its brightness goes with
                // it, so the last of it dims rather than blinking off.
                var hot = new Vector3(1f, 0.86f, 0.55f);
                var cold = new Vector3(1f, 0.24f, 0.05f);

                float fade = (1f - age) * (1f - age);

                return new Particle(
                    mote.Position,
                    mote.Size * (0.55f + (0.45f * (1f - age))),
                    new Vector4(Vector3.Lerp(hot, cold, age), fade),
                    mote.Spin,

                    // Wholly additive: an ember is light rather than a thing, and blending
                    // it over the wall behind would punch a dull orange hole in it.
                    1f);
            }

            // Smoke spreads as it rises, and its edges thin out long before its middle
            // does. Fading in over the first fifth of its life is what keeps a puff from
            // appearing out of nothing at the top of the flame.
            float thickness = MathF.Min(age / 0.2f, 1f) * (1f - age) * (1f - age);

            var warm = new Vector3(0.42f, 0.24f, 0.13f);
            var grey = new Vector3(0.17f, 0.17f, 0.19f);

            return new Particle(
                mote.Position,
                mote.Size * (1f + (2.2f * age)),
                new Vector4(Vector3.Lerp(warm, grey, MathF.Min(age * 2.5f, 1f)), thickness * mote.Alpha),
                mote.Spin + (age * mote.Turn),
                0f);
        }

        /// <summary>Lights one mote, in the first slot that has gone out.</summary>
        private void Light(bool smoke)
        {
            for (int i = 0; i < _motes.Length; i++)
            {
                if (_motes[i].Life > 0f)
                {
                    continue;
                }

                _motes[i] = smoke ? Smoke() : Ember();
                return;
            }

            // Every slot in use. Dropping the new one rather than replacing the oldest,
            // because replacing one is a puff of smoke that vanishes in mid-air.
        }

        private Mote Ember()
        {
            float rise = Flame.Height * (2.2f + (2.6f * Spread()));
            float span = 0.6f + (0.9f * Flame.Size) + (0.4f * Spread());

            return new Mote
            {
                Position = Flame.Position + new Vector3(
                    Flame.Width * 0.22f * Either(),
                    Flame.Height * 0.3f * Spread(),
                    Flame.Width * 0.22f * Either()),

                Velocity = new Vector3(
                    Flame.Height * 0.8f * Either(), rise, Flame.Height * 0.8f * Either()),

                Size = MathF.Max(Flame.Width * (0.05f + (0.05f * Spread())), 0.25f),
                Life = span,
                Span = span,
                Alpha = 1f,
                Spin = Spread() * MathF.Tau,
                Smoke = false,
            };
        }

        private Mote Smoke()
        {
            float span = 1.6f + (3.4f * Flame.Size) + (1.2f * Spread());

            return new Mote
            {
                // Above the flame rather than in it: smoke that starts inside the card
                // draws in front of the fire and greys it out.
                Position = Flame.Position + new Vector3(
                    Flame.Width * 0.2f * Either(),
                    Flame.Height * (0.55f + (0.2f * Spread())),
                    Flame.Width * 0.2f * Either()),

                Velocity = new Vector3(
                    Flame.Height * 0.35f * Either(),
                    Flame.Height * (0.9f + (0.7f * Spread())),
                    Flame.Height * 0.35f * Either()),

                Size = MathF.Max(Flame.Width * (0.35f + (0.2f * Spread())), 0.8f),
                Life = span,
                Span = span,

                // A candle's smoke is barely there and a bowl of fire's is a column. It is
                // the difference between a room with a fire in it and a room on fire.
                Alpha = 0.06f + (0.26f * Flame.Size),
                Spin = Spread() * MathF.Tau,
                Turn = Either() * 0.8f,
                Smoke = true,
            };
        }

        /// <summary>Nought to one.</summary>
        private float Spread() => (float)_random.NextDouble();

        /// <summary>Minus one to one.</summary>
        private float Either() => ((float)_random.NextDouble() * 2f) - 1f;
    }

    /// <summary>One ember or one puff of smoke while it lasts.</summary>
    private struct Mote
    {
        public Vector3 Position;
        public Vector3 Velocity;
        public float Size;
        public float Life;
        public float Span;
        public float Alpha;
        public float Spin;
        public float Turn;
        public bool Smoke;
    }
}
