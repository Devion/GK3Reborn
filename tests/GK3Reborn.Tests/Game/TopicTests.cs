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
        LOOK, up=v_look_std, hover=v_look_hov, type=Normal
        OPEN, up=v_open_std
        CLICK, type=Normal
        BLACK_MARKER, up=i_blkmarker_std, hover=i_blkmarker_hov, type=Inventory
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

    /// <summary>A person who can be talked to and asked about two things.</summary>
    private const string TalkingFile = """
        BUTHANE, LOOK, ALL, script={}
        BUTHANE, TALK, DIALOGUE_TOPICS_LEFT, script={}
        BUTHANE, T_INTRODUCE, ALL, script={}
        BUTHANE, T_TOUR_GROUP, ALL, script={}
        """;

    private static IReadOnlyList<string> Offered(ActionResolver resolver, string noun) =>
        [.. resolver.Resolve(noun).Select(a => a.LocalizedVerb)];

    [Fact]
    public void A_talk_that_only_opens_the_topic_list_is_not_offered_beside_it()
    {
        // DIALOGUE_TOPICS_LEFT means "there is something to ask about", and in the original
        // choosing Talk there opened the list of those things. The list is on the menu
        // itself here, so Talk would be a door into the room the player is standing in.
        IReadOnlyList<string> offered = Offered(
            Resolver(new GameState(), TalkingFile), "BUTHANE");

        Assert.Contains("T_INTRODUCE", offered);
        Assert.Contains("T_TOUR_GROUP", offered);
        Assert.Contains("LOOK", offered);
        Assert.DoesNotContain("TALK", offered);
    }

    [Fact]
    public void A_talk_that_does_something_of_its_own_stays()
    {
        // Ninety-five of the corpus's 127 Talk rules are guarded by something other than
        // DIALOGUE_TOPICS_LEFT, and they are the ones with a line or a conversation behind
        // them. Larry's is one: he has no topics at all and a voice-over to play.
        IReadOnlyList<string> offered = Offered(
            Resolver(new GameState(), """
                LARRY, LOOK, ALL, script={}
                LARRY, TALK, ALL, script={}
                """),
            "LARRY");

        Assert.Contains("TALK", offered);
    }

    [Fact]
    public void The_talk_for_when_there_is_nothing_left_to_ask_stays()
    {
        // The other half of the pair, and the reason this cannot simply drop every Talk.
        // Nine rules in the corpus are guarded by NOT_DIALOGUE_TOPICS_LEFT: they are what a
        // character says once the player has run out of things to ask them, and they are
        // the only thing that verb reaches at that point.
        //
        // Its opposite needs no filtering at all — with no topics left the case is not
        // satisfied and the rule never reaches the menu.
        IReadOnlyList<string> offered = Offered(
            Resolver(new GameState(), """
                EMILIO, TALK, NOT_DIALOGUE_TOPICS_LEFT, script={}
                """),
            "EMILIO");

        Assert.Contains("TALK", offered);
    }

    [Fact]
    public void Without_the_verb_file_nothing_is_hidden()
    {
        // Whether a verb is a topic is only knowable from VERBS.TXT. Without it the port
        // cannot tell that Talk duplicates anything, and showing one verb too many beats
        // hiding one the player needs.
        var resolver = new ActionResolver(new Gk3SheepApi(new GameState()));
        resolver.Add(NvcFile.Parse(TalkingFile, "test.nvc", new DiagnosticBag()));

        Assert.Contains("TALK", Offered(resolver, "BUTHANE"));
    }

    [Fact]
    public void The_file_also_says_what_each_verb_looks_like()
    {
        // The original's verb ring was pictures and no words at all, so this file is the
        // only place that says which picture belongs to which verb. The names in it are
        // lowercase and extensionless; what is in the archives is neither.
        VerbLibrary verbs = Verbs;

        Assert.Equal("V_LOOK_STD.BMP", verbs.IconOf("LOOK"));
        Assert.Equal("V_LOOK_HOV.BMP", verbs.IconOf("LOOK", lit: true));

        // Asked for by whatever case the action file happened to write.
        Assert.Equal("I_BLKMARKER_STD.BMP", verbs.IconOf("black_marker"));
    }

    [Fact]
    public void A_verb_with_no_lit_picture_is_drawn_resting()
    {
        // WALK_DOWN in the shipped file names an up and a down and no hover. Falling back
        // to the resting picture keeps the row drawn; leaving it null would make the icon
        // vanish at exactly the moment the player put the pointer on it.
        Assert.Equal("V_OPEN_STD.BMP", Verbs.IconOf("OPEN", lit: true));
    }

    [Fact]
    public void A_verb_the_file_gives_no_picture_answers_with_nothing()
    {
        // Three of the 287 name no art — CLICK, SELECT and WRITE. Those are drawn by their
        // word alone rather than by a blank square, and so is a verb the file never lists.
        VerbLibrary verbs = Verbs;

        Assert.Null(verbs.IconOf("CLICK"));
        Assert.Null(verbs.IconOf("NOT_A_VERB"));
        Assert.Null(verbs.IconOf(null));
        Assert.Equal(6, verbs.IconCount);
    }
}
