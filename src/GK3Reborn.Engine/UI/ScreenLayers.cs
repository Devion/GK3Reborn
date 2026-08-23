namespace GK3Reborn.UI;

/// <summary>The modal screens GK3 puts in front of the room.</summary>
public enum ScreenKind
{
    /// <summary>What the player is carrying.</summary>
    Inventory,

    /// <summary>One inventory item, close up, with its own actions.</summary>
    InventoryInspect,

    /// <summary>Something in the room, close up.</summary>
    SceneInspect,

    /// <summary>The binoculars.</summary>
    Binoculars,

    /// <summary>The map you drive the moped around.</summary>
    Driving,

    /// <summary>The fingerprint kit.</summary>
    Fingerprint,

    /// <summary>Sidney, the portable computer.</summary>
    Sidney,

    /// <summary>The quest log: what the player is trying to do, and what they have done.</summary>
    /// <remarks>
    /// The port's own, with nothing behind it in the original. A 1999 adventure game will
    /// let a player wander for an hour with no idea what it wants of them, and
    /// <c>Plan/03</c> section 3 asks for an interface easier than that one's.
    /// </remarks>
    Journal,
}

/// <summary>One screen, and what it is showing.</summary>
/// <param name="Kind">Which screen.</param>
/// <param name="Subject">
/// What it is about — the item being inspected, the noun being dusted for prints — or null
/// where the screen is about nothing in particular.
/// </param>
public readonly record struct Screen(ScreenKind Kind, string? Subject = null)
{
    /// <summary>
    /// Whether this screen takes the player's ordinary controls away.
    /// </summary>
    /// <remarks>
    /// Driving is the only one that does: the player is somewhere else entirely, steering
    /// on a map, and the room they left is not theirs to act in. Everything else is a panel
    /// over the room — the inventory, an item held up to the light, the binoculars — and
    /// leaving it puts the player back exactly where they were.
    /// </remarks>
    public bool TakesOverInput => Kind == ScreenKind.Driving;

    /// <inheritdoc/>
    public override string ToString() => Subject is { Length: > 0 } about ? $"{Kind}({about})" : $"{Kind}";
}

/// <summary>
/// What is in front of the room, and how the player gets out of it.
/// </summary>
/// <remarks>
/// <para>
/// GK3 has a lot of modal screens — the inventory, an item held up close, the binoculars,
/// the fingerprint kit, the driving map, Sidney — and in the original each arrived with
/// its own way in and its own way out. <c>Plan/03-gameplay-ui-audio.md</c> section 3 asks
/// for the opposite: that they "share navigation, back behavior and scaling conventions",
/// so the player learns the way out once and it works everywhere. This is that stack.
/// </para>
/// <para>
/// One rule for leaving: <see cref="Back"/> closes whatever is on top and puts the player
/// back where they were. It never closes two things, never lands somewhere they have not
/// been, and is the same gesture whichever screen they are looking at.
/// </para>
/// <para>
/// One rule for the inventory: <see cref="InventoryReachable"/> says whether a dedicated
/// binding should open it right now, and the answer is yes unless the player is somewhere
/// their pockets are not — which is only the driving map. The original made the inventory
/// a small target to click at the edge of the screen; there is nothing to be gained by
/// reproducing that.
/// </para>
/// <para>
/// Scripts can ask what is showing — <c>IsTopLayerInventory</c> is a real question in the
/// data — so this is game state rather than presentation, and part of the state hash. What
/// it is not is a widget: nothing here draws anything, and a screen being open is a fact
/// about the game rather than about a window.
/// </para>
/// </remarks>
public sealed class ScreenLayers
{
    private readonly List<Screen> _open = [];

    /// <summary>What is open, the last one being on top.</summary>
    public IReadOnlyList<Screen> Open => _open;

    /// <summary>The screen the player is looking at, or null for the room itself.</summary>
    public Screen? Top => _open.Count > 0 ? _open[^1] : null;

    /// <summary>Whether the player is looking at the room rather than at a screen.</summary>
    public bool InTheRoom => _open.Count == 0;

    /// <summary>
    /// Whether a dedicated inventory binding should work right now.
    /// </summary>
    /// <remarks>
    /// True in the room, true over any panel that leaves the player where they were, and
    /// true when the inventory is itself on top so that the same binding shuts it again.
    /// False only where the player's pockets are not: the driving map.
    /// </remarks>
    public bool InventoryReachable => !_open.Exists(s => s.TakesOverInput);

    /// <summary>Whether a screen of some kind is open.</summary>
    /// <param name="kind">Which screen.</param>
    /// <returns>True when it is somewhere in the stack.</returns>
    public bool IsOpen(ScreenKind kind) => _open.Exists(s => s.Kind == kind);

    /// <summary>Whether a screen is the one on top.</summary>
    /// <param name="kind">Which screen.</param>
    /// <returns>True when it is the one the player is looking at.</returns>
    public bool IsOnTop(ScreenKind kind) => Top?.Kind == kind;

    /// <summary>Opens a screen, or brings it forward if it is already open.</summary>
    /// <param name="screen">The screen.</param>
    /// <remarks>
    /// Brought forward rather than opened twice, so asking for the inventory while it is
    /// buried under an inspect panel does what the player meant instead of stacking a
    /// second copy they would then have to close twice.
    /// </remarks>
    public void Show(Screen screen)
    {
        _open.RemoveAll(s => s.Kind == screen.Kind);
        _open.Add(screen);
    }

    /// <summary>Closes a particular screen, wherever it is in the stack.</summary>
    /// <param name="kind">Which screen.</param>
    /// <returns>True when it was open.</returns>
    public bool Hide(ScreenKind kind) => _open.RemoveAll(s => s.Kind == kind) > 0;

    /// <summary>Closes whatever is on top.</summary>
    /// <returns>The screen that closed, or null if the player was in the room already.</returns>
    /// <remarks>
    /// The one gesture the player has to learn. It closes exactly one thing, so backing
    /// out of an item held up close returns to the inventory it came from rather than all
    /// the way to the room.
    /// </remarks>
    public Screen? Back()
    {
        if (_open.Count == 0)
        {
            return null;
        }

        Screen top = _open[^1];
        _open.RemoveAt(_open.Count - 1);
        return top;
    }

    /// <summary>Closes everything and puts the player back in the room.</summary>
    /// <remarks>
    /// For changing location, where a screen left open would belong to a room that is no
    /// longer there.
    /// </remarks>
    public void CloseAll() => _open.Clear();
}
