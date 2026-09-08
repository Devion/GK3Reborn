using System.Globalization;
using System.Numerics;
using GK3Reborn.Game.Interaction;
using GK3Reborn.Game.Navigation;
using GK3Reborn.Formats.Scenes;
using GK3Reborn.Rendering;

namespace GK3Reborn.Game.Mechanisms;

/// <summary>
/// CS2's five heads, and the beams that come out of them.
/// </summary>
public sealed class LaserHeads : SceneMechanism
{
    /// <summary>Where the five beams are meant to meet.</summary>
    private static readonly Vector3 Centre = new(4.02f, 57.0f, 18.90f);

    /// <summary>
    /// The five angles each head can be turned to, in degrees.
    /// </summary>
    private static readonly float[,] Angles =
    {
        { 160.57f, -163.08f, -145.32f, -126.91f, -90.95f },
        { -126.72f, -90.69f, -72.84f, -54.56f, -18.47f },
        { -54.56f, -18.47f, -0.70f, 16.62f, 53.28f },
        { 17.77f, 53.09f, 71.40f, 89.31f, 125.44f },
        { 89.05f, 125.27f, 143.58f, 161.53f, -162.23f },
    };

    /// <summary>How far each head stands from the middle.</summary>
    private const float HeadRadius = 129.5f;

    /// <summary>And each beam, which starts a little nearer.</summary>
    private const float BeamRadius = 128.0f;

    /// <summary>How high the heads stand.</summary>
    private const float HeadHeight = 52.0f;

    /// <summary>How wide a beam is drawn, as a fraction of the model's own width.</summary>
    private const float BeamWidth = 0.2f;

    /// <summary>How long the beam model is at its own scale.</summary>
    private const float BeamLength = 100.0f;

    /// <summary>How far ahead of itself a beam starts looking for what it lands on.</summary>
    private const float Clearance = 10f;

    /// <summary>How long a head takes to swing to its new angle.</summary>
    private const float TurnSeconds = 2.0f;

    /// <summary>
    /// How long a head waits before it starts.
    /// </summary>
    private const float TurnDelay = 0.8f;

    /// <summary>The angle each head is at now, in radians.</summary>
    private readonly float[] _facing = new float[Count];

    /// <summary>
    /// Where each head stands, worked out once and never again.
    /// </summary>
    private readonly Vector3[] _home = new Vector3[Count];

    /// <summary>Which of its five angles each head is turning towards.</summary>
    private readonly int[] _turned = new int[Count];

    /// <summary>How far through its swing each head is; negative is the wait before it.</summary>
    private readonly float[] _swinging = new float[Count];

    private readonly PlacedModel?[] _heads = new PlacedModel?[Count];
    private readonly PlacedModel?[] _beams = new PlacedModel?[Count];

    /// <summary>How far each beam reaches before something stops it.</summary>
    private readonly float[] _reach = new float[Count];

    /// <summary>How many heads there are.</summary>
    private const int Count = 5;

    /// <summary>Creates the mechanism.</summary>
    /// <param name="world">The room.</param>
    /// <param name="api">The script host.</param>
    public LaserHeads(SceneUpdate world, Gk3SheepApi api)
        : base(world, api)
    {
    }

    /// <inheritdoc/>
    public override string Name => "Laser";

    /// <summary>Whether the beams are switched on.</summary>
    public bool Lit { get; private set; }

    /// <summary>Where the ray goes to find what a beam lands on, when anything can answer.</summary>
    public Func<Ray, ScenePick?>? Cast { get; set; }

    /// <summary>The five names the beams must not be allowed to stop each other with.</summary>
    public static IReadOnlySet<string> Beams { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "cs2laser_01", "cs2laser_02", "cs2laser_03", "cs2laser_04", "cs2laser_05",
        };

    /// <inheritdoc/>
    public override void Begin()
    {
        for (int i = 0; i < Count; i++)
        {
            _heads[i] = World.ModelNamed(
                string.Create(CultureInfo.InvariantCulture, $"cs2head0{i + 1}"));
            _beams[i] = World.ModelNamed(
                string.Create(CultureInfo.InvariantCulture, $"cs2laser_0{i + 1}"));

            // The middle angle, which is where the puzzle starts and where the scripts
            // expect to find it: Check_Staircase asks for all five at 1, at 3, at 0 or at 4,
            // and 2 is none of those.
            _turned[i] = 2;
            _swinging[i] = TurnSeconds;
            _facing[i] = Radians(i, 2);
            _moved = true;
            _home[i] = Centre - (Direction(_facing[i]) * HeadRadius);
            _home[i].Y = HeadHeight;

            Story.SetVariable(
                string.Create(CultureInfo.InvariantCulture, $"Cs2Head{i + 1}"), 2);
        }

        Settle();
    }

    /// <inheritdoc/>
    public override string Report() =>
        $"{_heads.Count(h => h is not null)} of {Count} heads, " +
        $"{_beams.Count(b => b is not null)} of {Count} beams";

    /// <inheritdoc/>
    public override double Seconds(string asked)
    {
        ArgumentNullException.ThrowIfNull(asked);

        return Turn(asked) is var (head, direction) && head >= 0 &&
               Next(head, direction) is { } wanted &&
               World.Animations?.SecondsOf(Hands(direction, wanted)) is { } length
            ? length
            : 0;
    }

    /// <inheritdoc/>
    public override bool Perform(string asked)
    {
        ArgumentNullException.ThrowIfNull(asked);

        if (asked.Equals("toggleLasers", StringComparison.OrdinalIgnoreCase))
        {
            Toggle();

            return true;
        }

        if (Turn(asked) is var (head, direction) && head >= 0)
        {
            Swing(head, direction);

            return true;
        }

        return false;
    }

    /// <inheritdoc/>
    public override void Advance(double seconds)
    {
        bool moved = false;

        for (int i = 0; i < Count; i++)
        {
            if (_swinging[i] >= TurnSeconds)
            {
                continue;
            }

            _swinging[i] += (float)seconds;
            moved = true;

            // Interpolated as directions rather than as angles, so a head crossing the
            // wrap at ±180° takes the short way round instead of spinning through the
            // whole circle. Head one and head five both do: their five angles straddle it.
            Vector3 from = Direction(_facing[i]);
            Vector3 to = Direction(Radians(i, _turned[i]));
            float through = Math.Clamp(_swinging[i] / TurnSeconds, 0f, 1f);

            _facing[i] = Walker.Heading(Vector3.Lerp(from, to, through));
        }

        // A head that is turning drags six lights round the room with it, and the rig has
        // to be laid again for them to move. Not every frame: laying one rebuilds the
        // scene's light grid, so a turn is sampled a handful of times over its two seconds
        // rather than sixty — which nobody can see and the grid cannot afford.
        if (moved)
        {
            _since += seconds;

            if (_since >= RelightEvery)
            {
                _since = 0;
                _moved = true;
            }
        }
        else if (_since > 0)
        {
            // And once more when it stops, so the lights end exactly where the beam did
            // rather than wherever the last sample left them.
            _since = 0;
            _moved = true;
        }

        if (moved || Lit)
        {
            Settle();
        }
    }

    /// <summary>Puts every head and every beam where its angle says it goes.</summary>
    private void Settle()
    {
        for (int i = 0; i < Count; i++)
        {
            Vector3 looking = Direction(_facing[i]);

            if (_heads[i] is { } head)
            {
                Stand(head, _home[i], _facing[i]);
            }

            if (_beams[i] is not { } beam)
            {
                continue;
            }

            // The beam leaves the head's face rather than its middle: a pace in front of
            // it, at the height the five of them are meant to meet. In the reference the
            // beam is a child of the head and this is the offset it keeps, which is why it
            // swings round with the head instead of staying put.
            Vector3 from = _home[i] + (looking * (HeadRadius - BeamRadius));
            from.Y = Centre.Y;

            // How far the beam reaches: to whatever the room puts in front of it. Started a
            // little ahead of itself so that the head it comes out of is not the first
            // thing it meets, and the other four beams are ignored, or the first pair to
            // cross would each stop the other dead.
            float reach = BeamLength;

            if (Lit && Cast is { } into &&
                into(new Ray(from + (looking * Clearance), looking)) is { } met)
            {
                reach = Math.Max(1f, met.Distance + Clearance);
            }

            _reach[i] = reach;

            Stand(beam, from, _facing[i], new Vector3(BeamWidth, BeamWidth, reach / BeamLength));

            // <b>A beam is its own light.</b> Nothing in the room lights it and nothing
            // should be allowed to dim it: with rays on, the tower is a dark room with five
            // red lines across it, and shading those lines by the room they cross leaves
            // them the colour of everything else. Self-lit is how the engine already draws
            // a bulb — the texture untouched, above white where the display allows it —
            // and a laser is more of a light source than a bulb is.
            //
            // Here rather than where they are switched on, so that a player changing the
            // ray-tracing setting with the beams already up sees the change.
            World.SelfLit(beam, Lit && Tracing != RayTracingQuality.None);
        }
    }

    /// <summary>Switches the beams on, or off again.</summary>
    private void Toggle()
    {
        Lit = !Lit;
        _moved = true;

        foreach (PlacedModel? beam in _beams)
        {
            if (beam is null)
            {
                continue;
            }

            World.Show(beam, Lit);
        }

        World.Play(Lit ? "CS2LasersOn" : "CS2LasersOff");
        Settle();
    }

    /// <summary>
    /// The beams, drawn as light rather than as red plastic.
    /// </summary>
    /// <param name="eye">Where the camera is.</param>
    /// <returns>A run of additive sprites along each lit beam, farthest first.</returns>
    public override IReadOnlyList<Particle> Particles(Vector3 eye)
    {
        if (!Lit || Tracing == RayTracingQuality.None)
        {
            return [];
        }

        var glow = new List<Particle>();

        for (int i = 0; i < Count; i++)
        {
            if (_beams[i] is not { Visible: true })
            {
                continue;
            }

            Vector3 looking = Direction(_facing[i]);
            Vector3 from = _home[i] + (looking * (HeadRadius - BeamRadius));
            from.Y = Centre.Y;

            float reach = _reach[i];

            // One sprite every few units, which at this size is close enough that the discs
            // overlap into a continuous cord rather than reading as beads.
            int along = Math.Max(2, (int)(reach / GlowSpacing));

            for (int step = 0; step <= along; step++)
            {
                float where = (float)step / along;
                Vector3 at = from + (looking * reach * where);

                // Brightest where it leaves the head and dimmest where it lands, which is
                // both what a scattering beam does and what stops five of them piling up
                // into a white blaze in the middle of the room.
                float fade = 1f - (0.45f * where);

                glow.Add(new Particle(
                    at,
                    GlowRadius,
                    new Vector4(GlowColour * fade, 1f),

                    // No spin: a disc has none to speak of, and giving each one a different
                    // angle would make the cord twinkle as the camera moved.
                    0f,

                    // Additive. A beam of light adds to whatever is behind it and hides
                    // nothing, which is the difference between light and a red rod.
                    1f));
            }
        }

        // Farthest first, which is what the pass expects. Additive sprites do not need it
        // for themselves — adding is order-independent — but they share the buffer with the
        // room's smoke, which does.
        glow.Sort((a, b) =>
            Vector3.DistanceSquared(b.Position, eye)
                .CompareTo(Vector3.DistanceSquared(a.Position, eye)));

        return glow;
    }

    /// <summary>
    /// How far apart the glow sprites are along a beam.
    /// </summary>
    private const float GlowSpacing = 1.5f;

    /// <summary>And how wide each is.</summary>
    private const float GlowRadius = 2f;

    /// <summary>What colour a beam scatters.</summary>
    private static readonly Vector3 GlowColour = new(0.6f, 0.025f, 0.012f);

    /// <summary>
    /// The light the beams throw on the room.
    /// </summary>
    public override IReadOnlyList<AuthoredLight> Lights
    {
        get
        {
            _moved = false;

            // With no rays the bake lights the room and the rig reaches only the models
            // standing in it — see Game.FlameLighting — so beam lights would light Grace
            // and nothing she is standing on. The whole of this look is one thing or
            // nothing, and with rays off the beams are the model the game shipped.
            if (!Lit || Tracing == RayTracingQuality.None)
            {
                return [];
            }

            var rig = new List<AuthoredLight>(Count * LightsPerBeam);

            for (int i = 0; i < Count; i++)
            {
                if (_beams[i] is not { Visible: true })
                {
                    continue;
                }

                Vector3 looking = Direction(_facing[i]);
                Vector3 from = _home[i] + (looking * (HeadRadius - BeamRadius));
                from.Y = Centre.Y;

                for (int step = 0; step < LightsPerBeam; step++)
                {
                    // Spaced along the part of the beam that exists, and inset half a step
                    // at each end so the first light is not inside the head it comes out of.
                    float where = (step + 0.5f) / LightsPerBeam;

                    rig.Add(new AuthoredLight(
                        string.Create(CultureInfo.InvariantCulture, $"laser:{i + 1}:{step}"),
                        AuthoredLightKind.Point,
                        from + (looking * _reach[i] * where),
                        -Vector3.UnitY,
                        Beamlight,

                        // Cone angles a point light has no use for. The falloff has to start
                        // inside the reach or the light ends at a hard circle on the floor.
                        0f,
                        0f,
                        LightReach * 0.1f,
                        LightReach,
                        UsesAttenuation: true,
                        CastsShadows: false,
                        LightIntensity,

                        // The emitter is the beam, which is a few units across.
                        2f));
                }
            }

            return rig;
        }
    }

    /// <inheritdoc/>
    public override bool LightsMoved => _moved;

    /// <summary>Whether anything has happened that the rig would have to be laid again for.</summary>
    private bool _moved;

    /// <summary>How long since the rig was last laid while something was turning.</summary>
    private double _since;

    /// <summary>How often a turning head's lights are moved.</summary>
    private const double RelightEvery = 0.2;

    /// <summary>How many lights stand along each beam.</summary>
    private const int LightsPerBeam = 8;

    /// <summary>How far one of them reaches.</summary>
    private const float LightReach = 100f;

    /// <summary>And how bright.</summary>
    private const float LightIntensity = 0.9f;

    /// <summary>What colour they throw.</summary>
    private static readonly Vector3 Beamlight = new(1f, 0.05f, 0.03f);

    /// <summary>Starts a head turning, if it has anywhere to turn to.</summary>
    private void Swing(int head, int direction)
    {
        if (Next(head, direction) is not { } wanted)
        {
            return;
        }

        _turned[head] = wanted;
        _swinging[head] = -TurnDelay;

        Story.SetVariable(
            string.Create(CultureInfo.InvariantCulture, $"Cs2Head{head + 1}"), wanted);

        // Grace's hands, which are a different clip for every angle she can leave a head at.
        World.Play(Hands(direction, wanted));
    }

    /// <summary>Where a turn would leave a head, or null when it is already at the end.</summary>
    private int? Next(int head, int direction) =>
        _turned[head] + direction is >= 0 and <= 4 and { } wanted ? wanted : null;

    /// <summary>Which clip shows Grace putting a head where it is going.</summary>
    private static string Hands(int direction, int wanted) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"GraCs2TrnHead{(direction > 0 ? 'L' : 'R')}{wanted}");

    /// <summary>Reads <c>turnL3</c> as "head three, to the left".</summary>
    /// <returns>The head's index and which way, or -1 for a word that is not a turn.</returns>
    private static (int Head, int Direction) Turn(string asked)
    {
        if (asked.Length != 6 ||
            !asked.StartsWith("turn", StringComparison.OrdinalIgnoreCase) ||
            !int.TryParse(
                asked[5..], CultureInfo.InvariantCulture, out int head) ||
            head is < 1 or > Count)
        {
            return (-1, 0);
        }

        return char.ToUpperInvariant(asked[4]) switch
        {
            'L' => (head - 1, 1),
            'R' => (head - 1, -1),
            _ => (-1, 0),
        };
    }

    /// <summary>One of a head's five angles, in radians.</summary>
    private static float Radians(int head, int turn) =>
        Angles[head, turn] * MathF.PI / 180f;

    /// <summary>Which way a heading looks, on the ground plane.</summary>
    private static Vector3 Direction(float heading) =>
        new(MathF.Sin(heading), 0, MathF.Cos(heading));
}
