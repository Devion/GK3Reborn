using GK3Reborn.Formats.Actions;
using GK3Reborn.Game;
using GK3Reborn.UI;
using Xunit;

namespace GK3Reborn.Tests.Game;

public sealed class StoryStateCorrectionTests
{
    private static ActionResolver Rules(GameState state, params string[] names)
    {
        string root = ScriptCorpusTests.ContentRoot();
        var resolver = new ActionResolver(new Gk3SheepApi(state));
        foreach (string name in names)
        {
            resolver.Add(NvcFile.Parse(File.ReadAllText(Path.Combine(root, "actions", name)), name, new()));
        }
        return resolver;
    }

    [Theory]
    [InlineData("CHU110A.NVC", "FOUR_ANGELS_WORDS", "LOOK", "1ST_TIME", null)]
    [InlineData("HAL104P.NVC", "BUCHELLI_DOOR", "GLASS", "1ST_TIME", "OTR_TIME")]
    [InlineData("R21210A.NVC", "PULLEY_WB", "LOOK", "1ST_TIME_PULLEY", "OTR_TIME_PULLEY")]
    [InlineData("KIT_ALL.NVC", "PULLEY_GE", "LOOK", "1ST_TIME_PULLEY", "OTR_TIME_PULLEY")]
    [InlineData("DU1210A.NVC", "PULLEY_GE", "LOOK", "1ST_TIME_PULLEY", "OTR_TIME_PULLEY")]
    [InlineData("DU2210A.NVC", "PULLEY_WB", "LOOK", "1ST_TIME_PULLEY", "OTR_TIME_PULLEY")]
    [InlineData("R23210A.NVC", "PULLEY_WB", "LOOK", "1ST_TIME_PULLEY", "OTR_TIME_PULLEY")]
    [InlineData("R23310A.NVC", "PULLEY_WB", "LOOK", "1ST_TIME_PULLEY", "OTR_TIME_PULLEY")]
    [InlineData("R25_ALL.NVC", "PULLEY_WB", "LOOK", "1ST_TIME_PULLEY", "OTR_TIME_PULLEY")]
    [InlineData("R27210A.NVC", "DW_PULLEY", "LOOK", "1ST_TIME", "OTR_TIME")]
    [InlineData("GLB_23ALL.NVC", "POEM", "LOOK", "1ST_TIME_GABE", "OTR_TIME_GABE")]
    public void Performing_an_action_changes_its_own_first_time_rule(string file, string noun, string verb, string first, string? repeated)
    {
        var state = new GameState();
        ActionResolver resolver = Rules(state, file);
        NvcAction action = Assert.IsType<NvcAction>(resolver.Find(noun, verb));
        Assert.Equal(first, action.Case);
        // Calls into presentation scripts are irrelevant here; the authored inline count
        // is run through the real action runner, rather than supplied by the test.
        Assert.True(new ActionRunner(new Gk3SheepApi(state)).Run(action).Ran);
        Assert.Equal(repeated, resolver.Find(noun, verb)?.Case);
    }

    [Theory]
    [InlineData("FOUR_ANGLES_WORDS", "FOUR_ANGELS_WORDS", "LOOK")]
    [InlineData("BUCHELLIS_DOOR", "BUCHELLI_DOOR", "GLASS")]
    [InlineData("WB_PULLEY", "DW_PULLEY", "LOOK")]
    [InlineData("PULLEY_WB", "DW_PULLEY", "LOOK")]
    [InlineData("PULLEY_GE", "DW_PULLEY", "LOOK")]
    public void Old_counter_spellings_recover_without_crossing_actors_or_adding_counts(string oldName, string newName, string verb)
    {
        var state = new GameState();
        state.SetNounVerbCount(oldName, verb, 2);
        state.SetNounVerbCount(newName, verb, 1);
        state.Restore(state.Capture());
        state.Restore(state.Capture());
        Assert.Equal(2, state.GetNounVerbCount(newName, verb));
        Assert.Equal(0, state.GetNounVerbCount("GRACE", newName, verb));
    }

    [Fact]
    public void Reading_the_hermitage_sign_updates_the_parking_area_description()
    {
        var state = new GameState();
        ActionResolver inside = Rules(state, "LER_ALL.NVC");
        ActionResolver outside = Rules(state, "PL4_ALL.NVC");
        Assert.Equal("GABE_ALL", outside.Find("LERMITAGE_SIGN", "LOOK")?.Case);
        new ActionRunner(new Gk3SheepApi(state)).Run(inside.Find("LERMITAGE_SIGN", "READ")!);
        Assert.Equal("GABE_KNOWS_LERMITAGE", outside.Find("LERMITAGE_SIGN", "LOOK")?.Case);
    }

    [Fact]
    public void Tower_descriptions_follow_the_puzzle_completion_flags()
    {
        var state = new GameState { Ego = "GRACE" };
        ActionResolver resolver = Rules(state, "MA3_3ALL.NVC");
        Assert.Equal("G_NOT_DONE_LIBRA", resolver.Find("TOWER_WALLS", "LOOK", "GRACE")?.Case);
        state.SetFlag("Libra");
        Assert.Equal("G_DONE_LIBRA_NOT_SCORPIO", resolver.Find("TOWER_WALLS", "LOOK", "GRACE")?.Case);
        state.SetFlag("Scorpio");
        Assert.Equal("G_DONE_SCORPIO", resolver.Find("TOWER_WALLS", "LOOK", "GRACE")?.Case);
        Assert.Null(resolver.Find("MT_CARDOU_IN_VIEW", "LOOK", "GRACE"));
        state.SetFlag("Ophiuchus");
        Assert.NotNull(resolver.Find("MT_CARDOU_IN_VIEW", "LOOK", "GRACE"));
    }

    [Fact]
    public void Every_crypt_door_uses_the_shared_description_count()
    {
        var state = new GameState();
        ActionResolver resolver = Rules(state, "CS5_ALL.NVC");
        for (int door = 1; door <= 9; door++)
        {
            string noun = $"DOORS{door:00}";
            Assert.EndsWith("1ST_TIME", resolver.Find(noun, "LOOK")!.Case, StringComparison.Ordinal);
        }
        new ActionRunner(new Gk3SheepApi(state)).Run(resolver.Find("DOORS01", "LOOK")!);
        for (int door = 1; door <= 9; door++)
        {
            Assert.EndsWith("OTR_TIME", resolver.Find($"DOORS{door:00}", "LOOK")!.Case, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Comparing_the_postcard_at_the_pillars_is_credited_by_the_original_script()
    {
        var state = new GameState { Ego = "GRACE", Timeblock = new Timeblock(3, 7, false) };
        ActionResolver resolver = Rules(state, "LER307A.NVC");
        var api = new Gk3SheepApi(state);
        var host = new ScriptHost(api);
        foreach (string name in new[] { "LER_ALL", "LER307A" })
        {
            string path = Path.Combine(ScriptCorpusTests.ContentRoot(), "scripts", name + ".SHP");
            host.Add(GK3Reborn.Sheep.SheepScriptFile.Parse(File.ReadAllBytes(path), name + ".SHP"));
        }
        NvcAction first = resolver.Find("PILLARS", "TENIERS_POSTCARD_NO_TEMP", "GRACE")!;
        Assert.EndsWith("1ST_TIME", first.Case, StringComparison.Ordinal);
        Assert.True(new ActionRunner(api).Run(first).Ran);
        Assert.EndsWith("OTR_TIME", resolver.Find("PILLARS", "TENIERS_POSTCARD_NO_TEMP", "GRACE")!.Case, StringComparison.Ordinal);
        Assert.Equal("OTR_TIME", resolver.Find("CAVE", "TENIERS_POSTCARD_NO_TEMP", "GRACE")!.Case);
    }

    [Fact]
    public void Searching_the_suitcase_reaches_its_repeat_response()
    {
        var state = new GameState { Timeblock = new Timeblock(2, 10, false) };
        state.SetVariable("MaidCleaningPath210a", 8);
        ActionResolver resolver = Rules(state, "R31210A.NVC");
        var api = new Gk3SheepApi(state);
        var host = new ScriptHost(api);
        string path = Path.Combine(ScriptCorpusTests.ContentRoot(), "scripts", "R31_ALL.SHP");
        host.Add(GK3Reborn.Sheep.SheepScriptFile.Parse(File.ReadAllBytes(path), "R31_ALL.SHP"));
        Assert.True(new ActionRunner(api).Run(resolver.Find("CHEAP_SUITCASE_IN_CLOSET", "SEARCH")!).Ran);
        Assert.Equal("OTR_TIME", resolver.Find("CHEAP_SUITCASE_IN_CLOSET", "SEARCH")?.Case);
    }

    [Fact]
    public void Taking_the_pamphlet_enables_the_Roque_Negre_thought()
    {
        var state = new GameState { Ego = "GRACE" };
        var api = new Gk3SheepApi(state);
        var host = new ScriptHost(api);
        string path = Path.Combine(ScriptCorpusTests.ContentRoot(), "scripts", "CHU_ALL.SHP");
        host.Add(GK3Reborn.Sheep.SheepScriptFile.Parse(File.ReadAllBytes(path), "CHU_ALL.SHP"));
        ActionResolver resolver = Rules(state, "ROQ_ALL.NVC");
        Assert.Null(resolver.Find("ROQUE_NEGRE", "THINK", "GRACE"));
        host.Run("CHU_ALL", "G_Get_CHU_Pamphlet");
        Assert.NotNull(resolver.Find("ROQUE_NEGRE", "THINK", "GRACE"));
    }

    [Fact]
    public void Church_descriptions_use_the_completed_discussions()
    {
        var state = new GameState();
        ActionResolver resolver = Rules(state, "CHU_ALL.NVC");
        Assert.Equal("NOT_GABE_TALKED_MONTREAUX", resolver.Find("GRAPEVINE_MOTIF", "LOOK")?.Case);
        state.SetTopicCount("MONTREAUX", "T_VITICULTURE", 1);
        Assert.Equal("GABE_TALKED_MONTREAUX", resolver.Find("GRAPEVINE_MOTIF", "LOOK")?.Case);
        state.SetTopicCount("GRACE_N_MOSE", "T_HOLY_GRAIL", 1);
        state.SetTopicCount("GRACE_N_MOSE", "T_BOOK", 2);
        Assert.Equal("GABE_AFTER_HGHB_DISCUSSION", resolver.Find("ST_MAGDALEN_STATUE", "LOOK")?.Case);
        state.Ego = "GRACE";
        Assert.NotEqual("GABE_AFTER_HGHB_DISCUSSION", resolver.Find("ST_MAGDALEN_STATUE", "LOOK", "GRACE")?.Case);
    }

    [Fact]
    public void Tower_departure_distinguishes_unstarted_partial_and_complete_discussions()
    {
        var state = new GameState();
        ActionResolver resolver = Rules(state, "MA3303P.NVC");
        Assert.Null(resolver.Find("TOWER_DOOR", "OPEN"));
        state.SetTopicCount("ABBE", "T_PRIORY", 1);
        Assert.Equal("ASKED_PRIORY_NOT_DONE_DIAL", resolver.Find("TOWER_DOOR", "OPEN")?.Case);
        foreach (string topic in new[] { "T_THRONE", "T_TREASURE_MA3", "T_MONTREAUX", "T_EXCAVATIONS" })
        {
            state.SetTopicCount("ABBE", topic, 1);
        }
        Assert.Equal("ALL_DIALOGUE_DONE", resolver.Find("TOWER_DOOR", "OPEN")?.Case);
    }

    [Fact]
    public void Village_response_uses_the_overheard_cemetery_conversation()
    {
        var state = new GameState();
        ActionResolver resolver = Rules(state, "RC1104P.NVC");
        Assert.Equal("TIME_BLOCK", resolver.Find("VAN", "LOOK")?.Case);
        state.SetNounVerbCount("OFFICE_WINDOW", "WALK", 1);
        Assert.Equal("HEARD_ABBE_BUTHANE", resolver.Find("VAN", "LOOK")?.Case);
    }

    [Fact]
    public void Cemetery_setup_runs_only_on_the_first_visit_of_the_timeblock()
    {
        var state = new GameState { Location = "CEM", Timeblock = new Timeblock(1, 4, true) };
        ActionResolver resolver = Rules(state, "CEM104P.NVC");
        state.SetLocationCount("GABRIEL", "CEM", 1);
        Assert.NotNull(resolver.Find("SCENE", "ENTER"));
        state.SetLocationCount("GABRIEL", "CEM", 2);
        Assert.Null(resolver.Find("SCENE", "ENTER"));
    }

    [Fact]
    public void Register_first_look_condition_is_available_after_day_one()
    {
        var state = new GameState { Timeblock = new Timeblock(2, 2, true) };
        ActionResolver resolver = Rules(state, "LBY_ALL.NVC");
        Assert.Equal("NOT_SEEN_REGISTER", resolver.Find("REGISTER", "LOOK")?.Case);
        state.SetNounVerbCount("REGISTER", "READ", 1);
        Assert.Equal("GABE_ALL", resolver.Find("REGISTER", "LOOK")?.Case);
    }

    [Theory]
    [InlineData("INV312P.NVC", "LSR_LEO", "THINK", "TranslatedSUM")]
    [InlineData("INV_ALL.NVC", "ABBE_TAPE", "LOOK", "TranslatedAbbeTape")]
    public void Translation_hints_stop_when_Sidney_records_the_translation(string file, string noun, string verb, string flag)
    {
        var state = new GameState { Ego = "GRACE" };
        ActionResolver resolver = Rules(state, file);
        Assert.NotNull(resolver.Find(noun, verb, "GRACE"));
        state.SetFlag(flag);
        Assert.Null(resolver.Find(noun, verb, "GRACE"));
    }

    [Theory]
    [InlineData("PHONE")]
    [InlineData("OTR_PHONE_1")]
    [InlineData("OTR_PHONE_2")]
    public void The_James_card_hint_accepts_all_three_telephone_counters(string phone)
    {
        var state = new GameState();
        ActionResolver resolver = Rules(state, "INV110A.NVC");
        Assert.NotNull(resolver.Find("PRINCE_JAMES_CARD", "THINK"));
        state.SetNounVerbCount(phone, "PRINCE_JAMES_CARD", 1);
        Assert.Null(resolver.Find("PRINCE_JAMES_CARD", "THINK"));
    }

    [Fact]
    public void Lifting_the_envelope_print_disables_both_inventory_directions()
    {
        var state = new GameState { Ego = "GRACE", Timeblock = new Timeblock(2, 5, true) };
        state.Screens.Show(new Screen(ScreenKind.InventoryInspect, "LSR_ENVELOPE_INV"));
        ActionResolver resolver = Rules(state, "INV_ALL.NVC");
        Assert.Contains("ShowFingerprintInterface", resolver.Find("FINGERPRINT_KIT", "LSR_ENVELOPE_INV", "GRACE")!.Script);
        FingerprintKit.Lift("LSR_ENVELOPE_INV", state, ScoreEvents.Open());
        Assert.DoesNotContain("ShowFingerprintInterface", resolver.Find("FINGERPRINT_KIT", "LSR_ENVELOPE_INV", "GRACE")!.Script);
        Assert.DoesNotContain("ShowFingerprintInterface", resolver.Find("LSR_ENVELOPE_INV", "FINGERPRINT_KIT", "GRACE")!.Script);
    }

    [Theory]
    [InlineData("CDB_ALL.NVC", "LARRYS_HOUSE")]
    [InlineData("LHE_ALL.NVC", "HOUSE")]
    [InlineData("LMB_ALL.NVC", "FRESH_DIRT")]
    public void Manuscript_descriptions_follow_the_digging_script(string file, string noun)
    {
        var state = new GameState { Timeblock = new Timeblock(3, 10, false) };
        var api = new Gk3SheepApi(state);
        var host = new ScriptHost(api);
        string path = Path.Combine(ScriptCorpusTests.ContentRoot(), "scripts", "LMB202A.SHP");
        host.Add(GK3Reborn.Sheep.SheepScriptFile.Parse(File.ReadAllBytes(path), "LMB202A.SHP"));
        host.Run("LMB202A", "GabeDig");
        Assert.True(state.Inventory.Has("GABRIEL", "BLOODLINE_MANUSCRIPT"));
        Assert.Equal("GOT_MANUSCRIPT", Rules(state, file).Find(noun, "LOOK")?.Case);
    }
}
