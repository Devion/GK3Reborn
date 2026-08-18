using System.Globalization;
using GK3Reborn.Content.Manifests;
using GK3Reborn.Foundation.Diagnostics;
using GK3Reborn.Formats.Barn;
using GK3Reborn.Tools.Media;
using GK3Reborn.Tools.Stages;

namespace GK3Reborn.Tools;

/// <summary>
/// Entry point for the offline toolchain: content import, content compilation,
/// asset inspection and Sheep utilities.
/// </summary>
/// <remarks>
/// These were separate executables at first. They share the same parsers, manifests
/// and diagnostics, and none of them is large, so they are one command with
/// subcommands instead. See ADR 0005.
/// </remarks>
public static class Program
{
    /// <summary>Runs the requested subcommand.</summary>
    /// <param name="args">Command-line arguments.</param>
    /// <returns>Process exit code.</returns>
    public static int Main(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintUsage();
            return args.Length == 0 ? 1 : 0;
        }

        Options options = Options.Parse(args);
        if (options.Error is not null)
        {
            Console.Error.WriteLine(options.Error);
            PrintUsage();
            return 2;
        }

        var diagnostics = new DiagnosticBag();

        switch (options.Command)
        {
            case "extract-barn":
                return ExtractBarn(options, diagnostics);

            case "inventory":
                return Inventory(options, diagnostics);

            case "organize":
                return Organize(options, diagnostics);

            case "classify-models":
                return ClassifyModels(options, diagnostics);

            case "texture-plan":
                return TexturePlan(options, diagnostics);

            case "lighting-analysis":
                return LightingAnalysis(options, diagnostics);

            case "derive-lighting":
                return DeriveLighting(options, diagnostics);

            case "import-video":
                return ImportVideo(options, diagnostics);

            case "sheep":
                return Sheep(options, diagnostics);

            case "compile-content":
            case "inspect":
                Console.Error.WriteLine($"{options.Command}: not implemented yet.");
                return 64;

            default:
                Console.Error.WriteLine($"Unknown command: {options.Command}");
                PrintUsage();
                return 2;
        }
    }

    private static int ImportVideo(Options options, DiagnosticBag diagnostics)
    {
        if (options.Source is null || options.Workspace is null)
        {
            Console.Error.WriteLine("import-video requires --source and --workspace.");
            return 2;
        }

        if (!Directory.Exists(options.Source))
        {
            Console.Error.WriteLine($"Source directory does not exist: {options.Source}");
            return 2;
        }

        FfmpegTools? tools = FfmpegTools.Locate(options.FfmpegDirectory, diagnostics);
        if (tools is null)
        {
            Report(diagnostics);
            return 3;
        }

        Console.WriteLine($"ffmpeg: {tools.Version}");
        Console.WriteLine($"source: {options.Source}");
        Console.WriteLine($"workspace: {options.Workspace}");
        Console.WriteLine();

        var stage = new VideoImportStage(tools, Console.WriteLine);
        VideoManifest manifest = stage.Run(options.Source, options.Workspace, options.Force, diagnostics);

        Console.WriteLine();
        foreach ((string key, int count) in manifest.Summary.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {key,-24} {count}"));
        }

        Report(diagnostics);
        return diagnostics.HasErrors ? 1 : 0;
    }

    private static int ExtractBarn(Options options, DiagnosticBag diagnostics)
    {
        if (options.Source is null || options.Workspace is null)
        {
            Console.Error.WriteLine("extract-barn requires --source and --workspace.");
            return 2;
        }

        if (!Directory.Exists(options.Source))
        {
            Console.Error.WriteLine($"Source directory does not exist: {options.Source}");
            return 2;
        }

        Console.WriteLine($"source: {options.Source}");
        Console.WriteLine($"workspace: {options.Workspace}");
        Console.WriteLine(options.Verify ? "mode: verify only (nothing written)" : "mode: extract");
        Console.WriteLine();

        var stage = new BarnExtractStage(Console.WriteLine);
        BarnManifest manifest = stage.Run(
            options.Source, options.Workspace, writeFiles: !options.Verify, diagnostics);

        Console.WriteLine();
        foreach ((string key, int count) in manifest.Summary.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {key,-24} {count}"));
        }

        Report(diagnostics);
        return diagnostics.HasErrors ? 1 : 0;
    }

    private static int Inventory(Options options, DiagnosticBag diagnostics)
    {
        if (options.Source is null || options.Workspace is null)
        {
            Console.Error.WriteLine("inventory requires --source and --workspace.");
            return 2;
        }

        if (!Directory.Exists(options.Source))
        {
            Console.Error.WriteLine($"Source directory does not exist: {options.Source}");
            return 2;
        }

        Console.WriteLine($"source: {options.Source}");
        Console.WriteLine($"workspace: {options.Workspace}");
        Console.WriteLine();

        var stage = new CorpusInventoryStage(Console.WriteLine);
        CorpusManifest manifest = stage.Run(options.Source, options.Workspace, diagnostics);

        Console.WriteLine();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  {manifest.Summary.Assets} assets, {manifest.Summary.TotalBytes / 1_048_576.0:F0} MB, "
            + $"{manifest.Summary.DistinctExtensions} distinct extensions"));
        Console.WriteLine();
        Console.WriteLine("  kind                  count        MB   extensions");

        foreach ((string kind, int count) in manifest.KindCounts)
        {
            long bytes = manifest.KindBytes.GetValueOrDefault(kind);
            int extensions = manifest.ExtensionsByKind.GetValueOrDefault(kind);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {kind,-20} {count,6} {bytes / 1_048_576.0,9:F1}   {extensions,6}"));
        }

        Console.WriteLine();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  references: {manifest.Summary.ReferencesResolved} resolved, "
            + $"{manifest.Summary.ReferencesDangling} dangling"));

        Report(diagnostics);
        return diagnostics.HasErrors ? 1 : 0;
    }

    private static int Organize(Options options, DiagnosticBag diagnostics)
    {
        if (options.Source is null || options.Workspace is null)
        {
            Console.Error.WriteLine("organize requires --source and --workspace.");
            return 2;
        }

        if (!Directory.Exists(options.Source))
        {
            Console.Error.WriteLine($"Source directory does not exist: {options.Source}");
            return 2;
        }

        Console.WriteLine($"source: {options.Source}");
        Console.WriteLine($"workspace: {options.Workspace}");
        Console.WriteLine();

        var stage = new OrganizeStage(Console.WriteLine);
        OrganizedManifest manifest = stage.Run(options.Source, options.Workspace, diagnostics);

        Console.WriteLine();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  {manifest.Summary.Assets} assets placed, {manifest.Summary.Converted} converted, "
            + $"{manifest.Summary.Failed} failed"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  {manifest.Summary.SourceBytes / 1_048_576.0:F0} MB in, "
            + $"{manifest.Summary.OutputBytes / 1_048_576.0:F0} MB out"));
        Console.WriteLine();

        foreach ((string directory, int count) in manifest.DirectoryCounts)
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {directory,-20} {count,6}"));
        }

        Report(diagnostics);
        return diagnostics.HasErrors ? 1 : 0;
    }

    private static int ClassifyModels(Options options, DiagnosticBag diagnostics)
    {
        if (options.Source is null || options.Workspace is null)
        {
            Console.Error.WriteLine("classify-models requires --source and --workspace.");
            return 2;
        }

        var stage = new ModelRoleStage(Console.WriteLine);
        ModelRoleManifest manifest = stage.Run(options.Source, options.Workspace, diagnostics);

        Console.WriteLine();
        foreach ((string disposition, int count) in manifest.DispositionCounts)
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {disposition,-16} {count,6}"));
        }

        Report(diagnostics);
        return diagnostics.HasErrors ? 1 : 0;
    }

    private static int TexturePlan(Options options, DiagnosticBag diagnostics)
    {
        if (options.Source is null || options.Workspace is null)
        {
            Console.Error.WriteLine("texture-plan requires --source and --workspace.");
            return 2;
        }

        var stage = new TexturePlanStage(Console.WriteLine);
        TexturePlanManifest manifest = stage.Run(options.Source, options.Workspace, diagnostics);

        Console.WriteLine();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  {manifest.Textures.Count} textures, {manifest.TotalMegapixels} megapixels total"));
        foreach ((string tier, int count) in manifest.TierCounts)
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {tier,-8} {count,6}"));
        }

        Report(diagnostics);
        return diagnostics.HasErrors ? 1 : 0;
    }

    private static int LightingAnalysis(Options options, DiagnosticBag diagnostics)
    {
        if (options.Source is null || options.Workspace is null)
        {
            Console.Error.WriteLine("lighting-analysis requires --source and --workspace.");
            return 2;
        }

        var stage = new LightingAnalysisStage(Console.WriteLine);
        LightingAnalysisManifest manifest = stage.Run(options.Source, options.Workspace, diagnostics);
        LightingSummary s = manifest.Summary;

        Console.WriteLine();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  {s.Sets} lightmap sets over {s.Scenes} scenes"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  {s.ScenesWithTimeblockVariants} scenes have more than one time of day"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  surfaces: {s.DirectionalSurfaces} directional, {s.FlatSurfaces} flat, {s.DarkSurfaces} dark"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  {s.DirectionalFraction:P1} of surfaces carry directional information"));

        Report(diagnostics);
        return diagnostics.HasErrors ? 1 : 0;
    }

    private static int DeriveLighting(Options options, DiagnosticBag diagnostics)
    {
        if (options.Source is null || options.Workspace is null)
        {
            Console.Error.WriteLine("derive-lighting requires --source and --workspace.");
            return 2;
        }

        var stage = new LightRigStage(Console.WriteLine);
        LightRigSummary summary = stage.Run(options.Source, options.Workspace, diagnostics);

        Console.WriteLine();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  {summary.Rigs} rigs written, {summary.Lights} lights proposed"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  {summary.LowConfidence} lights need review before use"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  {summary.ScenesWithoutLights} scenes yielded nothing and need authoring"));

        Report(diagnostics);
        return diagnostics.HasErrors ? 1 : 0;
    }

    private static int Sheep(Options options, DiagnosticBag diagnostics)
    {
        if (options.Source is null || options.Workspace is null)
        {
            Console.Error.WriteLine("sheep requires --source and --workspace.");
            return 2;
        }

        var stage = new SheepDisassembleStage(Console.WriteLine);
        SheepDisassemblySummary summary = stage.Run(options.Source, options.Workspace, diagnostics);

        Console.WriteLine();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  {summary.Scripts} scripts, {summary.Functions} functions, {summary.Instructions} instructions"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  {summary.FullyDecoded} decoded completely, {summary.Partial} stopped early, {summary.Failed} failed"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  {summary.DistinctImports} distinct API functions called"));

        Report(diagnostics);
        return diagnostics.HasErrors ? 1 : 0;
    }

    private static void Report(DiagnosticBag diagnostics)
    {
        if (diagnostics.Items.Count == 0)
        {
            return;
        }

        Console.WriteLine();
        foreach (Diagnostic d in diagnostics.Items)
        {
            TextWriter writer = d.Severity == DiagnosticSeverity.Error ? Console.Error : Console.Out;
            writer.WriteLine(d.ToString());
        }
    }

    private static void PrintUsage() =>
        Console.WriteLine(
            """
            GK3Reborn offline toolchain

            usage:
              GK3Reborn.Tools <command> [options]

            commands:
              extract-barn      Extract every entry from every Barn archive.
              inventory         Classify every asset and map what references what.
              organize          Lay the corpus out by kind and convert textures to PNG.
              classify-models   Work out what each model is for, from the scene files.
              texture-plan      Rank textures by how visible they are, for enhancement.
              lighting-analysis Measure the baked lighting, as evidence for light rigs.
              derive-lighting   Propose a light rig per scene and time of day.
              import-video      Convert the BIK/AVI cinematic corpus to the runtime format.
              compile-content   Compile workspace content into runtime packages. (not yet)
              inspect           Inspect converted assets and manifests. (not yet)
              sheep             Disassemble every compiled Sheep script.

            options:
              --source <dir>       The game's Data directory. Read only; never modified.
              --workspace <dir>    Content workspace root. Outputs go to build/.
              --ffmpeg-dir <dir>   Directory containing ffmpeg and ffprobe.
              --force              Redo work even when a cached output is still valid.
              --verify             Decompress and validate without writing anything.

            The toolchain never writes to the source installation.
            """);

    private sealed record Options
    {
        public string? Command { get; init; }

        public string? Source { get; init; }

        public string? Workspace { get; init; }

        public string? FfmpegDirectory { get; init; }

        public bool Force { get; init; }

        public bool Verify { get; init; }

        public string? Error { get; init; }

        public static Options Parse(string[] args)
        {
            string? source = null, workspace = null, ffmpeg = null;
            bool force = false;
            bool verify = false;

            for (int i = 1; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--source" when i + 1 < args.Length:
                        source = args[++i];
                        break;
                    case "--workspace" when i + 1 < args.Length:
                        workspace = args[++i];
                        break;
                    case "--ffmpeg-dir" when i + 1 < args.Length:
                        ffmpeg = args[++i];
                        break;
                    case "--force":
                        force = true;
                        break;
                    case "--verify":
                        verify = true;
                        break;
                    default:
                        return new Options { Error = $"Unrecognized or incomplete argument: {args[i]}" };
                }
            }

            return new Options
            {
                Command = args[0],
                Source = source,
                Workspace = workspace,
                FfmpegDirectory = ffmpeg,
                Force = force,
                Verify = verify,
            };
        }
    }
}
