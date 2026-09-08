namespace GK3Reborn.Game;

/// <summary>
/// Which action files are in scope in a scene, and in what order.
/// </summary>
public static class ActionSets
{
    /// <summary>The sets that apply in every scene.</summary>
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

        // Within a family, most particular first — which is what the name says and not what
        // the order says. LBY.SIF lists lby_all.nvc above lby_1all.nvc, and taking that as
        // priority put every rule about day one behind the file that covers every day: on
        // the bar, Jean's two day-one topics came out below the small talk the whole game
        // shares. OrderByDescending is stable, so files of equal particularity keep the
        // order their scene file gave them.
        void Take(IReadOnlyList<string>? candidates, bool check)
        {
            IEnumerable<string> wanted = candidates ?? [];

            if (check && at is { } now)
            {
                wanted = wanted.Where(name => TimeblockRange.Applies(name, now));
            }

            foreach (string name in wanted.OrderByDescending(TimeblockRange.Specificity))
            {
                if (seen.Add(name))
                {
                    names.Add(name);
                }
            }
        }
    }
}
