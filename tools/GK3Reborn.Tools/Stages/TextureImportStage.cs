using System.Globalization;
using System.Text.Json;
using GK3Reborn.Content.Manifests;
using GK3Reborn.Formats;
using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Foundation.Diagnostics;

namespace GK3Reborn.Tools.Stages;

/// <summary>
/// Takes generated texture candidates into the enhanced set, or says why not.
/// </summary>
/// <remarks>
/// <para>
/// A generator produces a directory of pictures. What the game needs is a set of textures
/// under the names the geometry already uses, each one checked against the original it
/// replaces, with a record of where it came from — <c>Plan/02</c> section 1 requires the
/// provenance to be kept, and <c>docs/texture-enhancement.md</c> sets out what a
/// replacement has to preserve. This is the step between the two.
/// </para>
/// <para>
/// Three checks disqualify a candidate outright, because each produces a game that looks
/// wrong rather than merely different. <b>Aspect ratio</b>, because the UV layout is fixed
/// and the geometry will stretch whatever it is given. <b>Alpha</b>, in both directions:
/// an alpha-tested texture that comes back opaque draws a solid block where a chain or a
/// leaf should be, and an opaque one that comes back with holes in it punches them through
/// a shirt. And <b>flat colours</b>, which the brief says belong in a material as a
/// base-colour factor and not in an image pipeline at all.
/// </para>
/// <para>
/// Everything else is recorded and passed on. Nothing here approves anything: a candidate
/// that survives every check a machine can make is a draft, and the manifest says so.
/// </para>
/// </remarks>
public sealed class TextureImportStage
{
    /// <summary>How much larger than its source an upscale may be before it is inventing.</summary>
    /// <remarks>
    /// The cap <c>docs/texture-enhancement.md</c> puts on the plan's targets. Exceeding it
    /// is a warning rather than a refusal — a remade texture is inventing on purpose, and
    /// whether that is wanted is a decision for the person reviewing it, not for this.
    /// </remarks>
    private const int RestorationLimit = 16;

    private readonly Action<string> _log;

    /// <summary>Creates the stage.</summary>
    /// <param name="log">Progress sink.</param>
    public TextureImportStage(Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
    }

    /// <summary>Imports a directory of candidates.</summary>
    /// <param name="workspace">The content workspace root.</param>
    /// <param name="candidates">Where the candidates are, relative to the workspace.</param>
    /// <param name="variant">Suffix of the file to take, such as <c>_imagegen_2048w</c>.</param>
    /// <param name="tool">What produced them, for the provenance record.</param>
    /// <param name="diagnostics">Receives stage-level diagnostics.</param>
    /// <returns>True when at least one candidate was accepted.</returns>
    public bool Run(
        string workspace,
        string candidates,
        string variant,
        string tool,
        DiagnosticBag diagnostics)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(variant);
        ArgumentNullException.ThrowIfNull(diagnostics);

        string root = Path.Combine(workspace, candidates);

        if (!Directory.Exists(root))
        {
            diagnostics.Add(new Diagnostic(
                "TEX001", DiagnosticSeverity.Error, $"No candidate directory at {root}."));

            return false;
        }

        if (Plan(workspace, diagnostics) is not { } plan)
        {
            return false;
        }

        string output = Path.Combine(workspace, "enhanced", "textures");
        string sources = Path.Combine(workspace, "normalized", "textures");
        Directory.CreateDirectory(output);

        List<EnhancedTexture> results = [];
        Dictionary<string, int> rejected = new(StringComparer.Ordinal);
        int accepted = 0;

        foreach (string file in Directory.EnumerateFiles(root, "*" + variant + ".png")
                     .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            name = name[..^variant.Length];

            EnhancedTexture result = Examine(name, file, sources, plan, root, workspace);
            results.Add(result);

            foreach (string reason in result.Rejections)
            {
                rejected[Reason(reason)] = rejected.GetValueOrDefault(Reason(reason)) + 1;
            }

            if (result.Verdict != TextureVerdict.Rejected)
            {
                File.Copy(file, Path.Combine(output, name + ".PNG"), overwrite: true);
                accepted++;
            }
        }

        var manifest = new EnhancedTextureManifest
        {
            Tool = tool,
            CandidateRoot = candidates,
            Variant = variant,
            Considered = results.Count,
            Accepted = accepted,
            RejectedBy = rejected,
            Textures = results,
        };

        string path = Path.Combine(workspace, "manifests", "enhanced-textures.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(manifest, ManifestJson.Options));

        Report(results, accepted, rejected, output, path);

        return accepted > 0;
    }

    /// <summary>Checks one candidate against the original it would replace.</summary>
    private static EnhancedTexture Examine(
        string name,
        string file,
        string sources,
        Dictionary<string, TexturePlanEntry> plan,
        string root,
        string workspace)
    {
        List<string> rejections = [];
        List<string> warnings = [];

        plan.TryGetValue(name, out TexturePlanEntry? entry);

        if (entry is null)
        {
            rejections.Add("not in the texture plan");
        }
        else if (entry.IsFlatColor)
        {
            rejections.Add("a single colour, which belongs in a material rather than an image");
        }

        DecodedImage candidate = PngReader.Decode(File.ReadAllBytes(file), file);
        bool candidateAlpha = HasTransparency(candidate);

        int sourceWidth = entry?.Width ?? 0;
        int sourceHeight = entry?.Height ?? 0;
        bool sourceAlpha = entry?.HasAlpha ?? false;

        string original = Path.Combine(sources, name + ".PNG");

        if (File.Exists(original))
        {
            DecodedImage source = PngReader.Decode(File.ReadAllBytes(original), original);
            sourceWidth = source.Width;
            sourceHeight = source.Height;
            sourceAlpha = HasTransparency(source);
        }

        if (sourceWidth > 0 && sourceHeight > 0)
        {
            double wanted = (double)sourceWidth / sourceHeight;
            double got = (double)candidate.Width / candidate.Height;

            if (Math.Abs(wanted - got) > 0.005)
            {
                string ratios = string.Create(
                    CultureInfo.InvariantCulture,
                    $"aspect ratio {got:F3} where the original is {wanted:F3}");

                rejections.Add($"{ratios}, so the geometry would stretch it");
            }

            if (candidate.Width < sourceWidth || candidate.Height < sourceHeight)
            {
                rejections.Add("smaller than the original");
            }
        }

        if (sourceAlpha && !candidateAlpha)
        {
            rejections.Add("the original is alpha-tested and this is opaque");
        }

        if (!sourceAlpha && candidateAlpha)
        {
            rejections.Add("transparent where the original is opaque");
        }

        int scale = sourceWidth > 0 ? candidate.Width / sourceWidth : 0;

        if (scale > RestorationLimit)
        {
            warnings.Add(
                $"{scale}x the original, past the {RestorationLimit}x the brief calls " +
                "restoration; this is a remake and needs to be judged as one");
        }

        if (entry is { Tier: > 0 })
        {
            warnings.Add($"tier {entry.Tier}, where this pass was meant to be tier 0");
        }

        return new EnhancedTexture
        {
            Name = name,
            Candidate = Path.GetRelativePath(workspace, file).Replace('\\', '/'),
            Verdict = rejections.Count > 0 ? TextureVerdict.Rejected : TextureVerdict.Draft,
            Rejections = rejections,
            Warnings = warnings,
            SourceWidth = sourceWidth,
            SourceHeight = sourceHeight,
            Width = candidate.Width,
            Height = candidate.Height,
            Scale = scale,
            Tier = entry?.Tier ?? -1,
            PlannedSize = entry?.TargetSize ?? 0,
            SourceHasAlpha = sourceAlpha,
            HasAlpha = candidateAlpha,
        };
    }

    /// <summary>Whether any pixel is not fully opaque.</summary>
    /// <remarks>
    /// Not whether the file has an alpha channel: a generator that writes RGBA and fills
    /// the alpha with 255 has produced an opaque image, and one that writes RGB where the
    /// original was keyed has lost something. What matters is what a renderer would see.
    /// </remarks>
    private static bool HasTransparency(DecodedImage image)
    {
        for (int i = 3; i < image.Pixels.Length; i += 4)
        {
            if (image.Pixels[i] != 255)
            {
                return true;
            }
        }

        return false;
    }

    private static string Reason(string rejection) =>
        rejection.StartsWith("aspect ratio", StringComparison.Ordinal) ? "aspect ratio" : rejection;

    private Dictionary<string, TexturePlanEntry>? Plan(
        string workspace, DiagnosticBag diagnostics)
    {
        string path = Path.Combine(workspace, "manifests", "texture-plan.json");

        if (!File.Exists(path))
        {
            diagnostics.Add(new Diagnostic(
                "TEX002",
                DiagnosticSeverity.Error,
                $"No texture plan at {path}; run texture-plan first so candidates can be " +
                "checked against what they replace."));

            return null;
        }

        TexturePlanManifest? plan = JsonSerializer.Deserialize<TexturePlanManifest>(
            File.ReadAllText(path), ManifestJson.Options);

        if (plan is null)
        {
            diagnostics.Add(new Diagnostic(
                "TEX003", DiagnosticSeverity.Error, $"{path} could not be read."));

            return null;
        }

        Dictionary<string, TexturePlanEntry> byName = new(StringComparer.OrdinalIgnoreCase);

        foreach (TexturePlanEntry texture in plan.Textures)
        {
            byName[texture.Name] = texture;
        }

        _log($"plan: {byName.Count} textures, {plan.TierCounts.GetValueOrDefault("tier0")} in tier 0");

        return byName;
    }

    private void Report(
        List<EnhancedTexture> results,
        int accepted,
        IReadOnlyDictionary<string, int> rejected,
        string output,
        string manifest)
    {
        _log($"{results.Count} candidates: {accepted} written to {output}, " +
             $"{results.Count - accepted} refused");

        foreach ((string reason, int count) in rejected.OrderByDescending(r => r.Value))
        {
            _log($"  refused, {count}: {reason}");

            foreach (EnhancedTexture texture in results
                         .Where(t => t.Rejections.Any(r => Reason(r) == reason))
                         .Take(8))
            {
                _log($"    {texture.Name} ({texture.SourceWidth}x{texture.SourceHeight} -> " +
                     $"{texture.Width}x{texture.Height})");
            }
        }

        Dictionary<string, int> warnings = new(StringComparer.Ordinal);

        foreach (EnhancedTexture texture in results.Where(t => t.Verdict != TextureVerdict.Rejected))
        {
            foreach (string warning in texture.Warnings)
            {
                string key = warning.Contains("restoration", StringComparison.Ordinal)
                    ? "past the 16x the brief calls restoration"
                    : warning;

                warnings[key] = warnings.GetValueOrDefault(key) + 1;
            }
        }

        foreach ((string warning, int count) in warnings.OrderByDescending(w => w.Value))
        {
            _log($"  worth a look, {count}: {warning}");
        }

        _log($"every accepted texture is a draft; nothing here is approved. Manifest: {manifest}");
    }
}
