using System.Numerics;
using GK3Reborn.Formats.Scenes;
using GK3Reborn.Game;
using GK3Reborn.Game.Actors;
using GK3Reborn.Game.Story;
using GK3Reborn.Rendering;
using GK3Reborn.UI;
using Xunit;

namespace GK3Reborn.Tests.Game;

public sealed class DayOneRegressionTests
{
    [Fact]
    public void Intro_setting_toggles_without_launching_the_movie()
    {
        var front = new FrontEnd(new Settings { PlayIntro = true });
        front.Show(FrontEndPage.Gameplay);
        Assert.Equal(FrontEndOutcome.Stay, front.Choose(new MenuAction("intro")));
        Assert.False(front.Settings.PlayIntro);
        front.Choose(new MenuAction("intro"));
        Assert.True(front.Settings.PlayIntro);
        front.Show(FrontEndPage.Main);
        Assert.Equal(FrontEndOutcome.Intro, front.Choose(new MenuAction("intro")));
    }

    [Theory]
    [InlineData(1, "e_102p_map_follow_buthane", "CSD")]
    [InlineData(2, "e_102p_map_follow_wilkes", "LER")]
    [InlineData(5, "e_106p_map_follow_two_men", null)]
    public void Finishing_a_map_chase_records_its_story_outcome_once(int follow, string score, string? location)
    {
        var state = new GameState { Location = "MOP", Timeblock = new Timeblock(1, follow == 5 ? 6 : 2, true) };
        var traffic = DrivingTraffic.For(state, DrivingMap.Empty, follow);
        traffic.Skip();
        string? destination = traffic.Complete(state);
        Assert.True(state.HasScored(score));
        int points = state.Score;
        traffic.Complete(state);
        Assert.Equal(points, state.Score);
        Assert.Equal(2, points);
        if (follow == 5)
        {
            Assert.Equal("PLO", destination);
            Assert.Equal(5, state.GetVariable("TwoMenState"));
            state.RideTo(destination!);
            Assert.Equal(12, state.GetVariable("BikeLocation"));
        }
        else
        {
            Assert.Null(destination);
            Assert.Equal(location, state.GetActorLocation(traffic.Chase!.Noun));
        }
    }

    [Theory]
    [InlineData("ABBE", "T_INTRODUCE")]
    [InlineData("BUTHANE", "T_TOUR_GROUP")]
    public void Gabriels_conversations_do_not_complete_Graces_topics(string noun, string topic)
    {
        var state = new GameState { Ego = "GABRIEL" };
        state.SetTopicCount(noun, topic, 1);
        state.Said(noun, topic, "FIRST");
        var restored = new GameState();
        restored.Restore(state.Capture("test"));
        restored.Ego = "GRACE";
        Assert.Equal(0, restored.GetTopicCount(noun, topic));
        Assert.False(restored.HasSaid(noun, topic, "FIRST"));
        restored.SetTopicCount(noun, topic, 2);
        restored.Ego = "GABRIEL";
        Assert.Equal(1, restored.GetTopicCount(noun, topic));
        Assert.True(restored.HasSaid(noun, topic, "FIRST"));
    }

    [Fact]
    public void Wilkes_rocks_complete_when_discussed_and_remain_complete_as_Grace()
    {
        var state = new GameState { Timeblock = new Timeblock(1, 4, true) };
        var journal = new Journal(state);
        Assert.Contains(journal.Now(), e => e.Quest.TitleKey == "quest.104P.3");
        state.SetTopicCount("WILKES", "T_ROQUE_NEGRE", 1);
        Assert.DoesNotContain(journal.Now(), e => e.Quest.TitleKey == "quest.104P.3");
        state.Ego = "GRACE";
        Assert.DoesNotContain(journal.Now(), e => e.Quest.TitleKey == "quest.104P.3");
    }

    [Fact]
    public void Old_day_two_morning_saves_do_not_give_Gabriels_introductions_to_Grace()
    {
        var state = new GameState { Ego = "GRACE", Timeblock = new Timeblock(2, 7, false) };
        SaveGame legacy = state.Capture("legacy") with
        {
            SchemaVersion = 3,
            TopicCounts = new Dictionary<string, int> { ["ABBE|T_INTRODUCE"] = 1 },
            SaidTopics = ["ABBE\u0001T_INTRODUCE\u0001FIRST"],
        };
        state.Restore(legacy);
        Assert.Equal(0, state.GetTopicCount("ABBE", "T_INTRODUCE"));
        Assert.False(state.HasSaid("ABBE", "T_INTRODUCE", "FIRST"));
        state.Ego = "GABRIEL";
        Assert.Equal(1, state.GetTopicCount("ABBE", "T_INTRODUCE"));
        Assert.True(state.HasSaid("ABBE", "T_INTRODUCE", "FIRST"));
        state.Restore(state.Capture("upgraded"));
        Assert.Equal(1, state.GetTopicCount("ABBE", "T_INTRODUCE"));

        state.Restore(legacy with { Scored = ["e_207a_ma3_talk_abbe_introduce"] });
        Assert.Equal("GRACE", state.Ego);
        Assert.Equal(1, state.GetTopicCount("ABBE", "T_INTRODUCE"));
    }

    [Theory]
    [InlineData(2, "quest.102P.12", "e_notebook_wilkes_license,e_talk_wilkes_machine,e_talk_wilkes_treasure,e_talk_wilkes_grail")]
    [InlineData(6, "quest.106P.1", "e_notebook_howard_estelle_license,e_notebook_buchelli_license")]
    public void Journal_objectives_complete_from_their_own_actions(int hour, string key, string awards)
    {
        var state = new GameState { Timeblock = new Timeblock(1, hour, true) };
        var journal = new Journal(state);
        state.AwardScore("e_102p_tr2_think_about_arrivals", 2);
        Assert.Contains(journal.Now(), e => e.Quest.TitleKey == key);
        foreach (string award in awards.Split(','))
        {
            state.AwardScore(award, 2);
        }
        Assert.DoesNotContain(journal.Now(), e => e.Quest.TitleKey == key);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Leaving_inspect_restores_the_actual_previous_view(bool firstPerson)
    {
        var scene = new LoadedScene("TEST", new SceneDefinition(SceneInitFile.Parse(
            "[INSPECT_CAMERAS]\nnoun=BOOK, pos={100,20,30}, angle={0,0}", "TEST.SIF")), null, null, 0);
        var state = new GameState { FirstPerson = firstPerson };
        var world = new SceneUpdate(scene, new Gk3SheepApi(state), new Glances(), new HeadlessSceneSink());
        var before = new Camera { Position = new Vector3(3, 4, 5), Target = new Vector3(6, 7, 8) };
        world.StartAt(before);
        world.Elsewhere = before;
        state.Inspecting = "BOOK";
        world.Advance(1);
        Assert.NotEqual(before.Position, world.View!.Position);
        world.Elsewhere = null;
        world.Advance(1);
        Assert.True(world.Framed);
        state.Inspecting = "";
        world.Advance(1);
        Assert.Equal(before.Position, world.View!.Position);
        Assert.Equal(before.Target, world.View.Target);
        Assert.False(world.Framed);
        world.Advance(1);
        Assert.Equal(before.Position, world.View.Position);
    }
}
