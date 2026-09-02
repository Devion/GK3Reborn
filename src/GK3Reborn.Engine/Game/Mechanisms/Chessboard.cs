using System.Globalization;
using GK3Reborn.Game.Interaction;
using GK3Reborn.Sheep;

namespace GK3Reborn.Game.Mechanisms;

/// <summary>
/// TE1: the giant chessboard, and the trapdoors under it.
/// </summary>
/// <remarks>
/// <para>
/// Gabriel has to cross an eight-by-eight board a knight's move at a time, landing on all
/// sixteen sword tiles and never on the same tile twice. Landing on a tile he has already
/// used, or on one of the twelve that are traps from the start, opens it and drops him.
/// </para>
/// <para>
/// <b>The scripts jump; this decides.</b> Clicking a tile does that tile's <c>JUMP</c>
/// action, and which of three scripts runs is chosen by the case
/// <c>Te1MoveType == 1</c> — so the answer to "is that a legal move" has to be written
/// down <em>before</em> the click, from whatever the pointer is over. That is what
/// <see cref="Pointing"/> does, and it is why this is the one mechanism that watches the
/// mouse.
/// </para>
/// <para>
/// <b>A legal move is arithmetic.</b> Both differences greater than zero and summing to
/// three is exactly the set of knight's moves; off the board, the only legal move is onto
/// the first row. The turn Gabriel makes before jumping is <em>not</em> arithmetic: the
/// scripts choose an animation from a code number laid out like a numeric keypad centred
/// on 12, and the table below is that keypad.
/// </para>
/// <para>
/// Adapted from G-Engine's <c>Chessboard</c> under GPL-3, attributed in NOTICE.
/// </para>
/// </remarks>
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
        $"{_found} of {Side * Side} tiles found, {Swords} of them swords";

    /// <summary>How many of the sixty-four tiles the room actually has.</summary>
    private int _found;

    /// <inheritdoc/>
    /// <remarks>
    /// The board is not set up here: the room's own <c>SCENE, ENTER</c> calls
    /// <c>Restart$</c>, which calls <c>clearTiles</c>, and the reference says the same. All
    /// this does is count what it has to work with, because a board whose tiles cannot be
    /// found is a puzzle that opens no trapdoors and says nothing about it.
    /// </remarks>
    public override void Begin()
    {
        _found = 0;

        for (int row = 0; row < Side; row++)
        {
            for (int column = 0; column < Side; column++)
            {
                if (World.MiddleOf(TileAt(row, column)) is not null)
                {
                    _found++;
                }
            }
        }
    }

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
            // do — the room is put back by "reset" a moment later — and neither can this.
            case "FELL":
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
    }

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
    /// <remarks>
    /// An ordinary tile falls away behind him — a second late, or it goes while he is still
    /// on it. A sword tile stays: they are the ones he is collecting.
    /// </remarks>
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
    /// <remarks>
    /// <c>Te1TileState</c> is the landing count and is what the scripts read to decide
    /// whether this was a death. <c>AllSwords</c> says the sixteenth sword is out; whether
    /// that <em>finishes</em> the puzzle is the scripts' business, because he also has to
    /// end on the right one.
    /// </remarks>
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
    /// <remarks>
    /// The jump animations drift, and after a few of them he is visibly off the middle of
    /// a square. The original's own answer, called by its scripts after every jump.
    /// </remarks>
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
        // change under an action that has already been decided on.
        if (busy)
        {
            return;
        }

        _under = under?.Name ?? string.Empty;

        (int row, int column) = Square(_under);

        if (row < 0)
        {
            return;
        }

        Story.SetVariable("Te1TileRow", row);
        Story.SetVariable("Te1TileColumn", column);

        int here = Row;
        int there = Column;

        // Off the board, the only move is onto the first row.
        if (here < 0)
        {
            Story.SetVariable("Te1MoveType", row == 0 ? Legal : Illegal);

            return;
        }

        int down = Math.Abs(here - row);
        int across = Math.Abs(there - column);

        // A knight's move, and nothing else: both differences non-zero and summing to
        // three is exactly the eight of them.
        Story.SetVariable(
            "Te1MoveType", down > 0 && across > 0 && down + across == 3 ? Legal : Illegal);

        Story.SetVariable("Te1MoveCode", Code(row - here, column - there));
    }

    /// <summary>What <c>Te1MoveType</c> means.</summary>
    private const int Legal = 1;

    /// <summary>And its opposite.</summary>
    private const int Illegal = 2;

    /// <inheritdoc/>
    /// <remarks>
    /// <b>The board is not somewhere to walk.</b> While Gabriel is on it, a click anywhere
    /// on its floor has to be a jump or nothing — letting the ordinary walk have it sends
    /// him strolling across tiles that open under him. The one click that means something
    /// is on the surrounding floor, which is him trying to get off the board again.
    /// </remarks>
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
    /// <remarks>
    /// Sixteen of them, eight white and eight black, laid on two broken diagonals. There is
    /// no formula: this is the board as the artists painted it.
    /// </remarks>
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
    /// <remarks>
    /// <b>A numeric keypad centred on 12.</b> The scripts pick a turn animation from this
    /// number, and the numbering is a five-by-five grid of where the tile is relative to
    /// Gabriel, read left to right and back to front: 12 is standing still, 17 is one step
    /// forward, 22 is two, 13 is one to the right. The reference works the same twenty-four
    /// codes out through three pages of branches; they are two lines of arithmetic once the
    /// grid is seen, and every one of the twenty-four agrees.
    /// </remarks>
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
