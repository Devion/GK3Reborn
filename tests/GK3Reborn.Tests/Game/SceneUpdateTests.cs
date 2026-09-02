using System.Numerics;
using GK3Reborn.Formats.Actions;
using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Formats.Lightmaps;
using GK3Reborn.Formats.Models;
using GK3Reborn.Formats.Scenes;
using GK3Reborn.Foundation.Diagnostics;
using GK3Reborn.Game;
using GK3Reborn.Game.Navigation;
using GK3Reborn.Game.Actors;
using GK3Reborn.Rendering;
using GK3Reborn.Sheep;
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

        public Action? Progress { get; set; }

        public Dictionary<(int Placement, int Mesh), Matrix4x4> Turns { get; } = [];

        public Vector3 Minimum => Vector3.Zero;

        public Vector3 Maximum => Vector3.One;

        public int TextureCount => 0;

        public int TriangleCount => 0;

        public void AddTexture(string name, CompressedImage image) =>
            AddTexture(name, new DecodedImage(image.Width, image.Height, [], false, "block"));

        public void AddNormalMap(string name, CompressedImage image) =>
            AddNormalMap(name, new DecodedImage(image.Width, image.Height, [], false, "block"));

        public void AddTexture(string name, DecodedImage image)
        {
        }

        public bool HasTexture(string name) => false;

        public void AddNormalMap(string name, DecodedImage image)
        {
        }

        public bool HasNormalMap(string name) => false;

        public void AddOrmMap(string name, DecodedImage image)
        {
        }

        public void AddOrmMap(string name, CompressedImage image)
        {
        }

        public bool HasOrmMap(string name) => false;

        public void AddHeightMap(string name, DecodedImage image)
        {
        }

        public void AddHeightMap(string name, CompressedImage image)
        {
        }

        public bool HasHeightMap(string name) => false;

        public void SetSkybox(IReadOnlyList<DecodedImage> faces, float azimuth)
        {
        }

        public void SetTerrain(TerrainBackdrop backdrop)
        {
        }

        public void ReliefEverywhere(IReadOnlySet<string> textures)
        {
        }

        public void MoveInWind(IReadOnlySet<string> textures)
        {
        }

        public ModelPlacement Add(
            ModFile model,
            Matrix4x4? transform = null,
            IReadOnlyDictionary<int, Matrix4x4>? meshTurns = null)
        {
            // The transform the model was added with, so TransformOf answers for a model
            // that has never moved. The real geometry keeps it from the moment a model is
            // placed; recording it only on MoveModel made this stub say every unmoved actor
            // stood at the identity, which is a heading of a half turn.
            Moves[_next] = transform ?? Matrix4x4.Identity;

            return new(_next++);
        }

        public void KeepRelief(IReadOnlySet<string> textures)
        {
        }

        public void AddScene(
            BspFile scene,
            MulFile? lightmaps = null,
            IReadOnlySet<string>? hiddenObjects = null,
            string? floorObject = null,
            IReadOnlySet<int>? hiddenSurfaces = null,
            SceneOverlay? enhanced = null)
        {
        }

        public void TurnMesh(ModelPlacement placement, int mesh, Matrix4x4 turn) =>
            Turns[(placement.Id, mesh)] = turn;

        /// <summary>Where each placed model has been moved to.</summary>
        public Dictionary<int, Matrix4x4> Moves { get; } = [];

        public void MoveModel(ModelPlacement placement, Matrix4x4 transform) =>
            Moves[placement.Id] = transform;

        public Matrix4x4 TransformOf(ModelPlacement placement) =>
            Moves.TryGetValue(placement.Id, out Matrix4x4 where) ? where : Matrix4x4.Identity;

        /// <summary>What each texture of each model has been painted over with.</summary>
        public Dictionary<(int Placement, string Texture), string?> Painted { get; } =
            new();

        public void Repaint(ModelPlacement placement, string texture, string? painted) =>
            Painted[(placement.Id, texture)] = painted;

        /// <summary>Which models have been hidden, and which shown again.</summary>
        public Dictionary<int, bool> Visible { get; } = [];

        /// <summary>Which of the room's own objects a script has shown or hidden.</summary>
        public List<(string Object, bool Visible)> SceneObjects { get; } = [];

        public bool SetSceneObjectVisible(string objectName, bool visible)
        {
            SceneObjects.Add((objectName, visible));
            return true;
        }

        /// <summary>Which of the room's own objects an animation has repainted, and with what.</summary>
        public List<(string Object, string? Texture)> ScenePainted { get; } = [];

        public bool PaintSceneObject(string objectName, string? texture)
        {
            ScenePainted.Add((objectName, texture));
            return true;
        }

        /// <summary>The replacement bakes a script has handed the room.</summary>
        public List<string> Baked { get; } = [];

        public bool SwapLightmaps(GK3Reborn.Formats.Lightmaps.MulFile lightmaps)
        {
            Baked.Add(lightmaps.Name);
            return true;
        }

        public void SetSelfLit(ModelPlacement placement, bool selfLit)
        {
        }

        public void SetVisible(ModelPlacement placement, bool visible) =>
            Visible[placement.Id] = visible;

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

    private static LoadedScene Scene(string? text = null, WalkBoundary? boundary = null) =>
        new(
            "TEST",
            new SceneDefinition(SceneInitFile.Parse(
                text ??
                """
                [ROOM_CAMERAS]
                NEAR, angle={0, 0}, pos={0, 60, 0}, Default
                FAR,  angle={0, 0}, pos={0, 60, 200}
                """,
                "TEST.SIF")),
            Asset: null,
            Lightmaps: null,
            ModelsPlaced: 1,
            Walkable: boundary,
            Placed:
            [
                new PlacedModel(
                    "gab",
                    "GABRIEL",
                    Verb: null,
                    Person(),

                    // Placed at heading zero, which faces +Z. Said rather than assumed: an
                    // unrotated placement is a model facing −Z, so Identity here would put
                    // everything the glance tests look at behind the actor.
                    Matrix4x4.CreateRotationY(Walker.Rotation(0f)),
                    PlacedModelKind.Actor,
                    new ModelPlacement(0)),
            ]);

    private static (SceneUpdate Update, Glances Glances, Watcher Sink, GameState State) World()
    {
        var state = new GameState();
        var glances = new Glances();
        var sink = new Watcher();

        // Where the actor is standing, as the geometry would already know it. The real one
        // keeps a placement's transform from the moment the model is added; this fixture
        // builds its placement by hand, so it has to be told the same thing — and the heads
        // read the model's own transform rather than remembering one they were handed.
        sink.Moves[0] = Matrix4x4.CreateRotationY(Walker.Rotation(0f));

        return (new SceneUpdate(Scene(), new Gk3SheepApi(state), glances, sink), glances, sink, state);
    }

    [Fact]
    public void The_story_takes_the_camera_while_an_action_is_playing()
    {
        // Reported as the view jumping: the player flew the camera off during a cutscene
        // and the next cut the script made snapped it back across the room. What was
        // missing is the answer to who is holding the camera, not the cut itself.
        var state = new GameState();
        var api = new Gk3SheepApi(state);
        var update = new SceneUpdate(Scene(), api, new Glances(), new Watcher());

        Assert.False(update.Directing);

        api.ActionSeconds = 2;
        Assert.True(update.Occupied);
        Assert.True(update.Directing);

        api.ActionSeconds = 0;
        Assert.False(update.Directing);
    }

    [Fact]
    public void A_player_who_has_turned_cinematics_off_keeps_the_camera()
    {
        // The preference exists so the story stops moving the view. Taking the controls
        // away as well would leave the player looking at whatever the room opened on for
        // the length of the scene, with nothing directing it and no way to turn.
        var state = new GameState { CinematicsEnabled = false };
        var api = new Gk3SheepApi(state) { ActionSeconds = 2 };
        var update = new SceneUpdate(Scene(), api, new Glances(), new Watcher());

        Assert.True(update.Occupied);
        Assert.False(update.Directing);

        // Unless a script has insisted, which is what SetForcedCameraCuts is for: the
        // preference gives way for as long as it holds, for the cuts and for the controls
        // alike.
        state.ForcedCameraCuts = true;
        Assert.True(update.Directing);
    }

    [Fact]
    public void Forced_camera_cuts_hold_the_camera_with_nothing_else_playing()
    {
        // A script that has said it is directing is directing, action or no action: the
        // shot it is setting up is often several cuts with waits between them.
        var state = new GameState { ForcedCameraCuts = true };
        var update = new SceneUpdate(
            Scene(), new Gk3SheepApi(state), new Glances(), new Watcher());

        Assert.False(update.Occupied);
        Assert.True(update.Directing);
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

    /// <summary>A room with one patch of floor that does something, around the origin.</summary>
    private const string TriggerRoom =
        """
        [ROOM_CAMERAS]
        NEAR, angle={0, 0}, pos={0, 60, 0}, Default

        [TRIGGERS]
        noun=GET_CLOSE, rect={-50, 50, 50, -50}
        """;

    private static (SceneUpdate Update, GameState State) TriggerWorld(string script)
    {
        var state = new GameState();
        var api = new Gk3SheepApi(state);
        var resolver = new ActionResolver(api);

        resolver.Add(NvcFile.Parse(script, "test.nvc", new DiagnosticBag()));

        var sink = new Watcher();
        sink.Moves[0] = Matrix4x4.CreateRotationY(Walker.Rotation(0f));

        return (
            new SceneUpdate(
                Scene(TriggerRoom), api, new Glances(), sink, resolver, new ActionRunner(api)),
            state);
    }

    [Fact]
    public void Standing_on_a_trigger_does_its_nouns_walk()
    {
        // How the game says "and then you overhear them". The player is placed at the
        // origin, which the fixture's rectangle is drawn around.
        (SceneUpdate update, GameState state) = TriggerWorld(
            """GET_CLOSE, WALK, ALL, script={SetFlag("overheard")}""");

        Assert.Single(update.Advance(1.0 / 60));
        Assert.True(state.GetFlag("overheard"));
    }

    [Fact]
    public void A_trigger_the_player_is_not_standing_in_does_nothing()
    {
        (SceneUpdate update, GameState state) = TriggerWorld(
            """GET_CLOSE, WALK, ALL, script={SetFlag("overheard")}""");

        update.Place("GABRIEL", new Vector3(400, 0, 400), 0f);

        Assert.Empty(update.Advance(1.0 / 60));
        Assert.False(state.GetFlag("overheard"));
    }

    [Fact]
    public void A_trigger_whose_noun_has_nothing_written_about_it_is_quiet()
    {
        // 26 of the corpus's 34 rectangles are this at any given point in the story. The
        // player walks over them as they would over any other patch of floor, and a room
        // that reported it every frame would drown everything else out.
        (SceneUpdate update, GameState state) = TriggerWorld(
            """SOMETHING_ELSE, WALK, ALL, script={SetFlag("overheard")}""");

        for (int i = 0; i < 10; i++)
        {
            Assert.Empty(update.Advance(1.0 / 60));
        }

        Assert.False(state.GetFlag("overheard"));
    }

    [Fact]
    public void A_trigger_does_not_fire_again_while_what_it_started_is_running()
    {
        // The reference tests every frame and leans on the action's own case to stop it
        // happening twice; what stops it in the meantime is that an action is playing.
        // Here the case never stops applying, so anything that fires it every frame counts
        // to twenty rather than to one.
        (SceneUpdate update, GameState state) = TriggerWorld(
            """GET_CLOSE, WALK, ALL, script={wait SetTimerSeconds(0.5); IncNounVerbCount("GET_CLOSE", "WALK");}""");

        for (int i = 0; i < 20; i++)
        {
            update.Advance(1.0 / 60);
        }

        Assert.Equal(1, state.GetNounVerbCount("GET_CLOSE", "WALK"));

        // And again once it is over, because the player is still standing there and the
        // rule still applies. That is the original's behaviour and it is what the rules
        // written with a count in them are guarding against.
        for (int i = 0; i < 20; i++)
        {
            update.Advance(1.0 / 60);
        }

        Assert.Equal(2, state.GetNounVerbCount("GET_CLOSE", "WALK"));
    }

    [Fact]
    public void A_walk_that_would_cross_a_trigger_stops_at_its_edge()
    {
        // Walker::FindEarliestPathNodeInsideActiveTriggerRegion. Its own comment names the
        // case: in the lobby on the first morning the way to the front door goes through
        // Jean's rectangle, and walking over it starts a conversation with somebody who is
        // by then at the door.
        var state = new GameState();
        var api = new Gk3SheepApi(state);
        var resolver = new ActionResolver(api);

        resolver.Add(NvcFile.Parse(
            """FAR_HALF, WALK, ALL, script={SetFlag("crossed")}""",
            "test.nvc",
            new DiagnosticBag()));

        var sink = new Watcher();
        sink.Moves[0] = Matrix4x4.CreateRotationY(Walker.Rotation(0f));

        // Eight texels square over eight hundred units centred on the origin, every one of
        // them open, so the route is a straight line of corners rather than a way round
        // anything. Each texel is a hundred units across and their middles fall on 50, 150,
        // 250 and 350 either side of nothing.
        var boundary = new WalkBoundary(
            new IndexedImage(8, 8, new byte[8 * 8]),
            new Vector2(800, 800),
            new Vector2(400, 400));

        var update = new SceneUpdate(
            Scene(
                """
                [ROOM_CAMERAS]
                NEAR, angle={0, 0}, pos={0, 60, 0}, Default

                [TRIGGERS]
                noun=FAR_HALF, rect={-400, 100, 400, 400}
                """,
                boundary),
            api,
            new Glances(),
            sink,
            resolver,
            new ActionRunner(api));

        update.Place("GABRIEL", new Vector3(0, 0, -300), 0f);
        update.Walk("GABRIEL", new Vector3(0, 0, 350), mayRun: true);

        Vector3 stops = Assert.IsType<Vector3>(update.Heading("GABRIEL"));

        // Not the corner it was sent to, and not the corner of the route that happens to
        // be inside — the point on the edge where the walk crosses it.
        Assert.Equal(100f, stops.Z, 1);
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
    public void A_timer_that_comes_due_inside_an_action_waits_for_it()
    {
        // GameTimers::Update fires one only if(secondsRemaining <= 0 && !IsActionPlaying()).
        // Without the second half a timer goes off over the top of whatever is playing.
        var state = new GameState();
        var api = new Gk3SheepApi(state);
        var resolver = new ActionResolver(api);

        resolver.Add(NvcFile.Parse(
            """
            DOOR,  OPEN, ALL, script={wait SetTimerSeconds(1.0); SetFlag("opened");}
            PHONE, RING, ALL, script={SetFlag("rang");}
            """,
            "test.nvc",
            new DiagnosticBag()));

        var update = new SceneUpdate(
            Scene(), api, new Glances(), new Watcher(), resolver, new ActionRunner(api));

        new ActionRunner(api).Run(resolver.Find("DOOR", "OPEN")!);
        state.Timers.Set("PHONE", "RING", 0.25);

        // Halfway through the door, and well past when the phone ran out.
        update.Advance(0.5);

        Assert.False(state.GetFlag("rang"), "the phone rang over the top of the door");
        Assert.Equal(1, state.Timers.Count);

        // And it is still there to ring once the door is over, rather than having been
        // dropped on the frame it could not be performed.
        update.Advance(1.0);

        Assert.True(state.GetFlag("rang"));
        Assert.Equal(0, state.Timers.Count);
    }

    [Fact]
    public void A_timer_waits_for_an_action_that_is_waiting_on_a_script()
    {
        // CS3's attic, which is how this was reported. Grace hides in the wardrobe; the
        // action's own script is `wait CallSheep(...)`, and the function it calls spends
        // several seconds on animations before raising the count that makes the pending
        // Montreaux timer's rule stop applying. Fire the timer during those seconds and its
        // rule still holds, so Montreaux's arrival plays once from the timer and again from
        // the wardrobe — and every line after it is heard twice.
        //
        // The action's length is not a number of seconds here: it is another script. So
        // what has to count as the story being busy is the script still parked in the
        // scheduler, which is the half of it a click never armed.
        var state = new GameState();
        var api = new Gk3SheepApi(state);
        var host = new ScriptHost(api);
        var scheduler = new SheepScheduler(host.Machine);

        host.Scheduler = scheduler;

        var resolver = new ActionResolver(api);

        resolver.Add(NvcFile.Parse(
            """
            WARDROBE,  HIDE,      ALL,               script={wait CallSheep("CS3212P", "HideWardrobe$");}
            MONTREAUX, TIMER_EXP, NOT_YET_ARRIVED,   script={IncNounVerbCount("MONTREAUX", "TIMER_EXP"); SetFlag("arrived");}

            [LOGIC]
            NOT_YET_ARRIVED = {GetNounVerbCount("MONTREAUX", "TIMER_EXP") == 0}
            """,
            "test.nvc",
            new DiagnosticBag()));

        // The hiding, in the shape the real one has: time spent before the counts are
        // raised, and the arrival run by the script itself at the end of it.
        host.Add(SheepCompiler.Compile(
            """
            code
            {
                HideWardrobe$()
                {
                    wait SetTimerSeconds(2.0);
                    IncNounVerbCount("MONTREAUX", "TIMER_EXP");
                    SetFlag("arrived");
                }
            }
            """,
            "CS3212P.SHP",
            Signatures(
                ("SetTimerSeconds", SheepSignatures.Void, [SheepSignatures.Float]),
                ("IncNounVerbCount",
                    SheepSignatures.Void, [SheepSignatures.String, SheepSignatures.String]),
                ("SetFlag", SheepSignatures.Void, [SheepSignatures.String]))));

        var update = new SceneUpdate(
            Scene(),
            api,
            new Glances(),
            new Watcher(),
            resolver,
            new ActionRunner(api),
            scheduler);

        // What the room does for the clock-bearing hooks an action needs. Only the one
        // this is about: the rest of the scene wiring wants a standing scene.
        api.Starts = update.Starting;
        api.DefersUntil = update.Until;

        // Montreaux is half a second off and the hiding takes two, so the timer runs out
        // squarely inside the action — which is the whole of the case.
        state.Timers.Set("MONTREAUX", "TIMER_EXP", 0.5);
        new ActionRunner(api).Run(resolver.Find("WARDROBE", "HIDE")!);

        Assert.True(update.Occupied, "hiding is the story being busy");

        for (int frame = 0; frame < 60 * 4; frame++)
        {
            update.Advance(1.0 / 60);
        }

        // Once, by the script, and never by the timer: by the time the room was free again
        // the count was up and the timer's rule no longer applied.
        Assert.Equal(1, state.GetNounVerbCount("MONTREAUX", "TIMER_EXP"));
        Assert.Equal(0, state.Timers.Count);
        Assert.True(state.GetFlag("arrived"));
    }

    private static SheepSignatures Signatures(
        params (string Name, sbyte Returns, sbyte[] Args)[] functions)
    {
        var catalogue = new SheepSignatures();

        foreach ((string name, sbyte returns, sbyte[] args) in functions)
        {
            catalogue.Add(new SheepImport(name, returns, args));
        }

        return catalogue;
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

    [Fact]
    public void Something_held_back_for_a_walk_happens_when_the_walk_would_be_over()
    {
        // What makes an action's approach mean anything: the script runs when the player
        // has arrived, not while they are still on their way.
        (SceneUpdate update, _, _, _) = World();

        int done = 0;

        Assert.True(update.After(2.0, () => done++));
        Assert.Equal(1, update.Later);

        update.Advance(1.0);
        Assert.Equal(0, done);

        update.Advance(1.0);
        Assert.Equal(1, done);
        Assert.Equal(0, update.Later);

        // And only once, however much more time passes.
        update.Advance(10.0);
        Assert.Equal(1, done);
    }

    [Fact]
    public void A_wait_of_no_length_is_refused_rather_than_queued()
    {
        (SceneUpdate update, _, _, _) = World();

        Assert.False(update.After(0, () => { }));
        Assert.Equal(0, update.Later);
    }

    [Fact]
    public void Leaving_a_room_forgets_what_was_waiting_to_happen_in_it()
    {
        // An action script belongs to the room that offered it. Letting one run into the
        // next room is how a door opens twice.
        (SceneUpdate update, _, _, _) = World();

        int done = 0;

        update.After(1.0, () => done++);
        update.Cancel();
        update.Advance(5.0);

        Assert.Equal(0, done);
    }

    [Fact]
    public void A_model_can_be_taken_out_of_the_picture_and_put_back()
    {
        // GK3 stages a moment by leaving its pieces in the room, hidden, and showing them
        // when they are wanted. Both halves matter: the geometry stops drawing it, and the
        // model remembers, so a picker does not offer a noun for something invisible.
        (SceneUpdate update, _, Watcher sink, _) = World();

        PlacedModel gab = Assert.IsType<PlacedModel>(update.ModelNamed("GABRIEL"));

        Assert.Same(gab, update.ModelNamed("gab"));
        Assert.Null(update.ModelNamed("wmo"));

        update.Show(gab, visible: false);

        Assert.False(gab.Visible);
        Assert.False(sink.Visible[0]);

        update.Show(gab, visible: true);

        Assert.True(gab.Visible);
        Assert.True(sink.Visible[0]);
    }

    [Fact]
    public void Getting_unstuck_lets_go_of_everything_that_was_holding_the_room()
    {
        // The menu's Get Unstuck row. Occupied is made of four things and Directing turns
        // any of them into a camera the player does not have and clicks that do not reach
        // the floor — so a room that wedges leaves the player with no way to say so, every
        // way of saying so being a click.
        var state = new GameState { ForcedCameraCuts = true, Inspecting = "CAT" };
        var api = new Gk3SheepApi(state) { ActionSeconds = 90 };
        var update = new SceneUpdate(Scene(), api, new Glances(), new Watcher());

        int held = 0;
        update.After(90, () => held++);

        Assert.True(update.Occupied);
        Assert.True(update.Directing);

        Assert.NotEmpty(update.Unstick());

        Assert.False(update.Occupied);
        Assert.False(update.Directing);
        Assert.Equal(0, update.Later);
        Assert.Equal(0, api.ActionSeconds);
        Assert.False(state.ForcedCameraCuts);
        Assert.Equal(string.Empty, state.Inspecting);

        // And what was waiting on the walk is abandoned rather than hurried along. Running
        // it would perform the action the player has just said they are stuck in.
        update.Advance(120);
        Assert.Equal(0, held);
    }

    [Fact]
    public void And_says_so_plainly_when_nothing_was_holding_it()
    {
        // Nothing to report is a real answer, and the caller says as much to the player:
        // somebody who reached for this and was told nothing was wrong has learned
        // something about where to look next.
        (SceneUpdate update, _, _, _) = World();

        Assert.Empty(update.Unstick());
    }
}
