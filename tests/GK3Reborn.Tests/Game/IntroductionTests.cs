using System.Numerics;
using GK3Reborn.Formats.Actions;
using GK3Reborn.Formats.Models;
using GK3Reborn.Formats.Scenes;
using GK3Reborn.Foundation.Diagnostics;
using GK3Reborn.Game;
using GK3Reborn.Game.Actors;
using GK3Reborn.Game.Story;
using GK3Reborn.Rendering;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Tests for not telling the player who somebody is before they have been introduced.
/// </summary>
public sealed class IntroductionTests
{
    [Fact]
    public void The_table_the_engine_ships_can_be_read()
    {
        Introductions table = Introductions.Open();

        Assert.Equal(12, table.Count);
    }

    [Fact]
    public void Buthane_is_a_stranger_until_she_has_explained_the_tour()
    {
        // Her introduction is the tour-group topic and not a T_INTRODUCE, which she has
        // none of. RC1110A.NVC writes exactly this as MET_BUTHANE.
        var state = new GameState();
        var api = new Gk3SheepApi(state);

        Introductions table = Introductions.Open();

        Assert.False(table.Knows("BUTHANE", api));

        state.SetTopicCount("BUTHANE", "T_TOUR_GROUP", 1);

        Assert.True(table.Knows("BUTHANE", api));
    }

    [Fact]
    public void The_two_women_of_the_museum_are_introduced_together()
    {
        // The data has one noun for the pair and one condition, so all three names turn
        // over at once. Withholding Estelle's after Lady Howard has said it would be worse
        // than showing both.
        var state = new GameState();
        var api = new Gk3SheepApi(state);

        Introductions table = Introductions.Open();

        Assert.False(table.Knows("LADY_HOWARD", api));
        Assert.False(table.Knows("ESTELLE", api));

        state.SetTopicCount("LADY_H_ESTELLE", "T_INTRODUCE", 1);

        Assert.True(table.Knows("LADY_HOWARD", api));
        Assert.True(table.Knows("ESTELLE", api));
    }

    [Fact]
    public void Anybody_the_table_says_nothing_about_is_known()
    {
        // The safe way round. Gabriel arrives knowing Mosely, the maid is what she is
        // rather than who she is, and a character the data never asks about keeps a name
        // rather than becoming permanently anonymous.
        var api = new Gk3SheepApi(new GameState());

        Introductions table = Introductions.Open();

        Assert.True(table.Knows("MOSELY", api));
        Assert.True(table.Knows("MAID", api));
        Assert.True(table.Knows("BATHROOM_DOOR", api));
        Assert.True(table.Knows(null, api));
    }

    [Fact]
    public void A_condition_that_cannot_be_read_answers_known()
    {
        // A label is not worth failing over, and it is asked every frame the pointer rests
        // on somebody, so a broken line must not become a diagnostic a hundred times a
        // second either.
        Introductions table = Introductions.Parse(
            """
            # a comment
            SOMEBODY | ))) not an expression (((
            NOBODY   |
            """);

        Assert.Equal(1, table.Count);
        Assert.True(table.Knows("SOMEBODY", new Gk3SheepApi(new GameState())));
    }

    [Fact]
    public void Which_of_them_is_a_woman_comes_out_of_their_shoes()
    {
        // The only thing in the shipped data that says so, and it is there to pick a
        // footstep sound.
        CharacterLibrary characters = CharacterLibrary.Parse(
            """
            [MAD]
            ModelName=mad
            ShoeType=Female Leather

            [GAB]
            ModelName=gab
            ShoeType=Male Boot

            [BAR]
            ModelName=bar
            """);

        Assert.True(characters.Of("mad")?.IsWoman);
        Assert.False(characters.Of("gab")?.IsWoman);
        Assert.Null(characters.Of("bar")?.IsWoman);
    }

    [Fact]
    public void The_label_says_what_can_be_seen_until_the_name_is_earned()
    {
        var state = new GameState();
        var api = new Gk3SheepApi(state);
        var sink = new HeadlessSceneSink();

        PlacedModel person = Standing(sink, "mad", "BUTHANE");

        var scene = new LoadedScene(
            "TEST",
            new SceneDefinition(SceneInitFile.Parse("[MODELS]", "TEST.SIF")),
            Asset: null,
            Lightmaps: null,
            ModelsPlaced: 1,
            Walkable: null,
            Geometry: null,
            Placed: [person],
            Actions: Rules());

        var interaction = new SceneInteraction(scene, api)
        {
            Introductions = Introductions.Open(),
            Watcher = new SceneUpdate(scene, api, new Glances(), sink)
            {
                Characters = CharacterLibrary.Parse("[MAD]\nModelName=mad\nShoeType=Female Leather"),
            },
        };

        Assert.Equal("Woman", Assert.Single(interaction.Nouns()).Noun);

        state.SetTopicCount("BUTHANE", "T_TOUR_GROUP", 1);

        Assert.Equal("BUTHANE", Assert.Single(interaction.Nouns()).Noun);
    }

    [Fact]
    public void A_stranger_the_character_file_has_no_shoes_for_keeps_their_name()
    {
        // Both ways of not knowing fail the same way, and it is the safe one: a name a
        // little early is a small spoiler, a permanent stranger is not recoverable.
        var api = new Gk3SheepApi(new GameState());
        var sink = new HeadlessSceneSink();

        PlacedModel person = Standing(sink, "mad", "BUTHANE");

        var scene = new LoadedScene(
            "TEST",
            new SceneDefinition(SceneInitFile.Parse("[MODELS]", "TEST.SIF")),
            Asset: null,
            Lightmaps: null,
            ModelsPlaced: 1,
            Walkable: null,
            Geometry: null,
            Placed: [person],
            Actions: Rules());

        var interaction = new SceneInteraction(scene, api)
        {
            Introductions = Introductions.Open(),
            Watcher = new SceneUpdate(scene, api, new Glances(), sink),
        };

        Assert.Equal("BUTHANE", Assert.Single(interaction.Nouns()).Noun);
    }

    [Fact]
    public void Showing_every_hotspot_at_once_renames_the_doors_too()
    {
        // Holding Alt draws every noun in the room, and it drew them raw — so the corridor
        // the door fix was written for still named all eight guests when the key was held,
        // which is the one place it matters most.
        var api = new Gk3SheepApi(new GameState());
        var sink = new HeadlessSceneSink();

        PlacedModel door = Standing(sink, "hal_27_door_scene", "EMILIOS_DOOR");

        var resolver = new ActionResolver(api);

        resolver.Add(NvcFile.Parse(
            """EMILIOS_DOOR, LOOK, ALL, script={}""", "test.nvc", new DiagnosticBag()));

        var scene = new LoadedScene(
            "TEST",
            new SceneDefinition(SceneInitFile.Parse("[MODELS]", "TEST.SIF")),
            Asset: null,
            Lightmaps: null,
            ModelsPlaced: 1,
            Walkable: null,
            Geometry: null,
            Placed: [door],
            Actions: resolver);

        var interaction = new SceneInteraction(scene, api)
        {
            Introductions = Introductions.Open(),
        };

        Assert.Equal("Room 27", Assert.Single(interaction.Nouns()).Noun);
    }

    [Fact]
    public void The_roster_says_who_the_story_has_introduced_by_each_point_in_day_one()
    {
        // For the saves that cannot answer the conditions above — the ones the 1999 game
        // wrote, which carry a timeblock and no topic counts at all. Each block is credited
        // with its own line and every line before it.
        Introductions table = Introductions.Open();

        Assert.Equal(
            ["BUTHANE", "EMILIO", "ESTELLE", "GIRARD", "JEAN", "LADY_HOWARD", "LADY_H_ESTELLE"],
            table.MetBy(new Timeblock(1, 10, IsAfternoon: false)));

        // Noon adds the church and the dining room.
        Assert.Contains("BUCHELLI", table.MetBy(new Timeblock(1, 12, IsAfternoon: true)));
        Assert.Contains("ABBE", table.MetBy(new Timeblock(1, 12, IsAfternoon: true)));
        Assert.Contains("WILKES", table.MetBy(new Timeblock(1, 12, IsAfternoon: true)));
        Assert.DoesNotContain("LARRY", table.MetBy(new Timeblock(1, 12, IsAfternoon: true)));

        // And two in the afternoon adds what the moped reaches. From there on the list is
        // everybody, which is why nothing new is added by four or six.
        Assert.Equal(table.Count, table.MetBy(new Timeblock(1, 2, IsAfternoon: true)).Count);
        Assert.Equal(table.Count, table.MetBy(new Timeblock(1, 6, IsAfternoon: true)).Count);
    }

    [Fact]
    public void A_save_from_a_later_day_knows_the_whole_cast()
    {
        // Every introduction in the game is on day one, so a save standing in day two or
        // three has made all of them. Answered from the day rather than from the roster
        // adding up to the same thing, which it would stop doing the moment a line is added.
        Introductions table = Introductions.Open();

        Assert.Equal(table.Count, table.MetBy(new Timeblock(2, 7, IsAfternoon: false)).Count);
        Assert.Equal(table.Count, table.MetBy(new Timeblock(3, 12, IsAfternoon: true)).Count);
        Assert.Equal(
            table.Nouns.OrderBy(noun => noun, StringComparer.Ordinal),
            table.MetBy(new Timeblock(3, 6, IsAfternoon: true)));
    }

    [Fact]
    public void A_roster_line_is_told_from_a_person_by_its_timeblock()
    {
        // The two kinds of line share a file and a separator, and only the shape of the
        // left side separates them. A condition with an "||" in it is why they cannot be
        // told apart by counting bars.
        Introductions table = Introductions.Parse(
            """
            SOMEBODY | GetFlag("A") || GetFlag("B")
            110A     | SOMEBODY
            """);

        Assert.Equal(1, table.Count);
        Assert.Equal(["SOMEBODY"], table.MetBy(new Timeblock(1, 10, IsAfternoon: false)));

        var state = new GameState();

        Assert.False(table.Knows("SOMEBODY", new Gk3SheepApi(state)));

        state.SetFlag("B");

        Assert.True(table.Knows("SOMEBODY", new Gk3SheepApi(state)));
    }

    [Fact]
    public void A_restored_game_knows_who_its_save_says_it_knows()
    {
        // The whole point of the roster: no topic has been raised in this state and none
        // ever will be, because the game it came from was played somewhere else.
        var state = new GameState();
        var api = new Gk3SheepApi(state);

        Introductions table = Introductions.Open();

        Assert.False(table.Knows("BUTHANE", api));

        state.Restore(state.Capture() with { Introduced = ["BUTHANE"] });

        Assert.True(table.Knows("BUTHANE", api));
        Assert.False(table.Knows("WILKES", api));

        // And it goes on being known after the player saves again: a restored game that
        // wrote itself back down without this would forget at the second load.
        Assert.Equal(["BUTHANE"], state.Capture().Introduced);
    }

    [Fact]
    public void A_restored_game_forgets_the_introductions_of_the_one_before_it()
    {
        // Loading into a state that still holds the last game's answers is the classic save
        // bug, and this is a set like any other: see GameState.Restore.
        var state = new GameState();

        state.Restore(state.Capture() with { Introduced = ["BUTHANE"] });
        state.Restore(state.Capture() with { Introduced = [] });

        Assert.Empty(state.Introduced);
        Assert.False(Introductions.Open().Knows("BUTHANE", new Gk3SheepApi(state)));
    }

    /// <summary>Something for the noun to answer to, so the picker offers it at all.</summary>
    [Fact]
    public void A_moped_is_anybody_s_until_the_story_says_whose_it_is()
    {
        // The same leak on the other half of the cast. Five suspects hire mopeds and the
        // scene files name each one after its owner, so the port's label handed the licence
        // plate puzzle over on day one: MOSELYS_MOPED reads back as "Mosely's moped" while
        // it is standing in the hire shop's yard with nobody yet on it.
        var state = new GameState();
        var api = new Gk3SheepApi(state);
        var sink = new HeadlessSceneSink();

        PlacedModel bike = Standing(sink, "ves_01", "MOSELYS_MOPED");
        PlacedModel plate = Standing(sink, "ves_01_liscense_plate", "MOSELYS_MOPED_LICENSE");

        var scene = new LoadedScene(
            "TEST",
            new SceneDefinition(SceneInitFile.Parse("[MODELS]", "TEST.SIF")),
            Asset: null,
            Lightmaps: null,
            ModelsPlaced: 2,
            Walkable: null,
            Geometry: null,
            Placed: [bike, plate],
            Actions: Rules());

        var interaction = new SceneInteraction(scene, api)
        {
            Introductions = Introductions.Open(),
            Watcher = new SceneUpdate(scene, api, new Glances(), sink),
        };

        Assert.Equal("Moped", interaction.NameOf("MOSELYS_MOPED"));
        Assert.Equal("Moped number plate", interaction.NameOf("MOSELYS_MOPED_LICENSE"));

        // SeenMoselyMop, which L'Homme Mort sets the first time Gabriel looks at him
        // standing beside it. MOP_ALL.NVC's KNOW_MOSELYS_MOPED is this flag and nothing
        // else.
        state.SetFlag("SeenMoselyMop");

        Assert.Equal("MOSELYS_MOPED", interaction.NameOf("MOSELYS_MOPED"));
        Assert.Equal("MOSELYS_MOPED_LICENSE", interaction.NameOf("MOSELYS_MOPED_LICENSE"));
    }

    [Fact]
    public void A_moped_nobody_ever_rides_past_is_named_once_Sidney_identifies_it()
    {
        // Wilkes, Buchelli and Emilio have no "seen them on it" flag: the only way to work
        // out whose those three are is to copy the plate and let Grace link it to a suspect,
        // which is what IDedWilkesVehicle and its two siblings record.
        var state = new GameState();
        var api = new Gk3SheepApi(state);
        var sink = new HeadlessSceneSink();

        PlacedModel bike = Standing(sink, "ves_03", "WILKES_MOPED");

        var scene = new LoadedScene(
            "TEST",
            new SceneDefinition(SceneInitFile.Parse("[MODELS]", "TEST.SIF")),
            Asset: null,
            Lightmaps: null,
            ModelsPlaced: 1,
            Walkable: null,
            Geometry: null,
            Placed: [bike],
            Actions: Rules());

        var interaction = new SceneInteraction(scene, api)
        {
            Introductions = Introductions.Open(),
            Watcher = new SceneUpdate(scene, api, new Glances(), sink),
        };

        Assert.Equal("Moped", interaction.NameOf("WILKES_MOPED"));

        state.SetFlag("IDedWilkesVehicle");

        Assert.Equal("WILKES_MOPED", interaction.NameOf("WILKES_MOPED"));
    }

    [Fact]
    public void Gabriel_s_own_moped_is_his_once_he_has_hired_it()
    {
        // It is his because he hired it, so the label says so from the moment he has the
        // keys — and not a moment before. The hire shop stands it in its yard under this
        // very noun at two o'clock on day one, hours before he has paid for it.
        var api = new Gk3SheepApi(new GameState());
        var sink = new HeadlessSceneSink();

        PlacedModel bike = Standing(sink, "bikebody", "GABES_MOPED");

        var scene = new LoadedScene(
            "TEST",
            new SceneDefinition(SceneInitFile.Parse("[MODELS]", "TEST.SIF")),
            Asset: null,
            Lightmaps: null,
            ModelsPlaced: 1,
            Walkable: null,
            Geometry: null,
            Placed: [bike],
            Actions: Rules());

        var interaction = new SceneInteraction(scene, api)
        {
            Introductions = Introductions.Open(),
            Watcher = new SceneUpdate(scene, api, new Glances(), sink),
        };

        Assert.Equal(
            "Harley for hire", interaction.NameOf("GABES_MOPED"));

        api.State.Inventory.Add("GABRIEL", "MOPED_KEYS");

        Assert.Equal("GABES_MOPED", interaction.NameOf("GABES_MOPED"));
    }

    private static ActionResolver Rules()
    {
        var resolver = new ActionResolver(new Gk3SheepApi(new GameState()));

        resolver.Add(NvcFile.Parse(
            """BUTHANE, LOOK, ALL, script={}""", "test.nvc", new DiagnosticBag()));

        return resolver;
    }

    /// <summary>A one-triangle person standing at the origin.</summary>
    private static PlacedModel Standing(HeadlessSceneSink sink, string name, string noun)
    {
        var submesh = new ModSubmesh
        {
            TextureName = "skin",
            Color = (255, 255, 255),
            Positions = [new Vector3(-10, -10, 0), new Vector3(0, 20, 0), new Vector3(10, -10, 0)],
            Normals = [-Vector3.UnitZ, -Vector3.UnitZ, -Vector3.UnitZ],
            TexCoords = new Vector2[3],
            Indices = [0, 1, 2],
        };

        var mesh = new ModMesh
        {
            MeshToLocal = Matrix4x4.Identity,
            BoundsMin = new Vector3(-10, -10, 0),
            BoundsMax = new Vector3(10, 20, 0),
            Submeshes = [submesh],
        };

        ModFile model = ModFile.FromMeshes(name, [mesh]);

        return new PlacedModel(
            name,
            noun,
            Verb: null,
            model,
            Matrix4x4.Identity,
            PlacedModelKind.Actor)
        {
            Placement = sink.Add(model, Matrix4x4.Identity),
            Stage = sink,
        };
    }
}
