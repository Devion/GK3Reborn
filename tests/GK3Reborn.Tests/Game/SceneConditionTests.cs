using System.Numerics;
using GK3Reborn.Formats.Scenes;
using GK3Reborn.Game;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Tests for deciding a scene file's conditions against the story.
/// </summary>
/// <remarks>
/// The fixture is R25 reduced to the parts that change: the hall door that stands in
/// every timeblock but 202P, the hall backdrop visible only on the first visit that
/// afternoon, and the suitcases that appear once Gabriel has been up to the room.
/// </remarks>
public sealed class SceneConditionTests
{
    private const string Fixture =
        """
        [GENERAL]
        scene=r25_n

        [GENERAL={IsCurrentTime("202p")}]
        scene=r25_a

        [MODELS={IsCurrentTime("202p") && GetEgoCurrentLocationCount() < 1}]
        model=R25_HAL_BKG, type=scene

        [MODELS={!IsCurrentTime("202p") || GetEgoCurrentLocationCount()}]
        model=R25_HAL_BKG, type=scene, hidden

        [MODELS={!IsCurrentTime("202p")}]
        model=r25door2hal_scene, noun=HALL_DOOR, type=scene

        [MODELS={IsCurrentTime("202p")}]
        model=r25door2hal_scene, noun=HALL_DOOR, type=scene, hidden

        [MODELS={DoesGabeHaveInvItem("MOPED_KEYS")}]
        model=mopedkeys, type=prop

        [MODELS]
        model=pencil, type=prop, hidden

        [ROOM_CAMERAS]
        START, angle={-46.97, 0.90}, pos={277.58, 61.70, 50.57}, Default
        """;

    /// <summary>
    /// R25's file for 202P, reduced: who is in the room, what they brought, where they
    /// stand and the angles the conversation cuts between.
    /// </summary>
    private const string TimeblockFixture =
        """
        [ACTORS={(GetGameVariableInt("FiveMinTimer202p") == 0)}]
        model=gra,noun=GRACE,pos=GRACE_INIT

        [MODELS]
        model=pencil,type=prop

        [MODELS={GetTopicCount("GRACE_N_MOSE","T_BOOK") == 0}]
        model=r25_pop,noun=POP_BOTTLE,type=prop

        [ROOM_CAMERAS]

        [CINEMATIC_CAMERAS]
        Couch_Overview,pos={62.17, 61.08, 253.22}, angle={115.22, 3.37}

        [POSITIONS]
        GRACE_INIT, pos={11.96,0.00,34.92}, heading=0.00
        """;

    [Fact]
    public void The_hall_door_is_gone_at_the_one_timeblock_that_hides_it()
    {
        Assert.True(Read("202P").Hidden("r25door2hal_scene"));
        Assert.False(Read("110A").Hidden("r25door2hal_scene"));
    }

    [Fact]
    public void The_backdrop_behind_the_open_door_shows_only_on_the_first_visit()
    {
        Assert.False(Read("202P").Hidden("R25_HAL_BKG"));

        // Second time through, the door is shut again and the hallway is not visible.
        Assert.True(Read("202P", visits: 1).Hidden("R25_HAL_BKG"));
    }

    [Fact]
    public void The_scene_asset_is_chosen_by_the_file_rather_than_by_the_caller()
    {
        Assert.Equal("r25_a", Read("202P").Definition.SceneAsset());
        Assert.Equal("r25_n", Read("309P").Definition.SceneAsset());
    }

    [Fact]
    public void Inventory_decides_a_condition_like_any_other_state()
    {
        Scene without = Read("110A");
        Assert.DoesNotContain(without.Definition.Models(), m => m.Name == "mopedkeys");

        var state = new GameState { Timeblock = Parse("110A"), Location = "R25" };
        state.Inventory.Add("GABRIEL", "MOPED_KEYS");

        Assert.Contains(Read(state).Definition.Models(), m => m.Name == "mopedkeys");
    }

    [Fact]
    public void The_timeblock_file_adds_to_the_general_one_and_overrides_it()
    {
        Scene scene = Read("202P", TimeblockFixture);

        // The timeblock file brings the people and their props with it.
        Assert.Contains(scene.Definition.Actors(), a => a.Name == "gra");
        Assert.Contains(scene.Definition.Models(), m => m.Name == "r25_pop");

        // And the room the general file describes is still there underneath.
        Assert.Contains(scene.Definition.Models(), m => m.Name == "r25door2hal_scene");

        // Where they disagree the timeblock file wins: the general file leaves the pencil
        // out and the timeblock file puts one on the desk.
        Assert.False(Assert.Single(
            scene.Definition.Models(), m => m.Name == "pencil").Hidden);
    }

    [Fact]
    public void A_cinematic_camera_from_the_timeblock_file_can_be_named()
    {
        Scene scene = Read("202P", TimeblockFixture);

        Assert.Equal("Couch_Overview", scene.Definition.CameraNamed("Couch_Overview")?.Name);

        // The room camera the general file marks Default still opens the scene, even
        // though the timeblock file declares an empty ROOM_CAMERAS of its own.
        Assert.Equal("START", scene.Definition.DefaultCamera()?.Name);
    }

    [Fact]
    public void A_spot_the_timeblock_file_defines_is_where_its_actor_stands()
    {
        Scene scene = Read("202P", TimeblockFixture);

        Assert.Equal(
            new Vector3(11.96f, 0f, 34.92f),
            scene.Definition.PositionNamed("GRACE_INIT")?.Position);
    }

    [Fact]
    public void A_malformed_condition_is_reported_and_the_section_left_out()
    {
        var conditions = new SceneConditions(new Gk3SheepApi(new GameState()));

        Assert.False(conditions.Holds("IsCurrentTime(\"110a\") &&"));
        Assert.Single(conditions.Diagnostics.Items);

        // Reported once, however many times the reader asks: the accessors each walk the
        // file, so an uncached evaluator would raise the same diagnostic four times over.
        Assert.False(conditions.Holds("IsCurrentTime(\"110a\") &&"));
        Assert.Single(conditions.Diagnostics.Items);
    }

    [Fact]
    public void An_unconditional_section_always_holds()
    {
        Assert.True(new SceneConditions(new Gk3SheepApi(new GameState())).Holds(null));
    }

    private static Timeblock Parse(string code)
    {
        Assert.True(Timeblock.TryParse(code, out Timeblock timeblock), code);
        return timeblock;
    }

    private static Scene Read(string timeblock, string? specific = null, int visits = 0)
    {
        var state = new GameState { Timeblock = Parse(timeblock), Location = "R25" };
        state.SetLocationCount(state.Ego, "R25", visits);

        return Read(state, specific);
    }

    private static Scene Read(GameState state, string? specific = null)
    {
        var conditions = new SceneConditions(new Gk3SheepApi(state));

        var definition = new SceneDefinition(
            SceneInitFile.Parse(Fixture, "R25.SIF", conditions.Applies),
            specific is null
                ? null
                : SceneInitFile.Parse(specific, "R25202P.SIF", conditions.Applies));

        Assert.Empty(conditions.Diagnostics.Items);
        return new Scene(definition);
    }

    /// <summary>One reading of the fixture, with the question the tests ask of it.</summary>
    private sealed record Scene(SceneDefinition Definition)
    {
        public bool Hidden(string model)
        {
            SceneModel? found = Definition.Models().FirstOrDefault(
                m => string.Equals(m.Name, model, StringComparison.OrdinalIgnoreCase));

            // A model no applying block declares is not in the scene, which for these
            // tests is the same answer as being hidden.
            return found is null || found.Hidden;
        }
    }
}
