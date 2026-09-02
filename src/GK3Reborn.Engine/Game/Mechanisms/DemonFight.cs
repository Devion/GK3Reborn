namespace GK3Reborn.Game.Mechanisms;

/// <summary>
/// TE6: the fight at the end, where the player does not get to walk.
/// </summary>
/// <remarks>
/// <para>
/// Gabriel is circling a pentagram with Asmodeus in it, and the room is a set piece rather
/// than a floor: he moves in the room's own backward-stepping animations, one per click,
/// not by being sent across the ground.
/// </para>
/// <para>
/// <b>So a click on the floor is not a walk.</b> It sets <c>Te6ClickedOnFloor</c>, which
/// <c>TE6.SHP</c> is polling, and the script decides what he does about it — unless
/// <c>Te6GabeWalk</c> says he is already moving, in which case the click is dropped. The
/// reference registers exactly this as the scene's <em>walk override</em>; without it the
/// player walks Gabriel out of the fight and the script goes on without him.
/// </para>
/// <para>
/// The reference's other half — forcing Gabriel onto a circle of radius 210 every frame —
/// is a workaround for a rotation bug in its own animation code, stated as such in a
/// comment, and is deliberately not carried over. If the walk animations drift here, that
/// is a fault in this engine's clips to be found and fixed rather than papered over.
/// </para>
/// </remarks>
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
