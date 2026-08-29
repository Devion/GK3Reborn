using GK3Reborn.Rendering.Geometry;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GK3Reborn.Content.Manifests;
using GK3Reborn.Formats;
using GK3Reborn.Formats.Barn;
using GK3Reborn.Formats.Models;
using GK3Reborn.Foundation;
using GK3Reborn.Foundation.Diagnostics;

namespace GK3Reborn.Tools.Stages;

/// <summary>
/// Works out what each model is for, so the enhancement pipeline knows what to touch.
/// </summary>
/// <remarks>
/// <para>
/// Names cannot be trusted for this and neither can any single signal. <c>CS2_CAMBNDS</c>
/// is declared as a scene's camera bounds yet carries 92 meshes and seven textured
/// materials of actual furniture, while <c>LBYCAMERABOUNDS</c> is 31 meshes with no
/// texture at all — a genuine invisible volume. A model can hold several roles at once.
/// </para>
/// <para>
/// So roles come from the scene initialisation files, which declare them explicitly:
/// <c>cameraBounds=</c>, <c>boundary=</c>, <c>floor=</c>, and <c>model=…, type=</c> with
/// values <c>scene</c>, <c>prop</c>, <c>gasprop</c>, <c>hittest</c> and <c>noclick</c>,
/// plus models named in an <c>[ACTORS]</c> section. Those declarations are then weighed
/// against what the geometry actually contains.
/// </para>
/// <para>
/// The output is a recommendation, not a verdict. Anything ambiguous is marked for
/// review rather than silently included or skipped.
/// </para>
/// </remarks>
public sealed partial class ModelRoleStage
{
    private readonly Action<string> _log;

    /// <summary>Creates the stage.</summary>
    /// <param name="log">Progress sink.</param>
    public ModelRoleStage(Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
    }

    /// <summary>Classifies every model.</summary>
    /// <param name="sourceDirectory">The game's <c>Data</c> directory.</param>
    /// <param name="workspaceDirectory">Content workspace root.</param>
    /// <param name="diagnostics">Receives stage-level diagnostics.</param>
    /// <returns>The role manifest.</returns>
    public ModelRoleManifest Run(string sourceDirectory, string workspaceDirectory, DiagnosticBag diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);

        Dictionary<string, ModFile> models = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, HashSet<string>> roles = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> animationPrefixes = new(StringComparer.OrdinalIgnoreCase);

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

                if (extension.Equals("ACT", StringComparison.OrdinalIgnoreCase))
                {
                    // Animation names are CHARACTER_ACTION; the prefix says which model
                    // is animated, and an animated model is a character rather than set
                    // dressing.
                    int underscore = stem.IndexOf('_', StringComparison.Ordinal);
                    if (underscore > 0)
                    {
                        animationPrefixes.Add(stem[..underscore]);
                    }

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

                if (extension.Equals("MOD", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        models[stem] = ModFile.Parse(data, entry.Name);
                    }
                    catch (FormatParseException ex)
                    {
                        diagnostics.Add(ex.Diagnostic);
                    }
                }
                else if (extension.Equals("SIF", StringComparison.OrdinalIgnoreCase))
                {
                    CollectRoles(Encoding.Latin1.GetString(data), roles);
                }
            }
        }

        _log($"{models.Count} models, {roles.Count} referenced by scene files");

        List<ModelRole> results = [];
        foreach ((string name, ModFile model) in models.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            HashSet<string> declared = roles.GetValueOrDefault(name, []);
            bool animated = animationPrefixes.Contains(name);
            bool textured = model.Meshes
                .SelectMany(m => m.Submeshes)
                .Any(s => s.TextureName.Length > 0);

            results.Add(new ModelRole
            {
                Name = name,
                Roles = [.. declared.OrderBy(r => r, StringComparer.Ordinal)],
                Animated = animated,
                Textured = textured,
                MeshCount = model.Meshes.Count,
                VertexCount = model.VertexCount,
                TriangleCount = model.TriangleCount,
                Disposition = Decide(declared, animated, textured),
            });
        }

        var manifest = new ModelRoleManifest
        {
            SchemaVersion = 1,
            Stage = "C5.model-roles",
            SourceRoot = sourceDirectory.Replace('\\', '/'),
            DispositionCounts = Count(results, r => r.Disposition),
            Models = results,
        };

        string manifestPath = Path.Combine(workspaceDirectory, "manifests", "model-roles.json");
        AtomicFile.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, ManifestJson.Options) + "\n");
        _log($"manifest: {manifestPath}");

        int review = manifest.DispositionCounts.GetValueOrDefault(ModelDisposition.Review.ToString());
        if (review > 0)
        {
            diagnostics.Add(new Diagnostic(
                "GK3R2400", DiagnosticSeverity.Warning,
                $"{review} models need a human decision before enhancement.",
                null, null, "an unambiguous role", $"{review} ambiguous",
                "These are declared only as collision or bounds geometry yet carry textured "
                + "art, so they may be doing both jobs. See model-roles.json."));
        }

        return manifest;
    }

    /// <summary>
    /// Decides what the enhancement pipeline should do with a model.
    /// </summary>
    /// <remarks>
    /// A visible declaration wins outright. Collision-only geometry is left alone: it is
    /// never drawn, and the plan requires the original navigation and collision to survive
    /// even where visible geometry is replaced. The awkward case is a model declared only
    /// as collision that nonetheless carries textures, which is exactly what happens when
    /// one asset does both jobs — that goes to review rather than being guessed at.
    /// </remarks>
    public static ModelDisposition Decide(IReadOnlySet<string> roles, bool animated, bool textured)
    {
        ArgumentNullException.ThrowIfNull(roles);
        _ = animated;

        // Being animated does not make something a character: 425 models animate without
        // being one - doors, phones, curtains, an alarm clock. Only the 41 models a scene
        // file names in an [ACTORS] section are the cast. Animation is still recorded, as
        // the enhancement pipeline needs it to choose skinned or static handling.
        if (roles.Contains("actor"))
        {
            return ModelDisposition.Character;
        }

        if (roles.Contains("prop") || roles.Contains("gasprop"))
        {
            return ModelDisposition.Prop;
        }

        if (roles.Contains("scene"))
        {
            return ModelDisposition.SceneGeometry;
        }

        bool collisionOnly = roles.Count > 0 && roles.All(r =>
            r is "camerabounds" or "boundary" or "floor" or "hittest" or "noclick");

        if (collisionOnly)
        {
            return textured ? ModelDisposition.Review : ModelDisposition.Collision;
        }

        // Nothing declared it. Untextured geometry nobody references is almost certainly
        // a helper volume; textured geometry is more likely unused or dynamically named
        // art, which is worth a look.
        return textured ? ModelDisposition.Review : ModelDisposition.Collision;
    }

    private static void CollectRoles(string sif, Dictionary<string, HashSet<string>> roles)
    {
        void Add(string name, string role)
        {
            string key = name.Trim();
            if (key.Length == 0)
            {
                return;
            }

            if (!roles.TryGetValue(key, out HashSet<string>? set))
            {
                set = new HashSet<string>(StringComparer.Ordinal);
                roles[key] = set;
            }

            set.Add(role);
        }

        foreach (Match m in SceneLevelDeclaration().Matches(sif))
        {
            Add(m.Groups["name"].Value, m.Groups["kind"].Value.ToLowerInvariant());
        }

        // Section headers can carry a condition, so match the name and ignore the rest.
        string currentSection = string.Empty;
        foreach (string rawLine in sif.Split('\n'))
        {
            string line = rawLine.Trim();

            Match section = SectionHeader().Match(line);
            if (section.Success)
            {
                currentSection = section.Groups["name"].Value.ToUpperInvariant();
                continue;
            }

            Match model = ModelDeclaration().Match(line);
            if (!model.Success)
            {
                continue;
            }

            Match type = TypeAttribute().Match(line);
            Add(model.Groups["name"].Value,
                type.Success ? type.Groups["type"].Value.ToLowerInvariant()
                : currentSection == "ACTORS" ? "actor"
                : "scene");
        }
    }

    private static Dictionary<string, int> Count(IEnumerable<ModelRole> models, Func<ModelRole, ModelDisposition> key)
    {
        Dictionary<string, int> counts = new(StringComparer.Ordinal);
        foreach (ModelRole model in models)
        {
            string name = key(model).ToString();
            counts[name] = counts.GetValueOrDefault(name) + 1;
        }

        return counts.OrderByDescending(kv => kv.Value).ToDictionary(StringComparer.Ordinal);
    }

    [GeneratedRegex(@"^\s*(?<kind>cameraBounds|boundary|floor)\s*=\s*(?<name>[A-Za-z0-9_\-]+)",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex SceneLevelDeclaration();

    [GeneratedRegex(@"^\[(?<name>[A-Za-z]+)", RegexOptions.CultureInvariant)]
    private static partial Regex SectionHeader();

    [GeneratedRegex(@"^model\s*=\s*(?<name>[A-Za-z0-9_\-]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ModelDeclaration();

    [GeneratedRegex(@"type\s*=\s*(?<type>[A-Za-z]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TypeAttribute();
}
