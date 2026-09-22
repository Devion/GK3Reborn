namespace GK3Reborn.Game.Mechanisms;

/// <summary>
/// TE6: the fight at the end, where the player does not get to walk.
/// </summary>
public sealed class DemonFight : SceneMechanism
{
    private const double OpeningGraceSeconds = 6.0;
    private double _openingGrace;
    private bool _approachRequested;

    /// <summary>Creates the mechanism.</summary>
    /// <param name="world">The room.</param>
    /// <param name="api">The script host.</param>
    public DemonFight(SceneUpdate world, Gk3SheepApi api)
        : base(world, api)
    {
    }

    /// <inheritdoc/>
    public override string Name => "Holy";

    /// <inheritdoc/>
    public override string Report() => "the floor is the script's, not the player's";

    /// <inheritdoc/>
    public override bool Perform(string asked) => false;

    /// <summary>Hold the first approach while Gabriel can ask Grace for help.</summary>
    public bool HoldDemonWalk()
    {
        if (!_approachRequested)
        {
            _approachRequested = true;
            _openingGrace = OpeningGraceSeconds;
        }

        return _openingGrace > 0;
    }

    /// <inheritdoc/>
    public override void Advance(double seconds)
    {
        if (_approachRequested)
        {
            _openingGrace = Math.Max(0, _openingGrace - seconds);
        }
    }

    /// <inheritdoc/>
    public override bool TakesFloorClick()
    {
        // Dropped while he is already moving: the script clears this when the step it
        // started has finished, and a second click queued behind the first is a step the
        // fight never asked for.
        if (!Story.GetFlag("Te6GabeWalk"))
        {
            Story.SetFlag("Te6ClickedOnFloor");
        }

        return true;
    }
}
