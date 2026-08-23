namespace GK3Reborn.Game;

/// <summary>
/// What the characters are carrying.
/// </summary>
/// <remarks>
/// <para>
/// Inventory is per character rather than global: GK3 switches between Gabriel and Grace,
/// they carry different things, and a great deal of the action files' logic turns on
/// which of them holds what — <c>DoesEgoHaveInvItem</c> appears in 161 conditions and
/// <c>DoesGraceHaveInvItem</c> exists separately for exactly this reason.
/// </para>
/// <para>
/// Items are named, not numbered, and compared case-insensitively like everything else in
/// this data.
/// </para>
/// </remarks>
public sealed class Inventory
{
    private readonly Dictionary<string, HashSet<string>> _byOwner = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _active = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gives an item to someone.</summary>
    /// <param name="owner">Who receives it.</param>
    /// <param name="item">The item.</param>
    public void Add(string owner, string item)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(item);

        if (!_byOwner.TryGetValue(owner, out HashSet<string>? items))
        {
            items = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _byOwner[owner] = items;
        }

        items.Add(item.Trim());
    }

    /// <summary>Takes an item away.</summary>
    /// <param name="owner">Who loses it.</param>
    /// <param name="item">The item.</param>
    /// <returns>True when they had it.</returns>
    public bool Remove(string owner, string item)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(item);

        if (string.Equals(ActiveItemOf(owner), item.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            // An item that is gone cannot still be the one held ready.
            _active.Remove(owner);
        }

        return _byOwner.TryGetValue(owner, out HashSet<string>? items) && items.Remove(item.Trim());
    }

    /// <summary>What someone is holding ready to use on things.</summary>
    /// <param name="owner">Whose hand to look in.</param>
    /// <returns>The item, or null when they are holding nothing.</returns>
    /// <remarks>
    /// Distinct from carrying it. GK3's inventory screen has one item selected at a time,
    /// and using an item on something is written in the action files as a verb named for
    /// the item — so <c>IsActiveInvItem</c> asks which of the things in the bag is the one
    /// currently in hand.
    /// </remarks>
    public string? ActiveItemOf(string owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return _active.GetValueOrDefault(owner);
    }

    /// <summary>Puts an item in someone's hand, or empties it.</summary>
    /// <param name="owner">Whose hand.</param>
    /// <param name="item">The item, or null for none.</param>
    /// <remarks>
    /// Not refused when they are not carrying it. The original logs a warning and does it
    /// anyway, and a script that sets an item active before granting it is relying on that.
    /// </remarks>
    public void SetActive(string owner, string? item)
    {
        ArgumentNullException.ThrowIfNull(owner);

        if (item is { Length: > 0 })
        {
            _active[owner] = item.Trim();
        }
        else
        {
            _active.Remove(owner);
        }
    }

    /// <summary>Empties every pocket.</summary>
    /// <remarks>
    /// For loading a game. Adding what a save holds is not enough on its own: whatever the
    /// player was carrying before has to go, or a load hands them both games' inventories
    /// and every puzzle either side of it stops meaning anything.
    /// </remarks>
    public void Clear()
    {
        _byOwner.Clear();
        _active.Clear();
    }

    /// <summary>Whether someone is carrying an item.</summary>
    /// <param name="owner">Who to check.</param>
    /// <param name="item">The item.</param>
    /// <returns>True when they have it.</returns>
    public bool Has(string owner, string item)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(item);

        return _byOwner.TryGetValue(owner, out HashSet<string>? items) && items.Contains(item.Trim());
    }

    /// <summary>Everything someone is carrying, in a stable order.</summary>
    /// <param name="owner">Who to list.</param>
    /// <returns>Their items.</returns>
    public IReadOnlyList<string> ItemsOf(string owner)
    {
        ArgumentNullException.ThrowIfNull(owner);

        return _byOwner.TryGetValue(owner, out HashSet<string>? items)
            ? [.. items.OrderBy(i => i, StringComparer.OrdinalIgnoreCase)]
            : [];
    }

    /// <summary>Everyone holding an item ready, and what, in a stable order.</summary>
    public IReadOnlyList<(string Owner, string Item)> ActiveItems =>
        [.. _active.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => (kv.Key, kv.Value))];

    /// <summary>Everyone who is carrying something, in a stable order.</summary>
    public IReadOnlyList<string> Owners =>
        [.. _byOwner.Where(kv => kv.Value.Count > 0)
            .Select(kv => kv.Key)
            .OrderBy(o => o, StringComparer.OrdinalIgnoreCase)];
}
