using System.Globalization;
using System.Numerics;
using GK3Reborn.Game.Interaction;
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
            World.Show(prop, tile.Doing is Phase.Glinting or Phase.Glowing);
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
                Sleep(index, SleepSeconds);

                // And if he is still standing on it, there is nothing under him any more.
                if (!_jumping && _standing == index)
                {
                    Fall(inTheAir: false);
                }

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

            World.Place(Story.Ego, landing, 0f);

            if (index >= 0 && index < Count)
            {
                Glow(index);
            }

            // Straight ahead again: every one of these clips is authored from Gabriel
            // facing down the bridge, and a few jumps of drift makes the next one miss.
            double standing = World.Play("GABTE5STAND");

            Then(standing, () =>
            {
                _jumping = false;
                World.Place(Story.Ego, landing, 0f);

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
    /// Three kinds of jump exist and no more: straight along, along a diagonal, and a
    /// knight's move. Anything else is a jump he cannot make.
    /// </remarks>
    private static string? Leap(int across, int along)
    {
        int sideways = Math.Abs(across);
        int forward = Math.Abs(along);

        if (sideways == 0 && forward > 0)
        {
            return forward > 1 ? "GABTE5JUMP02SQ" : "GABTE5JUMP01SQ";
        }

        if (sideways == forward && sideways > 0)
        {
            return "GABTE5JUMP01SQ";
        }

        if (sideways > 0 && forward > 0 && sideways + forward == 3)
        {
            return "GABTE5JUMP26KNIGHT";
        }

        return null;
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

            for (int i = 0; i < Count; i++)
            {
                _tiles[i].Doing = Phase.Out;
            }
        });
    }

    /// <summary>Runs one of the room's own scripts.</summary>
    private void Call(string function) => Api.Invoke(
        "CallSheep",
        [SheepValue.FromString("TE5"), SheepValue.FromString(function)]);
}
