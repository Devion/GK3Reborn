using System.Globalization;
using System.Numerics;
using System.Text.Json;
using GK3Reborn.Content.Authoring;
using GK3Reborn.Content.Manifests;
using GK3Reborn.Formats;
using GK3Reborn.Formats.Barn;
using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Formats.Lightmaps;
using GK3Reborn.Formats.Scenes;
using GK3Reborn.Foundation;
using GK3Reborn.Foundation.Diagnostics;
using GK3Reborn.Rendering.Lighting;

namespace GK3Reborn.Tools.Stages;

/// <summary>
/// Stage C4b: proposes a light rig per scene and time of day.
/// </summary>
/// <remarks>
/// <para>
/// Implements ADR 0002 as amended. For each scene and timeblock, surfaces are reduced to
/// a centroid, an area-weighted normal and their measured baked brightness, and light
/// sources are fitted to that.
/// </para>
/// <para>
/// Where a scene has a night bake alongside a daylight one, the two are differenced
/// first. Night shows only artificial light, so subtracting it isolates the sun and the
/// remainder identifies the practicals — which is a far better-posed problem than trying
/// to explain a single bake containing both at once.
/// </para>
/// <para>
/// Output goes to <c>content/lighting/</c> in the rig format the renderer reads, marked
/// <c>derived</c> with a confidence per light. Corrections belong in the paired
/// <c>.edits.json</c> file, which this stage never writes (ADR 0006).
/// </para>
/// </remarks>
public sealed class LightRigStage
{
    private readonly Action<string> _log;

    /// <summary>Creates the stage.</summary>
    /// <param name="log">Progress sink.</param>
    public LightRigStage(Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
    }

    /// <summary>Derives the rigs.</summary>
    /// <param name="sourceDirectory">The game's <c>Data</c> directory.</param>
    /// <param name="workspaceDirectory">Content workspace root.</param>
    /// <param name="diagnostics">Receives stage-level diagnostics.</param>
    /// <returns>How many rigs were written.</returns>
    public LightRigSummary Run(string sourceDirectory, string workspaceDirectory, DiagnosticBag diagnostics)
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
                if (extension is not ("BSP" or "MUL") &&
                    !extension.Equals("BSP", StringComparison.OrdinalIgnoreCase) &&
                    !extension.Equals("MUL", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    byte[] data = archive.Extract(entry);
                    string stem = Path.GetFileNameWithoutExtension(entry.Name);

                    if (extension.Equals("BSP", StringComparison.OrdinalIgnoreCase))
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

        string outputRoot = Path.Combine(workspaceDirectory, "content", "lighting");
        Directory.CreateDirectory(outputRoot);

        int rigs = 0;
        int lights = 0;
        int lowConfidence = 0;
        int unlit = 0;

        // Group lightmap sets by the scene they light, so a scene's timeblocks are
        // available together and can be differenced.
        var groups = lightmaps
            .Select(kv => (Set: kv.Key, Scene: MatchScene(kv.Key, scenes), Lightmaps: kv.Value))
            .Where(x => x.Scene is not null)
            .GroupBy(x => x.Scene!, StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups)
        {
            BspFile scene = scenes[group.Key];
            SurfaceGeometry[] geometry = MeasureGeometry(scene);

            // Night is the reference for differencing, when the scene has one.
            var night = group.FirstOrDefault(x => Timeblock(x.Set) == "N");
            float[]? nightBrightness = night.Lightmaps is null
                ? null
                : Brightness(geometry, night.Lightmaps);

            foreach (var variant in group)
            {
                string timeblock = Timeblock(variant.Set);
                float[] brightness = Brightness(geometry, variant.Lightmaps);

                SceneLightRig rig = Derive(
                    variant.Set, timeblock, geometry, brightness,
                    timeblock == "N" ? null : nightBrightness);

                if (rig.Lights.Count == 0)
                {
                    unlit++;
                }

                lights += rig.Lights.Count;
                lowConfidence += rig.Lights.Count(l => l.Confidence < 0.35f);

                AuthoringStore.Save(Path.Combine(outputRoot, $"{variant.Set}.lighting.json"), rig);
                rigs++;
            }
        }

        _log($"{rigs} rigs, {lights} lights");

        if (unlit > 0)
        {
            diagnostics.Add(new Diagnostic(
                "GK3R2600", DiagnosticSeverity.Warning,
                $"{unlit} scenes yielded no lights.",
                null, null, "at least one derivable source", $"{unlit} with none",
                "These need lighting authored by hand; their bakes carry too little "
                + "variation to fit a source to."));
        }

        return new LightRigSummary(rigs, lights, lowConfidence, unlit);
    }

    private static SceneLightRig Derive(
        string setName,
        string timeblock,
        SurfaceGeometry[] geometry,
        float[] brightness,
        float[]? nightBrightness)
    {
        List<LitSurface> daylight = [];
        List<LitSurface> artificial = [];

        for (int i = 0; i < geometry.Length; i++)
        {
            SurfaceGeometry g = geometry[i];
            if (g.Area <= 0)
            {
                continue;
            }

            // Subtracting the night bake leaves only what the sun contributed. Without a
            // night bake the whole measurement has to serve for both, which is weaker.
            float artificialPart = nightBrightness is null ? 0 : nightBrightness[i];
            float daylightPart = MathF.Max(0, brightness[i] - artificialPart);

            var sample = new LitSurface(g.Centroid, g.Normal, g.Area, daylightPart, g.Color);
            daylight.Add(sample);
            artificial.Add(sample with { Brightness = nightBrightness is null ? brightness[i] : artificialPart });
        }

        List<SceneLight> result = [];

        if (nightBrightness is not null || timeblock != "N")
        {
            if (LightEstimator.FitDirectional(daylight) is { } sun && sun.Confidence > 0.1f)
            {
                result.Add(new SceneLight
                {
                    Id = "sun",
                    Kind = SceneLightKind.Spot,
                    Position = -sun.Direction * 10000f,
                    Direction = sun.Direction,
                    Color = sun.Color,
                    Intensity = sun.Intensity,
                    Radius = 100000f,
                    ConeAngleRadians = MathF.PI,
                    Provenance = AuthoringProvenance.Derived,
                    Confidence = sun.Confidence,
                    ReviewNote = nightBrightness is null
                        ? "fitted to the whole bake; no night variant existed to separate artificial light"
                        : "fitted to the daylight bake minus the night bake",
                });
            }
        }

        float extent = Extent(geometry);
        IReadOnlyList<PointEstimate> practicals =
            LightEstimator.FitPointLights(artificial, MathF.Max(extent * 0.15f, 50f));

        for (int i = 0; i < practicals.Count; i++)
        {
            PointEstimate p = practicals[i];
            result.Add(new SceneLight
            {
                Id = string.Create(CultureInfo.InvariantCulture, $"practical-{i}"),
                Kind = SceneLightKind.Point,
                Position = p.Position,
                Color = p.Color,
                Intensity = p.Intensity,
                Radius = p.Radius,
                Provenance = AuthoringProvenance.Derived,
                Confidence = p.Confidence,
                ReviewNote = "clustered from surfaces lit when the sun is not",
            });
        }

        return new SceneLightRig
        {
            SchemaVersion = 1,
            SceneId = setName,
            Lights = result,
            SignedOff = false,
        };
    }

    /// <summary>Reduces every surface to a centroid, normal, area and colour.</summary>
    private static SurfaceGeometry[] MeasureGeometry(BspFile scene)
    {
        SurfaceGeometry[] result = new SurfaceGeometry[scene.Surfaces.Count];
        Vector3[] centroid = new Vector3[scene.Surfaces.Count];
        Vector3[] normal = new Vector3[scene.Surfaces.Count];
        float[] area = new float[scene.Surfaces.Count];
        int[] count = new int[scene.Surfaces.Count];

        foreach (BspPolygon polygon in scene.Polygons)
        {
            int s = polygon.SurfaceIndex;

            foreach ((ushort a, ushort b, ushort c) in scene.Triangulate(polygon))
            {
                Vector3 p0 = scene.Vertices[a];
                Vector3 p1 = scene.Vertices[b];
                Vector3 p2 = scene.Vertices[c];

                Vector3 cross = Vector3.Cross(p1 - p0, p2 - p0);
                float triangleArea = cross.Length() * 0.5f;

                // Weighting the normal by area keeps a sliver from swinging the average.
                normal[s] += cross;
                centroid[s] += (p0 + p1 + p2) / 3 * triangleArea;
                area[s] += triangleArea;
                count[s]++;
            }
        }

        for (int i = 0; i < result.Length; i++)
        {
            result[i] = new SurfaceGeometry
            {
                Centroid = area[i] > 0 ? centroid[i] / area[i] : Vector3.Zero,
                Normal = normal[i].LengthSquared() > 1e-6f ? Vector3.Normalize(normal[i]) : Vector3.UnitY,
                Area = area[i],
                Color = Vector3.One,
            };
        }

        return result;
    }

    private static float[] Brightness(SurfaceGeometry[] geometry, MulFile lightmaps)
    {
        float[] result = new float[geometry.Length];

        for (int i = 0; i < geometry.Length && i < lightmaps.Lightmaps.Count; i++)
        {
            DecodedImage lightmap = lightmaps.Lightmaps[i];
            int pixels = lightmap.Width * lightmap.Height;
            if (pixels == 0)
            {
                continue;
            }

            double sum = 0;
            for (int p = 0; p < pixels; p++)
            {
                int at = p * 4;
                sum += ((0.2126 * lightmap.Pixels[at]) +
                        (0.7152 * lightmap.Pixels[at + 1]) +
                        (0.0722 * lightmap.Pixels[at + 2])) / 255.0;
            }

            result[i] = (float)(sum / pixels);
        }

        return result;
    }

    private static float Extent(SurfaceGeometry[] geometry)
    {
        if (geometry.Length == 0)
        {
            return 100;
        }

        Vector3 min = new(float.MaxValue);
        Vector3 max = new(float.MinValue);

        foreach (SurfaceGeometry g in geometry)
        {
            min = Vector3.Min(min, g.Centroid);
            max = Vector3.Max(max, g.Centroid);
        }

        return (max - min).Length();
    }

    private static string? MatchScene(string setName, Dictionary<string, BspFile> scenes)
    {
        if (scenes.ContainsKey(setName))
        {
            return setName;
        }

        int underscore = setName.LastIndexOf('_');
        if (underscore > 0 && scenes.ContainsKey(setName[..underscore]))
        {
            return setName[..underscore];
        }

        return null;
    }

    private static string Timeblock(string setName)
    {
        int underscore = setName.LastIndexOf('_');
        string suffix = underscore > 0 ? setName[(underscore + 1)..] : string.Empty;
        return suffix is "M" or "A" or "E" or "N" ? suffix : "-";
    }

    private sealed record SurfaceGeometry
    {
        public required Vector3 Centroid { get; init; }

        public required Vector3 Normal { get; init; }

        public required float Area { get; init; }

        public required Vector3 Color { get; init; }
    }
}

/// <summary>Totals from a light-rig derivation run.</summary>
/// <param name="Rigs">Rigs written.</param>
/// <param name="Lights">Lights proposed.</param>
/// <param name="LowConfidence">Lights whose fit is weak enough to need review first.</param>
/// <param name="ScenesWithoutLights">Scenes that yielded nothing and need authoring.</param>
public readonly record struct LightRigSummary(int Rigs, int Lights, int LowConfidence, int ScenesWithoutLights);
