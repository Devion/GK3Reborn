using GK3Reborn.Game;
using GK3Reborn.Sheep;
using GK3Reborn.UI;
using Xunit;

namespace GK3Reborn.Tests.UI;

/// <summary>
/// Tests for what is in front of the room and how the player gets out of it.
/// </summary>
/// <remarks>
/// GK3 has a lot of modal screens and in the original each arrived with its own way in and
/// its own way out. <c>Plan/03</c> section 3 asks that they share navigation and back
/// behaviour, so the player learns the way out once. These pin that: one Back, one thing
/// closed, back where you were.
/// </remarks>
public sealed class ScreenLayerTests
{
    private static int Eval(GameState state, string expression) =>
        SheepExpression.Evaluate(expression, new Gk3SheepApi(state)).AsInt();

    [Fact]
    public void Back_closes_exactly_one_thing_and_lands_where_the_player_was()
    {
        var screens = new ScreenLayers();

        screens.Show(new Screen(ScreenKind.Inventory));
        screens.Show(new Screen(ScreenKind.InventoryInspect, "CHURCH_PAMPHLET"));

        // Backing out of an item held up close returns to the inventory it came from,
        // rather than all the way to the room.
        Assert.Equal(new Screen(ScreenKind.InventoryInspect, "CHURCH_PAMPHLET"), screens.Back());
        Assert.True(screens.IsOnTop(ScreenKind.Inventory));

        Assert.Equal(new Screen(ScreenKind.Inventory), screens.Back());
        Assert.True(screens.InTheRoom);

        // And backing out of the room is not an error, it is simply nothing.
        Assert.Null(screens.Back());
    }

    [Fact]
    public void Asking_for_a_screen_that_is_already_open_brings_it_forward()
    {
        // Rather than opening a second copy the player would have to close twice.
        var screens = new ScreenLayers();

        screens.Show(new Screen(ScreenKind.Inventory));
        screens.Show(new Screen(ScreenKind.InventoryInspect, "DAGGER"));
        screens.Show(new Screen(ScreenKind.Inventory));

        Assert.Equal(2, screens.Open.Count);
        Assert.True(screens.IsOnTop(ScreenKind.Inventory));
    }

    [Fact]
    public void The_inventory_is_reachable_from_everywhere_the_players_pockets_are()
    {
        var screens = new ScreenLayers();
        Assert.True(screens.InventoryReachable);

        // A panel over the room leaves the player where they were, so their pockets are
        // still theirs.
        screens.Show(new Screen(ScreenKind.Binoculars));
        Assert.True(screens.InventoryReachable);

        screens.Show(new Screen(ScreenKind.SceneInspect, "PAINTING"));
        Assert.True(screens.InventoryReachable);

        // The driving map is somewhere else entirely.
        screens.Show(new Screen(ScreenKind.Driving));
        Assert.False(screens.InventoryReachable);

        screens.Back();
        Assert.True(screens.InventoryReachable);
    }

    [Fact]
    public void Changing_room_closes_everything()
    {
        var screens = new ScreenLayers();
        screens.Show(new Screen(ScreenKind.Inventory));
        screens.Show(new Screen(ScreenKind.Fingerprint, "DOORKNOB"));

        screens.CloseAll();

        Assert.True(screens.InTheRoom);
        Assert.Null(screens.Top);
    }

    [Fact]
    public void A_script_opens_and_closes_the_inventory()
    {
        var state = new GameState();

        Assert.Equal(0, Eval(state, "IsTopLayerInventory()"));

        Eval(state, "ShowInventory()");
        Assert.Equal(1, Eval(state, "IsTopLayerInventory()"));

        Eval(state, "HideInventory()");
        Assert.Equal(0, Eval(state, "IsTopLayerInventory()"));
        Assert.True(state.Screens.InTheRoom);
    }

    [Fact]
    public void Hiding_the_inventory_takes_the_panel_it_opened_with_it()
    {
        // The original leaves the inspect panel behind, which is a bug it shipped with:
        // scanning an item from the inspect screen left the panel over a room it had no
        // business being over.
        var state = new GameState();

        Eval(state, "ShowInventory()");
        Eval(state, """InventoryInspect("CHURCH_PAMPHLET")""");
        Assert.Equal(1, Eval(state, "IsTopLayerInventory()"));

        Eval(state, "HideInventory()");

        Assert.True(state.Screens.InTheRoom);
    }

    [Fact]
    public void An_item_held_up_close_still_counts_as_the_inventory()
    {
        var state = new GameState();

        Eval(state, "ShowInventory()");
        Eval(state, """InventoryInspect("DAGGER")""");

        Assert.Equal(1, Eval(state, "IsTopLayerInventory()"));

        Eval(state, "InventoryUninspect()");
        Assert.Equal(1, Eval(state, "IsTopLayerInventory()"));
    }

    [Fact]
    public void Every_modal_screen_a_script_can_open_goes_on_the_same_stack()
    {
        var state = new GameState();

        Eval(state, "ShowBinocs()");
        Eval(state, """ShowFingerprintInterface("DOORKNOB")""");
        Eval(state, "ShowDrivingInterface()");

        Assert.Equal(
            [ScreenKind.Binoculars, ScreenKind.Fingerprint, ScreenKind.Driving],
            state.Screens.Open.Select(s => s.Kind));

        Assert.Equal("DOORKNOB", state.Screens.Open[1].Subject);
    }

    [Fact]
    public void What_is_showing_is_part_of_the_state_two_runs_are_compared_on()
    {
        // A script that behaves differently by what is showing has diverged if two runs
        // disagree about it.
        var state = new GameState();
        string room = state.ComputeHash();

        Eval(state, "ShowInventory()");
        Assert.NotEqual(room, state.ComputeHash());

        Eval(state, "HideInventory()");
        Assert.Equal(room, state.ComputeHash());
    }

    [Fact]
    public void An_inventory_status_says_who_is_holding_the_thing()
    {
        var state = new GameState();

        Eval(state, """SetInvItemStatus("CANDY", "GabeHas")""");
        Assert.True(state.Inventory.Has("GABRIEL", "CANDY"));
        Assert.False(state.Inventory.Has("GRACE", "CANDY"));

        Eval(state, """SetInvItemStatus("CANDY", "BothHave")""");
        Assert.True(state.Inventory.Has("GRACE", "CANDY"));

        // NotPlaced, Placed and Used all mean nobody is carrying it.
        Eval(state, """SetInvItemStatus("CANDY", "Used")""");
        Assert.False(state.Inventory.Has("GABRIEL", "CANDY"));
        Assert.False(state.Inventory.Has("GRACE", "CANDY"));
    }

    [Fact]
    public void An_inventory_status_nobody_recognises_is_ignored_and_reported()
    {
        var state = new GameState();
        var api = new Gk3SheepApi(state);

        SheepExpression.Evaluate("""SetInvItemStatus("CANDY", "Mislaid")""", api);

        Assert.False(state.Inventory.Has("GABRIEL", "CANDY"));
        Assert.Contains(api.Diagnostics.Items, d => d.Code == "GK3R3201");
    }

    [Fact]
    public void A_script_can_insist_on_an_answer()
    {
        var state = new GameState();

        Assert.False(state.MustChooseAnAction);

        Eval(state, "SetVerbModal(1)");
        Assert.True(state.MustChooseAnAction);

        Eval(state, "SetVerbModal(0)");
        Assert.False(state.MustChooseAnAction);
    }
}
