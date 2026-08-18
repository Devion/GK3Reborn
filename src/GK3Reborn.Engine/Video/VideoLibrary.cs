using System.Text.Json;
using GK3Reborn.Content.Manifests;
using GK3Reborn.Foundation;

namespace GK3Reborn.Video;

/// <summary>
/// Resolves a logical video id to a playable file produced by the import pipeline.
/// </summary>
/// <remarks>
/// Game data names videos without an extension, so lookup is by <see cref="AssetId"/>.
/// A video that failed to import resolves to nothing and is reported as a missing
/// asset rather than silently playing black - Plan/01-architecture.md section 4.
/// </remarks>
public sealed class VideoLibrary
{
    private readonly Dictionary<AssetId, string> _paths = [];
    private readonly Dictionary<AssetId, VideoEntry> _entries = [];

    private VideoLibrary(VideoManifest manifest, string outputRoot)
    {
        Manifest = manifest;
        foreach (VideoEntry entry in manifest.Entries)
        {
            AssetId id = AssetId.FromExact(entry.LogicalId);
            _entries[id] = entry;
            if (entry.IsPlayable && entry.Output is { } output)
            {
                _paths[id] = Path.Combine(outputRoot, output.File.Replace('/', Path.DirectorySeparatorChar));
            }
        }
    }

    /// <summary>The manifest this library was built from.</summary>
    public VideoManifest Manifest { get; }

    /// <summary>Ids that resolve to a playable file.</summary>
    public IReadOnlyCollection<AssetId> PlayableIds => _paths.Keys;

    /// <summary>Every id known to the manifest, playable or not.</summary>
    public IReadOnlyCollection<AssetId> KnownIds => _entries.Keys;

    /// <summary>Loads a library from a manifest file.</summary>
    /// <param name="manifestPath">Path to <c>manifests/video.json</c>.</param>
    /// <returns>The loaded library.</returns>
    public static VideoLibrary Load(string manifestPath)
    {
        string json = File.ReadAllText(manifestPath);
        VideoManifest manifest = JsonSerializer.Deserialize<VideoManifest>(json, ManifestJson.Options)
            ?? throw new InvalidDataException($"Video manifest is empty: {manifestPath}");

        // Outputs are recorded relative to the workspace's build directory.
        string workspace = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(manifestPath)))
            ?? Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
        return new VideoLibrary(manifest, Path.Combine(workspace, "build"));
    }

    /// <summary>Creates a library directly from a manifest instance.</summary>
    /// <param name="manifest">The manifest.</param>
    /// <param name="buildRoot">Directory that <c>Output.File</c> paths are relative to.</param>
    public static VideoLibrary FromManifest(VideoManifest manifest, string buildRoot) =>
        new(manifest, buildRoot);

    /// <summary>Tries to resolve a playable file path for a video name.</summary>
    /// <param name="name">Video name, with or without extension.</param>
    /// <param name="path">Receives the absolute path when found.</param>
    /// <returns>True when a playable file exists.</returns>
    public bool TryResolve(string name, out string? path) =>
        _paths.TryGetValue(AssetId.From(name), out path);

    /// <summary>Gets the manifest entry for a video name, playable or not.</summary>
    public VideoEntry? GetEntry(string name) =>
        _entries.GetValueOrDefault(AssetId.From(name));
}
