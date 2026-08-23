using GK3Reborn.Game;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Tests for the game's own names for places and times.
/// </summary>
/// <remarks>
/// The file is the only thing in the content that says what a location is called, and the
/// interface had been drawing three-letter codes instead. What matters here is that a
/// missing name never turns into a blank corner or a crash, and that the exits get the name
/// of what is on the other side of them.
/// </remarks>
public sealed class GameStringsTests
{
    private const string File = """
        // location strings
        loc_lby   = Hotel Lobby
        loc_rc1   = Rennes-le-Chateau: Outside Hotel
        loc_rc3   = Rennes-le-Chateau: Outside Church
        loc_mop   = Moped Rental Shop
        loc_blank =

        Day110a           = Day 1, 10am - 12pm

        [ToolTips]
        inventoryexit=Return to game
        """;

    private static GameStrings Strings() => GameStrings.Parse(File);

    [Fact]
    public void A_location_is_named_whatever_the_case_of_its_code()
    {
        // The codes arrive from three places that disagree about case: a scene's own name
        // is upper, a SetLocation argument is usually lower, and a SIF writes both.
        Assert.Equal("Hotel Lobby", Strings().Place("LBY"));
        Assert.Equal("Hotel Lobby", Strings().Place("lby"));
    }

    [Fact]
    public void A_name_the_file_does_not_give_is_absent_rather_than_empty()
    {
        // Several keys in the shipped file are declared with nothing after the equals, and
        // a blank name is worse than no name: it draws an empty corner and says nothing.
        Assert.Null(Strings().Place("BLANK"));
        Assert.Null(Strings().Place("NOSUCHPLACE"));
        Assert.Null(Strings().Place(null));
    }

    [Fact]
    public void The_corner_falls_back_to_the_codes_it_has_no_name_for()
    {
        GameStrings strings = Strings();

        Assert.Equal("Hotel Lobby - Day 1, 10am - 12pm", strings.Where("LBY", "110A"));

        // A room nobody named still says when it is, and an unknown time still says where.
        Assert.Equal("XYZ - Day 1, 10am - 12pm", strings.Where("XYZ", "110A"));
        Assert.Equal("Hotel Lobby - 999Z", strings.Where("LBY", "999Z"));
    }

    [Fact]
    public void An_installation_with_no_string_table_still_says_where_you_are()
    {
        Assert.Equal("LBY - 110A", GameStrings.None.Where("LBY", "110A"));
    }

    [Fact]
    public void The_numbered_exits_are_the_ones_worth_renaming()
    {
        // 33 of the corpus's ways out are numbered and the number means nothing. The ones
        // somebody troubled to name are left alone.
        Assert.True(GameStrings.IsNumberedExit("EXIT"));
        Assert.True(GameStrings.IsNumberedExit("EXIT3"));
        Assert.True(GameStrings.IsNumberedExit("exit5"));

        Assert.False(GameStrings.IsNumberedExit("EXIT_TO_ROAD"));
        Assert.False(GameStrings.IsNumberedExit("EXIT_PATH"));
        Assert.False(GameStrings.IsNumberedExit("FRONT_DOOR"));
        Assert.False(GameStrings.IsNumberedExit(null));
    }

    [Fact]
    public void An_exit_is_called_after_the_place_its_own_script_sends_you_to()
    {
        // Reported: RC1's ways out drew as "Exit3" and "Exit5", which tells the player
        // nothing. The rule behind the door already says where it goes.
        Assert.Equal(
            "Rennes-le-Chateau: Outside Church",
            Strings().ExitName("SetLocation(\"rc3\");"));

        // Whitespace and case are the shipped data's, not a tidy example's.
        Assert.Equal(
            "Moped Rental Shop",
            Strings().ExitName("SetFlag(\"InConversation\"); SetLocation ( \"MOP\" );"));
    }

    [Fact]
    public void An_exit_that_leads_nowhere_named_is_just_an_exit()
    {
        GameStrings strings = Strings();

        // RC1's EXIT5 raises the driving map rather than opening a room, so there is no
        // destination to read — and the number is still worth losing.
        Assert.Equal("Exit", strings.ExitName("wait StartVoiceOver(\"07LXA22U51\",1);"));
        Assert.Equal("Exit", strings.ExitName("SetLocation(\"zzz\");"));
        Assert.Equal("Exit", strings.ExitName(null));
    }
}
