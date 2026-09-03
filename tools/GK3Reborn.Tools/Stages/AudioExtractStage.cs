using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using GK3Reborn.Content;
using GK3Reborn.Content.Manifests;
using GK3Reborn.Formats;
using GK3Reborn.Formats.Animation;
using GK3Reborn.Formats.Audio;
using GK3Reborn.Formats.Barn;
using GK3Reborn.Foundation;
using GK3Reborn.Foundation.Diagnostics;

namespace GK3Reborn.Tools.Stages;

/// <summary>Extracts the untouched audio corpus and a restoration-ready PCM view.</summary>
/// <remarks>
/// A YAK supplies animation audio references, but those include door ambience, page turns
/// and telephone hooks as well as speech. GK3's voice-over assets follow the A-prefixed,
/// sequence-suffixed naming convention; referenced conventional WAVs remain in the general
/// audio lane. This avoids feeding animation sound effects to a speech model.
/// </remarks>
public sealed class AudioExtractStage
{
    /// <summary>Bump when path or decode semantics change.</summary>
    public const string StageVersion = "1.0.0";

    private readonly Action<string> _log;

    /// <summary>Creates the stage.</summary>
    public AudioExtractStage(Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
    }

    /// <summary>Writes raw and normalized audio without ever touching enhanced masters.</summary>
    public AudioManifest Run(
        string sourceDirectory,
        string workspaceDirectory,
        bool writeFiles,
        DiagnosticBag diagnostics)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceDirectory);
        ArgumentNullException.ThrowIfNull(diagnostics);

        string manifestDirectory = Path.Combine(workspaceDirectory, "manifests");
        Directory.CreateDirectory(manifestDirectory);

        if (writeFiles)
        {
            // Empty on first import. Restoration and deliberate promotion own their
            // contents; extraction only establishes the two lanes and never writes over
            // a reviewed master.
            Directory.CreateDirectory(Path.Combine(
                workspaceDirectory, "enhanced", "audio", "dialogue"));
            Directory.CreateDirectory(Path.Combine(
                workspaceDirectory, "enhanced", "audio", "sfx"));
        }

        List<FileInfo> files =
        [
            .. new DirectoryInfo(sourceDirectory)
                .EnumerateFiles("*.brn")
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase),
        ];

        using GameArchives game = GameArchives.Open(sourceDirectory);
        Dictionary<string, YakMetadata> yakAudio = YakIndex(game, diagnostics);
        _log($"YAK audio index: {yakAudio.Count} recording name(s)");

        var records = new List<AudioAssetRecord>();
        var seen = new HashSet<AssetId>();

        foreach (FileInfo file in files)
        {
            _log($"=== {file.Name}");
            using BarnArchive archive = BarnArchive.Open(file.FullName);
            int found = 0;

            foreach (BarnEntry entry in archive.Entries)
            {
                // The runtime searches volumes in this same order. A later duplicate is
                // not the asset the game would play, while a pointer carries no bytes and
                // lets the real entry in its named volume remain eligible.
                if (entry.IsPointer || !seen.Add(entry.Id))
                {
                    continue;
                }

                byte[] bytes;
                try
                {
                    bytes = archive.Extract(entry);
                }
                catch (Exception ex) when (ex is FormatParseException or IOException)
                {
                    diagnostics.Add(ex is FormatParseException parse
                        ? parse.Diagnostic
                        : Failure("GK3R2250", file.Name, entry.Name, ex.Message));
                    continue;
                }

                AssetClassification kind = AssetClassifier.Classify(
                    bytes.AsSpan(0, Math.Min(AssetClassifier.RequiredBytes, bytes.Length)));

                if (kind.Kind != AssetKind.Audio)
                {
                    continue;
                }

                found++;
                string name = SafeName(entry.Name);
                YakMetadata? metadata = yakAudio.GetValueOrDefault(entry.Name);
                string lane = metadata is not null && IsVoiceOver(entry.Name)
                    ? "dialogue"
                    : "sfx";
                string rawPath = Path.Combine(workspaceDirectory, "raw", "audio", lane, name);
                string normalizedPath = Path.Combine(
                    workspaceDirectory, "normalized", "audio", lane, name + ".wav");

                if (writeFiles)
                {
                    Write(rawPath, bytes);
                }

                var decodeDiagnostics = new DiagnosticBag();
                WavFile? sound = WavFile.Read(bytes, entry.Name, decodeDiagnostics);
                diagnostics.AddRange(decodeDiagnostics.Items);

                byte[]? normalized = sound?.ToPcmWave();
                if (writeFiles && normalized is not null)
                {
                    Write(normalizedPath, normalized);
                }

                records.Add(new AudioAssetRecord
                {
                    Name = entry.Name,
                    Archive = file.Name,
                    Lane = lane,
                    RawPath = Relative(workspaceDirectory, rawPath),
                    NormalizedPath = normalized is null
                        ? null
                        : Relative(workspaceDirectory, normalizedPath),
                    SourceHash = Hash(bytes),
                    NormalizedHash = normalized is null ? null : Hash(normalized),
                    Channels = sound?.Channels ?? 0,
                    SampleRate = sound?.SampleRate ?? 0,
                    Frames = sound?.FrameCount ?? 0,
                    Seconds = sound?.Duration ?? 0,
                    Yaks = metadata?.Yaks.Order(StringComparer.OrdinalIgnoreCase).ToArray() ?? [],
                    Speakers = metadata?.Speakers.Order(StringComparer.OrdinalIgnoreCase).ToArray() ?? [],
                    Captions = metadata?.Captions.Order(StringComparer.Ordinal).ToArray() ?? [],
                    Error = sound is null ? "The RIFF/WAVE payload could not be decoded." : null,
                });
            }

            _log(string.Create(CultureInfo.InvariantCulture, $"    {found} audio asset(s)"));
        }

        records.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        string[] unresolved =
        [
            .. yakAudio.Keys
                .Where(name => !records.Exists(r =>
                    r.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                .Order(StringComparer.OrdinalIgnoreCase),
        ];

        var manifest = new AudioManifest
        {
            SchemaVersion = 1,
            Stage = "C3.audio",
            StageVersion = StageVersion,
            SourceRoot = sourceDirectory.Replace('\\', '/'),
            Summary = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["assets"] = records.Count,
                ["dialogue"] = records.Count(r => r.Lane == "dialogue"),
                ["sfx"] = records.Count(r => r.Lane == "sfx"),
                ["normalized"] = records.Count(r => r.NormalizedPath is not null),
                ["failed"] = records.Count(r => r.Error is not null),
                ["yakReferencesWithoutAudio"] = unresolved.Length,
            },
            UnresolvedDialogueReferences = unresolved,
            Assets = records,
        };

        string manifestPath = Path.Combine(manifestDirectory, "audio.json");
        AtomicFile.WriteAllText(
            manifestPath, JsonSerializer.Serialize(manifest, ManifestJson.Options) + "\n");
        _log($"manifest: {manifestPath}");

        return manifest;
    }

    private static Dictionary<string, YakMetadata> YakIndex(
        GameArchives archives, DiagnosticBag diagnostics)
    {
        var result = new Dictionary<string, YakMetadata>(StringComparer.OrdinalIgnoreCase);

        foreach (string yak in archives.Names(".YAK"))
        {
            string? text = archives.ReadText(yak);
            if (text is null)
            {
                continue;
            }

            var local = new DiagnosticBag();
            AnimationFile animation;

            try
            {
                animation = AnimationFile.Parse(text, yak, local);
            }
            catch (FormatParseException ex)
            {
                diagnostics.Add(ex.Diagnostic);
                continue;
            }

            diagnostics.AddRange(local.Items);

            foreach (AnimationSound cue in animation.Sounds)
            {
                string sound = Resolve(archives, cue.Name);
                if (!result.TryGetValue(sound, out YakMetadata? metadata))
                {
                    result[sound] = metadata = new YakMetadata();
                }

                metadata.Yaks.Add(yak);
                foreach (AnimationCaption caption in animation.Captions)
                {
                    if (!string.IsNullOrWhiteSpace(caption.Speaker))
                    {
                        metadata.Speakers.Add(caption.Speaker);
                    }

                    if (!string.IsNullOrWhiteSpace(caption.Text))
                    {
                        metadata.Captions.Add(caption.Text);
                    }
                }
            }
        }

        return result;
    }

    private static bool IsVoiceOver(string name) =>
        Path.GetFileName(name).StartsWith('A')
        && !name.EndsWith(".WAV", StringComparison.OrdinalIgnoreCase);

    private static string Resolve(GameArchives archives, string name) =>
        archives.Exists(name) || Path.GetExtension(name).Length > 0 || !archives.Exists(name + ".WAV")
            ? name
            : name + ".WAV";

    private static string SafeName(string name)
    {
        string file = Path.GetFileName(name.Replace('\\', '/'));
        return string.IsNullOrWhiteSpace(file) || file is "." or ".."
            ? "unnamed-" + Hash(System.Text.Encoding.UTF8.GetBytes(name))[..16]
            : file;
    }

    private static void Write(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    private static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');

    private static string Hash(byte[] bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static Diagnostic Failure(
        string code, string archive, string asset, string actual) =>
        new(
            code,
            DiagnosticSeverity.Warning,
            $"Audio asset '{asset}' could not be extracted.",
            archive,
            null,
            "a readable Barn entry",
            actual,
            "Verify the installation and run extract-audio again.");

    private sealed class YakMetadata
    {
        public HashSet<string> Yaks { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Speakers { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Captions { get; } = new(StringComparer.Ordinal);
    }
}
