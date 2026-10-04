namespace GK3Reborn.Formats.Actions;

/// <summary>Repairs verified disagreements between authored state readers and writers.</summary>
internal static class StoryStateCorrections
{
    internal static void CompleteCases(string name, List<NvcAction> actions, Dictionary<string, string> cases)
    {
        for (int i = 0; i < actions.Count; i++)
        {
            NvcAction action = actions[i];
            string? shared = (Path.GetFileName(name).ToUpperInvariant(), action.Noun.ToUpperInvariant(), action.Verb.ToUpperInvariant()) switch
            {
                ("CS5_ALL.NVC", var noun, "LOOK") when noun.StartsWith("DOORS", StringComparison.Ordinal) => "GetNounVerbCount(\"DOORS\",\"LOOK\")",
                ("LER307A.NVC", "PILLARS", "TENIERS_POSTCARD_NO_TEMP") => "GetNounVerbCount(\"CAVE\",\"TENIERS_POSTCARD_NO_TEMP\")",
                ("INV_ALL.NVC", "FINGERPRINT_KIT", "LSR_ENVELOPE_INV") => "GetFlag(\"GotLEstellePrint\")",
                _ => null,
            };
            if (shared is not null && action.Case.ToUpperInvariant() is "1ST_TIME" or "OTR_TIME")
            {
                string condition = action.Noun + "_" + action.Verb + "_" + action.Case;
                cases.TryAdd(condition, shared + (action.Case.Equals("1ST_TIME", StringComparison.OrdinalIgnoreCase) ? " == 0" : " > 0"));
                actions[i] = action with { Case = condition };
            }
        }
        foreach ((string file, string condition, string expression) in MissingCases)
        {
            if (Path.GetFileName(name).Equals(file, StringComparison.OrdinalIgnoreCase))
            {
                cases.TryAdd(condition, expression);
            }
        }
    }

    private static readonly (string File, string Condition, string Expression)[] MissingCases =
    [
        ("CHU_ALL.NVC", "GABE_TALKED_MONTREAUX", "IsCurrentEgo(\"GABRIEL\") && GetTopicCount(\"MONTREAUX\",\"T_VITICULTURE\") > 0"),
        ("INV110A.NVC", "NOT_CALLED_PRINCE_JAMES", "GetNounVerbCount(\"PHONE\",\"PRINCE_JAMES_CARD\") == 0 && GetNounVerbCount(\"OTR_PHONE_1\",\"PRINCE_JAMES_CARD\") == 0 && GetNounVerbCount(\"OTR_PHONE_2\",\"PRINCE_JAMES_CARD\") == 0"),
        ("INV312P.NVC", "NOT_TRANSLATED_SUM", "!GetFlag(\"TranslatedSUM\")"),
        ("INV_ALL.NVC", "NOT_TRANSLATED", "!GetFlag(\"TranslatedAbbeTape\")"),
        ("GLB_23ALL.NVC", "1ST_TIME_GABE", "IsCurrentEgo(\"GABRIEL\") && GetNounVerbCount(\"POEM\",\"LOOK\") == 0"),
        ("GLB_23ALL.NVC", "OTR_TIME_GABE", "IsCurrentEgo(\"GABRIEL\") && GetNounVerbCount(\"POEM\",\"LOOK\") > 0"),
        ("CEM104P.NVC", "1ST_TIME_BLOCK", "GetEgoCurrentLocationCount() == 1"),
        ("RC1104P.NVC", "HEARD_ABBE_BUTHANE", "GetNounVerbCount(\"OFFICE_WINDOW\",\"WALK\") > 0"),
        ("LBY_ALL.NVC", "NOT_SEEN_REGISTER", "GetNounVerbCount(\"REGISTER\",\"READ\") == 0"),
        ("MA3303P.NVC", "ALL_DIALOGUE_DONE", "GetTopicCount(\"ABBE\",\"T_PRIORY\") > 0 && GetTopicCount(\"ABBE\",\"T_THRONE\") > 0 && GetTopicCount(\"ABBE\",\"T_TREASURE_MA3\") > 0 && GetTopicCount(\"ABBE\",\"T_MONTREAUX\") > 0 && GetTopicCount(\"ABBE\",\"T_EXCAVATIONS\") > 0"),
    ];

    internal static string Apply(string text, string name)
    {
        if (PulleyFiles.Contains(Path.GetFileName(name), StringComparer.OrdinalIgnoreCase))
        {
            foreach (string noun in new[] { "PULLEY", "PULLEY_GE", "PULLEY_WB", "WB_PULLEY" })
            {
                foreach (string function in new[] { "GetNounVerbCount", "IncNounVerbCount" })
                {
                    text = text.Replace($"{function}(\"{noun}\",\"LOOK\")",
                        $"{function}(\"DW_PULLEY\",\"LOOK\")", StringComparison.Ordinal);
                }
            }
        }
        // File-scoped literal replacements preserve custom conditions and scripts.
        // Do not make global aliases: similarly named counters often mean different things.
        foreach ((string file, string before, string after) in Replacements)
        {
            if (Path.GetFileName(name).Equals(file, StringComparison.OrdinalIgnoreCase))
            {
                text = text.Replace(before, after, StringComparison.Ordinal);
            }
        }
        return text;
    }

    private static readonly string[] PulleyFiles =
    [
        "DU1210A.NVC", "DU2210A.NVC", "KIT_ALL.NVC", "R21210A.NVC",
        "R23210A.NVC", "R23310A.NVC", "R25_ALL.NVC", "R27210A.NVC",
    ];

    private static readonly (string File, string Before, string After)[] Replacements =
    [
        // First-time cases read the action noun, but these scripts wrote a misspelling.
        ("CHU110A.NVC", "IncNounVerbCount(\"FOUR_ANGLES_WORDS\",\"LOOK\")", "IncNounVerbCount(\"FOUR_ANGELS_WORDS\",\"LOOK\")"),
        ("HAL104P.NVC", "IncNounVerbCount(\"BUCHELLIS_DOOR\",\"GLASS\")", "IncNounVerbCount(\"BUCHELLI_DOOR\",\"GLASS\")"),
        // LER_ALL's READ action records LOOK; the parking area's description reads READ.
        ("PL4_ALL.NVC", "GetNounVerbCount(\"LERMITAGE_SIGN\",\"READ\")", "GetNounVerbCount(\"LERMITAGE_SIGN\",\"LOOK\")"),
        // SYDNEY completes these signs without the obsolete Done prefix.
        ("MA3_3ALL.NVC", "GetFlag(\"DoneLibra\")", "GetFlag(\"Libra\")"),
        ("MA3_3ALL.NVC", "GetFlag(\"DoneScorpio\")", "GetFlag(\"Scorpio\")"),
        ("MA3_3ALL.NVC", "GetFlag(\"DoneOphiuchus\")", "GetFlag(\"Ophiuchus\")"),
        // LMB202A gives the manuscript during digging; there is no separate pickup.
        ("CDB_ALL.NVC", "GetNounVerbCount(\"PLASTIC_BAG_IN_HOLE\",\"PICKUP\")", "GetNounVerbCount(\"FRESH_DIRT\",\"SHOVEL\")"),
        ("LHE_ALL.NVC", "GetNounVerbCount(\"PLASTIC_BAG_IN_HOLE\",\"PICKUP\")", "GetNounVerbCount(\"FRESH_DIRT\",\"SHOVEL\")"),
        ("LMB_ALL.NVC", "GetNounVerbCount(\"PLASTIC_BAG_IN_HOLE\",\"PICKUP\")", "GetNounVerbCount(\"FRESH_DIRT\",\"SHOVEL\")"),
        // R25202P records the joint discussion, and T_BOOK becomes 2 when it ends.
        ("CHU_ALL.NVC", "(GetTopicCount(\"GRACE\",\"T_BOOK\") == 1) && (GetTopicCount(\"GRACE\",\"T_HOLY_GRAIL\") == 1)",
            "(GetTopicCount(\"GRACE_N_MOSE\",\"T_BOOK\") > 1) && (GetTopicCount(\"GRACE_N_MOSE\",\"T_HOLY_GRAIL\") == 1)"),
        ("CHU_ALL.NVC", "GetFlag(\"TalkedMontreaux\")", "(GetTopicCount(\"MONTREAUX\",\"T_VITICULTURE\") > 0)"),
        // The pamphlet pickup script increments a count, never a GotPamphlet flag.
        ("ROQ_ALL.NVC", "GetFlag(\"GotPamphlet\")", "GetNounVerbCount(\"CHURCH_PAMPHLET\",\"PICKUP\")"),
        // Two hint rules misspell conditions already defined in this same file.
        ("CHU_ALL.NVC", "THINK,                DONE_CANCER_NOT_LEO,", "THINK,                G_DONE_CANCER_NOT_LEO,"),
        ("CHU_ALL.NVC", "LOOK,                 G_DONE_PISCES_NOT_ARIES,", "LOOK,                 GOT_LSR_DONE_PISCES_NOT_ARIES,"),
        ("GLB_23ALL.NVC", "StartVoiceOver(\"10O3U44SW1\",2);", "StartVoiceOver(\"10O3U44SW1\",2); IncNounVerbCount(\"POEM\",\"LOOK\");"),
        ("GLB_23ALL.NVC", "StartVoiceOver(\"10O3U44SX1\",1);", "StartVoiceOver(\"10O3U44SX1\",1); IncNounVerbCount(\"POEM\",\"LOOK\");"),
        // CheapSuit plays the completed search but never credits the repeat-action case.
        ("R31210A.NVC", "wait CallSheep(\"r31_all\",\"CheapSuit\");", "wait CallSheep(\"r31_all\",\"CheapSuit\"); IncNounVerbCount(\"CHEAP_SUITCASE_IN_CLOSET\",\"SEARCH\");"),
        ("WOD312P.NVC", "GetFlag(\"DugLerHole\")", "GetNounVerbCount(\"CENTER_MARK\",\"SHOVEL\")"),
        // The original OR chain escapes the asked-priory prerequisite.
        ("MA3303P.NVC", "GetTopicCount(\"ABBE\",\"T_PRIORY\") && (GetTopicCount(\"ABBE\",\"T_THRONE\") == 0) || (GetTopicCount(\"ABBE\",\"T_TREASURE_ma3\") == 0)  || (GetTopicCount(\"ABBE\",\"T_MONTREAUX\") == 0) || (GetTopicCount(\"ABBE\",\"T_EXCAVATIONS\") == 0)",
            "GetTopicCount(\"ABBE\",\"T_PRIORY\") && ((GetTopicCount(\"ABBE\",\"T_THRONE\") == 0) || (GetTopicCount(\"ABBE\",\"T_TREASURE_ma3\") == 0) || (GetTopicCount(\"ABBE\",\"T_MONTREAUX\") == 0) || (GetTopicCount(\"ABBE\",\"T_EXCAVATIONS\") == 0))"),
        // Inventory rules must use their own conditions, not those of a distant room.
        ("INV_ALL.NVC", "THINK,                CONNECTED_ESTELLE_LSR,", "THINK,                CONNECTED_ESTELLE_LSR_INV,"),
        ("INV_ALL.NVC", "THINK,            G_DONE_TAURUS_NOT_GEMINI,", "THINK,            G_DONE_TAURUS_NOT_GEMINI_INV,"),
    ];
}
