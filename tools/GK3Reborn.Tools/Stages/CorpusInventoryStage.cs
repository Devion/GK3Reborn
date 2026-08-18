using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GK3Reborn.Content.Manifests;
using GK3Reborn.Formats;
using GK3Reborn.Formats.Barn;
using GK3Reborn.Foundation;
using GK3Reborn.Foundation.Diagnostics;

namespace GK3Reborn.Tools.Stages;

/// <summary>
/// Pipeline stage C2: classifies every asset and maps what references what.
/// </summary>
/// <remarks>
/// <para>
/// C1 proves the archives can be read. C2 answers what is in them. Both questions have
/// to be settled before anything downstream can claim completeness, and the second one
/// is where the surprises are: the corpus appears to hold 2,775 file types but really
/// holds about a dozen, because most audio assets carry a three-character dialogue code
/// as their extension instead of <c>.WAV</c>.
/// </para>
/// <para>
/// The reference scan is deliberately shallow. It reads the text assets - scenes,
/// action sets, animation scripts, soundtracks - and resolves every asset-shaped token
/// against the corpus index. That finds dangling references without needing a parser
/// for each format, and it is honest about what it is: candidates, not a parse.
/// </para>
/// </remarks>
public sealed partial class CorpusInventoryStage
{
    private readonly Action<string> _log;

    /// <summary>Creates the stage.</summary>
    /// <param name="log">Progress sink.</param>
    public CorpusInventoryStage(Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
    }

    /// <summary>Builds the inventory.</summary>
    /// <param name="sourceDirectory">The game's <c>Data</c> directory.</param>
    /// <param name="workspaceDirectory">Content workspace root.</param>
    /// <param name="diagnostics">Receives stage-level diagnostics.</param>
    /// <returns>The corpus manifest.</returns>
    public CorpusManifest Run(string sourceDirectory, string workspaceDirectory, DiagnosticBag diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);

        string manifestDirectory = Path.Combine(workspaceDirectory, "manifests");
        Directory.CreateDirectory(manifestDirectory);

        List<FileInfo> archiveFiles =
        [
            .. new DirectoryInfo(sourceDirectory)
                .EnumerateFiles("*.brn")
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase),
        ];

        // Pass one: classify everything, and build the index the reference scan resolves
        // against. Text assets are held back so every name is known before scanning.
        Dictionary<AssetId, CorpusAsset> assets = [];
        List<(AssetId Id, string Archive, byte[] Data)> textAssets = [];

        foreach (FileInfo archiveFile in archiveFiles)
        {
            _log($"=== {archiveFile.Name}");
            using BarnArchive archive = BarnArchive.Open(archiveFile.FullName);

            int classified = 0;
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
                    continue;
                }

                AssetClassification classification = AssetClassifier.Classify(
                    data.AsSpan(0, Math.Min(AssetClassifier.RequiredBytes, data.Length)));

                string extension = Path.GetExtension(entry.Name).TrimStart('.').ToUpperInvariant();

                assets[entry.Id] = new CorpusAsset
                {
                    Name = entry.Name,
                    Archive = archiveFile.Name,
                    Extension = extension.Length == 0 ? null : extension,
                    Kind = classification.Kind.ToString(),
                    Basis = classification.Basis,
                    Magic = classification.Magic,
                    Bytes = data.Length,
                };

                if (classification.Kind is AssetKind.Text or AssetKind.Html)
                {
                    textAssets.Add((entry.Id, archiveFile.Name, data));
                }

                classified++;
            }

            _log(string.Create(CultureInfo.InvariantCulture, $"    {classified} assets classified"));
        }

        // Pass two: resolve references now that every name is known.
        _log($"scanning {textAssets.Count} text assets for references");
        List<CorpusDanglingReference> dangling = [];
        int resolved = 0;

        foreach ((AssetId id, string archive, byte[] data) in textAssets)
        {
            string text = Encoding.Latin1.GetString(data);
            HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

            foreach (Match match in AssetReference().Matches(text))
            {
                string referenced = match.Value;
                if (!seen.Add(referenced))
                {
                    continue;
                }

                if (assets.ContainsKey(AssetId.FromExact(referenced)))
                {
                    resolved++;
                }
                else
                {
                    dangling.Add(new CorpusDanglingReference
                    {
                        From = assets[id].Name,
                        FromArchive = archive,
                        Reference = referenced,
                    });
                }
            }
        }

        List<CorpusAsset> ordered = [.. assets.Values.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)];

        var manifest = new CorpusManifest
        {
            SchemaVersion = 1,
            Stage = "C2.corpus",
            SourceRoot = sourceDirectory.Replace('\\', '/'),
            Summary = new CorpusSummary
            {
                Assets = ordered.Count,
                TotalBytes = ordered.Sum(a => (long)a.Bytes),
                DistinctExtensions = ordered.Select(a => a.Extension).Distinct().Count(),
                ReferencesResolved = resolved,
                ReferencesDangling = dangling.Count,
            },
            KindCounts = Aggregate(ordered, a => a.Kind),
            KindBytes = AggregateBytes(ordered),
            ExtensionsByKind = ExtensionsPerKind(ordered),
            Unknown = [.. ordered.Where(a => a.Kind == nameof(AssetKind.Unknown)).Take(200)],
            DanglingReferences = [.. dangling
                .OrderBy(d => d.From, StringComparer.OrdinalIgnoreCase)
                .ThenBy(d => d.Reference, StringComparer.OrdinalIgnoreCase)
                .Take(500)],
            Assets = ordered,
        };

        string manifestPath = Path.Combine(manifestDirectory, "corpus.json");
        AtomicFile.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, ManifestJson.Options) + "\n");
        _log($"manifest: {manifestPath}");

        int unknown = manifest.KindCounts.GetValueOrDefault(nameof(AssetKind.Unknown));
        if (unknown > 0)
        {
            // The C2 gate is that no required file type is unexplained. Unknown entries
            // are not fatal, but they are the backlog, so they are reported rather than
            // reduced to a line in a summary.
            diagnostics.Add(new Diagnostic(
                "GK3R2300", DiagnosticSeverity.Warning,
                $"{unknown} assets could not be classified.",
                null, null, "every asset to match a known signature or be text",
                $"{unknown} unclassified",
                "See the 'unknown' list in corpus.json; each entry records its leading bytes."));
        }

        if (dangling.Count > 0)
        {
            diagnostics.Add(new Diagnostic(
                "GK3R2301", DiagnosticSeverity.Warning,
                $"{dangling.Count} asset references do not resolve.",
                null, null, "every referenced asset to exist in the corpus",
                $"{dangling.Count} dangling",
                "See 'danglingReferences' in corpus.json. Some are expected: text assets "
                + "mention filenames that are generated at runtime or belong to other editions."));
        }

        return manifest;
    }

    private static Dictionary<string, int> Aggregate(IEnumerable<CorpusAsset> assets, Func<CorpusAsset, string> key)
    {
        Dictionary<string, int> counts = new(StringComparer.Ordinal);
        foreach (CorpusAsset asset in assets)
        {
            counts[key(asset)] = counts.GetValueOrDefault(key(asset)) + 1;
        }

        return counts.OrderByDescending(kv => kv.Value).ToDictionary(StringComparer.Ordinal);
    }

    private static Dictionary<string, long> AggregateBytes(IEnumerable<CorpusAsset> assets)
    {
        Dictionary<string, long> bytes = new(StringComparer.Ordinal);
        foreach (CorpusAsset asset in assets)
        {
            bytes[asset.Kind] = bytes.GetValueOrDefault(asset.Kind) + asset.Bytes;
        }

        return bytes.OrderByDescending(kv => kv.Value).ToDictionary(StringComparer.Ordinal);
    }

    /// <summary>
    /// Records how many distinct extensions each kind hides behind.
    /// </summary>
    /// <remarks>
    /// This is the number that shows why classification cannot go by name: audio spans
    /// thousands of extensions, while every other kind is essentially well behaved.
    /// </remarks>
    private static Dictionary<string, int> ExtensionsPerKind(IEnumerable<CorpusAsset> assets)
    {
        Dictionary<string, HashSet<string>> extensions = new(StringComparer.Ordinal);
        foreach (CorpusAsset asset in assets)
        {
            if (!extensions.TryGetValue(asset.Kind, out HashSet<string>? set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                extensions[asset.Kind] = set;
            }

            set.Add(asset.Extension ?? "(none)");
        }

        return extensions
            .OrderByDescending(kv => kv.Value.Count)
            .ToDictionary(kv => kv.Key, kv => kv.Value.Count, StringComparer.Ordinal);
    }

    /// <summary>
    /// Matches an asset-shaped token: a name followed by one of the known extensions.
    /// </summary>
    /// <remarks>
    /// Audio is deliberately absent. Its extensions are dialogue codes rather than a
    /// fixed set, so including them would match arbitrary words followed by a period.
    /// </remarks>
    [GeneratedRegex(
        @"\b[A-Za-z0-9_\-]{1,32}\.(?:BMP|MOD|ACT|ANM|YAK|MUL|BSP|SIF|SCN|NVC|SHP|GAS|SEQ|FON|CUR|STK|TXT|HTM|HTML|WAV)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AssetReference();
}
