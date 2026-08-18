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

        return _byOwner.TryGetValue(owner, out HashSet<string>? items) && items.Remove(item.Trim());
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

    /// <summary>Everyone who is carrying something, in a stable order.</summary>
    public IReadOnlyList<string> Owners =>
        [.. _byOwner.Where(kv => kv.Value.Count > 0)
            .Select(kv => kv.Key)
            .OrderBy(o => o, StringComparer.OrdinalIgnoreCase)];
}
