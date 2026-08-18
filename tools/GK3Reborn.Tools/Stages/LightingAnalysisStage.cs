using System.Globalization;
using System.Numerics;
using System.Text.Json;
using GK3Reborn.Content.Manifests;
using GK3Reborn.Formats;
using GK3Reborn.Formats.Barn;
using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Formats.Lightmaps;
using GK3Reborn.Formats.Scenes;
using GK3Reborn.Foundation;
using GK3Reborn.Foundation.Diagnostics;

namespace GK3Reborn.Tools.Stages;

/// <summary>
/// Measures the baked lighting, as the evidence base for stage C4b.
/// </summary>
/// <remarks>
/// <para>
/// Before proposing light rigs it is worth establishing whether the lightmaps contain
/// enough structure to propose them from. This stage answers that with measurements
/// rather than assertion: how bright each surface is, what colour its light is, how much
/// the light varies across the surface, and how much it changes between times of day.
/// </para>
/// <para>
/// A surface lit evenly tells you almost nothing about where its light came from. A
/// surface with a strong gradient tells you a direction. The ratio between those two
/// populations is what decides whether the derivation in ADR 0002 can work at all.
/// </para>
/// <para>
/// Timeblock differencing is the other half. A night bake shows only artificial light,
/// so a surface that is bright in the morning and dark at night is sun-driven, while one
/// that holds steady is lit by a practical.
/// </para>
/// </remarks>
public sealed class LightingAnalysisStage
{
    private readonly Action<string> _log;

    /// <summary>Creates the stage.</summary>
    /// <param name="log">Progress sink.</param>
    public LightingAnalysisStage(Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
    }

    /// <summary>Analyses the baked lighting.</summary>
    /// <param name="sourceDirectory">The game's <c>Data</c> directory.</param>
    /// <param name="workspaceDirectory">Content workspace root.</param>
    /// <param name="diagnostics">Receives stage-level diagnostics.</param>
    /// <returns>The analysis.</returns>
    public LightingAnalysisManifest Run(
        string sourceDirectory, string workspaceDirectory, DiagnosticBag diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);

        Dictionary<string, BspFile> scenes = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, MulFile> lightmaps = new(StringComparer.OrdinalIgnoreCase);

        foreach (FileInfo archiveFile in new DirectoryInfo(sourceDirectory)
                     .EnumerateFiles("*.brn")
                     .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
        {
            _log($"=== {archiveFile.Name}");
            using BarnArchive archive = BarnArchive.Open(archiveFile.FullName);

            foreach (BarnEntry entry in archive.Entries)
            {
                if (entry.IsPointer)
                {
                    continue;
                }

                string extension = Path.GetExtension(entry.Name).TrimStart('.');
                bool isScene = extension.Equals("BSP", StringComparison.OrdinalIgnoreCase);
                bool isLightmap = extension.Equals("MUL", StringComparison.OrdinalIgnoreCase);

                if (!isScene && !isLightmap)
                {
                    continue;
                }

                try
                {
                    byte[] data = archive.Extract(entry);
                    string stem = Path.GetFileNameWithoutExtension(entry.Name);

                    if (isScene)
                    {
                        scenes[stem] = BspFile.Parse(data, entry.Name);
                    }
                    else
                    {
                        lightmaps[stem] = MulFile.Parse(data, entry.Name);
                    }
                }
                catch (FormatParseException ex)
                {
                    diagnostics.Add(ex.Diagnostic);
                }
            }
        }

        _log($"{scenes.Count} scenes, {lightmaps.Count} lightmap sets");

        List<SceneLighting> results = [];
        int unpaired = 0;

        foreach ((string setName, MulFile set) in lightmaps.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            // A set is named after its scene plus a timeblock letter, so try the full name
            // first and then the name with the suffix removed.
            string? sceneName = null;
            string timeblock = "-";

            if (scenes.ContainsKey(setName))
            {
                sceneName = setName;
            }
            else
            {
                int underscore = setName.LastIndexOf('_');
                if (underscore > 0)
                {
                    string candidate = setName[..underscore];
                    if (scenes.ContainsKey(candidate))
                    {
                        sceneName = candidate;
                        timeblock = setName[(underscore + 1)..];
                    }
                }
            }

            if (sceneName is null)
            {
                unpaired++;
                continue;
            }

            results.Add(Analyse(scenes[sceneName], set, sceneName, setName, timeblock));
        }

        if (unpaired > 0)
        {
            diagnostics.Add(new Diagnostic(
                "GK3R2500", DiagnosticSeverity.Warning,
                $"{unpaired} lightmap sets could not be matched to a scene.",
                null, null, "a scene of the same name", $"{unpaired} unmatched",
                "These may light scenes that are named differently or built at runtime."));
        }

        var manifest = new LightingAnalysisManifest
        {
            SchemaVersion = 1,
            Stage = "C4b.lighting-analysis",
            SourceRoot = sourceDirectory.Replace('\\', '/'),
            Summary = Summarize(results),
            Scenes = [.. results.OrderBy(r => r.SetName, StringComparer.Ordinal)],
        };

        string path = Path.Combine(workspaceDirectory, "manifests", "lighting-analysis.json");
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(manifest, ManifestJson.Options) + "\n");
        _log($"manifest: {path}");

        return manifest;
    }

    private static SceneLighting Analyse(
        BspFile scene, MulFile set, string sceneName, string setName, string timeblock)
    {
        int surfaces = Math.Min(scene.Surfaces.Count, set.Lightmaps.Count);

        double totalLuminance = 0;
        Vector3 totalColor = Vector3.Zero;
        int directional = 0;
        int flat = 0;
        int dark = 0;

        for (int i = 0; i < surfaces; i++)
        {
            DecodedImage lightmap = set.Lightmaps[i];
            (double mean, double range, Vector3 color) = Measure(lightmap);

            totalLuminance += mean;
            totalColor += color;

            if (mean < 0.04)
            {
                dark++;
            }
            else if (range > 0.15)
            {
                // Enough variation across the surface to imply a direction.
                directional++;
            }
            else
            {
                flat++;
            }
        }

        Vector3 average = surfaces > 0 ? totalColor / surfaces : Vector3.Zero;

        return new SceneLighting
        {
            Scene = sceneName,
            SetName = setName,
            Timeblock = timeblock,
            Surfaces = surfaces,
            SurfaceCountMismatch = scene.Surfaces.Count != set.Lightmaps.Count,
            MeanLuminance = Math.Round(surfaces > 0 ? totalLuminance / surfaces : 0, 4),
            MeanColor = string.Create(CultureInfo.InvariantCulture,
                $"#{(int)(average.X * 255):X2}{(int)(average.Y * 255):X2}{(int)(average.Z * 255):X2}"),
            DirectionalSurfaces = directional,
            FlatSurfaces = flat,
            DarkSurfaces = dark,
        };
    }

    /// <summary>
    /// Measures one lightmap: how bright it is, how much it varies, and its colour.
    /// </summary>
    /// <remarks>
    /// Range is taken between the fifth and ninety-fifth percentile rather than absolute
    /// minimum and maximum, so one stray texel does not make an evenly lit surface look
    /// directional.
    /// </remarks>
    private static (double Mean, double Range, Vector3 Color) Measure(DecodedImage lightmap)
    {
        int count = lightmap.Width * lightmap.Height;
        if (count == 0)
        {
            return (0, 0, Vector3.Zero);
        }

        double[] luminance = new double[count];
        Vector3 color = Vector3.Zero;

        for (int i = 0; i < count; i++)
        {
            int at = i * 4;
            double r = lightmap.Pixels[at] / 255.0;
            double g = lightmap.Pixels[at + 1] / 255.0;
            double b = lightmap.Pixels[at + 2] / 255.0;

            luminance[i] = (0.2126 * r) + (0.7152 * g) + (0.0722 * b);
            color += new Vector3((float)r, (float)g, (float)b);
        }

        Array.Sort(luminance);
        double low = luminance[(int)(count * 0.05)];
        double high = luminance[(int)Math.Min(count - 1, count * 0.95)];

        return (luminance.Average(), high - low, color / count);
    }

    private static LightingSummary Summarize(List<SceneLighting> results)
    {
        int directional = results.Sum(r => r.DirectionalSurfaces);
        int flat = results.Sum(r => r.FlatSurfaces);
        int dark = results.Sum(r => r.DarkSurfaces);

        // How many scenes carry more than one time of day, which is what makes
        // differencing possible.
        int withVariants = results
            .GroupBy(r => r.Scene, StringComparer.OrdinalIgnoreCase)
            .Count(g => g.Count() > 1);

        return new LightingSummary
        {
            Sets = results.Count,
            Scenes = results.Select(r => r.Scene).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            ScenesWithTimeblockVariants = withVariants,
            DirectionalSurfaces = directional,
            FlatSurfaces = flat,
            DarkSurfaces = dark,
            DirectionalFraction = Math.Round(
                directional + flat + dark > 0 ? (double)directional / (directional + flat + dark) : 0, 4),
            SurfaceCountMismatches = results.Count(r => r.SurfaceCountMismatch),
        };
    }
}
