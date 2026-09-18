namespace GK3Reborn.Game;

/// <summary>
/// The places the driving map's hint button flashes, by timeblock and story state (the retail hint manager).
/// </summary>
public static class DrivingHints
{
    /// <summary>How long the hinted places flash, in seconds (the retail 2.2).</summary>
    public const double Seconds = 2.2;

    /// <summary>The map sprites worth a visit now; empty when there is nothing to hint.</summary>
    /// <param name="story">The game.</param>
    /// <returns>Sprite names such as <c>dm_plo</c>.</returns>
    public static IReadOnlySet<string> For(GameState story)
    {
        ArgumentNullException.ThrowIfNull(story);

        HashSet<string> places = new(StringComparer.OrdinalIgnoreCase);
        void Flash(string code) => places.Add("dm_" + code.ToLowerInvariant());
        bool Has(string item) => story.Inventory.Has(story.Ego, item);

        int Count(string noun, string verb) => story.GetNounVerbCount(noun, verb);
        int Topic(string noun, string topic) => story.GetTopicCount(noun, topic);
        bool Train() => (Topic("MARCIE", "T_TRAIN_FROM_NAPLES") > 0 || Count("ARRIVALS_IN_CU", "THINK") > 0) && Count("TAXI_DRIVER", "WALLET") > 0;

        // The retail engine's own timeblock order (sub_47DFFF), which counts 202A after 205P.
        switch (Story.TimeblockRules.Order(story.Timeblock) + 1)
        {
            case 3: // 102P
                if (Count("WILKES", DrivingMap.Follow) == 0 || Count("BUTHANE", DrivingMap.Follow) == 0)
                {
                    Flash("PLO");
                }

                if (!Train() && Topic("LARRY", "T_TEMPLARS") == 0)
                {
                    Flash("LHE");
                    Flash("TR1");
                }

                break;

            case 4: // 104P
            {
                bool binoculars = Count("VIEW_OF_LHOMME_MORE", "BINOCULARS") > 0;
                bool train = Train();
                bool larry = Topic("LARRY", "T_TEMPLARS") > 0;
                bool wilkes = Topic("WILKES", "T_INTRODUCE") > 0;

                if (!binoculars || !wilkes)
                {
                    Flash("PLO");
                }
                if (!train)
                {
                    Flash("TR1");
                }
                if (!larry)
                {
                    Flash("LHE");
                }
                if (binoculars && train && larry && wilkes)
                {
                    Flash("RLC");
                }
                break;
            }

            case 5: // 106P
            {
                int men = story.GetVariable("TwoMenState");
                bool followed = Count("TWO_MEN", DrivingMap.Follow) > 0;

                if (Count("HIDING_STONE", "HIDE") == 0)
                {
                    Flash("RLC");
                }
                if (men == 4 && !followed)
                {
                    Flash("PLO");
                }
                if (men == 6)
                {
                    Flash("RLC");
                }
                else if (followed)
                {
                    Flash("LHE");
                }
                break;
            }

            case 9: // 202P
            {
                if (Count("HANGER", "PICKUP") == 0)
                {
                    Flash("RLC");
                }

                bool knees = Count("KNEE_INDENTS", "THINK") > 1;
                if (!knees)
                {
                    Flash("ARM");
                }

                bool larry = story.GetVariable("FiveMinTimer202p") > 5;
                if (!larry)
                {
                    Flash("LHE");
                }

                if (story.GetActorLocation("ESTELLE").Equals("MAP", StringComparison.OrdinalIgnoreCase) && Count("ESTELLE", DrivingMap.Follow) == 0)
                {
                    Flash("PLO");
                }

                if (larry && Topic("JEAN", "T_WAKEUPCALL") == 0)
                {
                    Flash("RLC");
                }
                if (knees && larry && !Has("FAKE_ID_REPORTER") && !Has("FAKE_ID_NYT_REP"))
                {
                    Flash("RLC");
                }

                bool talked = Topic("GRACE_N_MOSE", "T_ABBE") > 1 && Topic("GRACE_N_MOSE", "T_BLOODLINE") > 1 &&
                              Topic("GRACE_N_MOSE", "T_HOLY_GRAIL") > 0 && Topic("GRACE_N_MOSE", "T_PRIORY") > 1 &&
                              Topic("GRACE_N_MOSE", "T_TREASURE") > 0;

                if (Topic("MONTREAUX", "T_VITICULTURE") == 3)
                {
                    Flash("RLC");
                }
                else if (talked)
                {
                    Flash("CSE");
                }
                break;
            }

            case 11: // 202A
                Flash(Has("BLOODLINE_MANUSCRIPT") ? "RLC" : "PLO");
                break;

            case 12: // 307A
            {
                if (!Has("COORDINATE_FIXING_DEVICE"))
                {
                    Flash("RLC");
                }

                bool ermitage = story.GetFlag("UseCoordLER") && Count("CLUE_NOTE_1", "PICKUP") > 0;
                if (!ermitage)
                {
                    Flash("LER");
                }

                bool sidney = story.GetFlag("TempleFloorplan") && story.GetFlag("LockedHexagram") && story.GetFlag("SavedArcadiaText") &&
                              story.GetFlag("PlacedWalls");
                if (!sidney || ermitage)
                {
                    Flash("RLC");
                }
                break;
            }

            case 13: // 310A
            {
                bool body = story.GetLocationCount(story.Ego, "WDB") > 0;
                bool larry = Topic("LARRY", "T_MANUSCRIPT") > 0;

                if (!body)
                {
                    Flash("LHM");
                }
                if (!larry)
                {
                    Flash("LHE");
                }
                if (body && larry)
                {
                    Flash("RLC");
                }
                break;
            }

            case 14: // 312P
            {
                if (Count("CLUE_NOTE_2", "PICKUP") == 0)
                {
                    Flash("MCB");
                }
                if (Count("CLUE_NOTE_4", "PICKUP") == 0)
                {
                    Flash("BEC");
                }

                bool note = Count("CLUE_NOTE_3", "PICKUP") > 0;
                if (story.GetFlag("MarkedTheSite") && !note)
                {
                    Flash("TRE");
                }

                bool rock = Count("VIEW_OF_ORANGE_ROCK", "BINOCULARS") > 0;
                if (note && !rock)
                {
                    Flash("PLO");
                }
                if (rock)
                {
                    Flash("RLC");
                }
                if (note && !story.GetActorLocation("PRINCE_JAMES").Equals("BET", StringComparison.OrdinalIgnoreCase))
                {
                    Flash("RLC");
                }

                if (!(story.GetFlag("MarkedTheSite") && story.GetFlag("PlacedTempleDivisions") && story.GetFlag("ArcadiaComplete") &&
                      story.GetFlag("PlacedSerpent")))
                {
                    Flash("RLC");
                }

                if (note && !Has("BINOCULARS"))
                {
                    Flash("WOD");
                }
                break;
            }

            case 15: // 303P
                if (Count("MANUSCRIPT_IN_HOLE", "PICKUP") == 0)
                {
                    Flash("BMB");
                }
                Flash(story.GetLocationCount(story.Ego, "BET") > 0 ? "CSE" : "RLC");
                break;
        }

        return places;
    }
}
