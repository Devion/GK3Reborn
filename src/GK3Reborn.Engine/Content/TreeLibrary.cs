using System.Text.Json;
using System.Text.Json.Serialization;
using GK3Reborn.Formats;
using GK3Reborn.Formats.Models;
using GK3Reborn.Formats.Rebarn;
using GK3Reborn.Foundation.Diagnostics;

namespace GK3Reborn.Content;

/// <summary>One grown tree: a species, a variant of it, and the shape it came out.</summary>
public sealed record GrownTree
{
    /// <summary>File name, without extension: <c>spruce_02</c>.</summary>
    public required string Name { get; init; }

    /// <summary>Which species this is a variant of.</summary>
    public required string Species { get; init; }

    /// <summary>How far the crown reaches from the trunk, as a fraction of the height.</summary>
    public required float Radius { get; init; }

    /// <summary>Triangles in the grown mesh.</summary>
    public required int Triangles { get; init; }

    /// <summary>Whether this is the full tree or the cheap one grown for a far hillside.</summary>
    public required bool Far { get; init; }
}

/// <summary>What a species is, and which of the game's sprites it stands in for.</summary>
public sealed record TreeSpecies
{
    /// <summary>Its name: <c>spruce</c>, <c>maple</c>.</summary>
    public required string Name { get; init; }

    /// <summary>
    /// Whether the grown unit is the crown alone rather than a whole tree.
    /// </summary>
    public required bool Canopy { get; init; }

    /// <summary>The original sprites this species replaces.</summary>
    public required IReadOnlyList<string> Sprites { get; init; }

    /// <summary>The bitmap its leaves are painted with: <c>RBN_MAPLE_CLUMP</c>.</summary>
    public required string Card { get; init; }

    /// <summary>The variants grown for it, in name order.</summary>
    public required IReadOnlyList<GrownTree> Variants { get; init; }

    /// <summary>The full ones, for the trees a player walks up to.</summary>
    public IReadOnlyList<GrownTree> Near => [.. Variants.Where(v => !v.Far)];

    /// <summary>
    /// The cheap ones, for the rest of the hillside.
    /// </summary>
    public IReadOnlyList<GrownTree> Distant => [.. Variants.Where(v => v.Far)];
}

/// <summary>
/// The modelled trees that stand in for GK3's foliage cards.
/// </summary>
public sealed class TreeLibrary
{
    private static readonly JsonSerializerOptions Lenient =
        new() { PropertyNameCaseInsensitive = true };

    private readonly Dictionary<string, TreeSpecies> _species =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, string> _bySprite =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, ModFile?> _grown =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly RebarnContent? _packs;

    private TreeLibrary(string directory, RebarnContent? packs)
    {
        Directory = directory;
        Packed = packs is not null;
        _packs = packs;

        // Loose PNGs only. A packed card has been encoded to a block format and is found by
        // the ordinary compressed-texture path under its own name, so the loader needs no
        // help with it; a loose one is a PNG in this directory and does.
        //
        // The overrides come with it, because this set is asked before the enhanced one for
        // every texture a tree names: a foliage card replaced in overrides/ that only
        // reached the layer underneath would never be looked at.
        Textures = EnhancedTextures.Open(directory, packs?.Overrides);
    }

    /// <summary>Where the trees were read from.</summary>
    public string Directory { get; }

    /// <summary>Whether any of this came out of a ReBarn pack rather than a directory.</summary>
    public bool Packed { get; }

    /// <summary>
    /// The foliage the trees are painted with, which ships with them.
    /// </summary>
    public EnhancedTextures Textures { get; }

    /// <summary>How many species have at least one variant to draw.</summary>
    public int SpeciesCount => _species.Count;

    /// <summary>How many grown trees are available across every species.</summary>
    public int Count => _species.Values.Sum(s => s.Variants.Count);

    /// <summary>Whether there is anything here to draw.</summary>
    public bool IsEmpty => Count == 0;

    /// <summary>The species, in name order.</summary>
    public IReadOnlyList<TreeSpecies> Species =>
        [.. _species.Values.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)];

    /// <summary>The leaf bitmaps of every species here, which is what moves in the wind.</summary>
    public IReadOnlySet<string> Cards =>
        _species.Values
            .Select(s => Path.GetFileNameWithoutExtension(s.Card))
            .Where(c => c.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Indexes a directory of grown trees and the manifest beside them.</summary>
    /// <param name="directory">Where they are.</param>
    /// <param name="diagnostics">Receives a warning when the manifest will not read.</param>
    /// <returns>The library, empty when there is nothing there.</returns>
    public static TreeLibrary Open(string directory, DiagnosticBag? diagnostics = null) =>
        Open(directory, null, diagnostics);

    /// <summary>Indexes a directory of grown trees, a set of ReBarn packs, or both.</summary>
    /// <param name="directory">Where the loose ones are. May be empty or missing.</param>
    /// <param name="packs">Packs beside the executable, or null for none.</param>
    /// <param name="diagnostics">Receives a warning when the manifest will not read.</param>
    /// <returns>The library, empty when neither has anything.</returns>
    public static TreeLibrary Open(
        string directory, RebarnContent? packs, DiagnosticBag? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(directory);

        var library = new TreeLibrary(directory, packs);

        string manifest = Path.Combine(directory, "trees.json");
        bool loose = directory.Length > 0 &&
                     System.IO.Directory.Exists(directory) &&
                     File.Exists(manifest);

        if (!loose && packs?.Has(RebarnKind.Manifest, "trees") != true)
        {
            return library;
        }

        string where = loose ? manifest : (packs?.Directory ?? "<packs>");
        TreeManifest? read;

        try
        {
            byte[] bytes = loose
                ? File.ReadAllBytes(manifest)
                : packs!.Read(RebarnKind.Manifest, "trees") ?? [];

            read = bytes.Length == 0
                ? null
                : JsonSerializer.Deserialize<TreeManifest>(bytes, Lenient);
        }
        catch (Exception ex) when (ex is JsonException or IOException or FormatParseException)
        {
            diagnostics?.Add(new Diagnostic(
                "GK3R1122",
                DiagnosticSeverity.Warning,
                $"The tree manifest will not read, so every tree keeps its card: {ex.Message}",
                where,
                null,
                "a readable trees.json",
                ex.GetType().Name,
                "Grow the trees again with tools/blender/grow_trees.py."));

            return library;
        }

        if (read?.Species is null || read.Trees is null)
        {
            return library;
        }

        foreach ((string name, TreeManifestSpecies described) in read.Species)
        {
            // Only species with a grown variant on disk. A manifest that describes a
            // species nobody has grown yet would otherwise take the card away and put
            // nothing in its place, which is a hole in a hillside.
            List<GrownTree> variants =
            [
                .. read.Trees
                    .Where(t => string.Equals(t.Species, name, StringComparison.OrdinalIgnoreCase))
                    .Where(t => t.Name is { Length: > 0 } && library.Holds(t.Name))
                    .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(t => new GrownTree
                    {
                        Name = t.Name!,
                        Species = name,
                        Radius = t.Radius,
                        Triangles = t.Triangles,
                        Far = string.Equals(t.Detail, "far", StringComparison.OrdinalIgnoreCase),
                    }),
            ];

            if (variants.Count == 0)
            {
                continue;
            }

            library._species[name] = new TreeSpecies
            {
                Name = name,
                Canopy = described.Canopy,
                Sprites = described.Sprites ?? [],
                Card = described.Card ?? string.Empty,
                Variants = variants,
            };

            foreach (string sprite in described.Sprites ?? [])
            {
                library._bySprite[Path.GetFileNameWithoutExtension(sprite)] = name;
            }
        }

        return library;
    }

    /// <summary>Whether the geometry for a grown tree is anywhere this can reach.</summary>
    private bool Holds(string name) =>
        (Directory.Length > 0 && File.Exists(Path.Combine(Directory, name + ".glb"))) ||
        _packs?.Has(RebarnKind.Model, name) == true;

    /// <summary>Which species, if any, stands in for a sprite.</summary>
    /// <param name="texture">A texture name, with or without an extension.</param>
    /// <returns>The species, or null when this sprite keeps its card.</returns>
    public TreeSpecies? For(string texture)
    {
        ArgumentNullException.ThrowIfNull(texture);

        return _bySprite.TryGetValue(Path.GetFileNameWithoutExtension(texture), out string? name)
            ? _species[name]
            : null;
    }

    /// <summary>Picks one variant of a species, the same way every time.</summary>
    /// <param name="species">The species.</param>
    /// <param name="seed">Something stable about the place the tree stands.</param>
    /// <param name="far">Whether to take the cheap one grown for a far hillside.</param>
    /// <returns>The variant.</returns>
    public static GrownTree Variant(TreeSpecies species, int seed, bool far = false)
    {
        ArgumentNullException.ThrowIfNull(species);

        // Whichever detail was asked for, and the other one when nobody grew it. A set
        // with no far variants should draw full trees rather than no trees, and a set with
        // only far ones is a perfectly good set to be going on with.
        IReadOnlyList<GrownTree> wanted = far ? species.Distant : species.Near;

        if (wanted.Count == 0)
        {
            wanted = far ? species.Near : species.Distant;
        }

        if (wanted.Count == 0)
        {
            wanted = species.Variants;
        }

        return wanted[(int)((uint)seed % (uint)wanted.Count)];
    }

    /// <summary>Reads a grown tree's geometry, once per run.</summary>
    /// <param name="tree">Which tree.</param>
    /// <param name="diagnostics">Receives a warning when it will not parse.</param>
    /// <returns>The model, or null when it will not read.</returns>
    public ModFile? Read(GrownTree tree, DiagnosticBag? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(tree);

        if (_grown.TryGetValue(tree.Name, out ModFile? already))
        {
            return already;
        }

        string file = Path.Combine(Directory, tree.Name + ".glb");
        ModFile? parsed = null;

        try
        {
            byte[]? bytes = Directory.Length > 0 && File.Exists(file)
                ? File.ReadAllBytes(file)
                : _packs?.Read(RebarnKind.Model, tree.Name);

            parsed = bytes is null
                ? null
                : GlbReader.TryParse(bytes, tree.Name, diagnostics);
        }
        catch (IOException ex)
        {
            diagnostics?.Add(new Diagnostic(
                "GK3R1123",
                DiagnosticSeverity.Warning,
                $"The grown tree {tree.Name} will not open, so its card stays: {ex.Message}",
                file));
        }

        // Cached either way, failure included: a tree that will not read will not read on
        // the ninetieth pine of a scene either, and the warning belongs in the log once.
        _grown[tree.Name] = parsed;
        return parsed;
    }

    private sealed record TreeManifest
    {
        [JsonPropertyName("species")]
        public Dictionary<string, TreeManifestSpecies>? Species { get; init; }

        [JsonPropertyName("trees")]
        public List<TreeManifestTree>? Trees { get; init; }
    }

    private sealed record TreeManifestSpecies
    {
        [JsonPropertyName("canopy")]
        public bool Canopy { get; init; }

        [JsonPropertyName("sprites")]
        public List<string>? Sprites { get; init; }

        [JsonPropertyName("card")]
        public string? Card { get; init; }
    }

    private sealed record TreeManifestTree
    {
        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("species")]
        public string? Species { get; init; }

        [JsonPropertyName("detail")]
        public string? Detail { get; init; }

        [JsonPropertyName("radius")]
        public float Radius { get; init; }

        [JsonPropertyName("triangles")]
        public int Triangles { get; init; }
    }
}
