using System.Globalization;
using System.Numerics;
using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Formats.Lightmaps;
using GK3Reborn.Formats.Models;
using GK3Reborn.Formats.Scenes;
using GK3Reborn.Foundation.Diagnostics;
using GK3Reborn.Rendering;
using GK3Reborn.Rendering.Vulkan;

namespace GK3Reborn.Tools.Stages;

/// <summary>
/// Renders a scene the way the game assembles it.
/// </summary>
/// <remarks>
/// <para>
/// A scene is not one file. The initialisation file names a scene asset for the time of
/// day; the scene asset names the geometry, the props that stand in it and the lights that
/// lit it; the geometry references textures and pairs surface for surface with a lightmap
/// set. This stage walks that chain and draws the result from one of the scene's own
/// cameras, which is the first point at which the renderer shows what a player would see
/// rather than an asset in isolation.
/// </para>
/// <para>
/// Conditional sections are taken at face value here. Which of them apply depends on the
/// story's state, and deciding that needs the Sheep virtual machine and a running game; a
/// still frame is more useful showing everything the scene can contain than showing
/// nothing.
/// </para>
/// </remarks>
public sealed class SceneRenderStage
{
    private readonly Action<string> _log;

    /// <summary>Creates the stage.</summary>
    /// <param name="log">Progress sink.</param>
    public SceneRenderStage(Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
    }

    /// <summary>Renders a scene.</summary>
    /// <param name="sourceDirectory">The game's <c>Data</c> directory.</param>
    /// <param name="sceneName">Scene name, such as <c>R25</c>.</param>
    /// <param name="timeblock">Time of day: <c>M</c>, <c>A</c>, <c>E</c> or <c>N</c>.</param>
    /// <param name="cameraName">Which room camera to use; null takes the scene's default.</param>
    /// <param name="outputPath">Where to write the PNG.</param>
    /// <param name="width">Image width.</param>
    /// <param name="height">Image height.</param>
    /// <param name="diagnostics">Receives stage-level diagnostics.</param>
    /// <returns>True if something was rendered.</returns>
    public bool Run(
        string sourceDirectory,
        string sceneName,
        string? timeblock,
        string? cameraName,
        string outputPath,
        int width,
        int height,
        DiagnosticBag diagnostics)
    {
        ArgumentNullException.ThrowIfNull(sourceDirectory);
        ArgumentNullException.ThrowIfNull(sceneName);
        ArgumentNullException.ThrowIfNull(outputPath);
        ArgumentNullException.ThrowIfNull(diagnostics);

        string scene = Path.GetFileNameWithoutExtension(sceneName).ToUpperInvariant();

        using ArchiveSet archives = ArchiveSet.Open(sourceDirectory);

        SceneInitFile? init = ReadInit(archives, scene, diagnostics);
        SceneAssetFile? asset = ReadAsset(archives, scene, timeblock, init, diagnostics);

        string bspName = asset?.BspName ?? scene;

        byte[]? bspBytes = archives.Read(bspName + ".BSP");
        if (bspBytes is null)
        {
            diagnostics.Add(new Diagnostic(
                "SCENE001", DiagnosticSeverity.Error, $"No archive contains {bspName}.BSP."));
            return false;
        }

        BspFile geometry = BspFile.Parse(bspBytes, bspName + ".BSP");
        _log($"geometry: {bspName}.BSP, {geometry.TriangleCount} triangles, " +
             $"{geometry.Surfaces.Count} surfaces");

        MulFile? lightmaps = ReadLightmaps(archives, asset?.Name, scene, timeblock, diagnostics);

        using VulkanContext context = VulkanContext.CreateHeadless();
        _log($"device: {context.DeviceName}");

        using var renderer = ModelRenderer.Create(context);

        LoadTextures(archives, renderer, geometry.Surfaces.Select(s => s.TextureName), bspName, diagnostics);
        renderer.AddScene(geometry, lightmaps);

        int placed = PlaceModels(archives, renderer, asset, init, diagnostics);
        _log($"models: {placed} placed");

        if (asset is not null)
        {
            _log($"lights: {asset.Lights.Count} declared " +
                 $"({asset.Lights.Count(l => l.Kind == AuthoredLightKind.Spot)} spot, " +
                 $"{asset.Lights.Count(l => l.CastsShadows)} casting shadows)");
        }

        Camera camera = ChooseCamera(init, cameraName, renderer, diagnostics);

        _log(string.Create(
            CultureInfo.InvariantCulture,
            $"camera: ({camera.Position.X:F1}, {camera.Position.Y:F1}, {camera.Position.Z:F1}) " +
            $"looking at ({camera.Target.X:F1}, {camera.Target.Y:F1}, {camera.Target.Z:F1})"));

        _log($"textures: {renderer.TextureCount}, triangles: {renderer.TriangleCount}");

        DecodedImage image = renderer.Render(width, height, camera);

        string? directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllBytes(outputPath, PngWriter.Encode(image));
        _log($"wrote {outputPath}");

        return true;
    }

    private SceneInitFile? ReadInit(ArchiveSet archives, string scene, DiagnosticBag diagnostics)
    {
        string? text = archives.ReadText(scene + ".SIF");
        if (text is null)
        {
            diagnostics.Add(new Diagnostic(
                "SCENE002",
                DiagnosticSeverity.Warning,
                $"No {scene}.SIF; falling back to a camera derived from the geometry's bounds."));

            return null;
        }

        SceneInitFile init = SceneInitFile.Parse(text, scene + ".SIF");
        _log($"init: {init.Name}, {init.RoomCameras().Count} room cameras, " +
             $"{init.Models().Count} models, {init.Actors().Count} actors");

        return init;
    }

    private SceneAssetFile? ReadAsset(
        ArchiveSet archives,
        string scene,
        string? timeblock,
        SceneInitFile? init,
        DiagnosticBag diagnostics)
    {
        List<string> candidates = [];

        if (timeblock is not null)
        {
            candidates.Add($"{scene}_{timeblock.ToUpperInvariant()}");
        }

        if (init?.SceneAsset() is { Length: > 0 } declared)
        {
            candidates.Add(declared);
        }

        candidates.AddRange(new[] { "_M", "_A", "_E", "_N", string.Empty }.Select(s => scene + s));

        foreach (string candidate in candidates)
        {
            string? text = archives.ReadText(candidate + ".SCN");
            if (text is not null)
            {
                SceneAssetFile asset = SceneAssetFile.Parse(text, candidate + ".SCN");
                _log($"asset: {asset.Name}, bsp {asset.BspName}, {asset.Models.Count} models, " +
                     $"{asset.Lights.Count} lights");

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
        ArchiveSet archives,
        string? assetName,
        string scene,
        string? timeblock,
        DiagnosticBag diagnostics)
    {
        List<string> candidates = [];

        // Lightmaps are named after the scene asset, not the BSP: several timeblocks share
        // one BSP and differ only in their bake.
        if (assetName is not null)
        {
            candidates.Add(Path.GetFileNameWithoutExtension(assetName));
        }

        if (timeblock is not null)
        {
            candidates.Add($"{scene}_{timeblock.ToUpperInvariant()}");
        }

        candidates.AddRange(new[] { "_M", "_A", "_E", "_N", string.Empty }.Select(s => scene + s));

        foreach (string candidate in candidates)
        {
            byte[]? bytes = archives.Read(candidate + ".MUL");
            if (bytes is not null)
            {
                MulFile lightmaps = MulFile.Parse(bytes, candidate + ".MUL");
                _log($"lightmaps: {lightmaps.Name}, {lightmaps.Lightmaps.Count} maps, " +
                     $"{lightmaps.TotalPixels} texels");

                return lightmaps;
            }
        }

        diagnostics.Add(new Diagnostic(
            "SCENE004",
            DiagnosticSeverity.Warning,
            $"No lightmaps for {scene}; the scene renders with directional shading instead."));

        return null;
    }

    private static int PlaceModels(
        ArchiveSet archives,
        ModelRenderer renderer,
        SceneAssetFile? asset,
        SceneInitFile? init,
        DiagnosticBag diagnostics)
    {
        // A scene asset's model list names the objects inside the BSP, not separate
        // files — r25_couch is geometry the BSP already holds. Only the initialisation
        // file places actual .MOD props, so a name from the asset is placed only when a
        // model of that name really exists, and its absence is not a problem.
        HashSet<string> declared = init is null
            ? []
            : init.Models().Where(m => !m.Hidden).Select(m => m.Name)
                  .ToHashSet(StringComparer.OrdinalIgnoreCase);

        HashSet<string> hidden = init is null
            ? []
            : init.Models().Where(m => m.Hidden).Select(m => m.Name)
                  .ToHashSet(StringComparer.OrdinalIgnoreCase);

        IEnumerable<string> names = declared
            .Concat(asset?.Models ?? [])
            .Distinct(StringComparer.OrdinalIgnoreCase);

        int placed = 0;

        foreach (string name in names)
        {
            if (hidden.Contains(name))
            {
                continue;
            }

            byte[]? bytes = archives.Read(name + ".MOD");
            if (bytes is null)
            {
                if (declared.Contains(name))
                {
                    diagnostics.Add(new Diagnostic(
                        "SCENE005",
                        DiagnosticSeverity.Warning,
                        $"The scene places {name}, which no archive contains."));
                }

                continue;
            }

            ModFile model = ModFile.Parse(bytes, name + ".MOD");

            LoadTextures(
                archives,
                renderer,
                model.Meshes.SelectMany(m => m.Submeshes).Select(s => s.TextureName),
                name,
                diagnostics);

            renderer.Add(model);
            placed++;
        }

        return placed;
    }

    private Camera ChooseCamera(
        SceneInitFile? init, string? cameraName, ModelRenderer renderer, DiagnosticBag diagnostics)
    {
        SceneCamera? chosen = null;

        if (init is not null)
        {
            IReadOnlyList<SceneCamera> cameras = init.RoomCameras();

            chosen = cameraName is not null
                ? cameras.FirstOrDefault(c => string.Equals(
                    c.Name, cameraName, StringComparison.OrdinalIgnoreCase))
                : init.DefaultCamera();

            if (cameraName is not null && chosen is null)
            {
                diagnostics.Add(new Diagnostic(
                    "SCENE006",
                    DiagnosticSeverity.Warning,
                    $"No camera named {cameraName}; the scene defines " +
                    $"{string.Join(", ", cameras.Select(c => c.Name))}."));

                chosen = init.DefaultCamera();
            }
        }

        if (chosen is null)
        {
            return Camera.Framing(renderer.Minimum, renderer.Maximum, Vector3.UnitY);
        }

        _log($"camera: {chosen.Name}");

        Vector3 extent = renderer.Maximum - renderer.Minimum;
        float reach = MathF.Max(1f, extent.Length());

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

    private static void LoadTextures(
        ArchiveSet archives,
        ModelRenderer renderer,
        IEnumerable<string> names,
        string owner,
        DiagnosticBag diagnostics)
    {
        foreach (string texture in names
                     .Where(n => n.Length > 0)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            byte[]? bytes = archives.Read(texture) ?? archives.Read(texture + ".BMP");
            if (bytes is null || !BitmapDecoder.CanDecode(bytes))
            {
                diagnostics.Add(new Diagnostic(
                    "SCENE007",
                    DiagnosticSeverity.Warning,
                    $"{owner} references a texture no archive contains: {texture}."));

                continue;
            }

            renderer.AddTexture(texture, BitmapDecoder.Decode(bytes, texture));
        }
    }
}
