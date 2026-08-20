using System.Numerics;
using GK3Reborn.Game.Actors;
using GK3Reborn.Game.Navigation;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Tests for what makes a character walk rather than slide.
/// </summary>
/// <remarks>
/// <c>CHARACTERS.TXT</c> names the animation and gives the height; the height is also what
/// decides how far short of a thing somebody stops. Both are read from real data at runtime,
/// so what is pinned here is the shape of the answers rather than the answers themselves.
/// </remarks>
public sealed class CharacterLibraryTests
{
    [Fact]
    public void An_actor_stops_a_stand_off_short_of_what_they_walked_to()
    {
        // The middle of a picture is inside the wall it hangs on. Walking to it puts an
        // actor's nose against it: in R25 the four paintings end the walk 9, 9, 13 and 35
        // units off them.
        var picture = new Vector3(100, 60, 0);
        var actor = new Vector3(0, 0, 0);

        Vector3 stood = Walker.StandingOff(picture, actor, 76f);

        Assert.Equal(76f, new Vector2(stood.X - picture.X, stood.Z - picture.Z).Length(), 2);

        // On the line between the two, and on the actor's side of the thing.
        Assert.Equal(24f, stood.X, 2);
        Assert.Equal(0f, stood.Z, 2);
    }

    [Fact]
    public void Standing_off_never_walks_backwards()
    {
        // Somebody already closer than the stand-off is close enough. Backing away from
        // what they were told to look at would be worse than standing too near it.
        var thing = new Vector3(10, 0, 0);
        var actor = new Vector3(0, 0, 0);

        Assert.Equal(thing, Walker.StandingOff(thing, actor, 76f));
    }

    [Fact]
    public void The_stand_off_ignores_how_high_the_thing_is()
    {
        // A picture is on a wall and an actor is on the floor. Measuring the gap in three
        // dimensions would have them stop short by the height difference as well, and stop
        // further away the higher the picture hangs.
        var high = new Vector3(100, 200, 0);
        Vector3 stood = Walker.StandingOff(high, Vector3.Zero, 76f);

        Assert.Equal(24f, stood.X, 2);
    }

    [Fact]
    public void A_walk_with_a_stride_goes_at_the_strides_pace()
    {
        // Gabriel's stride carries him 49.9 units in 1.40 seconds, which is 35.6 — against
        // the 65 the walker guesses at. Walking faster than the legs is what a sliding
        // character looks like.
        var route = new WalkRoute(true, [new Vector3(0, 0, 100)]);

        var guessed = new Walker("GABRIEL", route, Vector3.Zero, 0f);
        var strode = new Walker("GABRIEL", route, Vector3.Zero, 0f, pace: 35.6f);

        Assert.Equal(Walker.Speed, guessed.Pace, 2);
        Assert.Equal(35.6f, strode.Pace, 2);
        Assert.True(
            strode.Seconds > guessed.Seconds,
            "a slower pace has to take longer over the same ground");
    }

    [Fact]
    public void A_pace_of_nothing_falls_back_rather_than_standing_still()
    {
        // A character with no entry in CHARACTERS.TXT still has to cross the room.
        var route = new WalkRoute(true, [new Vector3(0, 0, 100)]);

        Assert.Equal(Walker.Speed, new Walker("X", route, Vector3.Zero, 0f, pace: 0f).Pace, 2);
        Assert.Equal(Walker.Speed, new Walker("X", route, Vector3.Zero, 0f, pace: -5f).Pace, 2);
    }

    /// <summary>The shape of a real section, cut down to what is read.</summary>
    private const string File = """
        [GAB] // Gabriel

          // Walker info
        WalkerHeight=76.0
        ShoeThickness=0.75

          // Walker animation names
        StartAnim=gabstart
        ContAnim=Gabwalk
        StopAnim=Gabstop
        StartTurnRightAnim=gabTurnRight2Walk

        [MOS]
        WalkerHeight=72.0
        ContAnim=MosWalk
        """;

    [Fact]
    public void A_section_gives_a_character_their_height_and_their_stride()
    {
        CharacterConfig gabriel = CharacterLibrary.Parse(File).Of("gab")!;

        Assert.Equal("GAB", gabriel.Identifier);
        Assert.Equal(76f, gabriel.WalkerHeight, 2);
        Assert.Equal("Gabwalk", gabriel.WalkAnimation);
        Assert.Equal("gabstart", gabriel.StartAnimation);
        Assert.Equal("Gabstop", gabriel.StopAnimation);
    }

    [Fact]
    public void A_model_name_that_carries_more_than_the_code_still_finds_its_character()
    {
        // A scene may place gabclothes110a, and the file lists GAB. The first three
        // characters are the code, which is how the clothing variants share a walk.
        CharacterLibrary library = CharacterLibrary.Parse(File);

        Assert.Equal("GAB", library.Of("gabclothes110a")!.Identifier);
        Assert.Equal("GAB", library.Of("GAB")!.Identifier);
    }

    [Fact]
    public void A_character_the_file_does_not_list_has_no_configuration()
    {
        // A partial answer is the normal case: not every model that walks is in the file,
        // and one that is not still crosses the room, in whatever pose it was standing in.
        CharacterLibrary library = CharacterLibrary.Parse(File);

        Assert.Null(library.Of(null));
        Assert.Null(library.Of(string.Empty));
        Assert.Null(library.Of("zz"));
        Assert.Null(library.Of("nobody"));
    }

    [Fact]
    public void A_character_with_no_start_or_stop_still_has_a_stride()
    {
        // Most of the cast are listed with a ContAnim and nothing else, and a walk loop is
        // the whole of what is played anyway.
        CharacterConfig mosely = CharacterLibrary.Parse(File).Of("MOS")!;

        Assert.Equal("MosWalk", mosely.WalkAnimation);
        Assert.Null(mosely.StartAnimation);
        Assert.Equal(72f, mosely.WalkerHeight, 2);
    }
}
