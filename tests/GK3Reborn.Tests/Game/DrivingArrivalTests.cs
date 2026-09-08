using GK3Reborn.Formats.Scenes;
using GK3Reborn.Game;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Tests for arriving somewhere on the moped.
/// </summary>
public sealed class DrivingArrivalTests
{
    /// <summary>Larry Chester's yard, cut down to what a ride decides.</summary>
    private const string LarrysHouse =
        """
        [ACTORS]
        model=gab,noun=GABRIEL,idle=gabIdle.gas,talk=gabTalk.gas,ego

        [MODELS={(GetGameVariableInt("BikeLocation")==11) || WasLastLocation("Map") }]
        model=bikebody, noun=GABES_MOPED, type=prop, initanim=gabgetsonbikelhe, shadow

        [MODELS]
        model=lhe_house,noun=HOUSE,type=scene

        [POSITIONS]
        FR_CDB, pos={78.67, 1.15, 912.40},  heading=117.31, camera=FR_CDB
        FR_LHI, pos={240.72, 0.25, 343.62}, heading=182.00, camera=RC_HOUSE
        FR_MAP, pos={176.52, 0.63, 123.20}, heading=353.00, camera=FR_MAP
        """;

    /// <summary>Riding somewhere leaves the player having arrived from the map.</summary>
    [Fact]
    public void Riding_the_moped_arrives_from_the_map()
    {
        var story = new GameState { Location = "MOP" };

        story.RideTo("LHE");

        Assert.Equal("LHE", story.Location);
        Assert.Equal(DrivingMap.Location, story.LastLocation);
    }

    /// <summary>And not from the room the moped was ridden out of.</summary>
    [Fact]
    public void Riding_the_moped_does_not_arrive_from_the_room_it_was_ridden_out_of()
    {
        var story = new GameState { Location = "MOP" };

        story.RideTo("LHE");

        Assert.NotEqual("MOP", story.LastLocation);
    }

    /// <summary>Gabriel's moped is standing in the yard when he rides in.</summary>
    [Fact]
    public void The_moped_is_in_the_yard_after_a_ride_to_it()
    {
        Assert.Contains(Arriving(Ride("LHE")).Models(), m => m.Name == "bikebody");
    }

    /// <summary>
    /// And is not there when he walked out of the house instead.
    /// </summary>
    [Fact]
    public void The_moped_is_not_in_the_yard_after_walking_out_of_the_house()
    {
        var story = new GameState { Location = "LHI" };
        story.Location = "LHE";

        Assert.DoesNotContain(Arriving(story).Models(), m => m.Name == "bikebody");
    }

    /// <summary>
    /// A ride to a place the story has already parked the moped at still shows it.
    /// </summary>
    [Fact]
    public void The_moped_stays_where_the_story_parked_it()
    {
        var story = new GameState { Location = "LHI" };
        story.Location = "LHE";
        story.SetVariable("BikeLocation", 11);

        Assert.Contains(Arriving(story).Models(), m => m.Name == "bikebody");
    }

    /// <summary>The player stands where the room says a ride arrives.</summary>
    [Fact]
    public void The_player_arrives_at_the_spot_the_room_keeps_for_a_ride()
    {
        GameState story = Ride("LHE");

        ScenePosition? spot = Arriving(story).StartPosition(story.LastLocation);

        Assert.NotNull(spot);
        Assert.Equal("FR_MAP", spot.Name, ignoreCase: true);
    }

    /// <summary>Walking in through a door still arrives at that door.</summary>
    [Fact]
    public void A_ride_does_not_change_where_a_door_arrives()
    {
        Assert.Equal(
            "FR_LHI",
            Arriving(new GameState()).StartPosition("LHI")?.Name,
            ignoreCase: true);
    }

    /// <summary>Blanchefort, cut down to the two lines that stranded the player there.</summary>
    private const string Blanchefort =
        """
        [ACTORS]
        model=gab,noun=GABRIEL,idle=gabIdle.gas,talk=gabTalk.gas,ego

        [MODELS={(GetGameVariableInt("BikeLocation")==12) && !IsCurrentTime("202a")}]
        model=bikebody, noun=GABES_MOPED, type=prop, initanim=gabgetsonbikeplo, shadow

        [POSITIONS]
        FR_MAP, pos={1724.02, 2.81, 1739.40}, heading=91.69, camera=FR_MAP
        """;

    /// <summary>Riding somewhere parks the moped there.</summary>
    [Theory]
    [InlineData("PL2", 3)]
    [InlineData("PL1", 4)]
    [InlineData("PL6", 9)]
    [InlineData("MOP", 10)]
    [InlineData("LHE", 11)]
    [InlineData("PLO", 12)]
    public void Riding_somewhere_parks_the_moped_there(string scene, int parked)
    {
        var story = new GameState { Location = "TR1" };

        story.RideTo(scene);

        Assert.Equal(parked, story.GetVariable(DrivingMap.Parked));
        Assert.Equal(parked, DrivingMap.ParkedAt(scene));
    }

    /// <summary>Somewhere the moped does not go has no number at all.</summary>
    [Fact]
    public void A_room_the_moped_cannot_reach_is_not_a_place_to_park_it()
    {
        Assert.Null(DrivingMap.ParkedAt("R25"));
    }

    /// <summary>
    /// Riding to Blanchefort leaves a moped in the field to ride away on.
    /// </summary>
    [Fact]
    public void Riding_to_blanchefort_leaves_a_moped_to_ride_away_on()
    {
        var story = new GameState { Location = "MOP" };
        story.RideTo("PLO");

        Assert.Contains(Field(story).Models(), m => m.Name == "bikebody");
    }

    /// <summary>And riding away again takes it with you.</summary>
    [Fact]
    public void Riding_away_takes_the_moped_with_you()
    {
        var story = new GameState { Location = "MOP" };
        story.RideTo("PLO");
        story.RideTo("LHE");

        Assert.DoesNotContain(Field(story).Models(), m => m.Name == "bikebody");
        Assert.Contains(Arriving(story).Models(), m => m.Name == "bikebody");
    }

    /// <summary>
    /// A place that hides its moped for the story still hides it.
    /// </summary>
    [Fact]
    public void Parking_the_moped_does_not_override_the_story()
    {
        var story = new GameState { Location = "MOP", Timeblock = new Timeblock(2, 2, IsAfternoon: false) };
        story.RideTo("PLO");

        Assert.DoesNotContain(Field(story).Models(), m => m.Name == "bikebody");
    }

    /// <summary>Blanchefort, read against the story as it stands.</summary>
    private static SceneDefinition Field(GameState story)
    {
        var conditions = new SceneConditions(new Gk3SheepApi(story));
        var scene = new SceneDefinition(
            SceneInitFile.Parse(Blanchefort, "PLO.SIF", conditions.Applies));

        Assert.Empty(conditions.Diagnostics.Items);

        return scene;
    }

    /// <summary>A ride from somewhere to somewhere.</summary>
    private static GameState Ride(string destination)
    {
        var story = new GameState { Location = "MOP" };
        story.RideTo(destination);

        return story;
    }

    /// <summary>The yard, read against the story as it stands.</summary>
    private static SceneDefinition Arriving(GameState story)
    {
        var conditions = new SceneConditions(new Gk3SheepApi(story));
        var scene = new SceneDefinition(
            SceneInitFile.Parse(LarrysHouse, "LHE.SIF", conditions.Applies));

        Assert.Empty(conditions.Diagnostics.Items);

        return scene;
    }
}
