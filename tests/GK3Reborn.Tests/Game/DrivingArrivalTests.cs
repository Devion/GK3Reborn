using GK3Reborn.Formats.Scenes;
using GK3Reborn.Game;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Tests for arriving somewhere on the moped.
/// </summary>
/// <remarks>
/// <para>
/// Reported as the moped being missing from Larry Chester's yard and the player being
/// unable to leave it. Both come from one thing: the map is a <em>location</em> in the
/// original — its location table lists <c>map</c> beside <c>lhe</c> and <c>mop</c>, and the
/// driving layer holds that entry as its own — and a ride that set the destination straight
/// from the room the player left never passed through it.
/// </para>
/// <para>
/// The fixture is <c>LHE.SIF</c> reduced to the three lines that turn on the answer: the
/// moped, the spot arrived at, and the doors this is not.
/// </para>
/// </remarks>
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
    /// <remarks>
    /// Which is the whole fix: two moves rather than one, so the question every scene asks
    /// about a ride has the answer the game's own data was written against.
    /// </remarks>
    [Fact]
    public void Riding_the_moped_arrives_from_the_map()
    {
        var story = new GameState { Location = "MOP" };

        story.RideTo("LHE");

        Assert.Equal("LHE", story.Location);
        Assert.Equal(DrivingMap.Location, story.LastLocation);
    }

    /// <summary>And not from the room the moped was ridden out of.</summary>
    /// <remarks>
    /// The failure as reported. <c>LHE</c> names no spot to arrive at from <c>MOP</c>, so
    /// the player stood at the origin; the moped's line is declared under
    /// <c>WasLastLocation("Map")</c>, so there was no moped; and the yard's only way back to
    /// the map is an exit guarded by the moped being there, so there was no way out either.
    /// </remarks>
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
    /// <remarks>
    /// The other half of the same condition, and what says the fix is a fact about the ride
    /// rather than a model turned on for everybody. Larry's house is where a ride is
    /// remembered as a game variable too, so this is the state before the script that sets
    /// it has ever run.
    /// </remarks>
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
    /// <remarks>
    /// The condition's other arm, which the yard's own enter script sets on the way in.
    /// Walking back out of the house after a ride finds the moped where it was left.
    /// </remarks>
    [Fact]
    public void The_moped_stays_where_the_story_parked_it()
    {
        var story = new GameState { Location = "LHI" };
        story.Location = "LHE";
        story.SetVariable("BikeLocation", 11);

        Assert.Contains(Arriving(story).Models(), m => m.Name == "bikebody");
    }

    /// <summary>The player stands where the room says a ride arrives.</summary>
    /// <remarks>
    /// <c>FR_MAP</c>, by the artists' own convention — every one of the sixteen places the
    /// moped can be ridden to names one, and three of them go further and put it on the
    /// player's own line. Coming from <c>MOP</c> matched nothing and left them at the
    /// origin.
    /// </remarks>
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
    /// <remarks>
    /// Its moped and its way back to the map both ask for the same number, so a ride that
    /// did not set one left no moped in the field and no way off it.
    /// </remarks>
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
    /// <remarks>
    /// Which is the number the game's own scene files were written against. Six of them
    /// give a place one and every one of those is that place's position in the map's list.
    /// </remarks>
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
    /// <remarks>
    /// Reported as being stuck there. Its <c>EXIT_TO_MAP</c> is guarded by the same number
    /// as its moped, so the missing variable took the way out with the model.
    /// </remarks>
    [Fact]
    public void Riding_to_blanchefort_leaves_a_moped_to_ride_away_on()
    {
        var story = new GameState { Location = "MOP" };
        story.RideTo("PLO");

        Assert.Contains(Field(story).Models(), m => m.Name == "bikebody");
    }

    /// <summary>And riding away again takes it with you.</summary>
    /// <remarks>
    /// One variable says where the moped is, so parking it somewhere new is what empties
    /// the place it was. Larry Chester's driveway draws the moped from the yard's own
    /// number, which is the same fact seen from next door.
    /// </remarks>
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
    /// <remarks>
    /// Blanchefort's condition is the number <em>and</em> not being at 202A, which is the
    /// timeblock the moped is taken out of the player's hands for. Parking it there must
    /// not put it back.
    /// </remarks>
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
