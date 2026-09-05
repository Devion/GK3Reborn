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
/// <remarks>
/// <para>
/// Both are switched on in <see cref="Settings"/> and both work by changing what the
/// game's own scripts do rather than by rewriting them, because the scripts are the
/// shipped data and this port does not edit it. One hands an item over early; the other
/// answers one function call differently. Everything either of them touches is named here,
/// so that what an assistance costs the story can be read in one place.
/// </para>
/// <para>
/// Neither is on by default. GK3 as it shipped is a hard game on purpose, and somebody
/// meeting it for the first time should meet it that way.
/// </para>
/// </remarks>
public static class Assists
{
    /// <summary>The finished cat-hair moustache, as the action files name it.</summary>
    public const string Moustache = "BLACK_MOUSTACHE";

    /// <summary>Who is given it.</summary>
    public const string Owner = "GABRIEL";

    /// <summary>The flag that records having handed it over.</summary>
    /// <remarks>
    /// A story flag rather than a field, so it travels in the save. Without it, a player
    /// who combined the moustache into the cap and then reloaded would be handed a second
    /// one — and with the flag in the save, a game begun without the assistance and
    /// continued with it is still given one.
    /// </remarks>
    public const string GaveMoustacheFlag = "AssistGaveMoustache";

    /// <summary>The face code Gabriel's own artwork is listed under.</summary>
    public const string PlainFace = "GAB";

    /// <summary>The face code the game paints the moustache into.</summary>
    /// <remarks>
    /// <para>
    /// <c>GA3</c> is a character in <c>FACES.TXT</c> and <c>CHARACTERS.TXT</c> in his own
    /// right: the disguised Gabriel, placed offstage and hidden in the moped shop, who the
    /// original brings on for the one scene the disguise works in. His face bitmap is
    /// Gabriel's own with a moustache painted into it — the two are identical everywhere
    /// but the lip — and he has all eight lip-sync mouths, a forehead, eyelids and two
    /// blink animations to match.
    /// </para>
    /// <para>
    /// Which is what makes a permanently moustached Gabriel the game's own artwork rather
    /// than something drawn here: the face is composed out of <c>GA3</c>'s bitmaps and
    /// painted onto <c>GAB</c>'s texture, so he keeps his own model, his own clothes and
    /// his own animations and grows a moustache. The rest of the disguise — the cap and
    /// the gold coat, which are on GA3's model rather than on his face — is left where it
    /// belongs, in the bag.
    /// </para>
    /// </remarks>
    public const string MoustachedFace = "GA3";

    /// <summary>The function every death in the game goes through.</summary>
    /// <remarks>
    /// Five scripts define it — <c>TE1</c>, <c>TE3</c>, <c>TE4</c>, <c>TE5</c> and
    /// <c>TE6</c>, the five rooms of the temple on the last night — and no other script in
    /// the corpus has a function by this name. Each one is the same three steps: stop the
    /// music, show the death screen, put the puzzle back to the beginning.
    /// </remarks>
    public const string Death = "Die";

    /// <summary>The first thing every death does, before the screen or the reset.</summary>
    /// <remarks>
    /// <para>
    /// All five <c>Die$</c> bodies open with <c>StopAllSoundTracks</c>, and every
    /// <c>PostDeath$</c> starts the room's soundtracks again afterwards — TE1 its fire and
    /// the porch, TE4 its fire and the temple, TE3 the pendulum, TE5 and TE6 their own. The
    /// pair is one gesture: silence the room, then start it playing from the top.
    /// </para>
    /// <para>
    /// Plot armour never enters <c>Die</c>, so for a long time nothing did the first half —
    /// and a soundtrack already running is not started a second time, so <c>PostDeath</c>'s
    /// half did nothing either and the room's audio simply ran on across every retry. In TE6
    /// that is <c>TE6Demon.STK</c>, a single looping growl, and a player saved from the demon
    /// heard it repeating over and over with nothing able to stop it.
    /// </para>
    /// </remarks>
    public const string Silence = "StopAllSoundTracks";

    /// <summary>What the death screen's restart button calls back into.</summary>
    /// <remarks>
    /// <c>Restart</c> resets the puzzle and <c>PostDeath</c> starts it running again — the
    /// music, the demon, the pendulum. Nothing in the corpus ever calls <c>PostDeath</c>:
    /// it belongs to the death screen, which is why the pair of them is what plot armour
    /// has to run in the death screen's place.
    /// </remarks>
    public const string Restart = "Restart";

    /// <summary>The second half of it. See <see cref="Restart"/>.</summary>
    public const string Resume = "PostDeath";

    /// <summary>When the moustache becomes worth having.</summary>
    /// <remarks>
    /// Day 1, 2pm: the afternoon of the cat, the moped shop and Mosely's passport, and the
    /// only timeblock any of those nouns exists in. Handing it over sooner would put an
    /// item in Gabriel's pocket that nothing in the game has anything to say about.
    /// </remarks>
    public static Timeblock MoustacheDue => new(1, 2, IsAfternoon: true);

    /// <summary>
    /// Everything the moustache can have become, so it is not handed over twice.
    /// </summary>
    /// <remarks>
    /// Combining consumes it: with the cap it becomes <c>CAP_N_MOUSTACHE</c>, with the coat
    /// <c>COAT_N_MOUSTACHE</c>, with both <c>MOSELY_DISGUISE</c>, and once the disguise has
    /// worked the player is carrying the moped keys instead. A player already holding any
    /// of those is past this puzzle, whatever the flag says — which is what makes turning
    /// the assistance on halfway through a game safe.
    /// </remarks>
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
    /// <remarks>
    /// Safe to call as often as anything likes — on entering a room, on changing the
    /// setting — because everything about whether it has already happened is in the state
    /// rather than in a caller's memory.
    /// </remarks>
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
    /// <remarks>
    /// Both halves are checked, not just the name: a script that has a <c>Die</c> without a
    /// <c>Restart</c> and a <c>PostDeath</c> to put in its place is not one of the five,
    /// and would be left half-run rather than helped.
    /// </remarks>
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
    /// <remarks>
    /// The same pair, in the same order, that the original's death screen runs when the
    /// player chooses to try again — so what plot armour removes is the screen and the
    /// dying, and not the retry the game already knew how to give. <see cref="Silence"/>
    /// comes first, because in the original the room has been quiet since <c>Die</c> by
    /// the time the player presses the button.
    /// </remarks>
    public static IReadOnlyList<string> Survive => [Restart, Resume];

    /// <summary>Whether a script declares a function, spelled either way.</summary>
    /// <remarks>
    /// A compiled script writes the trailing <c>$</c> and its callers routinely leave it
    /// off, so the suffix comes off both sides — the same rule the machine matches a call
    /// by, and the reason <c>Die</c> finds <c>Die$</c>.
    /// </remarks>
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
