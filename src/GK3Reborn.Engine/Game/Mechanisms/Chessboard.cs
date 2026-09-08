using System.Globalization;
using System.Numerics;
using GK3Reborn.Game.Interaction;
using GK3Reborn.Rendering;
using GK3Reborn.Sheep;

namespace GK3Reborn.Game.Mechanisms;

/// <summary>
/// TE1: the giant chessboard, and the trapdoors under it.
/// </summary>
public sealed class Chessboard : SceneMechanism
{
    /// <summary>How many rows and columns there are.</summary>
    private const int Side = 8;

    /// <summary>How many sword tiles have to be landed on to finish.</summary>
    private const int Swords = 16;

    /// <summary>How many times each tile has been landed on.</summary>
    private readonly int[,] _landed = new int[Side, Side];

    /// <summary>How many sword tiles have been put out.</summary>
    private int _taken;

    /// <summary>What the pointer was over when it was last asked.</summary>
    private string _under = string.Empty;

    /// <summary>The middle of each tile, measured off the room once.</summary>
    private readonly Vector3?[,] _middles = new Vector3?[Side, Side];

    /// <summary>How far apart the tiles are, which is what everything drawn is sized from.</summary>
    private float _pitch;

    /// <summary>How long the room has been running, for the glow's beat.</summary>
    private double _clock;

    /// <summary>Whether the sword tiles are lit, which is whether the puzzle is armed.</summary>
    private bool _lit;

    /// <summary>The legal tile under the pointer, or −1 for none worth offering.</summary>
    private (int Row, int Column) _target = None;

    /// <summary>
    /// Whether Gabriel is lying in a fall he was not killed by.
    /// </summary>
    private bool _fell;

    /// <summary>Creates the mechanism.</summary>
    /// <param name="world">The room.</param>
    /// <param name="api">The script host.</param>
    public Chessboard(SceneUpdate world, Gk3SheepApi api)
        : base(world, api)
    {
    }

    /// <inheritdoc/>
    public override string Name => "Chess";

    /// <inheritdoc/>
    public override string Report() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{_found} of {Side * Side} tiles found, {Swords} of them swords, "
            + $"{_pitch:0.#} units apart");

    /// <summary>How many of the sixty-four tiles the room actually has.</summary>
    private int _found;

    /// <inheritdoc/>
    public override void Begin()
    {
        _found = 0;

        for (int row = 0; row < Side; row++)
        {
            for (int column = 0; column < Side; column++)
            {
                // Kept rather than counted. Everything the board draws over itself is
                // placed from these, and they are asked for here because a tile that has
                // been dropped is hidden geometry with nothing left to measure.
                _middles[row, column] = World.MiddleOf(TileAt(row, column));

                if (_middles[row, column] is not null)
                {
                    _found++;
                }
            }
        }

        _pitch = Pitch();
    }

    /// <summary>
    /// How far apart the tiles are.
    /// </summary>
    /// <returns>The average gap between neighbours along a row, or nought when unknown.</returns>
    private float Pitch()
    {
        float total = 0f;
        int pairs = 0;

        for (int row = 0; row < Side; row++)
        {
            for (int column = 0; column + 1 < Side; column++)
            {
                if (_middles[row, column] is { } left && _middles[row, column + 1] is { } right)
                {
                    total += Vector3.Distance(left, right);
                    pairs++;
                }
            }
        }

        return pairs > 0 ? total / pairs : 0f;
    }

    /// <inheritdoc/>
    public override void Advance(double seconds) => _clock += seconds;

    /// <inheritdoc/>
    public override bool Perform(string asked)
    {
        ArgumentNullException.ThrowIfNull(asked);

        switch (asked.ToUpperInvariant())
        {
            case "CLEARTILES":
                Restart(lit: false);
                return true;

            case "RESET":
                Restart(lit: true);
                return true;

            case "TAKEOFF":
                Takeoff();
                return true;

            case "LANDED":
                Landed();
                return true;

            case "HIDECURRENTTILE":
            case "BADLAND":
                Drop(Row, Column);
                return true;

            case "CENTERME":
                Centre();
                return true;

            // Called when Gabriel has fallen. The reference could find nothing for it to
            // do — the room is put back by "reset" a moment later — and nor is there
            // anything to do here, except for a player who was not killed by it: see
            // Stand. Remembered rather than acted on, because he is still lying under the
            // floor and the restart that puts him back has not run yet.
            case "FELL":
                _fell = Deathless;
                return true;

            default:
                return false;
        }
    }

    /// <summary>Which row Gabriel is on, or −1 while he is off the board.</summary>
    private int Row => Story.GetVariable("Te1GabeRow");

    /// <summary>And which column.</summary>
    private int Column => Story.GetVariable("Te1GabeColumn");

    /// <summary>
    /// Puts the board back to the start.
    /// </summary>
    /// <param name="lit">Whether the sword tiles glow, which they do on a fresh attempt.</param>
    private void Restart(bool lit)
    {
        // Which the drawing below reads as "the puzzle is armed". clearTiles puts the
        // swords out and reset lights them, and the room is only jumpable between the two.
        _lit = lit;

        Story.SetVariable("Te1GabeRow", -1);
        Story.SetVariable("Te1GabeColumn", -1);
        Story.SetVariable("Te1MoveType", 1);
        Story.SetVariable("Te1TileState", 0);
        Story.SetVariable("Te1TileRow", 0);
        Story.SetVariable("Te1TileColumn", 0);
        Story.SetVariable("Te1SwordCount", 0);
        Story.ClearFlag("AllSwords");

        _taken = 0;

        for (int row = 0; row < Side; row++)
        {
            for (int column = 0; column < Side; column++)
            {
                _landed[row, column] = 0;
                World.ShowObject(TileAt(row, column), true);
                Glow(row, column, lit);
            }
        }

        // Twelve tiles are traps from the start: four in front of each pair of doors and
        // the four in the middle. Given a landing count that already counts as a repeat,
        // which is how the scripts learn they are fatal without a table of their own.
        foreach ((int row, int column) in Deadly)
        {
            _landed[row, column] = 2;
        }

        // And the door at the far end, which a finished attempt opened, is shut again: its
        // opening frame is the shut one, which is what makes sampling it a way of closing it.
        World.Pose("Te1GoDoor", ["te1doorcirclemod"], atEnd: false);

        // This is the restart the death screen would have run, and by now it has put
        // Gabriel back beside Mosely and Mesmi. What it has not done is take him out of
        // the fall.
        if (_fell)
        {
            _fell = false;
            Stand();
        }
    }

    /// <summary>
    /// Puts Gabriel back on his feet after a fall he was not killed by.
    /// </summary>
    private void Stand() => World.Next(() =>
    {
        double settling = World.Play(Standing);

        // And breathing again afterwards. A clip the story plays on a character stops
        // whatever they were doing on their own — see SceneUpdate.Play — so without this
        // the rescue would leave him as still as the fall did.
        Then(settling, () => World.StartFidget(Story.Ego, FidgetKind.Idle));
    });

    /// <summary>The room's own clip of Gabriel standing on the board.</summary>
    private const string Standing = "GabTe1Stand";

    /// <summary>The twelve tiles that kill on the first landing.</summary>
    private static readonly (int Row, int Column)[] Deadly =
    [
        (1, 3), (1, 4), (6, 3), (6, 4),
        (3, 1), (4, 1), (3, 6), (4, 6),
        (3, 3), (4, 3), (3, 4), (4, 4),
    ];

    /// <summary>
    /// Gabriel is about to leave the tile he is standing on.
    /// </summary>
    private void Takeoff()
    {
        int row = Row;
        int column = Column;

        if (Sword(row, column) is not null)
        {
            return;
        }

        Then(1.0, () => Drop(row, column));
    }

    /// <summary>
    /// Gabriel has landed.
    /// </summary>
    private void Landed()
    {
        int row = Row;
        int column = Column;

        if (!On(row, column))
        {
            return;
        }

        _landed[row, column]++;

        // What the scripts read to decide whether this was a death. Held at one where the
        // player has asked not to be killed: the board's deaths are a number this writes
        // rather than a script's Die$, so the ordinary armour never sees them, and holding
        // the count is the whole of stopping them.
        Story.SetVariable(
            "Te1TileState", Deathless ? 1 : _landed[row, column]);

        if (_landed[row, column] == 1 && Glow(row, column, lit: false))
        {
            Sound("TE1SWORDOFF.WAV");

            _taken++;
            Story.SetVariable("Te1SwordCount", _taken);

            if (_taken == Swords)
            {
                Story.SetFlag("AllSwords");
            }
        }
        else if (_landed[row, column] > 1)
        {
            // A tile he has used before. It opens under him while the fall plays.
            Drop(row, column);
        }
    }

    /// <summary>
    /// Stands Gabriel in the middle of the tile he is on.
    /// </summary>
    private void Centre()
    {
        // The middle of the tile, which is part of the room's geometry rather than a prop —
        // so there is no placement to ask and the triangles are the answer. Failing that,
        // the heading alone is still worth setting: every jump animation is authored from
        // Gabriel facing straight down the board.
        if ((World.MiddleOf(TileAt(Row, Column)) ?? World.Where(Story.Ego)) is { } spot)
        {
            World.Place(Story.Ego, spot, 0f);
        }
    }

    /// <summary>Opens a tile's trapdoor.</summary>
    private void Drop(int row, int column)
    {
        if (!On(row, column) || Deathless)
        {
            return;
        }

        World.ShowObject(TileAt(row, column), false);
        Sound("TE1TRAPDOOROPEN.WAV");
    }

    /// <inheritdoc/>
    public override void Pointing(ScenePick? under, bool busy)
    {
        // Not while the player is choosing a verb or watching a jump: the answer would
        // change under an action that has already been decided on. Nothing is offered
        // either — a border lighting up in the middle of a jump invites a second click on
        // a move that has already been made.
        if (busy)
        {
            _target = None;

            return;
        }

        _under = under?.Name ?? string.Empty;

        (int row, int column) = Square(_under);

        if (row < 0)
        {
            _target = None;

            return;
        }

        Story.SetVariable("Te1TileRow", row);
        Story.SetVariable("Te1TileColumn", column);

        int here = Row;
        int there = Column;

        // Off the board, the only move is onto the first row.
        if (here < 0)
        {
            Offer(row, column, row == 0);

            return;
        }

        int down = Math.Abs(here - row);
        int across = Math.Abs(there - column);

        // A knight's move, and nothing else: both differences non-zero and summing to
        // three is exactly the eight of them.
        Offer(row, column, down > 0 && across > 0 && down + across == 3);

        Story.SetVariable("Te1MoveCode", Code(row - here, column - there));
    }

    /// <summary>
    /// Writes down whether the tile under the pointer may be jumped to, and offers it.
    /// </summary>
    /// <param name="row">The tile's row.</param>
    /// <param name="column">And its column.</param>
    /// <param name="legal">Whether the move is one.</param>
    private void Offer(int row, int column, bool legal)
    {
        Story.SetVariable("Te1MoveType", legal ? Legal : Illegal);

        _target = legal && _lit ? (row, column) : None;
    }

    /// <summary>No tile: what <see cref="_target"/> holds when nothing is offered.</summary>
    private static readonly (int Row, int Column) None = (-1, -1);

    /// <summary>
    /// What the board says about itself that its textures cannot.
    /// </summary>
    /// <param name="eye">Where the camera is. Unused: all of this is light.</param>
    /// <returns>The sprites, in any order.</returns>
    public override IReadOnlyList<Particle> Particles(Vector3 eye)
    {
        // A board whose tiles could not be found is a board with nothing to measure and
        // nowhere to put a sprite. Begin says so in the log; this simply draws nothing.
        if (_pitch <= 0f)
        {
            return [];
        }

        var drawn = new List<Particle>((Swords * (Shape.Length + RisePoints)) + BorderPoints);

        if (_lit)
        {
            for (int row = 0; row < Side; row++)
            {
                for (int column = 0; column < Side; column++)
                {
                    // Exactly the tiles Landed puts out, and for the same reason: a sword
                    // is spent on the first landing, whether or not the attempt survives it.
                    if (_landed[row, column] == 0 &&
                        Sword(row, column) is not null &&
                        _middles[row, column] is { } where)
                    {
                        Shine(drawn, where, row, column);
                    }
                }
            }
        }

        if (_target.Row >= 0 && _middles[_target.Row, _target.Column] is { } tile)
        {
            Border(drawn, tile);
        }

        return drawn;
    }

    /// <summary>
    /// The light coming off a sword that is still there.
    /// </summary>
    /// <param name="into">Where the sprites go.</param>
    /// <param name="where">The middle of the tile.</param>
    /// <param name="row">Which tile, so that no two breathe together.</param>
    /// <param name="column">And the other half of it.</param>
    private void Shine(List<Particle> into, Vector3 where, int row, int column)
    {
        // Its own beat. Sixteen swords breathing in unison read as one mechanism running
        // under the floor rather than as sixteen things still to be picked up, and the two
        // multipliers do not divide each other, so no two of the sixteen share a phase.
        float own = (row * 1.7f) + (column * 0.9f);
        float breath = 0.68f + (0.32f * MathF.Sin((float)(_clock * GlowRate) + own));

        // The sword, lying in the tile where it is painted.
        for (int point = 0; point < Shape.Length; point++)
        {
            (float along, float across, float wide) = Shape[point];

            into.Add(Spark(where, along, across, wide, 0f, breath, own));
        }

        // And what comes off it. Fewer sprites than the sword has, spread up through the
        // air over it: what is wanted is a suggestion that the shape below is lit, not a
        // second sword drawn above the first.
        for (int point = 0; point < RisePoints; point++)
        {
            // Every other one of the sword's own sprites, so the thing rising has the
            // sword's outline rather than the blade's line alone — the crossguard is a
            // fifth of the shape and all of what makes it a sword.
            int from = point * Shape.Length / RisePoints;
            (float along, float across, float wide) = Shape[from];

            // Nothing sits at the same height as its neighbour, and the whole set creeps
            // upwards and starts again, so what the eye follows is movement off the tile
            // rather than a row of lamps at a fixed height.
            float up = Fraction(((point + 0.5f) / RisePoints) + (float)(_clock * RiseRate) + own);

            into.Add(Spark(
                where,
                along,
                across + (Wander * MathF.Sin((float)(_clock * WanderRate) + (point * 1.7f) + own)),

                // Wider as it goes, because light in air spreads and because the sword's
                // own edges must stay the sharpest thing here.
                wide * (1f + (Spreading * up)),
                Rising * up,

                // Under the sword's own brightness. The rise is the thing being noticed
                // out of the corner of an eye and the blade is the thing being counted, and
                // a haze as bright as what it comes off reads as a second object.
                breath * RiseShare,
                own));
        }
    }

    /// <summary>The part of a number after the point, for a value that wraps.</summary>
    private static float Fraction(float value) => value - MathF.Floor(value);

    /// <summary>One sprite of a lit sword.</summary>
    /// <param name="middle">The middle of the tile it is painted on.</param>
    /// <param name="along">How far down the sword it sits, as a fraction of the pitch.</param>
    /// <param name="across">And how far to the side of it.</param>
    /// <param name="wide">How wide it is, likewise.</param>
    /// <param name="up">How far it has risen off the tile, likewise. Nought lies in it.</param>
    /// <param name="breath">How far through its tile's beat the whole sword is.</param>
    /// <param name="own">That tile's phase, so no two swords breathe together.</param>
    private Particle Spark(
        Vector3 middle,
        float along,
        float across,
        float wide,
        float up,
        float breath,
        float own)
    {
        // How far off the sword this one has got, from nought in it to one at the top.
        float off = up / Rising;

        // Thinner the further it has got, and gone by the top. Squared, so most of what
        // there is to see is in the first inch of it and the air above that is a
        // suggestion — which is the difference between a lit inlay and a lamp.
        float thinning = 1f - off;
        thinning *= thinning;

        return new Particle(
            middle + new Vector3(across * _pitch, _pitch * (Lift + up), along * _pitch),
            _pitch * wide,

            // The sword's own red in the tile, paling as it leaves. Not a second colour
            // arriving: red light thins towards its own white, and the sword's edge has to
            // stay the most saturated thing here or the haze becomes the subject.
            new Vector4(
                Vector3.Lerp(Steel, Hot, off * off),
                GlowAlpha * breath * thinning),
            own + along,

            // Wholly additive: a plain soft disc that adds its light and hides nothing.
            1f);
    }

    /// <summary>
    /// The border round the tile the pointer is offering.
    /// </summary>
    /// <param name="into">Where the sprites go.</param>
    /// <param name="where">The middle of the tile.</param>
    private void Border(List<Particle> into, Vector3 where)
    {
        // A little inside the tile: an outline drawn at the full pitch lands on the seam
        // between two squares and reads as either of them.
        float edge = _pitch * BorderEdge;

        for (int point = 0; point < BorderPoints; point++)
        {
            // Once round the square, four sides walked in order, so the arc below travels
            // rather than jumping between them.
            float round = (float)point / BorderPoints;
            float along = round * 4f;
            int side = Math.Min((int)along, 3);
            float across = ((along - side) * 2f * edge) - edge;

            (float X, float Z) at = side switch
            {
                0 => (across, -edge),
                1 => (edge, across),
                2 => (-across, edge),
                _ => (-edge, -across),
            };

            // sin is positive for half a lap and the power narrows that to a fraction of
            // one, so most of the border is a steady line and one part of it is catching
            // something.
            float sweep = MathF.Sin((round - (float)(_clock * BorderRate)) * MathF.Tau);
            float travelling = MathF.Pow(MathF.Max(sweep, 0f), BorderFocus);

            into.Add(new Particle(
                where + new Vector3(at.X, _pitch * Lift, at.Z),
                _pitch * BorderWide * (0.85f + (0.35f * travelling)),
                new Vector4(
                    Vector3.Lerp(Dull, Kindled, travelling),
                    BorderAlpha * (0.5f + (0.9f * travelling))),
                round * MathF.Tau,
                1f));
        }
    }

    /// <summary>
    /// The sword, as sprites: where each sits and how wide it is, in fractions of a tile.
    /// </summary>
    private static readonly (float Along, float Across, float Wide)[] Shape =
    [
        (+0.434f, +0.000f, 0.050f),
        (+0.375f, +0.000f, 0.065f),
        (+0.316f, +0.000f, 0.075f),
        (+0.257f, +0.000f, 0.075f),
        (+0.199f, +0.000f, 0.075f),
        (+0.140f, +0.000f, 0.075f),
        (+0.081f, +0.000f, 0.075f),
        (+0.023f, +0.000f, 0.075f),
        (-0.036f, +0.000f, 0.075f),
        (-0.095f, +0.000f, 0.075f),
        (-0.154f, +0.000f, 0.075f),
        (-0.223f, -0.054f, 0.055f),
        (-0.223f, +0.000f, 0.055f),
        (-0.223f, +0.054f, 0.055f),
        (-0.330f, +0.000f, 0.075f),
        (-0.388f, +0.000f, 0.075f),
        (-0.447f, +0.000f, 0.050f),
    ];

    /// <summary>How many sprites rise off each sword.</summary>
    private const int RisePoints = 11;

    /// <summary>How far the glow stands off the tile, as a fraction of the pitch.</summary>
    private const float Rising = 0.20f;

    /// <summary>How fast the whole set creeps upward, in heights a second.</summary>
    private const float RiseRate = 0.16f;

    /// <summary>How much of the sword's brightness the rise off it gets.</summary>
    private const float RiseShare = 0.7f;

    /// <summary>How much wider a sprite is by the time it reaches the top.</summary>
    private const float Spreading = 0.45f;

    /// <summary>How far a rising sprite wanders to the side, as a fraction of the pitch.</summary>
    private const float Wander = 0.018f;

    /// <summary>And how fast it wanders.</summary>
    private const float WanderRate = 0.61f;

    /// <summary>How far above the floor the sword itself lies, as a fraction of the pitch.</summary>
    private const float Lift = 0.02f;

    /// <summary>How bright a lit sword is at its brightest.</summary>
    private const float GlowAlpha = 0.42f;

    /// <summary>How fast a sword breathes, in radians a second.</summary>
    private const float GlowRate = 1.15f;

    /// <summary>How far from the middle the border is drawn.</summary>
    private const float BorderEdge = 0.44f;

    /// <summary>How many points trace it.</summary>
    private const int BorderPoints = 36;

    /// <summary>How wide one of them is.</summary>
    private const float BorderWide = 0.09f;

    /// <summary>And how bright.</summary>
    private const float BorderAlpha = 0.44f;

    /// <summary>How fast the arc travels round, in laps a second.</summary>
    private const float BorderRate = 0.30f;

    /// <summary>How tight it is: higher is a shorter, brighter arc.</summary>
    private const float BorderFocus = 5f;

    /// <summary>The colour of a sword still to be taken.</summary>
    private static readonly Vector3 Steel = new(1.00f, 0.16f, 0.09f);

    /// <summary>And what it thins to on the way up.</summary>
    private static readonly Vector3 Hot = new(1.00f, 0.55f, 0.38f);

    /// <summary>The border at rest: an ember, well under the swords' own red.</summary>
    private static readonly Vector3 Dull = new(0.66f, 0.13f, 0.06f);

    /// <summary>And where the arc is passing over it, which is only a little hotter.</summary>
    private static readonly Vector3 Kindled = new(0.95f, 0.40f, 0.20f);

    /// <summary>What <c>Te1MoveType</c> means.</summary>
    private const int Legal = 1;

    /// <summary>And its opposite.</summary>
    private const int Illegal = 2;

    /// <inheritdoc/>
    public override bool TakesClick(ScenePick? under)
    {
        if (Row < 0)
        {
            return false;
        }

        string name = under?.Name ?? string.Empty;

        if (name.Equals(TileFloor, StringComparison.OrdinalIgnoreCase))
        {
            // Only from the first row is the jump back short enough. From anywhere else the
            // script says so, which is why the move type is set either way rather than the
            // click being refused.
            Story.SetVariable("Te1MoveType", Row == 0 ? Legal : Illegal);
            Jump();

            return true;
        }

        return Edges.Contains(name);
    }

    /// <inheritdoc/>
    public override bool TakesFloorClick() => Row >= 0;

    /// <summary>The floor around the board, which is where jumping off lands.</summary>
    private const string TileFloor = "te1tilefloor";

    /// <summary>And the rest of the floor the ordinary walk must not have.</summary>
    private static readonly HashSet<string> Edges =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "te1_hittestfloor", "te1flooredges", "te1_floorstand",
        };

    /// <summary>Runs the room's own jump action.</summary>
    private void Jump() => World.Next(() => Api.Invoke(
        "CallSheep",
        [
            SheepValue.FromString("te1"),
            SheepValue.FromString(Story.GetVariable("Te1MoveType") == Legal
                ? "JumpOff$"
                : "TooFar$"),
        ]));

    /// <summary>Plays one of the board's noises.</summary>
    private void Sound(string wave) =>
        Api.Invoke("PlaySound", [SheepValue.FromString(wave)]);

    /// <summary>Whether a row and column are on the board at all.</summary>
    private static bool On(int row, int column) =>
        row is >= 0 and < Side && column is >= 0 and < Side;

    /// <summary>What the room calls one of its tiles: <c>te1floora1</c> to <c>te1floorh8</c>.</summary>
    private static string TileAt(int row, int column) =>
        On(row, column)
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"te1floor{(char)('a' + column)}{(char)('1' + row)}")
            : string.Empty;

    /// <summary>Reads a tile's name back into a row and a column.</summary>
    private static (int Row, int Column) Square(string name) =>
        name.Length == 10 && name.StartsWith("te1floor", StringComparison.OrdinalIgnoreCase) &&
        char.ToLowerInvariant(name[8]) - 'a' is >= 0 and < Side and { } column &&
        name[9] - '1' is >= 0 and < Side and { } row
            ? (row, column)
            : (-1, -1);

    /// <summary>
    /// Whether a tile carries a sword, and which colour.
    /// </summary>
    private static bool? Sword(int row, int column)
    {
        if (!On(row, column))
        {
            return null;
        }

        return (row, column) switch
        {
            (0, 3) or (1, 2) or (2, 1) or (3, 0) or (4, 7) or (5, 6) or (6, 5) or (7, 4) => true,
            (0, 4) or (1, 5) or (2, 6) or (3, 7) or (4, 0) or (5, 1) or (6, 2) or (7, 3) => false,
            _ => null,
        };
    }

    /// <summary>Lights or puts out a sword tile.</summary>
    /// <returns>True when the tile is a sword tile at all.</returns>
    private bool Glow(int row, int column, bool lit)
    {
        if (Sword(row, column) is not { } white)
        {
            return false;
        }

        World.PaintObject(
            TileAt(row, column),
            // Without the extension: a texture is named the way an animation's [STEXTURES]
            // line names one, and the loader puts the .BMP on.
            (white, lit) switch
            {
                (true, true) => "TE1SWORDW_GLOW",
                (true, false) => "TE1SWORDW",
                (false, true) => "TE1SWORDB_GLOW",
                (false, false) => "TE1SWORDB",
            });

        return true;
    }

    /// <summary>
    /// Which way Gabriel turns before he jumps, as the scripts number it.
    /// </summary>
    /// <param name="down">How many rows away the tile is, signed.</param>
    /// <param name="across">How many columns, signed.</param>
    /// <returns>The code <c>Te1MoveCode</c> carries.</returns>
    private static int Code(int down, int across)
    {
        // Two steps in each direction is as far as the grid goes; every move on this board
        // is within it, and an illegal one further out still wants the turn that points at
        // it.
        down = Math.Clamp(down, -2, 2);
        across = Math.Clamp(across, -2, 2);

        // The one exception to the grid: a two-step diagonal is the same turn as a one-step
        // diagonal, so the four corners fold inwards. That is why 6, 8, 16 and 18 each
        // appear twice and 0, 4, 20 and 24 never appear at all.
        if (Math.Abs(down) == 2 && Math.Abs(across) == 2)
        {
            down /= 2;
            across /= 2;
        }

        return (5 * (2 + down)) + 2 + across;
    }
}
