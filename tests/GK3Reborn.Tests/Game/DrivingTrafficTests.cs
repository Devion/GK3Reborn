using System.Numerics;
using GK3Reborn.Game;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Tests for the traffic on the driving map.
/// </summary>
/// <remarks>
/// <para>
/// Reported: clicking Wilkes's moped or Madeleine's van as they went past the Blanchefort
/// parking lot brought the map up again and nothing else happened, so L'Ermitage, Coume
/// Sourde and L'Homme Mort could not be discovered at all. Two things were missing — the
/// chase itself, which <c>FollowOnDrivingMap</c> was recording rather than running, and
/// anybody on the map to give chase to.
/// </para>
/// <para>
/// The road network here is invented. It carries the junction names the game's own routes
/// are written in, because those are what the code joins up, and none of its coordinates
/// or bends are the shipped file's: what is being tested is that a route becomes a road,
/// not what the Aude valley looks like.
/// </para>
/// </remarks>
public sealed class DrivingTrafficTests
{
    /// <summary>Eight junctions of a valley that is not the game's.</summary>
    /// <remarks>
    /// Enough of a network to have the two shapes that matter: a leg that is one road, and
    /// a leg between two junctions with no road between them.
    /// </remarks>
    private const string Roads =
        """
        NodeBegin Plo
        	Location 500,100
        	LinksBegin
        		Pl3 Plo_Pl3 TRUE
        	LinksEnd
        NodeEnd

        NodeBegin Pl3
        	Location 500,150
        	LinksBegin
        		Plo Plo_Pl3 FALSE
        		Rl1 Rl1_Pl3 FALSE
        	LinksEnd
        NodeEnd

        NodeBegin Rl1
        	Location 490,190
        	LinksBegin
        		Pl3 Rl1_Pl3 TRUE
        		In4 Rl1_In4 TRUE
        	LinksEnd
        NodeEnd

        NodeBegin In4
        	Location 470,230
        	LinksBegin
        		Rl1 Rl1_In4 FALSE
        		In3 In4_In3 TRUE
        		Vgr Vgr_In4 FALSE
        	LinksEnd
        NodeEnd

        NodeBegin In3
        	Location 460,220
        	LinksBegin
        		In4 In4_In3 FALSE
        		Pl2 In3_Pl2 TRUE
        	LinksEnd
        NodeEnd

        NodeBegin Pl2
        	Location 430,220
        	LinksBegin
        		In3 In3_Pl2 FALSE
        	LinksEnd
        NodeEnd

        NodeBegin Vgr
        	Location 475,260
        	LinksBegin
        		In4 Vgr_In4 TRUE
        		Pl4 Pl4_Vgr FALSE
        	LinksEnd
        NodeEnd

        NodeBegin Pl4
        	Location 460,290
        	LinksBegin
        		Vgr Pl4_Vgr TRUE
        	LinksEnd
        NodeEnd

        SegmentBegin Plo_Pl3
        	PointListBegin
        		Point 512,120
        		Point 508,136
        	PointListEnd
        SegmentEnd
        """;

    private static DrivingMap Valley => DrivingMap.Roading(Roads);

    /// <summary>A point in the story, by its code.</summary>
    private static Timeblock Block(string code) =>
        Timeblock.TryParse(code, out Timeblock parsed) ? parsed : default;

    /// <summary>A leg with a road under it bends the way the road does.</summary>
    /// <remarks>
    /// The whole reason the segments are read at all. Without them the moped flies between
    /// villages in a straight line over a painting of a valley whose roads are drawn on it.
    /// </remarks>
    [Fact]
    public void A_route_follows_the_road_rather_than_the_straight_line()
    {
        IReadOnlyList<Vector2> road = Valley.Route(["plo", "pl3"]);

        Assert.Equal(
            [new Vector2(500, 100), new Vector2(512, 120), new Vector2(508, 136), new Vector2(500, 150)],
            road);
    }

    /// <summary>And it is read backwards from the end the file did not name it for.</summary>
    [Fact]
    public void The_same_road_taken_the_other_way_runs_the_other_way()
    {
        IReadOnlyList<Vector2> road = Valley.Route(["pl3", "plo"]);

        Assert.Equal(
            [new Vector2(500, 150), new Vector2(508, 136), new Vector2(512, 120), new Vector2(500, 100)],
            road);
    }

    /// <summary>
    /// A leg between two junctions with no road between them goes the way round there is.
    /// </summary>
    /// <remarks>
    /// Madeleine's drive to Coume Sourde is written <c>plo/pl3/rl1/in4/pl2</c> and there is
    /// no road from <c>In4</c> to <c>Pl2</c>. Read literally her van crosses a hillside.
    /// </remarks>
    [Fact]
    public void A_leg_with_no_road_of_its_own_is_joined_up_through_the_network()
    {
        IReadOnlyList<Vector2> road = Valley.Route(["in4", "pl2"]);

        Assert.Equal(
            [new Vector2(470, 230), new Vector2(460, 220), new Vector2(430, 220)],
            road);
    }

    /// <summary>Two people are out on the roads on the afternoon of the first day.</summary>
    [Fact]
    public void Wilkes_and_Madeleine_are_out_on_the_first_afternoon()
    {
        var story = new GameState { Timeblock = Block("102P") };

        Assert.Equal(
            ["WILKES", "BUTHANE"],
            DrivingTraffic.Circling(story).Select(t => t.Noun));
    }

    /// <summary>And each of them stops once they have been followed.</summary>
    /// <remarks>
    /// Which is what they are out there for. The room's own action writes the count — see
    /// <c>PLO102P.NVC</c> — and the retail driving layer asks the same question.
    /// </remarks>
    [Fact]
    public void Somebody_already_followed_is_no_longer_on_the_roads()
    {
        var story = new GameState { Timeblock = Block("102P") };

        story.SetNounVerbCount("WILKES", DrivingMap.Follow, 1);

        Assert.Equal(["BUTHANE"], DrivingTraffic.Circling(story).Select(t => t.Noun));
    }

    /// <summary>Nobody is out at a point in the story where nobody is going anywhere.</summary>
    [Fact]
    public void The_roads_are_empty_when_the_story_has_nobody_on_them()
    {
        var story = new GameState { Timeblock = Block("110A") };

        Assert.Empty(DrivingTraffic.Circling(story));
    }

    /// <summary>Following Wilkes ends at L'Ermitage and puts it on the map.</summary>
    [Fact]
    public void Following_Wilkes_arrives_at_LErmitage()
    {
        Traveller chase = Assert.IsType<Traveller>(DrivingTraffic.Chased(2, "PLO"));

        Assert.Equal("WILKES", chase.Noun);
        Assert.Equal("PL4", chase.Arrives);
        Assert.Equal(["PL4"], chase.Reveals);
    }

    /// <summary>
    /// Following Madeleine ends at Coume Sourde, and the road she takes depends on where
    /// the chase started.
    /// </summary>
    [Fact]
    public void Following_Madeleine_takes_the_road_out_of_wherever_the_chase_began()
    {
        Traveller fromBlanchefort = Assert.IsType<Traveller>(DrivingTraffic.Chased(1, "PLO"));
        Traveller fromErmitage = Assert.IsType<Traveller>(DrivingTraffic.Chased(1, "PL4"));

        Assert.Equal("plo", fromBlanchefort.Junctions[0]);
        Assert.Equal("pl4", fromErmitage.Junctions[0]);
        Assert.Equal("PL2", fromBlanchefort.Arrives);
        Assert.Equal("PL2", fromErmitage.Arrives);

        // Both of them put L'Homme Mort on the map as well: it is a mile up the same road,
        // and the retail layer reveals the two together.
        Assert.Equal(["PL2", "PL1"], fromErmitage.Reveals);
    }

    /// <summary>A number that describes no chase starts none.</summary>
    [Fact]
    public void A_follow_number_the_game_never_uses_is_not_a_chase()
    {
        Assert.Null(DrivingTraffic.Chased(9, "PLO"));
    }

    /// <summary>The player rides behind the person they are following, not beside them.</summary>
    [Fact]
    public void The_player_starts_behind_whoever_they_are_chasing()
    {
        var story = new GameState { Timeblock = Block("102P"), Location = "PLO" };
        DrivingTraffic traffic = DrivingTraffic.For(story, Valley, follow: 2);

        Assert.True(traffic.Following);
        Assert.Equal(2, traffic.Riders.Count);

        traffic.Advance(0.5);

        DrivingTraffic.Rider quarry = traffic.Riders.First(r => !r.IsPlayer);
        DrivingTraffic.Rider player = traffic.Riders.First(r => r.IsPlayer);

        Assert.NotEqual(quarry.At, player.At);
        Assert.False(traffic.Arrived);
    }

    /// <summary>A player who does not want to watch it is put at the end of it.</summary>
    [Fact]
    public void Skipping_a_chase_puts_everybody_where_it_was_going()
    {
        var story = new GameState { Timeblock = Block("102P"), Location = "PLO" };
        DrivingTraffic traffic = DrivingTraffic.For(story, Valley, follow: 2);

        traffic.Skip();

        Assert.True(traffic.Arrived);

        foreach (DrivingTraffic.Rider rider in traffic.Riders)
        {
            Assert.Equal(new Vector2(460, 290), rider.At);
        }
    }

    /// <summary>Watching it to the end arrives at the same place.</summary>
    [Fact]
    public void A_chase_watched_all_the_way_through_arrives()
    {
        var story = new GameState { Timeblock = Block("102P"), Location = "PLO" };
        DrivingTraffic traffic = DrivingTraffic.For(story, Valley, follow: 2);

        for (int frame = 0; frame < 600 && !traffic.Arrived; frame++)
        {
            traffic.Advance(1.0 / 60);
        }

        Assert.True(traffic.Arrived);
        Assert.Equal(new Vector2(460, 290), traffic.Riders.First(r => !r.IsPlayer).At);
    }

    /// <summary>Somebody circling goes round again rather than stopping.</summary>
    [Fact]
    public void Somebody_circling_comes_back_round()
    {
        var story = new GameState { Timeblock = Block("102P"), Location = "PLO" };
        DrivingTraffic traffic = DrivingTraffic.For(story, Valley);

        Assert.False(traffic.Following);
        Assert.NotEmpty(traffic.Riders);

        Vector2 where = traffic.Riders[0].At;

        for (int frame = 0; frame < 6000; frame++)
        {
            traffic.Advance(1.0 / 60);
        }

        // Still on the map after a hundred seconds, rather than parked at the end of a
        // road: a loop that stops is scenery that stops being noticed.
        Assert.NotEqual(where, traffic.Riders[0].At);
        Assert.False(traffic.Arrived);
    }

    /// <summary>
    /// Coume Sourde and L'Homme Mort arrive on the map when Madeleine has been followed.
    /// </summary>
    /// <remarks>
    /// The whole of what the chase is for. Before it, neither place exists as far as the
    /// map is concerned, and neither does L'Ermitage.
    /// </remarks>
    [Fact]
    public void Following_Madeleine_puts_Coume_Sourde_on_the_map()
    {
        var story = new GameState { Timeblock = Block("102P"), Location = "PLO" };

        Assert.DoesNotContain(DrivingMap.Open(story), s => s.Scene == "PL2");

        story.SetNounVerbCount("BUTHANE", DrivingMap.Follow, 2);

        Assert.Contains(DrivingMap.Open(story), s => s.Scene == "PL2");
        Assert.Contains(DrivingMap.Open(story), s => s.Scene == "PL1");
    }

    /// <summary>And L'Ermitage when Wilkes has.</summary>
    [Fact]
    public void Following_Wilkes_puts_LErmitage_on_the_map()
    {
        var story = new GameState { Timeblock = Block("102P"), Location = "PLO" };

        Assert.DoesNotContain(DrivingMap.Open(story), s => s.Scene == "PL4");

        story.SetNounVerbCount("WILKES", DrivingMap.Follow, 2);

        Assert.Contains(DrivingMap.Open(story), s => s.Scene == "PL4");
    }

    /// <summary>
    /// And the story hands them over anyway once it has moved past the afternoon they
    /// belong to.
    /// </summary>
    /// <remarks>
    /// The retail layer's own safety net, and the reason a player who missed the chase is
    /// not locked out of the second half of the first day.
    /// </remarks>
    [Fact]
    public void The_afternoon_after_puts_all_three_on_the_map_regardless()
    {
        var story = new GameState { Timeblock = Block("104P"), Location = "PLO" };

        Assert.Contains(DrivingMap.Open(story), s => s.Scene == "PL4");
        Assert.Contains(DrivingMap.Open(story), s => s.Scene == "PL2");
        Assert.Contains(DrivingMap.Open(story), s => s.Scene == "PL1");
    }

    /// <summary>The five places the map opens with are still the five it opens with.</summary>
    [Fact]
    public void The_first_ride_still_offers_five_places()
    {
        var story = new GameState { Timeblock = Block("110A"), Location = "MOP" };

        Assert.Equal(
            ["MOP", "LHE", "PLO", "RL1", "TR1"],
            DrivingMap.Open(story).Select(s => s.Scene));
    }
}
