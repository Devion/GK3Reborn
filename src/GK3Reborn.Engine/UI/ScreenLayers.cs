namespace GK3Reborn.UI;

/// <summary>The modal screens GK3 puts in front of the room.</summary>
public enum ScreenKind
{
    /// <summary>What the player is carrying.</summary>
    Inventory,

    /// <summary>One inventory item, close up, with its own actions.</summary>
    InventoryInspect,

    /// <summary>The binoculars.</summary>
    Binoculars,

    /// <summary>The map you drive the moped around.</summary>
    Driving,

    /// <summary>The fingerprint kit.</summary>
    Fingerprint,

    /// <summary>Sidney, the portable computer.</summary>
    Sidney,

    /// <summary>
    /// The hose, aimed at something until it comes down.
    /// </summary>
    Water,

    /// <summary>The quest log: what the player is trying to do, and what they have done.</summary>
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
    public bool TakesOverInput => Kind == ScreenKind.Driving;

    /// <summary>
    /// What the binoculars' subject reads while they are showing somewhere else.
    /// </summary>
    public const string Zoomed = "zoomed";

    /// <inheritdoc/>
    public override string ToString() => Subject is { Length: > 0 } about ? $"{Kind}({about})" : $"{Kind}";
}

/// <summary>
/// What is in front of the room, and how the player gets out of it.
/// </summary>
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
    public bool InventoryReachable => !_open.Exists(s => s.TakesOverInput);

    /// <summary>Whether a screen of some kind is open.</summary>
    /// <param name="kind">Which screen.</param>
    /// <returns>True when it is somewhere in the stack.</returns>
    public bool IsOpen(ScreenKind kind) => _open.Exists(s => s.Kind == kind);

    /// <summary>Whether a screen is the one on top.</summary>
    /// <param name="kind">Which screen.</param>
    /// <returns>True when it is the one the player is looking at.</returns>
    public bool IsOnTop(ScreenKind kind) => Top?.Kind == kind;

    /// <summary>Changes what the screen on top is about, without stacking another.</summary>
    /// <param name="screen">The same screen, with a different subject.</param>
    public void Replace(Screen screen)
    {
        if (_open.Count > 0)
        {
            _open[^1] = screen;
            return;
        }

        Show(screen);
    }

    /// <summary>Opens a screen, or brings it forward if it is already open.</summary>
    /// <param name="screen">The screen.</param>
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
    public void CloseAll() => _open.Clear();
}
