using GK3Reborn.Game.Navigation;

namespace GK3Reborn.Game.Mechanisms;

/// <summary>The four rooms whose code is a patch for a bug in the game's own data.</summary>
public sealed class RoomPatches : SceneMechanism
{
    private readonly string _room;

    /// <summary>Creates the patch for one room.</summary>
    /// <param name="room">Which room, as the game names the location.</param>
    /// <param name="world">The room.</param>
    /// <param name="api">The script host.</param>
    public RoomPatches(string room, SceneUpdate world, Gk3SheepApi api) : base(world, api)
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

    /// <summary>The museum: a flag the room's own script forgets to clear.</summary>
    private void Museum()
    {
        Story.ClearFlag("TE6Topics");
        _did = "cleared TE6Topics, which the room's own enter script should";
    }

    /// <summary>The chateau's east side: a one-pixel gap in the walk boundary.</summary>
    private void Chateau()
    {
        if (World.Boundary is not { } boundary || (Story.Timeblock != new Timeblock(2, 2, true) && Story.Timeblock != new Timeblock(3, 3, true)))
        {
            return;
        }

        boundary.SetRegionOpen(6, open: false);
        _did = "closed walker region 6, which is a one-pixel path through a door";
    }

    /// <summary>Chateau de Blanchefort: Emilio does not sit where the data says he sits.</summary>
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

    /// <summary>The lobby: Buchelli's wine glass, left in mid-air.</summary>
    private void Lobby()
    {
        if (Story.Timeblock != new Timeblock(2, 5, true) || Story.GetVariable("LSRState") <= 2)
        {
            return;
        }

        if (World.Pose("VITLBYSTANDWBRB", ["bglass", "bourbon"]) > 0)
        {
            _did = "put Buchelli's glass down, which the room's opening poses do not";
        }
    }
}
