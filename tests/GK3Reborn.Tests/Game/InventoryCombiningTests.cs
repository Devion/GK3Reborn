using GK3Reborn.Formats.Actions;
using GK3Reborn.Foundation.Diagnostics;
using GK3Reborn.Game;
using GK3Reborn.Game.Actions;
using GK3Reborn.UI;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Tests for making one thing out of two, on the shape of the moustache puzzle.
/// </summary>
/// <remarks>
/// <para>
/// Day one at two: syrup and the fibres off the cat make a moustache, the moustache and
/// the cap make a disguise, and the disguise is how Gabriel gets the moped. Four
/// combinations, all of them written as <c>CombineInvItems</c> in <c>INV102P.NVC</c>, and
/// every one of them guarded by <c>GABE_ALL_INV</c> — which the shipped data defines in
/// <c>INV_ALL.NVC</c> as being Gabriel with the inventory open.
/// </para>
/// <para>
/// The lines here are the shipped ones, cut down to the first combination. What they check
/// is the whole path the player takes: the item is offered as a verb on the other item,
/// only while the inventory is open and only when it is actually carried, and performing
/// it consumes both and leaves the third.
/// </para>
/// </remarks>
public sealed class InventoryCombiningTests
{
    private const string Verbs = """
        [VERBS]
        LOOK, up=i_look_std, type=Normal
        EAT, up=i_eatdrink_std, type=Normal
        BLACK_FIBERS, up=i_blkfiber_std, type=Inventory
        SYRUP_PACKAGE, up=i_syrup_std, type=Inventory
        BLACK_MOUSTACHE, up=i_blkmoustache_std, type=Inventory
        """;

    private const string Sample = """
        BLACK_FIBERS,   LOOK,           ALL,            approach=None,script={wait StartVoiceOver("109A944PF1",1);}
        BLACK_FIBERS,   SYRUP_PACKAGE,  ALL,            approach=None,script={wait CombineInvItems("SYRUP_PACKAGE","BLACK_FIBERS","BLACK_MOUSTACHE"); }
        SYRUP_PACKAGE,  LOOK,           GABE_ALL_INV,   approach=None,script={wait StartVoiceOver("109EE44PF1",1);}
        SYRUP_PACKAGE,  EAT,            GABE_ALL_INV,   approach=None,script={wait StartVoiceOver("109EE3XPF1",1);}
        SYRUP_PACKAGE,  BLACK_FIBERS,   GABE_ALL_INV,   approach=None,script={wait CombineInvItems("SYRUP_PACKAGE","BLACK_FIBERS","BLACK_MOUSTACHE");}

        [LOGIC]
        ALL_INV={IsTopLayerInventory()}
        GABE_ALL_INV={IsCurrentEgo("Gabriel") && IsTopLayerInventory()}
        """;

    /// <summary>Gabriel, carrying both halves, with the inventory open.</summary>
    private static GameState Carrying(bool inventoryOpen = true)
    {
        var state = new GameState { Ego = "GABRIEL" };

        state.Inventory.Add("GABRIEL", "SYRUP_PACKAGE");
        state.Inventory.Add("GABRIEL", "BLACK_FIBERS");

        if (inventoryOpen)
        {
            state.Screens.Show(new Screen(ScreenKind.Inventory));
        }

        return state;
    }

    private static ActionResolver Build(GameState state)
    {
        var resolver = new ActionResolver(new Gk3SheepApi(state))
        {
            Verbs = VerbLibrary.Parse(Verbs),
        };

        resolver.Add(NvcFile.Parse(Sample, "INV102P.NVC", new DiagnosticBag()));

        return resolver;
    }

    [Fact]
    public void One_thing_in_the_bag_is_offered_as_a_verb_on_another()
    {
        // This is the whole of combining: an action file writes "syrup on the fibres" as a
        // rule whose verb is the item's name, so the list beside an item is where the other
        // item is chosen.
        GameState state = Carrying();
        ActionResolver resolver = Build(state);

        Assert.Contains(
            "SYRUP_PACKAGE",
            resolver.Resolve("BLACK_FIBERS", state.Ego, state.Inventory.ItemsOf(state.Ego))
                .Select(a => a.LocalizedVerb));

        // Both ways round, because the file writes both and the player may click either.
        Assert.Contains(
            "BLACK_FIBERS",
            resolver.Resolve("SYRUP_PACKAGE", state.Ego, state.Inventory.ItemsOf(state.Ego))
                .Select(a => a.LocalizedVerb));
    }

    [Fact]
    public void Nothing_is_combined_from_the_room()
    {
        // GABE_ALL_INV is IsCurrentEgo("Gabriel") && IsTopLayerInventory(), and the second
        // half is the one that matters here: the syrup's own verbs exist only while the
        // player is looking in their pockets.
        GameState state = Carrying(inventoryOpen: false);
        ActionResolver resolver = Build(state);

        Assert.Empty(resolver.Resolve("SYRUP_PACKAGE", state.Ego, state.Inventory.ItemsOf(state.Ego)));

        // The fibres' own rules are written ALL rather than GABE_ALL_INV — the shipped file
        // guards the two directions differently — so theirs survive. What this pins is that
        // the case is read, not that everything is refused with the screen down.
        Assert.Contains(
            "LOOK",
            resolver.Resolve("BLACK_FIBERS", state.Ego, state.Inventory.ItemsOf(state.Ego))
                .Select(a => a.LocalizedVerb));
    }

    [Fact]
    public void An_item_that_is_not_carried_is_not_offered()
    {
        // Offering every item on every noun offers the player each puzzle's solution as a
        // menu entry. VERBS.TXT is what says BLACK_FIBERS is an item rather than a verb.
        GameState state = Carrying();
        ActionResolver resolver = Build(state);

        state.Inventory.Remove("GABRIEL", "BLACK_FIBERS");

        Assert.DoesNotContain(
            "BLACK_FIBERS",
            resolver.Resolve("SYRUP_PACKAGE", state.Ego, state.Inventory.ItemsOf(state.Ego))
                .Select(a => a.LocalizedVerb));
    }

    [Fact]
    public void Performing_it_consumes_both_and_leaves_the_third()
    {
        GameState state = Carrying();
        var api = new Gk3SheepApi(state);
        _ = new ScriptHost(api);

        var resolver = new ActionResolver(api) { Verbs = VerbLibrary.Parse(Verbs) };
        resolver.Add(NvcFile.Parse(Sample, "INV102P.NVC", new DiagnosticBag()));

        NvcAction? combine = resolver.Find("BLACK_FIBERS", "SYRUP_PACKAGE", state.Ego);

        Assert.NotNull(combine);

        ActionOutcome ran = new ActionRunner(api).Run(combine);

        Assert.True(ran.Ran);
        Assert.False(state.Inventory.Has("GABRIEL", "SYRUP_PACKAGE"));
        Assert.False(state.Inventory.Has("GABRIEL", "BLACK_FIBERS"));
        Assert.True(state.Inventory.Has("GABRIEL", "BLACK_MOUSTACHE"));
    }

    [Fact]
    public void The_new_thing_is_on_the_page_the_moment_it_exists()
    {
        // The screen is laid out fresh from what the player is carrying, so a combination
        // performed with the inventory open shows its result without anything being told to
        // refresh — which is a call the original needed and this does not.
        GameState state = Carrying();
        var api = new Gk3SheepApi(state);
        _ = new ScriptHost(api);

        var resolver = new ActionResolver(api) { Verbs = VerbLibrary.Parse(Verbs) };
        resolver.Add(NvcFile.Parse(Sample, "INV102P.NVC", new DiagnosticBag()));

        _ = new ActionRunner(api).Run(resolver.Find("BLACK_FIBERS", "SYRUP_PACKAGE", state.Ego)!);

        var painter = new ScreenPainter(
            new GK3Reborn.Rendering.Overlay(Tests.UI.MenuPageTests.Font()));
        List<string> asked = [];

        painter.Build(
            new ScreenView(
                new Screen(ScreenKind.Inventory),
                state.Inventory.ItemsOf(state.Ego),
                state.Inventory.ActiveItemOf(state.Ego),
                Icons: item =>
                {
                    asked.Add(item);

                    return new ItemIcon(1, 94, 94);
                }),
            1280,
            720);

        Assert.Equal(["BLACK_MOUSTACHE"], asked);
        Assert.NotNull(painter.HitAt(Middle(painter, "item:BLACK_MOUSTACHE")));
    }

    /// <summary>The first point that answers to an identifier.</summary>
    private static System.Numerics.Vector2 Middle(ScreenPainter painter, string id)
    {
        for (int y = 0; y < 720; y += 3)
        {
            for (int x = 0; x < 1280; x += 3)
            {
                if (painter.HitAt(new System.Numerics.Vector2(x, y)) == id)
                {
                    return new System.Numerics.Vector2(x, y);
                }
            }
        }

        return default;
    }
}
