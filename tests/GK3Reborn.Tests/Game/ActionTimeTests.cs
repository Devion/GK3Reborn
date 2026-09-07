using GK3Reborn.Formats.Actions;
using GK3Reborn.Foundation.Diagnostics;
using GK3Reborn.Game;
using GK3Reborn.Game.Actions;
using GK3Reborn.UI.Interaction;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Tests for the rules the resolver withholds because they belong to another moment.
/// </summary>
/// <remarks>
/// An action file is named for when it applies and hands off to a script that is named the
/// same way, and the two do not always agree — which is the whole of what is tested here.
/// Both halves fail silently: a rule withheld is a verb that is simply not on the bar, and
/// there is nothing to see except a character who cannot be asked about anything.
/// </remarks>
public sealed class ActionTimeTests
{
    /// <summary>The shape of a real VERBS.TXT, cut down to what is read.</summary>
    private const string VerbsFile = """
        [VERBS]
        LOOK, up=v_look_std, type=Normal
        TALK, up=v_talk_std, type=Normal
        TRACE, up=v_trace_std, type=Normal
        PICKUP, up=v_pickup_std, type=Normal
        T_INTRODUCE, up=i_intro_std, type=Topic
        """;

    private static ActionResolver Resolver(Timeblock now, params (string Name, string Text)[] files)
    {
        var resolver = new ActionResolver(new Gk3SheepApi(new GameState()))
        {
            Verbs = VerbLibrary.Parse(VerbsFile),
            Now = now,
        };

        var diagnostics = new DiagnosticBag();

        foreach ((string name, string text) in files)
        {
            resolver.Add(NvcFile.Parse(text, name, diagnostics));
        }

        return resolver;
    }

    private static IReadOnlyList<string> Verbs(ActionResolver resolver, string noun) =>
        [.. resolver.Resolve(noun).Select(a => a.LocalizedVerb)];

    [Fact]
    public void A_file_that_spans_the_story_is_read_for_when_its_rules_belong()
    {
        // The church's four angels, which are in the room from the first morning and whose
        // Trace belongs to the second afternoon. CHU_ALL.NVC covers every day and says
        // nothing about when, so the script the rule hands off to is the only clue there
        // is: CallSheep("chu205p", …) is the completion of one point in the story, and
        // running it from another runs that point's ending out of turn.
        (string, string) angels = ("CHU_ALL.NVC", """
            FOUR_ANGELS1, TRACE, ALL, script={CallSheep("chu205p","Done");}
            FOUR_ANGELS1, LOOK, ALL, script={}
            """);

        Assert.Equal(["LOOK"], Verbs(Resolver(new Timeblock(1, 10, false), angels), "FOUR_ANGELS1"));

        Assert.Equal(
            ["LOOK", "TRACE"],
            Verbs(Resolver(new Timeblock(2, 5, true), angels), "FOUR_ANGELS1").Order());
    }

    [Fact]
    public void A_file_that_names_when_it_belongs_is_taken_at_its_word()
    {
        // TR1102P04P.NVC covers two o'clock and four, and everything in it hands off to
        // tr1102p — the only script the pair of them has, because no TR1104P.SHP was ever
        // written. Reading the script name alone withheld every one of those rules at four,
        // which is how the taxi driver at Couiza station lost his whole conversation.
        //
        // The file has already been checked against the clock before it is in scope at all:
        // its name is the condition, and ActionSets.For reads it. So a rule inside it
        // belongs to now whatever script it calls.
        (string, string) station = ("TR1102P04P.NVC", """
            TAXI_DRIVER, T_INTRODUCE, 1ST_TIME, script={wait CallSheep("tr1102p","Introduce");}
            TAXI_DRIVER, LOOK, ALL, script={}
            """);

        foreach (Timeblock when in new[] { new Timeblock(1, 2, true), new Timeblock(1, 4, true) })
        {
            Assert.Equal(
                ["LOOK", "T_INTRODUCE"],
                Verbs(Resolver(when, station), "TAXI_DRIVER").Order());
        }

        // And a file naming one block alone still says when its rules belong. HAL_1ALL.NVC
        // is all of day one and hands the glass to hal112p; that is the day's file speaking
        // about its own day, not a rule from somewhere else.
        Assert.Equal(
            ["PICKUP"],
            Verbs(
                Resolver(
                    new Timeblock(1, 10, false),
                    ("HAL_1ALL.NVC", """
                        GLASS, PICKUP, ALL, script={wait CallSheep("hal112p","Pick_Glass");}
                        """)),
                "GLASS"));
    }

    [Fact]
    public void Talk_and_the_topic_list_answer_the_same_question()
    {
        // DIALOGUE_TOPICS_LEFT is what puts Talk on the bar, and Resolve takes Talk off
        // again precisely when there are topics to show instead. If the two are answered
        // differently the player gets a Talk with nothing behind it — reported from the
        // Couiza station as their character walking over to the taxi driver, playing a
        // talking animation, and saying nothing.
        //
        // Asked here of the case that used to disagree: a topic the resolver withholds
        // must not still count as something left to say.
        ActionResolver resolver = Resolver(
            new Timeblock(1, 10, false),
            ("CHU_ALL.NVC", """
                ABBE, TALK, DIALOGUE_TOPICS_LEFT, script={}
                ABBE, TALK, NOT_DIALOGUE_TOPICS_LEFT, script={}
                ABBE, T_INTRODUCE, ALL, script={CallSheep("chu205p","Done");}
                """));

        Assert.Equal(["TALK"], Verbs(resolver, "ABBE"));

        Assert.Equal(
            "NOT_DIALOGUE_TOPICS_LEFT",
            resolver.Find("ABBE", "TALK", "GABRIEL")?.Case);
    }

    [Fact]
    public void A_topic_already_said_is_not_something_left_to_say()
    {
        // The other way the two questions can come apart, and the one the shipped data
        // reaches every time a conversation is finished: with the last topic used up, Talk
        // has to fall through to the line about there being nothing more to ask.
        var state = new GameState();

        var resolver = new ActionResolver(new Gk3SheepApi(state))
        {
            Verbs = VerbLibrary.Parse(VerbsFile),
        };

        resolver.Add(NvcFile.Parse(
            """
            ABBE, TALK, DIALOGUE_TOPICS_LEFT, script={}
            ABBE, TALK, NOT_DIALOGUE_TOPICS_LEFT, script={}
            ABBE, T_INTRODUCE, ALL, script={}
            """,
            "CHU_ALL.NVC",
            new DiagnosticBag()));

        Assert.Equal(["T_INTRODUCE"], Verbs(resolver, "ABBE"));

        state.Said("ABBE", "T_INTRODUCE", "ALL");

        Assert.Equal(["TALK"], Verbs(resolver, "ABBE"));

        Assert.Equal(
            "NOT_DIALOGUE_TOPICS_LEFT",
            resolver.Find("ABBE", "TALK", "GABRIEL")?.Case);
    }
}
