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

            case "compile-sheep":
                return CompileSheep(options, diagnostics);

            case "actions":
                return Actions(options, diagnostics);

            case "render-model":
            case "render-scene":
                return Render(options, diagnostics);

            case "check-scenes":
                return CheckScenes(options, diagnostics);

            case "import-textures":
                return ImportTextures(options, diagnostics);

            case "act-info":
                return ActInfo(options, diagnostics);

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

    private static int Render(Options options, DiagnosticBag diagnostics)
    {
        if (options.Source is null || options.Model is null)
        {
            Console.Error.WriteLine($"{options.Command} requires --source and --model.");
            return 2;
        }

        string output = options.Output ?? Path.Combine(
            Environment.CurrentDirectory, options.Model + ".png");

        bool rendered = options.Command == "render-scene"
            ? new SceneRenderStage(Console.WriteLine).Run(
                options.Source,
                options.Model,
                options.Timeblock,
                options.Camera,
                options.RayTracing,
                output,
                options.Width,
                options.Height,
                options.WalkOverlay,
                options.WalkPath,
                options.Pick,
                options.NounMap,
                options.Perform,
                options.Advance,
                options.Glance,
                EnhancedDirectory(options),
                diagnostics)
            : new ModelRenderStage(Console.WriteLine).Run(
                options.Source, options.Model, output, options.Width, options.Height, diagnostics);

        Report(diagnostics);
        return rendered ? 0 : 3;
    }

    /// <summary>Where the enhanced textures are, if the caller asked for any.</summary>
    /// <remarks>
    /// A relative path is taken from the workspace, because that is where enhanced content
    /// lives and typing the whole thing every time is how a flag stops being used.
    /// </remarks>
    private static string? EnhancedDirectory(Options options)
    {
        if (options.Enhanced is not { Length: > 0 } directory)
        {
            return null;
        }

        return Path.IsPathRooted(directory) || options.Workspace is null
            ? directory
            : Path.Combine(options.Workspace, directory);
    }

    private static int ImportTextures(Options options, DiagnosticBag diagnostics)
    {
        if (options.Workspace is null)
        {
            Console.Error.WriteLine("import-textures requires --workspace.");
            return 2;
        }

        bool imported = new TextureImportStage(Console.WriteLine).Run(
            options.Workspace,
            options.Model ?? "enhanced/textures/imagegen-pilot",
            options.Variant ?? "_imagegen_2048w",
            options.Tool ?? "unrecorded",
            options.Force,
            diagnostics);

        Report(diagnostics);
        return imported ? 0 : 3;
    }

    private static int ActInfo(Options options, DiagnosticBag diagnostics)
    {
        if (options.Source is null)
        {
            Console.Error.WriteLine("act-info requires --source.");
            return 2;
        }

        bool clean = new ActInfoStage(Console.WriteLine).Run(
            options.Source, options.Model, options.Deep, diagnostics);

        Report(diagnostics);
        return clean ? 0 : 3;
    }

    private static int CheckScenes(Options options, DiagnosticBag diagnostics)
    {
        if (options.Source is null)
        {
            Console.Error.WriteLine("check-scenes requires --source.");
            return 2;
        }

        bool ok = new SceneCheckStage(Console.WriteLine).Run(
            options.Source, options.Model, options.Deep, diagnostics);

        Report(diagnostics);
        return ok ? 0 : 3;
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

        if (options.Execute)
        {
            var runner = new SheepExecuteStage(Console.WriteLine);
            SheepExecutionSummary run = runner.Run(options.Source, diagnostics, options.ApiReturnValue);

            Console.WriteLine();
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {run.Scripts} scripts, {run.Functions} functions executed, {run.Calls} API calls"));
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {run.Completed} completed, {run.Halted} halted, {run.Blocked} suspended, "
                + $"{run.Faulted} faulted"));

            Report(diagnostics);
            return diagnostics.HasErrors ? 1 : 0;
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
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  {summary.RoundTripped} of {summary.Scripts} written back out and read again identically"));

        Report(diagnostics);
        return diagnostics.HasErrors ? 1 : 0;
    }

    private static int CompileSheep(Options options, DiagnosticBag diagnostics)
    {
        if (options.Input is null)
        {
            Console.Error.WriteLine("compile-sheep requires --input <script.shp source file>.");
            return 2;
        }

        var stage = new SheepCompileStage(Console.WriteLine);
        SheepCompileSummary? summary = stage.Run(
            options.Input, options.Output, options.Source, diagnostics);

        if (summary is { } made)
        {
            Console.WriteLine();
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {made.Functions} function(s), {made.Instructions} instructions, "
                + $"{made.Bytes} bytes of bytecode"));
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {made.Imports} function(s) called, {made.Strings} string(s), "
                + $"{made.Variables} symbol(s)"));
        }

        Report(diagnostics);
        return summary is null || diagnostics.HasErrors ? 1 : 0;
    }

    private static int Actions(Options options, DiagnosticBag diagnostics)
    {
        if (options.Source is null)
        {
            Console.Error.WriteLine("actions requires --source.");
            return 2;
        }

        var stage = new ActionSurveyStage(Console.WriteLine);
        ActionSurveySummary summary = stage.Run(options.Source, diagnostics);

        Console.WriteLine();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  {summary.Files} files, {summary.Actions} actions, {summary.Nouns} nouns, {summary.Verbs} verbs"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  {summary.Cases} named conditions, {summary.CasesEvaluated} evaluated, {summary.CasesFailed} failed"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  {summary.UnreadableLines} unreadable lines"));

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
              sheep             Disassemble every compiled Sheep script, gather the
                                function signatures, and check the writer by reading
                                every one back.
              compile-sheep     Compile a Sheep source file to bytecode the game's
                                own machine runs. --input is the source, --output
                                the .SHP, --source the game data whose scripts say
                                what each function takes and returns.
              actions           Read the noun/verb/case files and resolve against them.
              render-model      Render one model from the archives to a PNG.
              render-scene      Render a scene, its props and its lighting, to a PNG.
              check-scenes      Load every scene at every point in the story and
                                report what came out. --model limits it to one
                                location.
              act-info          Read every vertex animation and say what is in them.
              import-textures   Check generated texture candidates against the
                                originals they replace and take the sound ones
                                into the enhanced set.

            options:
              --source <dir>       The game's Data directory. Read only; never modified.
              --input <file>       The file a command reads.
              --workspace <dir>    Content workspace root. Outputs go to build/.
              --ffmpeg-dir <dir>   Directory containing ffmpeg and ffprobe.
              --force              Redo work even when a cached output is still valid.
                                   For import-textures it also writes over textures
                                   already in the enhanced set, which are hand-corrected
                                   and live outside the repository. Without it, anything
                                   already there is left exactly as it is.
              --verify             Decompress and validate without writing anything.
              --model NAME         Model or scene to render; the extension is optional.
              --timeblock <block>  Which time of day render-scene loads. A story
                                   timeblock such as 202P decides the scene file's
                                   conditions and so loads the scene in one state;
                                   M, A, E or N only picks the bake.
              --camera NAME        Which of the scene's room cameras to render from.
              --rt none|low|med|high  How much ray tracing render-scene does.
              --output PATH        Where render-model writes its PNG.
              --width N            Render width (default 1024).
              --height N           Render height (default 768).
              --deep               check-scenes also loads geometry, bakes and
                                   textures, not only what a scene is made of.
              --variant SUFFIX     Which of each candidate's files import-textures
                                   takes (default _imagegen_2048w).
              --tool NAME          What produced the candidates, recorded as
                                   provenance by import-textures.
              --enhanced DIR       Textures to use in place of the archives',
                                   named without extensions. Relative paths are
                                   taken from --workspace.
              --walk-overlay       Draw where actors may stand over the floor, shaded
                                   by region: green is open ground, darkening towards
                                   the walls, amber for the regions scripts open.
              --walk-path FROM:TO  Find a way across the boundary and draw it, blue if
                                   it arrives and red if it could only get near. Each
                                   end is one of the scene's position names or a pair
                                   of world coordinates, x,z.
              --pick X,Y           Report what a click on that pixel would land on.
              --noun-map PATH      Write a map of what the player can click, one
                                   colour per noun, from the same camera as the
                                   render. Grey is scenery with no noun.
              --do NOUN:VERB       Carry out an action and report what it did.
                                   Needs --timeblock, since a story state is what
                                   decides which rule applies.
              --advance SECONDS    Let that much time pass afterwards and perform
                                   whatever the story had asked for by then.
              --glance ACTOR:AT    Turn an actor's head towards another actor, a
                                   prop or an object in the geometry.

            The toolchain never writes to the source installation.
            """);

    private sealed record Options
    {
        public string? Command { get; init; }

        public string? Source { get; init; }

        public string? Workspace { get; init; }

        public string? FfmpegDirectory { get; init; }

        public string? Model { get; init; }

        public string? Timeblock { get; init; }

        public string? Camera { get; init; }

        public string? RayTracing { get; init; }

        public string? Output { get; init; }

        public string? Input { get; init; }

        public int Width { get; init; } = 1024;

        public int Height { get; init; } = 768;

        public bool Deep { get; init; }

        public bool WalkOverlay { get; init; }

        public string? WalkPath { get; init; }

        public string? Pick { get; init; }

        public string? NounMap { get; init; }

        public string? Perform { get; init; }

        public double Advance { get; init; }

        public string? Glance { get; init; }

        public string? Variant { get; init; }

        public string? Tool { get; init; }

        public string? Enhanced { get; init; }

        public bool Force { get; init; }

        public bool Verify { get; init; }

        public bool Execute { get; init; }

        public int ApiReturnValue { get; init; }

        public string? Error { get; init; }

        public static Options Parse(string[] args)
        {
            string? source = null, workspace = null, ffmpeg = null, model = null, output = null;
            string? input = null;
            string? timeblock = null, camera = null, rayTracing = null;
            int width = 1024, height = 768;
            bool deep = false;
            bool walkOverlay = false;
            string? walkPath = null;
            string? pick = null;
            string? nounMap = null;
            string? perform = null;
            double advance = 0;
            string? glance = null;
            string? variant = null;
            string? tool = null;
            string? enhanced = null;
            bool force = false;
            bool verify = false;
            bool execute = false;
            int apiReturnValue = 0;

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
                    case "--execute":
                        execute = true;
                        break;
                    case "--api-returns" when i + 1 < args.Length:
                        apiReturnValue = int.Parse(args[++i], CultureInfo.InvariantCulture);
                        break;
                    case "--rt" when i + 1 < args.Length:
                        rayTracing = args[++i];
                        break;
                    case "--camera" when i + 1 < args.Length:
                        camera = args[++i];
                        break;
                    case "--timeblock" when i + 1 < args.Length:
                        timeblock = args[++i];
                        break;
                    case "--model" when i + 1 < args.Length:
                        model = args[++i];
                        break;
                    case "--input" when i + 1 < args.Length:
                        input = args[++i];
                        break;

                    case "--output" when i + 1 < args.Length:
                        output = args[++i];
                        break;
                    case "--width" when i + 1 < args.Length:
                        width = int.Parse(args[++i], CultureInfo.InvariantCulture);
                        break;
                    case "--height" when i + 1 < args.Length:
                        height = int.Parse(args[++i], CultureInfo.InvariantCulture);
                        break;
                    case "--deep":
                        deep = true;
                        break;
                    case "--walk-overlay":
                        walkOverlay = true;
                        break;
                    case "--walk-path" when i + 1 < args.Length:
                        walkPath = args[++i];
                        break;
                    case "--pick" when i + 1 < args.Length:
                        pick = args[++i];
                        break;
                    case "--noun-map" when i + 1 < args.Length:
                        nounMap = args[++i];
                        break;
                    case "--do" when i + 1 < args.Length:
                        perform = args[++i];
                        break;
                    case "--advance" when i + 1 < args.Length:
                        advance = double.Parse(args[++i], CultureInfo.InvariantCulture);
                        break;
                    case "--glance" when i + 1 < args.Length:
                        glance = args[++i];
                        break;
                    case "--variant" when i + 1 < args.Length:
                        variant = args[++i];
                        break;
                    case "--tool" when i + 1 < args.Length:
                        tool = args[++i];
                        break;
                    case "--enhanced" when i + 1 < args.Length:
                        enhanced = args[++i];
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
                Execute = execute,
                ApiReturnValue = apiReturnValue,
                Model = model,
                Timeblock = timeblock,
                Camera = camera,
                RayTracing = rayTracing,
                Output = output,
                Input = input,
                Width = width,
                Height = height,
                Deep = deep,
                WalkOverlay = walkOverlay,
                WalkPath = walkPath,
                Pick = pick,
                NounMap = nounMap,
                Perform = perform,
                Advance = advance,
                Glance = glance,
                Variant = variant,
                Tool = tool,
                Enhanced = enhanced,
            };
        }
    }
}
