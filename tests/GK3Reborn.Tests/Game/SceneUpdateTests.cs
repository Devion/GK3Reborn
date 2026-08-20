using System.Numerics;
using GK3Reborn.Formats.Actions;
using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Formats.Lightmaps;
using GK3Reborn.Formats.Models;
using GK3Reborn.Formats.Scenes;
using GK3Reborn.Foundation.Diagnostics;
using GK3Reborn.Game;
using GK3Reborn.Game.Actors;
using GK3Reborn.Rendering;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Tests for the part of the game that runs on its own.
/// </summary>
/// <remarks>
/// The caller says how much time has passed and everything downstream is a function of
/// that number, so these can step a world by a tenth of a second at a time and know
/// exactly what should have happened. That is the property <c>ADR 0004</c> is protecting:
/// nothing reads a clock but the platform layer.
/// </remarks>
public sealed class SceneUpdateTests
{
    /// <summary>A sink that remembers where things were last put.</summary>
    private sealed class Watcher : ISceneSink
    {
        private int _next;

        public Dictionary<(int Placement, int Mesh), Matrix4x4> Turns { get; } = [];

        public Vector3 Minimum => Vector3.Zero;

        public Vector3 Maximum => Vector3.One;

        public int TextureCount => 0;

        public int TriangleCount => 0;

        public void AddTexture(string name, DecodedImage image)
        {
        }

        public bool HasTexture(string name) => false;

        public void SetSkybox(IReadOnlyList<DecodedImage> faces, float azimuth)
        {
        }

        public ModelPlacement Add(
            ModFile model,
            Matrix4x4? transform = null,
            IReadOnlyDictionary<int, Matrix4x4>? meshTurns = null) => new(_next++);

        public void AddScene(
            BspFile scene, MulFile? lightmaps = null, IReadOnlySet<string>? hiddenObjects = null)
        {
        }

        public void TurnMesh(ModelPlacement placement, int mesh, Matrix4x4 turn) =>
            Turns[(placement.Id, mesh)] = turn;

        /// <summary>Where each placed model has been moved to.</summary>
        public Dictionary<int, Matrix4x4> Moves { get; } = [];

        public void MoveModel(ModelPlacement placement, Matrix4x4 transform) =>
            Moves[placement.Id] = transform;

        /// <summary>Where each mesh has been posed by an animation.</summary>
        public Dictionary<(int, int), Matrix4x4> Poses { get; } = [];

        public void PoseMesh(ModelPlacement placement, int mesh, Matrix4x4 meshToLocal) =>
            Poses[(placement.Id, mesh)] = meshToLocal;

        /// <summary>What each submesh was last reshaped to.</summary>
        public Dictionary<(int, int, int), IReadOnlyList<Vector3>> Shapes { get; } = [];

        public void ShapeMesh(
            ModelPlacement placement, int mesh, int submesh, IReadOnlyList<Vector3> positions) =>
            Shapes[(placement.Id, mesh, submesh)] = positions;
    }

    private static ModFile Person()
    {
        ModMesh Mesh(float height, string texture) => new()
        {
            MeshToLocal = Matrix4x4.CreateTranslation(0, height, 0),
            BoundsMin = Vector3.Zero,
            BoundsMax = Vector3.One,
            Submeshes =
            [
                new ModSubmesh
                {
                    TextureName = texture,
                    Color = (255, 255, 255),
                    Positions = [Vector3.Zero],
                    Normals = [Vector3.UnitY],
                    TexCoords = [Vector2.Zero],
                    Indices = [0, 0, 0],
                },
            ],
        };

        return ModFile.FromMeshes("GAB", [Mesh(30, "GAB_SHIRT"), Mesh(65, "GAB_FACE")]);
    }

    private static LoadedScene Scene() =>
        new(
            "TEST",
            new SceneDefinition(SceneInitFile.Parse(
                """
                [ROOM_CAMERAS]
                NEAR, angle={0, 0}, pos={0, 60, 0}, Default
                FAR,  angle={0, 0}, pos={0, 60, 200}
                """,
                "TEST.SIF")),
            Asset: null,
            Lightmaps: null,
            ModelsPlaced: 1,
            Placed:
            [
                new PlacedModel(
                    "gab",
                    "GABRIEL",
                    Verb: null,
                    Person(),
                    Matrix4x4.Identity,
                    PlacedModelKind.Actor,
                    new ModelPlacement(0)),
            ]);

    private static (SceneUpdate Update, Glances Glances, Watcher Sink, GameState State) World()
    {
        var state = new GameState();
        var glances = new Glances();
        var sink = new Watcher();

        return (new SceneUpdate(Scene(), new Gk3SheepApi(state), glances, sink), glances, sink, state);
    }

    [Fact]
    public void An_actor_with_a_head_can_turn_it_and_a_prop_cannot()
    {
        (SceneUpdate update, _, _, _) = World();

        Assert.Equal(1, update.Movable);
    }

    [Fact]
    public void A_head_turns_over_several_frames_rather_than_arriving()
    {
        // The whole difference between a character glancing at you and a character who was
        // always facing you.
        (SceneUpdate update, Glances glances, Watcher sink, _) = World();

        glances.Look(new Glance("gab", "SOMETHING", new Vector3(100, 65, 0), Quick: false));

        // A quarter turn at three radians a second wants about half a second; one frame at
        // sixty is nowhere near enough.
        update.Advance(1.0 / 60);

        Matrix4x4 after = sink.Turns[(0, 1)];
        float first = MathF.Atan2(after.M31, after.M33);

        Assert.True(first > 0.01f, "the head started turning");
        Assert.True(first < 0.2f, "and did not arrive in one frame");

        for (int i = 0; i < 120; i++)
        {
            update.Advance(1.0 / 60);
        }

        float settled = MathF.Atan2(sink.Turns[(0, 1)].M31, sink.Turns[(0, 1)].M33);

        // Square to the side is past what a neck manages, so it settles at the limit.
        Assert.Equal(Glances.YawLimit, settled, 2);
    }

    [Fact]
    public void A_quick_glance_arrives_at_once()
    {
        (SceneUpdate update, Glances glances, Watcher sink, _) = World();

        glances.Look(new Glance("gab", "SOMETHING", new Vector3(100, 65, 0), Quick: true));
        update.Advance(1.0 / 60);

        Assert.Equal(
            Glances.YawLimit,
            MathF.Atan2(sink.Turns[(0, 1)].M31, sink.Turns[(0, 1)].M33),
            2);
    }

    [Fact]
    public void A_head_comes_back_when_there_is_nothing_left_to_look_at()
    {
        (SceneUpdate update, Glances glances, Watcher sink, _) = World();

        glances.Look(new Glance("gab", "SOMETHING", new Vector3(100, 65, 0), Quick: true));
        update.Advance(1.0 / 60);

        glances.Cancel("gab");

        for (int i = 0; i < 120; i++)
        {
            update.Advance(1.0 / 60);
        }

        // Facing forward again, and eased back rather than snapping.
        Assert.Equal(0f, MathF.Atan2(sink.Turns[(0, 1)].M31, sink.Turns[(0, 1)].M33), 3);
    }

    [Fact]
    public void Nothing_moves_when_no_time_passes()
    {
        (SceneUpdate update, Glances glances, Watcher sink, _) = World();

        glances.Look(new Glance("gab", "SOMETHING", new Vector3(100, 65, 0), Quick: false));

        Assert.Empty(update.Advance(0));
        Assert.Empty(sink.Turns);
    }

    [Fact]
    public void A_timer_that_comes_due_performs_its_action()
    {
        var state = new GameState();
        var api = new Gk3SheepApi(state);
        var resolver = new ActionResolver(api);

        resolver.Add(NvcFile.Parse(
            """PHONE, RING, ALL, script={SetFlag("rang")}""", "test.nvc", new DiagnosticBag()));

        var update = new SceneUpdate(
            Scene(), api, new Glances(), new Watcher(), resolver, new ActionRunner(api));

        state.Timers.Set("PHONE", "RING", 1.0);

        Assert.Empty(update.Advance(0.5));
        Assert.False(state.GetFlag("rang"));

        Assert.Single(update.Advance(0.6));
        Assert.True(state.GetFlag("rang"));
    }

    [Fact]
    public void A_timer_with_nothing_to_run_says_so_rather_than_going_quiet()
    {
        (SceneUpdate update, _, _, GameState state) = World();

        state.Timers.Set("PHONE", "RING", 1.0);

        Assert.Contains(
            "nothing here to run it",
            Assert.Single(update.Advance(2)),
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_cut_takes_the_view_there_at_once()
    {
        (SceneUpdate update, _, _, GameState state) = World();

        update.StartAt(new Camera { Position = Vector3.Zero, Target = Vector3.UnitZ });

        state.CameraGliding = false;
        state.CameraAngle = "FAR";

        update.Advance(1.0 / 60);

        Assert.False(update.Gliding);
        Assert.Equal(200f, update.View!.Position.Z, 1);
    }

    [Fact]
    public void A_glide_takes_a_moment_and_lands_exactly_where_a_cut_would()
    {
        (SceneUpdate update, _, _, GameState state) = World();

        update.StartAt(new Camera { Position = Vector3.Zero, Target = Vector3.UnitZ });

        state.CameraGliding = true;
        state.CameraAngle = "FAR";

        update.Advance(SceneUpdate.GlideSeconds / 3);

        Assert.True(update.Gliding, "still on its way");
        Assert.InRange(update.View!.Position.Z, 1f, 199f);

        update.Advance(SceneUpdate.GlideSeconds);

        Assert.False(update.Gliding);
        Assert.Equal(200f, update.View!.Position.Z, 1);
    }

    [Fact]
    public void A_glide_with_nowhere_to_leave_from_is_a_cut()
    {
        // The scene has only just opened and nobody has said where the view is, so there is
        // nothing to interpolate away from.
        (SceneUpdate update, _, _, GameState state) = World();

        state.CameraGliding = true;
        state.CameraAngle = "FAR";

        update.Advance(1.0 / 60);

        Assert.False(update.Gliding);
        Assert.Equal(200f, update.View!.Position.Z, 1);
    }

    [Fact]
    public void The_view_stays_where_the_story_left_it()
    {
        (SceneUpdate update, _, _, GameState state) = World();

        update.StartAt(new Camera { Position = Vector3.Zero, Target = Vector3.UnitZ });
        state.CameraAngle = "FAR";

        for (int i = 0; i < 200; i++)
        {
            update.Advance(1.0 / 60);
        }

        Assert.Equal(200f, update.View!.Position.Z, 1);
    }
}
