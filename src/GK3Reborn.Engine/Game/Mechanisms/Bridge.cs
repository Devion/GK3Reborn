using System.Globalization;
using System.Numerics;
using GK3Reborn.Game.Interaction;
using GK3Reborn.Rendering;
using GK3Reborn.Sheep;

namespace GK3Reborn.Game.Mechanisms;

/// <summary>
/// TE5: the bridge that is not there.
/// </summary>
/// <remarks>
/// <para>
/// Nine tiles across a chasm, and each of them is only solid some of the time. A tile
/// <em>glints</em> to say it can be jumped to, <em>glows</em> while Gabriel is standing on
/// it, and then goes out — and if he is still on it when it does, he falls. Getting across
/// is reading the pattern and moving before the floor stops existing.
/// </para>
/// <para>
/// <b>The whole puzzle is code.</b> The scene file declares nine hidden props and the room
/// has no idea where they go; the grid, the timings, which jump animation goes with which
/// direction and what a wrong jump costs are all here. What the scripts own is the
/// spoken part — the cutscene at the near end, the two ways of dying, the one at the far
/// end — and those are called by name.
/// </para>
/// <para>
/// <b>Nothing turns on a timer the player cannot see.</b> A tile's state has a duration
/// and the duration comes from the animation the artists drew for it, so the rhythm the
/// player learns is the rhythm the art states. The one set of numbers invented here is the
/// staggered sleep that starts the pattern off, which the reference's author also had to
/// invent and says so.
/// </para>
/// <para>
/// Adapted from G-Engine's <c>Bridge</c> under GPL-3, attributed in NOTICE.
/// </para>
/// </remarks>
public sealed class Bridge : SceneMechanism
{
    /// <summary>How many tiles the bridge has.</summary>
    private const int Count = 9;

    /// <summary>Where the grid's own origin is in the room.</summary>
    private static readonly Vector3 Origin = new(-125f, 0f, -180f);

    /// <summary>How far apart the tiles are.</summary>
    private const float Spacing = 45f;

    /// <summary>How far a tile's top is above the grid, so boots land on it rather than in it.</summary>
    private const float Thickness = 2f;

    /// <summary>Where Gabriel stands before the first jump.</summary>
    private static readonly Vector2 Start = new(-78f, -220f);

    /// <summary>How near that spot counts as being at it.</summary>
    private const float Near = 20f;

    /// <summary>Where each tile sits on the grid, across and along.</summary>
    /// <remarks>
    /// The path, as the artists laid it: two steps right and back to the left, a gap, and
    /// then a run down the far side. Nothing about it is derivable — it is the puzzle.
    /// </remarks>
    private static readonly (int Across, int Along)[] Grid =
    [
        (1, 0), (2, 1), (0, 2), (0, 4), (1, 5), (2, 7), (0, 8), (0, 9), (1, 10),
    ];

    /// <summary>What a tile is doing.</summary>
    private enum Phase
    {
        /// <summary>Not there at all, and not coming back until the puzzle starts.</summary>
        Out,

        /// <summary>Catching the light: solid, and safe to jump to.</summary>
        Glinting,

        /// <summary>Lit under Gabriel's feet, and counting down.</summary>
        Glowing,

        /// <summary>Gone, for a while.</summary>
        Sleeping,

        /// <summary>
        /// Stood on once, and solid from then on.
        /// </summary>
        /// <remarks>
        /// <b>A deliberate divergence, and the only one in this room.</b> The original puts
        /// a tile out again a few seconds after Gabriel lands on it and drops him if he is
        /// still there, which makes the crossing a sequence of timed jumps: the player must
        /// read the pattern ahead <em>and</em> keep moving, and a moment's thought about
        /// where to go next is fatal. The reading is the puzzle; the hurrying is a reaction
        /// test laid over it. This keeps the first and drops the second — a plate he has
        /// found stays found, and the tiles he has not reached yet still come and go, so
        /// there is still a pattern to read and still a wrong jump to make.
        /// </remarks>
        Held,
    }

    /// <summary>One tile.</summary>
    private sealed class Tile
    {
        /// <summary>The prop, when the room has it.</summary>
        public PlacedModel? Model { get; set; }

        /// <summary>Where it is, once it has been laid out.</summary>
        public Vector3 Where { get; set; }

        /// <summary>What it is doing.</summary>
        public Phase Doing { get; set; } = Phase.Out;

        /// <summary>How long it has been doing it.</summary>
        public double Since { get; set; }

        /// <summary>And how long that lasts.</summary>
        public double Lasts { get; set; }
    }

    private readonly Tile[] _tiles = [.. Enumerable.Range(0, Count).Select(_ => new Tile())];

    /// <summary>Which tile Gabriel is standing on, or −1 while he is off the bridge.</summary>
    private int _standing = -1;

    /// <summary>Which he is in the air towards, or −1.</summary>
    private int _towards = -1;

    /// <summary>Whether a jump is under way.</summary>
    private bool _jumping;

    /// <summary>Which way he is looking, kept between jumps.</summary>
    /// <remarks>
    /// Only ever read where a jump has no direction to take one from, which is a jump of no
    /// distance -- something the move table cannot produce, and a value rather than a throw
    /// if it ever does.
    /// </remarks>
    private float _facing;

    /// <summary>Whether the opening cutscene has been played.</summary>
    private bool _greeted;

    /// <summary>What the pointer is over.</summary>
    private string _under = string.Empty;

    /// <summary>Creates the mechanism.</summary>
    /// <param name="world">The room.</param>
    /// <param name="api">The script host.</param>
    public Bridge(SceneUpdate world, Gk3SheepApi api)
        : base(world, api)
    {
    }

    /// <inheritdoc/>
    public override string Name => "Bridge";

    /// <inheritdoc/>
    public override string Report() =>
        $"{_tiles.Count(t => t.Model is not null)} of {Count} tiles found";

    /// <inheritdoc/>
    public override bool Perform(string asked) => false;

    /// <inheritdoc/>
    /// <remarks>
    /// The tiles are declared with no position at all, so laying them on their grid is the
    /// first thing that has to happen: without it all nine are stacked at the room's origin
    /// and the bridge is a single flickering square in the wrong place.
    /// </remarks>
    public override void Begin()
    {
        for (int i = 0; i < Count; i++)
        {
            _tiles[i].Model = World.ModelNamed(
                string.Create(CultureInfo.InvariantCulture, $"te5sq0{i + 1}"));

            _tiles[i].Where = Origin +
                (Vector3.UnitX * Spacing * Grid[i].Across) +
                (Vector3.UnitZ * Spacing * Grid[i].Along);

            _tiles[i].Doing = Phase.Out;

            if (_tiles[i].Model is { } tile)
            {
                Stand(tile, _tiles[i].Where, 0f);
                World.Show(tile, false);
            }
        }

        _standing = -1;
        _towards = -1;
        _jumping = false;
        _greeted = false;
    }

    /// <inheritdoc/>
    public override void Advance(double seconds)
    {
        // Standing at the near end starts the puzzle: the cutscene once, and then the first
        // tile begins catching the light so the player knows where to go.
        if (AtTheEdge())
        {
            if (!_greeted)
            {
                _greeted = true;
                Call("PreFirstJump$");
            }

            if (_tiles[0].Doing == Phase.Out)
            {
                Glint(0);
            }
        }

        _clock += seconds;

        for (int i = 0; i < Count; i++)
        {
            Step(i, seconds);
        }
    }

    /// <summary>Moves one tile on.</summary>
    private void Step(int index, double seconds)
    {
        Tile tile = _tiles[index];

        // A tile exists exactly while it is glinting or glowing. That is the puzzle: what
        // can be seen can be landed on, and nothing else can.
        if (tile.Model is { } prop)
        {
            World.Show(prop, tile.Doing is Phase.Glinting or Phase.Glowing or Phase.Held);
        }

        tile.Since += seconds;

        switch (tile.Doing)
        {
            case Phase.Glinting when tile.Since >= tile.Lasts:
                // Its moment has passed. Unless Gabriel is already on his way to it, in
                // which case it holds until he lands — the reference does the same, and
                // without it a jump the player was entitled to make kills them mid-air.
                if (_towards != index)
                {
                    Sleep(index, SleepSeconds);
                }

                break;

            case Phase.Glowing when tile.Since >= tile.Lasts:
                // Where the original puts the tile out and drops whoever is on it, this
                // keeps it. Only a tile he has landed on ever glows, so every tile that
                // reaches here is one he has found -- see Phase.Held for why.
                tile.Doing = Phase.Held;
                tile.Since = 0;

                break;

            case Phase.Sleeping when tile.Since >= tile.Lasts && InThePuzzle():
                // The first tile always comes back — it is the way in, and a player who
                // stepped away has to be able to start again. The rest only come back while
                // somebody is out there on them.
                if (index == 0 || _standing >= 0)
                {
                    Glint(index);
                }

                break;

            default:
                break;
        }
    }

    /// <summary>How long a tile stays away before it catches the light again.</summary>
    private const double SleepSeconds = 2.0;

    /// <summary>How long the whole room has been running, for the shimmer's phase.</summary>
    private double _clock;

    /// <summary>
    /// A ghost of the plates that are not there.
    /// </summary>
    /// <param name="eye">Where the camera is, for sorting.</param>
    /// <returns>The sprites, farthest first.</returns>
    /// <remarks>
    /// <para>
    /// <b>The bridge asks the player to read a pattern they cannot see.</b> A tile that is
    /// out is drawn as nothing at all, so the chasm gives away neither the shape of the path
    /// nor the fact that there is one. This puts a trace of them back: enough to say
    /// <em>something is here</em>, not enough to say <em>stand on it</em>.
    /// </para>
    /// <para>
    /// <b>Two things, and they do different jobs.</b> A body of cloud says where the plate
    /// would be and has no shape worth reading; an outline says what shape it is, and is the
    /// half that has to look like glass rather than like smoke. Drawn in that order, so the
    /// edge sits in front of its own haze.
    /// </para>
    /// <para>
    /// <b>Smoke, not embers.</b> The particle pass draws a sprite two ways and the choice is
    /// the sprite's additiveness: at or above a half it is a plain soft disc, which is what
    /// an ember wants and is why the first attempt at this read as a grid of glowing dots.
    /// Below a half the fragment stage cuts two octaves of value noise out of the disc — see
    /// <c>ParticleShaders</c> — and overlapping sprites stop being circles and become one
    /// body of cloud. The cloud is on that side of the line and the outline is on the other,
    /// which is the whole of why one looks like fog and the other like a lit edge.
    /// </para>
    /// <para>
    /// <b>The flatness is in the layout, not in the sprite.</b> Every sprite faces the
    /// camera, so no arrangement of them is a flat plate seen edge-on — but a mat of them
    /// lying in the tile's own plane reads as haze lying in the chasm, which is how ground
    /// fog is drawn anywhere it is drawn with sprites at all. A tile-shaped translucent quad
    /// carrying a shader of its own would be the other way to do it, and would mean a second
    /// blended pipeline in both backends to draw what this pass already blends.
    /// </para>
    /// <para>
    /// <b>Nothing here is still.</b> Each puff creeps round its own slow ellipse, rises and
    /// sinks on its own beat, swells and shrinks, and turns — the noise is cut in the
    /// sprite's own frame, so turning it churns the cloud from inside rather than sliding it
    /// across the screen. The rates deliberately do not divide each other: anything that does
    /// comes back into step and starts to pulse, and a pulse reads as a mechanism.
    /// </para>
    /// <para>
    /// <b>It ends the moment the plate is real.</b> Only a tile that is out or sleeping is
    /// haunted; one catching the light, one lit under his feet and one he has already found
    /// are drawn as geometry, and a haze over any of them would blunt the one signal the
    /// puzzle has. Nothing here may be mistaken for a glint — so it is cold, dim and drifting
    /// where a glint is warm, bright and a hard-edged slab of lit geometry, and it stays
    /// under the alpha at which a cloud stops looking like air and starts looking like a
    /// surface.
    /// </para>
    /// </remarks>
    public override IReadOnlyList<Particle> Particles(Vector3 eye)
    {
        var ghost = new List<Particle>(Count * (Puffs + RimPoints));

        for (int i = 0; i < Count; i++)
        {
            if (_tiles[i].Doing is not (Phase.Out or Phase.Sleeping))
            {
                continue;
            }

            // A swell that travels along the bridge rather than everything breathing at
            // once, so the chasm looks like it is being crossed by something.
            float tide = (float)((_clock * TideRate) - (i * TideLag));

            Haze(ghost, _tiles[i].Where, tide, i);
            Rim(ghost, _tiles[i].Where, tide, i);
        }

        // Farthest first. The cloud takes a little of the light behind it rather than being
        // purely additive, so the order is load-bearing here in a way it is not for CS2's
        // laser beams.
        ghost.Sort((a, b) =>
            Vector3.DistanceSquared(b.Position, eye)
                .CompareTo(Vector3.DistanceSquared(a.Position, eye)));

        return ghost;
    }

    /// <summary>The body of cloud that says a plate belongs here.</summary>
    /// <param name="into">Where the sprites go.</param>
    /// <param name="where">The middle of the tile.</param>
    /// <param name="tide">How far through the travelling swell this tile is.</param>
    /// <param name="tile">Which tile, so no two churn in step.</param>
    private void Haze(List<Particle> into, Vector3 where, float tide, int tile)
    {
        for (int puff = 0; puff < Puffs; puff++)
        {
            // Laid on a golden-angle spiral rather than on a grid. A grid of anything is a
            // grid however soft each thing on it is, and the eye finds the rows.
            float turn = puff * GoldenAngle;
            float from = MathF.Sqrt((puff + 0.5f) / Puffs) * Spacing * Reach;

            // Its own slow ellipse, on two rates that do not divide each other, so a puff
            // never retraces the path it took a moment ago.
            float own = (float)(_clock * DriftRate) + (puff * 1.31f);

            float x = (MathF.Cos(turn) * from) + (MathF.Cos(own) * Drift);
            float z = (MathF.Sin(turn) * from) + (MathF.Sin(own * 0.73f) * Drift);

            // And its own rise and fall, so the mat has a little body and does not read as a
            // decal lying on nothing.
            float lift = Rise *
                (0.5f + (0.5f * MathF.Sin((float)(_clock * RiseRate) + (puff * 2.17f))));

            // Swelling and shrinking. Wide enough that neighbours overlap several deep
            // whatever the phase, which is what makes them one cloud rather than many.
            float size = Wide *
                (0.72f + (0.42f * MathF.Sin((float)(_clock * SwellRate) + (puff * 0.87f))));

            // Thinner towards the edge of the mat, so the cloud has no rim of its own. A
            // cloud with a rim is a disc, which is the thing this is trying to stop being —
            // and the only edge that should be legible is the outline drawn over it.
            float fade = 1f - (0.55f * (from / (Spacing * Reach)));
            float breath = 0.55f + (0.45f * MathF.Sin(tide + (puff * 1.61f)));

            into.Add(new Particle(
                where + new Vector3(x, Above + lift, z),
                size,

                // Colder and faintly green where it is thickest. Nothing else in this room
                // is green, and it is what keeps the cloud from reading as ordinary blue
                // haze at the edge of a light.
                //
                // Alpha is also the noise's seed in the fragment stage, so two puffs at the
                // same alpha are the same lump of cloud. They never are here: the breath,
                // the fade and the tide all feed it.
                new Vector4(
                    Vector3.Lerp(Thin, Deep, breath),
                    CloudAlpha * fade * breath),

                // Turned, and turning.
                (float)(_clock * SpinRate) + (puff * 0.55f) + (tile * 0.9f),

                // Under a half: the noise-cut cloud rather than the plain disc an ember
                // gets. Only just under, so what it mostly does is glow — at this alpha the
                // coverage it writes takes almost nothing away from the chasm behind.
                Cloudy));
        }
    }

    /// <summary>The glassy edge of the plate that is not there.</summary>
    /// <param name="into">Where the sprites go.</param>
    /// <param name="where">The middle of the tile.</param>
    /// <param name="tide">How far through the travelling swell this tile is.</param>
    /// <param name="tile">Which tile, so no two edges catch the light together.</param>
    /// <remarks>
    /// <para>
    /// <b>Fully additive, so it is light and not fog.</b> That also puts it back on the
    /// smooth-disc side of the fragment stage's test, which is what makes it read as glass:
    /// an edge caught by a light is clean, and the noise that makes the cloud look like
    /// cloud would make this look like more cloud.
    /// </para>
    /// <para>
    /// <b>What sells the glass is the highlight running round it.</b> A square drawn at one
    /// brightness is a neon sign; a square with a bright point travelling round its
    /// perimeter is something with a surface, catching a light that is moving relative to
    /// it. Raised to a high power so the highlight is a short arc rather than a slow bulge.
    /// </para>
    /// <para>
    /// And the edge is not rigid: it lifts and settles along its length on a wave that is
    /// not the perimeter's own period, so the outline never holds one shape.
    /// </para>
    /// </remarks>
    private void Rim(List<Particle> into, Vector3 where, float tide, int tile)
    {
        for (int point = 0; point < RimPoints; point++)
        {
            // Once round the square. Four sides, walked in order, so the highlight below
            // travels rather than jumping between them.
            float round = (float)point / RimPoints;
            float along = round * 4f;
            int side = Math.Min((int)along, 3);
            float across = ((along - side) * 2f * Edge) - Edge;

            (float X, float Z) at = side switch
            {
                0 => (across, -Edge),
                1 => (Edge, across),
                2 => (-across, Edge),
                _ => (-Edge, -across),
            };

            // The edge breathes along its length, on a wave that does not divide the
            // perimeter — an outline that came back into shape every lap would read as a
            // rotating object rather than as something insubstantial.
            float wave = MathF.Sin((round * MathF.Tau * RimWaves) + (float)(_clock * RimRate) + tile);

            // A short bright arc travelling round. sin is only positive for half the lap and
            // the power narrows that to a fraction of it, so most of the outline is dim and
            // one part of it is catching something.
            float sweep = MathF.Sin((round - (float)(_clock * SweepRate) - (tile * 0.13f)) * MathF.Tau);
            float glint = MathF.Pow(MathF.Max(sweep, 0f), SweepFocus);

            float breath = 0.6f + (0.4f * MathF.Sin(tide + (round * 3.1f)));

            into.Add(new Particle(
                where + new Vector3(at.X, Above + (wave * RimLift), at.Z),
                RimWide * (0.85f + (0.3f * glint)),

                // Pale and cold at rest, and towards white where the highlight passes. Glass
                // does not take a colour of its own; what it shows is whatever is catching
                // it, and here that is nothing anybody can see.
                new Vector4(
                    Vector3.Lerp(Glass, Caught, glint),
                    RimAlpha * breath * (0.45f + (0.9f * glint))),

                (float)(_clock * SpinRate * 0.5f) + (point * 0.4f),

                // Wholly additive: a plain soft disc that adds its light and hides nothing.
                1f));
        }
    }

    /// <summary>How many puffs make up one tile's cloud.</summary>
    /// <remarks>
    /// Sixteen, with twenty-four more round the edge, and at most nine tiles are out at once
    /// — 360 sprites at the very worst against a buffer that holds eight hundred. They have
    /// to overlap several deep to read as one body: the first attempt used nine, spaced
    /// further apart than they were wide, and looked like nine dots because that is exactly
    /// what it was.
    /// </remarks>
    private const int Puffs = 16;

    /// <summary>And how many points trace the outline.</summary>
    /// <remarks>
    /// Six a side. Fewer and the corners are the only thing the eye finds; many more and the
    /// travelling highlight stops being a point of light and becomes a lit segment.
    /// </remarks>
    private const int RimPoints = 24;

    /// <summary>The angle a spiral turns by so that it never lines up with itself.</summary>
    private const float GoldenAngle = 2.39996323f;

    /// <summary>How far out the cloud spreads, as a fraction of a tile's pitch.</summary>
    /// <remarks>
    /// Wider than the plate, and wider than the outline, so the edge is drawn over its own
    /// haze rather than beside it.
    /// </remarks>
    private const float Reach = 0.58f;

    /// <summary>How far the outline sits from the middle.</summary>
    /// <remarks>
    /// A little inside the pitch: the plates do not touch, and an outline drawn at the full
    /// spacing would make the bridge look like a paved floor with the paving missing rather
    /// than like nine separate things.
    /// </remarks>
    private const float Edge = Spacing * 0.40f;

    /// <summary>How wide one puff of cloud is.</summary>
    private const float Wide = 15f;

    /// <summary>And one point of the outline, which is far tighter.</summary>
    private const float RimWide = 4.5f;

    /// <summary>How far a puff creeps from where it belongs.</summary>
    private const float Drift = 6f;

    /// <summary>And how far it rises.</summary>
    private const float Rise = 7f;

    /// <summary>How far the outline lifts and settles along its length.</summary>
    private const float RimLift = 3f;

    /// <summary>How far above the grid all of it lies.</summary>
    private const float Above = 3f;

    /// <summary>How thick the cloud is at its thickest.</summary>
    /// <remarks>
    /// Low. Sixteen of these lie over each other, so what one puff contributes is nothing
    /// like what the tile shows — and the cloud has to stay under the alpha at which it
    /// stops looking like air and starts looking like a surface somebody could stand on.
    /// </remarks>
    private const float CloudAlpha = 0.085f;

    /// <summary>And how bright the outline is.</summary>
    private const float RimAlpha = 0.30f;

    /// <summary>How fast the swell travels along the bridge, in radians a second.</summary>
    private const float TideRate = 0.9f;

    /// <summary>And how far behind the tile before it each tile is.</summary>
    private const float TideLag = 0.8f;

    /// <summary>How fast a puff creeps round its ellipse.</summary>
    private const float DriftRate = 0.37f;

    /// <summary>How fast it rises and sinks.</summary>
    private const float RiseRate = 0.29f;

    /// <summary>How fast it swells and shrinks.</summary>
    private const float SwellRate = 0.53f;

    /// <summary>And how fast it turns.</summary>
    private const float SpinRate = 0.11f;

    /// <summary>How many times the outline waves over one lap of itself.</summary>
    /// <remarks>Not a whole number, so the wave never closes on itself.</remarks>
    private const float RimWaves = 2.6f;

    /// <summary>How fast that wave runs along it.</summary>
    private const float RimRate = 0.8f;

    /// <summary>How fast the highlight travels round, in laps a second.</summary>
    private const float SweepRate = 0.22f;

    /// <summary>How tight it is: higher is a shorter, brighter arc.</summary>
    private const float SweepFocus = 6f;

    /// <summary>
    /// How much of an ember there is in the cloud, which is what picks the sprite's shape.
    /// </summary>
    /// <remarks>
    /// Just under the half the fragment stage tests against, so the cloud noise is cut out of
    /// it while it stays very nearly a light rather than a fog. Take it over a half and every
    /// puff becomes the smooth disc this began as.
    /// </remarks>
    private const float Cloudy = 0.46f;

    /// <summary>
    /// The colour where the cloud is thinnest.
    /// </summary>
    /// <remarks>
    /// Cold and faint on purpose. The glint the tiles use is the room's own warm gold, and
    /// the two must not be confusable at a glance — a player who reads this as "solid" walks
    /// into the chasm.
    /// </remarks>
    private static readonly Vector3 Thin = new(0.13f, 0.19f, 0.30f);

    /// <summary>And where it is thickest.</summary>
    private static readonly Vector3 Deep = new(0.10f, 0.26f, 0.28f);

    /// <summary>The outline at rest.</summary>
    private static readonly Vector3 Glass = new(0.22f, 0.40f, 0.52f);

    /// <summary>And where the highlight is passing over it.</summary>
    private static readonly Vector3 Caught = new(0.78f, 0.92f, 1.00f);

    /// <summary>Whether the puzzle is under way at all.</summary>
    private bool InThePuzzle() => _standing >= 0 || AtTheEdge();

    /// <summary>Whether Gabriel is standing at the near end, ready to start.</summary>
    private bool AtTheEdge()
    {
        if (World.Where(Story.Ego) is not { } where)
        {
            return false;
        }

        return new Vector2(where.X - Start.X, where.Z - Start.Y).LengthSquared() <= Near * Near;
    }

    /// <summary>Sets a tile catching the light.</summary>
    private void Glint(int index) => Begin(index, Phase.Glinting, Clip("TE5GLINT", index));

    /// <summary>Sets a tile lit under Gabriel's feet.</summary>
    private void Glow(int index) => Begin(index, Phase.Glowing, Clip("TE5GLOW", index));

    /// <summary>Puts a tile out for a while.</summary>
    private void Sleep(int index, double seconds)
    {
        _tiles[index].Doing = Phase.Sleeping;
        _tiles[index].Since = 0;
        _tiles[index].Lasts = seconds;
    }

    /// <summary>Starts one of the two animated states, for as long as its animation lasts.</summary>
    private void Begin(int index, Phase phase, string clip)
    {
        _tiles[index].Doing = phase;
        _tiles[index].Since = 0;

        // The art states the rhythm: a tile is visible for exactly as long as the animation
        // the artists drew for it, so nothing here has to choose how long a player has.
        _tiles[index].Lasts = World.Animations?.SecondsOf(clip) ?? 1.5;

        World.Play(clip);
    }

    /// <summary>What one of the per-tile animations is called.</summary>
    private static string Clip(string stem, int index) =>
        string.Create(CultureInfo.InvariantCulture, $"{stem}0{index + 1}");

    /// <inheritdoc/>
    public override void Pointing(ScenePick? under, bool busy) =>
        _under = under?.Name ?? string.Empty;

    /// <inheritdoc/>
    /// <remarks>
    /// <b>The bridge takes every click while Gabriel is on it.</b> There is no walking out
    /// there — the floor is nine squares that come and go — so a click is a jump, a jump
    /// back, or a step into the chasm, and nothing else may have it. Off the bridge the
    /// room behaves normally, except that clicking the first tile is how the puzzle begins.
    /// </remarks>
    public override bool TakesClick(ScenePick? under)
    {
        if (_jumping)
        {
            return _standing >= 0;
        }

        int tile = Hovered(under?.Name ?? _under);

        if (_standing < 0)
        {
            if (tile != 0)
            {
                return false;
            }

            Jump(0);

            return true;
        }

        if (tile >= 0 && tile != _standing)
        {
            Jump(tile);

            return true;
        }

        string name = under?.Name ?? _under;

        // Back off the bridge, which is only possible from the first two tiles: from
        // anywhere else the near end is too far and Gabriel simply does not go.
        if (name.Equals(NearFloor, StringComparison.OrdinalIgnoreCase))
        {
            if (_standing is 0 or 1)
            {
                Jump(-1);
            }

            return true;
        }

        // And the chasm itself, which the player usually clicks by accident as a tile goes
        // out from under the pointer. It is still a step into thin air.
        if (name.Equals(Chasm, StringComparison.OrdinalIgnoreCase))
        {
            Fall(inTheAir: true);

            return true;
        }

        return true;
    }

    /// <inheritdoc/>
    public override bool TakesFloorClick() => _standing >= 0;

    /// <summary>The ground at the near end.</summary>
    private const string NearFloor = "te5_floor";

    /// <summary>What is under the bridge, which is nothing.</summary>
    private const string Chasm = "te5_hittest_floor";

    /// <summary>The far side, which counts as a tenth tile.</summary>
    private const string FarSide = "te5hittestend";

    /// <summary>Which tile a name refers to: −1 for the near end, 9 for the far side.</summary>
    private static int Hovered(string name)
    {
        if (name.Equals(FarSide, StringComparison.OrdinalIgnoreCase))
        {
            return Count;
        }

        return name.Length == 7 &&
            name.StartsWith("te5sq0", StringComparison.OrdinalIgnoreCase) &&
            name[6] - '1' is >= 0 and < Count and { } index
            ? index
            : -1;
    }

    /// <summary>
    /// Jumps to a tile, or back to the near end.
    /// </summary>
    /// <param name="index">
    /// The tile, <c>-1</c> for the near end, or <see cref="Count"/> for the far side.
    /// </param>
    /// <remarks>
    /// <b>A jump too far is a death, not a refusal.</b> Gabriel can reach a square, a
    /// diagonal or a knight's move and no further; asking for more is the player getting it
    /// wrong, and the game lets them find out. The one exception is the first jump, which
    /// can only be onto tile one.
    /// </remarks>
    private void Jump(int index)
    {
        (int Across, int Along) to = index switch
        {
            >= 0 and < Count => Grid[index],
            Count => (1, 11),
            _ => (1, -1),
        };

        Vector3 landing = index switch
        {
            >= 0 and < Count => _tiles[index].Where,
            Count => _tiles[Count - 1].Where + (Vector3.UnitZ * Spacing),
            _ => new Vector3(Start.X, 0, Start.Y),
        };

        landing.Y += Thickness;

        string? clip;

        if (_standing < 0)
        {
            // Onto the bridge, and only onto its first tile.
            if (index != 0)
            {
                return;
            }

            clip = "GABTE5JUMP01SQ";
        }
        else
        {
            (int Across, int Along) from = Grid[_standing];
            int across = to.Across - from.Across;
            int along = to.Along - from.Along;

            clip = Leap(across, along);

            if (clip is null)
            {
                Fall(inTheAir: true);

                return;
            }
        }

        // Which way he is looking while he does it. Every clip is authored along his own
        // facing, so this is what aims the jump: without it he leapt down the bridge
        // whatever was clicked and arrived sideways at the tile, which is most of the eight
        // moves on the path and looks like the animation has come loose from the game.
        Vector3 standing = World.Where(Story.Ego) ?? landing;
        Vector3 away = landing - standing;

        // Flat: the tiles are all at one height and a heading is a compass bearing, so the
        // two-unit step up onto a tile top must not tilt it.
        away.Y = 0;

        float heading = away.LengthSquared() > 0.01f
            ? Navigation.Walker.Heading(away)
            : _facing;

        // Snapped rather than turned. Turn is an animated pivot that takes about a second,
        // and the jump has already started: he would take off facing the old way and swing
        // round in mid-air.
        World.Place(Story.Ego, standing, heading);

        _facing = heading;
        _towards = index;
        _jumping = true;

        double flight = World.Play(clip);

        Then(flight, () =>
        {
            // The first landing sets the rest of the bridge going. Until then only the way
            // in is lit, so the player is not asked to read a pattern before they are on it.
            if (_standing < 0)
            {
                Pattern();
            }

            _standing = index;
            _towards = -1;

            World.Place(Story.Ego, landing, heading);

            if (index >= 0 && index < Count)
            {
                Glow(index);
            }

            // He keeps the way he jumped rather than being squared up to the bridge. The
            // clip that follows is a landing, not a turn, and the next jump sets its own
            // heading from where it is going -- so nothing accumulates and nothing drifts.
            double settling = World.Play("GABTE5STAND");

            Then(settling, () =>
            {
                _jumping = false;
                World.Place(Story.Ego, landing, heading);

                if (index == Count)
                {
                    Story.SetFlag("CrossedBridge");
                    Call("GabeCrossedBridge$");
                }
            });
        });
    }

    /// <summary>
    /// Which clip carries Gabriel a given distance, or null when nothing does.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Four kinds of jump exist and no more: one square along, two squares along, a
    /// diagonal, and a knight's move. Anything else is a jump he cannot make.
    /// </para>
    /// <para>
    /// <b>The shape decides the clip and the heading decides the direction</b>, which is
    /// why a diagonal has a clip of its own and a jump backwards has none. The original
    /// keeps a table of every move on the board — twenty-one of them, eleven distinct — and
    /// they pair off exactly: the entry for going one square forward and the entry for
    /// going one square back are two different entries, and the game ships one animation.
    /// The same holds for the diagonal, for the two-square hop and for both knight's moves,
    /// and there are only ever four clips. So a clip is authored along whichever way Gabriel
    /// is looking, and turning him to look at the tile he is jumping to is what makes it
    /// carry him there.
    /// </para>
    /// <para>
    /// <c>GABTE5JUMP45</c> is the diagonal, and was going unused: diagonals were being sent
    /// through the one-square clip, which is authored for a shorter jump. Three of the eight
    /// moves along the path are diagonals.
    /// </para>
    /// </remarks>
    private static string? Leap(int across, int along)
    {
        int sideways = Math.Abs(across);
        int forward = Math.Abs(along);

        return (sideways, forward) switch
        {
            (0, 1) => "GABTE5JUMP01SQ",
            (0, 2) => "GABTE5JUMP02SQ",
            (1, 1) => "GABTE5JUMP45",
            (1, 2) or (2, 1) => "GABTE5JUMP26KNIGHT",
            _ => null,
        };
    }

    /// <summary>
    /// Starts the tiles going, once Gabriel is on the first of them.
    /// </summary>
    /// <remarks>
    /// Eight staggered sleeps, so that the tiles do not all come back at once and there is
    /// a pattern to read. These durations are invented — the original's are not recoverable
    /// from anything it ships — and the reference's author says the same of its own. They
    /// are chosen so that a player moving promptly always has somewhere to go.
    /// </remarks>
    private void Pattern()
    {
        double[] staggered = [0, 5.0, 3.0, 6.0, 3.2, 0.5, 2.0, 0.01, 0.8];

        for (int i = 1; i < Count; i++)
        {
            Sleep(i, staggered[i]);
        }
    }

    /// <summary>
    /// Gabriel falls.
    /// </summary>
    /// <param name="inTheAir">Whether he was mid-jump rather than standing on a tile.</param>
    /// <remarks>
    /// The two have different animations and different lines, which is why the scripts keep
    /// them apart. Either way the bridge is put back and the player starts again.
    /// </remarks>
    private void Fall(bool inTheAir)
    {
        _jumping = true;

        // Unless the player has asked not to be. The two falls are decided here rather than
        // by a script, so the armour that catches a script's Die$ never sees them.
        if (!Deathless)
        {
            Call(inTheAir ? "BishopFallDie$" : "FallDie$");
        }

        World.Next(() =>
        {
            _standing = -1;
            _towards = -1;
            _jumping = false;
            _greeted = false;

            // What he has already found, he keeps. The plates he has stood on are knowledge
            // the player has earned, and making them walk the known half of the bridge again
            // is the tedium this room is being relieved of -- the same reason a plate stays
            // solid in the first place. Entering the room afresh still lays them all out:
            // see Begin, which is the one place the bridge starts from nothing.
            for (int i = 0; i < Count; i++)
            {
                if (_tiles[i].Doing != Phase.Held)
                {
                    _tiles[i].Doing = Phase.Out;
                }
            }
        });
    }

    /// <summary>Runs one of the room's own scripts.</summary>
    private void Call(string function) => Api.Invoke(
        "CallSheep",
        [SheepValue.FromString("TE5"), SheepValue.FromString(function)]);
}
