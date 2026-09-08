using GK3Reborn.Formats.Scenes;
using GK3Reborn.Game;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Tests for where the player is standing when a room opens.
/// </summary>
public sealed class EgoArrivalTests
{
    /// <summary>
    /// The phone room, as it ships: a player with no position of their own, no
    /// <c>START</c>, and a first entry belonging to somebody else.
    /// </summary>
    private const string PhoneRoom = """
        [ACTORS]
        model=gab,noun=GABRIEL,idle=gabIdle.gas,talk=gabTalk.gas,ego

        [POSITIONS]
        EMILIO_HERE_1,pos={69.22,0.86,145.03},heading=254.25
        EMILIO_HERE_2,pos={117.22,0.86,145.03},heading=254.25
        FR_LBY, pos={83.85, 3.96, 31.91}, heading=2.97, camera=FR_LBY
        TO_LBY, pos={87.45, 3.33, 15.99}, heading=193.28, camera=FR_LBY
        BOOTH1, pos={24.1,1.43,152.6}, heading=243.4
        """;

    private static SceneDefinition Scene(string text) =>
        new(SceneInitFile.Parse(text, "PHO.SIF"));

    /// <summary>Walking in from the lobby stands the player at the door from the lobby.</summary>
    [Fact]
    public void The_player_arrives_at_the_door_they_came_through()
    {
        ScenePosition? spot = Scene(PhoneRoom).StartPosition("LBY");

        Assert.NotNull(spot);
        Assert.Equal("FR_LBY", spot.Name, ignoreCase: true);
    }

    /// <summary>And never at whichever position the file happens to list first.</summary>
    [Fact]
    public void The_player_does_not_arrive_at_somebody_elses_spot()
    {
        Assert.Null(Scene(PhoneRoom).StartPosition("NOWHERE"));
        Assert.Null(Scene(PhoneRoom).StartPosition(null));
    }

    /// <summary>A scene that names a START still uses it.</summary>
    [Fact]
    public void A_scene_that_names_a_start_uses_it()
    {
        SceneDefinition scene = Scene("""
            [POSITIONS]
            SOMEWHERE_ELSE, pos={1,2,3}, heading=0
            START, pos={10,20,30}, heading=90
            """);

        Assert.Equal("START", scene.StartPosition()?.Name, ignoreCase: true);
    }

    /// <summary>The door the player came through outranks a START.</summary>
    [Fact]
    public void The_door_outranks_a_start()
    {
        SceneDefinition scene = Scene("""
            [POSITIONS]
            START, pos={10,20,30}, heading=90
            FR_LBY, pos={40,50,60}, heading=180
            """);

        Assert.Equal("FR_LBY", scene.StartPosition("LBY")?.Name, ignoreCase: true);
    }
}
