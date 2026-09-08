namespace GK3Reborn.Game.Mechanisms;

/// <summary>
/// TE6: the fight at the end, where the player does not get to walk.
/// </summary>
public sealed class DemonFight : SceneMechanism
{
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
