namespace GK3Reborn.Game.Story;

/// <summary>What a timeblock's rules decided: the point in the story to move to.</summary>
/// <param name="Next">The timeblock that starts.</param>
/// <param name="Location">The room to open with it, or null to stay where the player is.</param>
public readonly record struct TimeblockCompletion(Timeblock Next, string? Location);

/// <summary>
/// When a point in the story is over.
/// </summary>
/// <remarks>
/// <para>
/// The game advances the clock when the player has done everything a timeblock requires,
/// checked on every change of location. Nothing in the shipped data holds these rules: no
/// script in the eight barns calls <c>SetTime</c> or <c>SetLocationTime</c> at all, because
/// the original carried them inside its executable. What they are is written down in the
/// design document the game shipped with, <c>TIMEBLOCKBIBLE.TXT</c>, one "Completion Rules"
/// list per timeblock.
/// </para>
/// <para>
/// Adapted from G-Engine's <c>Assets/Timeblocks.shp</c> by Clark Kromenaker
/// (https://github.com/kromenak/gengine), GNU General Public License version 3. See NOTICE.
/// Its form there is a Sheep script the engine compiles at startup, and that is not the form
/// here: a compiled Sheep script is a <c>.shp</c>, every <c>.shp</c> in existence is original
/// game data, and this repository refuses that extension in <c>.gitignore</c> and again in the
/// CI check. Carrying the rules as source in an assembly that cannot hold the file was a
/// contradiction; carrying them as code is not, and it is checked by the compiler besides.
/// </para>
/// <para>
/// Every condition below reads the same <see cref="GameState"/> the Sheep functions of the
/// same name read — <c>GetNounVerbCount</c> is <see cref="GameState.GetNounVerbCount(string, string)"/>,
/// <c>GetFlag</c> is <see cref="GameState.GetFlag"/> — so the rules still ask the game the
/// questions the game's own scripts ask, and stay checkable against the corpus.
/// </para>
/// <para>
/// Nothing here changes the clock. Deciding and acting are separate so that a rule can be
/// asked what it thinks without moving the story; <see cref="Application"/> is what applies
/// the answer.
/// </para>
/// </remarks>
public static class TimeblockRules
{
    /// <summary>Asks whether this point in the story is over.</summary>
    /// <param name="state">The game.</param>
    /// <returns>Where the story goes next, or null when the timeblock is not finished.</returns>
    public static TimeblockCompletion? Check(GameState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        return state.Timeblock.ToString() switch
        {
            // Day 1.
            "110A" => Day1At10Am(state),
            "112P" => Day1At12Pm(state),
            "102P" => Day1At2Pm(state),
            "104P" => Day1At4Pm(state),
            "106P" => Day1At6Pm(state),

            // Day 2.
            "207A" => Day2At7Am(state),
            "210A" => Day2At10Am(state),
            "212P" => Day2At12Pm(state),
            "202P" => Day2At2Pm(state),
            "205P" => Day2At5Pm(state),

            // Named for day two even though the clock says day three; the original's own
            // numbering, and the string a scene file compares against.
            "202A" => Day2At2Am(state),

            // Day 3.
            "307A" => Day3At7Am(state),
            "310A" => Day3At10Am(state),
            "312P" => Day3At12Pm(state),
            "303P" => Day3At3Pm(state),
            "306P" => Day3At6Pm(state),

            _ => null,
        };
    }

    /// <summary>The blocks this class knows the rules for, in story order.</summary>
    /// <remarks>
    /// Sixteen of them, which is every block the game has rules for; the seventeenth,
    /// <c>309P</c>, is where the story ends and nothing follows it. Exposed so that a start-up
    /// check can say how many rules were carried without running any of them.
    /// </remarks>
    public static IReadOnlyList<string> Known { get; } =
    [
        "110A", "112P", "102P", "104P", "106P",
        "207A", "210A", "212P", "202P", "205P", "202A",
        "307A", "310A", "312P", "303P", "306P",
    ];

    // ---------------------------------------------------------------------------------
    // Day 1.
    // ---------------------------------------------------------------------------------

    private static TimeblockCompletion? Day1At10Am(GameState state)
    {
        // Must be at RC1 to complete the timeblock.
        if (!At(state, "RC1"))
        {
            return null;
        }

        // Must have met Mosely. COFFEE_POT/POUR is set during the dining room cutscene.
        if (state.GetNounVerbCount("COFFEE_POT", "POUR") == 0)
        {
            return null;
        }

        // Must have called Prince James from one of the three phones.
        if (state.GetNounVerbCount("PHONE", "PRINCE_JAMES_CARD") == 0 &&
            state.GetNounVerbCount("OTR_PHONE_1", "PRINCE_JAMES_CARD") == 0 &&
            state.GetNounVerbCount("OTR_PHONE_2", "PRINCE_JAMES_CARD") == 0)
        {
            return null;
        }

        // Must have read the register.
        if (state.GetNounVerbCount("REGISTER", "READ") == 0)
        {
            return null;
        }

        // Must have at least met Emilio.
        if (state.GetTopicCount("EMILIO", "T_INTRODUCE") == 0)
        {
            return null;
        }

        // Must ask Buthane about check-in times.
        if (state.GetTopicCount("BUTHANE", "T_CHECK_IN") < 2)
        {
            return null;
        }

        // Must see the "San Greal" display at the bookstore.
        if (state.GetNounVerbCount("SAN_GREAL_WORDS", "LOOK") == 0)
        {
            return null;
        }

        // Must talk to Girard about the Holy Grail.
        if (state.GetTopicCount("GIRARD", "T_HOLY_GRAIL") == 0)
        {
            return null;
        }

        // Must at least introduce himself to Howard and Estelle.
        if (state.GetTopicCount("LADY_H_ESTELLE", "T_INTRODUCE") == 0)
        {
            return null;
        }

        return Then("112P");
    }

    private static TimeblockCompletion? Day1At12Pm(GameState state)
    {
        // Must be at RC1 to complete the timeblock.
        if (!At(state, "RC1"))
        {
            return null;
        }

        // Must have met Buchelli and talked about check-in time.
        if (state.GetTopicCount("BUCHELLI", "T_CHECK_IN") < 1)
        {
            return null;
        }

        // Must have met the Abbe and talked about the Templars.
        if (state.GetTopicCount("ABBE", "T_TEMPLARS") < 1)
        {
            return null;
        }

        // Must have witnessed Lady Howard and Emilio switching rooms, or read the updated
        // hotel register.
        if (!state.GetFlag("SeenLHRoomSwitch") && state.GetNounVerbCount("REGISTER", "READ") < 2)
        {
            return null;
        }

        // Must have talked to Mosely about the case, Grace and Schattenjagers.
        if (state.GetTopicCount("MOSELY", "T_CASE") < 1 ||
            state.GetTopicCount("MOSELY", "T_GRACE") < 1 ||
            state.GetTopicCount("MOSELY", "T_SCHATTENJAGER") < 1)
        {
            return null;
        }

        return Then("102P");
    }

    /// <summary>
    /// How many of the two afternoon threads the player has finished.
    /// </summary>
    /// <remarks>
    /// Two blocks read this and want different answers from it: 102P ends once either has
    /// been done, 104P once both have. In the Sheep original it is a function writing a
    /// script-global, which is why it is one thing rather than two.
    /// </remarks>
    private static int TrainStationAndLarryActions(GameState state)
    {
        int count = 0;

        // "Think" at the train station arrival board, and the taxi driver bribed.
        if ((state.GetNounVerbCount("ARRIVALS_IN_CU", "THINK") > 0 ||
             state.GetTopicCount("MARCIE", "T_TRAIN_FROM_NAPLES") > 0) &&
            state.GetNounVerbCount("TAXI_DRIVER", "WALLET") == 2)
        {
            count++;
        }

        // Larry talked to about the Templars.
        if (state.GetTopicCount("LARRY", "T_TEMPLARS") >= 2)
        {
            count++;
        }

        return count;
    }

    private static TimeblockCompletion? Day1At2Pm(GameState state)
    {
        // Must be on the map screen.
        if (!At(state, "MAP"))
        {
            return null;
        }

        // Must have rented the moped. Having the keys is taken as proof of it.
        if (!Carrying(state, "MOPED_KEYS"))
        {
            return null;
        }

        // Must have followed Buthane.
        if (!ActorAt(state, "BUTHANE", "CSD"))
        {
            return null;
        }

        // Must have followed Wilkes.
        if (!ActorAt(state, "WILKES", "LER"))
        {
            return null;
        }

        // Must have done either of the two afternoon threads.
        if (TrainStationAndLarryActions(state) == 0)
        {
            return null;
        }

        return Then("104P");
    }

    private static TimeblockCompletion? Day1At4Pm(GameState state)
    {
        // Must be at MOP.
        if (!At(state, "MOP"))
        {
            return null;
        }

        // Must have done both of the afternoon threads by now.
        if (TrainStationAndLarryActions(state) < 2)
        {
            return null;
        }

        // Must have introduced himself to Wilkes. This can technically be done in 112P or
        // 102P, but it must have been done by now.
        if (state.GetTopicCount("WILKES", "T_INTRODUCE") == 0)
        {
            return null;
        }

        // Must have used the binoculars to spy on Mosely and Buthane from the Chateau de
        // Blanchefort.
        if (state.GetNounVerbCount("VIEW_OF_LHOMME_MORE", "BINOCULARS") == 0)
        {
            return null;
        }

        return Then("106P");
    }

    private static TimeblockCompletion? Day1At6Pm(GameState state)
    {
        // Must be at R25.
        if (!At(state, "R25"))
        {
            return null;
        }

        // Must have asked Grace and Mosely about the Abbe Arnaud. There is more to reach
        // this point, but it can only be done once everything else has been.
        if (state.GetTopicCount("GRACE_N_MOSE", "T_ABBE") == 0)
        {
            return null;
        }

        return Then("207A");
    }

    // ---------------------------------------------------------------------------------
    // Day 2.
    // ---------------------------------------------------------------------------------

    private static TimeblockCompletion? Day2At7Am(GameState state)
    {
        // The way this timeblock ends is that the Chateau de Blanchefort's own script
        // changes the location to R25 once its conditions are met.
        if (!At(state, "R25"))
        {
            return null;
        }

        // Must have come here from the Chateau de Blanchefort.
        if (!CameFrom(state, "CD1"))
        {
            return null;
        }

        // The original will not let the timeblock be cleared by using SetLocation from CD1
        // back to R25, so these topics are checked as well.

        // Must have talked to these three about Le Serpent Rouge.
        if (state.GetTopicCount("EMILIO", "T_LE_SERPENT_ROUGE") < 1 ||
            state.GetTopicCount("MOSELY", "T_LE_SERPENT_ROUGE") < 1 ||
            state.GetTopicCount("BUTHANE", "T_LE_SERPENT_ROUGE") < 1)
        {
            return null;
        }

        // Must have talked to Mosely about Gabriel, Grace and the treasure.
        if (state.GetTopicCount("MOSELY", "T_GABRIEL") < 1 ||
            state.GetTopicCount("MOSELY", "T_MOSELY") < 1 ||
            state.GetTopicCount("MOSELY", "T_TREASURE") < 1)
        {
            return null;
        }

        return Then("210A");
    }

    private static TimeblockCompletion? Day2At10Am(GameState state)
    {
        // Must be at LBY.
        if (!At(state, "LBY"))
        {
            return null;
        }

        // Several things must be done for this timeblock to end, and all of them are
        // checked by a condition in the lobby's own action file. That triggers a cutscene
        // where Jean and Roxanne offer to make lunch, and the cutscene uses this
        // noun/verb count to say the timeblock should end.
        if (state.GetNounVerbCount("MAID", "FOLLOW") == 0)
        {
            return null;
        }

        // The one block that moves the player as well as the clock: the Chateau de Serras.
        return Then("212P", "CSE");
    }

    private static TimeblockCompletion? Day2At12Pm(GameState state)
    {
        // Must be at R25, which the final cutscene sets.
        if (!At(state, "R25"))
        {
            return null;
        }

        // Must have talked to the old lady in the cellar.
        if (state.GetNounVerbCount("OLD_LADY", "TALK") == 0)
        {
            return null;
        }

        return Then("202P");
    }

    private static TimeblockCompletion? Day2At2Pm(GameState state)
    {
        // Must be at R25.
        if (!At(state, "R25"))
        {
            return null;
        }

        // The end-of-timeblock cutscene sets this to 7, which is what says to move on. It
        // only reaches 7 once the player has asked Jean for a wake-up call, which needs
        // Larry confronted and the alarm clock solved; followed Estelle to her site, which
        // needs the case discussed with Grace and Mosely; and exhausted Montreaux, which
        // needs the identity puzzle solved.
        if (state.GetVariable("FiveMinTimer202p") != 7)
        {
            return null;
        }

        return Then("205P");
    }

    private static TimeblockCompletion? Day2At5Pm(GameState state)
    {
        // This one ends when the Gemini and Cancer sections of Le Serpent Rouge are
        // finished at the computer: the game sets the flags and changes the location to
        // HAL, which is what is caught here.
        if (!At(state, "HAL"))
        {
            return null;
        }

        if (!state.GetFlag("Gemini") || !state.GetFlag("Cancer"))
        {
            return null;
        }

        return Then("202A");
    }

    private static TimeblockCompletion? Day2At2Am(GameState state)
    {
        // The goal is to get Larry's manuscript and return to R25.
        if (!At(state, "R25"))
        {
            return null;
        }

        if (!Carrying(state, "BLOODLINE_MANUSCRIPT"))
        {
            return null;
        }

        return Then("307A");
    }

    // ---------------------------------------------------------------------------------
    // Day 3.
    // ---------------------------------------------------------------------------------

    private static TimeblockCompletion? Day3At7Am(GameState state)
    {
        // The goal is to make progress on Le Serpent Rouge, and other scripts do most of
        // the deciding: they set "End307a" and move the player to the hallway.
        if (!At(state, "HAL"))
        {
            return null;
        }

        if (!state.GetFlag("End307a"))
        {
            return null;
        }

        return Then("310A");
    }

    private static TimeblockCompletion? Day3At10Am(GameState state)
    {
        // Gabriel investigates Wilkes and the bloodline manuscript, which ends in a talk
        // with Grace. The game sets the location to R25 with the topic counts set.
        if (!At(state, "R25"))
        {
            return null;
        }

        if (state.GetTopicCount("GRACE", "T_FREEMASONS") == 0 ||
            state.GetTopicCount("GRACE", "T_WILKES") == 0 ||
            state.GetTopicCount("GRACE", "T_UNICORN") == 0 ||
            state.GetTopicCount("GRACE", "T_THRONE") == 0 ||
            state.GetTopicCount("GRACE", "T_PRINCE_JAMES_MEN") == 0 ||
            state.GetTopicCount("GRACE", "T_MANUSCRIPT") == 0)
        {
            return null;
        }

        return Then("312P");
    }

    private static TimeblockCompletion? Day3At12Pm(GameState state)
    {
        // Grace finishes more of Le Serpent Rouge and recovers the lost manuscript. Once
        // the flags are set and Grace reaches R25 a short cutscene plays, and the script
        // then moves the player to the dining room.
        if (!At(state, "DIN"))
        {
            return null;
        }

        // Not certain, but the closing cutscene appears to set this to say to move on.
        if (state.GetNounVerbCount("VIEW_OF_ORANGE_ROCK", "BINOCULARS") != 2)
        {
            return null;
        }

        return Then("303P");
    }

    private static TimeblockCompletion? Day3At3Pm(GameState state)
    {
        // Gabriel uncovers the kidnappers. The game's own scripts play the closing
        // cutscene and the movie; all that is left here is to notice the warp to R25.
        if (!At(state, "R25"))
        {
            return null;
        }

        // The only visible trace of the closing cutscene is this noun/verb count.
        if (state.GetNounVerbCount("CEILING", "INSPECT") == 0)
        {
            return null;
        }

        return Then("306P");
    }

    private static TimeblockCompletion? Day3At6Pm(GameState state)
    {
        // Grace ties up loose ends. A long video ends the timeblock and returns the player
        // to R25.
        if (!At(state, "R25"))
        {
            return null;
        }

        // Must have visited R27, to talk with Emilio and see the cutscene.
        if (state.GetLocationCount(state.Ego, "R27") == 0)
        {
            return null;
        }

        return Then("309P");
    }

    // ---------------------------------------------------------------------------------
    // The questions the rules ask, in the words the Sheep functions use.
    // ---------------------------------------------------------------------------------

    /// <summary>The Sheep <c>IsCurrentLocation</c>.</summary>
    private static bool At(GameState state, string location) =>
        string.Equals(state.Location, location, StringComparison.OrdinalIgnoreCase);

    /// <summary>The Sheep <c>WasLastLocation</c>.</summary>
    private static bool CameFrom(GameState state, string location) =>
        string.Equals(state.LastLocation, location, StringComparison.OrdinalIgnoreCase);

    /// <summary>The Sheep <c>IsActorAtLocation</c>.</summary>
    private static bool ActorAt(GameState state, string actor, string location) =>
        string.Equals(state.GetActorLocation(actor), location, StringComparison.OrdinalIgnoreCase);

    /// <summary>The Sheep <c>DoesEgoHaveInvItem</c>.</summary>
    private static bool Carrying(GameState state, string item) =>
        state.Inventory.Has(state.Ego, item);

    /// <summary>The Sheep <c>SetTime</c> and <c>SetLocationTime</c>, as a decision.</summary>
    /// <param name="code">The timeblock code to move to.</param>
    /// <param name="location">The room to open with it, or null to stay put.</param>
    /// <remarks>
    /// Every caller passes a literal from the design document, so a code that will not
    /// parse is a mistake in this file rather than anything the game did.
    /// </remarks>
    private static TimeblockCompletion Then(string code, string? location = null) =>
        Timeblock.TryParse(code, out Timeblock next)
            ? new TimeblockCompletion(next, location)
            : throw new InvalidOperationException($"'{code}' is not a timeblock code.");
}
