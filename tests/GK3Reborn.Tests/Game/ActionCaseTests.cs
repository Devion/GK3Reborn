using GK3Reborn.Formats.Actions;
using GK3Reborn.Foundation.Diagnostics;
using GK3Reborn.Game;
using GK3Reborn.UI.Interaction;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Tests for the cases the engine answers itself.
/// </summary>
/// <remarks>
/// A case the resolver does not recognise is treated as unavailable, so a missing built-in
/// does not fail — it quietly takes the action out of the game. That is how
/// <c>TIME_BLOCK_OVERRIDE</c> went unnoticed until a sweep of the whole corpus counted 918
/// actions naming a case nothing defined.
/// </remarks>
public sealed class ActionCaseTests
{
    private static ActionResolver Resolver(GameState state, params string[] files)
    {
        var resolver = new ActionResolver(new Gk3SheepApi(state));
        var diagnostics = new DiagnosticBag();

        for (int i = 0; i < files.Length; i++)
        {
            resolver.Add(NvcFile.Parse(files[i], $"test{i}.nvc", diagnostics));
        }

        return resolver;
    }

    private static IReadOnlyList<string> Verbs(ActionResolver resolver, string noun) =>
        [.. resolver.Resolve(noun).Select(a => a.LocalizedVerb)];

    [Fact]
    public void A_timeblock_override_is_a_built_in_that_always_applies()
    {
        // Used by 90 of the corpus's action files and written into the logic section of
        // exactly one, because the original answers it itself: it marks an action a
        // timeblock's file writes over one the location's general file gives.
        ActionResolver resolver = Resolver(
            new GameState(),
            "PAINTING, LOOK, TIME_BLOCK_OVERRIDE, script={}\nCHAIR, LOOK, TIME_BLOCK, script={}");

        Assert.Equal(["LOOK"], Verbs(resolver, "PAINTING"));
        Assert.Equal(["LOOK"], Verbs(resolver, "CHAIR"));
    }

    [Fact]
    public void The_time_cases_count_what_the_player_has_already_done()
    {
        var state = new GameState();

        // Each rule's case counts that rule's own noun and verb, so these four are
        // independent of one another rather than four stages of one thing.
        ActionResolver Build() => Resolver(
            state,
            """
            DOOR, OPEN, 1ST_TIME, script={}
            DOOR, PUSH, 2CD_TIME, script={}
            DOOR, PULL, 3RD_TIME, script={}
            DOOR, KICK, OTR_TIME, script={}
            """);

        // Nothing has been done to the door, so only the first-time rule applies.
        Assert.Equal(["OPEN"], Verbs(Build(), "DOOR"));

        state.IncrementNounVerbCount("DOOR", "PUSH");
        state.IncrementNounVerbCount("DOOR", "KICK");
        Assert.Equal(["OPEN", "PUSH", "KICK"], Verbs(Build(), "DOOR"));

        // Opening it once takes the first-time rule away; pushing it again takes the
        // second-time rule away and leaves the pull, which wants a third.
        state.IncrementNounVerbCount("DOOR", "OPEN");
        state.IncrementNounVerbCount("DOOR", "PUSH");
        state.SetNounVerbCount("DOOR", "PULL", 2);
        Assert.Equal(["PULL", "KICK"], Verbs(Build(), "DOOR"));
    }

    [Fact]
    public void Who_the_player_is_decides_which_of_a_pair_of_lines_is_theirs()
    {
        // INV_23ALL.NVC as it ships: the same rule written twice, Gabriel's above Grace's,
        // both ending in IsCurrentEgo. Reported from the chateau on the second afternoon —
        // clicking SCANNER answered in Gabriel's voice while playing Grace, because the
        // scene had never told the game whose day it was.
        const string ScannerRules = """
            [ACTIONS]
            ANY_OBJECT, SCANNER, GABE_ALL_INV,  script={StartVoiceOver("10LXW7XPG1",1);}
            ANY_OBJECT, SCANNER, GRACE_ALL_INV, script={StartVoiceOver("10LXW7XDG1",1);}

            [LOGIC]
            GABE_ALL_INV={IsCurrentEgo("Gabriel") && IsTopLayerInventory()}
            GRACE_ALL_INV={IsCurrentEgo("Grace") && IsTopLayerInventory()}
            """;

        static string? Said(string ego)
        {
            var state = new GameState { Ego = ego };

            // The rules ask whether the inventory is what the player is looking at, which
            // is the whole reason these lines are only reachable from the close-up.
            state.Screens.Show(new GK3Reborn.UI.Screen(GK3Reborn.UI.ScreenKind.InventoryInspect, "IMMORTAL_1"));

            return Resolver(state, ScannerRules).Find("IMMORTAL_1", "SCANNER", ego)?.Script;
        }

        Assert.Contains("10LXW7XPG1", Said("GABRIEL"), StringComparison.Ordinal);
        Assert.Contains("10LXW7XDG1", Said("GRACE"), StringComparison.Ordinal);
    }

    [Fact]
    public void Easter_eggs_are_off()
    {
        ActionResolver resolver = Resolver(new GameState(), "STATUE, LOOK, EGG, script={}");

        Assert.Empty(Verbs(resolver, "STATUE"));
    }

    [Fact]
    public void Whether_there_is_anything_left_to_say_is_asked_of_the_topics()
    {
        var state = new GameState();

        ActionResolver Build() => Resolver(
            state,
            """
            MOSELY, TALK, DIALOGUE_TOPICS_LEFT, script={}
            MOSELY, Z_CHAT, NOT_DIALOGUE_TOPICS_LEFT, script={}
            MOSELY, T_THE_BODY, ALL, script={}
            """);

        // A topic nobody has raised yet means there is something to talk about.
        Assert.Contains("TALK", Verbs(Build(), "MOSELY"));
        Assert.DoesNotContain("Z_CHAT", Verbs(Build(), "MOSELY"));

        state.IncrementNounVerbCount("MOSELY", "T_THE_BODY");

        Assert.DoesNotContain("TALK", Verbs(Build(), "MOSELY"));
        Assert.Contains("Z_CHAT", Verbs(Build(), "MOSELY"));
    }

    [Fact]
    public void A_case_still_unknown_after_all_that_is_reported_and_treated_as_unavailable()
    {
        // Real: CHU_ALL.NVC asks for G_DONE_PISCES_NOT_ARIES and defines
        // GOT_LSR_DONE_PISCES_NOT_ARIES. The action never fires in the original either.
        ActionResolver resolver = Resolver(
            new GameState(),
            """
            ANGELS, LOOK, G_DONE_PISCES_NOT_ARIES, script={}

            [LOGIC]
            GOT_LSR_DONE_PISCES_NOT_ARIES={1}
            """);

        Assert.Empty(Verbs(resolver, "ANGELS"));
        Assert.Contains(resolver.Diagnostics.Items, d => d.Code == "GK3R3301");
    }

    [Fact]
    public void A_case_ending_in_a_semicolon_is_still_an_expression()
    {
        // LBY110A02P.NVC writes {!DoesEgoHaveInvItem("Candy");}. The braces are the field
        // and the semicolon terminates a statement, which the original's compiler tolerates
        // because it compiles the case as a snippet rather than reading it as an expression.
        var diagnostics = new DiagnosticBag();

        NvcFile file = NvcFile.Parse(
            """
            MOSELY, CANDY, NO_CANDY, script={}

            [LOGIC]
            NO_CANDY={!DoesEgoHaveInvItem("Candy");}
            """,
            "test.nvc",
            diagnostics);

        Assert.Equal("!DoesEgoHaveInvItem(\"Candy\")", file.Cases["NO_CANDY"]);

        var resolver = new ActionResolver(new Gk3SheepApi(new GameState()));
        resolver.Add(file);

        Assert.Equal(["CANDY"], Verbs(resolver, "MOSELY"));
        Assert.Empty(resolver.Diagnostics.Items);
    }

    [Fact]
    public void The_egg_case_is_off_until_something_sets_the_flag()
    {
        // The original hard-codes this one false — its own source has the same placeholder —
        // so the content behind it never shipped in a playable form. Reading a flag costs
        // nothing when nobody sets it, which is every ordinary game, and gives the console
        // something to set.
        var state = new GameState();

        ActionResolver resolver = Resolver(
            state,
            """
            CHICKEN, LOOK, EGG, script={}
            CHICKEN, TALK, ALL, script={}
            """);

        Assert.Equal(["TALK"], Verbs(resolver, "CHICKEN"));

        state.SetFlag("EGG");

        Assert.Equal(["LOOK", "TALK"], Verbs(resolver, "CHICKEN"));
    }
}
