using GK3Reborn.Formats.Actions;
using GK3Reborn.Foundation.Diagnostics;
using GK3Reborn.Game;
using GK3Reborn.Game.Actions;
using GK3Reborn.UI;
using GK3Reborn.UI.Interaction;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Tests for the cases the engine answers itself.
/// </summary>
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
    public void What_the_player_answers_to_themselves_leaves_out_what_every_noun_answers_to()
    {
        // GLB_ALL's second line writes the fingerprint kit against ANY_OBJECT, so from the
        // moment Gabriel picks it up it is a verb on everything there is. That belongs to
        // whatever is under the crosshair, not on a bar of things to do to yourself -- the
        // bar is the right click on Gabriel that first person leaves nobody to make.
        ActionResolver resolver = Resolver(
            new GameState(),
            "ANY_OBJECT, FINGERPRINT_KIT, ALL, script={}", "GABRIEL, BINOCULARS, ALL, script={}", "GABRIEL, LOOK, ALL, script={}");

        Assert.Contains("FINGERPRINT_KIT", Verbs(resolver, "GABRIEL"));

        IReadOnlyList<string> own = [.. resolver.Resolve("GABRIEL", "GABRIEL", null, wildcards: false).Select(a => a.LocalizedVerb)];

        Assert.DoesNotContain("FINGERPRINT_KIT", own);
        Assert.Contains("BINOCULARS", own);
        Assert.Contains("LOOK", own);
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
        // Asked of the topics by offering them: the question is "would any of them be on
        // the bar", and it has to be, because Resolve takes the Talk off the bar precisely
        // when there are topics to show instead. Answered any other way the two come apart
        // and the player gets a Talk with nothing behind it.
        //
        // It used to be asked of the noun/verb counts, which raising a topic does not
        // touch: ActionRunner.Finish records a topic count and the line that was said, and
        // an ordinary count moves only when a script says IncNounVerbCount. So the case
        // answered yes for ever, Mosely never ran out of things to say, and the nine
        // NOT_DIALOGUE_TOPICS_LEFT rules in the corpus were unreachable.
        var state = new GameState();

        ActionResolver Build()
        {
            ActionResolver resolver = Resolver(
                state,
                """
                MOSELY, TALK, DIALOGUE_TOPICS_LEFT, script={}
                MOSELY, Z_CHAT, NOT_DIALOGUE_TOPICS_LEFT, script={}
                MOSELY, T_THE_BODY, ALL, script={}
                """);

            resolver.Verbs = VerbLibrary.Parse(
                """
                [VERBS]
                TALK, up=i_talk_std, type=Normal
                Z_CHAT, up=i_chat_std, type=Chat
                T_THE_BODY, up=i_body_std, type=Topic
                """);

            return resolver;
        }

        // A topic nobody has raised yet means there is something to talk about — and the
        // bar shows the topic itself rather than the Talk that would only have opened it.
        Assert.Equal(["T_THE_BODY"], Verbs(Build(), "MOSELY"));
        Assert.Equal("DIALOGUE_TOPICS_LEFT", Build().Find("MOSELY", "TALK", "GABRIEL")?.Case);

        // And raising it, exactly as ActionRunner.Finish does.
        state.SetTopicCount("MOSELY", "T_THE_BODY", 1);
        state.Said("MOSELY", "T_THE_BODY", "ALL");

        Assert.Equal(["Z_CHAT"], Verbs(Build(), "MOSELY"));
        Assert.Null(Build().Find("MOSELY", "TALK", "GABRIEL"));
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
    public void A_script_missing_its_closing_brace_still_has_its_script()
    {
        NvcFile file = NvcFile.Parse(
            """
            CHURCH_PAMPHLET, LOOK, ALL_INV, script={wait StartVoiceOver("10P7544PF1",1);
            CHURCH_PAMPHLET_P2, LOOK, ALL_INV, script={wait StartVoiceOver("10P7544PF1",1
            """,
            "test.nvc",
            new DiagnosticBag());

        Assert.All(file.Actions, a => Assert.Equal("ALL_INV", a.Case));
        Assert.All(file.Actions, a => Assert.Equal("wait StartVoiceOver(\"10P7544PF1\",1);", a.Script));
    }

    /// <summary>MS3's Grace case for after the Sidney page lost none of its "!" and so never held once she had read it.</summary>
    [Fact]
    public void The_asmodeus_case_for_after_the_sidney_page_asks_for_the_flag()
    {
        NvcFile file = NvcFile.Parse(
            """
            ASMODEUS, LOOK, LOOKED_UP_ASMODEUS_ON_SIDNEY, script={}

            [LOGIC]
            LOOKED_UP_ASMODEUS_ON_SIDNEY      ={!GetFlag("Asmodeus") && IsCurrentEgo("GRACE")}
            """,
            "MS3_ALL.NVC",
            new DiagnosticBag());

        Assert.Equal("GetFlag(\"Asmodeus\") && IsCurrentEgo(\"GRACE\")", file.Cases["LOOKED_UP_ASMODEUS_ON_SIDNEY"]);
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
    [Fact]
    public void The_dead_mens_faces_answer_to_the_rules_written_for_both_faces()
    {
        // ARM.SIF names the two faces DEAD_FACES_HE1 and DEAD_FACES_HE2 while ARM202P.NVC
        // writes every rule for DEAD_FACES. Without the alias Think was never offered, and
        // with it went ThinkFaces$ and its points.
        var state = new GameState();

        ActionResolver resolver = Resolver(
            state,
            """
            DEAD_FACES, LOOK, GABE_ALL, script={}
            DEAD_FACES, THINK, FOUND_BLOOD_POOLS, script={}
            DEAD_FACES, THINK, NOT_FOUND_BLOOD_POOLS_OTR, script={}

            [LOGIC]
            FOUND_BLOOD_POOLS={GetNounVerbCount("BLOOD_POOL","LOOK")}
            NOT_FOUND_BLOOD_POOLS_OTR={(GetnounVerbCount("BLOOD_POOL","LOOK")==0) && GetNounVerbCountInt(n$,v$) }
            """);

        state.IncrementNounVerbCount("BLOOD_POOL", "LOOK");

        Assert.Equal(["LOOK", "THINK"], Verbs(resolver, "DEAD_FACES_HE1"));
        Assert.Equal(["LOOK", "THINK"], Verbs(resolver, "DEAD_FACES_HE2"));
        Assert.Equal("FOUND_BLOOD_POOLS", resolver.Find("DEAD_FACES_HE2", "THINK")?.Case);
    }

    [Fact]
    public void Scanning_into_Sidney_beats_the_two_rules_that_only_talk_about_scanning()
    {
        // Every scannable item carries three SCANNER rules: the one that does the work, and
        // one for each ego saying they cannot do it here. All three can be satisfied at once
        // — GABE_ALL_INV is only "IsCurrentEgo(Gabriel) && IsTopLayerInventory()" — so which
        // one wins is the whole question. A case somebody defined is worth 7 against that
        // one's 2, which settles it, and this is what the port's scanning depends on.
        var state = new GameState();

        ActionResolver resolver = Resolver(
            state,
            """
            [LOGIC]
            IN_SIDNEY_ADD_DATA={IsTopLayerInventory() && GetFlag("UsingScanner")}
            GABE_ALL_INV={IsCurrentEgo("Gabriel") && IsTopLayerInventory()}
            GRACE_ALL_INV={IsCurrentEgo("Grace") && IsTopLayerInventory()}

            [ACTIONS]
            ABBE_FINGERPRINT, SCANNER, IN_SIDNEY_ADD_DATA, script={}
            ABBE_FINGERPRINT, SCANNER, GABE_ALL_INV, script={}
            ABBE_FINGERPRINT, SCANNER, GRACE_ALL_INV, script={}
            """);

        // Nothing open: no scanner, and no rule at all.
        Assert.Null(resolver.Find("ABBE_FINGERPRINT", "SCANNER", state.Ego));

        // The bag open in the room, which is somebody holding a print and no machine to put
        // it in. What they get is the line about not being able to.
        state.Screens.Show(new Screen(ScreenKind.Inventory, "ABBE_FINGERPRINT"));

        Assert.Equal(
            "GABE_ALL_INV",
            resolver.Find("ABBE_FINGERPRINT", "SCANNER", state.Ego)?.Case);

        // And the scanner up, which is the rule that marks the item used and runs whatever
        // script hangs off it.
        state.SetFlag("UsingScanner");

        Assert.Equal(
            "IN_SIDNEY_ADD_DATA",
            resolver.Find("ABBE_FINGERPRINT", "SCANNER", state.Ego)?.Case);
    }
}