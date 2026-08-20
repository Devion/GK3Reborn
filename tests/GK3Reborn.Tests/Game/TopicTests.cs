using GK3Reborn.Formats.Actions;
using GK3Reborn.Foundation.Diagnostics;
using GK3Reborn.Game;
using GK3Reborn.Game.Actions;
using GK3Reborn.Sheep;
using GK3Reborn.UI.Interaction;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Tests for what a person may be asked about.
/// </summary>
/// <remarks>
/// A topic is written exactly like a verb — <c>BUTHANE, T_TOUR_GROUP, CASE, script={…}</c>
/// — and only <c>VERBS.TXT</c> says which is which. The two rules that follow from being a
/// topic are both about <em>disappearing</em>, and neither is written down in the action
/// files: a topic is said once, and its <c>ALL</c> line is the last thing said rather than
/// something always available. Miss them and a conversation offers the same thing for ever
/// and offers its closing line from the start.
/// </remarks>
public sealed class TopicTests
{
    /// <summary>The shape of a real VERBS.TXT, cut down to what is read.</summary>
    private const string VerbsFile = """
        [VERBS]
        LOOK, up=v_look_std, type=Normal
        OPEN, up=v_open_std
        BLACK_MARKER, up=i_blkmarker_std, type=Inventory
        T_INTRODUCE, up=i_intro_std, type=Topic
        T_TOUR_GROUP, up=i_tour_std, type=Topic
        T_HANDSHAKE, up=i_shake_std, type=RecurringTopic
        """;

    private static VerbLibrary Verbs => VerbLibrary.Parse(VerbsFile);

    private static ActionResolver Resolver(GameState state, string file)
    {
        var resolver = new ActionResolver(new Gk3SheepApi(state)) { Verbs = Verbs };

        resolver.Add(NvcFile.Parse(file, "test.nvc", new DiagnosticBag()));

        return resolver;
    }

    private static IReadOnlyList<string> Topics(ActionResolver resolver, string noun) =>
        [.. resolver.Resolve(noun).Select(a => a.LocalizedVerb).Where(Verbs.IsTopic)];

    [Fact]
    public void The_file_says_which_verbs_are_things_to_say()
    {
        VerbLibrary verbs = Verbs;

        Assert.True(verbs.IsTopic("T_INTRODUCE"));
        Assert.True(verbs.IsTopic("T_HANDSHAKE"));
        Assert.False(verbs.IsTopic("LOOK"));
        Assert.False(verbs.IsTopic("BLACK_MARKER"));
        Assert.Equal(VerbKind.Inventory, verbs.KindOf("BLACK_MARKER"));

        // A verb with no type at all is an ordinary one.
        Assert.Equal(VerbKind.Normal, verbs.KindOf("OPEN"));

        // And a verb the file never mentions, which is how a partial answer behaves.
        Assert.Equal(VerbKind.Normal, verbs.KindOf("INVENTED"));
        Assert.False(verbs.IsTopic(null));
    }

    [Fact]
    public void A_topic_is_said_once_and_then_gone()
    {
        var state = new GameState();
        ActionResolver resolver = Resolver(
            state, "EMILIO, T_INTRODUCE, 1ST_TIME, script={}");

        Assert.Equal(["T_INTRODUCE"], Topics(resolver, "EMILIO"));

        state.Said("EMILIO", "T_INTRODUCE", "1ST_TIME");

        Assert.Empty(Topics(resolver, "EMILIO"));
    }

    [Fact]
    public void A_recurring_topic_may_be_raised_again()
    {
        // One verb in the game is declared this way, and the rule has to be read off the
        // file rather than off the T_ prefix, which every topic shares.
        var state = new GameState();
        ActionResolver resolver = Resolver(state, "EMILIO, T_HANDSHAKE, ALL, script={}");

        Assert.Equal(["T_HANDSHAKE"], Topics(resolver, "EMILIO"));

        state.Said("EMILIO", "T_HANDSHAKE", "ALL");

        Assert.Equal(["T_HANDSHAKE"], Topics(resolver, "EMILIO"));
    }

    [Fact]
    public void An_ordinary_verb_can_be_done_again_and_again()
    {
        // The rule is about topics and nothing else. Looking at a painting twice is fine.
        var state = new GameState();
        ActionResolver resolver = Resolver(state, "PAINTING, LOOK, ALL, script={}");

        state.Said("PAINTING", "LOOK", "ALL");

        Assert.Equal(["LOOK"], resolver.Resolve("PAINTING").Select(a => a.LocalizedVerb));
    }

    [Fact]
    public void A_topics_closing_line_waits_until_the_others_are_used()
    {
        // ALL on a topic is the last thing there is to say, not something always available.
        // Read as "always", this line is offered from the very start and can be repeated
        // for ever — which is a conversation showing what the player has not got to yet.
        var state = new GameState();
        ActionResolver resolver = Resolver(
            state,
            "BUTHANE, T_TOUR_GROUP, 1ST_TIME, script={}\n" +
            "BUTHANE, T_TOUR_GROUP, ALL, script={}");

        // Two lines, none said: the first-time line is the one offered.
        Assert.Equal(["T_TOUR_GROUP"], Topics(resolver, "BUTHANE"));
        Assert.Equal("1ST_TIME", resolver.Find("BUTHANE", "T_TOUR_GROUP")!.Case);

        state.SetTopicCount("BUTHANE", "T_TOUR_GROUP", 1);
        state.Said("BUTHANE", "T_TOUR_GROUP", "1ST_TIME");

        // One said of two: now the closing line is what is left.
        Assert.Equal("ALL", resolver.Find("BUTHANE", "T_TOUR_GROUP")!.Case);

        state.SetTopicCount("BUTHANE", "T_TOUR_GROUP", 2);
        state.Said("BUTHANE", "T_TOUR_GROUP", "ALL");

        Assert.Empty(Topics(resolver, "BUTHANE"));
    }

    [Fact]
    public void Without_the_verb_file_nothing_is_a_topic()
    {
        // A partial answer rather than a wrong one: every line is offered and none is used
        // up, which is exactly what the game did before the file was read.
        var state = new GameState();
        var resolver = new ActionResolver(new Gk3SheepApi(state));

        resolver.Add(NvcFile.Parse(
            "EMILIO, T_INTRODUCE, ALL, script={}", "test.nvc", new DiagnosticBag()));

        state.Said("EMILIO", "T_INTRODUCE", "ALL");

        Assert.Equal(["T_INTRODUCE"], resolver.Resolve("EMILIO").Select(a => a.LocalizedVerb));
    }

    [Fact]
    public void A_conversation_is_something_the_story_is_in_or_not()
    {
        var api = new Gk3SheepApi(new GameState());

        Assert.Null(api.State.Conversation);
        Assert.Equal(0, api.Invoke("InConversation", []).AsInt());

        api.Invoke("SetConversation", [SheepValue.FromString("Buth")]);

        Assert.Equal("Buth", api.State.Conversation);
        Assert.Equal(1, api.Invoke("InConversation", []).AsInt());

        api.Invoke("EndConversation", []);

        Assert.Null(api.State.Conversation);
    }
}
