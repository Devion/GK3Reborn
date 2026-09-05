using System.Globalization;
using System.Numerics;
using GK3Reborn.Content;
using GK3Reborn.Formats.Ini;
using GK3Reborn.Foundation.Diagnostics;
using GK3Reborn.Game.Interaction;
using GK3Reborn.Game.Navigation;
using GK3Reborn.Sheep;

namespace GK3Reborn.Game.Mechanisms;

/// <summary>
/// TE3: the blade, the turning floor, and the altar in the middle.
/// </summary>
/// <remarks>
/// <para>
/// Twenty-four platforms turn slowly round a shaft; a blade the height of the room swings
/// across it, into a slot on either side. Gabriel jumps out onto the ring and rides it,
/// stepping forward and back to stay out of the blade's way, until he can catch hold of it
/// as it passes and let go over the altar in the middle.
/// </para>
/// <para>
/// <b>The numbers are in the game, not in the code.</b> <c>PENDULUM.TXT</c> gives the
/// ring's period, how many platforms pass per swing, the blade's greatest angle and the
/// two angles between which letting go is survivable. The reference works from remembered
/// values and says it did not bother reading the file; this reads it, so the puzzle runs at
/// the speed its designers set.
/// </para>
/// <para>
/// <b>Everything about a platform's position is derived.</b> All twenty-four models sit at
/// the middle of the room and are <em>turned</em> into place, so asking one where it is
/// gives the middle of the room — which is why there is a formula here instead. The one
/// fixed fact it needs is that platform 24 faces the entryway when nothing has turned.
/// </para>
/// <para>
/// Adapted from G-Engine's <c>Pendulum</c> under GPL-3, attributed in NOTICE.
/// </para>
/// </remarks>
public sealed class Pendulum : SceneMechanism
{
    /// <summary>How many platforms make up the ring.</summary>
    private const int Platforms = 24;

    /// <summary>How much of the circle each takes.</summary>
    private const float PerPlatform = MathF.Tau / Platforms;

    /// <summary>How far out from the middle they are.</summary>
    private const float Radius = 800f;

    /// <summary>How far above the room's origin their top surface is.</summary>
    private const float Height = 25f;

    /// <summary>How long the blade's arm is, above the room.</summary>
    private const float Arm = 2000f;

    /// <summary>Where Gabriel stands before he jumps out, as the scene names it.</summary>
    private const string Entryway = "LOOKOUT";

    /// <summary>Where he lands if he gets it right.</summary>
    private const string Landing = "ALTAR_LAND";

    /// <summary>What Gabriel is doing.</summary>
    private enum Doing
    {
        /// <summary>In the doorway, not yet committed.</summary>
        Waiting,

        /// <summary>In the air between platforms.</summary>
        Jumping,

        /// <summary>Riding the ring.</summary>
        Riding,

        /// <summary>Hanging off the blade.</summary>
        Holding,

        /// <summary>On the altar, which is the end of it.</summary>
        Arrived,

        /// <summary>Being killed.</summary>
        Dying,
    }

    private readonly PlacedModel?[] _ring = new PlacedModel?[Platforms];

    private PlacedModel? _blade;
    private PlacedModel? _bladeWithHim;
    private PlacedModel? _altar;

    /// <summary>Where the blade hangs from, worked out from where it starts.</summary>
    private Vector3 _pivot;

    /// <summary>How far the ring has turned, in radians.</summary>
    private float _turned;

    /// <summary>How far through its swing the blade is, in seconds.</summary>
    private double _swung;

    /// <summary>Which platform Gabriel is on, or −1.</summary>
    private int _on = -1;

    /// <summary>What he is doing.</summary>
    private Doing _doing = Doing.Waiting;

    /// <summary>What the pointer is over.</summary>
    private string _under = string.Empty;

    /// <summary>Creates the mechanism.</summary>
    /// <param name="world">The room.</param>
    /// <param name="api">The script host.</param>
    public Pendulum(SceneUpdate world, Gk3SheepApi api)
        : base(world, api)
    {
    }

    /// <inheritdoc/>
    public override string Name => "Circle";

    /// <summary>Where the file is read from, when there is anything to read it out of.</summary>
    public GameArchives? Archives { get; init; }

    /// <summary>Seconds for the ring to go round once.</summary>
    private float _period = 50f;

    /// <summary>How many platforms pass while the blade swings out and back.</summary>
    private int _per = 6;

    /// <summary>The blade's greatest angle from vertical, in radians.</summary>
    private float _reach = 24.5f * MathF.PI / 180f;

    /// <summary>The angle inside which letting go is allowed at all.</summary>
    private float _allowed = 15f * MathF.PI / 180f;

    /// <summary>And inside which it is survivable.</summary>
    private float _safe = 3f * MathF.PI / 180f;

    /// <inheritdoc/>
    public override string Report() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{_ring.Count(p => p is not null)} of {Platforms} platforms, " +
            $"{_period:F0}s a turn, blade to {_reach * 180 / MathF.PI:F1}°");

    /// <inheritdoc/>
    public override bool Perform(string asked) => false;

    /// <inheritdoc/>
    public override void Begin()
    {
        Read();

        for (int i = 0; i < Platforms; i++)
        {
            _ring[i] = World.ModelNamed(
                string.Create(CultureInfo.InvariantCulture, $"te3_r{i + 1:00}"));
        }

        _blade = World.ModelNamed("te3_pendulum_center_code");
        _bladeWithHim = World.ModelNamed("te3__pendulum_gabe");
        _altar = World.ModelNamed("te3_hpaltar");

        // Where it hangs from. The blade is modelled at the bottom of its arc, so the pivot
        // is straight up from wherever the scene put it — there is nothing in the data that
        // says so, and no other way to find it.
        _pivot = (_blade?.Standing.Translation ?? Vector3.Zero) + (Vector3.UnitY * Arm);

        _turned = 0;
        _swung = 0;
        _on = -1;
        _doing = Doing.Waiting;
    }

    /// <summary>Reads the numbers the game ships for this room.</summary>
    private void Read()
    {
        if (Archives?.ReadText("PENDULUM.TXT") is not { } text)
        {
            return;
        }

        foreach (IniLine line in IniDocument.Parse(text, "PENDULUM.TXT").LinesOf(string.Empty))
        {
            if (line.Head is not { Key: { Length: > 0 } key, Value: { Length: > 0 } value })
            {
                continue;
            }

            float number = Number(value);

            switch (key.ToUpperInvariant())
            {
                case "CIRCLEPERIOD" when number > 0: _period = number; break;
                case "PENDULUMCYCLE" when number > 0: _per = (int)number; break;
                case "PENDULUMANGLE": _reach = Radians(number); break;
                case "STARTSAFE": _allowed = Radians(number); break;
                case "ENDSAFE": _safe = Radians(number); break;
                default: break;
            }
        }
    }

    private static float Radians(float degrees) => degrees * MathF.PI / 180f;

    /// <summary>A number off a line, with the file's own trailing comment thrown away.</summary>
    private static float Number(string value)
    {
        int comment = value.IndexOf("//", StringComparison.Ordinal);

        return float.TryParse(
            (comment >= 0 ? value[..comment] : value).AsSpan().Trim(),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out float number)
            ? number
            : 0;
    }

    /// <summary>How fast the ring turns, in radians a second.</summary>
    private float Turning => MathF.Tau / MathF.Max(_period, 0.01f);

    /// <summary>
    /// How long the blade takes to swing out and back.
    /// </summary>
    /// <remarks>
    /// Not a number anybody wrote down: the file says <em>six platforms pass per swing</em>,
    /// so the swing is six platforms' worth of the ring's own rotation. Tying the two
    /// together is what makes the puzzle readable — the blade always arrives on the same
    /// beat of the floor going round.
    /// </remarks>
    private double Cycle => PerPlatform / Turning * _per;

    /// <inheritdoc/>
    public override void Advance(double seconds)
    {
        _turned = Wrapped(_turned + (Turning * (float)seconds));
        _swung += seconds;

        if (_swung >= Cycle)
        {
            _swung -= Cycle;
        }

        Swing();
        Turn();
        Ride();
        Reach();
    }

    /// <summary>
    /// Catches the blade for him, where the player has asked not to have to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// After <see cref="Swing"/> rather than before it, so that a frame which is both the
    /// last of the reach and the first of the killing is still the killing: the assistance
    /// is a pair of hands, not a second kind of plot armour, and a player who has both on
    /// should see the same room either way round. In practice the reach runs out a platform
    /// before the blade arrives, so the two never meet.
    /// </para>
    /// <para>
    /// It is the same <see cref="Grab"/> the click performs — the score, the camera cut and
    /// the climb are all the room's, and none of them is skipped.
    /// </para>
    /// </remarks>
    private void Reach()
    {
        if (Story.CatchesPendulum && _doing == Doing.Riding && _danger != 0 && WithinReach())
        {
            Grab();
        }
    }

    /// <summary>Puts the blade where its clock says it is, and kills anybody under it.</summary>
    private void Swing()
    {
        float angle = Angle();

        // Which of the two models is the blade at the moment: the plain one, or the one
        // with Gabriel modelled hanging off it. Both cannot be up at once.
        bool holding = _doing == Doing.Holding;
        bool gone = _doing == Doing.Arrived;

        if (_blade is { } plain)
        {
            World.Show(plain, !holding && !gone);
        }

        if (_bladeWithHim is { } withHim)
        {
            World.Show(withHim, holding);
        }

        if ((holding ? _bladeWithHim : _blade) is { } swinging)
        {
            // It hangs from a pivot two thousand units up and turns about the room's Z, so
            // this is a rotation and the position that rotation implies — the model's own
            // origin is not at the pivot.
            Vector3 down = Vector3.Transform(
                -Vector3.UnitY, Matrix4x4.CreateRotationZ(angle));

            Put(
                swinging,
                Matrix4x4.CreateRotationZ(angle) *
                Matrix4x4.CreateTranslation(_pivot + (down * Arm)));
        }

        // Near the end of its arc the blade is in one of the two wall slots, which is both
        // when it can be caught and when it kills.
        if (MathF.Abs(_reach - MathF.Abs(angle)) >= DangerAngle)
        {
            _danger = 0;

            return;
        }

        _danger = angle > 0 ? 1 : -1;

        if (_doing != Doing.Riding)
        {
            return;
        }

        if (_on == (angle > 0 ? Slot(6) : Slot(-6)))
        {
            Killed(angle > 0, _swung > Cycle * 0.5);
        }
    }

    /// <summary>How near the end of its arc counts as being in a slot.</summary>
    private static readonly float DangerAngle = 8f * MathF.PI / 180f;

    /// <summary>Which slot the blade is in: 1 left, −1 right, 0 neither.</summary>
    private int _danger;

    /// <summary>Where the blade is now, as an angle off vertical.</summary>
    /// <remarks>
    /// Eased rather than linear, because a pendulum is: it hangs at the ends of its arc and
    /// is quickest through the bottom, and the whole puzzle is timing against that.
    /// </remarks>
    private float Angle()
    {
        double half = Cycle * 0.5;
        double through = _swung <= half ? _swung / half : (_swung - half) / half;
        float from = _swung <= half ? _reach : -_reach;
        float to = -from;

        return from + ((to - from) * Eased((float)through));
    }

    /// <summary>Slow at both ends, quick through the middle.</summary>
    private static float Eased(float through) =>
        through < 0.5f
            ? 4f * through * through * through
            : 1f - (MathF.Pow((-2f * through) + 2f, 3f) / 2f);

    /// <summary>Turns the ring.</summary>
    private void Turn()
    {
        foreach (PlacedModel? platform in _ring)
        {
            if (platform is not null)
            {
                Stand(platform, platform.Transform.Translation, _turned);
            }
        }
    }

    /// <summary>Carries Gabriel wherever he is standing.</summary>
    private void Ride()
    {
        switch (_doing)
        {
            case Doing.Waiting when World.PositionNamed(Entryway) is { } spot:
                // Held there every frame, which is also what puts him back after a death.
                World.Carry(Story.Ego, spot.Position, spot.Heading);
                break;

            case Doing.Riding when _on >= 0:
                Vector3 platform = Where(_on);

                // Facing the middle, which is where everything worth looking at is.
                World.Carry(Story.Ego, platform, Walker.Heading(-platform));
                break;

            default:
                break;
        }
    }

    /// <summary>Where a platform's middle is now.</summary>
    /// <remarks>
    /// Half a platform's worth of angle in, because the index counts leading edges and this
    /// wants the middle to stand on.
    /// </remarks>
    private Vector3 Where(int platform)
    {
        float angle = (PerPlatform * 0.5f) + (PerPlatform * platform) + _turned;

        Vector3 outwards = Vector3.Transform(
            Vector3.UnitZ, Matrix4x4.CreateRotationY(angle));

        return (Vector3.UnitY * Height) + (outwards * Radius);
    }

    /// <summary>Which platform is at the entryway, or a given number of places past it.</summary>
    /// <remarks>
    /// Platform twenty-four faces the door before anything has turned, and one more passes
    /// every <see cref="PerPlatform"/> radians. The blade's two slots are six platforms
    /// ahead of the door and six behind it.
    /// </remarks>
    private int Slot(int past = 0)
    {
        int gone = (int)(Wrapped(_turned) / PerPlatform);
        int at = Platforms - 1 - gone + past;

        return ((at % Platforms) + Platforms) % Platforms;
    }

    private static float Wrapped(float angle)
    {
        angle %= MathF.Tau;

        return angle < 0 ? angle + MathF.Tau : angle;
    }

    /// <inheritdoc/>
    public override void Pointing(ScenePick? under, bool busy) =>
        _under = under?.Name ?? string.Empty;

    /// <summary>What the player is offered when the blade can be caught.</summary>
    /// <remarks>
    /// A verb rather than a noun, because the noun is already on the screen and does not
    /// change: the blade says PENDULUM whether it is halfway across the room or buried in
    /// the slot beside you. What the player has no way of knowing is <em>when</em>, and the
    /// window is a couple of seconds. The reference puts a grab cursor up for the same
    /// reason; this port has no cursor art, so it says so instead.
    /// </remarks>
    private const string Grabbing = "GRAB";

    /// <summary>And when he is hanging off it over the altar.</summary>
    private const string Dropping = "LET GO";

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// <b>The blade has a noun and the room has a rule for it.</b> Without this the click
    /// resolved <c>PENDULUM:LOOK</c> every time and <see cref="TakesClick"/> was never
    /// reached, so the grab — the only way out of this room — could not be performed.
    /// </para>
    /// <para>
    /// Once he has left the doorway the whole room is claimed, advertised or not. He is on
    /// a turning platform with a blade coming at him and the scales on the far side are not
    /// his to poke at; the original claims every click in the room for the same reason.
    /// </para>
    /// </remarks>
    public override string? ClaimsClick(ScenePick? under)
    {
        string name = under?.Name ?? _under;

        return _doing switch
        {
            // In the doorway nothing is claimed: the platforms carry no noun and reach
            // TakesClick on their own, and everything else in the room answers to the
            // action files as it does in any other.
            Doing.Waiting => null,

            // And once he is standing on the altar the room is his again. The scales are up
            // there -- te3_counter, te3_gldbrick and the six shapes, all of them nouns with
            // scripts of their own -- and that puzzle is the rest of the room. Claiming
            // clicks here would end the game on the pillar he just landed on.
            Doing.Arrived => null,

            Doing.Riding when _danger != 0 && IsBlade(name) && WithinReach() => Grabbing,

            Doing.Holding when name.Equals(
                _altar?.Name ?? "te3_hpaltar", StringComparison.OrdinalIgnoreCase) &&
                MathF.Abs(Angle()) < _allowed => Dropping,

            _ => string.Empty,
        };
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <b>Nothing in this room is walked to.</b> There is no floor to speak of — a doorway,
    /// a turning ring and a shaft — so every click is a jump, a grab, a drop, or nothing.
    /// </remarks>
    public override bool TakesClick(ScenePick? under)
    {
        string name = under?.Name ?? _under;

        switch (_doing)
        {
            case Doing.Waiting when Platform(name) >= 0:
                // Out of the doorway onto whichever platform is in front of it — not the one
                // clicked, because by the time he lands it has moved on.
                Forward();

                return true;

            case Doing.Riding when Platform(name) is >= 0 and { } wanted:
                return Step(wanted);

            case Doing.Riding when _danger != 0 && IsBlade(name) && WithinReach():
                Grab();

                return true;

            case Doing.Holding when name.Equals(
                _altar?.Name ?? "te3_hpaltar", StringComparison.OrdinalIgnoreCase):
                Drop();

                return true;

            case Doing.Waiting:
                return false;

            default:
                // Riding, holding or dying: the room is not the player's to poke at.
                return true;
        }
    }

    /// <inheritdoc/>
    public override bool TakesFloorClick() => _doing != Doing.Waiting;

    /// <summary>Which platform a name refers to, or −1.</summary>
    private static int Platform(string name) =>
        name.Length == 7 &&
        name.StartsWith("te3_r", StringComparison.OrdinalIgnoreCase) &&
        int.TryParse(name[5..], CultureInfo.InvariantCulture, out int number) &&
        number is >= 1 and <= Platforms
            ? number - 1
            : -1;

    /// <summary>Whether a name is one of the two blade models.</summary>
    private static bool IsBlade(string name) =>
        name.Equals("te3_pendulum_center_code", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("te3_pendulum_center", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether Gabriel is near enough the blade's slot to catch it.</summary>
    /// <remarks>
    /// Within two platforms of the slot, and behind it: he reaches forward for it as it
    /// comes to him rather than sideways as it passes.
    /// </remarks>
    private bool WithinReach()
    {
        int slot = Slot(_danger > 0 ? 6 : -6);

        return _on == ((slot - 1 + Platforms) % Platforms) ||
               _on == ((slot - 2 + Platforms) % Platforms);
    }

    /// <summary>The first jump, out of the doorway.</summary>
    private void Forward()
    {
        _doing = Doing.Jumping;

        double start = World.Play("GABTE3JUMPFS");

        Then(start, () =>
        {
            // Cut away while he is put on a platform: the jump is authored for a doorway
            // and the platform has turned since it began.
            Cut("JUMP_OUT");

            _on = Slot();

            World.Carry(Story.Ego, Where(_on), Walker.Heading(-Where(_on)));
            World.Play(Animation);

            double end = World.Play("GABTE3JUMPFE");

            Then(end, () => _doing = Doing.Riding);
        });
    }

    /// <summary>
    /// A step to the next platform or the one before it.
    /// </summary>
    /// <returns>True when the click was the mechanism's, which it is on the ring.</returns>
    /// <remarks>
    /// Only the two platforms either side answer: the ring is a circle of twenty-four and
    /// jumping across it is not a thing Gabriel can do.
    /// </remarks>
    private bool Step(int wanted)
    {
        int ahead = (_on + 1) % Platforms;
        int behind = (_on - 1 + Platforms) % Platforms;

        if (wanted != ahead && wanted != behind)
        {
            return true;
        }

        _doing = Doing.Jumping;

        bool forward = wanted == ahead;
        string stem = forward ? "GABTE3JUMPL" : "GABTE3JUMPR";

        double start = World.Play(stem + "S");

        Then(start, () =>
        {
            double middle = World.Play(stem + "M");

            Then(middle, () =>
            {
                _on = wanted;
                World.Carry(Story.Ego, Where(_on), Walker.Heading(-Where(_on)));

                double end = World.Play(stem + "E");

                Then(end, () => _doing = Doing.Riding);
            });
        });

        return true;
    }

    /// <summary>Catches hold of the blade.</summary>
    private void Grab()
    {
        _doing = Doing.Holding;

        // Cut away: this swaps one model for another with him already on it.
        Cut(_danger > 0 ? "KILL_HIGH" : "KILL_LOW");

        double climbing = World.Play("GABJMPPNDULM");

        Then(climbing, () => Cut("LONG_ALTAR"));

        Api.Invoke("ChangeScore", [SheepValue.FromString("e_temple_grab_pendulum")]);
    }

    /// <summary>Lets go, over the altar or over the shaft.</summary>
    /// <remarks>
    /// <b>Two angles decide it, and the game states both.</b> <c>startSafe</c> is how far
    /// off vertical the player is allowed to try at all; <c>endSafe</c> is how far off it
    /// still lands him on the altar. Between the two he is allowed to jump and misses,
    /// which is the whole tension of the moment.
    /// </remarks>
    private void Drop()
    {
        if (MathF.Abs(Angle()) >= _allowed)
        {
            return;
        }

        double falling = World.Play("GABJMPOFFPEN");

        Then(falling, () =>
        {
            if (MathF.Abs(Angle()) < _safe)
            {
                Arrive();
            }
            else
            {
                Missed();
            }
        });
    }

    /// <summary>Lands on the altar, which finishes the room.</summary>
    private void Arrive()
    {
        _doing = Doing.Arrived;

        if (World.PositionNamed(Landing) is { } spot)
        {
            World.Carry(Story.Ego, spot.Position, spot.Heading);
        }

        Cut("ALTAR_UP");
        Story.SetFlag("Te3GabeAtAltar");
        Call("GabeOnPillar$");
    }

    /// <summary>Misses it.</summary>
    private void Missed()
    {
        // Unless the player has asked not to be killed, in which case he lands on the altar
        // after all: the alternative is dropping him back into the doorway to ride round
        // again, which is not survival so much as a refusal.
        if (Deathless)
        {
            Arrive();

            return;
        }

        _doing = Doing.Dying;

        Cut("FALL_DOWN");

        double falling = World.Play("GABEFALLDEATH");

        Then(falling + 2.0, Restart);
    }

    /// <summary>Is cut in half.</summary>
    private void Killed(bool onTheLeft, bool goingLeft)
    {
        // The blade decides this, not a script, so the armour that catches a script's Die$
        // never sees it. Put back in the doorway to try again, which is what choosing
        // "retry" on the original's death screen does.
        if (Deathless)
        {
            Restart();

            return;
        }

        _doing = Doing.Dying;

        Cut(onTheLeft ? "KILL_HIGH" : "KILL_LOW");

        // Four of them: which slot, and whether the blade is coming or going.
        string clip = onTheLeft
            ? goingLeft ? "GABTE3PNFTH" : "GABTE3PNBKH"
            : goingLeft ? "GABTE3PNBKL" : "GABTE3PNFTL";

        Api.Invoke(
            "StartDialogue",
            [SheepValue.FromString("1REGJ67Q81"), SheepValue.FromInt(1)]);

        double dying = World.Play(clip);

        Then(dying, () =>
        {
            Cut(onTheLeft ? "AFTERKILL_HIGH" : "AFTERKILL_LOW");

            Then(2.0, Restart);
        });
    }

    /// <summary>Puts him back in the doorway to try again.</summary>
    private void Restart()
    {
        Call("Die$");

        _doing = Doing.Waiting;
        _on = -1;

        Story.ClearFlag("Te3GabeAtAltar");

        // And the door he came through is open again: its opening frame is the open one.
        //
        // The scene calls it te3_stonedoor. Naming it te3_door posed nothing at all --
        // Pose matches the clip's own model name and answers with a count nobody read --
        // so every retry, and every death survived under plot armour, put Gabriel back in
        // a doorway that was still shut behind him.
        if (World.Pose(Animation, [Door], atEnd: false) == 0)
        {
            Log.Warning($"TE3: nothing in {Animation} is called {Door}; the door stays shut.");
        }
    }

    /// <summary>The door out of the entryway, as the scene file names it.</summary>
    private const string Door = "te3_stonedoor";

    /// <summary>The animation that shuts it, whose first frame is it standing open.</summary>
    private const string Animation = "TE3_DOORCLOSE";

    /// <summary>Runs one of the room's own scripts.</summary>
    private void Call(string function) => Api.Invoke(
        "CallSheep",
        [SheepValue.FromString("TE3"), SheepValue.FromString(function)]);
}
