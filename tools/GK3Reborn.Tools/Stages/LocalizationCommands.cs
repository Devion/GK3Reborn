using System.Globalization;
using GK3Reborn.Content;
using GK3Reborn.Content.Manifests;
using GK3Reborn.Foundation.Diagnostics;
using GK3Reborn.Tools.Media;

namespace GK3Reborn.Tools.Stages;

/// <summary>
/// <c>extract-localized</c>: works out what each language of GK3 does differently.
/// </summary>
/// <remarks>
/// Its own flag parser, for the same reason the pack and scene commands have theirs:
/// <c>--localized</c>, <c>--language</c> and <c>--no-video</c> mean nothing to any other
/// command, and adding them to the record every command shares would make every command's
/// help worse.
/// </remarks>
public static class LocalizationCommands
{
    /// <summary>The commands this file owns.</summary>
    public static IReadOnlyList<string> Commands { get; } = ["extract-localized"];

    /// <summary>Runs one of them.</summary>
    /// <param name="args">The whole command line, the command included.</param>
    /// <returns>A process exit code.</returns>
    public static int Run(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        string? workspace = Flag(args, "--workspace");
        string? localized = Flag(args, "--localized");
        string? source = Flag(args, "--source");
        string? only = Flag(args, "--language");
        bool force = Has(args, "--force");
        bool dryRun = Has(args, "--dry-run");
        bool noVideo = Has(args, "--no-video");

        if (workspace is null)
        {
            return Usage("extract-localized requires --workspace.");
        }

        // ContentWorkspace/Localized by default, because that is where the per-language
        // releases live and naming it every time is a flag that is always the same.
        localized ??= Path.Combine(workspace, "Localized");

        if (only is { Length: > 0 } && !GameLanguage.IsKnown(only))
        {
            return Usage(
                $"--language {only} names no localisation GK3 was published in. "
                + $"Try one of: {string.Join(", ", GameLanguage.Known.Select(l => l.Code))}.");
        }

        var diagnostics = new DiagnosticBag();

        // The movies need FFmpeg and the assets do not, so a machine without it does the
        // fifteen thousand assets and says what it could not do about the sixteen movies —
        // rather than refusing to do either.
        LocalizationVideoStage? video = null;

        if (!noVideo)
        {
            var media = new DiagnosticBag();
            FfmpegTools? tools = FfmpegTools.Locate(Flag(args, "--ffmpeg-dir"), media);

            if (tools is not null)
            {
                Console.WriteLine($"ffmpeg: {tools.Version}");
                video = new LocalizationVideoStage(tools, Console.WriteLine);
            }
            else
            {
                Console.WriteLine(
                    "ffmpeg: not found, so the cutscene soundtracks are left alone. "
                    + "Pass --ffmpeg-dir, or --no-video to say so on purpose.");
            }
        }

        Console.WriteLine($"localized: {localized}");
        Console.WriteLine($"workspace: {workspace}");

        if (source is { Length: > 0 })
        {
            Console.WriteLine($"source:    {source}");
        }

        Console.WriteLine();

        var stage = new LocalizationExtractStage(Console.WriteLine);
        LocalizationSetManifest? manifest = stage.Run(
            localized, source ?? string.Empty, workspace, only, video, force, dryRun,
            diagnostics);

        if (manifest is not null)
        {
            Console.WriteLine();

            foreach (LocalizationLanguageEntry entry in manifest.Languages)
            {
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"  {entry.Language,-4} {entry.Name,-12} {entry.Assets,7} assets  "
                    + $"{entry.Textures.Count,5} bitmaps  "
                    + $"{entry.Movies.Count(m => m.Disposition == LocalizationMovieDisposition.Soundtrack),3} soundtracks  "
                    + $"{entry.Movies.Count(m => m.Disposition == LocalizationMovieDisposition.Recut),3} re-cut"));
            }
        }

        foreach (Diagnostic diagnostic in diagnostics.Items)
        {
            Console.Error.WriteLine(diagnostic.ToString());
        }

        return manifest is null || diagnostics.HasErrors ? 1 : 0;
    }

    private static string? Flag(string[] args, string name)
    {
        for (int i = 1; i < args.Length - 1; i++)
        {
            if (args[i].Equals(name, StringComparison.Ordinal))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static bool Has(string[] args, string name) =>
        args.Skip(1).Any(a => a.Equals(name, StringComparison.Ordinal));

    private static int Usage(string message)
    {
        Console.Error.WriteLine(message);
        Console.Error.WriteLine(
            """

            extract-localized --workspace DIR [--localized DIR] [--source DIR]
                              [--language CODE] [--no-video] [--ffmpeg-dir DIR]
                              [--force] [--dry-run]

              Compares the per-language releases of GK3 and writes out, for each of them,
              only the assets it spells or records differently. Those become the language
              packs the game reads in front of the installation.

              --workspace DIR   The content workspace. Written to.
              --localized DIR   The per-language releases, one subdirectory each, named
                                for its ISO 639-1 code: EN, FR, DE, IT, ES, PT, RU, PL.
                                Each may hold *.brn archives or a dumped tree of loose
                                files. Default: <workspace>/Localized.
              --source DIR      The installed game's Data directory, used as the fallback
                                reference for a name the baseline release does not hold.
                                Read and never written.
              --language CODE   Write only this language. The comparison still reads every
                                release, because that is what decides the set.
              --no-video        Leave the cutscene soundtracks alone.
              --ffmpeg-dir DIR  Where ffmpeg and ffprobe are, if not on the path.
              --force           Write every asset again rather than keeping identical ones.
              --dry-run         Say what would happen and write nothing.

              Writes enhanced/localized/<CODE>/, manifests/localization.json and
              reports/localization.md, and nothing anywhere else. In particular it never
              writes to enhanced/localtextures/, which is hand-curated: the pictures worth
              repainting are reported as "surfaces" in the manifest and put there by a
              person. Run `rebuild-content.cmd` afterwards to pack everything.
            """);

        return 2;
    }
}
