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
    /// <summary>The lights the artists authored for this time of day.</summary>
    public IReadOnlyList<AuthoredLight> Lights => Asset?.Lights ?? [];

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
    /// The same textures and normal maps, block-compressed, if the pipeline has built them.
    /// </summary>
    /// <remarks>
    /// Preferred over both of the above wherever it has an answer. There is nothing to
    /// decode, the mip chain is already built, and it takes a quarter of the video memory —
    /// which is most of what a scene load costs in both time and space.
    /// </remarks>
    public CompressedTextures? Compressed { get; set; }

    private int _normalsUsed;
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

        LoadTextures(geometry, bsp.Surfaces.Select(s => s.TextureName), bspName, diagnostics);
        geometry.AddScene(bsp, lightmaps, HiddenObjects(init));

        // 177 of the game's 229 scene assets name a sky, and which one is already decided
        // by the time of day the timeblock chose.
        if (asset?.Skybox is { IsEmpty: false } sky)
        {
            LoadSkybox(geometry, sky, diagnostics);
        }

        ReportDisputedVisibility(init, diagnostics);

        List<PlacedModel> placed = PlaceModels(geometry, asset, init, diagnostics);
        placed.AddRange(PlaceActors(geometry, init, diagnostics));
        _log?.Invoke(
            $"models: {placed.Count} placed, textures: {geometry.TextureCount}" +
            (_enhancedUsed > 0 ? $", {_enhancedUsed} of them enhanced" : string.Empty));

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
            ReadSoundtracks(init, diagnostics));
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

        if (chosen is null)
        {
            return Camera.Framing(geometry.Minimum, geometry.Maximum, Vector3.UnitY);
        }

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

    /// <summary>Reads the script that drives a prop when nobody is asking it to.</summary>
    /// <remarks>
    /// A script the reader does not fully understand is not run at all. Half of a
    /// behaviour is worse than none of it: the branching half of the language decides
    /// *which* idle to play, and running only the parts that are understood would pick
    /// the wrong one and repeat it for as long as the scene is loaded.
    /// </remarks>
    private GasFile? ReadIdle(SceneModel model, DiagnosticBag diagnostics)
    {
        if (model.Gas is not { Length: > 0 } named)
        {
            return null;
        }

        if (_archives.Read(named) is not { } bytes)
        {
            diagnostics.Add(new Diagnostic(
                "GK3R3330", DiagnosticSeverity.Info,
                "A prop names a behaviour script no archive contains.",
                model.Name, null, named, "nothing",
                "The prop is placed and stands still."));

            return null;
        }

        GasFile script = GasFile.Parse(bytes);

        if (!script.Complete)
        {
            diagnostics.Add(new Diagnostic(
                "GK3R3331", DiagnosticSeverity.Info,
                "A behaviour script uses instructions this engine does not run yet.",
                named, null, "an animation and a loop",
                string.Join(", ", script.Unsupported),
                "The prop is placed and stands still. Branching behaviour needs the " +
                "Sheep virtual machine behind it."));

            return null;
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
            if (model.Hidden || IsBakedIn(model))
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

            LoadTextures(
                geometry,
                parsed.Meshes.SelectMany(m => m.Submeshes).Select(s => s.TextureName),
                model.Name,
                diagnostics);

            placed.Add(new PlacedModel(
                model.Name,
                model.Noun,
                model.Verb,
                parsed,
                Matrix4x4.Identity,
                PlacedModelKind.Prop,
                geometry.Add(parsed))
            {
                Gas = model.Gas,
                Idle = ReadIdle(model, diagnostics),
            });
        }

        return placed;
    }

    /// <summary>Puts the scene's actors where the scene says they stand.</summary>
    /// <remarks>
    /// <para>
    /// The models are in their bind pose. Actors are animated by GAS scripts driving ACT
    /// animations against a skeleton, none of which exists yet, so an actor standing here
    /// is standing exactly as the artist modelled them rather than idling.
    /// </para>
    /// <para>
    /// Only the ego is placed. The rest are positioned by script when the story puts them
    /// somewhere, and guessing a spot for them would put characters in rooms they are not
    /// supposed to be in.
    /// </para>
    /// </remarks>
    private List<PlacedModel> PlaceActors(
        ISceneSink geometry, SceneDefinition init, DiagnosticBag diagnostics)
    {
        List<PlacedModel> placed = [];

        foreach (SceneActor actor in init.Actors().Where(a => !a.Hidden))
        {
            // Ego arrives at the scene's entry point; everyone else stands where their own
            // line says. Both are named spots, and an actor whose spot the scene does not
            // define has nowhere to be put.
            ScenePosition? spot = actor.IsEgo
                ? init.PositionNamed(actor.Position) ?? init.StartPosition()
                : init.PositionNamed(actor.Position);

            if (spot is null)
            {
                // An actor with no spot of their own is placed by a script, which is
                // ordinary and silent — 206 actor/timeblock pairs in the corpus are like
                // that. Naming a spot the scene does not define is a different matter, and
                // happens exactly once: the abbé at MA1 303P.
                if (actor.IsEgo || actor.Position is { Length: > 0 })
                {
                    diagnostics.Add(new Diagnostic(
                        "SCENE011",
                        DiagnosticSeverity.Warning,
                        $"{actor.Name} is placed at '{actor.Position ?? "START"}', which the " +
                        "scene does not define; the actor is left out."));
                }

                continue;
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

            ModFile model = ModFile.Parse(bytes, actor.Name + ".MOD");

            LoadTextures(
                geometry,
                model.Meshes.SelectMany(m => m.Submeshes).Select(s => s.TextureName),
                actor.Name,
                diagnostics);

            // Heading turns about the up axis; the model's own origin is at its feet, so
            // the position needs no vertical adjustment.
            Matrix4x4 placement =
                Matrix4x4.CreateRotationY(Navigation.Walker.Rotation(spot.Heading)) *
                Matrix4x4.CreateTranslation(spot.Position);

            ModelPlacement standing =
                geometry.Add(model, placement, TurnedHead(actor.Name, model, spot));

            _log?.Invoke(
                $"actor: {actor.Name} ({actor.Noun}) at {spot.Name}{(actor.IsEgo ? ", ego" : string.Empty)}");

            placed.Add(new PlacedModel(
                actor.Name, actor.Noun, null, model, placement, PlacedModelKind.Actor, standing));
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
        var wanted = new List<(string Name, bool Normal, bool Colour)>();

        foreach (string texture in names
                     .Where(n => n.Length > 0)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            // A generated normal map for this surface, if there is one. 250 of the game's
            // 6,657 textures have one so far and the rest look exactly as they did — a
            // partial set is a perfectly good set.
            bool normal = Normals is not null && !geometry.HasNormalMap(texture);

            // Already on the device from an earlier room, so there is nothing to read,
            // decode or upload. Most of what a room asks for is something it has met
            // before: the characters are in every room they appear in. HasTexture is what
            // counts a reuse, so it is asked exactly once for each name.
            bool colour = !geometry.HasTexture(texture);

            if (normal || colour)
            {
                wanted.Add((texture, normal, colour));
            }
        }

        if (wanted.Count == 0)
        {
            return;
        }

        var read = new (
            DecodedImage? Normal,
            CompressedImage? BlockNormal,
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
            (string texture, bool normal, bool colour) = wanted[i];
            var bag = new DiagnosticBag();
            bags[i] = bag;

            // The compressed normal map first: BC5 is linear by construction and its chain
            // is already built, so there is nothing to decode and nothing to say about
            // colour space.
            CompressedImage? blockBumps = normal ? Compressed?.ReadNormal(texture, bag) : null;
            DecodedImage? bumps = normal && blockBumps is null ? Normals?.Read(texture, bag) : null;

            if (!colour)
            {
                read[i] = (bumps, blockBumps, null, null, Source.Original, null);
                return;
            }

            // The original, read whatever else happens: it is small, and it is the only
            // thing that can say whether this texture uses GK3 magenta. A colour key cannot
            // be applied to blocks, so a texture that needs one must not take that path — it
            // would come out with magenta painted where its holes should be.
            byte[]? bytes = _archives.Read(texture) ?? _archives.Read(texture + ".BMP");
            bool readable = bytes is not null && BitmapDecoder.CanDecode(bytes);
            DecodedImage? original = readable ? BitmapDecoder.Decode(bytes!, texture) : null;

            if (original is not { } first || !TextureKeying.NeedsKey(first))
            {
                if (Compressed?.Read(texture, bag) is { } blocks)
                {
                    read[i] = (bumps, blockBumps, null, blocks, Source.Compressed, null);
                    return;
                }
            }

            // Then the enhanced version. It falls back on its own if it will not decode, so
            // a bad file in the enhanced set costs that texture and nothing else.
            if (Enhanced?.Read(texture, bag) is { } better)
            {
                read[i] = (bumps, blockBumps, better, null, Source.Enhanced, null);
                return;
            }

            read[i] = original is null
                ? (bumps, blockBumps, null, null, Source.Original, texture)
                : (bumps, blockBumps, original, null, Source.Original, null);
        });

        for (int i = 0; i < wanted.Count; i++)
        {
            foreach (Diagnostic diagnostic in bags[i].Items)
            {
                diagnostics.Add(diagnostic);
            }

            (DecodedImage? bumps,
             CompressedImage? blockBumps,
             DecodedImage? colour,
             CompressedImage? blocks,
             Source from,
             string? missing) = read[i];

            if (blockBumps is { } compressedMap)
            {
                geometry.AddNormalMap(wanted[i].Name, compressedMap);
                _normalsUsed++;
            }
            else if (bumps is { } map)
            {
                geometry.AddNormalMap(wanted[i].Name, map);
                _normalsUsed++;
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

        var resolver = new ActionResolver(api);
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
        string name, ModFile model, ScenePosition spot)
    {
        if (Glances.Of(name) is not { } glance || CharacterHead.Find(model) is not { } head)
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
