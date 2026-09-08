// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using GK3Reborn.Formats.Rebarn;

namespace GK3Reborn.Content;

/// <summary>Which towns the dressing has geometry for.</summary>
[Flags]
public enum DressedTowns
{
    /// <summary>Nothing installed: every room is the room the game shipped.</summary>
    None = 0,

    /// <summary>TR1, which GK3 called Couiza and modelled eleven boxes of.</summary>
    Couiza = 1 << 0,

    /// <summary>RL1, which has eight buildings on one street and grass beyond them.</summary>
    RennesLesBains = 1 << 1,

    /// <summary>
    /// RC3, whose cemetery gateway is a gap in a wall with nothing over it.
    /// </summary>
    RennesLeChateau = 1 << 2,
}

public static class SceneDressing
{
    /// <summary>
    /// A model each town cannot be installed without.
    /// </summary>
    private static readonly (DressedTowns Town, string Model)[] Sentinels =
    [
        (DressedTowns.Couiza, "RBN_CZ_ROW_A"),
        (DressedTowns.RennesLesBains, "RBN_RB_ROW_A"),
        (DressedTowns.RennesLeChateau, "RBN_RC_CEMGATE"),
    ];

    /// <summary>Couiza's sentinel, for the message that says why a room is empty.</summary>
    public const string Sentinel = "RBN_CZ_ROW_A";

    /// <summary>Which towns' geometry is installed.</summary>
    /// <param name="modelsDirectory">
    /// The loose <c>enhanced/models</c> directory, or empty when there is none.
    /// </param>
    /// <param name="packs">The ReBarn packs beside the executable, or null for none.</param>
    /// <returns>The towns whose sets are there and whose sections should be applied.</returns>
    public static DressedTowns Installed(string modelsDirectory, RebarnContent? packs)
    {
        ModelLibrary library = ModelLibrary.Open(modelsDirectory ?? string.Empty, packs);
        DressedTowns found = DressedTowns.None;

        foreach ((DressedTowns town, string model) in Sentinels)
        {
            if (library.Has(model))
            {
                found |= town;
            }
        }

        return found;
    }

    /// <summary>Whether any town's geometry is installed.</summary>
    /// <param name="modelsDirectory">Where the loose models are, or empty.</param>
    /// <param name="packs">The packs, or null.</param>
    /// <returns>True when there is anything to place.</returns>
    public static bool Available(string modelsDirectory, RebarnContent? packs) =>
        Installed(modelsDirectory, packs) != DressedTowns.None;
}
