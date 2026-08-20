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
}
