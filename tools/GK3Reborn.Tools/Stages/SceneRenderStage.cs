using System.Globalization;
using GK3Reborn.Content;
using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Foundation.Diagnostics;
using GK3Reborn.Game;
using GK3Reborn.Game.Navigation;
using GK3Reborn.Rendering;
using GK3Reborn.Rendering.Vulkan;

namespace GK3Reborn.Tools.Stages;

/// <summary>
/// Renders a scene the way the game assembles it, to a PNG.
/// </summary>
/// <remarks>
/// The loading is the engine's own, so what this produces is what the game would show
/// from the same viewpoint rather than a second implementation that can drift from it.
/// Rendering offscreen keeps it usable on a build agent and makes the output comparable
/// between runs.
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
    /// <param name="timeblock">
    /// A story timeblock such as <c>202P</c>, which decides the scene file's conditions, or
    /// an asset suffix — <c>M</c>, <c>A</c>, <c>E</c>, <c>N</c> — which only picks the bake.
    /// </param>
    /// <param name="cameraName">Which room camera to use; null takes the scene's default.</param>
    /// <param name="rayTracing">Quality level: none, low, med or high.</param>
    /// <param name="outputPath">Where to write the PNG.</param>
    /// <param name="width">Image width.</param>
    /// <param name="height">Image height.</param>
    /// <param name="walkOverlay">Whether to draw the walk boundary over the floor.</param>
    /// <param name="diagnostics">Receives stage-level diagnostics.</param>
    /// <returns>True if something was rendered.</returns>
    public bool Run(
        string sourceDirectory,
        string sceneName,
        string? timeblock,
        string? cameraName,
        string? rayTracing,
        string outputPath,
        int width,
        int height,
        bool walkOverlay,
        DiagnosticBag diagnostics)
    {
        ArgumentNullException.ThrowIfNull(sourceDirectory);
        ArgumentNullException.ThrowIfNull(sceneName);
        ArgumentNullException.ThrowIfNull(outputPath);
        ArgumentNullException.ThrowIfNull(diagnostics);

        using GameArchives archives = GameArchives.Open(sourceDirectory);

        using VulkanContext context = VulkanContext.CreateHeadless();
        _log($"device: {context.DeviceName}");

        using var renderer = SceneRenderer.Create(context);

        if (RayTracingSettings.Parse(rayTracing) is { } quality)
        {
            if (!renderer.SupportsRayTracing && quality != RayTracingQuality.None)
            {
                diagnostics.Add(new Diagnostic(
                    "SCENE009",
                    DiagnosticSeverity.Warning,
                    $"{context.DeviceName} offers no ray tracing; rendering without it."));
            }

            renderer.Quality = quality;
        }
        else if (rayTracing is not null)
        {
            diagnostics.Add(new Diagnostic(
                "SCENE010",
                DiagnosticSeverity.Error,
                $"Unknown ray tracing quality '{rayTracing}'; expected none, low, med or high."));

            return false;
        }

        using SceneGeometry geometry = renderer.CreateGeometry();

        SceneRequest request = SceneRequest.For(sceneName, timeblock);

        if (request.State is not null)
        {
            _log($"story: {request.State.Timeblock} in {request.State.Location}, first visit");
        }

        var loader = new SceneLoader(archives, _log);
        LoadedScene? scene = loader.Load(geometry, request, diagnostics);

        if (scene is null || geometry.TriangleCount == 0)
        {
            return false;
        }

        if (walkOverlay)
        {
            DrawWalkOverlay(geometry, scene);
        }

        renderer.SetLights(scene.Lights);

        _log($"lights: {scene.Lights.Count} authored " +
             $"({scene.Lights.Count(l => l.CastsShadows)} casting shadows in the bake)");

        Camera camera = SceneLoader.CameraFor(scene, geometry, cameraName);

        _log(string.Create(
            CultureInfo.InvariantCulture,
            $"camera: {scene.CameraNamed(cameraName)?.Name ?? "framed"} at " +
            $"({camera.Position.X:F1}, {camera.Position.Y:F1}, {camera.Position.Z:F1})"));

        _log($"drawing {geometry.TriangleCount} triangles in {geometry.BatchCount} batches");

        if (renderer.Quality != RayTracingQuality.None)
        {
            RayTracingSettings settings = RayTracingSettings.For(renderer.Quality);

            _log($"ray tracing {renderer.Quality}: {geometry.TraceableTriangleCount} opaque " +
                 $"triangles traced, {settings.ShadowLights} shadowed lights at " +
                 $"{settings.ShadowSamples} ray(s), {settings.AmbientOcclusionRays} occlusion rays");
        }

        DecodedImage image = renderer.Render(geometry, width, height, camera);

        string? directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllBytes(outputPath, PngWriter.Encode(image));
        _log($"wrote {outputPath}");

        return true;
    }

    /// <summary>Lays the walk boundary over the floor it describes.</summary>
    /// <remarks>
    /// Every part of a boundary — its row order, the sign of its offset, the size it is
    /// stretched to — produces a plausible-looking mask when it is wrong. Seeing it on the
    /// floor is the check, which is why `Plan/04` makes overlay validation an exit
    /// criterion for this phase rather than a nicety.
    /// </remarks>
    private void DrawWalkOverlay(SceneGeometry geometry, LoadedScene scene)
    {
        if (scene.Walkable is not { } boundary || scene.Geometry is not { } bsp)
        {
            _log("walk overlay: the scene declares no boundary");
            return;
        }

        IReadOnlyList<WalkOverlayPatch> patches =
            WalkOverlay.Build(bsp, scene.Definition.FloorObject(), boundary);

        foreach (WalkOverlayPatch patch in patches)
        {
            geometry.AddOverlay(
                $"walk-region-{patch.Region}", patch.Positions, patch.Indices, patch.Colour);
        }

        _log($"walk overlay: {patches.Sum(p => p.Indices.Length / 6)} texels over the floor, " +
             $"regions {string.Join(", ", patches.Select(p => p.Region))}");
    }
}
