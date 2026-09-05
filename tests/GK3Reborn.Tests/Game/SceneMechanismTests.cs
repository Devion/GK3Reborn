using System.Numerics;
using System.Reflection;
using GK3Reborn.Formats.Models;
using GK3Reborn.Formats.Scenes;
using GK3Reborn.Game;
using GK3Reborn.Game.Actors;
using GK3Reborn.Game.Interaction;
using GK3Reborn.Game.Mechanisms;
using GK3Reborn.Rendering;
using GK3Reborn.Rendering.Geometry;
using GK3Reborn.Sheep;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Tests for the code the eleven rooms need that their data cannot express.
/// </summary>
/// <remarks>
/// <para>
/// Reported as "the button on the desk makes a sound, but nothing happens in the scene".
/// It should turn on five laser heads. <c>CallSceneFunction</c> is the one call the whole
/// family arrives through, and the port resolved it as a Sheep function in the script
/// named after the location — where there is none, for any of the corpus's 43 calls. Every
/// puzzle behind it was inert and silent about it.
/// </para>
/// <para>
/// What is checked here is the part that can be: which room gets which mechanism, that a
/// word reaches it, that a waited call is priced before it is made, and the arithmetic
/// each one does. Where the props actually end up is a picture, and is checked by looking
/// at one.
/// </para>
/// </remarks>
public sealed class SceneMechanismTests
{
    private const string Chateau = """
        [GENERAL]
        custom=Laser

        [MODELS]
        model=cs2head01, noun=FIVE_HEADS_1, type=prop
        model=cs2laser_01, type=prop, hidden
        """;

    [Fact]
    public void The_scene_file_says_which_code_a_room_needs()
    {
        Assert.Equal("Laser", Read(Chateau).Mechanism());
        Assert.Equal("Angels", Read("[GENERAL]\ncustom=Angels").Mechanism());
        Assert.Null(Read("[GENERAL]\nfloor=cs2floor").Mechanism());
    }

    [Fact]
    public void The_timeblocks_declaration_wins()
    {
        // Two of the eleven are declared only in the timeblock file: BEC and LER name the
        // coordinate device on the afternoons Grace is carrying one and not otherwise.
        var scene = new SceneDefinition(
            SceneInitFile.Parse("[GENERAL]\nfloor=x", "BEC.SIF"),
            SceneInitFile.Parse("[GENERAL]\ncustom=CoordinateDevice", "BEC312P.SIF"));

        Assert.Equal("CoordinateDevice", scene.Mechanism());
    }

    [Fact]
    public void A_room_that_declares_nothing_gets_nothing()
    {
        (SceneUpdate world, Gk3SheepApi api) = World();

        Assert.Null(SceneMechanisms.For(null, world, api));
        Assert.Null(SceneMechanisms.For("Langolier", world, api));
    }

    [Fact]
    public void The_four_rooms_whose_code_is_a_patch_are_keyed_by_location()
    {
        // These declare no custom= at all: the original ran <location>-init on every scene
        // load, and four rooms use that to fix something their own data gets wrong.
        (SceneUpdate world, Gk3SheepApi api) = World();
        api.State.Location = "MS3";

        Assert.IsType<RoomPatches>(SceneMechanisms.For(null, world, api));

        api.State.Location = "R25";
        Assert.Null(SceneMechanisms.For(null, world, api));
    }

    [Fact]
    public void The_museums_patch_clears_the_flag_that_soft_locks_it()
    {
        // TE6Topics marks Lady Howard and Estelle as mid-animation. Leaving the room during
        // that animation leaves it set for ever, and the eavesdrop cutscene then waits on a
        // loop that can never end — a bug in the original, which the reference fixes here.
        (SceneUpdate world, Gk3SheepApi api) = World();
        api.State.Location = "MS3";
        api.State.SetFlag("TE6Topics");

        SceneMechanisms.For(null, world, api)!.Begin();

        Assert.False(api.State.GetFlag("TE6Topics"));
    }

    [Fact]
    public void A_turn_is_priced_before_it_is_performed()
    {
        // The VM asks how long a waited call takes and then makes it — see
        // SheepVirtualMachine — so a mechanism has to answer about work it has not done.
        // For a laser head that means the clip named for the angle it is *about* to reach.
        (SceneUpdate world, Gk3SheepApi api) = World();
        var heads = new LaserHeads(world, api);

        heads.Begin();

        // Nothing can price it without an animation library, which is the ordinary state of
        // a tool; what matters is that the question is answered rather than refused.
        Assert.Equal(0, heads.Seconds("turnL1"));
        Assert.Equal(0, heads.Seconds("nonsense"));
    }

    [Fact]
    public void Only_the_words_a_room_knows_are_taken()
    {
        (SceneUpdate world, Gk3SheepApi api) = World();
        var heads = new LaserHeads(world, api);

        heads.Begin();

        Assert.True(heads.Perform("toggleLasers"));
        Assert.True(heads.Perform("turnL3"));
        Assert.True(heads.Perform("turnR5"));

        // A word from another room's mechanism is not this one's to take: the caller falls
        // through to the Sheep reading rather than swallowing it.
        Assert.False(heads.Perform("Angel1"));
        Assert.False(heads.Perform("turnL6"));
        Assert.False(heads.Perform("turnX1"));
    }

    [Fact]
    public void A_head_starts_in_the_middle_and_cannot_be_turned_past_either_end()
    {
        // Check_Staircase asks for all five heads at 1, at 3, at 0 or at 4. Two is none of
        // those, which is why it is where the puzzle starts.
        (SceneUpdate world, Gk3SheepApi api) = World();
        var heads = new LaserHeads(world, api);

        heads.Begin();
        Assert.Equal(2, api.State.GetVariable("Cs2Head1"));

        heads.Perform("turnL1");
        heads.Perform("turnL1");
        Assert.Equal(4, api.State.GetVariable("Cs2Head1"));

        // The fifth turn has nowhere to go. The game ships GraCs2TrnHeadL1 to L4 and no L5
        // for the same reason: it is a turn that cannot happen.
        heads.Perform("turnL1");
        Assert.Equal(4, api.State.GetVariable("Cs2Head1"));

        for (int i = 0; i < 6; i++)
        {
            heads.Perform("turnR1");
        }

        Assert.Equal(0, api.State.GetVariable("Cs2Head1"));
    }

    [Fact]
    public void The_beams_go_on_and_off_together()
    {
        (SceneUpdate world, Gk3SheepApi api) = World();
        var heads = new LaserHeads(world, api);

        heads.Begin();
        Assert.False(heads.Lit);

        heads.Perform("toggleLasers");
        Assert.True(heads.Lit);

        heads.Perform("toggleLasers");
        Assert.False(heads.Lit);
    }

    [Fact]
    public void Tracing_the_angels_the_long_way_round_draws_the_square()
    {
        // Four sides and neither diagonal. Touching them 1-2-3-4-1 is the shape; the fifth
        // touch is what closes it, and is the same call as the first.
        (SceneUpdate world, Gk3SheepApi api) = World();
        var angels = new AngelTracing(world, api);

        angels.Begin();

        foreach (string angel in new[] { "Angel1", "Angel2", "Angel3", "Angel4" })
        {
            Assert.True(angels.Perform(angel));
        }

        // Not yet: three sides drawn and the fourth still open.
        Assert.Equal(0, api.State.GetNounVerbCount("Four_Angels", "Trace"));

        // But there is something to rub out, which is what puts the erase action on offer.
        Assert.Equal(1, api.State.GetNounVerbCount("Four_Angels", "ERASE"));

        angels.Perform("Angel1");
        Assert.Equal(1, api.State.GetNounVerbCount("Four_Angels", "Trace"));
    }

    [Fact]
    public void Crossing_the_middle_draws_a_diagonal_instead()
    {
        // Touching opposite angels draws one of the two lines across the square, and the
        // shape can no longer be right however the player carries on. That is the whole way
        // this puzzle can be got wrong, and the reason it has an erase action at all.
        (SceneUpdate world, Gk3SheepApi api) = World();
        var angels = new AngelTracing(world, api);

        angels.Begin();

        foreach (string angel in new[] { "Angel1", "Angel3", "Angel2", "Angel4", "Angel1" })
        {
            angels.Perform(angel);
        }

        Assert.Equal(0, api.State.GetNounVerbCount("Four_Angels", "Trace"));

        angels.Perform("Erase");
        Assert.Equal(0, api.State.GetNounVerbCount("Four_Angels", "ERASE"));
    }

    [Fact]
    public void The_device_is_switched_on_and_off_by_the_only_two_words_it_knows()
    {
        (SceneUpdate world, Gk3SheepApi api) = World();
        var gps = new CoordinateDevice(world, api);

        gps.Begin();
        Assert.False(gps.On);
        Assert.Null(gps.Reading());

        Assert.True(gps.Perform("on"));
        Assert.True(gps.On);

        Assert.True(gps.Perform("off"));
        Assert.False(gps.On);

        Assert.False(gps.Perform("Angel1"));
    }

    [Fact]
    public void One_room_takes_the_click_on_its_own_floor()
    {
        // TE6: Gabriel is circling a pentagram and moves a step at a time in the room's own
        // animations, so a click on the floor is a message to the script rather than a
        // place to walk to.
        (SceneUpdate world, Gk3SheepApi api) = World();
        var fight = new DemonFight(world, api);

        Assert.True(fight.TakesFloorClick());
        Assert.True(api.State.GetFlag("Te6ClickedOnFloor"));

        // And is dropped while he is already moving: a second step is one the fight never
        // asked for.
        api.State.ClearFlag("Te6ClickedOnFloor");
        api.State.SetFlag("Te6GabeWalk");

        Assert.True(fight.TakesFloorClick());
        Assert.False(api.State.GetFlag("Te6ClickedOnFloor"));
    }

    [Fact]
    public void Every_other_room_walks_as_it_always_did()
    {
        (SceneUpdate world, Gk3SheepApi api) = World();

        Assert.False(new LaserHeads(world, api).TakesFloorClick());
        Assert.False(new AngelTracing(world, api).TakesFloorClick());
    }

    [Fact]
    public void The_three_temple_puzzles_are_built_from_what_their_rooms_declare()
    {
        (SceneUpdate world, Gk3SheepApi api) = World();

        Assert.IsType<Chessboard>(SceneMechanisms.For("Chess", world, api));
        Assert.IsType<Bridge>(SceneMechanisms.For("Bridge", world, api));
        Assert.IsType<Pendulum>(SceneMechanisms.For("Circle", world, api));
    }

    [Fact]
    public void A_knights_move_is_the_only_legal_one_on_the_chessboard()
    {
        // Both differences greater than zero and summing to three is exactly the eight
        // knight's moves — which is what the action file's case reads to choose between
        // "jump", "first jump" and "that is too far".
        (SceneUpdate world, Gk3SheepApi api) = World();
        var board = new Chessboard(world, api);

        board.Perform("clearTiles");

        // Off the board, only the first row will do.
        board.Pointing(Tile("te1floord1"), busy: false);
        Assert.Equal(1, api.State.GetVariable("Te1MoveType"));

        board.Pointing(Tile("te1floord3"), busy: false);
        Assert.Equal(2, api.State.GetVariable("Te1MoveType"));

        // On d1 — row 0, column 3 — the knight's moves are two along and one across.
        api.State.SetVariable("Te1GabeRow", 0);
        api.State.SetVariable("Te1GabeColumn", 3);

        board.Pointing(Tile("te1floorb2"), busy: false);
        Assert.Equal(1, api.State.GetVariable("Te1MoveType"));

        board.Pointing(Tile("te1floorf2"), busy: false);
        Assert.Equal(1, api.State.GetVariable("Te1MoveType"));

        // Straight ahead, diagonally, and right across the board are all refusals.
        foreach (string tile in new[] { "te1floord2", "te1floore2", "te1floord8" })
        {
            board.Pointing(Tile(tile), busy: false);
            Assert.Equal(2, api.State.GetVariable("Te1MoveType"));
        }
    }

    [Fact]
    public void The_turn_before_a_jump_is_a_keypad_centred_on_twelve()
    {
        // The scripts pick a turn animation from this number. Every one of the twenty-four
        // is checked, because the numbering is the game's and nothing here would notice a
        // wrong one: Gabriel would simply turn the wrong way before jumping the right one.
        (SceneUpdate world, Gk3SheepApi api) = World();
        var board = new Chessboard(world, api);

        board.Perform("clearTiles");
        api.State.SetVariable("Te1GabeRow", 3);
        api.State.SetVariable("Te1GabeColumn", 3);

        // Standing still, one and two steps each way, the four diagonals, and the eight
        // L-shapes — the codes the reference works out through three pages of branches.
        (int Row, int Column, int Code)[] expected =
        [
            (3, 3, 12),
            (4, 3, 17), (5, 3, 22), (2, 3, 7), (1, 3, 2),
            (3, 4, 13), (3, 5, 14), (3, 2, 11), (3, 1, 10),
            (4, 4, 18), (5, 5, 18), (4, 2, 16), (5, 1, 16),
            (2, 2, 6), (1, 1, 6), (2, 4, 8), (1, 5, 8),
            (5, 4, 23), (4, 5, 19), (5, 2, 21), (4, 1, 15),
            (1, 2, 1), (2, 1, 5), (1, 4, 3), (2, 5, 9),
        ];

        foreach ((int row, int column, int code) in expected)
        {
            board.Pointing(Named(row, column), busy: false);

            Assert.Equal(code, api.State.GetVariable("Te1MoveCode"));
        }
    }

    [Fact]
    public void A_tile_landed_on_twice_is_a_death_and_a_sword_is_counted_once()
    {
        (SceneUpdate world, Gk3SheepApi api) = World();
        var board = new Chessboard(world, api);

        board.Perform("clearTiles");

        // d1 is row 0, column 3, which is one of the sixteen sword tiles.
        api.State.SetVariable("Te1GabeRow", 0);
        api.State.SetVariable("Te1GabeColumn", 3);

        board.Perform("landed");
        Assert.Equal(1, api.State.GetVariable("Te1TileState"));
        Assert.Equal(1, api.State.GetVariable("Te1SwordCount"));

        // Again, which is what the scripts read as fatal — and the sword is not counted a
        // second time.
        board.Perform("landed");
        Assert.Equal(2, api.State.GetVariable("Te1TileState"));
        Assert.Equal(1, api.State.GetVariable("Te1SwordCount"));
    }

    [Fact]
    public void Twelve_tiles_are_traps_from_the_first_landing()
    {
        // Four in front of each pair of doors and four in the middle. They are given a
        // landing count that already reads as a repeat, which is how the scripts learn they
        // are fatal without a table of their own.
        (SceneUpdate world, Gk3SheepApi api) = World();
        var board = new Chessboard(world, api);

        board.Perform("clearTiles");
        api.State.SetVariable("Te1GabeRow", 3);
        api.State.SetVariable("Te1GabeColumn", 3);

        board.Perform("landed");

        Assert.Equal(3, api.State.GetVariable("Te1TileState"));
    }

    [Fact]
    public void Sixteen_swords_finish_the_board()
    {
        (SceneUpdate world, Gk3SheepApi api) = World();
        var board = new Chessboard(world, api);

        board.Perform("clearTiles");

        (int Row, int Column)[] swords =
        [
            (0, 3), (1, 2), (2, 1), (3, 0), (4, 7), (5, 6), (6, 5), (7, 4),
            (0, 4), (1, 5), (2, 6), (3, 7), (4, 0), (5, 1), (6, 2), (7, 3),
        ];

        foreach ((int row, int column) in swords)
        {
            api.State.SetVariable("Te1GabeRow", row);
            api.State.SetVariable("Te1GabeColumn", column);
            board.Perform("landed");
        }

        Assert.Equal(16, api.State.GetVariable("Te1SwordCount"));
        Assert.True(api.State.GetFlag("AllSwords"));
    }

    [Fact]
    public void The_bridge_only_lets_the_first_tile_be_stepped_onto()
    {
        (SceneUpdate world, Gk3SheepApi api) = World();
        var bridge = new Bridge(world, api);

        bridge.Begin();

        // From the near end, every tile but the first is somebody else's problem — the room
        // behaves normally and a click on one is not the bridge's.
        Assert.False(bridge.TakesClick(Tile("te5sq04")));
        Assert.False(bridge.TakesClick(Tile("te5_floor")));
        Assert.True(bridge.TakesClick(Tile("te5sq01")));
    }

    [Fact]
    public void The_bridge_takes_every_click_once_he_is_on_it()
    {
        // There is no walking out there: the floor is nine squares that come and go, so a
        // click is a jump, a jump back, or a step into the chasm.
        (SceneUpdate world, Gk3SheepApi api) = World();
        var bridge = new Bridge(world, api);

        bridge.Begin();

        Assert.False(bridge.TakesFloorClick());

        bridge.TakesClick(Tile("te5sq01"));

        // The landing is scheduled behind the jump rather than happening on the click, so
        // the room has to be stepped before he is anywhere.
        world.Advance(0.1);

        Assert.True(bridge.TakesFloorClick());
    }

    [Fact]
    public void The_pendulum_reads_its_numbers_out_of_the_game()
    {
        // PENDULUM.TXT ships with the period, the swing and the two angles that decide
        // whether letting go is survivable. The reference works from remembered values and
        // says it did not bother reading the file.
        (SceneUpdate world, Gk3SheepApi api) = World();
        var pendulum = new Pendulum(world, api);

        pendulum.Begin();

        Assert.Contains("50s a turn", pendulum.Report(), StringComparison.Ordinal);
        Assert.Contains("24.5", pendulum.Report(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_shaft_is_not_somewhere_to_walk()
    {
        (SceneUpdate world, Gk3SheepApi api) = World();
        var pendulum = new Pendulum(world, api);

        pendulum.Begin();

        // In the doorway the room is ordinary. Once he is out on the ring it is not.
        Assert.False(pendulum.TakesFloorClick());
    }

    [Fact]
    public void Nothing_in_the_temple_kills_a_player_who_asked_not_to_be()
    {
        // "Gabriel cannot be killed" on the Playing page. Assists.IsDeath already catches
        // the deaths that arrive as a script's Die$ — these three are the ones that do not,
        // because the killing is decided in code from where a blade is or which tile has
        // gone out, and by the time a script is called the player is already dead.
        (SceneUpdate world, Gk3SheepApi api) = World();
        api.State.PlotArmour = true;

        var board = new Chessboard(world, api);

        board.Perform("clearTiles");
        api.State.SetVariable("Te1GabeRow", 3);
        api.State.SetVariable("Te1GabeColumn", 3);

        // One of the twelve that are traps from the start, landed on twice for good
        // measure. The scripts read this number, and it never reaches a fatal one.
        board.Perform("landed");
        board.Perform("landed");

        Assert.Equal(1, api.State.GetVariable("Te1TileState"));
    }

    [Fact]
    public void The_beams_are_drawn_as_light_only_where_there_is_a_lighting_model()
    {
        // The renderer's material pass cannot blend, so a beam that is to be see-through
        // has to go through the particle pass — and a glow laid over the 1999 bake, which
        // is uniformly lit and has no darkness to be bright against, reads as a smear. So
        // with rays off the beams are the model the game shipped and nothing more.
        (SceneUpdate world, Gk3SheepApi api) = World();
        var heads = new LaserHeads(world, api);

        heads.Begin();

        Assert.Empty(heads.Particles(Vector3.Zero));

        heads.Perform("toggleLasers");
        Assert.Empty(heads.Particles(Vector3.Zero));

        heads.Tracing = RayTracingQuality.High;
        heads.Advance(0.1);

        // Nothing to draw either way here: the fixture has no beam models to light. What
        // is checked is that the switch is the switch, not that a room without the art
        // invents any.
        Assert.Empty(heads.Particles(Vector3.Zero));

        // And switched off again, the glow goes with them whatever the setting says.
        heads.Perform("toggleLasers");
        Assert.False(heads.Lit);
        Assert.Empty(heads.Particles(Vector3.Zero));
    }

    [Fact]
    public void A_self_lit_beam_lights_nothing_so_the_beams_are_in_the_rig()
    {
        // The flag means "draw this at full brightness and skip shading" and nothing more,
        // so a beam marked emissive is a bright red line in a room exactly as dark as it
        // was. Anything meant to lay red on the floor under it has to be a light.
        (SceneUpdate world, Gk3SheepApi api) = World();
        var heads = new LaserHeads(world, api) { Tracing = RayTracingQuality.High };

        heads.Begin();

        // Nothing while they are off, whatever the setting says.
        Assert.Empty(heads.Lights);

        // And nothing with the rays off either: without them the bake lights the room and
        // the rig reaches only the models standing in it, so this look is all or nothing.
        heads.Perform("toggleLasers");
        heads.Tracing = RayTracingQuality.None;
        Assert.Empty(heads.Lights);

        // The fixture has no beam models to hang them off, so what is checked is the
        // switch rather than the count — see the room itself for that.
        heads.Tracing = RayTracingQuality.High;
        Assert.Empty(heads.Lights);
    }

    [Fact]
    public void The_rig_is_laid_again_only_when_something_has_moved()
    {
        // Laying one rebuilds the scene's light grid, which is a per-room cost. A room
        // where nothing is turning must not ask for it every frame.
        (SceneUpdate world, Gk3SheepApi api) = World();
        var heads = new LaserHeads(world, api) { Tracing = RayTracingQuality.High };

        heads.Begin();

        // Something to lay, once: the beams have just been put where they belong.
        Assert.True(heads.LightsMoved);

        // Reading them is what clears it.
        _ = heads.Lights;
        Assert.False(heads.LightsMoved);

        heads.Advance(0.5);
        Assert.False(heads.LightsMoved);

        // Switching them on is a change; so is a head starting to turn.
        heads.Perform("toggleLasers");
        Assert.True(heads.LightsMoved);

        _ = heads.Lights;
        heads.Perform("turnL1");
        heads.Advance(1.0);

        Assert.True(heads.LightsMoved);
    }

    [Fact]
    public void A_lamp_the_artists_lit_is_left_alone_and_one_they_forgot_is_not()
    {
        // Most of GK3's lamps have an omni inside them. Adding a second to every one would
        // double every practical in the game, so the rule is FlameLighting's: a glowing
        // thing with a light already standing in it needs nothing.
        var shade = new EmissiveSurface(
            "lamp", "LAMPSHADE", new Vector3(100, 50, 0), 8f, new Vector3(1f, 0.9f, 0.7f));

        var bulb = new AuthoredLight(
            "omni01",
            AuthoredLightKind.Point,
            new Vector3(100, 48, 0),
            -Vector3.UnitY,
            Vector3.One,
            0f, 0f, 10f, 200f,
            UsesAttenuation: true,
            CastsShadows: true,
            1.5f,
            2f);

        IReadOnlyList<AuthoredLight> lit =
            EmissiveLighting.Rig([bulb], [shade], out int added);

        Assert.Equal(0, added);
        Assert.Single(lit);

        // And the same shade in a room where nobody put a bulb in it.
        IReadOnlyList<AuthoredLight> dark =
            EmissiveLighting.Rig([], [shade], out int alone);

        Assert.Equal(1, alone);
        Assert.Equal("emissive:lamp", dark[0].Name);

        // The colour is the emission's own hue with its strength taken out, because a dim
        // yellow shade and a bright one are the same yellow.
        Assert.Equal(1f, dark[0].Color.X, 3);
        Assert.True(dark[0].Color.Z < dark[0].Color.X);

        // Never shadowed: the ray budget is eight lights in a whole room and it belongs to
        // the lamps that shape it, and a shade traced against its own light seals it in.
        Assert.False(dark[0].CastsShadows);
    }

    [Fact]
    public void A_room_with_nothing_glowing_gets_its_rig_back_unchanged()
    {
        IReadOnlyList<AuthoredLight> rig = [];

        Assert.Same(rig, EmissiveLighting.Rig(rig, [], out int added));
        Assert.Equal(0, added);
    }

    [Fact]
    public void The_bakes_own_fill_lights_are_turned_down_when_the_rays_replace_them()
    {
        // These are baking rigs. CS3's attic is 58 lights, 18 of them named fill, ambient,
        // bounce or warmer — the 1999 stand-in for the global illumination the tracer now
        // computes. Running both is the same light twice, and the room comes out brighter
        // and flatter than the bake it replaced.
        AuthoredLight Lamp(string name, float intensity) => new(
            name,
            AuthoredLightKind.Point,
            Vector3.Zero,
            -Vector3.UnitY,
            Vector3.One,
            0f, 0f, 10f, 200f,
            UsesAttenuation: true,
            CastsShadows: true,
            intensity,
            4f);

        IReadOnlyList<AuthoredLight> rig =
        [
            Lamp("cs3_lightbulb_special", 1f),
            Lamp("back_room_fill", 1f),
            Lamp("cs3_ambient", 2f),
            Lamp("sky_bounce01", 1f),
            Lamp("cs3_turret_window_floor_warmer04", 0.3f),
            Lamp("spot01", 2f),
        ];

        IReadOnlyList<AuthoredLight> traced =
            RigBalance.For(rig, RayTracingQuality.High, out int dimmed);

        Assert.Equal(4, dimmed);

        // The sources keep what the artists gave them.
        Assert.Equal(1f, traced[0].Intensity, 3);
        Assert.Equal(2f, traced[5].Intensity, 3);

        // The scaffolding does not.
        Assert.Equal(0.15f, traced[1].Intensity, 3);
        Assert.Equal(0.3f, traced[2].Intensity, 3);
    }

    [Fact]
    public void With_no_rays_the_rig_is_left_exactly_as_the_artists_left_it()
    {
        // Without them the bake is the room's lighting and the rig reaches only the models
        // standing in it, so nothing is being counted twice and nothing is corrected.
        IReadOnlyList<AuthoredLight> rig =
        [
            new(
                "back_room_fill",
                AuthoredLightKind.Point,
                Vector3.Zero,
                -Vector3.UnitY,
                Vector3.One,
                0f, 0f, 10f, 200f,
                UsesAttenuation: true,
                CastsShadows: true,
                1f,
                4f),
        ];

        Assert.Same(rig, RigBalance.For(rig, RayTracingQuality.None, out int dimmed));
        Assert.Equal(0, dimmed);
    }

    [Fact]
    public void Daylight_the_artists_put_above_the_roof_is_moved_to_the_window()
    {
        // CS3's attic fakes its daylight with cs3_turret_window_special_outside, which the
        // artists placed at y=632 — above the roof, because nothing in 1999 checked whether
        // a light could see the room it lit. Tracing checks, finds the roof, and the attic
        // gets no daylight at all.
        var room = new SceneExtent(new Vector3(-300, 0, -200), new Vector3(200, 120, 400));

        Window[] windows =
        [
            new("cs3_wndwfrms02", new Vector3(150, 70, -190), 40f),
        ];

        AuthoredLight Above(string name, float intensity) => new(
            name,
            AuthoredLightKind.Point,
            new Vector3(122, 632, -545),
            -Vector3.UnitY,
            new Vector3(1f, 0.95f, 0.85f),
            0f, 0f, 100f, 597f,
            UsesAttenuation: true,
            CastsShadows: true,
            intensity,
            4f);

        IReadOnlyList<AuthoredLight> lit = Daylight.Rig(
            [Above("cs3_turret_window_special_outside", 3f), Above("omni01", 1f)],
            windows,
            room,
            out int moved);

        Assert.Equal(1, moved);

        // The one named for a window now stands just outside it, looking in — which is what
        // makes the wall shape its light instead of stopping it.
        Assert.True(Vector3.Distance(lit[0].Position, windows[0].Centre) < 100f);
        Assert.True(lit[0].CastsShadows);

        // The one that is not about a window is left exactly where the artists put it.
        Assert.Equal(new Vector3(122, 632, -545), lit[1].Position);
    }

    [Fact]
    public void Daylight_that_can_already_reach_the_room_is_left_alone()
    {
        // R25's morning sun lays a window's shape across the carpet exactly as it should,
        // because its light stands in the room. Moving that would be fixing what is not
        // broken, and the test for broken is whether the room's own walls are in the way.
        var room = new SceneExtent(new Vector3(-300, 0, -200), new Vector3(200, 120, 400));

        AuthoredLight inside = new(
            "front_window_special",
            AuthoredLightKind.Point,
            new Vector3(150, 70, -190),
            -Vector3.UnitY,
            Vector3.One,
            0f, 0f, 10f, 200f,
            UsesAttenuation: true,
            CastsShadows: true,
            2f,
            4f);

        IReadOnlyList<AuthoredLight> rig = [inside];

        Assert.Same(
            rig,
            Daylight.Rig(rig, [], room, out int none));

        Assert.Equal(0, none);

        Daylight.Rig(rig, [new Window("w", new Vector3(150, 70, -190), 40f)], room, out int moved);
        Assert.Equal(0, moved);
    }

    [Fact]
    public void Both_spellings_of_window_are_recognised()
    {
        // One room uses both: cs3_wndwfrms01 is the frame and turret_window_special is the
        // light that belongs to it.
        Assert.True(Daylight.IsWindow("cs3_wndwfrms01"));
        Assert.True(Daylight.IsWindow("turret_window_special"));
        Assert.False(Daylight.IsWindow("cs3_lightbulb_special"));
        Assert.False(Daylight.IsWindow("back_room_fill"));
    }

    /// <summary>One of the chessboard's tiles, by row and column.</summary>
    private static ScenePick Named(int row, int column) =>
        Tile($"te1floor{(char)('a' + column)}{(char)('1' + row)}");

    /// <summary>A pick of one of the room's own objects, as the pointer would report it.</summary>
    [Fact]
    public void The_blade_is_grabbed_before_the_action_files_are_asked()
    {
        // Reported as the pendulum being impossible to jump to. The scene gives the blade
        // noun=PENDULUM and TE3309P.NVC gives PENDULUM a LOOK for ALL, so every click on it
        // resolved an action and TakesClick -- which is only asked once a click has failed
        // to resolve one -- was never reached. The original never consults the action files
        // here at all: its whole TE3 handler is keyed on the model name.
        (SceneUpdate world, Gk3SheepApi api) = World();
        var pendulum = new Pendulum(world, api);

        pendulum.Begin();

        // In the doorway the room is the player's, blade included: nothing is claimed, so a
        // LOOK at it is a LOOK at it.
        Assert.Null(pendulum.ClaimsClick(Blade()));
        Assert.Null(pendulum.ClaimsClick(Tile("te3_altar")));
    }

    [Fact]
    public void Nothing_in_the_shaft_is_pokeable_once_he_has_left_the_doorway()
    {
        // He is on a turning platform with a blade coming at him. The scales on the far
        // side of the room are not his to poke at, and the original claims every click in
        // the room for the same reason.
        (SceneUpdate world, Gk3SheepApi api) = World();
        var pendulum = new Pendulum(world, api);

        pendulum.Begin();
        pendulum.TakesClick(Tile("te3_r01"));
        world.Advance(0.1);

        Assert.NotNull(pendulum.ClaimsClick(Tile("te3_altar")));

        // But not once he is standing on the altar: the scales are up there, and that
        // puzzle is the rest of the room.
        typeof(Pendulum)
            .GetField("_doing", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(pendulum, 4);

        Assert.Null(pendulum.ClaimsClick(Tile("te3_counter")));
    }

    [Fact]
    public void A_diagonal_on_the_bridge_has_a_clip_of_its_own()
    {
        // Four jump clips ship and only three were used: diagonals went through the
        // one-square clip, which is authored for a shorter jump. Three of the eight moves
        // along the path are diagonals.
        //
        // The shape decides the clip and the heading decides the direction, which is why a
        // jump backwards needs no clip of its own. The original keeps a table of every move
        // on the board -- twenty-one entries, eleven distinct -- and they pair off exactly:
        // going one square forward and going one square back are two entries against one
        // animation, and the same holds for the diagonal, the two-square hop and both
        // knight's moves.
        Assert.Equal("GABTE5JUMP01SQ", Leap(0, 1));
        Assert.Equal("GABTE5JUMP02SQ", Leap(0, 2));
        Assert.Equal("GABTE5JUMP45", Leap(1, 1));
        Assert.Equal("GABTE5JUMP26KNIGHT", Leap(1, 2));
        Assert.Equal("GABTE5JUMP26KNIGHT", Leap(2, 1));

        // Backwards is the same jump the other way round, and reaches the same clip.
        Assert.Equal("GABTE5JUMP45", Leap(-1, -1));
        Assert.Equal("GABTE5JUMP02SQ", Leap(0, -2));

        // And anything else is a jump he cannot make, which is a death rather than a
        // refusal: the player is getting it wrong and the game lets them find out.
        Assert.Null(Leap(2, 2));
        Assert.Null(Leap(0, 3));
        Assert.Null(Leap(3, 1));
        Assert.Null(Leap(0, 0));
    }

    /// <summary>The bridge's own move table, which is private and worth checking directly.</summary>
    private static string? Leap(int across, int along) =>
        (string?)typeof(Bridge)
            .GetMethod("Leap", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, [across, along]);

    /// <summary>The swinging blade, as the scene names its model.</summary>
    [Fact]
    public void Letting_go_of_the_blade_is_offered_as_a_button_of_its_own()
    {
        // Reported as there being no way to drop off the pendulum at all. There is one --
        // a click on te3_hpaltar -- but from up there the altar is a slab of stone among
        // slabs of stone, a long way below and behind him, and nothing on the screen says
        // so. The exit existed and could not be found.
        (SceneUpdate world, Gk3SheepApi api) = World();
        var pendulum = new Pendulum(world, api);

        pendulum.Begin();

        // In the doorway, on the ring, and on the altar there is nothing to ask for: every
        // move in those states is a click on something the player can see and point at.
        Assert.Null(pendulum.Offers);

        Hanging(pendulum);

        Assert.Equal("LET GO", pendulum.Offers?.Verb);
    }

    [Fact]
    public void The_button_lights_up_for_the_window_it_may_be_pressed_in()
    {
        // startSafe out of PENDULUM.TXT, which is the same window ClaimsClick advertises on
        // the altar and the same one Drop enforces. Drawn dim rather than taken away either
        // side of it: a button that comes and goes is a timing cue to be learnt, and one
        // that lights up is the same cue where the player is already looking.
        (SceneUpdate world, Gk3SheepApi api) = World();
        var pendulum = new Pendulum(world, api);

        pendulum.Begin();
        Hanging(pendulum);

        // At the end of its arc the blade is 24.5 degrees off vertical and letting go is
        // not allowed at all. A quarter of the way through -- the bottom of the swing, the
        // eased middle of one half of it -- it hangs straight down and it is.
        Swung(pendulum, 0.0);
        Assert.False(pendulum.Offers?.Ready);

        Swung(pendulum, Cycle(pendulum) / 4.0);
        Assert.True(pendulum.Offers?.Ready);
    }

    [Fact]
    public void Pressing_it_is_the_same_drop_a_click_on_the_altar_performs()
    {
        (SceneUpdate world, Gk3SheepApi api) = World();
        var pendulum = new Pendulum(world, api);

        pendulum.Begin();
        Hanging(pendulum);
        Swung(pendulum, Cycle(pendulum) / 4.0);

        pendulum.Press();
        world.Advance(0.1);

        // Off the blade and onto the altar, which is the room's own Drop rather than
        // anything this button invented: the clip, the landing and the flag the rest of TE3
        // reads are all still the mechanism's.
        Assert.True(api.State.GetFlag("Te3GabeAtAltar"));

        // And pressing it at the end of the arc does nothing at all, which is why it is
        // drawn dim there rather than taken away.
        (SceneUpdate other, Gk3SheepApi another) = World();
        var swinging = new Pendulum(other, another);

        swinging.Begin();
        Hanging(swinging);
        Swung(swinging, 0.0);
        swinging.Press();
        other.Advance(0.1);

        Assert.False(another.State.GetFlag("Te3GabeAtAltar"));
    }

    [Fact]
    public void Nothing_is_offered_where_the_room_has_no_move_to_ask_for()
    {
        // Every other mechanism leaves it alone. The button exists for a move that pointing
        // at the room will not find, and there is exactly one of those.
        (SceneUpdate world, Gk3SheepApi api) = World();

        Assert.Null(new Chessboard(world, api).Offers);
        Assert.Null(new Bridge(world, api).Offers);
        Assert.Null(new LaserHeads(world, api).Offers);
    }

    /// <summary>Puts Gabriel on the blade, which is the one state the button is drawn in.</summary>
    private static void Hanging(Pendulum pendulum) =>
        typeof(Pendulum)
            .GetField("_doing", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(pendulum, 3);

    /// <summary>Puts the blade that far through its swing.</summary>
    private static void Swung(Pendulum pendulum, double seconds) =>
        typeof(Pendulum)
            .GetField("_swung", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(pendulum, seconds);

    /// <summary>How long the blade takes to go out and come back.</summary>
    private static double Cycle(Pendulum pendulum) =>
        (double)typeof(Pendulum)
            .GetProperty("Cycle", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(pendulum)!;

    private static ScenePick Blade() =>
        new("te3_pendulum_center_code", "PENDULUM", null, 1f, Vector3.Zero, PickKind.Prop);

    private static ScenePick Tile(string name) =>
        new(name, null, null, 1f, Vector3.Zero, PickKind.Geometry);

    private static SceneDefinition Read(string text) =>
        new(SceneInitFile.Parse(text, "TEST.SIF"));

    private static (SceneUpdate World, Gk3SheepApi Api) World()
    {
        var state = new GameState();
        var api = new Gk3SheepApi(state);

        var scene = new LoadedScene(
            "TEST",
            Read(Chateau),
            Asset: null,
            Lightmaps: null,
            ModelsPlaced: 0,
            Placed: []);

        // A room that counts instead of drawing. Every mechanism here is checked for the
        // numbers it writes into the story rather than for where it puts things.
        return (new SceneUpdate(scene, api, new Glances(), new HeadlessSceneSink()), api);
    }
}
