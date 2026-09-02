using GK3Reborn.Game.Navigation;

namespace GK3Reborn.Game.Mechanisms;

/// <summary>
/// The four rooms whose code is a patch for a bug in the game's own data.
/// </summary>
/// <remarks>
/// <para>
/// These declare no <c>custom=</c> and no script ever sends them a word: the original ran
/// <c>&lt;location&gt;-init</c> on every scene load whether the room declared a mechanism
/// or not, and four rooms use that hook to fix something the shipped data gets wrong. The
/// reference engine found all four; each is written up where it is applied.
/// </para>
/// <para>
/// <b>They are not cosmetic.</b> The museum's is a soft lock — a flag the game's own enter
/// script should clear and does not, after which eavesdropping on Lady Howard and Estelle
/// waits for ever on a loop that can never end. That one is a bug in the original too.
/// </para>
/// <para>
/// Adapted from G-Engine's <c>SceneFunctions.cpp</c> under GPL-3, attributed in NOTICE.
/// </para>
/// </remarks>
public sealed class RoomPatches : SceneMechanism
{
    private readonly string _room;

    /// <summary>Creates the patch for one room.</summary>
    /// <param name="room">Which room, as the game names the location.</param>
    /// <param name="world">The room.</param>
    /// <param name="api">The script host.</param>
    public RoomPatches(string room, SceneUpdate world, Gk3SheepApi api)
        : base(world, api)
    {
        ArgumentNullException.ThrowIfNull(room);

        _room = room.ToUpperInvariant();
    }

    /// <inheritdoc/>
    public override string Name => $"{_room} patch";

    /// <summary>What was actually put right, for the log.</summary>
    private string _did = "nothing to put right here";

    /// <inheritdoc/>
    public override string Report() => _did;

    /// <inheritdoc/>
    public override bool Perform(string asked) => false;

    /// <inheritdoc/>
    public override void Begin()
    {
        switch (_room)
        {
            case "MS3":
                Museum();
                break;

            case "CSE":
                Chateau();
                break;

            case "CD1":
                Blanchefort();
                break;

            case "LBY":
                Lobby();
                break;

            default:
                break;
        }
    }

    /// <summary>
    /// The museum: a flag the room's own script forgets to clear.
    /// </summary>
    /// <remarks>
    /// <c>TE6Topics</c> marks Lady Howard and Estelle as mid-animation, turning to face a
    /// display. Leaving the room during that animation leaves it set for ever, and the
    /// eavesdrop cutscene loops waiting for it to clear — a soft lock in the original,
    /// where the room's enter script should have cleared it and does not.
    /// </remarks>
    private void Museum()
    {
        Story.ClearFlag("TE6Topics");
        _did = "cleared TE6Topics, which the room's own enter script should";
    }

    /// <summary>
    /// The chateau's east side: a one-pixel gap in the walk boundary.
    /// </summary>
    /// <remarks>
    /// Region 6 of <c>CSE</c>'s boundary is technically walkable and technically a path,
    /// so Montreaux takes it and walks through a door. Closed for the two timeblocks where
    /// anybody is sent along it.
    /// </remarks>
    private void Chateau()
    {
        if (World.Boundary is not { } boundary ||
            (Story.Timeblock != new Timeblock(2, 2, true) &&
             Story.Timeblock != new Timeblock(3, 3, true)))
        {
            return;
        }

        boundary.SetRegionOpen(6, open: false);
        _did = "closed walker region 6, which is a one-pixel path through a door";
    }

    /// <summary>
    /// Chateau de Blanchefort: Emilio does not sit where the data says he sits.
    /// </summary>
    /// <remarks>
    /// The reference's author could not work out why — the scene names a spot, the
    /// original ignores it, and following the data puts Emilio somewhere he visibly is not
    /// in the original. The position here is that engine's measurement of where he
    /// actually stands.
    /// </remarks>
    private void Blanchefort()
    {
        if (Story.Timeblock != new Timeblock(1, 4, true))
        {
            return;
        }

        if (World.Place("EMILIO", new System.Numerics.Vector3(1272f, 723f, -616f), 0f))
        {
            _did = "stood Emilio where the original has him rather than where the file says";
        }
    }

    /// <summary>
    /// The lobby: Buchelli's wine glass, left in mid-air.
    /// </summary>
    /// <remarks>
    /// <c>LBY205P.SIF</c>'s opening poses for the glass and its contents do not touch the
    /// glass, and yet the original has it on the table once Buchelli has left for the
    /// dining room. Nobody has worked out how. Sampling the last frame of the animation
    /// that <em>does</em> move it — Buchelli putting it down — puts it where it belongs.
    /// </remarks>
    private void Lobby()
    {
        if (Story.Timeblock != new Timeblock(2, 5, true) ||
            Story.GetVariable("LSRState") <= 2)
        {
            return;
        }

        if (World.Pose("VITLBYSTANDWBRB", ["bglass", "bourbon"]) > 0)
        {
            _did = "put Buchelli's glass down, which the room's opening poses do not";
        }
    }
}
