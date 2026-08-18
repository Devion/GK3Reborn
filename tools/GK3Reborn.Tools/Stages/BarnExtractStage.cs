using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using GK3Reborn.Content.Manifests;
using GK3Reborn.Formats;
using GK3Reborn.Formats.Barn;
using GK3Reborn.Foundation;
using GK3Reborn.Foundation.Diagnostics;

namespace GK3Reborn.Tools.Stages;

/// <summary>
/// Pipeline stage C1: extracts every entry from every Barn archive.
/// </summary>
/// <remarks>
/// <para>
/// This is the gate the rest of the content pipeline stands on: until every entry in
/// every archive has a disposition, nothing downstream can claim completeness. The
/// stage therefore records an outcome for every entry rather than stopping at the
/// first failure, and writes a manifest that later stages and the reference graph read.
/// </para>
/// <para>
/// The source installation is opened read-only and never written to.
/// </para>
/// </remarks>
public sealed class BarnExtractStage
{
    /// <summary>Converter identity recorded in the manifest.</summary>
    public const string ExtractorName = "gk3reborn.barn";

    /// <summary>Extractor version. Bumping it invalidates cached extractions.</summary>
    public const string ExtractorVersion = "1.0.0";

    private readonly Action<string> _log;

    /// <summary>Creates the stage.</summary>
    /// <param name="log">Progress sink.</param>
    public BarnExtractStage(Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
    }

    /// <summary>
    /// Extracts every archive under <paramref name="sourceDirectory"/>.
    /// </summary>
    /// <param name="sourceDirectory">The game's <c>Data</c> directory. Never written to.</param>
    /// <param name="workspaceDirectory">Content workspace root.</param>
    /// <param name="writeFiles">
    /// When false, entries are decompressed and validated but not written to disk. Useful
    /// for verifying an installation without spending the space.
    /// </param>
    /// <param name="diagnostics">Receives stage-level diagnostics.</param>
    /// <returns>The manifest describing every entry's disposition.</returns>
    public BarnManifest Run(
        string sourceDirectory,
        string workspaceDirectory,
        bool writeFiles,
        DiagnosticBag diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);

        string rawRoot = Path.Combine(workspaceDirectory, "raw");
        string manifestDirectory = Path.Combine(workspaceDirectory, "manifests");
        Directory.CreateDirectory(manifestDirectory);

        List<FileInfo> archives =
        [
            .. new DirectoryInfo(sourceDirectory)
                .EnumerateFiles("*.brn")
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase),
        ];

        if (archives.Count == 0)
        {
            diagnostics.Add(new Diagnostic(
                "GK3R2200", DiagnosticSeverity.Error,
                "No Barn archives were found.",
                sourceDirectory, null, "at least core.brn", "no .brn files",
                "Point --source at the Data directory of a GK3 installation."));
        }

        List<BarnArchiveRecord> records = [];

        foreach (FileInfo file in archives)
        {
            records.Add(ExtractArchive(file, rawRoot, writeFiles, diagnostics));
        }

        var manifest = new BarnManifest
        {
            SchemaVersion = 1,
            Stage = "C1.barn",
            ExtractorVersion = ExtractorVersion,
            SourceRoot = sourceDirectory.Replace('\\', '/'),
            OutputRoot = writeFiles ? rawRoot.Replace('\\', '/') : null,
            Summary = Summarize(records),
            Archives = records,
        };

        string manifestPath = Path.Combine(manifestDirectory, "barn.json");
        AtomicFile.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, ManifestJson.Options) + "\n");
        _log($"manifest: {manifestPath}");

        return manifest;
    }

    private BarnArchiveRecord ExtractArchive(
        FileInfo file, string rawRoot, bool writeFiles, DiagnosticBag diagnostics)
    {
        _log($"=== {file.Name} ({file.Length / 1_048_576.0:F0} MB)");

        BarnArchive archive;
        try
        {
            archive = BarnArchive.Open(file.FullName);
        }
        catch (FormatParseException ex)
        {
            diagnostics.Add(ex.Diagnostic);
            return new BarnArchiveRecord
            {
                File = file.Name,
                Bytes = file.Length,
                EntryCount = 0,
                Extracted = 0,
                Pointers = 0,
                Failed = 0,
                Failures = [],
                CompressionCounts = new Dictionary<string, int>(StringComparer.Ordinal),
            };
        }

        using (archive)
        {
            string outputDirectory = Path.Combine(rawRoot, Path.GetFileNameWithoutExtension(file.Name));
            if (writeFiles)
            {
                Directory.CreateDirectory(outputDirectory);
            }

            int extracted = 0;
            int pointers = 0;
            long totalBytes = 0;
            List<BarnFailure> failures = [];
            Dictionary<string, int> compression = new(StringComparer.Ordinal);

            foreach (BarnEntry entry in archive.Entries)
            {
                string key = entry.Compression.ToString().ToLowerInvariant();
                compression[key] = compression.GetValueOrDefault(key) + 1;

                if (entry.IsPointer)
                {
                    pointers++;
                    continue;
                }

                try
                {
                    byte[] data = archive.Extract(entry);
                    totalBytes += data.Length;
                    extracted++;

                    if (writeFiles)
                    {
                        File.WriteAllBytes(SafeOutputPath(outputDirectory, entry.Name), data);
                    }
                }
                catch (Exception ex) when (ex is FormatParseException or InvalidDataException or IOException)
                {
                    failures.Add(new BarnFailure
                    {
                        Name = entry.Name,
                        Compression = key,
                        Offset = entry.Offset,
                        Size = entry.Size,
                        Error = ex is FormatParseException parse ? parse.Diagnostic.ToString() : ex.Message,
                    });
                }
            }

            _log(string.Create(CultureInfo.InvariantCulture,
                $"    {archive.Count} entries, {extracted} extracted, {pointers} pointers, "
                + $"{failures.Count} failed, {totalBytes / 1_048_576.0:F0} MB out"));

            foreach (BarnFailure failure in failures.Take(5))
            {
                _log($"    FAILED {failure.Name}: {failure.Error}");
            }

            if (failures.Count > 0)
            {
                diagnostics.Add(new Diagnostic(
                    "GK3R2201", DiagnosticSeverity.Error,
                    $"{failures.Count} of {archive.Count} entries in '{file.Name}' could not be extracted.",
                    file.Name, null, "every entry to extract",
                    $"{failures.Count} failures",
                    "Inspect the failures in the manifest; they name the entry, offset and cause."));
            }

            return new BarnArchiveRecord
            {
                File = file.Name,
                Bytes = file.Length,
                EntryCount = archive.Count,
                Extracted = extracted,
                Pointers = pointers,
                Failed = failures.Count,
                ExtractedBytes = totalBytes,
                Failures = failures,
                CompressionCounts = compression,
            };
        }
    }

    /// <summary>
    /// Builds an output path, refusing anything that would escape the output directory.
    /// </summary>
    /// <remarks>
    /// Archive entry names come from a file the project does not control. A name
    /// containing <c>..</c> or an absolute path would otherwise let an extraction write
    /// anywhere on disk.
    /// </remarks>
    private static string SafeOutputPath(string outputDirectory, string entryName)
    {
        string fileName = Path.GetFileName(entryName.Replace('\\', '/'));
        if (string.IsNullOrWhiteSpace(fileName) || fileName is "." or "..")
        {
            fileName = "unnamed-" + Convert.ToHexStringLower(
                SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(entryName)))[..16];
        }

        string full = Path.GetFullPath(Path.Combine(outputDirectory, fileName));
        string root = Path.GetFullPath(outputDirectory) + Path.DirectorySeparatorChar;

        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            ? full
            : Path.Combine(root, "quarantined-" + Path.GetRandomFileName());
    }

    private static Dictionary<string, int> Summarize(IEnumerable<BarnArchiveRecord> records)
    {
        Dictionary<string, int> summary = new(StringComparer.Ordinal)
        {
            ["archives"] = 0,
            ["entries"] = 0,
            ["extracted"] = 0,
            ["pointers"] = 0,
            ["failed"] = 0,
        };

        foreach (BarnArchiveRecord record in records)
        {
            summary["archives"]++;
            summary["entries"] += record.EntryCount;
            summary["extracted"] += record.Extracted;
            summary["pointers"] += record.Pointers;
            summary["failed"] += record.Failed;
        }

        return summary;
    }
}
