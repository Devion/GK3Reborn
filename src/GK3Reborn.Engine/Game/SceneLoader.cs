using System.Numerics;
using GK3Reborn.Content;
using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Formats.Lightmaps;
using GK3Reborn.Formats.Models;
using GK3Reborn.Formats.Scenes;
using GK3Reborn.Foundation.Diagnostics;
using GK3Reborn.Rendering;

namespace GK3Reborn.Game;

/// <summary>What loading a scene produced, besides its geometry.</summary>
/// <param name="Name">Scene name.</param>
/// <param name="Init">Its initialisation file, if it has one.</param>
/// <param name="Asset">The scene asset for the chosen time of day, if it has one.</param>
/// <param name="Lightmaps">The baked lighting that was applied, if any.</param>
/// <param name="ModelsPlaced">How many props were placed.</param>
public sealed record LoadedScene(
    string Name,
    SceneInitFile? Init,
    SceneAssetFile? Asset,
    MulFile? Lightmaps,
    int ModelsPlaced)
{
    /// <summary>The lights the artists authored for this time of day.</summary>
    public IReadOnlyList<AuthoredLight> Lights => Asset?.Lights ?? [];

    /// <summary>Cameras the player's view can occupy.</summary>
    public IReadOnlyList<SceneCamera> Cameras => Init?.RoomCameras() ?? [];

    /// <summary>Finds a camera by name, falling back to the scene's default.</summary>
    /// <param name="name">Camera name, or null for the default.</param>
    /// <returns>The camera, or null if the scene defines none.</returns>
    public SceneCamera? CameraNamed(string? name) =>
        name is null
            ? Init?.DefaultCamera()
            : Cameras.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
              ?? Init?.DefaultCamera();
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

    /// <summary>Creates a loader.</summary>
    /// <param name="archives">Where to read assets from.</param>
    /// <param name="log">Optional progress sink.</param>
    public SceneLoader(GameArchives archives, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(archives);
        _archives = archives;
        _log = log;
    }

    /// <summary>Loads a scene into geometry.</summary>
    /// <param name="geometry">Where to put it.</param>
    /// <param name="sceneName">Scene name, such as <c>R25</c>.</param>
    /// <param name="timeblock">Time of day: <c>M</c>, <c>A</c>, <c>E</c> or <c>N</c>.</param>
    /// <param name="diagnostics">Receives anything that could not be loaded.</param>
    /// <returns>What was loaded, or null if the scene has no geometry at all.</returns>
    public LoadedScene? Load(
        ISceneSink geometry, string sceneName, string? timeblock, DiagnosticBag diagnostics)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(sceneName);
        ArgumentNullException.ThrowIfNull(diagnostics);

        string scene = Path.GetFileNameWithoutExtension(sceneName).ToUpperInvariant();

        SceneInitFile? init = ReadInit(scene, diagnostics);
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

        int placed = PlaceModels(geometry, asset, init, diagnostics);
        _log?.Invoke($"models: {placed} placed, textures: {geometry.TextureCount}");

        return new LoadedScene(scene, init, asset, lightmaps, placed);
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
    private static HashSet<string> HiddenObjects(SceneInitFile? init)
    {
        if (init is null)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        return init.Models()
            .Where(m => IsHitTest(m) || (IsBakedIn(m) && m.Hidden))
            .Select(m => m.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
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

    private SceneInitFile? ReadInit(string scene, DiagnosticBag diagnostics)
    {
        string? text = _archives.ReadText(scene + ".SIF");
        if (text is null)
        {
            diagnostics.Add(new Diagnostic(
                "SCENE002",
                DiagnosticSeverity.Warning,
                $"No {scene}.SIF; the scene has no cameras of its own."));

            return null;
        }

        SceneInitFile init = SceneInitFile.Parse(text, scene + ".SIF");
        _log?.Invoke($"init: {init.Name}, {init.RoomCameras().Count} room cameras, " +
                     $"{init.Models().Count} models, {init.Actors().Count} actors");

        return init;
    }

    private SceneAssetFile? ReadAsset(
        string scene, string? timeblock, SceneInitFile? init, DiagnosticBag diagnostics)
    {
        foreach (string candidate in Candidates(scene, timeblock, init?.SceneAsset()))
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

    private int PlaceModels(
        ISceneSink geometry, SceneAssetFile? asset, SceneInitFile? init, DiagnosticBag diagnostics)
    {
        IReadOnlyList<SceneModel> declared = init?.Models() ?? [];
        int placed = 0;

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
            placed++;
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
}
