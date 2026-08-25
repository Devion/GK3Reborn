using System.Numerics;
using System.Text;
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
/// Tests for playing a clip against a head the clip has never seen.
/// </summary>
/// <remarks>
/// The refinement replaces a character's head with a denser one and leaves the clips alone,
/// so playback has to read a clip's vertices as a motion instead of writing them into a
/// buffer. The question that decides whether any of it is allowed is not whether the code
/// runs: it is whether the head still ends up exactly where the original animation puts it.
/// That is what these compare — the same clip, the same frame, played both ways.
/// </remarks>
public sealed class HeadPlaybackTests
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

        public ModelPlacement Add(
            ModFile model,
            Matrix4x4? transform = null,
            IReadOnlyDictionary<int, Matrix4x4>? meshTurns = null) =>
            _inner.Add(model, transform, meshTurns);

        /// <summary>Turns applied on top of a mesh's own transform.</summary>
        /// <remarks>
        /// Kept apart from <see cref="Poses"/> because they mean different things: a pose
        /// replaces a mesh's transform and a turn is applied over it. A refined head takes
        /// whichever of the two the clip left room for.
        /// </remarks>
        public Dictionary<(int Placement, int Mesh), Matrix4x4> Turns { get; } = [];

        public void TurnMesh(ModelPlacement placement, int mesh, Matrix4x4 turn)
        {
            Turns[(placement.Id, mesh)] = turn;
            _inner.TurnMesh(placement, mesh, turn);
        }

        /// <summary>What each texture of each model has been painted over with.</summary>
        public Dictionary<(int Placement, string Texture), string?> Painted { get; } =
            new();

        public void Repaint(ModelPlacement placement, string texture, string? painted) =>
            Painted[(placement.Id, texture)] = painted;

        /// <summary>Which models have been hidden, and which shown again.</summary>
        public Dictionary<int, bool> Visible { get; } = [];

        public bool SetSceneObjectVisible(string objectName, bool visible) => true;

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

    /// <summary>A head: eight corners, enough to fix a rotation and to subdivide.</summary>
    private static readonly Vector3[] Corners =
    [
        new(-4f, -4f, -4f), new(4f, -4f, -4f), new(4f, 4f, -4f), new(-4f, 4f, -4f),
        new(-4f, -4f, 4f), new(4f, -4f, 4f), new(4f, 4f, 4f), new(-4f, 4f, 4f),
    ];

    /// <summary>
    /// The three markers every mesh group in the game carries, at sixty units out.
    /// </summary>
    /// <remarks>
    /// Part of the fixture rather than a case of its own, because every real head has them
    /// and the fit has to be right in their presence, not merely capable of being right
    /// without them. They belong to no triangle and do not travel with the head.
    /// </remarks>
    private static readonly Vector3[] Triad =
    [
        new(60f, 0f, 0f), new(0f, 60f, 0f), new(0f, 0f, 60f),
    ];

    /// <summary>Every vertex the clip addresses: the head, then the markers.</summary>
    private static Vector3[] Authored => [.. Corners, .. Triad];

    private static readonly ushort[] Box =
    [
        0, 2, 1, 0, 3, 2,  4, 5, 6, 4, 6, 7,
        0, 1, 5, 0, 5, 4,  2, 3, 7, 2, 7, 6,
        1, 2, 6, 1, 6, 5,  0, 4, 7, 0, 7, 3,
    ];

    /// <summary>A character of one mesh, which the head finder will call a head.</summary>
    private static ModFile Model() => ModFile.FromMeshes(
        "gra",
        [
            new ModMesh
            {
                MeshToLocal = Matrix4x4.Identity,
                BoundsMin = new Vector3(-4f),
                BoundsMax = new Vector3(4f),
                Submeshes =
                [
                    new ModSubmesh
                    {
                        TextureName = "GRA_FACE",
                        Color = (255, 255, 255),
                        Positions = Authored,
                        Normals = [.. Authored.Select(Vector3.Normalize)],
                        TexCoords = [.. Authored.Select(c => new Vector2(c.X, c.Y))],
                        Indices = Box,
                    },
                ],
            },
        ]);

    /// <summary>Where the synthetic clip is authored, a long way from the model.</summary>
    private const float Away = 500f;

    /// <summary>
    /// The head, turned by this much, is what every frame of the synthetic clip records.
    /// </summary>
    /// <remarks>
    /// A rotation about all three axes rather than one, so a fit that recovered only part
    /// of it — or recovered it transposed — could not pass by symmetry.
    /// </remarks>
    private static Matrix4x4 Turned => Matrix4x4.CreateFromYawPitchRoll(0.6f, -0.35f, 0.2f);

    /// <summary>A clip that moves the mesh and records the head's vertices on every frame.</summary>
    /// <param name="model">Which model the header names.</param>
    /// <param name="frames">How long it runs.</param>
    /// <param name="shape">Where each authored vertex is, or null to record no vertices.</param>
    private static byte[] Clip(string model, int frames, Func<Vector3, Vector3>? shape)
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
                     { 1, 0, 0, 0, 1, 0, 0, 0, 1, Away + frame, 0, 0 })
            {
                block.AddRange(BitConverter.GetBytes(value));
            }

            if (shape is not null)
            {
                Vector3[] authored = Authored;

                List<byte> vertices = [.. BitConverter.GetBytes((ushort)0)];
                vertices.AddRange(BitConverter.GetBytes((ushort)authored.Length));

                for (int i = 0; i < authored.Length; i++)
                {
                    // The markers stay where they are while the head turns, which is what
                    // makes them poison for a fit: three points sixty units out, holding
                    // still, outvote three hundred that moved.
                    Vector3 placed = i < Corners.Length ? shape(authored[i]) : authored[i];

                    vertices.AddRange(BitConverter.GetBytes(placed.X));
                    vertices.AddRange(BitConverter.GetBytes(placed.Y));
                    vertices.AddRange(BitConverter.GetBytes(placed.Z));
                }

                block.Add(0);
                block.AddRange(BitConverter.GetBytes(vertices.Count));
                block.AddRange(vertices);
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

    /// <summary>A world with one character in it, their head refined to <paramref name="levels"/>.</summary>
    private static (SceneUpdate Update, Sink Sink, HeadRig? Rig) World(
        int levels, Func<Vector3, Vector3>? shape)
    {
        var sink = new Sink();

        (ModFile model, HeadRig? rig) = HeadRefinement.Apply(Model(), levels);

        sink.Add(model);

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
                    "gra", "GRACE", null, model, Matrix4x4.Identity,
                    PlacedModelKind.Actor, new ModelPlacement(0))
                {
                    Head = rig,
                },
            ]);

        var update = new SceneUpdate(scene, new Gk3SheepApi(new GameState()), new Glances(), sink)
        {
            Animations = new AnimationLibrary(n =>
                n.Equals("Breathe.ANM", StringComparison.OrdinalIgnoreCase)
                    ? "[HEADER]\n31\n\n[ACTIONS]\n1\n0,gra_Breathe,0,0,0,0\n"
                    : null),

            Clips = new ClipLibrary(n =>
                n.Equals("gra_Breathe.ACT", StringComparison.OrdinalIgnoreCase)
                    ? Clip("gra", 31, shape)
                    : null)
            { KeepVertices = true },
        };

        return (update, sink, rig);
    }

    /// <summary>
    /// The one that matters: a refined head lands exactly where the original animation put
    /// the authored one. Played both ways, from the same clip, at the same moment.
    /// </summary>
    [Fact]
    public void ARefinedHeadGoesWhereTheOriginalHeadWent()
    {
        Vector3 Shape(Vector3 corner) => Vector3.Transform(corner, Turned);

        (SceneUpdate plain, Sink flat, HeadRig? none) = World(0, Shape);
        (SceneUpdate refined, Sink smooth, HeadRig? rig) = World(2, Shape);

        Assert.Null(none);
        Assert.NotNull(rig);

        plain.Play("Breathe");
        plain.Advance(0.5);

        refined.Play("Breathe");
        refined.Advance(0.5);

        Matrix4x4 was = flat.Poses[(0, 0)];
        Matrix4x4 now = smooth.Poses[(0, 0)];
        IReadOnlyList<Vector3> shaped = flat.Shapes[(0, 0, 0)];

        for (int i = 0; i < Corners.Length; i++)
        {
            // Where the authored head's vertex ends up when the clip writes it into the
            // mesh, against where it ends up when the same clip is read as a motion and
            // applied to the mesh instead.
            Vector3 original = Vector3.Transform(shaped[i], was);
            Vector3 fitted = Vector3.Transform(Corners[i], now);

            Assert.True(
                Vector3.Distance(original, fitted) < 1e-2f,
                $"vertex {i}: {original} against {fitted}");
        }
    }

    /// <summary>
    /// The fit ignores the axis markers, and the head turns by exactly what the clip asked.
    /// </summary>
    /// <remarks>
    /// This is a regression test for a bug that measured well: including the triad made the
    /// corpus survey report Mosely as deforming his head by 40% of its width on a tenth of
    /// his frames, and the numbers were consistent enough to look like a finding about the
    /// game rather than a mistake in the fit.
    /// </remarks>
    [Fact]
    public void TheAxisMarkersDoNotDragTheFit()
    {
        (SceneUpdate update, Sink sink, HeadRig? rig) = World(
            2, corner => Vector3.Transform(corner, Turned));

        Assert.NotNull(rig);

        // The markers are in what the clip addresses and out of what the fit looks at.
        Assert.Equal(Authored.Length, Assert.Single(rig!.Rest).Length);
        Assert.Equal(Corners.Length, Assert.Single(rig.Sample).Length);

        update.Play("Breathe");
        update.Advance(0.5);

        Matrix4x4 posed = sink.Poses[(0, 0)];

        // The corners are a cube about the origin, so the head's centre is the origin and
        // whatever the pose does to it is the translation. Take that off and what is left is
        // the rotation the fit recovered, which has to be the one the clip recorded. Dragged
        // by three stationary points sixty units out, it would come back a fraction of that.
        Vector3 centre = Vector3.Transform(Vector3.Zero, posed);

        for (int i = 0; i < Corners.Length; i++)
        {
            Vector3 asked = Vector3.Transform(Corners[i], Turned);
            Vector3 got = Vector3.Transform(Corners[i], posed) - centre;

            Assert.True(
                Vector3.Distance(asked, got) < 1e-2f,
                $"vertex {i}: asked for {asked}, got {got}");
        }
    }

    /// <summary>A refined head is never reshaped, because the buffer is the wrong size.</summary>
    [Fact]
    public void ARefinedHeadIsNeverReshaped()
    {
        (SceneUpdate update, Sink sink, HeadRig? rig) = World(
            2, corner => Vector3.Transform(corner, Turned));

        Assert.NotNull(rig);

        update.Play("Breathe");
        update.Advance(0.5);

        Assert.NotEmpty(sink.Poses);
        Assert.Empty(sink.Shapes);
    }

    /// <summary>And an unrefined one still is, exactly as before.</summary>
    [Fact]
    public void AnUnrefinedHeadIsStillReshaped()
    {
        (SceneUpdate update, Sink sink, HeadRig? rig) = World(
            0, corner => Vector3.Transform(corner, Turned));

        Assert.Null(rig);

        update.Play("Breathe");
        update.Advance(0.5);

        Assert.NotEmpty(sink.Shapes);
    }

    /// <summary>
    /// Nine of the game's fifty-six models with head clips disagree with the geometry
    /// shipped for them, by up to 40% of head width. Those clips have to fall back to the
    /// transform track rather than turn somebody's head to a number fitted from nonsense.
    /// </summary>
    [Fact]
    public void AHeadThatDoesNotFitFallsBackToTheClipsOwnTransform()
    {
        // Not a rigid motion of the head at all: every vertex pulled somewhere unrelated.
        Vector3 Scrambled(Vector3 corner) =>
            new(corner.X * 3f, corner.Y * -0.2f, corner.Z + (corner.X * corner.Y));

        (SceneUpdate refused, Sink fallen, HeadRig? rig) = World(2, Scrambled);
        (SceneUpdate rigid, Sink carried, HeadRig? _) = World(2, null);

        Assert.NotNull(rig);

        refused.Play("Breathe");
        refused.Advance(0.5);

        rigid.Play("Breathe");
        rigid.Advance(0.5);

        // The same place a clip with no vertex track at all would have put it: the fit was
        // thrown away rather than applied.
        Assert.Equal(carried.Poses[(0, 0)], fallen.Poses[(0, 0)]);
        Assert.Empty(fallen.Shapes);
    }
}
