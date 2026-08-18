using System.Globalization;
using System.Text.Json;
using GK3Reborn.Content.Manifests;
using GK3Reborn.Formats;
using GK3Reborn.Formats.Barn;
using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Formats.Models;
using GK3Reborn.Formats.Scenes;
using GK3Reborn.Foundation;
using GK3Reborn.Foundation.Diagnostics;

namespace GK3Reborn.Tools.Stages;

/// <summary>
/// Pipeline stage C3: lays the corpus out sensibly and converts what needs converting.
/// </summary>
/// <remarks>
/// <para>
/// C1 dumps every archive entry into one directory per archive, which is faithful and
/// unusable: 15,966 files land in a single folder and the largest texture format is one
/// nothing outside the game can open.
/// </para>
/// <para>
/// This stage produces the tree people actually work in. Assets are grouped by what they
/// are, textures become PNG, and two groupings the data genuinely supports are applied:
/// animations by the character prefix their names carry, and scene assets by the
/// three-letter location code GK3 uses throughout. Everything else stays flat inside its
/// kind, because inventing structure the data does not support is worse than none.
/// </para>
/// <para>
/// The raw extraction stays untouched. This is a derived view, and re-running it is
/// always safe.
/// </para>
/// </remarks>
public sealed class OrganizeStage
{
    /// <summary>Stage version. Bumping it invalidates a previous organize run.</summary>
    public const string StageVersion = "1.0.0";

    private readonly Action<string> _log;

    /// <summary>Creates the stage.</summary>
    /// <param name="log">Progress sink.</param>
    public OrganizeStage(Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
    }

    /// <summary>Builds the normalized tree.</summary>
    /// <param name="sourceDirectory">The game's <c>Data</c> directory.</param>
    /// <param name="workspaceDirectory">Content workspace root.</param>
    /// <param name="diagnostics">Receives stage-level diagnostics.</param>
    /// <returns>The manifest describing where everything went.</returns>
    public OrganizedManifest Run(string sourceDirectory, string workspaceDirectory, DiagnosticBag diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);

        string root = Path.Combine(workspaceDirectory, "normalized");
        string manifestDirectory = Path.Combine(workspaceDirectory, "manifests");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(manifestDirectory);

        List<FileInfo> archives =
        [
            .. new DirectoryInfo(sourceDirectory)
                .EnumerateFiles("*.brn")
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase),
        ];

        List<OrganizedAsset> placed = [];
        int converted = 0;
        int failed = 0;
        long bytesIn = 0;
        long bytesOut = 0;

        foreach (FileInfo archiveFile in archives)
        {
            _log($"=== {archiveFile.Name}");
            using BarnArchive archive = BarnArchive.Open(archiveFile.FullName);

            foreach (BarnEntry entry in archive.Entries)
            {
                if (entry.IsPointer)
                {
                    continue;
                }

                byte[] data;
                try
                {
                    data = archive.Extract(entry);
                }
                catch (FormatParseException ex)
                {
                    diagnostics.Add(ex.Diagnostic);
                    failed++;
                    continue;
                }

                bytesIn += data.Length;

                AssetClassification classification = AssetClassifier.Classify(
                    data.AsSpan(0, Math.Min(AssetClassifier.RequiredBytes, data.Length)));

                string directory = DirectoryFor(entry.Name, classification.Kind);
                string outputName = Path.GetFileName(entry.Name);
                byte[] output = data;
                string? conversion = null;

                // Textures are the only conversion here. Everything else keeps its bytes:
                // converting a format before it is understood loses information silently.
                if (classification.Kind is AssetKind.BitmapGk3 or AssetKind.BitmapWindows &&
                    BitmapDecoder.CanDecode(data))
                {
                    try
                    {
                        DecodedImage image = BitmapDecoder.Decode(data, entry.Name);
                        output = PngWriter.Encode(image);
                        outputName = Path.ChangeExtension(outputName, ".png");
                        conversion = $"{image.SourceFormat} -> png"
                            + (image.HasAlpha ? " (magenta keyed to transparent)" : string.Empty);
                        converted++;
                    }
                    catch (FormatParseException ex)
                    {
                        diagnostics.Add(ex.Diagnostic);
                        failed++;
                    }
                }
                else if (classification.Kind == AssetKind.SceneGeometry)
                {
                    try
                    {
                        BspFile scene = BspFile.Parse(data, entry.Name);
                        output = SceneGlbWriter.Encode(scene);
                        outputName = Path.ChangeExtension(outputName, ".glb");
                        conversion = string.Create(CultureInfo.InvariantCulture,
                            $"bsp -> glb ({scene.ObjectNames.Count} objects, "
                            + $"{scene.Vertices.Length} vertices, {scene.TriangleCount} triangles)");
                        converted++;
                    }
                    catch (FormatParseException ex)
                    {
                        diagnostics.Add(ex.Diagnostic);
                        failed++;
                    }
                }
                else if (classification.Kind == AssetKind.Model)
                {
                    try
                    {
                        ModFile mod = ModFile.Parse(data, entry.Name);
                        output = GlbWriter.Encode(mod);
                        outputName = Path.ChangeExtension(outputName, ".glb");
                        conversion = string.Create(CultureInfo.InvariantCulture,
                            $"mod -> glb ({mod.Meshes.Count} meshes, {mod.VertexCount} vertices, "
                            + $"{mod.TriangleCount} triangles)");
                        converted++;
                    }
                    catch (FormatParseException ex)
                    {
                        diagnostics.Add(ex.Diagnostic);
                        failed++;
                    }
                }

                string fullDirectory = Path.Combine(root, directory);
                Directory.CreateDirectory(fullDirectory);
                File.WriteAllBytes(Path.Combine(fullDirectory, outputName), output);
                bytesOut += output.Length;

                placed.Add(new OrganizedAsset
                {
                    Name = entry.Name,
                    Path = $"{directory.Replace('\\', '/')}/{outputName}",
                    Kind = classification.Kind.ToString(),
                    SourceBytes = data.Length,
                    OutputBytes = output.Length,
                    Conversion = conversion,
                });
            }
        }

        placed.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));

        var manifest = new OrganizedManifest
        {
            SchemaVersion = 1,
            Stage = "C3.organize",
            StageVersion = StageVersion,
            SourceRoot = sourceDirectory.Replace('\\', '/'),
            OutputRoot = root.Replace('\\', '/'),
            Summary = new OrganizedSummary
            {
                Assets = placed.Count,
                Converted = converted,
                Failed = failed,
                SourceBytes = bytesIn,
                OutputBytes = bytesOut,
            },
            DirectoryCounts = Counts(placed),
            Assets = placed,
        };

        string manifestPath = Path.Combine(manifestDirectory, "organized.json");
        AtomicFile.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, ManifestJson.Options) + "\n");
        _log($"manifest: {manifestPath}");

        return manifest;
    }

    /// <summary>
    /// Chooses the directory an asset belongs in.
    /// </summary>
    /// <remarks>
    /// Only two sub-groupings are applied, because only two are supported by the data:
    /// 5,789 of 5,798 animation files carry a <c>CHARACTER_</c> prefix, and scene assets
    /// are named after the three-letter location codes the game uses for its rooms.
    /// Audio is sharded by initial letter purely so no directory holds 7,852 files; that
    /// is a mechanical split and claims nothing about the contents.
    /// </remarks>
    public static string DirectoryFor(string assetName, AssetKind kind)
    {
        ArgumentNullException.ThrowIfNull(assetName);

        string stem = Path.GetFileNameWithoutExtension(assetName).ToUpperInvariant();

        return kind switch
        {
            AssetKind.BitmapGk3 or AssetKind.BitmapWindows => "textures",
            AssetKind.Model => "models",
            AssetKind.ActorAnimation => Path.Combine("animations", CharacterPrefix(stem)),
            AssetKind.SceneGeometry or AssetKind.Lightmap => Path.Combine("scenes", LocationCode(stem)),
            AssetKind.Audio => Path.Combine("audio", Shard(stem)),
            AssetKind.SheepBytecode => "scripts",
            AssetKind.Html => Path.Combine("ui", "sidney"),
            AssetKind.Font => Path.Combine("ui", "fonts"),
            AssetKind.DesignDocument => "documentation",
            AssetKind.Executable or AssetKind.ZipArchive => "original-tools",
            AssetKind.Text => TextDirectory(assetName, stem),
            _ => "unclassified",
        };
    }

    private static string TextDirectory(string assetName, string stem)
    {
        string extension = Path.GetExtension(assetName).TrimStart('.').ToUpperInvariant();

        return extension switch
        {
            "SIF" or "SCN" => Path.Combine("scenes", LocationCode(stem)),
            "NVC" => "actions",
            "YAK" => "dialogue",
            "ANM" or "GAS" or "SEQ" => "animation-scripts",
            "STK" => "soundtracks",
            _ => "text",
        };
    }

    /// <summary>Takes the character prefix an animation name carries, if any.</summary>
    private static string CharacterPrefix(string stem)
    {
        int underscore = stem.IndexOf('_', StringComparison.Ordinal);
        return underscore > 0 ? stem[..underscore] : "_unprefixed";
    }

    /// <summary>Takes the three-letter location code a scene asset is named for.</summary>
    private static string LocationCode(string stem) =>
        stem.Length >= 3 ? stem[..3] : "_short";

    private static string Shard(string stem) =>
        stem.Length > 0 && char.IsLetterOrDigit(stem[0]) ? stem[..1] : "_other";

    private static Dictionary<string, int> Counts(IEnumerable<OrganizedAsset> assets)
    {
        Dictionary<string, int> counts = new(StringComparer.Ordinal);

        foreach (OrganizedAsset asset in assets)
        {
            // Report the top level only; the per-character and per-location splits below
            // it would bury the shape of the tree in hundreds of rows.
            string top = asset.Path.Split('/')[0];
            counts[top] = counts.GetValueOrDefault(top) + 1;
        }

        return counts.OrderByDescending(kv => kv.Value).ToDictionary(StringComparer.Ordinal);
    }
}
