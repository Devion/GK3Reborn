using System.Globalization;
using GK3Reborn.Content.Manifests;
using GK3Reborn.Foundation.Diagnostics;
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
            case "import-video":
                return ImportVideo(options, diagnostics);

            case "compile-content":
            case "inspect":
            case "sheep":
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
              import-video      Convert the BIK/AVI cinematic corpus to the runtime format.
              compile-content   Compile workspace content into runtime packages. (not yet)
              inspect           Inspect converted assets and manifests. (not yet)
              sheep             Compile, disassemble and diff Sheep scripts. (not yet)

            options:
              --source <dir>       The game's Data directory. Read only; never modified.
              --workspace <dir>    Content workspace root. Outputs go to build/.
              --ffmpeg-dir <dir>   Directory containing ffmpeg and ffprobe.
              --force              Redo work even when a cached output is still valid.

            The toolchain never writes to the source installation.
            """);

    private sealed record Options
    {
        public string? Command { get; init; }

        public string? Source { get; init; }

        public string? Workspace { get; init; }

        public string? FfmpegDirectory { get; init; }

        public bool Force { get; init; }

        public string? Error { get; init; }

        public static Options Parse(string[] args)
        {
            string? source = null, workspace = null, ffmpeg = null;
            bool force = false;

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
            };
        }
    }
}
