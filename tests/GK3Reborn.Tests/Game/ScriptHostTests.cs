using GK3Reborn.Game;
using GK3Reborn.Sheep;
using GK3Reborn.Tests.Sheep;
using Xunit;

namespace GK3Reborn.Tests.Game;

public sealed class InventoryTests
{
    [Fact]
    public void Items_belong_to_a_character_not_to_the_game()
    {
        // GK3 switches between Gabriel and Grace, and much of the action logic turns on
        // which of them is holding something.
        var inventory = new Inventory();
        inventory.Add("GABRIEL", "MOPED_KEYS");

        Assert.True(inventory.Has("GABRIEL", "MOPED_KEYS"));
        Assert.False(inventory.Has("GRACE", "MOPED_KEYS"));
    }

    [Fact]
    public void Names_are_case_insensitive()
    {
        var inventory = new Inventory();
        inventory.Add("Gabriel", "Moped_Keys");

        Assert.True(inventory.Has("GABRIEL", "MOPED_KEYS"));
        Assert.True(inventory.Has("gabriel", "moped_keys"));
    }

    [Fact]
    public void Removing_reports_whether_they_had_it()
    {
        var inventory = new Inventory();
        inventory.Add("GABRIEL", "TAPE");

        Assert.True(inventory.Remove("GABRIEL", "TAPE"));
        Assert.False(inventory.Remove("GABRIEL", "TAPE"));
        Assert.False(inventory.Has("GABRIEL", "TAPE"));
    }

    [Fact]
    public void Listing_is_stable()
    {
        var inventory = new Inventory();
        inventory.Add("GABRIEL", "ZEBRA");
        inventory.Add("GABRIEL", "ALPHA");

        Assert.Equal(["ALPHA", "ZEBRA"], inventory.ItemsOf("GABRIEL"));
    }

    [Fact]
    public void Inventory_is_part_of_the_comparable_state()
    {
        var state = new GameState();
        string before = state.ComputeHash();

        state.Inventory.Add("GABRIEL", "MOPED_KEYS");

        Assert.NotEqual(before, state.ComputeHash());
    }
}

public sealed class ScriptHostTests
{
    /// <summary>Builds a script whose single function calls another script.</summary>
    private static SheepScriptFile Caller(string name, string targetScript, string targetFunction) =>
        TestScripts.Build(name, builder =>
        {
            builder.Import("CallSheep", 0, 3, 3);
            int script = builder.String(targetScript);
            int function = builder.String(targetFunction);

            builder.Function("Main$")
                .Op(SheepOpcode.PushS, script)
                .Op(SheepOpcode.GetString)
                .Op(SheepOpcode.PushS, function)
                .Op(SheepOpcode.GetString)
                .Op(SheepOpcode.PushI, 2)
                .Op(SheepOpcode.CallSysFunctionV, 0)
                .Op(SheepOpcode.Pop)
                .Op(SheepOpcode.ReturnV);
        });

    /// <summary>Builds a script whose single function sets a flag.</summary>
    private static SheepScriptFile Callee(string name, string flag) =>
        TestScripts.Build(name, builder =>
        {
            builder.Import("SetFlag", 0, 3);
            int offset = builder.String(flag);

            builder.Function("Target$")
                .Op(SheepOpcode.PushS, offset)
                .Op(SheepOpcode.GetString)
                .Op(SheepOpcode.PushI, 1)
                .Op(SheepOpcode.CallSysFunctionV, 0)
                .Op(SheepOpcode.Pop)
                .Op(SheepOpcode.ReturnV);
        });

    [Fact]
    public void A_script_can_call_into_another_script()
    {
        // CallSheep appears 640 times in the corpus; without it the VM cannot follow the
        // game's control flow at all.
        var state = new GameState();
        var host = new ScriptHost(new Gk3SheepApi(state));

        host.Add(Caller("LBY.SHP", "LBY102P", "Target$"));
        host.Add(Callee("LBY102P.SHP", "CalledThrough"));

        host.Run("LBY.SHP", "Main$");

        Assert.True(state.GetFlag("CalledThrough"));
        Assert.Equal(["LBY:Main$", "LBY102P:Target$"], host.CallStackTrace);
    }

    [Fact]
    public void Calling_a_script_that_is_not_loaded_is_reported_rather_than_ignored()
    {
        var host = new ScriptHost(new Gk3SheepApi(new GameState()));
        host.Add(Caller("LBY.SHP", "NOT_LOADED", "Target$"));

        host.Run("LBY.SHP", "Main$");

        Assert.Contains(host.Diagnostics.Items, d => d.Code == "GK3R3400");
    }

    [Fact]
    public void Scripts_that_call_each_other_in_a_cycle_are_bounded()
    {
        // The data does call in circles, so this has to terminate rather than recurse
        // until the stack gives out.
        var host = new ScriptHost(new Gk3SheepApi(new GameState()), maxDepth: 8);

        host.Add(Caller("A.SHP", "B", "Main$"));
        host.Add(Caller("B.SHP", "A", "Main$"));

        host.Run("A.SHP", "Main$");

        Assert.Contains(host.Diagnostics.Items, d => d.Code == "GK3R3401");
    }

    [Fact]
    public void Script_names_resolve_with_or_without_an_extension()
    {
        var state = new GameState();
        var host = new ScriptHost(new Gk3SheepApi(state));

        host.Add(Callee("LBY102P.SHP", "Reached"));
        host.Run("lby102p", "target$");

        Assert.True(state.GetFlag("Reached"));
    }

    [Fact]
    public void Inventory_functions_reach_the_state()
    {
        var state = new GameState();
        var api = new Gk3SheepApi(state);
        _ = new ScriptHost(api);

        api.Invoke("EgoTakeInvItem", [SheepValue.FromString("MOPED_KEYS")]);
        Assert.Equal(1, api.Invoke("DoesEgoHaveInvItem", [SheepValue.FromString("moped_keys")]).AsInt());

        api.Invoke("EgoLoseInvItem", [SheepValue.FromString("MOPED_KEYS")]);
        Assert.Equal(0, api.Invoke("DoesEgoHaveInvItem", [SheepValue.FromString("MOPED_KEYS")]).AsInt());
    }

    [Fact]
    public void Combining_items_consumes_both_sources()
    {
        // The puzzle semantics matter: leaving the sources in place would let a player
        // combine the same pair repeatedly.
        var state = new GameState();
        var api = new Gk3SheepApi(state);
        _ = new ScriptHost(api);

        state.Inventory.Add("GABRIEL", "TAPE");
        state.Inventory.Add("GABRIEL", "RECORDER");

        api.Invoke("CombineInvItems",
            [SheepValue.FromString("TAPE"), SheepValue.FromString("RECORDER"), SheepValue.FromString("RECORDING")]);

        Assert.False(state.Inventory.Has("GABRIEL", "TAPE"));
        Assert.False(state.Inventory.Has("GABRIEL", "RECORDER"));
        Assert.True(state.Inventory.Has("GABRIEL", "RECORDING"));
    }

    [Fact]
    public void Ego_specific_inventory_follows_who_the_player_is()
    {
        var state = new GameState();
        var api = new Gk3SheepApi(state);
        _ = new ScriptHost(api);

        api.Invoke("GabeTakeInvItem", [SheepValue.FromString("PASSPORT")]);

        Assert.Equal(1, api.Invoke("DoesEgoHaveInvItem", [SheepValue.FromString("PASSPORT")]).AsInt());

        state.Ego = "GRACE";
        Assert.Equal(0, api.Invoke("DoesEgoHaveInvItem", [SheepValue.FromString("PASSPORT")]).AsInt());
        Assert.Equal(1, api.Invoke("DoesGabeHaveInvItem", [SheepValue.FromString("PASSPORT")]).AsInt());
    }
}
