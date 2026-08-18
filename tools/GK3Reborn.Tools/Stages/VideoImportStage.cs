using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using GK3Reborn.Content.Manifests;
using GK3Reborn.Foundation;
using GK3Reborn.Foundation.Diagnostics;
using GK3Reborn.Tools.Media;

namespace GK3Reborn.Tools.Stages;

/// <summary>
/// Pipeline stage C7.video: converts the GK3 cinematic corpus to the runtime format.
/// </summary>
/// <remarks>
/// <para>
/// Target format, from Plan/02-content-pipeline.md section 2: MP4 / H.264 with
/// <c>+faststart</c>, AAC 192 kbps resampled once to 48 kHz to match the mixer rate.
/// Frame size, frame rate and duration are preserved exactly and verified afterwards.
/// </para>
/// <para>
/// Outputs are keyed by uppercase base name with no extension, because GK3 data
/// references videos that way - GEngine's <c>VideoHelper</c> strips the extension
/// deliberately so localizations can substitute AVI for BIK.
/// </para>
/// </remarks>
public sealed class VideoImportStage
{
    /// <summary>Converter identity recorded in the manifest.</summary>
    public const string ConverterName = "gk3reborn.video";

    /// <summary>
    /// Converter version. Bumping this invalidates every cached output, because the
    /// recipe that produced them is no longer the current one.
    /// </summary>
    public const string ConverterVersion = "1.0.0";

    private const double DurationToleranceSeconds = 0.10;

    private readonly FfmpegTools _tools;
    private readonly Action<string> _log;

    /// <summary>Creates the stage.</summary>
    /// <param name="tools">Located FFmpeg toolchain.</param>
    /// <param name="log">Progress sink.</param>
    public VideoImportStage(FfmpegTools tools, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(log);
        _tools = tools;
        _log = log;
    }

    /// <summary>
    /// Converts every BIK and AVI under <paramref name="sourceDirectory"/>.
    /// </summary>
    /// <param name="sourceDirectory">The game's <c>Data</c> directory. Never written to.</param>
    /// <param name="workspaceDirectory">Content workspace root.</param>
    /// <param name="force">Reconvert even when a cached output is still valid.</param>
    /// <param name="diagnostics">Receives stage-level diagnostics.</param>
    /// <returns>The manifest describing every source file's disposition.</returns>
    public VideoManifest Run(
        string sourceDirectory,
        string workspaceDirectory,
        bool force,
        DiagnosticBag diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);

        string outputDirectory = Path.Combine(workspaceDirectory, "build", "video");
        string manifestDirectory = Path.Combine(workspaceDirectory, "manifests");
        Directory.CreateDirectory(outputDirectory);
        Directory.CreateDirectory(manifestDirectory);

        List<FileInfo> sources = [.. new DirectoryInfo(sourceDirectory)
            .EnumerateFiles()
            .Where(f => f.Extension.Equals(".bik", StringComparison.OrdinalIgnoreCase) ||
                        f.Extension.Equals(".avi", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)];

        if (sources.Count == 0)
        {
            diagnostics.Add(new Diagnostic(
                "GK3R2100", DiagnosticSeverity.Error,
                "No BIK or AVI files were found.",
                sourceDirectory, null, "the game's Data directory", "no media files",
                "Point --source at the Data directory of a GK3 installation."));
        }

        VideoManifest? previous = TryLoadPrevious(Path.Combine(manifestDirectory, "video.json"));
        List<VideoEntry> entries = [];

        for (int i = 0; i < sources.Count; i++)
        {
            FileInfo source = sources[i];
            string logicalId = AssetId.From(source.Name).Value;
            _log($"[{i + 1,2}/{sources.Count}] {source.Name}");
            entries.Add(Convert(source, logicalId, outputDirectory, previous, force));
        }

        entries.Sort((a, b) => string.CompareOrdinal(a.LogicalId, b.LogicalId));

        Dictionary<string, int> summary = new(StringComparer.Ordinal) { ["total"] = entries.Count };
        foreach (VideoEntry e in entries)
        {
            string key = StatusKey(e.Status);
            summary[key] = summary.GetValueOrDefault(key) + 1;
        }

        var manifest = new VideoManifest
        {
            SchemaVersion = 1,
            Stage = "C7.video",
            ConverterVersion = ConverterVersion,
            SourceRoot = Normalize(sourceDirectory),
            OutputRoot = Normalize(outputDirectory),
            Summary = summary,
            Entries = entries,
        };

        string manifestPath = Path.Combine(manifestDirectory, "video.json");
        AtomicFile.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, ManifestJson.Options) + "\n");
        _log($"manifest: {manifestPath}");

        foreach (VideoEntry e in entries.Where(e => !e.IsPlayable))
        {
            diagnostics.Add(new Diagnostic(
                e.Status == VideoEntryStatus.UnreadableSource ? "GK3R2101" : "GK3R2102",
                DiagnosticSeverity.Warning,
                $"Video '{e.LogicalId}' has no playable output ({StatusKey(e.Status)}).",
                e.Source.File,
                null,
                "a decodable video container",
                e.Diagnostic?.ProbeError ?? e.Diagnostic?.FfmpegError,
                e.Diagnostic?.Remediation));
        }

        return manifest;
    }

    private VideoEntry Convert(
        FileInfo source,
        string logicalId,
        string outputDirectory,
        VideoManifest? previous,
        bool force)
    {
        string sourceHash = Sha256File(source.FullName);
        string outputPath = Path.Combine(outputDirectory, logicalId + ".mp4");

        if (!force &&
            previous?.Entries.FirstOrDefault(e => e.LogicalId == logicalId) is { } cached &&
            cached.Recipe?.ConverterVersion == ConverterVersion &&
            cached.Source.Sha256 == sourceHash &&
            cached.Output is { } cachedOutput &&
            File.Exists(outputPath) &&
            Sha256File(outputPath) == cachedOutput.Sha256)
        {
            _log("    up to date");
            return cached;
        }

        using JsonDocument? probeJson = _tools.Probe(source.FullName, out string? probeError);
        MediaProbe? probe = probeJson is null ? null : MediaProbe.FromJson(probeJson);

        if (probe is null)
        {
            _log($"    UNREADABLE: {probeError}");
            return new VideoEntry
            {
                LogicalId = logicalId,
                Status = VideoEntryStatus.UnreadableSource,
                Source = new VideoMedia
                {
                    File = source.Name,
                    Bytes = source.Length,
                    Sha256 = sourceHash,
                },
                Diagnostic = new VideoDiagnostic
                {
                    ProbeError = probeError,
                    Remediation = "The file is not a recognized video container. "
                                + "Verify the installation's integrity or re-acquire it.",
                },
            };
        }

        List<string> arguments = BuildArguments(source.FullName, probe);
        ProcessResult result = _tools.RunFfmpeg([.. arguments, outputPath]);

        if (!result.Succeeded)
        {
            _log("    CONVERSION FAILED");
            return new VideoEntry
            {
                LogicalId = logicalId,
                Status = VideoEntryStatus.ConversionFailed,
                Source = Describe(source.Name, source.Length, sourceHash, probe),
                Diagnostic = new VideoDiagnostic
                {
                    FfmpegError = Truncate(result.StandardError, 2000),
                    Remediation = "Inspect the encoder error; the source may use an unsupported "
                                + "pixel format or an unusual frame size.",
                },
            };
        }

        using JsonDocument? outputJson = _tools.Probe(outputPath, out _);
        MediaProbe? outputProbe = outputJson is null ? null : MediaProbe.FromJson(outputJson);
        var outputInfo = new FileInfo(outputPath);

        var validation = new VideoValidation
        {
            DimensionsMatch = outputProbe is { } o1 && o1.Width == probe.Width && o1.Height == probe.Height,
            FrameRateMatch = outputProbe?.FrameRate == probe.FrameRate,
            DurationDriftSeconds = Math.Round((outputProbe?.DurationSeconds ?? 0) - probe.DurationSeconds, 4),
            DurationWithinTolerance =
                Math.Abs((outputProbe?.DurationSeconds ?? 0) - probe.DurationSeconds) <= DurationToleranceSeconds,
            AudioPreserved = (probe.AudioCodec is null) == (outputProbe?.AudioCodec is null),
        };

        _log(string.Create(CultureInfo.InvariantCulture,
            $"    -> {logicalId}.mp4  {probe.Width}x{probe.Height}  "
            + $"{probe.DurationSeconds:F1}s  {outputInfo.Length / 1_000_000.0:F1} MB"
            + $"{(validation.AllPassed ? string.Empty : "  WARNINGS")}"));

        return new VideoEntry
        {
            LogicalId = logicalId,
            Status = validation.AllPassed
                ? VideoEntryStatus.Converted
                : VideoEntryStatus.ConvertedWithWarnings,
            Source = Describe(source.Name, source.Length, sourceHash, probe),
            Output = outputProbe is null
                ? null
                : Describe($"video/{logicalId}.mp4", outputInfo.Length, Sha256File(outputPath), outputProbe),
            Validation = validation,
            Recipe = new VideoRecipe
            {
                Converter = ConverterName,
                ConverterVersion = ConverterVersion,
                Arguments = [.. arguments, "<output>"],
            },
        };
    }

    /// <summary>
    /// Builds the encoder command for one source.
    /// </summary>
    /// <remarks>
    /// H.264 4:2:0 requires even frame dimensions, and several Sidney scan clips are
    /// odd sized (41x51, 389x424, 431x350). Padding or cropping would shift the UI
    /// overlays those clips sit under, so odd-sized sources encode as 4:4:4 instead,
    /// which permits any dimension. They are tiny, so the cost is negligible.
    /// </remarks>
    public static List<string> BuildArguments(string sourcePath, MediaProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);

        List<string> args =
        [
            "-y", "-hide_banner", "-nostdin", "-loglevel", "error",
            "-i", sourcePath,
            "-map", "0:v:0",
            "-c:v", "libx264", "-preset", "slow", "-crf", "16",
        ];

        args.AddRange(probe.HasOddDimensions
            ? ["-pix_fmt", "yuv444p", "-profile:v", "high444"]
            : ["-pix_fmt", "yuv420p", "-profile:v", "high"]);

        args.AddRange(["-fps_mode", "passthrough", "-movflags", "+faststart"]);

        if (probe.AudioCodec is not null)
        {
            args.AddRange(["-map", "0:a:0", "-c:a", "aac", "-b:a", "192k", "-ar", "48000"]);
        }

        return args;
    }

    private static VideoMedia Describe(string file, long bytes, string hash, MediaProbe probe) => new()
    {
        File = file,
        Bytes = bytes,
        Sha256 = hash,
        Container = probe.Container,
        VideoCodec = probe.VideoCodec,
        Width = probe.Width,
        Height = probe.Height,
        FrameRate = probe.FrameRate,
        DurationSeconds = probe.DurationSeconds,
        AudioCodec = probe.AudioCodec,
        AudioSampleRate = probe.AudioSampleRate,
        AudioChannels = probe.AudioChannels,
    };

    private static VideoManifest? TryLoadPrevious(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<VideoManifest>(File.ReadAllText(path), ManifestJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string StatusKey(VideoEntryStatus status) => status switch
    {
        VideoEntryStatus.Converted => "converted",
        VideoEntryStatus.ConvertedWithWarnings => "converted-with-warnings",
        VideoEntryStatus.UnreadableSource => "unreadable-source",
        VideoEntryStatus.ConversionFailed => "conversion-failed",
        _ => "unknown",
    };

    private static string Sha256File(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return System.Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static string Normalize(string path) => path.Replace('\\', '/');

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value.Trim() : value[..max].Trim();
}
