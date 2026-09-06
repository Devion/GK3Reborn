using System.Numerics;
using GK3Reborn.Formats;
using GK3Reborn.Formats.Actions;
using GK3Reborn.Foundation.Diagnostics;
using GK3Reborn.Game;
using GK3Reborn.Game.Actions;
using GK3Reborn.Game.Interaction;
using GK3Reborn.Sheep;
using GK3Reborn.UI.Interaction;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// What a plain left click does to the thing under the pointer.
/// </summary>
/// <remarks>
/// <para>
/// Reported as "once Gabriel picks up the fingerprint scanner it overrides most other
/// nouns on everything, so most clicks afterwards need a right click to select the other
/// options". The kit is not special-cased anywhere; the second line of
/// <c>GLB_ALL.NVC</c> — in scope in every room in the game — is
/// <c>ANY_OBJECT, FINGERPRINT_KIT, GABE_ALL</c>, the catch-all that gives Gabriel a line
/// for dusting something with no prints on it.
/// </para>
/// <para>
/// So the moment the kit is in the bag it is a verb offered on every noun there is, and
/// being written about the wildcard noun it came out ahead of everything written about the
/// thing itself.
/// </para>
/// </remarks>
public sealed class DefaultVerbTests
{
    /// <summary>The verbs, with the two kinds that matter told apart as VERBS.TXT does.</summary>
    private const string Verbs = """
        [VERBS]
        LOOK, up=i_look_std, type=Normal
        OPEN, up=i_open_std, type=Normal
        TALK, up=i_talk_std, type=Normal
        FINGERPRINT_KIT, up=i_fingerkit_std, type=Inventory
        """;

    /// <summary>
    /// The shipped catch-all, and two nouns of the shape the report is about.
    /// </summary>
    /// <remarks>
    /// <c>ANY_OBJECT</c>'s line is copied from <c>GLB_ALL.NVC</c>. The cabinet answers to
    /// one verb of its own and no <c>LOOK</c>, which is what the kit used to beat; the
    /// wall answers to nothing at all, which is where it used to invent an action.
    /// </remarks>
    private const string Sample = """
        ANY_OBJECT,  FINGERPRINT_KIT,  GABE_ALL,  script={wait CallSheep("glb_all","FingerPrint");}
        CABINET,     OPEN,             ALL,       script={wait StartVoiceOver("aaa",1);}
        MOSELY,      LOOK,             ALL,       script={wait StartVoiceOver("bbb",1);}
        MOSELY,      TALK,             ALL,       script={wait StartVoiceOver("ccc",1);}
        """;

    private static ActionResolver Build(GameState state)
    {
        var resolver = new ActionResolver(new Gk3SheepApi(state))
        {
            Verbs = VerbLibrary.Parse(Verbs),
        };

        resolver.Add(NvcFile.Parse(Sample, "GLB_ALL.NVC", new DiagnosticBag()));

        return resolver;
    }

    /// <summary>What the pointer would report over a noun, with the bag as it is.</summary>
    private static Hover Over(ActionResolver resolver, GameState state, string noun) =>
        new(
            new ScenePick(noun, noun, null, 1f, Vector3.Zero, PickKind.Prop),
            resolver.Resolve(noun, state.Ego, state.Inventory.ItemsOf(state.Ego)));

    private static GameState Carrying()
    {
        var state = new GameState();
        state.Inventory.Add("GABRIEL", "FINGERPRINT_KIT");
        return state;
    }

    [Fact]
    public void An_item_in_the_bag_never_becomes_the_click_on_a_thing_of_its_own()
    {
        GameState state = Carrying();
        ActionResolver resolver = Build(state);

        // The cabinet opens. It has no LOOK, so before this the wildcard's kit was the
        // first verb offered and the click dusted the cabinet instead of opening it.
        Assert.Equal("OPEN", Over(resolver, state, "CABINET").Default);
    }

    [Fact]
    public void An_item_in_the_bag_does_not_invent_an_action_on_a_thing_that_has_none()
    {
        GameState state = Carrying();
        ActionResolver resolver = Build(state);

        // Nothing in the files is written about a wall, so a click on one means what a
        // click on the floor means. The kit made every such noun answer to something.
        Assert.Null(Over(resolver, state, "WALL").Default);
    }

    [Fact]
    public void The_item_is_still_offered_on_the_bar_and_still_runs()
    {
        GameState state = Carrying();
        ActionResolver resolver = Build(state);

        Hover hover = Over(resolver, state, "CABINET");

        // Choosing it from the verb bar is how the original used an item on something, and
        // that must still reach the same rule. It is last, behind what the thing itself does.
        Assert.Equal(["OPEN", "FINGERPRINT_KIT"], hover.Actions.Select(a => a.LocalizedVerb));
        Assert.Equal(ActionCategory.Item, hover.Actions[^1].Category);
        Assert.NotNull(resolver.Find("CABINET", "FINGERPRINT_KIT"));
    }

    [Fact]
    public void An_empty_bag_changes_nothing_about_any_of_it()
    {
        var state = new GameState();
        ActionResolver resolver = Build(state);

        Assert.Equal("OPEN", Over(resolver, state, "CABINET").Default);
        Assert.Equal("LOOK", Over(resolver, state, "MOSELY").Default);
        Assert.Null(Over(resolver, state, "WALL").Default);
    }

    [Fact]
    public void Looking_still_wins_the_click_where_a_thing_can_be_looked_at()
    {
        GameState state = Carrying();
        ActionResolver resolver = Build(state);

        Assert.Equal("LOOK", Over(resolver, state, "MOSELY").Default);
    }
}
