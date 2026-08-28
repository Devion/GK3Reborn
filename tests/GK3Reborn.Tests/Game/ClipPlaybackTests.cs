using System.Numerics;
using System.Text;
using GK3Reborn.Content;
using GK3Reborn.Formats;
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
/// Tests for playing a vertex animation's rigid half.
/// </summary>
/// <remarks>
/// Three names have to line up and none of them is the one the script said: a script names
/// an <c>.ANM</c>, whose <c>[ACTIONS]</c> names an <c>.ACT</c>, whose header names the model
/// it moves. Getting any of them wrong makes nothing happen, which looks exactly like
/// nothing having been asked for — so these check the chain rather than the arithmetic.
/// </remarks>
public sealed class ClipPlaybackTests
{
    /// <summary>
    /// Records what the renderer was told to do, and measures nothing.
    /// </summary>
    /// <remarks>
    /// Wrapped around the headless sink rather than reimplemented, so that adding a member
    /// to the contract does not silently give this one a different idea of what a scene is.
    /// </remarks>
    private sealed class Sink : ISceneSink
    {
        private readonly HeadlessSceneSink _inner = new();

        public Vector3 Minimum => _inner.Minimum;

        public Vector3 Maximum => _inner.Maximum;

        public int TextureCount => _inner.TextureCount;

        public int TriangleCount => _inner.TriangleCount;

        public Dictionary<(int Placement, int Mesh), Matrix4x4> Poses { get; } = [];

        public void AddTexture(string name, DecodedImage image) => _inner.AddTexture(name, image);

        public void AddTexture(string name, CompressedImage image) =>
            _inner.AddTexture(name, image);

        public void AddNormalMap(string name, CompressedImage image) =>
            _inner.AddNormalMap(name, image);

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
            IReadOnlyDictionary<int, Matrix4x4>? meshTurns = null) =>
            _inner.Add(model, transform, meshTurns);

        public void TurnMesh(ModelPlacement placement, int mesh, Matrix4x4 turn) =>
            _inner.TurnMesh(placement, mesh, turn);

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

        public void SetVisible(ModelPlacement placement, bool visible) =>
            Visible[placement.Id] = visible;

        public void PoseMesh(ModelPlacement placement, int mesh, Matrix4x4 meshToLocal) =>
            Poses[(placement.Id, mesh)] = meshToLocal;

        /// <summary>What each submesh was last reshaped to.</summary>
        public Dictionary<(int Placement, int Mesh, int Submesh), IReadOnlyList<Vector3>> Shapes
        { get; } = [];

        public void ShapeMesh(
            ModelPlacement placement, int mesh, int submesh, IReadOnlyList<Vector3> positions) =>
            Shapes[(placement.Id, mesh, submesh)] = positions;

        public void MoveModel(ModelPlacement placement, Matrix4x4 transform) =>
            _inner.MoveModel(placement, transform);

        public Matrix4x4 TransformOf(ModelPlacement placement) =>
            _inner.TransformOf(placement);

        public void KeepRelief(IReadOnlySet<string> textures) => _inner.KeepRelief(textures);

        public void AddScene(
            BspFile scene,
            MulFile? lightmaps = null,
            IReadOnlySet<string>? hiddenObjects = null,
            string? floorObject = null,
            IReadOnlySet<int>? hiddenSurfaces = null) =>
            _inner.AddScene(scene, lightmaps, hiddenObjects, floorObject, hiddenSurfaces);
    }

    /// <summary>A one-mesh clip whose mesh moves along X, a unit a frame.</summary>
    /// <param name="model">The model its header names.</param>
    /// <param name="frames">How many frames.</param>
    /// <param name="deform">Whether to also give it a one-vertex shape that climbs in Y.</param>
    private static byte[] Clip(string model, int frames, bool deform = false, float from = Away)
    {
        List<byte> body = [];
        List<int> offsets = [];
        int header = 20 + 32 + (frames * 4);

        for (int frame = 0; frame < frames; frame++)
        {
            offsets.Add(header + body.Count);

            List<byte> block = [2];
            block.AddRange(BitConverter.GetBytes(48));

            // Starts a long way from the origin, as a real clip does — authored wherever
            // the animator built it — and then advances a unit a frame.
            foreach (float value in new float[]
                     { 1, 0, 0, 0, 1, 0, 0, 0, 1, from + frame, 0, 0 })
            {
                block.AddRange(BitConverter.GetBytes(value));
            }

            if (deform)
            {
                // An uncompressed shape every frame: one vertex, climbing in Y. Real clips
                // use deltas after frame 0; what is checked here is that the shape reaches
                // the renderer, not the compression, which ActFileTests covers.
                List<byte> shape = [.. BitConverter.GetBytes((ushort)0)];
                shape.AddRange(BitConverter.GetBytes((ushort)1));
                shape.AddRange(BitConverter.GetBytes(0f));
                shape.AddRange(BitConverter.GetBytes((float)frame));
                shape.AddRange(BitConverter.GetBytes(0f));

                block.Add(0);
                block.AddRange(BitConverter.GetBytes(shape.Count));
                block.AddRange(shape);
            }

            body.AddRange(BitConverter.GetBytes((ushort)0));
            body.AddRange(BitConverter.GetBytes(block.Count));
            body.AddRange(block);
        }

        List<byte> file = [.. "HTCA"u8];
        file.AddRange(BitConverter.GetBytes(258));
        file.AddRange(BitConverter.GetBytes(frames));
        file.AddRange(BitConverter.GetBytes(1));
        file.AddRange(BitConverter.GetBytes(body.Count));

        byte[] name = new byte[32];
        Encoding.ASCII.GetBytes(model).CopyTo(name, 0);
        file.AddRange(name);

        foreach (int offset in offsets)
        {
            file.AddRange(BitConverter.GetBytes(offset));
        }

        file.AddRange(body);
        return [.. file];
    }

    /// <summary>A model of one mesh, which is all a rigid clip needs to move.</summary>
    /// <summary>How far from the model's rest position the synthetic clip is authored.</summary>
    private const float Away = 500f;

    private static ModFile Model() => ModFile.FromMeshes(
        "door",
        [
            new ModMesh
            {
                MeshToLocal = Matrix4x4.Identity,
                BoundsMin = Vector3.Zero,
                BoundsMax = Vector3.One,
                Submeshes =
                [
                    new ModSubmesh
                    {
                        TextureName = "DOOR",
                        Color = (255, 255, 255),
                        Positions = [Vector3.Zero],
                        Normals = [Vector3.UnitY],
                        TexCoords = [Vector2.Zero],
                        Indices = [0, 0, 0],
                    },
                ],
            },
        ]);

    /// <summary>A world with one thing in it and one animation to play on it.</summary>
    /// <remarks>
    /// An actor by default, because the correction — the interesting part — is theirs. A
    /// prop is placed by the identity and its clips are already in the room's coordinates,
    /// so there is nothing to correct; see <c>SceneUpdate.Playing.Correction</c>.
    /// </remarks>
    private static (SceneUpdate Update, Sink Sink) World(
        string animation,
        string clipName,
        string model,
        string? standing = null,
        bool clipExists = true,
        bool deform = false,
        PlacedModelKind kind = PlacedModelKind.Actor,
        Matrix4x4? placedAt = null,
        bool absolute = false)
    {
        var sink = new Sink();

        // As the loader does. The sink is what knows where a model stands, because it is
        // the sink that will multiply a posed mesh by it, and a fixture that declares a
        // model placed without telling the sink is a fixture that cannot see the
        // difference this makes.
        sink.Add(Model(), placedAt);

        var scene = new LoadedScene(
            "TEST",
            new SceneDefinition(SceneInitFile.Parse("[ROOM_CAMERAS]\nA, angle={0,0}, pos={0,0,0}, Default", "T.SIF")),
            Asset: null,
            Lightmaps: null,
            ModelsPlaced: 1,
            Placed:
            [
                new PlacedModel(
                    standing ?? model, "DOOR", null, Model(), placedAt ?? Matrix4x4.Identity,
                    kind, new ModelPlacement(0)),
            ]);

        var update = new SceneUpdate(scene, new Gk3SheepApi(new GameState()), new Glances(), sink)
        {
            // Four numbers is fewer than a placement needs, so the clip is relative;
            // eight of them, even all zero, is an absolute clip authored in the room's own
            // coordinates. That distinction is the whole of what makes a clip absolute.
            Animations = new AnimationLibrary(n =>
                n.Equals($"{animation}.ANM", StringComparison.OrdinalIgnoreCase)
                    ? $"[HEADER]\n31\n\n[ACTIONS]\n1\n0,{clipName}" +
                      (absolute ? ",0,0,0,0,0,0,0,0" : ",0,0,0,0") + "\n"
                    : null),

            // Shapes kept, as the launcher keeps them: without that a clip's vertex poses
            // are decoded and thrown away, and a character plays as sliding mesh groups.
            Clips = new ClipLibrary(n =>
                clipExists && n.Equals($"{clipName}.ACT", StringComparison.OrdinalIgnoreCase)
                    ? Clip(model, 31, deform)
                    : null)
            { KeepVertices = true },
        };

        return (update, sink);
    }

    /// <summary>How far away the idle's clip is authored, so the two can be told apart.</summary>
    private const float Elsewhere = 5000f;

    /// <summary>
    /// A world where the model also runs a behaviour script of its own.
    /// </summary>
    /// <remarks>
    /// A prop rather than an actor, because a prop's clips play exactly as authored — see
    /// <c>SceneUpdate.Playing.Correction</c> — so where its mesh ends up says which of the
    /// two clips is driving it, which is the whole question here.
    /// </remarks>
    private static (SceneUpdate Update, Sink Sink) Idling()
    {
        var sink = new Sink();
        sink.Add(Model());

        var scene = new LoadedScene(
            "TEST",
            new SceneDefinition(SceneInitFile.Parse(
                "[ROOM_CAMERAS]\nA, angle={0,0}, pos={0,0,0}, Default", "T.SIF")),
            Asset: null,
            Lightmaps: null,
            ModelsPlaced: 1,
            Placed:
            [
                new PlacedModel(
                    "door", "DOOR", null, Model(), Matrix4x4.Identity,
                    PlacedModelKind.Prop, new ModelPlacement(0))
                {
                    Idle = GK3Reborn.Formats.Animation.GasFile.Parse(Encoding.Latin1.GetBytes("ANIM Fidget\nloop\n")),
                },
            ]);

        var update = new SceneUpdate(scene, new Gk3SheepApi(new GameState()), new Glances(), sink)
        {
            Animations = new AnimationLibrary(n => n.ToUpperInvariant() switch
            {
                "FIDGET.ANM" => "[HEADER]\n31\n\n[ACTIONS]\n1\n0,door_Fidget,0,0,0,0,0,0,0,0\n",
                "WRDBOPEN.ANM" => "[HEADER]\n31\n\n[ACTIONS]\n1\n0,door_WrdbOpen,0,0,0,0,0,0,0,0\n",
                _ => null,
            }),

            Clips = new ClipLibrary(n => n.ToUpperInvariant() switch
            {
                "DOOR_FIDGET.ACT" => Clip("door", 31, from: Elsewhere),
                "DOOR_WRDBOPEN.ACT" => Clip("door", 31),
                _ => null,
            })
            { KeepVertices = true },
        };

        update.StartScenery();
        return (update, sink);
    }

    /// <summary>Where the model's one mesh has been put, along the axis the clips move on.</summary>
    private static float Along(Sink sink) => sink.Poses[(0, 0)].Translation.X;

    [Fact]
    public void The_story_takes_a_model_off_its_own_script_and_gives_it_back()
    {
        // The dining room, and the fault it was found through. Mosely reads his newspaper
        // through an idle script while the coffee scene animates him and the paper; both
        // posed the same mesh groups every frame, so the paper hung in mid-air beside him
        // and Gabriel flickered between the scene and his own breathing.
        (SceneUpdate update, Sink sink) = Idling();

        update.Advance(0.1);

        Assert.Equal(1, update.Animating);
        Assert.True(Along(sink) > Elsewhere, "the idle should be driving it to start with");

        // The story asks for something else on the same model.
        Assert.True(update.Play("WrdbOpen") > 0);
        Assert.Equal(1, update.Animating);

        // For as long as it runs, nothing else touches the model: not a second clip, and
        // not the idle, which is held rather than stopped.
        for (int frame = 0; frame < 30; frame++)
        {
            update.Advance(1.0 / 60);

            Assert.Equal(1, update.Animating);
            Assert.True(Along(sink) < Elsewhere, "the story should be driving it throughout");
        }

        // 31 frames at fifteen a second, and then the model is its own again.
        update.Advance(31 / 15.0);
        update.Advance(1.0 / 60);

        Assert.Equal(1, update.Animating);
        Assert.True(Along(sink) > Elsewhere, "the idle should carry on where it left off");
    }

    [Fact]
    public void A_story_animation_stops_a_characters_idle_rather_than_pausing_it()
    {
        // The rule above is a prop's. A character's is the other one: GKActor::StartAnimation
        // calls StopFidget on the way in to anything that did not come from the behaviour
        // script, and nothing on the way out turns it back on — the script does, by hand,
        // once it has finished with them. PourCoffee$ ends with StartIdleFidget("Gabriel")
        // for exactly that reason.
        //
        // Pausing instead leaves a gap between every pair of clips in a sequence and the
        // idle fires into it. A breath is a clip that gives back the ground it covered, so
        // Gabriel walked to the kitchen for coffee and snapped back to the dining table
        // between clips, and Estelle was dragged back against Lady Howard after each of
        // hers in the museum.
        (SceneUpdate update, Sink sink) = Fidgeting();

        update.Advance(0.1);
        Assert.True(Along(sink) > Elsewhere, "the idle should be driving them to start with");

        Assert.True(update.Play("WrdbOpen") > 0);

        // Long enough for the story's clip to be over more than twice, and for the idle to
        // have come round again several times.
        for (int frame = 0; frame < 600; frame++)
        {
            update.Advance(1.0 / 60);
        }

        Assert.True(
            Along(sink) < Elsewhere,
            "nothing should have moved them since the story's clip ended");

        // Until the script says so.
        update.StartFidget("gab", FidgetKind.Idle);
        update.Advance(0.2);

        Assert.True(Along(sink) > Elsewhere, "the idle starts again when it is asked to");
    }

    /// <summary>A character running an idle, over the same two clips as <see cref="Idling"/>.</summary>
    private static (SceneUpdate Update, Sink Sink) Fidgeting()
    {
        var sink = new Sink();
        sink.Add(Model());

        var scene = new LoadedScene(
            "TEST",
            new SceneDefinition(SceneInitFile.Parse(
                "[ROOM_CAMERAS]\nA, angle={0,0}, pos={0,0,0}, Default", "T.SIF")),
            Asset: null,
            Lightmaps: null,
            ModelsPlaced: 1,
            Placed:
            [
                new PlacedModel(
                    "gab", "GABRIEL", null, Model(), Matrix4x4.Identity,
                    PlacedModelKind.Actor, new ModelPlacement(0)),
            ]);

        var update = new SceneUpdate(scene, new Gk3SheepApi(new GameState()), new Glances(), sink)
        {
            Animations = new AnimationLibrary(n => n.ToUpperInvariant() switch
            {
                "FIDGET.ANM" => "[HEADER]\n31\n\n[ACTIONS]\n1\n0,gab_Fidget,0,0,0,0,0,0,0,0\n",
                "WRDBOPEN.ANM" => "[HEADER]\n31\n\n[ACTIONS]\n1\n0,gab_WrdbOpen,0,0,0,0,0,0,0,0\n",
                _ => null,
            }),

            Clips = new ClipLibrary(n => n.ToUpperInvariant() switch
            {
                "GAB_FIDGET.ACT" => Clip("gab", 31, from: Elsewhere),
                "GAB_WRDBOPEN.ACT" => Clip("gab", 31),
                _ => null,
            })
            { KeepVertices = true },
        };

        update.SetBehaviour(
            "GABRIEL",
            FidgetKind.Idle,
            GK3Reborn.Formats.Animation.GasFile.Parse(Encoding.Latin1.GetBytes("ANIM Fidget\nloop\n")));

        return (update, sink);
    }

    [Fact]
    public void A_second_clip_on_one_model_replaces_the_first()
    {
        // GK3 gives a model one animator, and VertexAnimator::Start stops whatever it was
        // playing before it starts anything. Two clips posing one model is two answers to
        // where its mesh groups are, settled by whichever happened to be added last.
        (SceneUpdate update, _) = World("WrdbOpen", "door_WrdbOpen", "door");

        update.Play("WrdbOpen");
        update.Play("WrdbOpen");
        update.Play("WrdbOpen");

        Assert.Equal(1, update.Animating);
    }

    [Fact]
    public void An_animation_finds_its_clip_which_finds_its_model()
    {
        (SceneUpdate update, Sink sink) = World("WrdbOpen", "door_WrdbOpen", "door");

        // 31 frames at fifteen a second.
        Assert.Equal(31 / 15.0, update.Play("WrdbOpen"), 3);
        Assert.Equal(1, update.Animating);

        update.Advance(0.5);

        // Half a second in: frame seven and a half, and the mesh seven and a half along
        // from where it began. A clip authored 500 units away plays here, not there — and
        // the half is the point: fifteen recorded poses a second are mixed rather than
        // shown four times each, so half a second lands between two of them.
        Assert.Equal(7.5f, sink.Poses[(0, 0)].Translation.X, 3);
    }

    [Fact]
    public void A_clip_that_deforms_reshapes_the_submesh_as_well_as_posing_the_mesh()
    {
        // 3,085 of the corpus's 3,086 character clips deform. Without this a character is
        // mesh groups sliding about rather than anybody moving.
        (SceneUpdate update, Sink sink) = World("Breathe", "door_Breathe", "door", deform: true);

        update.Play("Breathe");
        update.Advance(0.5);

        // Between frames seven and eight: that far along from where it started, not from
        // the origin, and its one vertex that far up. The clip is authored 500 away and
        // the correction takes that out. Shapes are mixed between recorded poses for the
        // same reason transforms are.
        Assert.Equal(7.5f, sink.Poses[(0, 0)].Translation.X, 3);
        Assert.Equal(7.5f, Assert.Single(sink.Shapes[(0, 0, 0)]).Y, 3);
    }

    [Fact]
    public void A_rigid_clip_reshapes_nothing()
    {
        (SceneUpdate update, Sink sink) = World("WrdbOpen", "door_WrdbOpen", "door");

        update.Play("WrdbOpen");
        update.Advance(0.5);

        Assert.NotEmpty(sink.Poses);
        Assert.Empty(sink.Shapes);
    }

    [Fact]
    public void A_clip_authored_elsewhere_plays_where_the_model_is()
    {
        // The reason a walk clip made Gabriel disappear: its mesh transforms are wherever
        // the animator built them, which for a walk is halfway across another room.
        (SceneUpdate update, Sink sink) = World("Walk", "door_Walk", "door");

        update.Play("Walk");
        update.Advance(0.001);

        // A thousandth of a second is a sixty-sixth of a recorded pose, so the mesh has
        // moved a sixty-sixth of the unit the clip advances each one — not five hundred.
        Assert.Equal(0f, sink.Poses[(0, 0)].Translation.X, 1);
    }

    [Fact]
    public void The_correction_is_taken_once_so_the_clip_still_moves()
    {
        // Recomputing it per frame would cancel exactly the movement it is meant to keep.
        (SceneUpdate update, Sink sink) = World("Walk", "door_Walk", "door");

        update.Play("Walk");
        update.Advance(0.001);
        float first = sink.Poses[(0, 0)].Translation.X;

        update.Advance(0.5);

        Assert.True(
            sink.Poses[(0, 0)].Translation.X > first + 5f,
            "the clip's own movement was corrected away");
    }

    [Fact]
    public void A_clip_leaves_the_model_in_the_pose_it_finished_in()
    {
        // The original reverts an actor's *position* after a non-move animation, not the
        // pose. That is why an opened wardrobe stays open. Reverting the pose as well —
        // which reads as the more careful thing to do — shuts the door again.
        (SceneUpdate update, Sink sink) = World("Walk", "door_Walk", "door");

        update.Play("Walk");
        update.Advance(3.0);

        Assert.Equal(0, update.Animating);
        Assert.NotEqual(Matrix4x4.Identity, sink.Poses[(0, 0)]);
    }

    [Fact]
    public void A_frame_long_enough_to_run_past_the_end_still_poses_the_end()
    {
        // Otherwise a slow frame leaves the model wherever the previous frame put it, so a
        // door that was opening stops half open.
        (SceneUpdate update, Sink sink) = World("Walk", "door_Walk", "door");

        update.Play("Walk");
        update.Advance(3.0);

        // Frame thirty of thirty-one, thirty along from where it started.
        Assert.Equal(30f, sink.Poses[(0, 0)].Translation.X, 3);
    }

    [Fact]
    public void A_clip_that_ends_stops_playing()
    {
        (SceneUpdate update, _) = World("WrdbOpen", "door_WrdbOpen", "door");

        update.Play("WrdbOpen");

        Assert.Contains("finished", string.Join(" ", update.Advance(3.0)), StringComparison.Ordinal);
        Assert.Equal(0, update.Animating);
    }

    [Fact]
    public void A_looping_clip_starts_again_rather_than_stopping()
    {
        (SceneUpdate update, _) = World("WrdbOpen", "door_WrdbOpen", "door");

        update.Play("WrdbOpen", repeat: true);
        update.Advance(3.0);

        Assert.Equal(1, update.Animating);
    }

    [Fact]
    public void An_animation_that_is_not_there_says_so_rather_than_doing_nothing()
    {
        // The whole chain fails by nothing happening, which is indistinguishable from
        // nothing having been asked for. Every step that can fail reports.
        (SceneUpdate update, _) = World("WrdbOpen", "door_WrdbOpen", "door");

        Assert.Equal(0, update.Play("NoSuchThing"));
        Assert.Contains(update.Diagnostics.Items, d => d.Code == "GK3R3312");
    }

    [Fact]
    public void An_animation_whose_clip_is_missing_says_so()
    {
        (SceneUpdate update, _) = World("WrdbOpen", "door_WrdbOpen", "door", clipExists: false);

        Assert.Equal(0, update.Play("WrdbOpen"));
        Assert.Contains(update.Diagnostics.Items, d => d.Code == "GK3R3314");
    }

    [Fact]
    public void A_clip_for_a_model_that_is_not_in_the_room_says_so()
    {
        // Common and usually harmless — clips are shared between rooms — which is why it is
        // reported at information rather than as a problem.
        (SceneUpdate update, _) = World("WrdbOpen", "door_WrdbOpen", "door", standing: "somebody_else");

        Assert.Equal(0, update.Play("WrdbOpen"));
        Assert.Contains(update.Diagnostics.Items, d => d.Code == "GK3R3311");
    }

    [Fact]
    public void Stopping_a_model_stops_what_it_was_doing()
    {
        (SceneUpdate update, _) = World("WrdbOpen", "door_WrdbOpen", "door");

        update.Play("WrdbOpen");
        update.StopAnimating("door");

        Assert.Equal(0, update.Animating);
    }

    [Fact]
    public void A_prop_plays_its_clip_exactly_where_it_was_authored()
    {
        // A prop is placed by the identity, so the room's coordinates are the model's
        // coordinates and a clip written for that room is already in the right place.
        // Correcting it back to where the model rests is what left the moped that rides
        // past RC1 riding past the world origin instead — and it would put every book back
        // on its shelf as it was picked up.
        (SceneUpdate update, Sink sink) = World(
            "Ride", "door_Ride", "door", kind: PlacedModelKind.Prop);

        update.Play("Ride");
        update.Advance(0.5);

        // A tolerance rather than a number of decimal places, for the reason given in full
        // by An_absolute_clip_lands_in_the_room_wherever_its_model_is_standing: the clip is
        // authored out at Away, and three decimal places of a number in the hundreds leaves
        // about eight ulps of a float to absorb the difference between how two
        // architectures contract a multiply-add. That one straddled a rounding boundary on
        // arm64 and this one is the only other assertion in the file with the same little
        // headroom, so it gets the same hundredth of a unit.
        Assert.Equal(Away + 7.5f, sink.Poses[(0, 0)].Translation.X, 0.01f);
    }

    [Fact]
    public void A_looping_clip_keeps_the_part_of_a_frame_it_overran_by()
    {
        // Resetting to exactly zero every time round drops up to a sixtieth of a second a
        // loop, and on a loop as short as a ceiling fan's that is a visible hitch every few
        // seconds.
        (SceneUpdate update, Sink sink) = World("Spin", "door_Spin", "door");

        update.Play("Spin", repeat: true);

        // 31 frames is 2.0666s. A third of a second past that is frame five, not frame nought.
        update.Advance((31 / 15.0) + (5 / 15.0));

        Assert.Equal(5f, sink.Poses[(0, 0)].Translation.X, 3);
    }

    [Fact]
    public void An_absolute_clip_lands_in_the_room_wherever_its_model_is_standing()
    {
        // A clip's mesh transforms are posed relative to the model, and the model's own
        // placement is applied on top. A prop stands at the identity, so an absolute clip
        // authored in the room's coordinates lands where it was authored and nothing had
        // to be done about it — which is why nothing was.
        //
        // An actor stands wherever the scene put them or wherever they last walked to. The
        // placement has to come back off, or the clip is moved by the whole of it: Mosely
        // read his newspaper out beyond the dining room while the paper, being a prop,
        // stayed on the table.
        Matrix4x4 far =
            Matrix4x4.CreateRotationY(1.1f) * Matrix4x4.CreateTranslation(400, 0, -250);

        (SceneUpdate atOrigin, Sink first) = World(
            "Sit", "door_Sit", "door", absolute: true);

        (SceneUpdate acrossTheRoom, Sink second) = World(
            "Sit", "door_Sit", "door", absolute: true, placedAt: far);

        atOrigin.Play("Sit");
        atOrigin.Advance(0.001);

        acrossTheRoom.Play("Sit");
        acrossTheRoom.Advance(0.001);

        // What is posed is in the model's space, so the model's placement is what turns it
        // into a place in the room. Both have to come out at the same place in the room.
        Matrix4x4 here = first.Poses[(0, 0)];
        Matrix4x4 there = second.Poses[(0, 0)] * far;

        // Compared as a distance with a tolerance rather than to a number of decimal
        // places. The room's coordinates run to the hundreds, where a float carries about
        // 6e-5 of precision, and arm64 contracts the multiply-add in a matrix product where
        // x64 does not. The two answers differed by 3e-5 - correct on both - but straddled a
        // rounding boundary, so 500.015015 rounded to 500.02 and 500.014984 to 500.01 and
        // the same right answer passed on one runner and failed on the other. A hundredth of
        // a unit is far below anything the eye can see and far above that noise.
        float apart = Vector3.Distance(here.Translation, there.Translation);

        Assert.True(
            apart < 0.01f,
            $"{here.Translation} and {there.Translation} are {apart} apart, not the same place.");
    }

    [Fact]
    public void A_relative_clip_still_follows_its_model()
    {
        // The other half, and the reason this cannot simply stop correcting: a walk cycle
        // or a talking fidget carries no placement at all and means "play this wherever
        // the model is standing". 4,984 of the corpus's 9,417 action lines are these.
        Matrix4x4 far = Matrix4x4.CreateTranslation(400, 0, -250);

        (SceneUpdate atOrigin, Sink first) = World("Walk", "door_Walk", "door");
        (SceneUpdate acrossTheRoom, Sink second) = World(
            "Walk", "door_Walk", "door", placedAt: far);

        atOrigin.Play("Walk");
        atOrigin.Advance(0.001);

        acrossTheRoom.Play("Walk");
        acrossTheRoom.Advance(0.001);

        // Posed the same in the model's own space, so the model's placement carries it.
        Assert.Equal(
            first.Poses[(0, 0)].Translation.X, second.Poses[(0, 0)].Translation.X, 2);
    }
}
