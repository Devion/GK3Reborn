using System.Globalization;
using GK3Reborn.Content;
using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Foundation.Diagnostics;
using GK3Reborn.Game;
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

        using GameArchives archives = GameArchives.Open(sourceDirectory);

        using VulkanContext context = VulkanContext.CreateHeadless();
        _log($"device: {context.DeviceName}");

        using var renderer = SceneRenderer.Create(context);
        using SceneGeometry geometry = renderer.CreateGeometry();

        var loader = new SceneLoader(archives, _log);
        LoadedScene? scene = loader.Load(geometry, sceneName, timeblock, diagnostics);

        if (scene is null || geometry.TriangleCount == 0)
        {
            return false;
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
}
