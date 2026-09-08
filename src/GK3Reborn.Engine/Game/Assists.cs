// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using GK3Reborn.Sheep;

namespace GK3Reborn.Game;

/// <summary>
/// The two ways the game can be made easier, and what each of them actually does.
/// </summary>
public static class Assists
{
    /// <summary>The finished cat-hair moustache, as the action files name it.</summary>
    public const string Moustache = "BLACK_MOUSTACHE";

    /// <summary>Who is given it.</summary>
    public const string Owner = "GABRIEL";

    /// <summary>The flag that records having handed it over.</summary>
    public const string GaveMoustacheFlag = "AssistGaveMoustache";

    /// <summary>The face code Gabriel's own artwork is listed under.</summary>
    public const string PlainFace = "GAB";

    /// <summary>The face code the game paints the moustache into.</summary>
    public const string MoustachedFace = "GA3";

    /// <summary>The function every death in the game goes through.</summary>
    public const string Death = "Die";

    /// <summary>The first thing every death does, before the screen or the reset.</summary>
    public const string Silence = "StopAllSoundTracks";

    /// <summary>What the death screen's restart button calls back into.</summary>
    public const string Restart = "Restart";

    /// <summary>The second half of it. See <see cref="Restart"/>.</summary>
    public const string Resume = "PostDeath";

    /// <summary>When the moustache becomes worth having.</summary>
    public static Timeblock MoustacheDue => new(1, 2, IsAfternoon: true);

    /// <summary>
    /// Everything the moustache can have become, so it is not handed over twice.
    /// </summary>
    private static readonly string[] Downstream =
    [
        Moustache,
        "CAP_N_MOUSTACHE",
        "COAT_N_MOUSTACHE",
        "MOSELY_DISGUISE",
        "MOPED_KEYS",
    ];

    /// <summary>
    /// Hands Gabriel the finished moustache, if this is the point to do it.
    /// </summary>
    /// <param name="state">The game.</param>
    /// <returns>True when it was given, which happens at most once a game.</returns>
    public static bool GiveMoustache(GameState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (state.Timeblock < MoustacheDue || state.GetFlag(GaveMoustacheFlag))
        {
            return false;
        }

        foreach (string already in Downstream)
        {
            if (state.Inventory.Has(Owner, already))
            {
                // Past it under their own steam. The flag is still set, so this stops
                // being asked.
                state.SetFlag(GaveMoustacheFlag);
                return false;
            }
        }

        state.Inventory.Add(Owner, Moustache);
        state.SetFlag(GaveMoustacheFlag);
        return true;
    }

    /// <summary>
    /// Whether a call is a script about to kill Gabriel, and plot armour is on.
    /// </summary>
    /// <param name="state">The game, for the player's preference.</param>
    /// <param name="script">The script being called into.</param>
    /// <param name="function">The function being entered.</param>
    /// <returns>True when <see cref="Survive"/> should be run instead of it.</returns>
    public static bool IsDeath(GameState state, SheepScriptFile script, string function)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(script);
        ArgumentNullException.ThrowIfNull(function);

        return state.PlotArmour &&
               function.TrimEnd('$').Equals(Death, StringComparison.OrdinalIgnoreCase) &&
               Has(script, Restart) &&
               Has(script, Resume);
    }

    /// <summary>What is run in a death's place: the puzzle, from the beginning.</summary>
    public static IReadOnlyList<string> Survive => [Restart, Resume];

    /// <summary>Whether a script declares a function, spelled either way.</summary>
    private static bool Has(SheepScriptFile script, string function)
    {
        foreach ((string name, int _) in script.Functions)
        {
            if (name is not null &&
                name.TrimEnd('$').Equals(function.TrimEnd('$'), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
