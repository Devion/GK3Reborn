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
/// Tests for a prop that is animated in somebody else's space rather than the room's.
/// </summary>
/// <remarks>
/// <para>
/// POU's second morning is what these are about. Six props stand in that scene with no
/// position — <c>model=abebinocs, type=prop, hidden</c> and five like it — because the
/// Abbé's binoculars, Buchelli's magnifier, his notepad and pencil, and Lady Howard's
/// camera and lens are meant to be in somebody's hands. Their clips were exported from the
/// same scene the character was, so they are authored around the character's own origin: a
/// median of 27.6 units away from it across the corpus's 314 held clips, and never more
/// than 94.3.
/// </para>
/// <para>
/// Play one in the room's own coordinates and it lands at the world origin, animating
/// correctly and in the wrong place — the shape the defect was reported in.
/// </para>
/// </remarks>
public sealed class HeldPropTests
{
    /// <summary>Records where each mesh was posed, and forwards the rest.</summary>
    private sealed class Sink : ISceneSink
    {
        private readonly HeadlessSceneSink _inner = new();

        public Action? Progress { get; set; }

        /// <summary>Where each mesh of each model was last posed to, in the model's space.</summary>
        public Dictionary<(int Placement, int Mesh), Matrix4x4> Poses { get; } = [];

        public Vector3 Minimum => _inner.Minimum;

        public Vector3 Maximum => _inner.Maximum;

        public int TextureCount => _inner.TextureCount;

        public int TriangleCount => _inner.TriangleCount;

        public void AddTexture(string name, DecodedImage image) => _inner.AddTexture(name, image);

        public void AddTexture(string name, CompressedImage image) => _inner.AddTexture(name, image);

        public void AddNormalMap(string name, DecodedImage image)
        {
        }

        public void AddNormalMap(string name, CompressedImage image) =>
            _inner.AddNormalMap(name, image);

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

        public bool HasTexture(string name) => false;

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

        public void Repaint(ModelPlacement placement, string texture, string? painted) =>
            _inner.Repaint(placement, texture, painted);

        public bool SetSceneObjectVisible(string objectName, bool visible) => true;

        public bool PaintSceneObject(string objectName, string? texture) => true;

        public bool SwapLightmaps(GK3Reborn.Formats.Lightmaps.MulFile lightmaps) => true;

        public void SetSelfLit(ModelPlacement placement, bool selfLit)
        {
        }

        public void SetVisible(ModelPlacement placement, bool visible) =>
            _inner.SetVisible(placement, visible);

        public void PoseMesh(ModelPlacement placement, int mesh, Matrix4x4 meshToLocal) =>
            Poses[(placement.Id, mesh)] = meshToLocal;

        public void ShapeMesh(
            ModelPlacement placement, int mesh, int submesh, IReadOnlyList<Vector3> positions)
        {
        }

        public void MoveModel(ModelPlacement placement, Matrix4x4 transform) =>
            _inner.MoveModel(placement, transform);

        public Matrix4x4 TransformOf(ModelPlacement placement) => _inner.TransformOf(placement);

        public void KeepRelief(IReadOnlySet<string> textures) => _inner.KeepRelief(textures);

        public void AddScene(
            BspFile scene,
            MulFile? lightmaps = null,
            IReadOnlySet<string>? hiddenObjects = null,
            string? floorObject = null,
            IReadOnlySet<int>? hiddenSurfaces = null,
            SceneOverlay? enhanced = null) =>
            _inner.AddScene(scene, lightmaps, hiddenObjects, floorObject, hiddenSurfaces);

        /// <summary>Where a model's one mesh has ended up, in the room.</summary>
        public Vector3 WorldPositionOf(int placement) =>
            (Poses[(placement, 0)] * TransformOf(new ModelPlacement(placement))).Translation;
    }

    /// <summary>Where the scene stands the Abbé, as POU207A's <c>ABBE1</c> does.</summary>
    private static readonly Vector3 Mark = new(333.9f, 224.9f, -470f);

    /// <summary>
    /// How far from the origin a held clip is authored — arm's length, as they all are.
    /// </summary>
    private const float Reach = 60f;

    /// <summary>A one-mesh clip whose mesh advances a unit a frame along X.</summary>
    private static byte[] Clip(string model, int frames, float from)
    {
        List<byte> body = [];
        List<int> offsets = [];
        int header = 20 + 32 + (frames * 4);

        for (int frame = 0; frame < frames; frame++)
        {
            offsets.Add(header + body.Count);

            List<byte> block = [2];
            block.AddRange(BitConverter.GetBytes(48));

            foreach (float value in new float[]
                     { 1, 0, 0, 0, 1, 0, 0, 0, 1, from + frame, 0, 0 })
            {
                block.AddRange(BitConverter.GetBytes(value));
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

    private static ModFile Model(string name) => ModFile.FromMeshes(
        name,
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
                        TextureName = name.ToUpperInvariant(),
                        Color = (255, 255, 255),
                        Positions = [Vector3.Zero],
                        Normals = [Vector3.UnitY],
                        TexCoords = [Vector2.Zero],
                        Indices = [0, 0, 0],
                    },
                ],
            },
        ]);

    /// <summary>Which model each placement in the fixture is.</summary>
    private const int Abbe = 0;
    private const int Binoculars = 1;

    /// <summary>
    /// The Abbé on his mark with his binoculars declared beside him, and the animations
    /// <c>AbeBinocIdle.gas</c> loops over.
    /// </summary>
    /// <param name="prefixed">
    /// Whether the animations are named after the man, as the game's are. Naming them
    /// something else is what a clip that is genuinely the room's own looks like.
    /// </param>
    /// <param name="animatesHim">
    /// Whether the animations carry a clip for the Abbé as well as for the binoculars. The
    /// game's do; it is what tells the subject of an animation from a name that happens to
    /// share three letters with a model.
    /// </param>
    private static (SceneUpdate Update, Sink Sink) Watching(
        bool prefixed = true, bool animatesHim = true)
    {
        var sink = new Sink();

        Matrix4x4 stood = Matrix4x4.CreateTranslation(Mark);

        sink.Add(Model("abe"), stood);
        sink.Add(Model("abebinocs"));

        var scene = new LoadedScene(
            "POU",
            new SceneDefinition(SceneInitFile.Parse(
                "[ROOM_CAMERAS]\nA, angle={0,0}, pos={0,0,0}, Default", "POU207A.SIF")),
            Asset: null,
            Lightmaps: null,
            ModelsPlaced: 2,
            Placed:
            [
                new PlacedModel(
                    "abe", "ABBE", null, Model("abe"), stood,
                    PlacedModelKind.Actor, new ModelPlacement(Abbe)),

                // As the scene file declares it: a prop, hidden, and with no position of
                // its own anywhere in the file.
                new PlacedModel(
                    "abebinocs", null, null, Model("abebinocs"), Matrix4x4.Identity,
                    PlacedModelKind.Prop, new ModelPlacement(Binoculars)),
            ]);

        string him = animatesHim ? "\n0,abe_Up" : string.Empty;
        int lines = animatesHim ? 2 : 1;

        var update = new SceneUpdate(scene, new Gk3SheepApi(new GameState()), new Glances(), sink)
        {
            Animations = new AnimationLibrary(n => n.ToUpperInvariant() switch
            {
                "ABEBINOCUP.ANM" or "XYZBINOCUP.ANM" =>
                    $"[HEADER]\n31\n\n[ACTIONS]\n{lines}\n0,abebinocs_Up{him}\n",
                "ABEBINOCBREATH.ANM" =>
                    "[HEADER]\n31\n\n[ACTIONS]\n2\n0,abebinocs_Breath\n0,abe_Breath\n",
                _ => null,
            }),

            Clips = new ClipLibrary(n => n.ToUpperInvariant() switch
            {
                "ABEBINOCS_UP.ACT" => Clip("abebinocs", 31, Reach),
                "ABE_UP.ACT" => Clip("abe", 31, Reach),
                "ABEBINOCS_BREATH.ACT" => Clip("abebinocs", 31, Reach),
                "ABE_BREATH.ACT" => Clip("abe", 31, Reach),
                _ => null,
            }),
        };

        _ = prefixed;
        return (update, sink);
    }

    [Fact]
    public void The_binoculars_are_animated_in_the_hands_rather_than_at_the_origin()
    {
        // Reported from POU's second morning: the NPCs' binoculars animate up and down
        // correctly and do it at the world origin, a quarter of the way across the valley
        // from the men holding them.
        (SceneUpdate update, Sink sink) = Watching();

        Assert.True(update.Play("AbeBinocUp") > 0);
        update.Advance(1.0 / 60);

        Vector3 abbe = sink.WorldPositionOf(Abbe);
        Vector3 binoculars = sink.WorldPositionOf(Binoculars);

        Assert.True(
            abbe.Length() > 500,
            $"the Abbé should be on his mark, not at the origin, but is at {abbe}");

        Assert.True(
            Vector3.Distance(abbe, binoculars) < 1f,
            $"the binoculars should be with him at {abbe}, but are at {binoculars}");
    }

    [Fact]
    public void The_binoculars_stay_with_him_while_the_clip_carries_them()
    {
        // The clip is what moves them: raising them is a mesh travelling through the
        // character's own space, so what has to hold from frame to frame is that they are
        // wherever his clip has them and not that they are motionless.
        (SceneUpdate update, Sink sink) = Watching();

        Assert.True(update.Play("AbeBinocUp") > 0);

        for (int frame = 0; frame < 60; frame++)
        {
            update.Advance(1.0 / 60);

            Assert.True(
                Vector3.Distance(sink.WorldPositionOf(Abbe), sink.WorldPositionOf(Binoculars)) < 1f,
                $"the binoculars left his hands on frame {frame}");
        }
    }

    [Fact]
    public void The_binoculars_do_not_blink_back_to_the_origin_between_clips()
    {
        // AbeBinocIdle.gas is a loop of eight separate animations, so there is a moment
        // between every pair of them when nothing is animating him. The original leaves the
        // binding behind when an animation stops — VertexAnimator::Stop clears the clip and
        // not the parent — precisely so that gap is not a frame of binoculars at the origin.
        (SceneUpdate update, Sink sink) = Watching();

        Assert.True(update.Play("AbeBinocUp") > 0);

        // Right through the end of the clip and well past it.
        for (int frame = 0; frame < 240; frame++)
        {
            update.Advance(1.0 / 60);
        }

        Assert.Equal(0, update.Animating);

        Vector3 resting = sink.WorldPositionOf(Binoculars);

        Assert.True(
            Vector3.Distance(sink.WorldPositionOf(Abbe), resting) < 1f,
            $"the binoculars should still be with him, but are at {resting}");

        // And the next animation of the loop finds them there rather than putting them back.
        Assert.True(update.Play("AbeBinocBreath") > 0);
        update.Advance(1.0 / 60);

        Assert.True(
            Vector3.Distance(sink.WorldPositionOf(Abbe), sink.WorldPositionOf(Binoculars)) < 1f,
            "the binoculars should still be with him through the next clip of the idle");
    }

    [Fact]
    public void A_prop_whose_animation_names_nobody_is_left_in_the_rooms_own_coordinates()
    {
        // The other 92%. A moped that rides past RC1 and a book lifted off a shelf are
        // authored where they happen, and correcting those to somebody's hands is the fault
        // this is the other side of.
        (SceneUpdate update, Sink sink) = Watching();

        Assert.True(update.Play("XyzBinocUp") > 0);
        update.Advance(1.0 / 60);

        Vector3 binoculars = sink.WorldPositionOf(Binoculars);

        Assert.True(
            binoculars.Length() < Reach + 10,
            $"an unnamed animation should play where it was authored, but reached {binoculars}");
    }

    [Fact]
    public void A_prefix_that_matches_a_model_the_animation_does_not_move_binds_nothing()
    {
        // Three letters is a short name. What separates the subject of an animation from a
        // coincidence is whether the animation moves that model too — the original settles
        // the same four corpus lines by hand instead, with noParenting on the pendulum.
        (SceneUpdate update, Sink sink) = Watching(animatesHim: false);

        Assert.True(update.Play("AbeBinocUp") > 0);
        update.Advance(1.0 / 60);

        Vector3 binoculars = sink.WorldPositionOf(Binoculars);

        Assert.True(
            binoculars.Length() < Reach + 10,
            $"nothing should have bound the binoculars, but they reached {binoculars}");
    }
}
