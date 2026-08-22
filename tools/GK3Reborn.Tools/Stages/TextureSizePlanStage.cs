using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using GK3Reborn.Content;
using GK3Reborn.Content.Manifests;
using GK3Reborn.Formats;
using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Rendering;

namespace GK3Reborn.Tools.Stages;

/// <summary>What size one texture should be packed at, and why.</summary>
/// <param name="Name">The texture's name, without an extension.</param>
/// <param name="Width">Its width in the enhanced set.</param>
/// <param name="Height">Its height in the enhanced set.</param>
/// <param name="Size">Longest edge it should be packed at.</param>
/// <param name="Reason">Which rule decided that.</param>
/// <param name="Form">
/// <c>dds</c> to block-compress it, or <c>png</c> to store the source file verbatim. A
/// full-screen image drawn as it is has nothing to gain from a block format.
/// </param>
/// <param name="Materials">
/// Whether it is a surface at all. False takes it out of every PbrLab pass and out of the
/// normal, ORM and height sets.
/// </param>
/// <param name="Pack">
/// Whether it may go into the pack at all. False for a texture whose 1999 original uses a
/// colour key that its replacement did not resolve into alpha: block data cannot be keyed
/// at runtime, so the loader has to keep reading the original for those.
/// </param>
/// <param name="WorldArea">World units squared it covers, or null when nothing measured it.</param>
/// <param name="DensityTarget">Size at which it reaches the corpus median texel density.</param>
public sealed record TextureSize(
    string Name,
    int Width,
    int Height,
    int Size,
    string Reason,
    string Form,
    bool Materials,
    bool Pack,
    double? WorldArea,
    int? DensityTarget);

/// <summary>The whole plan, as <c>manifests/pack-sizes.json</c> holds it.</summary>
/// <param name="SchemaVersion">Manifest schema version.</param>
/// <param name="Stage">Which stage wrote it.</param>
/// <param name="Multiplier">How much denser than the corpus median the plan asks for.</param>
/// <param name="Floor">The smallest a texture is allowed to become.</param>
/// <param name="Counts">How many textures landed at each size.</param>
/// <param name="Reasons">How many textures each rule decided.</param>
/// <param name="Textures">Every texture, ordered by name.</param>
public sealed record TextureSizePlan(
    int SchemaVersion,
    string Stage,
    int Multiplier,
    int Floor,
    IReadOnlyDictionary<string, int> Counts,
    IReadOnlyDictionary<string, int> Reasons,
    IReadOnlyList<TextureSize> Textures);

/// <summary>
/// Decides what size each enhanced texture is worth packing at.
/// </summary>
/// <remarks>
/// <para>
/// Nearly every enhanced texture is 2048 on its longest edge, whatever it depicts. A wall
/// that fills a room and a lipstick cap two centimetres across were upscaled by the same
/// rule, and the second one is 5.6 MB of block data for something that is never more than a
/// few dozen pixels on screen. This works out which is which.
/// </para>
/// <para>
/// The signal is <c>worldArea</c> and <c>densityTarget</c> from <c>surface-analysis.json</c>,
/// which is what the corpus measured for exactly this question: how many texels of a texture
/// fall across one world unit. <c>densityTarget</c> is the size at which a texture reaches
/// the corpus <em>median</em> density — a 1999 yardstick — so the plan multiplies it to
/// choose how much better than the original the remake wants to be, and rounds up to a power
/// of two. Reference counts are deliberately not used: they favour door latches over the
/// wallpaper that fills a frame.
/// </para>
/// <para>
/// <strong>Nothing is demoted without positive evidence.</strong> A texture the surface
/// analysis never saw keeps the size it has, because "not measured" is not "not important".
/// Three classes are protected outright on top of that, each of which is drawn far larger
/// than its world area suggests:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <strong>Face patches.</strong> Eyelids, blinks, winks and mouths are blitted into a
/// character's face bitmap at offsets in that bitmap's own coordinates — see
/// <c>docs/formats/faces.md</c>. They have to stay in scale with the face.
/// </description></item>
/// <item><description>
/// <strong>Inventory sprites.</strong> Named by the game's own <c>INVENTORYSPRITES.TXT</c>.
/// These are drawn as 2D art filling much of the screen in a close-up, and their world area
/// on room geometry says nothing about that. The 3D model textures for the same objects —
/// <c>LIPSTKCAP</c>, <c>RAZORFRNT</c> — are a different set and are sized normally.
/// </description></item>
/// <item><description>
/// <strong>Anything worn by a character.</strong> Faces and clothing are looked at in
/// conversation close-ups.
/// </description></item>
/// </list>
/// </remarks>
public sealed class TextureSizePlanStage
{
    private readonly Action<string> _log;

    /// <summary>Creates the stage.</summary>
    /// <param name="log">Where the report is written.</param>
    public TextureSizePlanStage(Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
    }

    /// <summary>Where the plan is written, under the workspace.</summary>
    public const string ManifestPath = "manifests/pack-sizes.json";

    /// <summary>Hand corrections, applied last and never overwritten.</summary>
    /// <remarks>
    /// A plain map of name to longest edge. It exists because no measurement sees everything:
    /// a thing the player walks up to and reads has a small world area and needs its pixels
    /// anyway, and there is no signal in the corpus for an in-world close-up camera. Same
    /// convention as <c>material-library.materials.edits.json</c> — the generated file is
    /// regenerated, the edits beside it survive.
    /// </remarks>
    public const string OverridesPath = "manifests/pack-rules.json";

    /// <summary>Works out the plan and writes it.</summary>
    /// <param name="workspace">The content workspace root.</param>
    /// <param name="source">The game's Data directory, for <c>INVENTORYSPRITES.TXT</c>.</param>
    /// <param name="multiplier">How much denser than the corpus median to aim for.</param>
    /// <param name="floor">The smallest a texture may become.</param>
    /// <returns>The plan.</returns>
    public TextureSizePlan Run(string workspace, string? source, int multiplier = 4, int floor = 512)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspace);
        ArgumentOutOfRangeException.ThrowIfLessThan(multiplier, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(floor, 4);

        Dictionary<string, (double Area, int Target)> density = ReadDensity(workspace);
        HashSet<string> worn = ReadWorn(workspace);
        HashSet<string> sprites = ReadInventorySprites(source);
        Dictionary<string, TextureRule> overrides = ReadOverrides(workspace);
        HashSet<string> unkeyed = ReadUnresolvedKeys(workspace, source);

        string textures = Path.Combine(workspace, "enhanced", "textures");
        var plan = new List<TextureSize>();

        foreach (string file in Directory.EnumerateFiles(textures, "*.PNG"))
        {
            if (ContentPackStage.PngSize(file) is not { } size)
            {
                continue;
            }

            string name = Path.GetFileNameWithoutExtension(file).ToUpperInvariant();
            int longest = Math.Max(size.Width, size.Height);

            (int chosen, string reason) = Decide(
                name, longest, density, worn, sprites, multiplier, floor);

            string form = "dds";
            bool materials = true;

            if (overrides.TryGetValue(name, out TextureRule? rule))
            {
                if (rule.Size is > 0)
                {
                    chosen = Math.Min(longest, rule.Size.Value);
                }
                else if (rule.Form == "png" || !rule.Materials)
                {
                    // Not sized by density. Whatever it is, it is not a surface being
                    // measured against the wallpaper, so the density argument does not
                    // apply to it and the size it was authored at stands.
                    chosen = longest;
                }

                form = rule.Form;
                materials = rule.Materials;
                reason = "by hand: "
                    + (rule.Note is { Length: > 0 } ? rule.Note : "see pack-rules.json");
            }

            bool packable = !unkeyed.Contains(name);

            if (!packable)
            {
                reason = "keyed original, replacement has no alpha";
            }

            plan.Add(new TextureSize(
                name,
                size.Width,
                size.Height,
                chosen,
                reason,
                form,
                materials,
                packable,
                density.TryGetValue(name, out (double Area, int Target) d) ? d.Area : null,
                density.TryGetValue(name, out d) ? d.Target : null));
        }

        plan.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));

        var result = new TextureSizePlan(
            1,
            "C8.pack-sizes",
            multiplier,
            floor,
            plan.GroupBy(t => t.Size)
                .OrderBy(g => g.Key)
                .ToDictionary(g => g.Key.ToString(CultureInfo.InvariantCulture), g => g.Count()),
            plan.GroupBy(t => t.Reason)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count()),
            plan);

        string path = Path.Combine(workspace, ManifestPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(result, ManifestJson.Options) + "\n");

        Report(result, path);
        return result;
    }

    private static (int Size, string Reason) Decide(
        string name,
        int longest,
        Dictionary<string, (double Area, int Target)> density,
        HashSet<string> worn,
        HashSet<string> sprites,
        int multiplier,
        int floor)
    {
        if (IsFacePatch(name))
        {
            return (longest, "face patch");
        }

        if (sprites.Any(s => name.StartsWith(s, StringComparison.Ordinal)))
        {
            return (longest, "inventory sprite");
        }

        if (worn.Contains(name))
        {
            return (longest, "worn by a character");
        }

        if (!density.TryGetValue(name, out (double Area, int Target) measured) || measured.Target <= 0)
        {
            // Never measured on any room's geometry, so there is no evidence it is small.
            return (longest, "no measurement");
        }

        int wanted = NextPowerOfTwo(measured.Target * multiplier);
        int chosen = Math.Max(floor, Math.Min(longest, wanted));

        return (chosen, chosen == longest ? "density, unchanged" : "density");
    }

    /// <summary>
    /// Whether a name is a patch blitted into a face bitmap rather than a texture of its own.
    /// </summary>
    /// <remarks>
    /// By name, because nothing else records it: <c>FACES.TXT</c> names the offsets and sizes
    /// but not which bitmaps are patches. The prefixes are the character codes, so matching
    /// the suffix is what generalises across all forty-one of them.
    /// </remarks>
    private static bool IsFacePatch(string name) =>
        name.Contains("EYELID", StringComparison.Ordinal)
        || name.Contains("_BLINK", StringComparison.Ordinal)
        || name.Contains("_WINK", StringComparison.Ordinal)
        || name.Contains("MOUTH", StringComparison.Ordinal)
        || name.Contains("_FACE", StringComparison.Ordinal);

    private static int NextPowerOfTwo(int value)
    {
        int result = 1;

        while (result < value)
        {
            result <<= 1;
        }

        return result;
    }

    /// <summary>
    /// Textures the pack must not hold, because their key was never resolved into alpha.
    /// </summary>
    /// <param name="workspace">The content workspace root.</param>
    /// <param name="source">The game's Data directory.</param>
    /// <returns>Their names.</returns>
    /// <remarks>
    /// <para>
    /// GK3 marks transparency with magenta, and a block-compressed texture cannot be keyed
    /// at runtime — <see cref="TextureKeying"/> works on texels and these are blocks. The
    /// enhanced set resolves the magenta into a real alpha channel, so nearly every keyed
    /// texture is safe to pack; the handful whose replacement came back opaque are not, and
    /// the loader has to go on reading the original for those.
    /// </para>
    /// <para>
    /// Decided with the engine's own decoder and the engine's own
    /// <see cref="TextureKeying.NeedsKey"/>, so the answer here is the same answer the
    /// loader will reach. Working it out any other way invites the two to disagree, and a
    /// disagreement shows up as one texture in a room quietly being the 1999 one.
    /// </para>
    /// </remarks>
    private HashSet<string> ReadUnresolvedKeys(string workspace, string? source)
    {
        var unresolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (source is not { Length: > 0 } || !Directory.Exists(source))
        {
            _log("No --source given, so keyed originals cannot be checked; none are excluded.");
            return unresolved;
        }

        using var archives = GameArchives.Open(source);
        string textures = Path.Combine(workspace, "enhanced", "textures");
        int keyed = 0;

        foreach (string file in Directory.EnumerateFiles(textures, "*.PNG"))
        {
            string name = Path.GetFileNameWithoutExtension(file).ToUpperInvariant();

            byte[]? bytes = archives.Read(name) ?? archives.Read(name + ".BMP");

            if (bytes is null || !BitmapDecoder.CanDecode(bytes))
            {
                continue;
            }

            DecodedImage original;

            try
            {
                original = BitmapDecoder.Decode(bytes, name);
            }
            catch (FormatParseException)
            {
                continue;
            }

            if (!TextureKeying.NeedsKey(original))
            {
                continue;
            }

            keyed++;

            // The replacement's own alpha is what says the key was carried across.
            if (ContentPackStage.PngSize(file) is { Alpha: false })
            {
                unresolved.Add(name);
            }
        }

        _log($"Colour keys: {keyed} original(s) use one, "
            + $"{keyed - unresolved.Count} resolved into alpha by the enhanced set, "
            + $"{unresolved.Count} left out of the pack");

        foreach (string name in unresolved.OrderBy(n => n, StringComparer.Ordinal))
        {
            _log($"    {name}");
        }

        return unresolved;
    }

    private Dictionary<string, TextureRule> ReadOverrides(string workspace)
    {
        var rules = new Dictionary<string, TextureRule>(StringComparer.OrdinalIgnoreCase);
        string path = Path.Combine(
            workspace, OverridesPath.Replace('/', Path.DirectorySeparatorChar));

        if (!File.Exists(path))
        {
            return rules;
        }

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));

        foreach (JsonProperty entry in document.RootElement.EnumerateObject())
        {
            // A leading underscore is a comment. The file is hand-written, and a format
            // with nowhere to say why a rule exists collects rules nobody dares delete.
            if (entry.Name.StartsWith('_'))
            {
                continue;
            }

            if (entry.Value.ValueKind == JsonValueKind.Number)
            {
                rules[entry.Name] = new TextureRule { Size = entry.Value.GetInt32() };
                continue;
            }

            if (entry.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            rules[entry.Name] = new TextureRule
            {
                Size = entry.Value.TryGetProperty("size", out JsonElement size) &&
                       size.ValueKind == JsonValueKind.Number
                    ? size.GetInt32()
                    : null,
                Form = entry.Value.TryGetProperty("form", out JsonElement form) &&
                       string.Equals(form.GetString(), "png", StringComparison.OrdinalIgnoreCase)
                    ? "png"
                    : "dds",
                Materials = !entry.Value.TryGetProperty("materials", out JsonElement mats) ||
                            mats.ValueKind != JsonValueKind.False,
                Note = entry.Value.TryGetProperty("note", out JsonElement note)
                    ? note.GetString()
                    : null,
            };
        }

        if (rules.Count > 0)
        {
            _log($"Rules: {rules.Count} texture(s) decided by hand ({OverridesPath})");

            foreach ((string name, TextureRule rule) in
                     rules.OrderBy(r => r.Key, StringComparer.Ordinal))
            {
                _log($"    {name,-20} form {rule.Form}, materials {rule.Materials}"
                    + (rule.Size is > 0 ? $", size {rule.Size}" : string.Empty));
            }
        }

        return rules;
    }

    /// <summary>One hand-written rule from <c>pack-rules.json</c>.</summary>
    private sealed record TextureRule
    {
        /// <summary>Longest edge to pack at, or null to leave the sizing alone.</summary>
        public int? Size { get; init; }

        /// <summary>Either <c>dds</c> or <c>png</c>.</summary>
        public string Form { get; init; } = "dds";

        /// <summary>Whether the texture is a surface with material channels.</summary>
        public bool Materials { get; init; } = true;

        /// <summary>Why the rule exists.</summary>
        public string? Note { get; init; }
    }

    private static Dictionary<string, (double Area, int Target)> ReadDensity(string workspace)
    {
        var found = new Dictionary<string, (double, int)>(StringComparer.OrdinalIgnoreCase);
        string path = Path.Combine(workspace, "manifests", "surface-analysis.json");

        if (!File.Exists(path))
        {
            return found;
        }

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));

        if (!document.RootElement.TryGetProperty("textures", out JsonElement list))
        {
            return found;
        }

        foreach (JsonElement entry in list.EnumerateArray())
        {
            if (!entry.TryGetProperty("name", out JsonElement name) ||
                !entry.TryGetProperty("densityTarget", out JsonElement target) ||
                target.ValueKind != JsonValueKind.Number)
            {
                continue;
            }

            double area = entry.TryGetProperty("worldArea", out JsonElement a) &&
                          a.ValueKind == JsonValueKind.Number
                ? a.GetDouble()
                : 0;

            found[name.GetString() ?? string.Empty] = (area, target.GetInt32());
        }

        return found;
    }

    private static HashSet<string> ReadWorn(string workspace)
    {
        var worn = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string path = Path.Combine(workspace, "manifests", "texture-plan.json");

        if (!File.Exists(path))
        {
            return worn;
        }

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));

        if (!document.RootElement.TryGetProperty("textures", out JsonElement list))
        {
            return worn;
        }

        foreach (JsonElement entry in list.EnumerateArray())
        {
            if (entry.TryGetProperty("usedByCharacters", out JsonElement used) &&
                used.ValueKind == JsonValueKind.Number &&
                used.GetInt32() > 0 &&
                entry.TryGetProperty("name", out JsonElement name))
            {
                worn.Add(name.GetString() ?? string.Empty);
            }
        }

        return worn;
    }

    /// <summary>The base names the inventory screen draws, from the game's own list.</summary>
    /// <remarks>
    /// The values in <c>INVENTORYSPRITES.TXT</c> are base names — <c>binocs_</c>,
    /// <c>Manu</c> — which the sprites suffix with a variant. Matching by prefix is therefore
    /// the right test, and the trailing underscore has to come off first or nothing matches.
    /// Names shorter than four characters are dropped: they would match half the corpus.
    /// </remarks>
    private HashSet<string> ReadInventorySprites(string? source)
    {
        var sprites = new HashSet<string>(StringComparer.Ordinal);

        if (source is not { Length: > 0 } || !Directory.Exists(source))
        {
            _log("No --source given, so inventory close-up sprites cannot be protected by name.");
            return sprites;
        }

        using var archives = GameArchives.Open(source);

        if (archives.Read("INVENTORYSPRITES.TXT") is not { } bytes)
        {
            _log("INVENTORYSPRITES.TXT is not in the archives; inventory sprites are unprotected.");
            return sprites;
        }

        foreach (string raw in System.Text.Encoding.Latin1.GetString(bytes)
                     .Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string line = raw.Split(';')[0].Trim();
            int equals = line.IndexOf('=', StringComparison.Ordinal);

            if (equals < 0)
            {
                continue;
            }

            string value = line[(equals + 1)..].Trim().TrimEnd('_').ToUpperInvariant();

            if (value.Length > 3 && !value.Equals("UNDEFINED", StringComparison.Ordinal))
            {
                sprites.Add(value);
            }
        }

        return sprites;
    }

    private void Report(TextureSizePlan plan, string path)
    {
        long before = plan.Textures.Sum(t => Blocks(t.Width, t.Height));
        long after = plan.Textures.Sum(t =>
        {
            double scale = (double)t.Size / Math.Max(t.Width, t.Height);
            return Blocks((int)(t.Width * scale), (int)(t.Height * scale));
        });

        _log($"Texture sizes: {plan.Textures.Count} textures at x{plan.Multiplier} "
            + $"the corpus median density, floor {plan.Floor}");

        foreach ((string size, int count) in plan.Counts)
        {
            _log($"    {size,6}  {count,5}");
        }

        foreach ((string reason, int count) in plan.Reasons)
        {
            _log($"    {reason,-22} {count,5}");
        }

        _log(string.Create(
            CultureInfo.InvariantCulture,
            $"Colour set would be {after / (1024.0 * 1024 * 1024):F2} GB rather than "
            + $"{before / (1024.0 * 1024 * 1024):F2} GB, a saving of "
            + $"{100.0 * (before - after) / before:F0}%"));

        _log($"Written to {path}");
    }

    private static long Blocks(int width, int height) =>
        (long)(Math.Max(4, width) * Math.Max(4, height) * 4 / 3);
}

/// <summary>What the packer needs to know about one texture.</summary>
/// <param name="Size">Longest edge to pack it at.</param>
/// <param name="Form">Either <c>dds</c> or <c>png</c>.</param>
/// <param name="Materials">Whether it has normal, ORM and height channels at all.</param>
/// <param name="Pack">Whether it may go into the pack at all.</param>
public readonly record struct PackedTexture(int Size, string Form, bool Materials, bool Pack);

/// <summary>Reads a written plan back.</summary>
public static class TextureSizePlanFile
{
    /// <summary>Loads a plan, keyed by texture name.</summary>
    /// <param name="workspace">The content workspace root.</param>
    /// <returns>The map, empty when there is no plan.</returns>
    public static Dictionary<string, PackedTexture> Load(string workspace)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspace);

        var sizes = new Dictionary<string, PackedTexture>(StringComparer.OrdinalIgnoreCase);
        string path = Path.Combine(
            workspace, TextureSizePlanStage.ManifestPath.Replace('/', Path.DirectorySeparatorChar));

        if (!File.Exists(path))
        {
            return sizes;
        }

        TextureSizePlan? plan = JsonSerializer.Deserialize<TextureSizePlan>(
            File.ReadAllText(path), ManifestJson.Options);

        foreach (TextureSize texture in plan?.Textures ?? [])
        {
            sizes[texture.Name] = new PackedTexture(
                texture.Size, texture.Form ?? "dds", texture.Materials, texture.Pack);
        }

        return sizes;
    }
}
