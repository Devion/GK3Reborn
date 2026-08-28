using System.Numerics;
using GK3Reborn.Content;
using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Formats.Lightmaps;
using GK3Reborn.Formats.Models;
using GK3Reborn.Formats.Scenes;
using GK3Reborn.Game;
using GK3Reborn.Game.Actors;
using GK3Reborn.Rendering;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Tests for the half of an animation that is about the room rather than about a model.
/// </summary>
/// <remarks>
/// <c>[STEXTURES]</c> and <c>[SVISIBILITY]</c> address a run of surfaces inside the room's
/// own geometry — a floor, a bar front, a curtain — and 198 lines across 78 of the corpus's
/// animations use them. They were read past in silence, which is why the bar's dance floor
/// under a disco ball was the same worn boards it always was.
/// </remarks>
public sealed class RoomAnimationTests
{
    /// <summary>A room whose geometry has one named object in it: the bar's floor.</summary>
    private static BspFile Floor() => BspFile.FromParts(
        "Rl2",
        ["rl2floor"],
        [
            new BspSurface
            {
                ObjectIndex = 0,
                TextureName = "Rl2floor",
                LightmapUvOffset = Vector2.Zero,
                LightmapUvScale = Vector2.One,
                Flags = 0,
            },
        ],
        [new BspPolygon { VertexIndexOffset = 0, VertexIndexCount = 3, SurfaceIndex = 0 }],
        [Vector3.Zero, Vector3.UnitX, Vector3.UnitZ],
        new Vector2[3],
        [0, 1, 2]);

    private static LoadedScene Scene() =>
        new(
            "RL2",
            new SceneDefinition(SceneInitFile.Parse(
                """
                [ROOM_CAMERAS]
                SEE_BAR, angle={0, 0}, pos={0, 60, 0}, Default
                """,
                "RL2.SIF")),
            Asset: null,
            Lightmaps: null,
            ModelsPlaced: 0,
            Placed: []);

    /// <summary>An update over a room whose geometry has one named object in it.</summary>
    private static (SceneUpdate Update, HeadlessSceneSink Sink) World(string animation, string body)
    {
        var sink = new HeadlessSceneSink();

        // So the sink can answer that the room really has this object, which is what tells
        // a line that means something from one shared in from another room.
        sink.AddScene(Floor());

        var update = new SceneUpdate(Scene(), new Gk3SheepApi(new GameState()), new Glances(), sink)
        {
            Animations = new AnimationLibrary(n =>
                n.Equals($"{animation}.ANM", StringComparison.OrdinalIgnoreCase) ? body : null),

            Clips = new ClipLibrary(_ => null),

            // Every texture is resident, so a swap that does not happen is the scheduling
            // rather than the archives.
            Textures = _ => true,
        };

        return (update, sink);
    }

    private const string FlashDance =
        "[HEADER]\n30\n\n[STEXTURES]\n2\n" +
        "0,rl2_disco_a,rl2floor,checker_02\n15,rl2_disco_a,rl2floor,checker_03\n";

    [Fact]
    public void An_animation_that_only_repaints_the_room_still_takes_time()
    {
        // `disco_flashdance_a` names no clips at all: it is a floor flashing and nothing
        // else. An animation reported as taking no time is one a `wait` walks straight past.
        (SceneUpdate update, _) = World("disco_flashdance_a", FlashDance);

        Assert.Equal(2.0, update.Play("disco_flashdance_a"), 3);
    }

    [Fact]
    public void The_opening_frame_is_painted_at_once_rather_than_a_frame_later()
    {
        (SceneUpdate update, HeadlessSceneSink sink) = World("disco_flashdance_a", FlashDance);

        update.Play("disco_flashdance_a");

        Assert.Equal(("rl2floor", "checker_02"), sink.SceneObjectsPainted[0]);
    }

    [Fact]
    public void A_later_frame_is_painted_when_its_frame_comes_round()
    {
        (SceneUpdate update, HeadlessSceneSink sink) = World("disco_flashdance_a", FlashDance);

        update.Play("disco_flashdance_a");

        update.Advance(0.5);
        Assert.Single(sink.SceneObjectsPainted);

        // Frame 15 at fifteen frames a second is one second in.
        update.Advance(0.6);
        Assert.Equal(("rl2floor", "checker_03"), sink.SceneObjectsPainted[^1]);
    }

    [Fact]
    public void A_looping_repaint_comes_round_again()
    {
        (SceneUpdate update, HeadlessSceneSink sink) = World("disco_flashdance_a", FlashDance);

        update.Play("disco_flashdance_a", repeat: true);

        for (int i = 0; i < 30; i++)
        {
            update.Advance(0.2);
        }

        // Six seconds of a two-second loop: the floor has been through it three times.
        Assert.True(
            sink.SceneObjectsPainted.Count >= 6,
            $"the floor was painted {sink.SceneObjectsPainted.Count} time(s)");
    }

    [Fact]
    public void Stopping_the_animation_by_name_stops_the_floor_flashing()
    {
        // A room repaint names no model, so the animation's own name is the only handle
        // StopAnimation has on it — and `StopAnimation("disco_flashdance_a")` is the only
        // thing that ever ends the bar's.
        (SceneUpdate update, HeadlessSceneSink sink) = World("disco_flashdance_a", FlashDance);

        update.Play("disco_flashdance_a", repeat: true);
        update.StopAnimating("disco_flashdance_a");

        int painted = sink.SceneObjectsPainted.Count;

        for (int i = 0; i < 30; i++)
        {
            update.Advance(0.2);
        }

        Assert.Equal(painted, sink.SceneObjectsPainted.Count);
    }

    [Fact]
    public void A_repaint_of_an_object_the_room_does_not_have_is_reported_rather_than_thrown()
    {
        // Animations are shared between rooms and every one of the bar's lines names the
        // scene variant its author had open, so a line that lands nowhere is ordinary.
        (SceneUpdate update, _) = World(
            "lbywin_a256",
            "[HEADER]\n2\n\n[STEXTURES]\n1\n0,lby_a,lbywindow,lbywin_van\n");

        update.Play("lbywin_a256");

        Assert.Contains(update.Diagnostics.Items, d => d.Code == "GK3R3346");
    }

    [Fact]
    public void A_repaint_with_a_texture_the_archives_do_not_have_leaves_the_surface_alone()
    {
        var sink = new HeadlessSceneSink();
        sink.AddScene(Floor());

        var update = new SceneUpdate(Scene(), new Gk3SheepApi(new GameState()), new Glances(), sink)
        {
            Animations = new AnimationLibrary(_ => FlashDance),
            Clips = new ClipLibrary(_ => null),
            Textures = _ => false,
        };

        update.Play("disco_flashdance_a");

        Assert.Empty(sink.SceneObjectsPainted);
        Assert.Contains(update.Diagnostics.Items, d => d.Code == "GK3R3345");
    }

    [Fact]
    public void A_script_that_asks_for_a_second_bake_and_has_nowhere_to_get_one_says_so()
    {
        (SceneUpdate update, _) = World("nothing", "[HEADER]\n1\n");

        Assert.False(update.Relit("rl2_disco_a"));
        Assert.Contains(update.Diagnostics.Items, d => d.Code == "GK3R3347");
    }

    [Fact]
    public void A_second_bake_is_handed_to_the_room()
    {
        (SceneUpdate update, _) = World("nothing", "[HEADER]\n1\n");

        string asked = string.Empty;
        update.Relight = name =>
        {
            asked = name;
            return true;
        };

        Assert.True(update.Relit("rl2_disco_a"));
        Assert.Equal("rl2_disco_a", asked);
    }
}
