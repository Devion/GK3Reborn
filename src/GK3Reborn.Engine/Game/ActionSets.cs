namespace GK3Reborn.Game;

/// <summary>
/// Which action files are in scope in a scene, and in what order.
/// </summary>
/// <remarks>
/// <para>
/// A scene's verbs do not all come from the scene. Four sets of files are in play at once:
/// the timeblock file's own, listed in its <c>[ACTIONS]</c> section; the location's, listed
/// in the general file and spanning days or the whole game; a dozen global sets that apply
/// everywhere; and sixteen inventory sets that say what can be done with what the player is
/// carrying. Without the last two most objects have no <c>LOOK</c>, because looking at
/// things is a rule about the game rather than about the room.
/// </para>
/// <para>
/// Which of them apply is written in their names — <c>R25_23ALL.NVC</c> is days two and
/// three — and <see cref="TimeblockRange"/> reads that. The original checks the name for
/// the files the general file lists and for the global and inventory sets, and does not
/// check it for the timeblock file's own: that file is already the timeblock's, so anything
/// it names is meant for now whatever it is called.
/// </para>
/// <para>
/// Order is priority, because <see cref="ActionResolver"/> keeps the first rule it finds
/// for a verb. Most specific first, then, which is the opposite of the order the original
/// inserts them in — it can afford general-first because it keeps every rule and separates
/// them by case at the point of use, where this keeps one entry per verb so that a menu can
/// be built from the answer.
/// </para>
/// </remarks>
public static class ActionSets
{
    /// <summary>The sets that apply in every scene.</summary>
    /// <remarks>
    /// From G-Engine's <c>ActionManager::kGlobalActionSets</c>. The names encode their own
    /// timeblocks, so this is the whole list and the range decides.
    /// </remarks>
    public static IReadOnlyList<string> Global { get; } =
    [
        "GLB_ALL.NVC",
        "GLB_23ALL.NVC",
        "GLB102P.NVC",
        "GLB202A.NVC",
        "GLB210A.NVC",
        "GLB212P.NVC",
        "GLB202P.NVC",
        "GLB205P.NVC",
        "GLB307A.NVC",
        "GLB310A.NVC",
        "GLB312P.NVC",
        "GLB306P.NVC",
    ];

    /// <summary>The sets that say what can be done with the things the player carries.</summary>
    /// <remarks>From G-Engine's <c>ActionManager::kInventoryActionSets</c>.</remarks>
    public static IReadOnlyList<string> Inventory { get; } =
    [
        "INV_ALL.NVC",
        "INV_1ALL.NVC",
        "INV_23ALL.NVC",
        "INV_3ALL.NVC",
        "INV110A.NVC",
        "INV102P.NVC",
        "INV104P.NVC",
        "INV202A.NVC",
        "INV207A.NVC",
        "INV210A.NVC",
        "INV212P.NVC",
        "INV202P.NVC",
        "INV205P.NVC",
        "INV307A.NVC",
        "INV312P.NVC",
        "INV303P.NVC",
    ];

    /// <summary>The files a scene brings into scope, most specific first.</summary>
    /// <param name="definition">The scene's initialisation files.</param>
    /// <param name="at">
    /// Where the story is, or null when the caller named no timeblock. With no timeblock
    /// there is no way to tell which of a location's files apply, so all of them are taken
    /// and the global and inventory sets are left out — the same union the rest of the
    /// loader falls back to when the conditions cannot be decided.
    /// </param>
    /// <returns>File names, without duplicates, in the order they should be consulted.</returns>
    public static IReadOnlyList<string> For(SceneDefinition definition, Timeblock? at)
    {
        ArgumentNullException.ThrowIfNull(definition);

        List<string> names = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        // The timeblock file's own, unchecked: it is already the timeblock's.
        Take(definition.Specific?.ActionFiles(), check: false);

        // The location's, which span days and say so in their names.
        Take(definition.General?.ActionFiles(), check: true);

        if (at is not null)
        {
            Take(Global, check: true);
            Take(Inventory, check: true);
        }

        return names;

        void Take(IReadOnlyList<string>? candidates, bool check)
        {
            foreach (string name in candidates ?? [])
            {
                if (check && at is { } now && !TimeblockRange.Applies(name, now))
                {
                    continue;
                }

                if (seen.Add(name))
                {
                    names.Add(name);
                }
            }
        }
    }
}
