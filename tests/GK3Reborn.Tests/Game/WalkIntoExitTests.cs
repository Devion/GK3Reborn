using System.Numerics;
using GK3Reborn.Formats.Actions;
using GK3Reborn.Formats.Scenes;
using GK3Reborn.Foundation.Diagnostics;
using GK3Reborn.Game;
using GK3Reborn.Game.Actions;
using GK3Reborn.Rendering;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Tests for walking into the way out of a room, which is how a way out is taken by
/// somebody on foot rather than clicking.
/// </summary>
public sealed class WalkIntoExitTests
{
    /// <summary>The verbs, with the exit column the shipped file carries.</summary>
    private const string Verbs = """
        EXIT,   c_exit_u, type=Verb
        OPEN,   c_use,    type=Verb
        LOOK,   c_look,   type=Verb
        """;

    /// <summary>A way out, a door that is not one, and a thing to look at.</summary>
    private const string Rules = """
        ARCHWAY, EXIT, ALL, script={SetLocation("rc2");}
        CUPBOARD, OPEN, ALL, script={SetVerbCount("CUPBOARD","OPEN",1);}
        PAINTING, LOOK, ALL, script={SetVerbCount("PAINTING","LOOK",1);}
        """;

    /// <summary>A slab standing up at the far end, so a ray along +Z meets it.</summary>
    private static BspFile Wall(string name, float z)
    {
        var vertices = new List<Vector3>
        {
            new(-100, 0, z),
            new(-100, 200, z),
            new(100, 200, z),
            new(100, 0, z),
        };

        return BspFile.FromParts(
            "test",
            [name],
            [
                new BspSurface
                {
                    ObjectIndex = 0,
                    TextureName = "stone",
                    LightmapUvOffset = Vector2.Zero,
                    LightmapUvScale = Vector2.One,
                    Flags = 0,
                },
            ],
            [new BspPolygon { VertexIndexOffset = 0, VertexIndexCount = 4, SurfaceIndex = 0 }],
            [.. vertices],
            new Vector2[vertices.Count],
            [0, 1, 2, 3]);
    }

    /// <summary>A room with one object at the far end, under the noun given.</summary>
    private static SceneInteraction Room(string noun, string verb, float z)
    {
        var state = new GameState();
        var api = new Gk3SheepApi(state);

        var actions = new ActionResolver(api) { Verbs = VerbLibrary.Parse(Verbs) };
        actions.Add(NvcFile.Parse(Rules, "TEST.NVC", new DiagnosticBag()));

        var scene = new LoadedScene(
            "TEST",
            new SceneDefinition(SceneInitFile.Parse(
                $"[MODELS]\nmodel=thing,noun={noun},verb={verb}\n", "TEST.SIF")),
            Asset: null,
            Lightmaps: null,
            ModelsPlaced: 0,
            Walkable: null,
            Wall("thing", z),
            Actions: actions);

        return new SceneInteraction(scene, api);
    }

    /// <summary>A ray from the middle of the room, at head height, walking along +Z.</summary>
    private static Ray Ahead() => new(new Vector3(0, 72, 0), Vector3.UnitZ);

    [Fact]
    public void An_exit_a_step_ahead_is_walked_into()
    {
        SceneInteraction room = Room("ARCHWAY", "EXIT", 60f);

        Hover? way = room.WayOut(Ahead(), blocked: false);

        Assert.NotNull(way);
        Assert.Equal("ARCHWAY", way!.Value.Noun);
        Assert.Equal("EXIT", way.Value.Default);
    }

    [Fact]
    public void An_exit_across_the_room_is_only_looked_at()
    {
        SceneInteraction room = Room("ARCHWAY", "EXIT", SceneInteraction.ReachWhenBlocked + 40f);

        Assert.Null(room.WayOut(Ahead(), blocked: false));
        Assert.Null(room.WayOut(Ahead(), blocked: true));
    }

    [Fact]
    public void Being_stopped_by_the_ground_reaches_further()
    {
        // Past a step's reach and inside the reach of somebody pressed up against the edge
        // of the ground, which is what the far side of an outdoor scene looks like.
        float between = (SceneInteraction.WithinReach + SceneInteraction.ReachWhenBlocked) / 2f;

        SceneInteraction room = Room("ARCHWAY", "EXIT", between);

        Assert.Null(room.WayOut(Ahead(), blocked: false));
        Assert.NotNull(room.WayOut(Ahead(), blocked: true));
    }

    [Fact]
    public void A_door_that_is_not_a_way_out_is_not_walked_through()
    {
        // It answers to OPEN and its script goes nowhere. Walking into the furniture must
        // not start opening it: only the verbs the game marks as ways out are taken by
        // walking into them.
        SceneInteraction room = Room("CUPBOARD", "OPEN", 60f);

        Assert.Null(room.WayOut(Ahead(), blocked: false));
    }

    [Fact]
    public void Something_there_is_nothing_to_do_with_is_not_a_way_out()
    {
        SceneInteraction room = Room("PAINTING", "LOOK", 60f);

        Assert.Null(room.WayOut(Ahead(), blocked: false));
    }

    [Fact]
    public void Walking_at_nothing_at_all_finds_no_way_out()
    {
        SceneInteraction room = Room("ARCHWAY", "EXIT", 60f);

        Assert.Null(room.WayOut(new Ray(new Vector3(0, 72, 0), -Vector3.UnitZ), blocked: true));
    }
}
