using GK3Reborn.Formats.Animation;
using System.Numerics;
using System.Text.Json;
using GK3Reborn.Content;
using GK3Reborn.Formats.Actions;
using GK3Reborn.Formats.Audio;
using GK3Reborn.Game.Actors;
using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Formats.Lightmaps;
using GK3Reborn.Formats.Models;
using GK3Reborn.Formats.Scenes;
using GK3Reborn.Formats.Terrain;
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
    /// Reads one of the game's bitmaps by name, decoded.
    /// </summary>
    public Func<string, Formats.Bitmaps.DecodedImage?>? Bitmaps { get; init; }

    /// <summary>
    /// The room's own foliage cards that a grown tree stands in for, by surface.
    /// </summary>
    public IReadOnlySet<int>? ReplacedSurfaces { get; init; }

    /// <summary>
    /// The silhouette painted on each of the room's hit-test textures, by texture name.
    /// </summary>
    public IReadOnlyDictionary<string, Rendering.CutoutMask>? HitTestMasks { get; init; }

    /// <summary>The modelled trees grown over those cards, and where they stand.</summary>
    public IReadOnlyList<GrownStand>? Woods { get; init; }

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

    /// <summary>
    /// How far a named part of the room's own geometry reaches.
    /// </summary>
    /// <param name="objectName">The object's name in the BSP.</param>
    /// <returns>Its corners, or null when the room has no object of that name.</returns>
    public (Vector3 Minimum, Vector3 Maximum)? ExtentOf(string objectName)
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

        var minimum = new Vector3(float.MaxValue);
        var maximum = new Vector3(float.MinValue);
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
                minimum = Vector3.Min(minimum, Vector3.Min(bsp.Vertices[a], Vector3.Min(bsp.Vertices[b], bsp.Vertices[c])));
                maximum = Vector3.Max(maximum, Vector3.Max(bsp.Vertices[a], Vector3.Max(bsp.Vertices[b], bsp.Vertices[c])));
                any = true;
            }
        }

        return any ? (minimum, maximum) : null;
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
public sealed class SceneLoader
{
    private static readonly string[] TimeblockSuffixes = ["_M", "_A", "_E", "_N", ""];

    private readonly GameArchives _archives;
    private readonly Action<string>? _log;
    private int _enhancedUsed;
    private int _treesGrown;
    private int _statuesCarved;
    private int _billboards;

    /// <summary>Which of a room's trees the budget stretched to growing in full.</summary>
    private readonly HashSet<int> _nearTrees = [];

    /// <summary>Where a prop has already put a modelled tree, so the room does not too.</summary>
    private readonly List<(System.Numerics.Vector3 Foot, float Radius)> _standing = [];

    /// <summary>
    /// The trees the room draws whole — leaves on a modelled bole — for the props that are
    /// pictures of the same trees to be measured against.
    /// </summary>
    private readonly List<TreeSite> _trunked = [];

    /// <summary>
    /// The trees grown over the room's own cards, kept so that a click can find them.
    /// </summary>
    private readonly List<GrownStand> _woods = [];

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
    /// Something to do between pieces of work, offered often while the scene is read.
    /// </summary>
    public Action? Progress { get; set; }

    /// <summary>
    /// How much of the room has been read, from nought to one.
    /// </summary>
    public double Through { get; private set; }

    /// <summary>
    /// Where each piece of a load ends, as a share of the whole.
    /// </summary>
    private const double AtSceneFiles = 0.05;
    private const double AtRoomGeometry = 0.14;
    private const double AtRoomTextures = 0.55;
    private const double AtRoomBuilt = 0.62;
    private const double AtSky = 0.68;
    private const double AtProps = 0.90;
    private const double AtActors = 0.97;

    /// <summary>Where the piece of work now running starts and ends on the bar.</summary>
    private double _from;
    private double _to;

    /// <summary>Says that the load has reached a milestone, and offers a frame.</summary>
    /// <param name="through">Which one, as a share of the whole.</param>
    private void Reached(double through)
    {
        _from = _to = through;
        Through = Math.Max(Through, through);
        Progress?.Invoke();
    }

    /// <summary>Says that the work about to run fills a stretch of the bar.</summary>
    /// <param name="from">Where it starts.</param>
    /// <param name="to">Where it ends.</param>
    private void Doing(double from, double to)
    {
        _from = from;
        _to = to;
    }

    /// <summary>Says how far through that stretch the work has got, and offers a frame.</summary>
    /// <param name="done">How many pieces are finished.</param>
    /// <param name="count">How many there are. Nought means the stretch is finished.</param>
    private void Within(int done, int count)
    {
        double part = count > 0 ? Math.Clamp(done / (double)count, 0, 1) : 1;

        Through = Math.Max(Through, _from + ((_to - _from) * part));
        Progress?.Invoke();
    }

    /// <summary>Where the time goes, when somebody is measuring.</summary>
    public LoadTimeline? Timeline { get; set; }

    /// <summary>
    /// Higher-resolution textures to use in place of the archives', if there are any.
    /// </summary>
    public EnhancedTextures? Enhanced { get; set; }

    /// <summary>
    /// Generated normal maps, standing beside the colour textures.
    /// </summary>
    public EnhancedTextures? Normals { get; set; }

    /// <summary>
    /// Generated occlusion, roughness and metalness, packed into one picture per surface.
    /// </summary>
    public EnhancedTextures? Orms { get; set; }

    /// <summary>
    /// Generated height fields, one per surface, for parallax.
    /// </summary>
    public EnhancedTextures? Heights { get; set; }

    /// <summary>
    /// Modelled trees to stand in place of the scene's flat foliage cards.
    /// </summary>
    public TreeLibrary? Trees { get; set; }

    /// <summary>
    /// Improved geometry for the rooms themselves, where any has been built.
    /// </summary>
    public EnhancedScenes? Scenes { get; set; }

    /// <summary>
    /// Prop geometry that did not ship with the game, for the objects restorations put back.
    /// </summary>
    public ModelLibrary? Models { get; set; }

    /// <summary>
    /// Rooms that did not ship with the game, built from glTF.
    /// </summary>
    public RoomLibrary? Rooms { get; set; }

    /// <summary>
    /// Where the reconstructed terrain sets live loose, or null for none.
    /// </summary>
    public string? TerrainDirectory { get; set; }

    /// <summary>
    /// What the material library measured about each texture, or null to displace only
    /// the floor.
    /// </summary>
    public Rendering.Materials.SurfaceFinishes? Finishes { get; set; }

    /// <summary>The ReBarn packs the terrain sets ship in, or null for none.</summary>
    public RebarnContent? TerrainPacks { get; set; }

    /// <summary>How many flat cards were replaced by a modelled tree in the last load.</summary>
    public int TreesGrown => _treesGrown;

    /// <summary>How many billboard cards were replaced by a sculpted model.</summary>
    public int StatuesCarved => _statuesCarved;

    /// <summary>How many billboards were left to turn to the camera every frame.</summary>
    public int Billboards => _billboards;

    /// <summary>
    /// The same textures and maps, block-compressed, if the pipeline has built them.
    /// </summary>
    public CompressedTextures? Compressed { get; set; }

    /// <summary>Colour only: no normal maps, no finishes, no height, from any source.</summary>
    public bool FlatSurfaces { get; set; }

    /// <summary>How many times to subdivide a character's head; zero draws it as authored.</summary>
    public int SmoothHeads { get; set; }

    /// <summary>
    /// The cast, which is where a character's changes of clothes are recorded.
    /// </summary>
    public Actors.CharacterLibrary? Characters { get; set; }

    /// <summary>
    /// Whether the synthesized sun is left out of every room.
    /// </summary>
    public static bool NoSun { get; set; }

    private Actors.CharacterLibrary? _cast;

    /// <summary>The cast, read from the archives if the caller did not supply it.</summary>
    private Actors.CharacterLibrary Cast =>
        _cast ??= Characters ?? Actors.CharacterLibrary.Open(_archives);

    private int _normalsUsed;
    private int _ormsUsed;
    private int _heightsUsed;
    private int _compressedUsed;

    /// <summary>
    /// Who is looking at what as the scene is built.
    /// </summary>
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

        // The other side of the seam gets the same hook. Two of the longest single calls
        // of a cold load are the sink's — cutting the floor into a million triangles, and
        // building the buffers the room is drawn from — and neither can offer a frame it
        // was never given. See Progress.
        geometry.Progress = Progress;

        // And one straight away, before any of it. What follows before the first texture
        // is read — the scene's two initialisation files, its asset, its geometry and its
        // bake — is a quarter of a second on a cold room, which is most of a fade: without
        // this the picture is still whole when the loader first speaks and the fade has
        // nothing left to do but cut.
        Reached(0);

        // Where this room's trees are, and nowhere else. A loader is meant to be built per
        // scene, but one that was not would carry the last room's trees into this one and
        // refuse to plant anything near where they stood — in a different room, at
        // coordinates that mean something else entirely.
        _standing.Clear();
        _nearTrees.Clear();
        _trunked.Clear();
        _woods.Clear();
        _statuesCarved = 0;
        _billboards = 0;

        SceneDefinition init = ReadDefinition(scene, request, diagnostics);
        Timeline?.Stamp("scene files (.SIF)");
        BecomeEgo(init, request, _log);
        Reached(AtSceneFiles / 2);

        SceneAssetFile? asset = ReadAsset(scene, timeblock, init, diagnostics);
        Timeline?.Stamp("scene asset (.SCN)");
        Reached(AtSceneFiles);

        string bspName = asset?.BspName ?? scene;

        byte[]? bspBytes = _archives.Read(bspName + ".BSP");
        BspFile? supplied = null;

        if (bspBytes is null)
        {
            // No .BSP by that name. Before giving up, ask the room library: a room the game
            // never had can only be here because something asked for it, and building one
            // out of glTF is the only way it can exist. Asked after the archives and never
            // before, so it can never stand in front of a room the game itself ships.
            supplied = Rooms?.Read(bspName, diagnostics);

            if (supplied is null)
            {
                diagnostics.Add(new Diagnostic(
                    "SCENE001", DiagnosticSeverity.Error, $"No archive contains {bspName}.BSP."));

                return null;
            }

            _log?.Invoke(
                $"geometry: {bspName} built from a model, {supplied.TriangleCount} triangles, " +
                $"{supplied.Surfaces.Count} surfaces, no bake");
        }

        // Between reading it and parsing it. The two are a tenth of a second together on a
        // large outdoor room and neither can be interrupted, so this is the only place a
        // frame fits — and without it the fade takes a third of itself in one step.
        Timeline?.Stamp("read .BSP");
        Reached(AtSceneFiles + ((AtRoomGeometry - AtSceneFiles) / 3));

        BspFile bsp = supplied ?? BspFile.Parse(bspBytes!, bspName + ".BSP");
        Timeline?.Stamp("parse .BSP");
        _log?.Invoke($"geometry: {bspName}.BSP, {bsp.TriangleCount} triangles, {bsp.Surfaces.Count} surfaces");
        Reached(AtSceneFiles + (2 * (AtRoomGeometry - AtSceneFiles) / 3));

        MulFile? lightmaps = ReadLightmaps(asset?.Name, scene, timeblock, diagnostics);
        Timeline?.Stamp("lightmaps (.MUL)");
        Reached(AtRoomGeometry);

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
            // Named rather than counted. Which textures the room lays on its own floor is
            // what decides where relief is cut and, now, which surface may be given a
            // reflection of the room standing on it — and a count says nothing about
            // whether the right ones were found.
            _log?.Invoke(
                $"floor: {floorObject}, {floorTextures.Count} " +
                $"texture{(floorTextures.Count == 1 ? string.Empty : "s")} that can carry " +
                $"relief: {string.Join(", ", floorTextures.Order(StringComparer.Ordinal))}");
        }

        // Outdoors, the ground does not stop at the floor object: verges, rock faces and
        // roadside carry the same ground the floor does and were left flat by the
        // floor-only rule, which the reconstructed horizon made the sharpest thing on
        // screen. Those surfaces are cut wherever they appear.
        //
        // <b>The room's own floor textures are what "the same ground" means.</b> The test
        // used to be every displaced-class texture the scene uses, and a skybox is not the
        // same thing as being outdoors: the museum has one through its doorway and so does
        // every hotel bedroom with a window, so the wider rule cut whatever those rooms
        // happen to be furnished with. R25 displaced its wardrobe, its rug and the keys of
        // Gabriel's laptop — 40 textures, up to 6.8 units — and MS3 its display cabinets.
        // A texture the scene itself lays on the floor it names is ground by the room's own
        // account, and nothing else in the room is.
        if (asset is { Skybox.IsEmpty: false } && Finishes is { } finishes)
        {
            HashSet<string> everywhere = new(StringComparer.OrdinalIgnoreCase);

            foreach (BspSurface surface in bsp.Surfaces)
            {
                if (floorTextures.Contains(surface.TextureName) &&
                    finishes.Of(surface.TextureName) is { Displaced: true, HeightDepth: > 0f })
                {
                    everywhere.Add(surface.TextureName);
                }
            }

            if (everywhere.Count > 0)
            {
                geometry.ReliefEverywhere(everywhere);
                _log?.Invoke(
                    $"terrain relief: {everywhere.Count} displaced " +
                    $"texture{(everywhere.Count == 1 ? string.Empty : "s")} cut beyond the floor");
            }
        }

        // The longest single stretch of a cold load, and the one that can be counted: the
        // list of names is in hand before a byte of it is read. See Through.
        Doing(AtRoomGeometry, AtRoomTextures);
        LoadTextures(geometry, bsp.Surfaces.Select(s => s.TextureName), bspName, diagnostics);
        Timeline?.Stamp("room textures");

        // Which batches are leaves, before any of them are made. Only the grown trees'
        // own cards: a 1999 tree is one picture on a quad and bending its top corners
        // folds the whole tree over. See ISceneSink.MoveInWind.
        if (Trees is { IsEmpty: false } foliage)
        {
            geometry.MoveInWind(foliage.Cards);
        }

        // Decided before the room is added, because growing a wood means not drawing the
        // cards it replaces, and the cards are hidden by naming them here.
        List<Foliage.FoliageObject> woods = GrowWoods(bsp, init, diagnostics);
        Timeline?.Stamp("grow woods");

        // The cards the grown trees stand in for, by surface. Not by object: an object can
        // be two trees and a painted strip of distant hillside, and hiding it by name takes
        // the hillside away with the trees.
        HashSet<int> replaced = [.. woods.SelectMany(w => w.Surfaces)];

        // Improved geometry for this room, where somebody has built any. Read before the
        // room is added because it is part of adding it, and refused quietly: a room with
        // no overlay and a room whose overlay did not match its geometry both draw the
        // 1999 picture, which is the whole point of the thing being optional.
        SceneOverlay? overlay = Scenes?.Read(bsp, bspBytes, diagnostics);

        if (overlay is not null)
        {
            _log?.Invoke(
                $"geometry: {overlay.Objects.Count} object(s) drawn from improved geometry, " +
                $"{overlay.TriangleCount} triangles");
        }

        geometry.AddScene(bsp, lightmaps, HiddenObjects(init), floorObject, replaced, overlay);
        Timeline?.Stamp("room: the rest of AddScene");

        // The four long stretches with nothing in them to offer a frame of their own: the
        // room's own batches above, and the sky, the horizon and the woods below. Each is
        // one call that can run for a hundred milliseconds or more. See Progress.
        Reached(AtRoomBuilt);

        // The sun, decided once and used by everything that has to agree with it: the room's
        // rig, and the reconstructed horizon standing behind the sky. It is aimed by the
        // artists' own scenekey wherever the asset ships one - see Sunlight - so it has to
        // be worked out after AddScene, which is what gives it a room to be measured
        // against, and before the terrain, which is lit by it.
        //
        // Against the room's corners rather than the whole scene's. Models are placed
        // below and grow the box with them, and a suitcase on the far side of a square is
        // not evidence about where the sun is.
        Vector3 centre = (geometry.Minimum + geometry.Maximum) / 2f;

        AuthoredLight? sun = !NoSun && asset is { Skybox.IsEmpty: false }
            ? Sunlight.For(
                Daylight(request, timeblock, asset),
                centre,
                Sunlight.AuthoredSun(asset.Lights, geometry.Minimum, geometry.Maximum))
            : null;

        // 177 of the game's 229 scene assets name a sky, and which one is already decided
        // by the time of day the timeblock chose.
        if (asset?.Skybox is { IsEmpty: false } sky)
        {
            LoadSkybox(geometry, sky, diagnostics);
            Timeline?.Stamp("skybox");
            Reached(AtRoomBuilt + ((AtSky - AtRoomBuilt) / 2));

            // The reconstructed horizon rides the same choice: the terrain set is named
            // after the sky's own faces, so day and night come free here too.
            LoadTerrain(geometry, sky, sun?.Direction, diagnostics);
            Timeline?.Stamp("terrain horizon");
            Reached(AtSky);
        }

        ReportDisputedVisibility(init, diagnostics);

        // The other stretch worth counting: the scene file says how many models it
        // declares before any of them is read. See Through.
        Reached(AtSky);
        Doing(AtSky, AtProps);
        List<PlacedModel> placed = PlaceModels(geometry, asset, init, diagnostics);
        Timeline?.Stamp("place models");

        // And the props the room's own scripts build rather than the scene file: the disco
        // ball over the bar, the monkey in the fridge. Staged hidden, so they cost the room
        // nothing until a script shows one. See StageConstructed.
        placed.AddRange(StageConstructed(geometry, scene, placed, diagnostics));
        Timeline?.Stamp("stage constructed props");

        // After the props, because the two overlap. A room's shadow-caster cards are a
        // second copy of the trees the scene file also places as props - WOD draws ten
        // pines twice, once in `wod_treeshadowcasters` and once as ten `_pineleavesff`
        // models a few units away - and the original drew both, one lit by the bake and one
        // not. Two flat cards in the same place are a slightly thicker tree; two *modelled*
        // trees in the same place are a mess, so the props win and the room's copies of
        // them are left out.
        PlantWoods(geometry, woods, diagnostics);
        Timeline?.Stamp("plant woods");
        Reached(AtProps);

        // The people in the room, and what is left of the bar. They are read the same way
        // the props were and the textures they want report through the same loop, so the
        // stretch is handed over rather than sat on: a room with four characters in it
        // spends real time here.
        Doing(AtProps, AtActors);
        placed.AddRange(PlaceActors(
            geometry, init, diagnostics, request.State?.LastLocation, request.State?.Timeblock));
        Timeline?.Stamp("place actors");

        // The last of it the loader can speak for. What is left is the sink's: cutting the
        // floor and building the buffers the room is drawn from, which the caller runs and
        // which offers frames through the same hook. See Progress.
        Reached(AtActors);
        _log?.Invoke(
            $"models: {placed.Count} placed, textures: {geometry.TextureCount}" +
            (_enhancedUsed > 0 ? $", {_enhancedUsed} of them enhanced" : string.Empty));

        if (_treesGrown > 0)
        {
            _log?.Invoke($"trees: {_treesGrown} cards grown into modelled trees");
        }

        if (_statuesCarved > 0 || _billboards > 0)
        {
            _log?.Invoke(
                $"billboards: {_statuesCarved} carved into models, " +
                $"{_billboards} left turning to the camera");
        }

        LoadedScene loaded = new(
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

            // The corners of the room, which is what tells a lamp standing in it from a key
            // light tens of thousands of units outside it. Models have been placed by now
            // and have grown the box, and that is fine here: the question this answers is
            // "can this light's stored range reach anything", and a suitcase is something.
            Bounds = (geometry.Minimum, geometry.Maximum),

            // Decided above, before the horizon that is lit by it.
            Sun = sun,

            // What the room stopped drawing, and what it drew instead. Both are wanted by
            // the picker and by nothing else: a click has to meet the tree that is on the
            // screen rather than the card that used to be.
            ReplacedSurfaces = replaced,
            Woods = [.. _woods],

            // What is painted on the room's hit tests, which is the only thing that tells
            // four quads in the same place apart. See ReadHitTestMasks.
            HitTestMasks = ReadHitTestMasks(init, bsp),

            // And a way back to the archive for the one thing after this that wants a
            // picture rather than a surface. See LoadedScene.Bitmaps.
            Bitmaps = ReadBitmap,
        };

        // The walk boundary, the action files, the soundtracks and the camera shell, all
        // of which are read in the initialiser above.
        Timeline?.Stamp("boundary, actions, soundtracks");

        return loaded;
    }

    /// <summary>
    /// What hour to put the sun at for a room that has a sky over it.
    /// </summary>
    /// <param name="request">What was asked for, which usually carries the story's clock.</param>
    /// <param name="timeblock">The timeblock or asset suffix the caller named, if any.</param>
    /// <param name="asset">The scene asset that was chosen.</param>
    /// <returns>The hour to light the room at.</returns>
    private static Timeblock Daylight(
        SceneRequest request, string? timeblock, SceneAssetFile? asset)
    {
        if (request.State?.Timeblock is { } known)
        {
            return known;
        }

        if (Timeblock.TryParse(timeblock, out Timeblock named))
        {
            return named;
        }

        // The suffix of whichever asset was chosen — pou_m, cem_a_e, wod_n — falling back
        // to what the caller named when the asset has no name of its own.
        string baked = Path.GetFileNameWithoutExtension(
            asset?.Name ?? timeblock ?? string.Empty);
        int underscore = baked.LastIndexOf('_');

        return (underscore >= 0 ? baked[(underscore + 1)..] : baked).ToUpperInvariant() switch
        {
            "M" => new Timeblock(1, 10, IsAfternoon: false),
            "A" => new Timeblock(1, 2, IsAfternoon: true),
            "E" => new Timeblock(1, 6, IsAfternoon: true),
            "N" => new Timeblock(1, 10, IsAfternoon: true),
            _ => new Timeblock(1, 10, IsAfternoon: false),
        };
    }

    /// <summary>
    /// Assembles what a scene <em>is</em>, without loading anything that has to be drawn.
    /// </summary>
    /// <param name="request">Which scene, and where the story is.</param>
    /// <param name="diagnostics">Receives loading diagnostics.</param>
    /// <returns>The scene, or null if it has no initialisation file at all.</returns>
    public LoadedScene? Compose(SceneRequest request, DiagnosticBag diagnostics)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(diagnostics);

        SceneDefinition init = ReadDefinition(request.Scene, request, diagnostics);

        if (init.IsEmpty)
        {
            return null;
        }

        BecomeEgo(init, request, _log);

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

    /// <summary>Reads one of the game's bitmaps and decodes it.</summary>
    /// <param name="texture">Its name, with or without the extension.</param>
    /// <returns>The decoded image, or null when there is no such bitmap.</returns>
    private Formats.Bitmaps.DecodedImage? ReadBitmap(string texture)
    {
        if (texture is not { Length: > 0 })
        {
            return null;
        }

        byte[]? bytes = _archives.Read(texture) ?? _archives.Read(texture + ".BMP");

        return bytes is not null && Formats.Bitmaps.BitmapDecoder.CanDecode(bytes)
            ? Formats.Bitmaps.BitmapDecoder.Decode(bytes, texture)
            : null;
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
    private static HashSet<string> HiddenObjects(SceneDefinition init)
    {
        return init.Models()
            .Where(m => IsHitTest(m) || (IsBakedIn(m) && m.Hidden))
            .Select(m => m.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The textures on the object the scene calls its floor.</summary>
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

    /// <summary>
    /// The silhouettes drawn on the room's hit tests, by texture name.
    /// </summary>
    /// <param name="init">What the scene files say the room contains.</param>
    /// <param name="bsp">The room's geometry, for which texture is on which object.</param>
    /// <returns>
    /// A mask for each keyed hit-test texture, or null when the room has no such thing —
    /// which most rooms do not.
    /// </returns>
    private Dictionary<string, CutoutMask>? ReadHitTestMasks(
        SceneDefinition init, BspFile? bsp)
    {
        if (bsp is null)
        {
            return null;
        }

        HashSet<string> tests = new(StringComparer.OrdinalIgnoreCase);

        foreach (SceneModel model in init.Models())
        {
            if (IsHitTest(model))
            {
                tests.Add(model.Name);
            }
        }

        if (tests.Count == 0)
        {
            return null;
        }

        Dictionary<string, CutoutMask> masks = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> tried = new(StringComparer.OrdinalIgnoreCase);

        foreach (BspSurface surface in bsp.Surfaces)
        {
            if (surface.ObjectIndex < 0 ||
                surface.ObjectIndex >= bsp.ObjectNames.Count ||
                !tests.Contains(bsp.ObjectNames[surface.ObjectIndex]) ||
                !tried.Add(surface.TextureName))
            {
                continue;
            }

            byte[]? bytes = _archives.Read(surface.TextureName) ??
                            _archives.Read(surface.TextureName + ".BMP");

            if (bytes is null || !BitmapDecoder.CanDecode(bytes))
            {
                continue;
            }

            if (CutoutMask.Silhouette(BitmapDecoder.Decode(bytes, surface.TextureName)) is { } mask)
            {
                masks[surface.TextureName] = mask;
            }
        }

        return masks.Count > 0 ? masks : null;
    }

    /// <summary>Whether a model refers to geometry inside the BSP rather than a file.</summary>
    private static bool IsBakedIn(SceneModel model) =>
        !string.Equals(model.Type, "prop", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(model.Type, "gasprop", StringComparison.OrdinalIgnoreCase) &&
        !SceneModel.IsDecal(model);

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

            // A room the game never had has no .MOD shell either, so the model library is
            // asked for one — after the archives and never before, like everywhere else.
            if (bytes is null)
            {
                if (Models?.Read(name, diagnostics) is { } supplied)
                {
                    shells.Add(supplied);
                    continue;
                }

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

        // The stretch the whole loop fills, so each model can be given its own slice of it
        // and the textures that model needs can divide the slice again. Read here because
        // Doing is about to be called with something narrower. See Through.
        (double from, double to) = (_from, _to);

        for (int i = 0; i < declared.Count; i++)
        {
            SceneModel model = declared[i];

            // A model whose textures are all resident already reads and uploads without
            // ever reaching the texture loop's own offer, and a room full of those is most
            // of a return trip. See Progress.
            Doing(
                from + ((to - from) * i / declared.Count),
                from + ((to - from) * (i + 1) / declared.Count));

            Within(0, 1);

            if (IsBakedIn(model))
            {
                continue;
            }

            if (PlaceProp(geometry, model, diagnostics) is { } prop)
            {
                placed.Add(prop);
            }
        }

        return placed;
    }

    /// <summary>
    /// Stages the props this room's scripts build for themselves.
    /// </summary>
    /// <param name="geometry">Where to put them.</param>
    /// <param name="scene">The room, for finding the scripts that belong to it.</param>
    /// <param name="already">What the scene file has already placed, so nothing is placed twice.</param>
    /// <param name="diagnostics">Receives anything that could not be read.</param>
    /// <returns>The props staged, hidden, waiting to be shown.</returns>
    private List<PlacedModel> StageConstructed(
        ISceneSink geometry,
        string scene,
        IEnumerable<PlacedModel> already,
        DiagnosticBag diagnostics)
    {
        List<PlacedModel> staged = [];
        HashSet<string> placed = new(already.Select(m => m.Name), StringComparer.OrdinalIgnoreCase);

        foreach (string name in ConstructedProps(scene, diagnostics))
        {
            if (!placed.Add(name))
            {
                continue;
            }

            if (PlaceProp(geometry, new SceneModel(name, null, "prop", Hidden: true), diagnostics)
                is { } prop)
            {
                staged.Add(prop);
            }
        }

        if (staged.Count > 0)
        {
            _log?.Invoke(
                $"construction: {staged.Count} prop{(staged.Count == 1 ? string.Empty : "s")} " +
                $"staged for scripts — {string.Join(", ", staged.Select(p => p.Name))}");
        }

        return staged;
    }

    /// <summary>
    /// The models this room's compiled scripts ask to have built.
    /// </summary>
    /// <param name="scene">The room's name, which its scripts are named after.</param>
    /// <param name="diagnostics">Receives a script that will not parse.</param>
    /// <returns>Model names, in the order the scripts name them, without duplicates.</returns>
    private List<string> ConstructedProps(string scene, DiagnosticBag diagnostics)
    {
        HashSet<string> found = new(StringComparer.OrdinalIgnoreCase);
        List<string> order = [];

        foreach (string script in _archives.Names(".SHP"))
        {
            if (!Path.GetFileNameWithoutExtension(script)
                    .StartsWith(scene, StringComparison.OrdinalIgnoreCase) ||
                _archives.Read(script) is not { } bytes)
            {
                continue;
            }

            Sheep.SheepScriptFile compiled;

            try
            {
                compiled = Sheep.SheepScriptFile.Parse(bytes, script);
            }
            catch (Formats.FormatParseException)
            {
                // A script that will not parse is a prop that will not be staged, not a
                // room that will not load. The call that would have built it reports its
                // own absence when it arrives.
                diagnostics.Add(new Diagnostic(
                    "SCENE026", DiagnosticSeverity.Info,
                    "A script belonging to this scene could not be read, so anything it " +
                    "builds for itself is not staged.",
                    script));

                continue;
            }

            foreach (string constant in compiled.StringConstants.Values)
            {
                if (ConstructedProp(constant) is { } model && found.Add(model))
                {
                    order.Add(model);
                }
            }
        }

        return order;
    }

    /// <summary>Reads a construction specification, if that is what a string is.</summary>
    /// <param name="specification">A string constant out of a compiled script.</param>
    /// <returns>The model named, or null when this is not a prop specification.</returns>
    public static string? ConstructedProp(string specification)
    {
        ArgumentNullException.ThrowIfNull(specification);

        string? model = null;
        bool prop = false;

        foreach (string field in specification.Split(','))
        {
            int equals = field.IndexOf('=', StringComparison.Ordinal);

            if (equals < 0)
            {
                continue;
            }

            string key = field[..equals].Trim();
            string value = field[(equals + 1)..].Trim();

            if (key.Equals("model", StringComparison.OrdinalIgnoreCase))
            {
                model = value;
            }
            else if (key.Equals("type", StringComparison.OrdinalIgnoreCase))
            {
                prop = value.Equals("prop", StringComparison.OrdinalIgnoreCase);
            }
        }

        return prop && model is { Length: > 0 } ? model : null;
    }

    /// <summary>Reads one prop, puts it in the room and says where it went.</summary>
    /// <param name="geometry">Where to put it.</param>
    /// <param name="model">What the scene, or a script, says to place.</param>
    /// <param name="diagnostics">Receives anything that could not be read.</param>
    /// <returns>The prop as placed, or null when the archives have no such model.</returns>
    private PlacedModel? PlaceProp(
        ISceneSink geometry, SceneModel model, DiagnosticBag diagnostics)
    {
        byte[]? bytes = _archives.Read(model.Name + ".MOD");
        ModFile? supplied = null;

        if (bytes is null)
        {
            // Nothing in the archives by that name. Before giving up, ask the model
            // library — a prop that did not ship with the game can only be here because a
            // restoration asked for it, and this is the only way it can exist at all. The
            // library is asked *after* the archives and never before, so it can never
            // stand in front of a model the game itself placed.
            supplied = Models?.Read(model.Name, diagnostics);

            if (supplied is null)
            {
                diagnostics.Add(new Diagnostic(
                    "SCENE006",
                    DiagnosticSeverity.Warning,
                    $"The scene places {model.Name}, which no archive contains."));

                return null;
            }
        }

        ModFile parsed = supplied ?? ModFile.Parse(bytes!, model.Name + ".MOD");
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
        else if (Carve(parsed, model.Name, diagnostics) is { } carved)
        {
            // A carved statue is modelled in the room's own coordinates, exactly as the
            // card it replaces was, so there is nothing to stand it on. It keeps the
            // card's noun, its scene flags and its name; only the shape changed.
            parsed = carved;
            _statuesCarved++;
        }

        // Where the scene says it stands, for a prop borrowed from another room. A .MOD's
        // vertices are in the coordinates of the room it was modelled for, so a suitcase
        // taken from R31 and put in R29 would otherwise appear wherever it sits in R31 —
        // usually inside a wall. Applied after the tree pass because a grown tree already
        // carries a transform of its own and no restoration places one.
        if (model.Position is { } stands && standing.IsIdentity)
        {
            standing = StandOn(parsed, stands, model.Heading ?? 0f);
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

        // Anything still flagged a billboard and still flat is turned to the camera every
        // frame, which is what the flag has always meant and what the port did not do:
        // the chains, the lanterns, the flowers and the small pines no grown tree
        // replaced. A model that was carved or grown is a shape now and must hold still.
        if (Statues.IsCard(parsed))
        {
            geometry.FaceCamera(placement);
            _billboards++;
        }

        if (model.Hidden)
        {
            geometry.SetVisible(placement, false);
        }

        // Lit by nothing and drawn as painted. Almost every one of the 68 lines that say
        // so is a thing that is itself light — a hanging flame, a fire in a bowl, a
        // fountain — and shading those by the room they light is what made TE4's bowl of
        // fire read as grey lichen. See SceneModel.FullLighting.
        if (model.FullLighting)
        {
            geometry.SetSelfLit(placement, true);
        }

        return new PlacedModel(
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
            SelfLit = model.FullLighting,
        };
    }


    /// <summary>Builds the transform that stands a model on a point.</summary>
    /// <param name="model">The mesh, in whatever coordinates it was authored in.</param>
    /// <param name="where">Where it is to stand.</param>
    /// <param name="heading">Which way it is to face, in degrees about Y.</param>
    /// <returns>The transform to place it with.</returns>
    private static Matrix4x4 StandOn(ModFile model, Vector3 where, float heading)
    {
        if (Box(model) is not { } corners)
        {
            return Matrix4x4.CreateTranslation(where);
        }

        (Vector3 min, Vector3 max) = corners;

        var footing = new Vector3((min.X + max.X) / 2f, min.Y, (min.Z + max.Z) / 2f);

        return Matrix4x4.CreateTranslation(-footing)
            * Matrix4x4.CreateRotationY(heading * MathF.PI / 180f)
            * Matrix4x4.CreateTranslation(where);
    }

    /// <summary>The box a model fills, in the space its own vertices are in.</summary>
    /// <param name="model">The parsed model.</param>
    /// <returns>Its corners, or null when it has no vertices at all.</returns>
    private static (Vector3 Least, Vector3 Most)? Box(ModFile model)
    {
        Vector3 min = new(float.MaxValue), max = new(float.MinValue);
        bool any = false;

        foreach (ModMesh mesh in model.Meshes)
        {
            foreach (ModSubmesh submesh in mesh.Submeshes)
            {
                foreach (Vector3 vertex in submesh.Positions)
                {
                    Vector3 local = Vector3.Transform(vertex, mesh.MeshToLocal);
                    min = Vector3.Min(min, local);
                    max = Vector3.Max(max, local);
                    any = true;
                }
            }
        }

        return any ? (min, max) : null;
    }

    /// <summary>
    /// How many triangles of grown wood one room may be given.
    /// </summary>
    private const int WoodBudget = 400_000;

    /// <summary>Finds the stands of trees in a room, as far as the budget reaches.</summary>
    /// <param name="scene">The parsed room.</param>
    /// <param name="init">What the scene files say the room holds.</param>
    /// <param name="diagnostics">Receives a warning for any grown tree that will not load.</param>
    /// <returns>The objects whose cards are to be replaced, largest first.</returns>
    private List<Foliage.FoliageObject> GrowWoods(
        BspFile scene, SceneDefinition init, DiagnosticBag diagnostics)
    {
        if (Trees is not { IsEmpty: false } library)
        {
            return [];
        }

        List<Foliage.FoliageObject> afforded = [];
        List<(Vector3 Least, Vector3 Most)>? reachable = null;
        int spent = 0;
        int refused = 0;
        int unreadable = 0;
        int buried = 0;

        foreach (Foliage.FoliageObject wood in Foliage.InGeometry(scene, library))
        {
            // Before the budget, because a stand that must not be grown should not be
            // charged for either: MCF's three maples cost the room's whole allowance, and
            // spending it on a stand that is then refused would leave nothing for the rest.
            reachable ??= Reachable(init, library, diagnostics);

            if (wood.Sites.Any(site => reachable.Exists(p => Foliage.Buries(site, p.Least, p.Most))))
            {
                buried += wood.Sites.Count;
                continue;
            }

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

        if (buried > 0)
        {
            // Said out loud because it is the one refusal that is about the game rather
            // than about the budget or the files, and a room that quietly stopped growing
            // its trees should say which of the two happened.
            _log?.Invoke(
                $"trees: {buried} left flat; a grown tree would close over something the " +
                "player has to click");
        }

        // The whole trees among them, for the props that are pictures of the same trees.
        // Only the objects the budget kept: a stand that was refused still draws its own
        // bole, and a prop fitted to it would put a modelled trunk through a 1999 one.
        _trunked.Clear();
        _trunked.AddRange(afforded.SelectMany(w => w.Sites).Where(s => s.Trunked));

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

    /// <summary>
    /// Where the things the player has to be able to click actually are.
    /// </summary>
    /// <param name="init">What the scene files say the room holds.</param>
    /// <param name="library">The trees, for recognising a prop that is only a picture of one.</param>
    /// <param name="diagnostics">Receives a prop that will not read.</param>
    /// <returns>The box each noun-bearing prop fills, in the room's own space.</returns>
    private List<(Vector3 Least, Vector3 Most)> Reachable(
        SceneDefinition init, TreeLibrary library, DiagnosticBag diagnostics)
    {
        List<(Vector3 Least, Vector3 Most)> found = [];

        foreach (SceneModel model in init.Models())
        {
            if (model.Noun is not { Length: > 0 } || IsBakedIn(model))
            {
                continue;
            }

            byte[]? bytes = _archives.Read(model.Name + ".MOD");

            ModFile? parsed = bytes is null
                ? Models?.Read(model.Name, diagnostics)
                : ModFile.Parse(bytes, model.Name + ".MOD");

            if (parsed is null || Foliage.SiteFor(parsed, library) is not null)
            {
                continue;
            }

            if (Box(parsed) is not { } corners)
            {
                continue;
            }

            // Where the scene puts it, for the props that carry another room's coordinates.
            // The same transform the placement uses, so the box tested is the box the
            // player will be clicking at. Both corners go through it and are put back in
            // order afterwards, since a turn about the vertical can swap them over.
            if (model.Position is { } stands)
            {
                Matrix4x4 standing = StandOn(parsed, stands, model.Heading ?? 0f);
                Vector3 a = Vector3.Transform(corners.Least, standing);
                Vector3 b = Vector3.Transform(corners.Most, standing);

                found.Add((Vector3.Min(a, b), Vector3.Max(a, b)));
                continue;
            }

            found.Add(corners);
        }

        return found;
    }

    /// <summary>Whether every tree a stand needs can actually be loaded.</summary>
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

                Matrix4x4 standing = Foliage.Standing(site, chosen);

                geometry.Add(grown, standing);
                _woods.Add(new GrownStand(wood.Named, grown, standing));
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

    /// <summary>
    /// The room's own measurement of a tree a prop is a picture of, where there is one.
    /// </summary>
    /// <param name="site">What the prop's card says about the tree.</param>
    /// <returns>The room's site, or the card's own when the room does not draw this tree.</returns>
    private TreeSite Whole(TreeSite site)
    {
        foreach (TreeSite room in _trunked)
        {
            if (!ReferenceEquals(room.Species, site.Species))
            {
                continue;
            }

            float apart = System.Numerics.Vector2.Distance(
                new System.Numerics.Vector2(room.Foot.X, room.Foot.Z),
                new System.Numerics.Vector2(site.Foot.X, site.Foot.Z));

            // The card hangs above the bole rather than beside it, so the feet are compared
            // sideways only and the crown is asked to overlap the room's tree in height.
            if (apart < MathF.Max(MathF.Min(room.Radius, site.Radius) * 0.5f, 12f) &&
                site.Foot.Y < room.Foot.Y + room.Height &&
                site.Foot.Y + site.Height > room.Foot.Y)
            {
                return room;
            }
        }

        return site;
    }

    /// <summary>Whether a prop has already grown a tree where this site is.</summary>
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
    private (ModFile Model, Matrix4x4 Standing)? GrowTree(
        ModFile card, DiagnosticBag diagnostics)
    {
        if (Trees is not { IsEmpty: false } library ||
            Foliage.SiteFor(card, library) is not { } site)
        {
            return null;
        }

        // Where the room draws the same tree whole, the room's measurement wins. A leaves
        // card knows how far the crown spread and nothing about where the trunk stands, so
        // a tree grown from it alone hangs in the air with its bole inside the room's — the
        // two trunks the hotel maple used to have. See _trunked.
        site = Whole(site);

        GrownTree chosen = TreeLibrary.Variant(site.Species, site.Seed);

        if (library.Read(chosen, diagnostics) is not { } grown)
        {
            return null;
        }

        _standing.Add((site.Foot, site.Radius));
        return (grown, Foliage.Standing(site, chosen));
    }

    /// <summary>Stands a sculpted model where a billboard card was, when there is one.</summary>
    /// <param name="card">The prop as the archives hold it.</param>
    /// <param name="name">What the scene called it, which is what the content is keyed by.</param>
    /// <param name="diagnostics">Receives anything about the sculpt that will not read.</param>
    /// <returns>The carved model, or null to keep the card.</returns>
    private ModFile? Carve(ModFile card, string name, DiagnosticBag diagnostics)
    {
        if (Models is not { IsEmpty: false } library || !Statues.IsCard(card))
        {
            return null;
        }

        ModFile? sculpt = library.Read(name, diagnostics);

        return Statues.IsSculpt(sculpt) ? sculpt : null;
    }

    /// <summary>
    /// Makes the player whoever the scene says they are.
    /// </summary>
    /// <param name="init">The scene's two initialisation files, already merged.</param>
    /// <param name="request">Which scene, and where the story is.</param>
    /// <param name="log">Where a change of ego is reported, if anywhere.</param>
    private static void BecomeEgo(SceneDefinition init, SceneRequest request, Action<string>? log)
    {
        if (request.State is not { } state || init.EgoNoun() is not { Length: > 0 } noun)
        {
            return;
        }

        if (string.Equals(state.Ego, noun, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        log?.Invoke($"ego: {request.Scene} is {noun}'s, not {state.Ego}'s");
        state.Ego = noun;
    }

    /// <summary>Puts the scene's actors where the scene says they stand.</summary>
    private List<PlacedModel> PlaceActors(
        ISceneSink geometry,
        SceneDefinition init,
        DiagnosticBag diagnostics,
        string? from = null,
        Timeblock? now = null)
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

            // What they are wearing today, before anything else touches the model: the
            // indices an [MTEXTURES] line carries are the ones in the file it was authored
            // against, and dressing them here is what keeps that true.
            parsedActor = Dress(parsedActor, actor.Name, now, diagnostics);

            // And on their own feet, so that standing them somewhere stands them there.
            // A character's model is not always drawn around its own origin — Lady Howard's
            // is 84 units from hers — and every transform that places one, here and in a
            // walk and in a script, assumes it is. See Actors.Footing.
            parsedActor = Actors.Footing.OnItsFeet(
                parsedActor, Cast.Of(actor.Name), out Vector3 footed);

            if (footed.Length() > 1f)
            {
                float off = footed.Length();

                _log?.Invoke(string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"footing: {actor.Name} is modelled {off:F0} units off their own origin, and is stood on their feet"));
            }

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

            // Heading turns about the up axis; the model has been stood on its own feet
            // above, so the position needs no adjustment of its own.
            //
            // <b>An actor with no spot is put back where their model was modelled.</b>
            // Standing them on their feet is a statement about a placement, and where there
            // is none there is nothing to say: the original leaves the model actor at the
            // origin and lets the model's own vertices decide, and everything about such an
            // actor — an absolute opening clip, or the script that walks them in — is
            // written against where the artists left them. Undoing the footing here is what
            // keeps that true. See Actors.Footing.
            Matrix4x4 placement = spot is null
                ? Matrix4x4.CreateTranslation(-footed)
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

                // Whether the scene put them somewhere itself, which decides how much of
                // that opening animation is allowed to reach them.
                Spotted = spot is not null,

                Visible = !actor.Hidden,
            });
        }

        return placed;
    }

    /// <summary>
    /// Puts a character into the clothes this point in the story calls for.
    /// </summary>
    /// <param name="model">The model as read from its file.</param>
    /// <param name="name">The name the scene placed it under.</param>
    /// <param name="now">The story's timeblock, or null when the caller named none.</param>
    /// <param name="diagnostics">Receives anything the change of clothes could not find.</param>
    /// <returns>The model, dressed.</returns>
    private ModFile Dress(
        ModFile model, string name, Timeblock? now, DiagnosticBag diagnostics)
    {
        if (Cast.Of(name)?.ClothingFor(now) is not { Length: > 0 } wearing)
        {
            return model;
        }

        if (_archives.ReadText(wearing + ".ANM") is not { } text)
        {
            diagnostics.Add(new Diagnostic(
                "SCENE012",
                DiagnosticSeverity.Warning,
                $"{name} is dressed by '{wearing}', which no archive contains; they wear " +
                "whatever their model was painted with."));

            return model;
        }

        AnimationFile clothes =
            AnimationFile.Parse(text, wearing + ".ANM", diagnostics);

        return Actors.Wardrobe.Dress(model, name, clothes, line => diagnostics.Add(new Diagnostic(
            "SCENE012",
            DiagnosticSeverity.Info,
            $"{wearing} paints group {line.Submesh} of mesh {line.Mesh} of {line.Model}, " +
            $"which {name}.MOD does not go up to; that surface keeps its own texture.")));
    }

    /// <summary>The cube map's sides, in the order the hardware wants them.</summary>
    private static readonly string[] Sides = ["front", "back", "up", "down", "right", "left"];

    /// <summary>
    /// Gives the room its sky, when the scene asset names one.
    /// </summary>
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

            // The enhanced set first, the same layer the room's surfaces get: a sky face
            // is a texture like any other, only named by the scene rather than the BSP.
            if (Enhanced?.Read(texture, diagnostics) is { } better)
            {
                read[face] = better;
                any ??= better;
                _enhancedUsed++;
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

    /// <summary>The set a sky's faces belong to: <c>BMB_A_512RT</c> names <c>BMB_A</c>.</summary>
    /// <param name="sky">The scene's sky.</param>
    /// <returns>The set name, or null when the faces follow no known convention.</returns>
    public static string? TerrainSetName(SkyboxDefinition sky)
    {
        foreach (string? name in new[] { sky.Front, sky.Back, sky.Left, sky.Right, sky.Up })
        {
            if (name is not { Length: > 6 })
            {
                continue;
            }

            // Every skybox face in the game is <set>_512<side>: the resolution is part
            // of the name, and the two letters after it say which way the face looks.
            int marker = name.LastIndexOf("_512", StringComparison.OrdinalIgnoreCase);

            if (marker > 0 && name.Length == marker + 6)
            {
                return name[..marker];
            }
        }

        return null;
    }

    private static readonly JsonSerializerOptions TerrainJson =
        new() { PropertyNameCaseInsensitive = true };

    /// <summary>What the offline pipeline wrote beside each heightfield.</summary>
    private sealed record TerrainMeta(int Grid, float ExtentMeters);

    /// <summary>One tree of the backdrop's forest, as the offline placement wrote it.</summary>
    private readonly record struct TerrainTree(
        float X, float Y, float Z, float S, float R, float K);

    /// <summary>
    /// The backdrop's forest, as the instance stream both tree pipelines read.
    /// </summary>
    /// <param name="set">The terrain set.</param>
    /// <param name="diagnostics">Receives anything wrong with what was found.</param>
    /// <returns>Six floats a tree, or empty for a set with no forest.</returns>
    private float[] ForestFor(string set, DiagnosticBag diagnostics)
    {
        if (ReadTerrainPart(set, "trees.f32") is { } raw)
        {
            if (TerrainForest.Read(raw) is not { } trees)
            {
                diagnostics.Add(new Diagnostic(
                    "SCENE025", DiagnosticSeverity.Warning,
                    "A terrain set's forest is not a whole number of trees.",
                    set, null, $"a multiple of {TerrainForest.BytesPerTree} bytes",
                    $"{raw.Length} bytes",
                    "The scene keeps its horizon and draws no forest on it."));

                return [];
            }

            return trees;
        }

        if (ReadTerrainPart(set, "trees.json") is not { } treesBytes ||
            JsonSerializer.Deserialize<List<TerrainTree>>(treesBytes, TerrainJson)
                is not { Count: > 0 } placed)
        {
            return [];
        }

        float[] fromJson = new float[placed.Count * TerrainForest.FloatsPerTree];

        for (int i = 0; i < placed.Count; i++)
        {
            TerrainTree tree = placed[i];
            fromJson[(i * TerrainForest.FloatsPerTree) + 0] = tree.X;
            fromJson[(i * TerrainForest.FloatsPerTree) + 1] = tree.Y;
            fromJson[(i * TerrainForest.FloatsPerTree) + 2] = tree.Z;
            fromJson[(i * TerrainForest.FloatsPerTree) + 3] = tree.S;
            fromJson[(i * TerrainForest.FloatsPerTree) + 4] = tree.R;

            // A set written before the shapes existed says nothing here, and zero is the
            // conifer every one of its trees used to be.
            fromJson[(i * TerrainForest.FloatsPerTree) + 5] = tree.K;
        }

        return fromJson;
    }

    /// <summary>One of a terrain set's two maps as blocks, where anything holds it so.</summary>
    /// <param name="set">The terrain set.</param>
    /// <param name="part">"splat" or "tint".</param>
    /// <returns>The compressed map, or null to fall back to the PNG beside it.</returns>
    private CompressedImage? TerrainBlocks(string set, string part)
    {
        if (ReadTerrainPart(set, $"{part}.DDS") is not { } bytes ||
            !DdsFile.CanDecode(bytes))
        {
            return null;
        }

        try
        {
            return DdsFile.Read(bytes, $"{set}.{part}.DDS");
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>One part of a terrain set: the loose file first, then the packs.</summary>
    private byte[]? ReadTerrainPart(string set, string part)
    {
        if (TerrainDirectory is { Length: > 0 } root)
        {
            string file = Path.Combine(root, $"{set}.{part}");

            if (File.Exists(file))
            {
                return File.ReadAllBytes(file);
            }
        }

        return TerrainPacks?.Read(Formats.Rebarn.RebarnKind.Raw, $"{set}.{part}");
    }

    private void LoadTerrain(
        ISceneSink geometry, SkyboxDefinition sky, Vector3? sunDirection,
        DiagnosticBag diagnostics)
    {
        if ((TerrainDirectory is not { Length: > 0 } && TerrainPacks is null)
            || TerrainSetName(sky) is not { } set)
        {
            return;
        }

        // A set that is not there is the ordinary case, not a fault: the terrain data is
        // optional content, and every scene without it keeps its painted horizon.
        byte[]? metaBytes = ReadTerrainPart(set, "terrain.json");
        byte[]? raw = ReadTerrainPart(set, "heights.r32");

        // Blocks first for both maps. They are always 1024 square, so decoding the PNGs
        // was a fixed 160 ms of every outdoor load; the blocks upload as they arrive.
        CompressedImage? splatBlocks = TerrainBlocks(set, "splat");
        CompressedImage? tintBlocks = TerrainBlocks(set, "tint");

        byte[]? splatBytes = splatBlocks is null ? ReadTerrainPart(set, "splat.png") : null;
        byte[]? tintBytes = tintBlocks is null ? ReadTerrainPart(set, "tint.png") : null;

        if (metaBytes is null || raw is null ||
            (splatBlocks is null && splatBytes is null) ||
            (tintBlocks is null && tintBytes is null))
        {
            return;
        }

        try
        {
            TerrainMeta? meta = JsonSerializer.Deserialize<TerrainMeta>(metaBytes, TerrainJson);

            if (meta is not { Grid: > 1, ExtentMeters: > 0 })
            {
                return;
            }

            if (raw.Length != meta.Grid * meta.Grid * sizeof(float))
            {
                diagnostics.Add(new Diagnostic(
                    "SCENE022", DiagnosticSeverity.Warning,
                    "A terrain set's heightfield does not match its own stated grid.",
                    set, null, $"{meta.Grid * meta.Grid * sizeof(float)} bytes",
                    $"{raw.Length} bytes",
                    "The scene keeps its painted horizon."));
                return;
            }

            float[] heights = new float[meta.Grid * meta.Grid];
            System.Buffer.BlockCopy(raw, 0, heights, 0, raw.Length);

            DecodedImage? forest = TerrainTile("HOW_MULCH", diagnostics);
            DecodedImage? rock = TerrainTile("ARMROCK03", diagnostics);
            DecodedImage? grass = TerrainTile("GRASS", diagnostics);
            DecodedImage? dirt = TerrainTile("ARMDIRT", diagnostics);

            if (forest is null || rock is null || grass is null || dirt is null)
            {
                diagnostics.Add(new Diagnostic(
                    "SCENE023", DiagnosticSeverity.Warning,
                    "A terrain set is present but its ground textures are not.",
                    set, null, "HOW_MULCH, ARMROCK03, GRASS and ARMDIRT", "at least one missing",
                    "The scene keeps its painted horizon."));
                return;
            }

            // The forest, six floats a tree. A set without one is a set without one.
            float[] trees = ForestFor(set, diagnostics);

            // And the grown trees the nearest of that forest is drawn as, when the
            // library that grows them is installed and the player has them on.
            List<DecodedImage> modelTextures = [];
            List<TerrainTreeModel> models =
                trees.Length > 0 ? TerrainTrees(modelTextures, diagnostics) : [];

            geometry.SetTerrain(new TerrainBackdrop
            {
                Grid = meta.Grid,
                ExtentMeters = meta.ExtentMeters,
                Heights = heights,
                Splat = splatBytes is null
                    ? default
                    : PngReader.Decode(splatBytes, $"{set}.splat.png"),
                Tint = tintBytes is null
                    ? default
                    : PngReader.Decode(tintBytes, $"{set}.tint.png"),
                SplatBlocks = splatBlocks,
                TintBlocks = tintBlocks,
                TileForest = forest.Value,
                TileRock = rock.Value,
                TileGrass = grass.Value,
                TileDirt = dirt.Value,
                SunDirection = sunDirection,
                Azimuth = sky.Azimuth,
                TreeModels = models,
                TreeTextures = modelTextures,

                // The scene's own centre, which is where the painted sky was
                // conceptually seen from.
                AnchorUnits = (geometry.Minimum + geometry.Maximum) / 2f,
                Trees = trees,
            });

            string grown = models.Count > 0
                ? $", the nearest grown from {models.Count} model(s): " +
                  string.Join(", ", models.Select(m => $"{m.Name} {m.Triangles}t"))
                : ", impostors only";

            _log?.Invoke(string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"terrain: {set}, {meta.Grid}x{meta.Grid} over {meta.ExtentMeters:F0} m, " +
                $"{trees.Length / 6} trees{grown}"));
        }
        catch (Exception error) when (
            error is IOException or JsonException or Formats.FormatParseException)
        {
            diagnostics.Add(new Diagnostic(
                "SCENE024", DiagnosticSeverity.Warning,
                "A terrain set is present but would not read.",
                set, null, "readable heightfield, splat and tint", error.Message,
                "The scene keeps its painted horizon."));
        }
    }

    /// <summary>
    /// Which species stands in for each of the backdrop's impostor shapes.
    /// </summary>
    private static readonly string[] TerrainTreeSpecies = ["spruce", "broadleaf", "cypress"];

    /// <summary>
    /// The grown trees the backdrop draws its nearest forest as.
    /// </summary>
    /// <param name="textures">Receives the bark and foliage they are painted with.</param>
    /// <param name="diagnostics">Receives anything that would not read.</param>
    /// <returns>Two levels of detail per species, or nothing at all.</returns>
    private List<TerrainTreeModel> TerrainTrees(
        List<DecodedImage> textures, DiagnosticBag diagnostics)
    {
        if (Trees is not { IsEmpty: false } library)
        {
            return [];
        }

        var models = new List<TerrainTreeModel>();
        var named = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        int Texture(string name)
        {
            if (named.TryGetValue(name, out int already))
            {
                return already;
            }

            // The foliage cards ship with the trees and no archive holds them; the bark is
            // one of the game's own. Enhanced first for both, which is the order everything
            // else here takes.
            DecodedImage? image =
                library.Textures.Read(name, diagnostics)
                ?? Enhanced?.Read(name, diagnostics)
                ?? TerrainTile(name, diagnostics);

            if (image is not { } found)
            {
                return -1;
            }

            named[name] = textures.Count;
            textures.Add(found);

            return textures.Count - 1;
        }

        for (int kind = 0; kind < TerrainTreeSpecies.Length; kind++)
        {
            TreeSpecies? species = library.Species.FirstOrDefault(
                s => string.Equals(
                    s.Name, TerrainTreeSpecies[kind], StringComparison.OrdinalIgnoreCase));

            if (species is null)
            {
                continue;
            }

            for (int detail = 0; detail < 2; detail++)
            {
                IReadOnlyList<GrownTree> band = detail == 0 ? species.Near : species.Distant;

                if (band.Count == 0 || library.Read(band[0], diagnostics) is not { } grown)
                {
                    continue;
                }

                if (Flatten(grown, kind, detail, band[0].Name, Texture) is { } model)
                {
                    models.Add(model);
                }
            }
        }

        return models;
    }

    /// <summary>
    /// Turns a grown tree into one buffer of corners and one of triangles.
    /// </summary>
    /// <param name="grown">The model, as the library read it.</param>
    /// <param name="kind">Which impostor shape it stands in for.</param>
    /// <param name="detail">Nought for the full tree, one for the cheap one.</param>
    /// <param name="name">What to call it in a report.</param>
    /// <param name="texture">Resolves a texture name to its place in the shared list.</param>
    /// <returns>The model, or null when nothing in it could be painted.</returns>
    private static TerrainTreeModel? Flatten(
        ModFile grown, int kind, int detail, string name, Func<string, int> texture)
    {
        var corners = new List<TerrainTreeVertex>();
        var byTexture = new Dictionary<string, (int Texture, List<uint> Indices)>(
            StringComparer.OrdinalIgnoreCase);

        foreach (ModMesh mesh in grown.Meshes)
        {
            foreach (ModSubmesh part in mesh.Submeshes)
            {
                if (part.Indices.Length == 0 || part.Positions.Length == 0)
                {
                    continue;
                }

                if (!byTexture.TryGetValue(
                        part.TextureName, out (int Texture, List<uint> Indices) group))
                {
                    int found = texture(part.TextureName);

                    if (found < 0)
                    {
                        continue;
                    }

                    group = (found, []);
                    byTexture[part.TextureName] = group;
                }

                uint first = (uint)corners.Count;

                for (int i = 0; i < part.Positions.Length; i++)
                {
                    corners.Add(new TerrainTreeVertex(
                        Vector3.Transform(part.Positions[i], mesh.MeshToLocal),
                        Vector3.Normalize(Vector3.TransformNormal(
                            i < part.Normals.Length ? part.Normals[i] : Vector3.UnitY,
                            mesh.MeshToLocal)),
                        i < part.TexCoords.Length ? part.TexCoords[i] : Vector2.Zero));
                }

                foreach (ushort index in part.Indices)
                {
                    group.Indices.Add(first + index);
                }
            }
        }

        if (corners.Count == 0 || byTexture.Count == 0)
        {
            return null;
        }

        var indices = new List<uint>();
        var parts = new List<TerrainTreePart>();

        foreach ((string what, (int found, List<uint> block)) in byTexture)
        {
            if (block.Count == 0)
            {
                continue;
            }

            parts.Add(new TerrainTreePart(
                found,
                (uint)indices.Count,
                (uint)block.Count,

                // Bark is the one thing in a grown tree that is not a clump of leaves, and
                // it is always one of the game's own trunk bitmaps. Everything else is a
                // card cut out of a spray, and needs the alpha test the trunk must not
                // have: a trunk drawn with it loses its own dark edges to the cutout.
                !what.StartsWith("TRUNK", StringComparison.OrdinalIgnoreCase) &&
                !what.Contains("BARK", StringComparison.OrdinalIgnoreCase)));

            indices.AddRange(block);
        }

        return parts.Count == 0
            ? null
            : new TerrainTreeModel
            {
                Kind = kind,
                Detail = detail,
                Name = name,
                Vertices = [.. corners],
                Indices = [.. indices],
                Parts = parts,
            };
    }

    /// <summary>Whether a layer's answer would put the wrong language on a surface.</summary>
    /// <param name="language">The chosen language's pack, or null when there is none.</param>
    /// <param name="texture">The texture's name, as the geometry refers to it.</param>
    /// <param name="ownPicture">
    /// Whether that layer's answer is this language's own picture — repainted for it, or a
    /// file the player put in <c>overrides/</c> — rather than the shared one.
    /// </param>
    /// <returns>True when the layer must stand aside and let the archive answer.</returns>
    public static bool ShadowsLanguage(
        LocalizedContent? language, string texture, bool ownPicture)
    {
        ArgumentNullException.ThrowIfNull(texture);

        return !ownPicture && language?.HasArchive(texture + ".BMP") == true;
    }

    /// <summary>One of the terrain's ground tiles: enhanced first, the archives after.</summary>
    private DecodedImage? TerrainTile(string name, DiagnosticBag diagnostics)
    {
        if (Enhanced?.Read(name, diagnostics) is { } better)
        {
            return better;
        }

        byte[]? bytes = _archives.Read(name) ?? _archives.Read(name + ".BMP");

        return bytes is not null && BitmapDecoder.CanDecode(bytes)
            ? BitmapDecoder.Decode(bytes, name)
            : null;
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
    private static int Decoders => Math.Max(1, Environment.ProcessorCount);

    /// <summary>
    /// Brings one texture in after the room is already standing, the same way the room's
    /// own were brought in.
    /// </summary>
    /// <param name="geometry">The room, already built.</param>
    /// <param name="name">The texture, without an extension.</param>
    /// <param name="diagnostics">Receives anything that went wrong reading it.</param>
    /// <returns>Whether the room now has a picture of that name.</returns>
    public bool LoadTextureLate(ISceneSink geometry, string name, DiagnosticBag diagnostics)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(name);

        if (geometry.HasTexture(name))
        {
            return true;
        }

        // Nothing is fading. Progress is the transition's own hook — it presents a frame of
        // the fade so a long load does not look like a hung window — and the room this is
        // called from is already up and being drawn. Left in, one texture arriving in the
        // middle of an animation would present a frame of a fade that finished minutes ago,
        // which is a black screen.
        Action? offer = Progress;
        Progress = null;

        try
        {
            LoadTextures(geometry, [name], "an animation", diagnostics);
        }
        finally
        {
            Progress = offer;
        }

        return geometry.HasTexture(name);
    }

    /// <summary>Reads, decodes and uploads the textures a room asks for.</summary>
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

        // In batches, with a frame offered between them. The decode is the one long
        // stretch of a load with nothing serial in it to offer from — a room's worth of
        // textures is a tenth of a second across every core — and a fade that goes a
        // third of the way down in one step is a fade with a visible corner in it. A
        // batch still saturates the decoders; only the tail of each one idles, and that
        // costs a fraction of what the offer buys. See Progress.
        const int DecodeBatch = 64;

        // Half the stretch to the decode and half to the upload. Which of the two
        // dominates depends on the machine and on whether the enhanced set is in the way —
        // decoding is spread over every core and uploading goes one at a time through the
        // one queue — so there is no honest constant to prefer, and a bar that runs at two
        // speeds is better than one that stops for the second half.
        for (int batch = 0; batch < wanted.Count; batch += DecodeBatch)
        {
            Within(batch, wanted.Count * 2);

            Parallel.For(
                batch,
                Math.Min(batch + DecodeBatch, wanted.Count),
                new ParallelOptions { MaxDegreeOfParallelism = Decoders },
                i =>
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

                // Whether either layer above the archive would answer with the shared
                // picture for a surface this language repaints. See ShadowsLanguage.
                LocalizedContent? language = _archives.Localization;

                bool sharedPicture = ShadowsLanguage(
                    language,
                    texture,
                    Enhanced?.IsLocalized(texture) == true
                    || Enhanced?.IsOverridden(texture) == true);

                bool sharedBlocks = ShadowsLanguage(
                    language,
                    texture,
                    Compressed?.IsLocalized(texture) == true
                    || Compressed?.IsOverridden(texture) == true);

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
                if (!sharedPicture && Enhanced?.Read(texture, bag) is { } better)
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
                if (!sharedBlocks
                    && (original is not { } first
                        || !TextureKeying.NeedsKey(first)
                        || Compressed?.Has(texture) == true))
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
        }

        for (int i = 0; i < wanted.Count; i++)
        {
            // Uploading is the serial half of this and the long one: the decode above runs
            // across every core the machine has, and everything below goes one at a time
            // through the one queue. So this is where a transition gets most of its
            // frames from. See Progress.
            Within(wanted.Count + i, wanted.Count * 2);

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
