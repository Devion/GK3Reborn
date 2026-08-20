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

        public bool HasTexture(string name) => false;

        public ModelPlacement Add(
            ModFile model,
            Matrix4x4? transform = null,
            IReadOnlyDictionary<int, Matrix4x4>? meshTurns = null) =>
            _inner.Add(model, transform, meshTurns);

        public void TurnMesh(ModelPlacement placement, int mesh, Matrix4x4 turn) =>
            _inner.TurnMesh(placement, mesh, turn);

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

        public void AddScene(
            BspFile scene,
            MulFile? lightmaps = null,
            IReadOnlySet<string>? hiddenObjects = null) =>
            _inner.AddScene(scene, lightmaps, hiddenObjects);
    }

    /// <summary>A one-mesh clip whose mesh moves along X, a unit a frame.</summary>
    /// <param name="model">The model its header names.</param>
    /// <param name="frames">How many frames.</param>
    /// <param name="deform">Whether to also give it a one-vertex shape that climbs in Y.</param>
    private static byte[] Clip(string model, int frames, bool deform = false)
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
                     { 1, 0, 0, 0, 1, 0, 0, 0, 1, Away + frame, 0, 0 })
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

    private static (SceneUpdate Update, Sink Sink) World(
        string animation,
        string clipName,
        string model,
        string? standing = null,
        bool clipExists = true,
        bool deform = false)
    {
        var sink = new Sink();

        var scene = new LoadedScene(
            "TEST",
            new SceneDefinition(SceneInitFile.Parse("[ROOM_CAMERAS]\nA, angle={0,0}, pos={0,0,0}, Default", "T.SIF")),
            Asset: null,
            Lightmaps: null,
            ModelsPlaced: 1,
            Placed:
            [
                new PlacedModel(
                    standing ?? model, "DOOR", null, Model(), Matrix4x4.Identity,
                    PlacedModelKind.Prop, new ModelPlacement(0)),
            ]);

        var update = new SceneUpdate(scene, new Gk3SheepApi(new GameState()), new Glances(), sink)
        {
            Animations = new AnimationLibrary(n =>
                n.Equals($"{animation}.ANM", StringComparison.OrdinalIgnoreCase)
                    ? $"[HEADER]\n31\n\n[ACTIONS]\n1\n0,{clipName},0,0,0,0\n"
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

    [Fact]
    public void An_animation_finds_its_clip_which_finds_its_model()
    {
        (SceneUpdate update, Sink sink) = World("WrdbOpen", "door_WrdbOpen", "door");

        // 31 frames at fifteen a second.
        Assert.Equal(31 / 15.0, update.Play("WrdbOpen"), 3);
        Assert.Equal(1, update.Animating);

        update.Advance(0.5);

        // Half a second in: frame seven, and the mesh seven along from where it began. A
        // clip authored 500 units away plays here, not there.
        Assert.Equal(7f, sink.Poses[(0, 0)].Translation.X, 3);
    }

    [Fact]
    public void A_clip_that_deforms_reshapes_the_submesh_as_well_as_posing_the_mesh()
    {
        // 3,085 of the corpus's 3,086 character clips deform. Without this a character is
        // mesh groups sliding about rather than anybody moving.
        (SceneUpdate update, Sink sink) = World("Breathe", "door_Breathe", "door", deform: true);

        update.Play("Breathe");
        update.Advance(0.5);

        // Frame seven: seven along from where it started, not seven from the origin, and
        // its one vertex seven up. The clip is authored 500 away and the correction takes
        // that out.
        Assert.Equal(7f, sink.Poses[(0, 0)].Translation.X, 3);
        Assert.Equal(7f, Assert.Single(sink.Shapes[(0, 0, 0)]).Y, 3);
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

        Assert.Equal(0f, sink.Poses[(0, 0)].Translation.X, 2);
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
}
