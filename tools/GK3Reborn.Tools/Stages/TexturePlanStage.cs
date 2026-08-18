using System.Globalization;
using System.Text;
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
/// Works out which textures matter, so enhancement effort goes where it shows.
/// </summary>
/// <remarks>
/// <para>
/// Texture resolution, not geometry, is what dates this game. All 6,658 textures
/// together hold 213 megapixels — about what twenty-six single 4K textures hold — and
/// 3,116 of them are 128 pixels or smaller. Gabriel's face is 256x256. No amount of
/// subdivision addresses that.
/// </para>
/// <para>
/// But 6,658 textures cannot all be treated alike, so this stage assigns each one a
/// tier from evidence rather than by hand: what references it, whether those things are
/// characters or set dressing, how many places use it, and how small it currently is.
/// The tiers are the ones in <c>Plan/02-content-pipeline.md</c> section 5.
/// </para>
/// </remarks>
public sealed class TexturePlanStage
{
    private readonly Action<string> _log;

    /// <summary>Creates the stage.</summary>
    /// <param name="log">Progress sink.</param>
    public TexturePlanStage(Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
    }

    /// <summary>Builds the texture plan.</summary>
    /// <param name="sourceDirectory">The game's <c>Data</c> directory.</param>
    /// <param name="workspaceDirectory">Content workspace root.</param>
    /// <param name="diagnostics">Receives stage-level diagnostics.</param>
    /// <returns>The plan.</returns>
    public TexturePlanManifest Run(string sourceDirectory, string workspaceDirectory, DiagnosticBag diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);

        Dictionary<string, ModelRole> roles = LoadRoles(workspaceDirectory);
        Dictionary<string, TextureUsage> usage = new(StringComparer.OrdinalIgnoreCase);

        string? Use(string texture, string by, string kind)
        {
            if (string.IsNullOrWhiteSpace(texture))
            {
                return null;
            }

            string key = Key(texture);
            if (!usage.TryGetValue(key, out TextureUsage? entry))
            {
                entry = new TextureUsage();
                usage[key] = entry;
            }

            entry.Add(by, kind);
            return key;
        }

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
                string stem = Path.GetFileNameWithoutExtension(entry.Name);

                bool isModel = extension.Equals("MOD", StringComparison.OrdinalIgnoreCase);
                bool isScene = extension.Equals("BSP", StringComparison.OrdinalIgnoreCase);
                bool isBitmap = extension.Equals("BMP", StringComparison.OrdinalIgnoreCase);

                if (!isModel && !isScene && !isBitmap)
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

                try
                {
                    if (isModel)
                    {
                        ModFile model = ModFile.Parse(data, entry.Name);
                        string kind = roles.TryGetValue(stem, out ModelRole? role)
                            ? role.Disposition.ToString()
                            : "Unknown";

                        foreach (ModSubmesh submesh in model.Meshes.SelectMany(m => m.Submeshes))
                        {
                            Use(submesh.TextureName, stem, kind);
                        }
                    }
                    else if (isScene)
                    {
                        BspFile scene = BspFile.Parse(data, entry.Name);
                        foreach (BspSurface surface in scene.Surfaces)
                        {
                            Use(surface.TextureName, stem, "Room");
                        }
                    }
                    else if (BitmapDecoder.CanDecode(data))
                    {
                        DecodedImage image = BitmapDecoder.Decode(data, entry.Name);
                        if (Use(stem, string.Empty, string.Empty) is { } key)
                        {
                            usage[key].SetSize(image.Width, image.Height, image.HasAlpha);
                            usage[key].SetFlatColor(BitmapDecoder.FlatColorOf(image));
                        }
                    }
                }
                catch (FormatParseException ex)
                {
                    diagnostics.Add(ex.Diagnostic);
                }
            }
        }

        List<TexturePlanEntry> entries = [];
        foreach ((string name, TextureUsage u) in usage.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (u.Width == 0)
            {
                // Referenced by geometry but absent from the corpus - already recorded as
                // a dangling reference by C2, and nothing to upscale.
                continue;
            }

            // A flat texture is never worth enlarging, whatever references it.
            int tier = u.FlatColor is null ? Tier(u) : 3;
            entries.Add(new TexturePlanEntry
            {
                Name = name,
                Width = u.Width,
                Height = u.Height,
                HasAlpha = u.HasAlpha,
                Tier = tier,
                IsFlatColor = u.FlatColor is not null,
                FlatColor = u.FlatColor is { } c
                    ? string.Create(CultureInfo.InvariantCulture, $"#{c.R:X2}{c.G:X2}{c.B:X2}")
                    : null,
                TargetSize = u.FlatColor is null ? TargetFor(tier, u) : 0,
                UsedByCharacters = u.Characters,
                UsedByProps = u.Props,
                UsedByRooms = u.Rooms,
                Referrers = [.. u.Names.OrderBy(n => n, StringComparer.Ordinal).Take(12)],
            });
        }

        var manifest = new TexturePlanManifest
        {
            SchemaVersion = 1,
            Stage = "C4.texture-plan",
            SourceRoot = sourceDirectory.Replace('\\', '/'),
            TierCounts = entries.GroupBy(e => e.Tier)
                .OrderBy(g => g.Key)
                .ToDictionary(g => $"tier{g.Key}", g => g.Count(), StringComparer.Ordinal),
            TotalMegapixels = Math.Round(entries.Sum(e => (double)e.Width * e.Height) / 1e6, 1),
            Textures = [.. entries.OrderBy(e => e.Tier).ThenByDescending(e => e.UsedByCharacters + e.UsedByRooms)],
        };

        string path = Path.Combine(workspaceDirectory, "manifests", "texture-plan.json");
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(manifest, ManifestJson.Options) + "\n");
        _log($"manifest: {path}");

        return manifest;
    }

    /// <summary>
    /// Normalizes a texture name into a lookup key.
    /// </summary>
    /// <remarks>
    /// Names are not reliably free of dots: <c>PREP.HEDGE.BMP</c> is one texture, not a
    /// file called PREP with an odd extension. Only a known image extension is stripped,
    /// so both the geometry that references a texture and the texture itself land on the
    /// same key.
    /// </remarks>
    private static string Key(string texture)
    {
        string name = Path.GetFileName(texture.Replace('\\', '/')).Trim();

        foreach (string extension in (string[])[".BMP", ".PNG", ".TGA"])
        {
            if (name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                name = name[..^extension.Length];
                break;
            }
        }

        return name.ToUpperInvariant();
    }

    /// <summary>
    /// Assigns a tier from what uses the texture and how small it currently is.
    /// </summary>
    /// <remarks>
    /// Tier 0 is reserved for what the player looks at closely and often: anything a
    /// character wears or is. Tier 1 covers room surfaces and widely reused props, where
    /// low resolution is spread across a lot of screen. Tier 2 is everything else that is
    /// used, and Tier 3 is unreferenced. Small textures are promoted a tier, since a
    /// 32-pixel texture on screen is the most visible kind of dated.
    /// </remarks>
    public static int Tier(TextureUsage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);

        int tier = usage switch
        {
            { Characters: > 0 } => 0,
            { Rooms: > 0 } => 1,
            { Props: >= 3 } => 1,
            { Props: > 0 } => 2,
            _ => 3,
        };

        // A texture already at 256 or above is less urgent than a 32-pixel one used the
        // same way, so only promote the genuinely tiny.
        if (tier > 0 && usage.Width > 0 && Math.Max(usage.Width, usage.Height) <= 64)
        {
            tier--;
        }

        return tier;
    }

    private static int TargetFor(int tier, TextureUsage usage)
    {
        // Upscaling beyond 16x invents more than it restores, so the target is capped
        // relative to the source as well as by tier.
        int ceiling = tier switch
        {
            0 => 4096,
            1 => 2048,
            2 => 1024,
            _ => 0,
        };

        if (ceiling == 0)
        {
            return 0;
        }

        int largest = Math.Max(usage.Width, usage.Height);
        return Math.Min(ceiling, Math.Max(256, largest * 16));
    }

    private static Dictionary<string, ModelRole> LoadRoles(string workspaceDirectory)
    {
        string path = Path.Combine(workspaceDirectory, "manifests", "model-roles.json");
        if (!File.Exists(path))
        {
            return new Dictionary<string, ModelRole>(StringComparer.OrdinalIgnoreCase);
        }

        ModelRoleManifest? manifest = JsonSerializer.Deserialize<ModelRoleManifest>(
            File.ReadAllText(path), ManifestJson.Options);

        return manifest?.Models.ToDictionary(m => m.Name, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, ModelRole>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Accumulates what references a texture.</summary>
    public sealed class TextureUsage
    {
        /// <summary>Names of the models and rooms that use it.</summary>
        public HashSet<string> Names { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>How many character models use it.</summary>
        public int Characters { get; private set; }

        /// <summary>How many props use it.</summary>
        public int Props { get; private set; }

        /// <summary>How many rooms use it.</summary>
        public int Rooms { get; private set; }

        /// <summary>Pixel width, once known.</summary>
        public int Width { get; private set; }

        /// <summary>Pixel height, once known.</summary>
        public int Height { get; private set; }

        /// <summary>Whether the texture carries transparency.</summary>
        public bool HasAlpha { get; private set; }

        /// <summary>The texture's single colour, when it has only one or two.</summary>
        public (byte R, byte G, byte B)? FlatColor { get; private set; }

        /// <summary>Records that the texture is a flat colour.</summary>
        /// <param name="color">The colour, or null when the texture has detail.</param>
        public void SetFlatColor((byte R, byte G, byte B)? color) => FlatColor = color;

        /// <summary>Records a reference.</summary>
        /// <param name="by">Name of the referring asset.</param>
        /// <param name="kind">Its disposition.</param>
        public void Add(string by, string kind)
        {
            if (!string.IsNullOrEmpty(by) && !Names.Add(by))
            {
                return;
            }

            switch (kind)
            {
                case "Character":
                    Characters++;
                    break;
                case "Room":
                    Rooms++;
                    break;
                case "Prop" or "SceneGeometry" or "Review":
                    Props++;
                    break;
                default:
                    break;
            }
        }

        /// <summary>Records the texture's actual size.</summary>
        /// <param name="width">Pixel width.</param>
        /// <param name="height">Pixel height.</param>
        /// <param name="hasAlpha">Whether it carries transparency.</param>
        public void SetSize(int width, int height, bool hasAlpha)
        {
            Width = width;
            Height = height;
            HasAlpha = hasAlpha;
        }
    }
}
