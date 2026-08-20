using System.Numerics;
using GK3Reborn.Content;
using GK3Reborn.Formats.Actions;
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
    IReadOnlyList<string>? Soundtracks = null)
{
    /// <summary>The lights the artists authored for this time of day.</summary>
    public IReadOnlyList<AuthoredLight> Lights => Asset?.Lights ?? [];

    /// <summary>Cameras the player's view can occupy.</summary>
    public IReadOnlyList<SceneCamera> Cameras => Definition.RoomCameras();

    /// <summary>The props and actors loaded from files, never null.</summary>
    public IReadOnlyList<PlacedModel> Models => Placed ?? [];

    /// <summary>The soundtracks the scene plays, never null.</summary>
    public IReadOnlyList<string> Ambient => Soundtracks ?? [];

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
            init.Soundtracks());
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
            init.Soundtracks());
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

            geometry.Add(parsed);

            placed.Add(new PlacedModel(
                model.Name,
                model.Noun,
                model.Verb,
                parsed,
                Matrix4x4.Identity,
                PlacedModelKind.Prop));
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
                Matrix4x4.CreateRotationY(spot.Heading) * Matrix4x4.CreateTranslation(spot.Position);

            geometry.Add(model, placement);

            _log?.Invoke(
                $"actor: {actor.Name} ({actor.Noun}) at {spot.Name}{(actor.IsEgo ? ", ego" : string.Empty)}");

            placed.Add(new PlacedModel(
                actor.Name, actor.Noun, null, model, placement, PlacedModelKind.Actor));
        }

        return placed;
    }

    private void LoadTextures(
        ISceneSink geometry, IEnumerable<string> names, string owner, DiagnosticBag diagnostics)
    {
        foreach (string texture in names
                     .Where(n => n.Length > 0)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            // The enhanced version first, when there is one. It falls back on its own if it
            // will not decode, so a bad file in the enhanced set costs that texture and
            // nothing else.
            if (Enhanced?.Read(texture, diagnostics) is { } better)
            {
                geometry.AddTexture(texture, better);
                _enhancedUsed++;
                continue;
            }

            byte[]? bytes = _archives.Read(texture) ?? _archives.Read(texture + ".BMP");
            if (bytes is null || !BitmapDecoder.CanDecode(bytes))
            {
                diagnostics.Add(new Diagnostic(
                    "SCENE007",
                    DiagnosticSeverity.Warning,
                    $"{owner} references a texture no archive contains: {texture}."));

                continue;
            }

            geometry.AddTexture(texture, BitmapDecoder.Decode(bytes, texture));
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
}
