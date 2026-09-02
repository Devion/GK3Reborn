using GK3Reborn.Formats.Scenes;
using GK3Reborn.Game;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Tests for who the player is in a room.
/// </summary>
/// <remarks>
/// <para>
/// Reported from the chateau on the second afternoon: scanning something in Grace's
/// timeblock answered in Gabriel's voice. <c>INV_23ALL.NVC</c> writes the same rule twice,
/// <c>ANY_OBJECT, SCANNER, GABE_ALL_INV</c> above <c>ANY_OBJECT, SCANNER,
/// GRACE_ALL_INV</c>, and both cases end in <c>IsCurrentEgo</c> — so a game that thought
/// it was Gabriel took the first of every such pair, in every room, for the whole of both
/// of Grace's days.
/// </para>
/// <para>
/// Nothing in the data says which timeblock belongs to whom. A SIF's cast list marks one
/// actor <c>ego</c> and that is the only statement of it anywhere, which is why this is a
/// question about scene files rather than about the clock.
/// </para>
/// </remarks>
public sealed class EgoIdentityTests
{
    /// <summary>The chateau's general file, which is Grace's on the day it is used.</summary>
    private const string Chateau = """
        [ACTORS]
        model=gra,noun=GRACE,pos=FR_CS3, idle=GraCs2StandIdle.gas, ego
        model=mos,noun=MOSELY,pos=MOS_HERE
        """;

    /// <summary>A room that is usually Gabriel's.</summary>
    private const string Lobby = """
        [ACTORS]
        model=gab,noun=GABRIEL,pos=FR_R25,idle=gabIdle.gas,talk=gabTalk.gas,ego
        """;

    /// <summary>And the same room on a morning that is not.</summary>
    private const string LobbyThatMorning = """
        [ACTORS]
        model=gra,noun=GRACE,idle=graIdle.gas,ego
        """;

    private static SceneDefinition Scene(string general, string? specific = null) =>
        new(
            SceneInitFile.Parse(general, "CS2.SIF"),
            specific is null ? null : SceneInitFile.Parse(specific, "CS2212P.SIF"));

    [Fact]
    public void The_scene_says_who_the_player_is()
    {
        Assert.Equal("GRACE", Scene(Chateau).EgoNoun(), ignoreCase: true);
        Assert.Equal("GABRIEL", Scene(Lobby).EgoNoun(), ignoreCase: true);
    }

    [Fact]
    public void The_timeblocks_own_answer_replaces_the_locations()
    {
        // 157 pairs across the corpus name Gabriel generally and Grace specifically, which
        // is every Grace timeblock in every location she visits.
        Assert.Equal("GRACE", Scene(Lobby, LobbyThatMorning).EgoNoun(), ignoreCase: true);
    }

    [Fact]
    public void A_timeblock_that_names_nobody_leaves_the_locations_answer_standing()
    {
        Assert.Equal(
            "GABRIEL",
            Scene(Lobby, "[ACTORS]\nmodel=mos,noun=MOSELY,pos=MOS_HERE").EgoNoun(),
            ignoreCase: true);
    }

    [Fact]
    public void A_room_with_no_cast_names_nobody()
    {
        // Sidney's screens and a handful of cutscene rooms have none, and walking into one
        // is not the player becoming nobody — so the loader leaves ego alone.
        Assert.Null(Scene("[POSITIONS]\nSTART, pos={0,0,0}").EgoNoun());
    }

    [Fact]
    public void The_noun_is_the_answer_rather_than_the_model()
    {
        // Scripts ask IsCurrentEgo("Grace"); the model is called "gra".
        Assert.Equal("GRACE", Scene(Chateau).EgoNoun());
    }
}
