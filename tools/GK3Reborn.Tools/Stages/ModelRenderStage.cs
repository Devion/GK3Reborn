using System.Globalization;
using System.Numerics;
using GK3Reborn.Content;
using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Formats.Models;
using GK3Reborn.Foundation.Diagnostics;
using GK3Reborn.Game.Actors;
using GK3Reborn.Rendering;
using GK3Reborn.Rendering.Vulkan;

namespace GK3Reborn.Tools.Stages;

/// <summary>
/// Renders one model straight out of the game's archives to a PNG.
/// </summary>
/// <remarks>
/// <para>
/// The shortest path from shipped data to pixels: open the barns, parse the model, decode
/// the textures it names, upload both, draw. Nothing is pre-converted, so what this
/// produces is evidence about the parsers and the renderer together rather than about an
/// intermediate file.
/// </para>
/// <para>
/// It renders offscreen deliberately. A headless render needs no window, runs on a build
/// agent, and its output can be compared between runs — none of which is true of a
/// screenshot.
/// </para>
/// </remarks>
public sealed class ModelRenderStage
{
    private readonly Action<string> _log;

    /// <summary>Creates the stage.</summary>
    /// <param name="log">Progress sink.</param>
    public ModelRenderStage(Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
    }

    /// <summary>Renders one model.</summary>
    /// <param name="sourceDirectory">The game's <c>Data</c> directory.</param>
    /// <param name="modelName">Model name, with or without the <c>.MOD</c> extension.</param>
    /// <param name="outputPath">Where to write the PNG.</param>
    /// <param name="width">Image width.</param>
    /// <param name="height">Image height.</param>
    /// <param name="heads">How far to subdivide a character's head; zero draws it as authored.</param>
    /// <param name="diagnostics">Receives stage-level diagnostics.</param>
    /// <returns>True if something was rendered.</returns>
    public bool Run(
        string sourceDirectory,
        string modelName,
        string outputPath,
        int width,
        int height,
        int heads,
        DiagnosticBag diagnostics)
    {
        ArgumentNullException.ThrowIfNull(sourceDirectory);
        ArgumentNullException.ThrowIfNull(modelName);
        ArgumentNullException.ThrowIfNull(outputPath);
        ArgumentNullException.ThrowIfNull(diagnostics);

        string wanted = Path.GetExtension(modelName).Equals(".MOD", StringComparison.OrdinalIgnoreCase)
            ? modelName
            : modelName + ".MOD";

        using GameArchives archives = GameArchives.Open(sourceDirectory);

        byte[]? modelBytes = archives.Read(wanted);
        if (modelBytes is null)
        {
            diagnostics.Add(new Diagnostic(
                "RENDER001", DiagnosticSeverity.Error, $"No archive contains {wanted}."));

            return false;
        }

        ModFile parsed = ModFile.Parse(modelBytes, wanted);
        _log($"{wanted}: {parsed.Meshes.Count} meshes, {parsed.TriangleCount} triangles");

        // The same call the game makes, so what is rendered here is what a player sees
        // rather than a second implementation that could drift from it.
        (ModFile model, HeadRig? rig) = HeadRefinement.Apply(parsed, heads);

        if (rig is not null)
        {
            _log(string.Create(CultureInfo.InvariantCulture,
                $"head: mesh {rig.Mesh}, {rig.Span:F1} units across, refined {heads} " +
                $"level(s) to {model.TriangleCount} triangles"));
        }

        using VulkanContext context = VulkanContext.CreateHeadless();
        _log($"device: {context.DeviceName}");

        using var renderer = SceneRenderer.Create(context);
        using SceneGeometry geometry = renderer.CreateGeometry();

        foreach (string texture in model.Meshes
                     .SelectMany(m => m.Submeshes)
                     .Select(s => s.TextureName)
                     .Where(n => n.Length > 0)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            byte[]? bytes = archives.Read(texture) ?? archives.Read(texture + ".BMP");
            if (bytes is null || !BitmapDecoder.CanDecode(bytes))
            {
                diagnostics.Add(new Diagnostic(
                    "RENDER002",
                    DiagnosticSeverity.Warning,
                    $"{wanted} references a texture no archive contains: {texture}."));

                continue;
            }

            geometry.AddTexture(texture, BitmapDecoder.Decode(bytes, texture));
        }

        geometry.Add(model);

        if (geometry.TriangleCount == 0)
        {
            diagnostics.Add(new Diagnostic(
                "RENDER003", DiagnosticSeverity.Error, $"{wanted} has no drawable geometry."));

            return false;
        }

        Vector3 minimum = geometry.Minimum;
        Vector3 maximum = geometry.Maximum;

        _log(string.Create(
            CultureInfo.InvariantCulture,
            $"bounds: ({minimum.X:F1}, {minimum.Y:F1}, {minimum.Z:F1}) .. " +
            $"({maximum.X:F1}, {maximum.Y:F1}, {maximum.Z:F1})"));

        _log($"textures: {geometry.TextureCount}, triangles: {geometry.TriangleCount}");

        // GK3 is Y-up: model bounds are consistently tallest on Y, and every sun direction
        // recovered from the lightmaps points down that axis.
        DecodedImage image = renderer.Render(
            geometry, width, height, Camera.Framing(minimum, maximum, Vector3.UnitY));

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
