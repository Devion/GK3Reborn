using System.Buffers.Binary;
using System.Numerics;
using GK3Reborn.Content;
using GK3Reborn.Formats;
using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Formats.Lightmaps;
using GK3Reborn.Formats.Scenes;
using GK3Reborn.Formats.Animation;
using GK3Reborn.Formats.Models;
using GK3Reborn.Game;
using GK3Reborn.Game.Actors;
using GK3Reborn.Rendering;
using GK3Reborn.Tests.Formats;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Tests for a face that is wearing an expression rather than playing one.
/// </summary>
/// <remarks>
/// <para>
/// The lobby at two in the morning is what these are about. <c>LBY202A.NVC</c> opens with
/// <c>setmood("simone","sleep")</c>, and the mood is a pair of animations: <c>SIMSLEEPON</c>
/// is one <c>FACETEX</c> holding Simone's eyelids at <c>SIM_BLINK_02</c> — the shut one —
/// and <c>SIMSLEEPOFF</c> is the <c>UNFACETEX</c> that takes it off again. Only the first
/// of them runs when she goes to sleep, and it is two frames long.
/// </para>
/// <para>
/// So an expression that put itself back when its animation ended opened her eyes an eighth
/// of a second after the room put her to sleep, and the blink timer then ran her through
/// three eyelid pictures every few seconds for the rest of the block: a woman asleep face
/// down on the reception desk, blinking. Both halves of that are one fault — the blink is
/// what opens the eyes — and both are here.
/// </para>
/// </remarks>
public sealed class SleepingFaceTests : IDisposable
{
    private readonly string _root =
        Directory.CreateTempSubdirectory("gk3r-sleeping-face").FullName;

    private readonly List<IDisposable> _open = [];

    public void Dispose()
    {
        foreach (IDisposable item in _open)
        {
            item.Dispose();
        }

        Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>The corpus's own files, transcribed.</summary>
    private const string SleepOn = "[HEADER]\n2\n\n[GK3]\n1\n0,FACETEX,SIMONE,SIM_BLINK_02,E\n";

    private const string SleepOff = "[HEADER]\n2\n\n[GK3]\n1\n1,UNFACETEX,SIMONE,E\n";

    private const string Blink =
        "[HEADER]\n4\n\n[GK3]\n4\n" +
        "0,FACETEX,SIMONE,SIM_BLINK_01,E\n1,FACETEX,SIMONE,SIM_BLINK_02,E\n" +
        "2,FACETEX,SIMONE,SIM_BLINK_01,E\n3,UNFACETEX,SIMONE,E\n";

    private const string FacesText =
        """
        [DEFAULT]
        Blink Frequency         = 5000,12000

        [SIM]
        Forehead Offset         = 90,77
        Eyelids Offset          = 105,106
        Blink Anims             = simblink,100
        Blink Frequency         = 5000,12000
        Mouth Offset            = 90,132
        Mouth Size              = 8x8
        """;

    [Fact]
    public void A_sleeping_character_keeps_her_eyes_shut()
    {
        (Faces faces, PlacedModel simone) = Lobby();
        Assert.True(faces.Add(simone));

        // Awake, her eyelids are her own.
        Assert.Equal("SIM_EYELIDS", faces.Wearing("SIMONE", FacePart.Eyelids));

        Asleep(faces);

        Assert.Equal("SIM_BLINK_02", faces.Wearing("SIMONE", FacePart.Eyelids));
    }

    [Fact]
    public void And_does_not_blink_through_the_night()
    {
        (Faces faces, PlacedModel simone) = Lobby();
        faces.Add(simone);
        Asleep(faces);

        // Five minutes at thirty frames a second. Her blink frequency is five to twelve
        // seconds, so an eye that could open would have opened twenty-five times over.
        for (int frame = 0; frame < 30 * 300; frame++)
        {
            faces.Advance(1.0 / 30.0);

            Assert.Equal("SIM_BLINK_02", faces.Wearing("SIMONE", FacePart.Eyelids));
        }
    }

    [Fact]
    public void And_wakes_up_when_the_mood_comes_off()
    {
        (Faces faces, PlacedModel simone) = Lobby();
        faces.Add(simone);
        Asleep(faces);

        // The other half of the pair, which is the UNFACETEX on its own.
        faces.Perform(Read(SleepOff));
        Advance(faces, seconds: 1);

        Assert.Equal("SIM_EYELIDS", faces.Wearing("SIMONE", FacePart.Eyelids));

        // And blinking picks up again by itself, because nothing is held on her eyelids
        // any more. Well inside the twelve seconds the frequency tops out at.
        Assert.True(
            Blinked(faces, seconds: 20),
            "she never blinked again after waking up");
    }

    [Fact]
    public void A_blink_puts_the_eyelids_back_itself_and_nothing_else_has_to()
    {
        // The other side of the same rule. Every blink in the corpus ends on an UNFACETEX,
        // so the eyelids come back from the animation's own last node rather than from the
        // engine clearing the face when the clip runs out — which is what used to take a
        // held expression off with it.
        (Faces faces, PlacedModel simone) = Lobby();
        faces.Add(simone);

        Assert.True(Blinked(faces, seconds: 20), "she never blinked at all");

        // Four frames of it, and then her own eyelids back — from the animation's last
        // node, with nothing outside it having to notice that the clip ran out.
        Advance(faces, seconds: 1);
        Assert.Equal("SIM_EYELIDS", faces.Wearing("SIMONE", FacePart.Eyelids));
    }

    [Fact]
    public void An_expression_leaves_the_regions_it_says_nothing_about_alone()
    {
        // A blink is eyelids only, and it used to reset the forehead and the mouth to
        // resting on every frame it ran — so a brow held up by a mood came down for the
        // duration of any blink and never went back up.
        (Faces faces, PlacedModel simone) = Lobby();
        faces.Add(simone);

        faces.Paint("SIMONE", FacePart.Forehead, "SIM_BROW_UP");
        Assert.True(Blinked(faces, seconds: 20));

        Assert.Equal("SIM_BROW_UP", faces.Wearing("SIMONE", FacePart.Forehead));
    }

    [Fact]
    public void The_eyelids_alpha_channel_belongs_to_the_resting_eyelids_only()
    {
        // FACES.TXT gives the eyelids region an alpha channel, and it is a hole cut where
        // the eye opening is: the resting eyelids are an open eye drawn round a black
        // eyeball, and the mask keeps that black off the face so what shows through is the
        // eye. Laid over a lid an animation has painted, the same hole is punched through a
        // shut eye and the open eyes baked into the face bitmap come straight back up
        // through it — a blink that never closes, and Simone asleep on the reception desk
        // with her eyes open, both out of one line.
        (Faces faces, PlacedModel simone, Sink sink) = Masked();
        faces.Add(simone);

        // At rest the mask still applies, so the face shows through the eye opening.
        Assert.Equal(FaceColour, sink.Eye);

        faces.Perform(Read(SleepOn));
        Advance(faces, seconds: 1);

        Assert.Equal("SIM_BLINK_02", faces.Wearing("SIMONE", FacePart.Eyelids));
        Assert.Equal(ShutColour, sink.Eye);

        // And the mask comes back with the resting eyelids when she wakes up.
        faces.Perform(Read(SleepOff));
        Advance(faces, seconds: 1);

        Assert.Equal(FaceColour, sink.Eye);
    }

    /// <summary>Puts her to sleep, as the lobby's <c>setmood</c> does.</summary>
    private static void Asleep(Faces faces)
    {
        faces.Perform(Read(SleepOn));

        // Long enough for the two frames of it to be over several times.
        Advance(faces, seconds: 1);
    }

    /// <summary>Runs time at thirty frames a second.</summary>
    private static void Advance(Faces faces, double seconds)
    {
        for (int frame = 0; frame < (int)(seconds * 30); frame++)
        {
            faces.Advance(1.0 / 30.0);
        }
    }

    /// <summary>Whether her eyelids left resting at any point over a span.</summary>
    private static bool Blinked(Faces faces, double seconds)
    {
        for (int frame = 0; frame < (int)(seconds * 30); frame++)
        {
            faces.Advance(1.0 / 30.0);

            if (faces.Wearing("SIMONE", FacePart.Eyelids) != "SIM_EYELIDS")
            {
                return true;
            }
        }

        return false;
    }

    private static AnimationFile Read(string text) =>
        AnimationFile.Parse(text, "TEST.ANM", new GK3Reborn.Foundation.Diagnostics.DiagnosticBag());

    /// <summary>Simone at the desk, with the bitmaps her face is pasted together from.</summary>
    private (Faces Faces, PlacedModel Simone) Lobby()
    {
        var barn = new BarnFixture();

        foreach (string name in (string[])
                 [
                     "SIM_FACE", "SIM_EYELIDS", "SIM_FOREHEAD", "SIM_MOUTH00",
                     "SIM_BLINK_01", "SIM_BLINK_02", "SIM_BROW_UP",
                 ])
        {
            barn.AddStored(name + ".BMP", Bitmap());
        }

        File.WriteAllBytes(Path.Combine(_root, "core.brn"), barn.Build());

        GameArchives archives = GameArchives.Open(_root);
        _open.Add(archives);

        var sink = new HeadlessSceneSink();
        ModFile model = Head();
        ModelPlacement placement = sink.Add(model, Matrix4x4.Identity);

        var faces = new Faces(
            FaceLibrary.Parse(FacesText),
            archives,
            new AnimationLibrary(name => name.ToUpperInvariant() switch
            {
                "SIMBLINK.ANM" => Blink,
                "SIMSLEEPON.ANM" => SleepOn,
                "SIMSLEEPOFF.ANM" => SleepOff,
                _ => null,
            }),
            sink);

        return (
            faces,
            new PlacedModel(
                "sim_", "SIMONE", null, model, Matrix4x4.Identity,
                PlacedModelKind.Actor, placement));
    }

    /// <summary>A head painted with the bitmap <c>FACES.TXT</c> says is Simone's.</summary>
    private static ModFile Head() => ModFile.FromMeshes(
        "sim_",
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
                        TextureName = "SIM_FACE",
                        Color = (255, 255, 255),
                        Positions = [Vector3.Zero],
                        Normals = [Vector3.UnitY],
                        TexCoords = [Vector2.Zero],
                        Indices = [0, 0, 0],
                    },
                ],
            },
        ]);

    /// <summary>A GK3 bitmap, eight square and one colour. Height comes before width.</summary>
    private static byte[] Bitmap(ushort colour = 0)
    {
        byte[] output = new byte[8 + (8 * 8 * 2)];
        output[0] = 0x36;
        output[1] = 0x31;
        output[2] = 0x6E;
        output[3] = 0x4D;
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(4), 8);
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(6), 8);

        for (int at = 0; at < 8 * 8; at++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(8 + (at * 2)), colour);
        }

        return output;
    }

    /// <summary>Blue as five-six-five, and as it comes back out of the decoder.</summary>
    private const ushort Blue = 0x001F;

    private static readonly (byte R, byte G, byte B) FaceColour = (0, 0, 255);

    /// <summary>Green the same way: what every eyelid picture is painted with here.</summary>
    private const ushort Green = 0x07E0;

    private static readonly (byte R, byte G, byte B) ShutColour = (0, 255, 0);

    /// <summary>The same character, with the alpha channel her eyelids actually have.</summary>
    /// <remarks>
    /// Three colours, so that what ends up over the eye can be told apart: the face is
    /// blue, every eyelid picture is green, and the mask is black — none of the patch shows
    /// through it at all. The forehead and the mouth are pushed off the eight-pixel face so
    /// that the only thing painted over the eye is the eyelids.
    /// </remarks>
    private (Faces Faces, PlacedModel Simone, Sink Sink) Masked()
    {
        const string configuration =
            """
            [DEFAULT]
            Blink Frequency         = 5000,12000

            [SIM]
            Forehead Offset         = 32,32
            Eyelids Offset          = 0,0
            Eyelids Alpha Channel   = sim_eyelids_alpha
            Blink Anims             = simblink,100
            Blink Frequency         = 5000,12000
            Mouth Offset            = 32,32
            Mouth Size              = 8x8
            """;

        var barn = new BarnFixture();
        barn.AddStored("SIM_FACE.BMP", Bitmap(Blue));
        barn.AddStored("SIM_EYELIDS_ALPHA.BMP", Bitmap());

        foreach (string name in (string[])
                 ["SIM_EYELIDS", "SIM_FOREHEAD", "SIM_MOUTH00", "SIM_BLINK_01", "SIM_BLINK_02"])
        {
            barn.AddStored(name + ".BMP", Bitmap(Green));
        }

        string root = Directory.CreateTempSubdirectory("gk3r-masked-face").FullName;
        File.WriteAllBytes(Path.Combine(root, "core.brn"), barn.Build());

        GameArchives archives = GameArchives.Open(root);
        _open.Add(archives);
        _open.Add(new Cleanup(root));

        var sink = new Sink();
        ModFile model = Head();
        ModelPlacement placement = sink.Add(model, Matrix4x4.Identity);

        var faces = new Faces(
            FaceLibrary.Parse(configuration),
            archives,
            new AnimationLibrary(name => name.ToUpperInvariant() switch
            {
                "SIMBLINK.ANM" => Blink,
                "SIMSLEEPON.ANM" => SleepOn,
                "SIMSLEEPOFF.ANM" => SleepOff,
                _ => null,
            }),
            sink);

        return (
            faces,
            new PlacedModel(
                "sim_", "SIMONE", null, model, Matrix4x4.Identity,
                PlacedModelKind.Actor, placement),
            sink);
    }

    /// <summary>Takes a directory away once the test is over.</summary>
    private sealed class Cleanup(string directory) : IDisposable
    {
        public void Dispose() => Directory.Delete(directory, recursive: true);
    }

    /// <summary>Keeps the composed faces, so what was painted on one can be read back.</summary>
    /// <remarks>
    /// Composition is the half of this that the state cannot speak for. Simone wore
    /// <c>SIM_BLINK_02</c> on her eyelids all night while the picture the composition made
    /// of it had her eyes open, so a test that only reads what a region is wearing passes
    /// straight over the fault.
    /// </remarks>
    private sealed class Sink : ISceneSink
    {
        private readonly HeadlessSceneSink _inner = new();

        private readonly Dictionary<string, DecodedImage> _composed =
            new(StringComparer.OrdinalIgnoreCase);

        private string? _painted;

        public Action? Progress { get; set; }

        /// <summary>The top left pixel of the face the model is painted with.</summary>
        /// <remarks>
        /// Which is the eye: the eyelids are pasted at the origin here, and everything else
        /// is off the edge of the picture.
        /// </remarks>
        public (byte R, byte G, byte B) Eye
        {
            get
            {
                DecodedImage image = _composed[_painted ?? throw new InvalidOperationException(
                    "nothing has been painted onto the model")];

                return (image.Pixels[0], image.Pixels[1], image.Pixels[2]);
            }
        }

        public Vector3 Minimum => _inner.Minimum;

        public Vector3 Maximum => _inner.Maximum;

        public int TextureCount => _inner.TextureCount;

        public int TriangleCount => _inner.TriangleCount;

        public void AddTexture(string name, DecodedImage image)
        {
            _composed[name] = image;
            _inner.AddTexture(name, image);
        }

        public void AddTexture(string name, CompressedImage image) =>
            _inner.AddTexture(name, image);

        public void AddNormalMap(string name, DecodedImage image)
        {
        }

        public void AddNormalMap(string name, CompressedImage image)
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

        public void Repaint(ModelPlacement placement, string texture, string? painted)
        {
            _painted = painted;
            _inner.Repaint(placement, texture, painted);
        }

        public bool SetSceneObjectVisible(string objectName, bool visible) => true;

        public bool PaintSceneObject(string objectName, string? texture) => true;

        public bool SwapLightmaps(MulFile lightmaps) => true;

        public void SetSelfLit(ModelPlacement placement, bool selfLit)
        {
        }

        public void SetVisible(ModelPlacement placement, bool visible) =>
            _inner.SetVisible(placement, visible);

        public void SetPartVisible(
            ModelPlacement placement, int mesh, int submesh, bool visible) =>
            _inner.SetPartVisible(placement, mesh, submesh, visible);

        public IReadOnlyList<(string Name, Vector3 Minimum, Vector3 Maximum)> SceneObjectBoxes() =>
            [];

        public void PoseMesh(ModelPlacement placement, int mesh, Matrix4x4 meshToLocal)
        {
        }

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
    }
}
