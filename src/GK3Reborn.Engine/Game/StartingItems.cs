// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

namespace GK3Reborn.Game;

/// <summary>
/// What the two of them are carrying when a new game begins.
/// </summary>
/// <remarks>
/// <para>
/// Nothing in the shipped data gives these out. No barn holds a list of starting items and
/// no scene script hands one over, because the table was compiled into the original
/// executable the same way the score table was — G-Engine hardcodes it too, with a comment
/// saying it ought to be data-driven and that its author could not find where. So the
/// engine carries it; see <c>Assets/Story/Pockets.txt</c> and NOTICE.
/// </para>
/// <para>
/// <b>It is not a convenience.</b> Prince James's card is where the number Gabriel dials
/// comes from, so a player starting with empty pockets cannot use the pay phone, and Day 1
/// 10am cannot be finished at all. Reported exactly that way.
/// </para>
/// <para>
/// Given once, when a game starts, and never on the way into a room — a player who has put
/// the wallet down somewhere should not find it back in their pocket at the next door.
/// Loading a save does not call this either: what a save holds is what was being carried.
/// </para>
/// </remarks>
public static class StartingItems
{
    /// <summary>The table the engine ships.</summary>
    /// <returns>Each owner and what they begin with.</returns>
    public static IReadOnlyList<(string Owner, string Item)> Open()
    {
        using Stream? stream = typeof(StartingItems).Assembly
            .GetManifestResourceStream("GK3Reborn.Assets.Story.Pockets.txt");

        if (stream is null)
        {
            return [];
        }

        using var reader = new StreamReader(stream);

        return Parse(reader.ReadToEnd());
    }

    /// <summary>Reads a table.</summary>
    /// <param name="text">Its contents.</param>
    /// <returns>Each owner and what they begin with, in the order written.</returns>
    public static IReadOnlyList<(string Owner, string Item)> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        List<(string, string)> pockets = [];

        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();

            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            string[] parts = line.Split(
                (char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (parts.Length >= 2)
            {
                pockets.Add((parts[0], parts[1]));
            }
        }

        return pockets;
    }

    /// <summary>Fills both characters' pockets.</summary>
    /// <param name="inventory">Where to put them.</param>
    /// <returns>How many items were given out.</returns>
    /// <remarks>
    /// Both of them, whoever the player currently is. Grace's things are hers from the
    /// start, and the game swaps between the two of them without ever handing over a bag.
    /// </remarks>
    public static int Fill(Inventory inventory)
    {
        ArgumentNullException.ThrowIfNull(inventory);

        int given = 0;

        foreach ((string owner, string item) in Open())
        {
            inventory.Add(owner, item);
            given++;
        }

        return given;
    }
}
