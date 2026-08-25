using GK3Reborn.Formats.Animation;
using System.Numerics;
using GK3Reborn.Content;
using GK3Reborn.Formats.Actions;
using GK3Reborn.Formats.Audio;
using GK3Reborn.Game.Actors;
using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Formats.Lightmaps;
using GK3Reborn.Formats.Models;
using GK3Reborn.Formats.Scenes;
using GK3Reborn.Foundation.Diagnostics;
using GK3Reborn.Game.Navigation;
using GK3Reborn.Rendering;

namespace GK3Reborn.Game;

/// <summary>What loading a scene produced, besides its geometry.</summary>
/// <param name="Name">Scene name.</param>
/// <param name="Definition">What the scene's initialisation files say it is.</param>
/// <param name="Asset">The scene asset for the chosen time of day, if it has one.</param>
/// <param name="Lightmaps">The baked lighting that was applied, if any.</param>
/// <param name="ModelsPlaced">How many props were placed.</param>
/// <param name="Placed">
/// The props and actors that were loaded from files, with where they stand. Kept so a
/// click can be resolved against them; the geometry the renderer holds cannot answer that.
/// </param>
/// <param name="Actions">
/// What the player may do to the things in the room, or null when the caller named no
/// point in the story: an action's case is a Sheep expression over the story's state, and
/// with no state there is nothing to decide it against.
/// </param>
/// <param name="Soundtracks">The <c>.STK</c> files the scene plays in the background.</param>
/// <param name="Ambience">Those soundtracks, read.</param>
/// <param name="Walkable">Where actors may stand, if the scene declares a boundary.</param>
/// <param name="Geometry">
/// The room's parsed geometry. Kept because several things want to ask questions of it
/// after loading — where the floor is under a point, what a click landed on — and
/// re-reading the file to answer them would be silly.
/// </param>
public sealed record LoadedScene(
    string Name,
    SceneDefinition Definition,
    SceneAssetFile? Asset,
    MulFile? Lightmaps,
    int ModelsPlaced,
    WalkBoundary? Walkable = null,
    BspFile? Geometry = null,
    IReadOnlyList<PlacedModel>? Placed = null,
    ActionResolver? Actions = null,
    IReadOnlyList<string>? Soundtracks = null,
    IReadOnlyList<SoundtrackFile>? Ambience = null)
{
    private WalkFloor? _ground;
    private bool _groundSought;

    /// <summary>
    /// The shell that keeps the camera inside the room, when the scene names one.
    /// </summary>
    /// <remarks>
    /// Beside the record's other members rather than among them because only a scene that
    /// was actually loaded can have one — <see cref="SceneLoader.Compose"/> reads the text
    /// and no models at all, and a corpus sweep over five hundred rooms has no camera to
    /// fence in.
    /// </remarks>
    public Navigation.CameraBounds? CameraShell { get; init; }

    /// <summary>
    /// The sun, on a daytime exterior. The one light no artist authored: see
    /// <see cref="Sunlight"/> for why it exists, where it stands, and whose light it
    /// replaces.
    /// </summary>
    public AuthoredLight? Sun { get; init; }

    /// <summary>The corners of the loaded geometry, for recognising distant lights.</summary>
    internal (Vector3 Minimum, Vector3 Maximum) Bounds { get; init; }

    /// <summary>
    /// The rig the room is actually lit by: the artists' lights, with any scenekey the
    /// synthesized sun stands in for taken out and the sun put in.
    /// </summary>
    public IReadOnlyList<AuthoredLight> Lights =>
        Sun is { } sun
            ? [.. (Asset?.Lights ?? [])
                  .Where(l => !Sunlight.IsAuthoredSun(l, Bounds.Minimum, Bounds.Maximum)), sun]
            : Asset?.Lights ?? [];

    /// <summary>
    /// How high the ground is under a point, or null when the scene cannot say.
    /// </summary>
    /// <remarks>
    /// Built from the object the scene calls its floor, the first time anybody asks. Lazily
    /// because most of what loads a scene never walks anybody across it — a corpus sweep
    /// over five hundred rooms should not triangulate five hundred floors to find that out.
    /// </remarks>
    public WalkFloor? Ground
    {
        get
        {
            if (!_groundSought)
            {
                _groundSought = true;
                _ground = WalkFloor.From(Geometry, Definition.FloorObject());
            }

            return _ground;
        }
    }

    /// <summary>Cameras the player's view can occupy.</summary>
    public IReadOnlyList<SceneCamera> Cameras => Definition.RoomCameras();

    /// <summary>The props and actors loaded from files, never null.</summary>
    public IReadOnlyList<PlacedModel> Models => Placed ?? [];

    /// <summary>
    /// The middle of a named piece of the room's own geometry.
    /// </summary>
    /// <param name="objectName">The BSP object's name, such as <c>bthdr_scene</c>.</param>
    /// <returns>Its centre in world space, or null when the room has no such object.</returns>
    /// <remarks>
    /// Most of what a script points at is not a model standing in the room but part of the
    /// room itself — a door, a rack, a noticeboard. 2,120 of the corpus's 3,617 approaches
    /// are <c>WalkToSee</c> and most of their targets are these, so without this a walk is
    /// asked for and there is nowhere to walk to.
    /// </remarks>
    public Vector3? MiddleOf(string objectName)
    {
        ArgumentNullException.ThrowIfNull(objectName);

        if (Geometry is not { } bsp)
        {
            return null;
        }

        int index = -1;

        for (int i = 0; i < bsp.ObjectNames.Count; i++)
        {
            if (string.Equals(bsp.ObjectNames[i], objectName, StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                break;
            }
        }

        if (index < 0)
        {
            return null;
        }

        Vector3 minimum = new(float.MaxValue);
        Vector3 maximum = new(float.MinValue);
        bool any = false;

        foreach (BspPolygon polygon in bsp.Polygons)
        {
            if (polygon.SurfaceIndex < 0 ||
                polygon.SurfaceIndex >= bsp.Surfaces.Count ||
                bsp.Surfaces[polygon.SurfaceIndex].ObjectIndex != index)
            {
                continue;
            }

            foreach ((ushort a, ushort b, ushort c) in bsp.Triangulate(polygon))
            {
                minimum = Vector3.Min(minimum, bsp.Vertices[a]);
                minimum = Vector3.Min(minimum, bsp.Vertices[b]);
                minimum = Vector3.Min(minimum, bsp.Vertices[c]);

                maximum = Vector3.Max(maximum, bsp.Vertices[a]);
                maximum = Vector3.Max(maximum, bsp.Vertices[b]);
                maximum = Vector3.Max(maximum, bsp.Vertices[c]);

                any = true;
            }
        }

        return any ? (minimum + maximum) / 2 : null;
    }

    /// <summary>The soundtracks the scene plays, never null.</summary>
    public IReadOnlyList<string> Ambient => Soundtracks ?? [];

    /// <summary>Those soundtracks as read, never null.</summary>
    public IReadOnlyList<SoundtrackFile> AmbienceRead => Ambience ?? [];

    /// <summary>Finds a camera by name, falling back to the scene's default.</summary>
    /// <param name="name">Camera name, or null for the default.</param>
    /// <returns>The camera, or null if the scene defines none.</returns>
    public SceneCamera? CameraNamed(string? name) => Definition.CameraNamed(name);
}

/// <summary>
/// Assembles a scene the way the game does.
/// </summary>
/// <remarks>
/// <para>
/// A scene is not one file. The initialisation file names a scene asset for the time of
/// day; the scene asset names the geometry, the objects in it and the lights that lit it;
/// the geometry references textures and pairs surface for surface with a lightmap set.
/// This walks that chain and puts the result on the GPU.
/// </para>
/// <para>
/// Conditional sections are taken at face value. Which apply depends on the story's
/// state, and deciding that needs the Sheep virtual machine and a running game; until
/// then, showing everything a scene can contain is more useful than showing nothing.
/// </para>
/// </remarks>
public sealed class SceneLoader
{
    private static readonly string[] TimeblockSuffixes = ["_M", "_A", "_E", "_N", ""];

    private readonly GameArchives _archives;
    private readonly Action<string>? _log;
    private int _enhancedUsed;
    private int _treesGrown;

    /// <summary>Which of a room's trees the budget stretched to growing in full.</summary>
    private readonly HashSet<int> _nearTrees = [];

    /// <summary>Where a prop has already put a modelled tree, so the room does not too.</summary>
    private readonly List<(System.Numerics.Vector3 Foot, float Radius)> _standing = [];

    /// <summary>Creates a loader.</summary>
    /// <param name="archives">Where to read assets from.</param>
    /// <param name="log">Optional progress sink.</param>
    public SceneLoader(GameArchives archives, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(archives);
        _archives = archives;
        _log = log;
    }

    /// <summary>
    /// Higher-resolution textures to use in place of the archives', if there are any.
    /// </summary>
    /// <remarks>
    /// A layer rather than a replacement: a texture with no enhanced version loads from the
    /// archive as before, so a partial set works. Null or empty means the game looks
    /// exactly as it shipped.
    /// </remarks>
    public EnhancedTextures? Enhanced { get; set; }

    /// <summary>
    /// Generated normal maps, standing beside the colour textures.
    /// </summary>
    /// <remarks>
    /// A separate set from <see cref="Enhanced"/> because they are a separate pass and a
    /// separate judgement: a surface may have a better colour texture and no normal map, or
    /// the other way round. Named for the colour texture they belong to.
    /// </remarks>
    public EnhancedTextures? Normals { get; set; }

    /// <summary>
    /// Generated occlusion, roughness and metalness, packed into one picture per surface.
    /// </summary>
    /// <remarks>
    /// Red, green and blue in that order, which is the glTF packing every generator and
    /// every authoring tool already writes. A separate set again, and a separate judgement:
    /// a roughness that reads as wet stone is a different mistake from a normal map that
    /// embosses printed lettering, and they are reviewed apart.
    /// </remarks>
    public EnhancedTextures? Orms { get; set; }

    /// <summary>
    /// Generated height fields, one per surface, for parallax.
    /// </summary>
    /// <remarks>
    /// Mid grey is the modelled surface. Consumed as a texture-coordinate offset rather
    /// than as displacement, so it deepens what is already flat and changes no silhouette.
    /// </remarks>
    public EnhancedTextures? Heights { get; set; }

    /// <summary>
    /// Modelled trees to stand in place of the scene's flat foliage cards.
    /// </summary>
    /// <remarks>
    /// The one enhancement here that changes geometry rather than what is painted on it,
    /// and it is confined to foliage because foliage is where a card is the whole of the
    /// object: a wall drawn flat is a wall, and a tree drawn flat is a picture of a tree.
    /// Null or empty leaves every card exactly as it shipped.
    /// </remarks>
    public TreeLibrary? Trees { get; set; }

    /// <summary>How many flat cards were replaced by a modelled tree in the last load.</summary>
    public int TreesGrown => _treesGrown;

    /// <summary>
    /// The same textures and maps, block-compressed, if the pipeline has built them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A fallback rather than a preference, and deliberately so while the enhanced sets are
    /// still being generated. A <c>.dds</c> in <c>build</c> is whatever the last compression
    /// run made of whatever the enhanced set held at the time; the <c>.png</c> beside it is
    /// what the generator has produced <em>now</em>. Taking the compressed one first means
    /// regenerating a texture changes nothing on screen until somebody remembers to
    /// recompress, which is a debugging session nobody enjoys twice.
    /// </para>
    /// <para>
    /// The trade it wins — nothing to decode, a mip chain already built, a quarter of the
    /// video memory — is a shipping concern rather than a development one, and it comes
    /// back the moment the enhanced sets stop moving.
    /// </para>
    /// </remarks>
    public CompressedTextures? Compressed { get; set; }

    /// <summary>Colour only: no normal maps, no finishes, no height, from any source.</summary>
    /// <remarks><c>--flat</c>, for photographing what the maps alone are doing.</remarks>
    public bool FlatSurfaces { get; set; }

    /// <summary>How many times to subdivide a character's head; zero draws it as authored.</summary>
    /// <remarks>
    /// Characters only, and only their heads. See <see cref="Actors.HeadRefinement"/> for
    /// why that is the one part of a GK3 character which can be re-meshed at all.
    /// </remarks>
    public int SmoothHeads { get; set; }

    private int _normalsUsed;
    private int _ormsUsed;
    private int _heightsUsed;
    private int _compressedUsed;

    /// <summary>
    /// Who is looking at what as the scene is built.
    /// </summary>
    /// <remarks>
    /// A glance is applied where an actor is placed, because a character has no skeleton
    /// and turning a head means placing one of its meshes differently. Live glancing —
    /// somebody turning to watch you cross the room — needs an update loop that does not
    /// exist yet; this is the same mechanism, decided once.
    /// </remarks>
    public Glances Glances { get; } = new();

    /// <summary>How many textures came from the enhanced set rather than the archives.</summary>
    public int EnhancedTexturesUsed => _enhancedUsed;

    /// <summary>Loads a scene into geometry.</summary>
    /// <param name="geometry">Where to put it.</param>
    /// <param name="sceneName">Scene name, such as <c>R25</c>.</param>
    /// <param name="timeblock">Time of day: <c>M</c>, <c>A</c>, <c>E</c> or <c>N</c>.</param>
    /// <param name="diagnostics">Receives anything that could not be loaded.</param>
    /// <returns>What was loaded, or null if the scene has no geometry at all.</returns>
    public LoadedScene? Load(
        ISceneSink geometry, string sceneName, string? timeblock, DiagnosticBag diagnostics) =>
        Load(geometry, SceneRequest.For(sceneName, timeblock), diagnostics);

    /// <summary>Loads a scene at a point in the story.</summary>
    /// <param name="geometry">Where to put it.</param>
    /// <param name="request">What to load, and when in the story.</param>
    /// <param name="diagnostics">Receives anything that could not be loaded.</param>
    /// <returns>What was loaded, or null if the scene has no geometry at all.</returns>
    public LoadedScene? Load(ISceneSink geometry, SceneRequest request, DiagnosticBag diagnostics)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(diagnostics);

        string scene = request.Scene;
        string? timeblock = request.AssetSuffix;

        // Where this room's trees are, and nowhere else. A loader is meant to be built per
        // scene, but one that was not would carry the last room's trees into this one and
        // refuse to plant anything near where they stood — in a different room, at
        // coordinates that mean something else entirely.
        _standing.Clear();
        _nearTrees.Clear();

        SceneDefinition init = ReadDefinition(scene, request, diagnostics);
        SceneAssetFile? asset = ReadAsset(scene, timeblock, init, diagnostics);

        string bspName = asset?.BspName ?? scene;

        byte[]? bspBytes = _archives.Read(bspName + ".BSP");
        if (bspBytes is null)
        {
            diagnostics.Add(new Diagnostic(
                "SCENE001", DiagnosticSeverity.Error, $"No archive contains {bspName}.BSP."));

            return null;
        }

        BspFile bsp = BspFile.Parse(bspBytes, bspName + ".BSP");
        _log?.Invoke($"geometry: {bspName}.BSP, {bsp.TriangleCount} triangles, {bsp.Surfaces.Count} surfaces");

        MulFile? lightmaps = ReadLightmaps(asset?.Name, scene, timeblock, diagnostics);

        if (lightmaps is not null && lightmaps.Lightmaps.Count != bsp.Surfaces.Count)
        {
            diagnostics.Add(new Diagnostic(
                "SCENE004",
                DiagnosticSeverity.Warning,
                $"{lightmaps.Name} has {lightmaps.Lightmaps.Count} lightmaps for " +
                $"{bsp.Surfaces.Count} surfaces; the pairing is by index, so the surplus " +
                "or shortfall is unlit."));
        }

        // Which textures are the floor's, before the textures themselves go past. A height
        // map is uploaded and forgotten unless something says it will be wanted as numbers,
        // and only the floor's ever is: that is the one surface displacement touches.
        string? floorObject = init.FloorObject();
        HashSet<string> floorTextures = FloorTextures(bsp, floorObject);

        geometry.KeepRelief(floorTextures);

        if (floorTextures.Count > 0)
        {
            _log?.Invoke(
                $"floor: {floorObject}, {floorTextures.Count} " +
                $"texture{(floorTextures.Count == 1 ? string.Empty : "s")} that can carry relief");
        }

        LoadTextures(geometry, bsp.Surfaces.Select(s => s.TextureName), bspName, diagnostics);

        // Decided before the room is added, because growing a wood means not drawing the
        // cards it replaces, and the cards are hidden by naming them here.
        List<Foliage.FoliageObject> woods = GrowWoods(bsp, diagnostics);
        HashSet<string> hidden = HiddenObjects(init);

        foreach (Foliage.FoliageObject wood in woods)
        {
            hidden.Add(wood.Named);
        }

        geometry.AddScene(bsp, lightmaps, hidden, floorObject);

        // 177 of the game's 229 scene assets name a sky, and which one is already decided
        // by the time of day the timeblock chose.
        if (asset?.Skybox is { IsEmpty: false } sky)
        {
            LoadSkybox(geometry, sky, diagnostics);
        }

        ReportDisputedVisibility(init, diagnostics);

        List<PlacedModel> placed = PlaceModels(geometry, asset, init, diagnostics);

        // After the props, because the two overlap. A room's shadow-caster cards are a
        // second copy of the trees the scene file also places as props - WOD draws ten
        // pines twice, once in `wod_treeshadowcasters` and once as ten `_pineleavesff`
        // models a few units away - and the original drew both, one lit by the bake and one
        // not. Two flat cards in the same place are a slightly thicker tree; two *modelled*
        // trees in the same place are a mess, so the props win and the room's copies of
        // them are left out.
        PlantWoods(geometry, woods, diagnostics);
        placed.AddRange(
            PlaceActors(geometry, init, diagnostics, request.State?.LastLocation));
        _log?.Invoke(
            $"models: {placed.Count} placed, textures: {geometry.TextureCount}" +
            (_enhancedUsed > 0 ? $", {_enhancedUsed} of them enhanced" : string.Empty));

        if (_treesGrown > 0)
        {
            _log?.Invoke($"trees: {_treesGrown} cards grown into modelled trees");
        }

        return new LoadedScene(
            scene,
            init,
            asset,
            lightmaps,
            placed.Count,
            ReadBoundary(init, diagnostics),
            bsp,
            placed,
            ReadActions(init, request, diagnostics),
            init.Soundtracks(),
            ReadSoundtracks(init, diagnostics))
        {
            CameraShell = ReadCameraBounds(init, diagnostics),

            // A sky overhead means the room is outdoors, and outdoors the sun is placed by
            // the story's own clock — over the middle of what was just built, which is why
            // this stands here after AddScene rather than anywhere tidier.
            Bounds = (geometry.Minimum, geometry.Maximum),
            // The state's own clock where there is one; the asset suffix is only "A"
            // for seven different mornings and afternoons, and names no hour.
            Sun = asset is { Skybox.IsEmpty: false } &&
                  (request.State?.Timeblock
                      ?? (Timeblock.TryParse(timeblock, out Timeblock parsed)
                          ? parsed
                          : (Timeblock?)null)) is { } when
                ? Sunlight.For(when, (geometry.Minimum + geometry.Maximum) / 2f)
                : null,
        };
    }

    /// <summary>
    /// Assembles what a scene <em>is</em>, without loading anything that has to be drawn.
    /// </summary>
    /// <param name="request">Which scene, and where the story is.</param>
    /// <param name="diagnostics">Receives loading diagnostics.</param>
    /// <returns>The scene, or null if it has no initialisation file at all.</returns>
    /// <remarks>
    /// The composition — which state the room is in, who is in it, where they may stand,
    /// what may be done to them — is decided entirely by text files, and answering
    /// questions about it does not need the fifty megabytes of geometry and the hundred
    /// textures that go with drawing it. A sweep of the whole corpus is the case that
    /// makes the difference worth having: 1,343 pairs at a few milliseconds each rather
    /// than at a second each.
    /// </remarks>
    public LoadedScene? Compose(SceneRequest request, DiagnosticBag diagnostics)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(diagnostics);

        SceneDefinition init = ReadDefinition(request.Scene, request, diagnostics);

        if (init.IsEmpty)
        {
            return null;
        }

        return new LoadedScene(
            request.Scene,
            init,
            ReadAsset(request.Scene, request.AssetSuffix, init, diagnostics),
            Lightmaps: null,
            ModelsPlaced: 0,
            ReadBoundary(init, diagnostics),
            Geometry: null,
            Placed: null,
            ReadActions(init, request, diagnostics),
            init.Soundtracks(),
            ReadSoundtracks(init, diagnostics));
    }

    /// <summary>Builds a camera from one of a scene's own viewpoints.</summary>
    /// <param name="scene">The loaded scene.</param>
    /// <param name="geometry">Its geometry, for a fallback framing.</param>
    /// <param name="cameraName">Which camera, or null for the scene's default.</param>
    /// <returns>The camera.</returns>
    public static Camera CameraFor(LoadedScene? scene, ISceneSink geometry, string? cameraName = null)
    {
        ArgumentNullException.ThrowIfNull(geometry);

        SceneCamera? chosen = scene?.CameraNamed(cameraName);

        return chosen is null
            ? Camera.Framing(geometry.Minimum, geometry.Maximum, Vector3.UnitY)
            : CameraAt(chosen, geometry);
    }

    /// <summary>Builds the view one of the scene's cameras describes.</summary>
    /// <param name="chosen">Where it stands and which way it points.</param>
    /// <param name="geometry">The room, for how far the far plane has to reach.</param>
    /// <returns>The view.</returns>
    /// <remarks>
    /// Separate from the lookup because not every camera in a scene has a name: the
    /// close-up views are keyed by what they look at rather than called anything.
    /// </remarks>
    public static Camera CameraAt(SceneCamera chosen, ISceneSink geometry)
    {
        ArgumentNullException.ThrowIfNull(chosen);
        ArgumentNullException.ThrowIfNull(geometry);

        float reach = MathF.Max(1f, (geometry.Maximum - geometry.Minimum).Length());

        return new Camera
        {
            Position = chosen.Position,
            Target = chosen.Position + chosen.Forward,
            Up = Vector3.UnitY,

            // The original renders at a 60 degree vertical field of view on a 4:3 screen.
            FieldOfView = MathF.PI / 3f,
            NearPlane = 1f,
            FarPlane = reach * 4f,
        };
    }

    /// <summary>Objects baked into the geometry that must not be drawn.</summary>
    /// <remarks>
    /// Hit tests are volumes the player can click but never see — a doorway's clickable
    /// region, the area a note occupies on a desk. They are ordinary geometry inside the
    /// BSP with an ordinary texture, so nothing about the geometry itself says to skip
    /// them; only the initialisation file does. Drawing them puts large flat slabs through
    /// the middle of a room, which is exactly what the lobby showed before this.
    /// </remarks>
    private static HashSet<string> HiddenObjects(SceneDefinition init)
    {
        return init.Models()
            .Where(m => IsHitTest(m) || (IsBakedIn(m) && m.Hidden))
            .Select(m => m.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The textures on the object the scene calls its floor.</summary>
    /// <remarks>
    /// Not a guess and not a matter of what a texture is called. Every scene's general
    /// <c>.SIF</c> names one <c>floor=</c> object, the BSP knows which surfaces belong to
    /// it, and each surface names its texture — the same chain the walk height query
    /// follows. Sixty-nine scenes name a floor and a hundred and twenty-six distinct
    /// textures are on one; <c>TE3FLOORCRS</c> is a floor and <c>27FLOOR</c> is not.
    /// </remarks>
    private static HashSet<string> FloorTextures(BspFile scene, string? floorObject)
    {
        var textures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(floorObject))
        {
            return textures;
        }

        int wanted = -1;

        for (int i = 0; i < scene.ObjectNames.Count; i++)
        {
            if (string.Equals(
                    scene.ObjectNames[i], floorObject, StringComparison.OrdinalIgnoreCase))
            {
                wanted = i;
                break;
            }
        }

        if (wanted < 0)
        {
            return textures;
        }

        foreach (BspSurface surface in scene.Surfaces)
        {
            if (surface.ObjectIndex == wanted)
            {
                textures.Add(surface.TextureName);
            }
        }

        return textures;
    }

    /// <summary>Notes the models drawn only because their condition could not be decided.</summary>
    /// <remarks>
    /// These are shown rather than hidden, so a wrong guess adds an object instead of
    /// removing one. Naming them is what makes the guess reviewable: without this the only
    /// evidence is an object that looks out of place, which is hard to trace back here.
    /// </remarks>
    private static void ReportDisputedVisibility(SceneDefinition init, DiagnosticBag diagnostics)
    {
        if (init.ConditionsResolved)
        {
            return;
        }

        foreach (SceneModel model in init.Models().Where(m => m.VisibilityDisputed && !m.Hidden))
        {
            diagnostics.Add(new Diagnostic(
                "SCENE009",
                DiagnosticSeverity.Info,
                $"{model.Name} is hidden in one conditional block and shown in another; " +
                "it is drawn, because the conditions need the Sheep virtual machine."));
        }
    }

    /// <summary>
    /// Reads a behaviour script — what something does when nobody is asking it to.
    /// </summary>
    /// <param name="named">The script's file name, or null.</param>
    /// <param name="owner">What it belongs to, for a diagnostic.</param>
    /// <param name="diagnostics">Receives what could not be read.</param>
    /// <returns>The script, or null when there is none or it is missing.</returns>
    /// <remarks>
    /// <para>
    /// A script whose every instruction is not understood is <em>kept</em> now. It did not
    /// used to be, and the reason was good at the time: the branching half of the language
    /// decides which idle to play, so running only the parts that were understood picked
    /// the wrong one and repeated it for as long as the scene was loaded.
    /// </para>
    /// <para>
    /// That half is understood now — <c>ONEOF</c> above all, which is 1,559 of the corpus's
    /// instructions — so what is left unread is the perception layer: <c>WHENNEAR</c> and
    /// its relatives, which add a way for a script to be interrupted rather than deciding
    /// what it does. A script missing those does the right things and misses a cue, which
    /// is much better than a character standing still.
    /// </para>
    /// </remarks>
    private GasFile? ReadBehaviour(string? named, string owner, DiagnosticBag diagnostics)
    {
        if (named is not { Length: > 0 })
        {
            return null;
        }

        if (_archives.Read(named) is not { } bytes)
        {
            diagnostics.Add(new Diagnostic(
                "GK3R3330", DiagnosticSeverity.Info,
                "Something names a behaviour script no archive contains.",
                owner, null, named, "nothing",
                "It is placed and stands still."));

            return null;
        }

        GasFile script = GasFile.Parse(bytes);

        if (!script.Complete)
        {
            diagnostics.Add(new Diagnostic(
                "GK3R3331", DiagnosticSeverity.Info,
                "A behaviour script uses instructions this engine does not run yet.",
                named, null, "instructions the player can run",
                string.Join(", ", script.Unsupported),
                "Everything else in it runs; those lines are skipped."));
        }

        return script;
    }

    private static bool IsHitTest(SceneModel model) =>
        string.Equals(model.Type, "hittest", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a model refers to geometry inside the BSP rather than a file.</summary>
    /// <remarks>
    /// Only <c>prop</c> and <c>gasprop</c> load a model file; everything else names an
    /// object the geometry already contains. Loading a file for those draws the same
    /// furniture twice, in slightly different places, which reads as z-fighting rather
    /// than as a loading mistake.
    /// </remarks>
    private static bool IsBakedIn(SceneModel model) =>
        !string.Equals(model.Type, "prop", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(model.Type, "gasprop", StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads the bitmap that says where actors may stand.</summary>
    private WalkBoundary? ReadBoundary(SceneDefinition init, DiagnosticBag diagnostics)
    {
        if (init.Boundary() is not { } declared)
        {
            return null;
        }

        byte[]? bitmap = _archives.Read(declared.Texture + ".BMP");
        if (bitmap is null)
        {
            diagnostics.Add(new Diagnostic(
                "SCENE012",
                DiagnosticSeverity.Warning,
                $"The scene's walk boundary is {declared.Texture}.BMP, which no archive " +
                "contains; nothing constrains where actors may stand."));

            return null;
        }

        WalkBoundary? boundary = WalkBoundary.From(
            bitmap, declared.Texture + ".BMP", declared.Size, declared.Offset);

        if (boundary is not null)
        {
            _log?.Invoke(
                $"walkable: {declared.Texture}, {boundary.Width}x{boundary.Height} over " +
                $"{declared.Size.X:F0}x{declared.Size.Y:F0} units, " +
                $"{boundary.WalkableTexels()} of {boundary.Width * boundary.Height} texels open");
        }

        return boundary;
    }

    /// <summary>Reads the shells that fence the camera into the room.</summary>
    /// <remarks>
    /// A scene that names none, or names one no archive holds, gets no bounds and a camera
    /// that can go anywhere — which is what every scene did before this and is a great deal
    /// better than a room the camera cannot move in. The names are model files rather than
    /// objects in the geometry: nothing draws them, and only this reads them.
    /// </remarks>
    private CameraBounds? ReadCameraBounds(SceneDefinition init, DiagnosticBag diagnostics)
    {
        IReadOnlyList<string> named = init.CameraBounds();

        if (named.Count == 0)
        {
            return null;
        }

        List<ModFile> shells = [];

        foreach (string name in named)
        {
            byte[]? bytes = _archives.Read(name + ".MOD");

            if (bytes is null)
            {
                diagnostics.Add(new Diagnostic(
                    "SCENE013",
                    DiagnosticSeverity.Warning,
                    $"The scene's camera bounds are {name}.MOD, which no archive contains; " +
                    "nothing keeps the camera inside the room."));

                continue;
            }

            shells.Add(ModFile.Parse(bytes, name + ".MOD"));
        }

        if (shells.Count == 0)
        {
            return null;
        }

        var bounds = new CameraBounds(shells);

        _log?.Invoke(
            $"camera bounds: {string.Join(", ", named)}, {bounds.TriangleCount} triangles");

        return bounds.IsEmpty ? null : bounds;
    }

    /// <summary>Reads the one or two initialisation files that describe a scene.</summary>
    private SceneDefinition ReadDefinition(
        string scene, SceneRequest request, DiagnosticBag diagnostics)
    {
        SceneInitFile? general = ReadInit(scene, request, diagnostics, required: true);

        // The timeblock file is where the story lives: the actors present, the props they
        // are holding, the cameras the conversation cuts between. Most location and
        // timeblock pairs have none, and that is not a problem worth reporting.
        SceneInitFile? specific = request.TimeblockCode is { Length: > 0 } code
            ? ReadInit(scene + code, request, diagnostics, required: false)
            : null;

        var definition = new SceneDefinition(general, specific);

        _log?.Invoke(
            $"init: {Named(general)}{(specific is null ? string.Empty : " + " + Named(specific))}, " +
            $"{definition.RoomCameras().Count} room cameras, {definition.Models().Count} models, " +
            $"{definition.Actors().Count} actors" +
            (definition.ConditionsResolved ? " for this point in the story" : string.Empty));

        return definition;

        static string Named(SceneInitFile? file) => file?.Name ?? "no SIF";
    }

    private SceneInitFile? ReadInit(
        string name, SceneRequest request, DiagnosticBag diagnostics, bool required)
    {
        string? text = _archives.ReadText(name + ".SIF");
        if (text is null)
        {
            if (required)
            {
                diagnostics.Add(new Diagnostic(
                    "SCENE002",
                    DiagnosticSeverity.Warning,
                    $"No {name}.SIF; the scene has no cameras of its own."));
            }

            return null;
        }

        return request.Conditions is null
            ? SceneInitFile.Parse(text, name + ".SIF")
            : SceneInitFile.Parse(text, name + ".SIF", request.Conditions.Applies);
    }

    private SceneAssetFile? ReadAsset(
        string scene, string? timeblock, SceneDefinition init, DiagnosticBag diagnostics)
    {
        string? declared = init.SceneAsset();

        foreach (string candidate in Candidates(scene, timeblock, declared))
        {
            string? text = _archives.ReadText(candidate + ".SCN");
            if (text is not null)
            {
                SceneAssetFile asset = SceneAssetFile.Parse(text, candidate + ".SCN");
                _log?.Invoke($"asset: {asset.Name}, bsp {asset.BspName}, " +
                             $"{asset.Models.Count} objects, {asset.Lights.Count} lights");

                return asset;
            }
        }

        diagnostics.Add(new Diagnostic(
            "SCENE003",
            DiagnosticSeverity.Warning,
            $"No scene asset for {scene}; taking the BSP of the same name."));

        return null;
    }

    private MulFile? ReadLightmaps(
        string? assetName, string scene, string? timeblock, DiagnosticBag diagnostics)
    {
        // Lightmaps are named after the scene asset, not the BSP: several timeblocks share
        // one BSP and differ only in their bake.
        string? preferred = assetName is null ? null : Path.GetFileNameWithoutExtension(assetName);

        foreach (string candidate in Candidates(scene, timeblock, preferred))
        {
            byte[]? bytes = _archives.Read(candidate + ".MUL");
            if (bytes is not null)
            {
                MulFile lightmaps = MulFile.Parse(bytes, candidate + ".MUL");
                _log?.Invoke($"lightmaps: {lightmaps.Name}, {lightmaps.Lightmaps.Count} maps, " +
                             $"{lightmaps.TotalPixels} texels");

                return lightmaps;
            }
        }

        diagnostics.Add(new Diagnostic(
            "SCENE005",
            DiagnosticSeverity.Warning,
            $"No lightmaps for {scene}; it renders with directional shading instead."));

        return null;
    }

    /// <summary>Names to try, most specific first.</summary>
    private static IEnumerable<string> Candidates(string scene, string? timeblock, string? declared)
    {
        if (timeblock is not null)
        {
            yield return $"{scene}_{timeblock.ToUpperInvariant()}";
        }

        if (!string.IsNullOrEmpty(declared))
        {
            yield return declared;
        }

        foreach (string suffix in TimeblockSuffixes)
        {
            yield return scene + suffix;
        }
    }

    private List<PlacedModel> PlaceModels(
        ISceneSink geometry, SceneAssetFile? asset, SceneDefinition init, DiagnosticBag diagnostics)
    {
        IReadOnlyList<SceneModel> declared = init.Models();
        List<PlacedModel> placed = [];

        foreach (SceneModel model in declared)
        {
            if (IsBakedIn(model))
            {
                continue;
            }

            byte[]? bytes = _archives.Read(model.Name + ".MOD");
            if (bytes is null)
            {
                diagnostics.Add(new Diagnostic(
                    "SCENE006",
                    DiagnosticSeverity.Warning,
                    $"The scene places {model.Name}, which no archive contains."));

                continue;
            }

            ModFile parsed = ModFile.Parse(bytes, model.Name + ".MOD");
            Matrix4x4 standing = Matrix4x4.Identity;

            // A flat tree becomes a modelled one here, before anything else is decided
            // about it. Everything downstream — the noun the player clicks, whether the
            // scene starts it hidden, the script that shows it again — is about the
            // placement rather than about the shape, so a tree that grew is still the
            // same prop under the same name.
            if (GrowTree(parsed, diagnostics) is { } grown)
            {
                parsed = grown.Model;
                standing = grown.Standing;
                _treesGrown++;
            }

            LoadTextures(
                geometry,
                parsed.Meshes.SelectMany(m => m.Submeshes).Select(s => s.TextureName),
                model.Name,
                diagnostics);

            // A model the scene declares hidden is loaded and placed all the same, and
            // then not drawn. It has to be: the story brings it out with ShowModel, and
            // RC1's moped — which waits out of sight for the scripted moment it rides
            // past the hotel — was never loaded at all, so the show did nothing and the
            // player heard Gabriel remark on a bike that was not there.
            ModelPlacement placement = geometry.Add(
                parsed, standing.IsIdentity ? null : standing);

            if (model.Hidden)
            {
                geometry.SetVisible(placement, false);
            }

            placed.Add(new PlacedModel(
                model.Name,
                model.Noun,
                model.Verb,
                parsed,
                standing,
                PlacedModelKind.Prop,
                placement)
            {
                Stage = geometry,
                Gas = model.Gas,
                Idle = ReadBehaviour(model.Gas, model.Name, diagnostics),
                Visible = !model.Hidden,
                InitialAnimation = model.InitialAnimation,
            });
        }

        return placed;
    }

    /// <summary>
    /// How many triangles of grown wood one room may be given.
    /// </summary>
    /// <remarks>
    /// A cap rather than a target, and a guard rather than a constraint: no room in the
    /// corpus comes near it now that a card is counted as the face an artist drew rather
    /// than as the pieces a BSP splitter left. It is kept because the arithmetic that
    /// motivated it is still true — a stand of a hundred and sixty trees at four thousand
    /// triangles each is six hundred thousand triangles of scenery behind a conversation, in
    /// a room that shipped at six — and because a scene nobody has looked at yet should not
    /// be able to spend that. Every stand is grown at the far detail first, which is a
    /// quarter of the cost, and the budget left over is spent raising the tallest trees to
    /// full.
    /// </remarks>
    private const int WoodBudget = 400_000;

    /// <summary>Finds the stands of trees in a room, as far as the budget reaches.</summary>
    /// <param name="scene">The parsed room.</param>
    /// <param name="diagnostics">Receives a warning for any grown tree that will not load.</param>
    /// <returns>The objects whose cards are to be replaced, largest first.</returns>
    /// <remarks>
    /// All of an object or none of it. A room is hidden by name and there is no way to hide
    /// half of one, so growing part of a stand and leaving the rest would draw the modelled
    /// trees over the cards they were meant to replace.
    /// </remarks>
    private List<Foliage.FoliageObject> GrowWoods(BspFile scene, DiagnosticBag diagnostics)
    {
        if (Trees is not { IsEmpty: false } library)
        {
            return [];
        }

        List<Foliage.FoliageObject> afforded = [];
        int spent = 0;
        int refused = 0;
        int unreadable = 0;

        foreach (Foliage.FoliageObject wood in Foliage.InGeometry(scene, library))
        {
            int cheapest = wood.Sites.Sum(
                s => TreeLibrary.Variant(s.Species, s.Seed, far: true).Triangles);

            if (spent + cheapest > WoodBudget)
            {
                refused += wood.Sites.Count;
                continue;
            }

            // Read before the object is committed to, not while planting it. Hiding a
            // room's cards and then finding the geometry that was to replace them will not
            // load leaves a hole in the hillside, which is worse than the flat trees this
            // set out to remove. Reads are cached, so this costs nothing twice.
            if (!Readable(wood, library, diagnostics))
            {
                unreadable += wood.Sites.Count;
                continue;
            }

            spent += cheapest;
            afforded.Add(wood);
        }

        if (unreadable > 0)
        {
            _log?.Invoke(
                $"trees: {unreadable} left flat; the grown trees for them will not load");
        }

        // What is left after every stand is standing, spent on the tallest trees across all
        // of them. Tallest rather than nearest, because there is no camera yet and height is
        // the only thing in the data that says which tree a room is about.
        _nearTrees.Clear();
        int left = WoodBudget - spent;

        foreach (TreeSite site in afforded
                     .SelectMany(w => w.Sites)
                     .OrderByDescending(s => s.Height))
        {
            int upgrade = TreeLibrary.Variant(site.Species, site.Seed).Triangles
                - TreeLibrary.Variant(site.Species, site.Seed, far: true).Triangles;

            if (upgrade > left)
            {
                break;
            }

            left -= upgrade;
            _nearTrees.Add(site.Seed);
        }

        if (refused > 0)
        {
            // Said out loud, because a silent cap reads as "the corpus has no more foliage
            // in it" when what happened is that this room had more than it could afford.
            _log?.Invoke(
                $"trees: {refused} left flat, over the {WoodBudget:N0}-triangle budget");
        }

        return afforded;
    }

    /// <summary>Whether every tree a stand needs can actually be loaded.</summary>
    /// <remarks>
    /// Both details, because the budget decides between them after this and either may be
    /// asked for. A stand is all or nothing: the room hides its cards by object name, so one
    /// tree that will not read costs the whole object rather than one trunk.
    /// </remarks>
    private static bool Readable(
        Foliage.FoliageObject wood, TreeLibrary library, DiagnosticBag diagnostics)
    {
        foreach (TreeSite site in wood.Sites)
        {
            if (library.Read(TreeLibrary.Variant(site.Species, site.Seed, far: true),
                    diagnostics) is null ||
                library.Read(TreeLibrary.Variant(site.Species, site.Seed), diagnostics) is null)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Stands the grown trees where a room's cards were.</summary>
    private void PlantWoods(
        ISceneSink geometry,
        List<Foliage.FoliageObject> woods,
        DiagnosticBag diagnostics)
    {
        if (Trees is not { } library || woods.Count == 0)
        {
            return;
        }

        int planted = 0;
        int full = 0;
        int cards = 0;
        int doubled = 0;

        foreach (Foliage.FoliageObject wood in woods)
        {
            foreach (TreeSite site in wood.Sites)
            {
                if (AlreadyStanding(site))
                {
                    doubled++;
                    continue;
                }

                bool near = _nearTrees.Contains(site.Seed);
                GrownTree chosen = TreeLibrary.Variant(site.Species, site.Seed, far: !near);

                if (library.Read(chosen, diagnostics) is not { } grown)
                {
                    continue;
                }

                LoadTextures(
                    geometry,
                    grown.Meshes.SelectMany(m => m.Submeshes).Select(s => s.TextureName),
                    chosen.Name,
                    diagnostics);

                geometry.Add(grown, Foliage.Standing(site, chosen));
                planted++;

                if (near)
                {
                    full++;
                }
            }

            cards += wood.Cards;
        }

        if (planted > 0)
        {
            _treesGrown += planted;
            _log?.Invoke(
                $"trees: {planted} grown over {cards} cards in {woods.Count} of the " +
                $"room's own objects, {full} of them at full detail" +
                (doubled > 0 ? $", {doubled} left to the props standing on them" : string.Empty));
        }
    }

    /// <summary>Whether a prop has already grown a tree where this site is.</summary>
    /// <remarks>
    /// <para>
    /// A third of a crown's radius, which is much tighter than it sounds and is a measured
    /// number rather than a cautious one. Where a scene places a foliage prop, the room's
    /// own copy of that tree is <b>within twenty-two units of it and usually within five</b>
    /// — measured across WOD's eighteen pines, whose crowns are two hundred units across.
    /// So the duplicates are unambiguous, and anything further away is a different tree.
    /// </para>
    /// <para>
    /// The looser rule this replaced took a whole radius, which suppressed 81 of WOD's 87
    /// stands to remove 18 duplicates: the hillside behind the eighteen props went with
    /// them, and the wood came out as a clearing.
    /// </para>
    /// </remarks>
    private bool AlreadyStanding(TreeSite site)
    {
        foreach ((System.Numerics.Vector3 foot, float radius) in _standing)
        {
            float apart = System.Numerics.Vector2.Distance(
                new System.Numerics.Vector2(foot.X, foot.Z),
                new System.Numerics.Vector2(site.Foot.X, site.Foot.Z));

            if (apart < MathF.Max(MathF.Min(radius, site.Radius) * 0.35f, 10f) &&
                MathF.Abs(foot.Y - site.Foot.Y) < site.Height)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Grows a modelled tree in place of a prop that is only a picture of one.
    /// </summary>
    /// <param name="card">The prop as the archive holds it.</param>
    /// <param name="diagnostics">Receives a warning when a grown tree will not read.</param>
    /// <returns>The tree and where it stands, or null when this prop stays as it is.</returns>
    /// <remarks>
    /// <para>
    /// Null is the ordinary answer and costs one dictionary lookup per submesh: no tree
    /// library, no foliage texture, or a prop that is a tree and something else all leave
    /// the card alone. That matters because this runs over every prop in every scene, and
    /// most scenes are indoors.
    /// </para>
    /// <para>
    /// A tree that will not read leaves the card too, rather than leaving a gap. Enhanced
    /// content is a draft until somebody has looked at it, and one bad file in a set should
    /// cost that tree and nothing else.
    /// </para>
    /// </remarks>
    private (ModFile Model, Matrix4x4 Standing)? GrowTree(
        ModFile card, DiagnosticBag diagnostics)
    {
        if (Trees is not { IsEmpty: false } library ||
            Foliage.SiteFor(card, library) is not { } site)
        {
            return null;
        }

        GrownTree chosen = TreeLibrary.Variant(site.Species, site.Seed);

        if (library.Read(chosen, diagnostics) is not { } grown)
        {
            return null;
        }

        _standing.Add((site.Foot, site.Radius));
        return (grown, Foliage.Standing(site, chosen));
    }

    /// <summary>Puts the scene's actors where the scene says they stand.</summary>
    /// <remarks>
    /// <para>
    /// The models are in their bind pose. Actors are animated by GAS scripts driving ACT
    /// animations against a skeleton, none of which exists yet, so an actor standing here
    /// is standing exactly as the artist modelled them rather than idling.
    /// </para>
    /// <para>
    /// <b>Everyone the section names is loaded</b>, whatever else the line says, and the
    /// two exceptions this used to make were the same mistake twice — the one already
    /// recorded above about RC1's moped, made again about people.
    /// </para>
    /// <para>
    /// An actor with no <c>pos=</c> was being skipped outright. 206 actor/timeblock pairs
    /// in the corpus have none, and they are not absent: <c>GKActor::Init</c> only declines
    /// to <em>set</em> a position, and what places them is their <c>initanim=</c> or the
    /// script that walks them in. Emilio is one of them in the lobby at 110A, so the room's
    /// only other person was never there — and when the story sent him out through the
    /// front door, all that arrived in the square was a door swinging by itself.
    /// </para>
    /// <para>
    /// An actor declared <c>hidden</c> was being skipped too, and hidden is where several
    /// of them start: RC1 hides Emilio while he is still indoors and the animation that
    /// walks him out turns him back on. A model that was never read cannot be shown.
    /// </para>
    /// </remarks>
    private List<PlacedModel> PlaceActors(
        ISceneSink geometry,
        SceneDefinition init,
        DiagnosticBag diagnostics,
        string? from = null)
    {
        List<PlacedModel> placed = [];

        foreach (SceneActor actor in init.Actors())
        {
            // Ego arrives at the scene's entry point; everyone else stands where their own
            // line says.
            ScenePosition? spot = actor.IsEgo
                ? init.PositionNamed(actor.Position) ?? init.StartPosition(from)
                : init.PositionNamed(actor.Position);

            if (spot is null && actor.Position is { Length: > 0 })
            {
                // Named a spot the scene does not define. It happens once in the game —
                // the dining room says Mosely stands at MOSTALK and defines TALK_MOSELY —
                // and it is a typo in the shipped data rather than anything this can fix.
                //
                // <b>The actor is still in the room.</b> The original only skips setting
                // the position (see GKActor::Init) and leaves everything else alone, and
                // that matters far more than where they end up standing: the room's entry
                // script calls SetActorLocation on Mosely and then StopFidget, and the
                // dialogue that follows is all addressed to him. Leaving him out of the
                // scene took the whole coffee scene with him.
                diagnostics.Add(new Diagnostic(
                    "SCENE011",
                    DiagnosticSeverity.Warning,
                    $"{actor.Name} is placed at '{actor.Position}', which the scene does " +
                    "not define; they stand at the origin until something moves them."));
            }

            if (spot is null && actor.IsEgo)
            {
                diagnostics.Add(new Diagnostic(
                    "SCENE011",
                    DiagnosticSeverity.Info,
                    $"{actor.Name} is the player and this scene names no spot to arrive at " +
                    $"from {(from is { Length: > 0 } ? from : "nowhere in particular")}; " +
                    "they stand at the origin until the room's own script places them."));
            }

            byte[]? bytes = _archives.Read(actor.Name + ".MOD");
            if (bytes is null)
            {
                diagnostics.Add(new Diagnostic(
                    "SCENE008",
                    DiagnosticSeverity.Warning,
                    $"The scene places {actor.Name}, which no archive contains."));

                continue;
            }

            ModFile parsedActor = ModFile.Parse(bytes, actor.Name + ".MOD");

            // Before the textures are read and before the model is placed, because both work
            // from the geometry and only one of the two versions should reach either. The rig
            // that comes back is what keeps the clips playable; see HeadRefinement.
            (ModFile model, Actors.HeadRig? head) =
                Actors.HeadRefinement.Apply(parsedActor, SmoothHeads);

            LoadTextures(
                geometry,
                model.Meshes.SelectMany(m => m.Submeshes).Select(s => s.TextureName),
                actor.Name,
                diagnostics);

            // Which way this character's model is built to face, out of the invisible
            // arrow the game ships beside it. Everything that turns them uses it in place of
            // the half turn most models happen to want; see Actors.FacingArrow.
            float? built = _archives.Read(Actors.FacingArrow.NameFor(actor.Name) + ".MOD") is
                { } arrowBytes
                ? Actors.FacingArrow.Of(
                    ModFile.Parse(arrowBytes, Actors.FacingArrow.NameFor(actor.Name) + ".MOD"),
                    actor.Name)
                : null;

            // Heading turns about the up axis; the model's own origin is at its feet, so
            // the position needs no vertical adjustment. An actor with no spot stands at
            // the origin facing zero, which is what the original leaves them at.
            Matrix4x4 placement = spot is null
                ? Matrix4x4.Identity
                : Matrix4x4.CreateRotationY(Actors.FacingArrow.Rotation(spot.Heading, built)) *
                  Matrix4x4.CreateTranslation(spot.Position);

            ModelPlacement standing =
                geometry.Add(model, placement, TurnedHead(actor.Name, model, spot));

            if (actor.Hidden)
            {
                geometry.SetVisible(standing, false);
            }

            _log?.Invoke(
                $"actor: {actor.Name} ({actor.Noun}) at {spot?.Name ?? "no spot of their own"}" +
                (actor.IsEgo ? ", ego" : string.Empty) +
                (actor.Hidden ? ", hidden" : string.Empty));

            placed.Add(new PlacedModel(
                actor.Name, actor.Noun, null, model, placement, PlacedModelKind.Actor, standing)
            {
                // Null unless the head was actually refined, which is what tells the clip
                // playback whether to shape the head's vertices or to fit them.
                Head = head,

                // Which way this model is built to face, so that turning it is a difference
                // rather than an assumption. See Actors.FacingArrow.
                BuiltFacing = built,

                // Where they are now comes from the sink, because walking moves them
                // there and nothing writes it back to the placement above.
                Stage = geometry,

                // What they do when nobody is telling them to do anything, while they
                // speak, and while somebody else does. A scene names all three per actor.
                Gas = actor.Idle,
                Idle = ReadBehaviour(actor.Idle, actor.Name, diagnostics),
                Talk = ReadBehaviour(actor.Talk, actor.Name, diagnostics),
                Listen = ReadBehaviour(actor.Listen, actor.Name, diagnostics),

                // The pose the scene opens them in, applied once the animation libraries
                // exist. It is a statement about where they are rather than something that
                // happens, so it is sampled and not played; see SceneUpdate.Open.
                InitialAnimation = actor.InitialAnimation,

                Visible = !actor.Hidden,
            });
        }

        return placed;
    }

    /// <summary>The cube map's sides, in the order the hardware wants them.</summary>
    /// <remarks>
    /// <b>Front is +X and right is +Z</b>, not the other way about. Measured off the images
    /// rather than reasoned from the names, twice and independently. Butting each side's
    /// right-hand column against each other side's left-hand column, the four that join are
    /// left→back→right→front, with a mean difference of 2.9 to 6.1 against 23 to 34 for
    /// every other pairing. Butting each side's top row against the four edges of the up
    /// face agrees: front meets +X, right meets +Z, back meets −X and left meets −Z, at 2.9
    /// to 3.2 against 25 to 48.
    /// </remarks>
    private static readonly string[] Sides = ["front", "back", "up", "down", "right", "left"];

    /// <summary>
    /// Gives the room its sky, when the scene asset names one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The six sides go to the device in the order the hardware wants — right, left, up,
    /// down, front, back — which is not the order the file lists them in.
    /// </para>
    /// <para>
    /// A missing side is filled with one that is present, as the original does: the ground
    /// is usually left out because nothing can see it, but the hardware still requires six.
    /// A sky whose sides are different sizes is refused rather than resampled, because
    /// nothing in the corpus has one and guessing would hide a misreading.
    /// </para>
    /// </remarks>
    private void LoadSkybox(ISceneSink geometry, SkyboxDefinition sky, DiagnosticBag diagnostics)
    {
        string?[] named = [sky.Front, sky.Back, sky.Up, sky.Down, sky.Right, sky.Left];
        DecodedImage?[] read = new DecodedImage?[6];


        DecodedImage? any = null;

        for (int face = 0; face < named.Length; face++)
        {
            if (named[face] is not { Length: > 0 } texture)
            {
                continue;
            }

            byte[]? bytes = _archives.Read(texture) ?? _archives.Read(texture + ".BMP");

            if (bytes is null || !BitmapDecoder.CanDecode(bytes))
            {
                continue;
            }

            read[face] = BitmapDecoder.Decode(bytes, texture);
            any ??= read[face];
        }

        if (any is not { } fallback)
        {
            diagnostics.Add(new Diagnostic(
                "SCENE020", DiagnosticSeverity.Warning,
                "A scene names a sky whose textures are not in the archives.",
                sky.Up ?? sky.Front ?? "skybox", null, "at least one readable side", "none",
                "The room will draw against an empty background."));

            return;
        }

        DecodedImage[] faces = new DecodedImage[6];

        for (int face = 0; face < faces.Length; face++)
        {
            faces[face] = read[face] ?? fallback;

            if (faces[face].Width == fallback.Width && faces[face].Height == fallback.Width)
            {
                continue;
            }

            diagnostics.Add(new Diagnostic(
                "SCENE021", DiagnosticSeverity.Warning,
                "A sky's sides are not all square and the same size, so it cannot be built.",
                named[face] ?? "skybox", null,
                $"{fallback.Width} by {fallback.Width}",
                $"{faces[face].Width} by {faces[face].Height}",
                "Every side of a cube map must match."));

            return;
        }

        geometry.SetSkybox(faces, sky.Azimuth);

        _log?.Invoke(string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"skybox: {faces[0].Width}px, turned {sky.Azimuth * 180 / MathF.PI:F0} degrees, " +
            $"sides {string.Join(", ", named.Select((n, i) => read[i] is null ? $"{Sides[i]}=none" : Sides[i]))}"));
    }

    /// <summary>How many surfaces in the last scene were given a normal map.</summary>
    public int NormalMapsUsed => _normalsUsed;

    /// <summary>How many were given an occlusion/roughness/metalness map.</summary>
    public int OrmMapsUsed => _ormsUsed;

    /// <summary>How many were given a height map.</summary>
    public int HeightMapsUsed => _heightsUsed;

    /// <summary>How many of the last scene's textures came from the compressed set.</summary>
    public int CompressedUsed => _compressedUsed;

    /// <summary>Where a texture came from, for the counts a load reports.</summary>
    private enum Source
    {
        /// <summary>The archives, as the game shipped.</summary>
        Original,

        /// <summary>A higher-resolution PNG.</summary>
        Enhanced,

        /// <summary>A block-compressed DDS.</summary>
        Compressed,
    }

    /// <summary>How many textures are decoded at once.</summary>
    /// <remarks>
    /// Bounded well below the core count on purpose. Each decode in flight holds about
    /// 33 MB — the compressed file, the inflated rows and the pixels — so the ceiling here
    /// is what the load costs in memory at its peak, and past a point the machine is waiting
    /// on memory bandwidth rather than on arithmetic.
    /// </remarks>
    private static int Decoders => Math.Max(1, Environment.ProcessorCount);

    /// <summary>Reads, decodes and uploads the textures a room asks for.</summary>
    /// <remarks>
    /// <para>
    /// In three passes rather than one, because the middle one is worth spreading over the
    /// machine. Deciding what is missing has to be in order — asking the sink what it holds
    /// is not something two threads may do at once, and the answer counts what was reused —
    /// and uploading has to be in order because that is the device. Decoding is neither: it
    /// is pure arithmetic over bytes nobody else is looking at.
    /// </para>
    /// <para>
    /// It is also nearly all of the time. An enhanced texture is 2048², which is 48 ms and
    /// 33 MB of decode apiece, and a room wants dozens of them with a normal map each; done
    /// one after another that is ten seconds of a scene load with thirty-one cores idle.
    /// </para>
    /// </remarks>
    private void LoadTextures(
        ISceneSink geometry, IEnumerable<string> names, string owner, DiagnosticBag diagnostics)
    {
        var wanted = new List<(string Name, bool Normal, bool Orm, bool Height, bool Colour)>();

        foreach (string texture in names
                     .Where(n => n.Length > 0)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            // A generated normal map for this surface, if there is one. 324 of the game's
            // 6,657 textures have one so far and the rest look exactly as they did — a
            // partial set is a perfectly good set.
            // From either source. These used to require the loose enhanced directory to
            // exist before the packs were even asked, which with packs present — the
            // shipped arrangement, where loose content is deliberately ignored — turned
            // every normal map, every surface finish and every height map in the game off.
            // Displacement, parallax and the material response all read as flat, because
            // they were: DisplacedTriangles was nought in every room.
            bool normal = !FlatSurfaces &&
                          (Normals ?? (object?)Compressed) is not null &&
                          !geometry.HasNormalMap(texture);

            // The same again for the other two. Each is asked for independently, because a
            // surface may have any combination of the three: the passes that produce them
            // run separately and are accepted separately.
            bool orm = !FlatSurfaces &&
                       (Orms ?? (object?)Compressed) is not null &&
                       !geometry.HasOrmMap(texture);
            bool height = !FlatSurfaces &&
                          (Heights ?? (object?)Compressed) is not null &&
                          !geometry.HasHeightMap(texture);

            // Already on the device from an earlier room, so there is nothing to read,
            // decode or upload. Most of what a room asks for is something it has met
            // before: the characters are in every room they appear in. HasTexture is what
            // counts a reuse, so it is asked exactly once for each name.
            bool colour = !geometry.HasTexture(texture);

            if (normal || orm || height || colour)
            {
                wanted.Add((texture, normal, orm, height, colour));
            }
        }

        if (wanted.Count == 0)
        {
            return;
        }

        var read = new (
            DecodedImage? Normal,
            CompressedImage? BlockNormal,
            DecodedImage? Orm,
            CompressedImage? BlockOrm,
            DecodedImage? Height,
            CompressedImage? BlockHeight,
            DecodedImage? Colour,
            CompressedImage? Blocks,
            Source From,
            string? Missing)[wanted.Count];

        // A bag each, merged in order afterwards, so a run says the same thing twice
        // running. A shared one would need a lock and would report in whatever order the
        // threads happened to finish.
        var bags = new DiagnosticBag[wanted.Count];

        Parallel.For(0, wanted.Count, new ParallelOptions { MaxDegreeOfParallelism = Decoders }, i =>
        {
            (string texture, bool normal, bool orm, bool height, bool colour) = wanted[i];
            var bag = new DiagnosticBag();
            bags[i] = bag;

            // The generated map first and the compressed build of it second, which is the
            // order the whole loader now takes: see Compressed for why. A .png is what the
            // generator produced this morning; a .dds is what somebody compressed at some
            // point, and while these sets are still moving the two are not the same file.
            DecodedImage? bumps = normal ? Normals?.Read(texture, bag) : null;
            CompressedImage? blockBumps =
                normal && bumps is null ? Compressed?.ReadNormal(texture, bag) : null;

            DecodedImage? finish = orm ? Orms?.Read(texture, bag) : null;
            CompressedImage? blockFinish =
                orm && finish is null ? Compressed?.ReadOrm(texture, bag) : null;

            DecodedImage? relief = height ? Heights?.Read(texture, bag) : null;
            CompressedImage? blockRelief =
                height && relief is null ? Compressed?.ReadHeight(texture, bag) : null;

            if (!colour)
            {
                read[i] = (
                    bumps, blockBumps,
                    finish, blockFinish,
                    relief, blockRelief,
                    null, null, Source.Original, null);

                return;
            }

            // The original, read whatever else happens: it is small, and it is the only
            // thing that can say whether this texture uses GK3 magenta. A colour key cannot
            // be applied to blocks, so a texture that needs one must not take that path — it
            // would come out with magenta painted where its holes should be.
            byte[]? bytes = _archives.Read(texture) ?? _archives.Read(texture + ".BMP");
            bool readable = bytes is not null && BitmapDecoder.CanDecode(bytes);
            DecodedImage? original = readable ? BitmapDecoder.Decode(bytes!, texture) : null;

            // Foliage drawn for the modelled trees, which no archive holds and no enhanced
            // set replaces: a needle spray is not a better version of a 1999 bitmap, it is
            // a new one. Asked first, and only for names the tree pack actually carries,
            // so it costs one dictionary lookup for every other texture in the game.
            if (Trees?.Textures.Read(texture, bag) is { } foliage)
            {
                read[i] = (
                    bumps, blockBumps,
                    finish, blockFinish,
                    relief, blockRelief,
                    foliage, null, Source.Enhanced, null);

                return;
            }

            // The enhanced picture. It falls back on its own if it will not decode, so a bad
            // file in the enhanced set costs that texture and nothing else.
            if (Enhanced?.Read(texture, bag) is { } better)
            {
                read[i] = (
                    bumps, blockBumps,
                    finish, blockFinish,
                    relief, blockRelief,
                    better, null, Source.Enhanced, null);

                return;
            }

            // A colour key cannot be applied to blocks, so a texture whose original uses
            // GK3 magenta normally has to take the decoded path. Unless the compressed set
            // holds it: that set is built from the enhanced textures, which resolved the
            // magenta into a real alpha channel before it was ever encoded, so the key is
            // already applied and there is nothing left to key.
            //
            // The distinction matters more than it used to. When this check was written the
            // pilot set was 324 textures and three of them were keyed; the set is now 2,926
            // and 398 are, so refusing all of them meant one texture in seven silently
            // rendering as its 1999 original — the hotel sign at Rennes-le-Chateau among
            // them. `pack-content` leaves out any keyed texture whose replacement has no
            // alpha, which is what makes "the pack holds it" enough to know it is safe.
            if (original is not { } first
                || !TextureKeying.NeedsKey(first)
                || Compressed?.Has(texture) == true)
            {
                if (Compressed?.Read(texture, bag) is { } blocks)
                {
                    read[i] = (
                        bumps, blockBumps,
                        finish, blockFinish,
                        relief, blockRelief,
                        null, blocks, Source.Compressed, null);

                    return;
                }
            }

            read[i] = original is null
                ? (bumps, blockBumps, finish, blockFinish, relief, blockRelief,
                   null, null, Source.Original, texture)
                : (bumps, blockBumps, finish, blockFinish, relief, blockRelief,
                   original, null, Source.Original, null);
        });

        for (int i = 0; i < wanted.Count; i++)
        {
            foreach (Diagnostic diagnostic in bags[i].Items)
            {
                diagnostics.Add(diagnostic);
            }

            (DecodedImage? bumps,
             CompressedImage? blockBumps,
             DecodedImage? finish,
             CompressedImage? blockFinish,
             DecodedImage? relief,
             CompressedImage? blockRelief,
             DecodedImage? colour,
             CompressedImage? blocks,
             Source from,
             string? missing) = read[i];

            if (bumps is { } map)
            {
                geometry.AddNormalMap(wanted[i].Name, map);
                _normalsUsed++;
            }
            else if (blockBumps is { } compressedMap)
            {
                geometry.AddNormalMap(wanted[i].Name, compressedMap);
                _normalsUsed++;
            }

            if (finish is { } packed)
            {
                geometry.AddOrmMap(wanted[i].Name, packed);
                _ormsUsed++;
            }
            else if (blockFinish is { } compressedPacked)
            {
                geometry.AddOrmMap(wanted[i].Name, compressedPacked);
                _ormsUsed++;
            }

            if (relief is { } field)
            {
                geometry.AddHeightMap(wanted[i].Name, field);
                _heightsUsed++;
            }
            else if (blockRelief is { } compressedField)
            {
                geometry.AddHeightMap(wanted[i].Name, compressedField);
                _heightsUsed++;
            }

            if (missing is not null)
            {
                diagnostics.Add(new Diagnostic(
                    "SCENE007",
                    DiagnosticSeverity.Warning,
                    $"{owner} references a texture no archive contains: {missing}."));

                continue;
            }

            if (blocks is { } compressed)
            {
                geometry.AddTexture(wanted[i].Name, compressed);
                _compressedUsed++;
                _enhancedUsed++;
            }
            else if (colour is { } image)
            {
                geometry.AddTexture(wanted[i].Name, image);

                if (from == Source.Enhanced)
                {
                    _enhancedUsed++;
                }
            }
        }
    }

    /// <summary>Brings the scene's action files into scope.</summary>
    /// <remarks>
    /// The files decide what the player may do to a noun, so this is what turns a click
    /// that resolves to <c>NIGHTSTAND</c> into a list of verbs. Which files apply is
    /// <see cref="ActionSets"/>' business; this reads them and hands them to a resolver
    /// sharing the story host the scene's own conditions were decided through, because two
    /// hosts over the same state would sooner or later give two answers.
    /// </remarks>
    private ActionResolver? ReadActions(
        SceneDefinition init, SceneRequest request, DiagnosticBag diagnostics)
    {
        if (request.Api is not { } api)
        {
            return null;
        }

        // A location's files are chosen by their own names, so a name that says nothing
        // about when it applies is one that will never be loaded. No file the corpus's
        // general SIFs list is like that; the one name in the game that cannot be read
        // this way, CHU's ch312p06p.nvc, is listed by a timeblock file, where the question
        // is never asked.
        foreach (string listed in init.General?.ActionFiles() ?? [])
        {
            if (!TimeblockRange.TryParse(listed, out _))
            {
                diagnostics.Add(new Diagnostic(
                    "SCENE015",
                    DiagnosticSeverity.Warning,
                    $"{listed} does not name the timeblocks it is for, so it is never " +
                    "loaded and whatever it allows can never be done."));
            }
        }

        // Told where the story has got to, so a rule that hands off to another point in
        // the story's own script is not offered at this one.
        var resolver = new ActionResolver(api) { Now = request.State?.Timeblock };
        IReadOnlyList<string> names = ActionSets.For(init, request.State?.Timeblock);
        List<string> read = [];

        foreach (string name in names)
        {
            // MA2207A.SIF lists "ma2207a.sif" in its own [ACTIONS] section, meaning the
            // .nvc beside it. Read as an action file a scene file is nonsense, and the
            // nonsense is not harmless: it would put invented nouns and verbs into scope.
            if (!name.EndsWith(".nvc", StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(new Diagnostic(
                    "SCENE018",
                    DiagnosticSeverity.Warning,
                    $"The scene lists {name} as an action file, which is not one; " +
                    "it is skipped, and whatever it allows cannot be done."));

                continue;
            }

            string? text = _archives.ReadText(name);

            if (text is null)
            {
                // The global and inventory lists are the original's, verbatim, and a few of
                // their names are not in the archives at all; a location's own file being
                // missing is worth hearing about, one of those is not.
                if (!ActionSets.Global.Contains(name) && !ActionSets.Inventory.Contains(name))
                {
                    diagnostics.Add(new Diagnostic(
                        "SCENE014",
                        DiagnosticSeverity.Warning,
                        $"The scene lists {name}, which no archive contains; " +
                        "whatever it allows cannot be done."));
                }

                continue;
            }

            resolver.Add(NvcFile.Parse(text, name, diagnostics));
            read.Add(name);
        }

        _log?.Invoke(
            $"actions: {resolver.Nouns.Count} nouns from {read.Count} of {names.Count} sets, " +
            $"most specific first: {string.Join(", ", read)}");

        return resolver;
    }

    /// <summary>Reads the soundtracks a scene names.</summary>
    /// <remarks>
    /// A soundtrack is a little script rather than a piece of music — wait a second, play
    /// the room's theme, wait five to ten seconds, play a mood — so a scene that names one
    /// and never reads it knows nothing about what the room sounds like. Reading it is
    /// cheap and separate from playing it, which needs a clock and an audio device.
    /// </remarks>
    private List<SoundtrackFile> ReadSoundtracks(SceneDefinition init, DiagnosticBag diagnostics)
    {
        List<SoundtrackFile> soundtracks = [];

        foreach (string name in init.Soundtracks())
        {
            if (_archives.ReadText(name) is not { } text)
            {
                diagnostics.Add(new Diagnostic(
                    "SCENE020",
                    DiagnosticSeverity.Warning,
                    $"The scene plays {name}, which no archive contains; the room is silent."));

                continue;
            }

            soundtracks.Add(SoundtrackFile.Parse(text, name, diagnostics));
        }

        return soundtracks;
    }

    /// <summary>
    /// Where an actor's head should be pointing, if they are looking at anything.
    /// </summary>
    /// <remarks>
    /// Null unless somebody has asked. A character with nothing to look at stands as the
    /// artist modelled them, which is what every actor in the game has done until now.
    /// </remarks>
    private Dictionary<int, Matrix4x4>? TurnedHead(
        string name, ModFile model, ScenePosition? spot)
    {
        // A glance is worked out from where the actor is standing, so an actor with no spot
        // has nothing to work one out from. They keep their head straight until something
        // moves them, which is one frame later than it sounds.
        if (spot is null ||
            Glances.Of(name) is not { } glance ||
            CharacterHead.Find(model) is not { } head)
        {
            return null;
        }

        // The head's own origin is where the neck is, and its height above the feet is
        // what decides whether the actor has to look up or down.
        float eyes = CharacterHead.PivotOf(model, head).Y;

        (float yaw, float pitch) = Glances.Turn(spot.Position, spot.Heading, eyes, glance.Point);

        _log?.Invoke(string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"glance: {name} looks at {glance.Target ?? "a point"}, head mesh {head} " +
            $"turned {float.RadiansToDegrees(yaw):F0} degrees and " +
            $"{float.RadiansToDegrees(pitch):F0} up"));

        // Pitch about the mesh's own sideways axis, then yaw about its up axis: nodding
        // inside a turn rather than turning a nodded head, which is what a neck does.
        return new Dictionary<int, Matrix4x4>
        {
            [head] = Matrix4x4.CreateRotationX(-pitch) * Matrix4x4.CreateRotationY(yaw),
        };
    }
}
