using GK3Reborn.Formats.Scenes;
using GK3Reborn.Game;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Tests for where the player is standing when a room opens.
/// </summary>
/// <remarks>
/// Reported as Gabriel's position resetting on the way into the phone room and the kitchen:
/// he arrived somewhere wrong, filling the screen, and a moment later the room's own script
/// moved him to the door. The wrong somewhere was the first entry of the scene's
/// <c>[POSITIONS]</c>, which the loader used when nothing else said — in the phone room that
/// is <c>EMILIO_HERE_1</c>, a spot authored for a different character.
/// </remarks>
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
    /// <remarks>
    /// The artists' own convention, and the same choice the room's enter script makes by
    /// hand a frame later: <c>FR_LBY</c> is where you stand having come from the lobby.
    /// Making it here as well is what stops the player ever seeing the wrong one.
    /// </remarks>
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
    /// <remarks>
    /// One scene in the game does. It is nearly a dead path and it is still the right answer
    /// where it exists, so it stays.
    /// </remarks>
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
    /// <remarks>
    /// A START says where a room begins and a door says where this arrival begins, and the
    /// second is the more specific answer whenever the game can supply it.
    /// </remarks>
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
