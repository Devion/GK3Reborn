using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using GK3Reborn.Foundation.Diagnostics;

namespace GK3Reborn.Tools.Media;

/// <summary>Result of running an external process.</summary>
/// <param name="ExitCode">Process exit code.</param>
/// <param name="StandardOutput">Captured stdout.</param>
/// <param name="StandardError">Captured stderr.</param>
public readonly record struct ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    /// <summary>True when the process exited successfully.</summary>
    public bool Succeeded => ExitCode == 0;
}

/// <summary>
/// Locates and drives the pinned external FFmpeg toolchain.
/// </summary>
/// <remarks>
/// Plan/01-architecture.md: video import uses an external FFmpeg executable whose
/// version is checked. Conversion is an offline import concern; the runtime never
/// shells out.
/// </remarks>
public sealed class FfmpegTools
{
    private FfmpegTools(string ffmpeg, string ffprobe, string version)
    {
        FfmpegPath = ffmpeg;
        FfprobePath = ffprobe;
        Version = version;
    }

    /// <summary>Path to the ffmpeg executable.</summary>
    public string FfmpegPath { get; }

    /// <summary>Path to the ffprobe executable.</summary>
    public string FfprobePath { get; }

    /// <summary>Version banner reported by ffmpeg.</summary>
    public string Version { get; }

    /// <summary>
    /// Finds ffmpeg and ffprobe, preferring an explicit directory, then <c>libs</c>,
    /// then PATH.
    /// </summary>
    /// <param name="explicitDirectory">Directory to search first, if any.</param>
    /// <param name="diagnostics">Receives an error when the tools are unusable.</param>
    /// <returns>The located toolchain, or null.</returns>
    public static FfmpegTools? Locate(string? explicitDirectory, DiagnosticBag diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);

        string exe = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
        List<string> roots = [];
        if (!string.IsNullOrWhiteSpace(explicitDirectory))
        {
            roots.Add(explicitDirectory);
        }

        roots.Add(Path.Combine(AppContext.BaseDirectory, "libs", "tools"));

        foreach (string root in roots)
        {
            string ffmpeg = Path.Combine(root, "ffmpeg" + exe);
            string ffprobe = Path.Combine(root, "ffprobe" + exe);
            if (File.Exists(ffmpeg) && File.Exists(ffprobe))
            {
                return Verify(ffmpeg, ffprobe, diagnostics);
            }
        }

        return Verify("ffmpeg" + exe, "ffprobe" + exe, diagnostics);
    }

    /// <summary>Probes a media file, returning parsed ffprobe JSON.</summary>
    /// <param name="path">File to probe.</param>
    /// <param name="error">Prober error text when probing fails.</param>
    /// <returns>The parsed document, or null when the file is not decodable.</returns>
    public JsonDocument? Probe(string path, out string? error)
    {
        ProcessResult result = Run(FfprobePath,
        [
            "-hide_banner", "-v", "error", "-print_format", "json",
            "-show_format", "-show_streams", path,
        ]);

        if (!result.Succeeded)
        {
            error = result.StandardError.Trim();
            return null;
        }

        error = null;
        try
        {
            return JsonDocument.Parse(result.StandardOutput);
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return null;
        }
    }

    /// <summary>Runs ffmpeg with the given arguments.</summary>
    public ProcessResult RunFfmpeg(IReadOnlyList<string> arguments) => Run(FfmpegPath, arguments);

    private static FfmpegTools? Verify(string ffmpeg, string ffprobe, DiagnosticBag diagnostics)
    {
        try
        {
            ProcessResult v = Run(ffmpeg, ["-hide_banner", "-version"]);
            if (!v.Succeeded)
            {
                diagnostics.Add(new Diagnostic(
                    "GK3R2001", DiagnosticSeverity.Error,
                    "ffmpeg was found but did not run successfully.",
                    ffmpeg, null, "exit code 0",
                    v.ExitCode.ToString(CultureInfo.InvariantCulture),
                    "Install a working FFmpeg build, or pass --ffmpeg-dir."));
                return null;
            }

            ProcessResult p = Run(ffprobe, ["-hide_banner", "-version"]);
            if (!p.Succeeded)
            {
                diagnostics.Add(new Diagnostic(
                    "GK3R2002", DiagnosticSeverity.Error,
                    "ffprobe was found but did not run successfully.",
                    ffprobe, null, "exit code 0",
                    p.ExitCode.ToString(CultureInfo.InvariantCulture),
                    "Install a working FFmpeg build, or pass --ffmpeg-dir."));
                return null;
            }

            string banner = v.StandardOutput.Split('\n')[0].Trim();
            return new FfmpegTools(ffmpeg, ffprobe, banner);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            diagnostics.Add(new Diagnostic(
                "GK3R2000", DiagnosticSeverity.Error,
                "ffmpeg and ffprobe were not found.",
                null, null, "ffmpeg/ffprobe on PATH or in libs/tools", ex.Message,
                "Install FFmpeg, or pass --ffmpeg-dir pointing at a directory containing both executables."));
            return null;
        }
    }

    private static ProcessResult Run(string fileName, IReadOnlyList<string> arguments)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (string a in arguments)
        {
            psi.ArgumentList.Add(a);
        }

        using Process process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Could not start {fileName}.");

        // Read both pipes before waiting, or a full buffer deadlocks the child.
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();

        return new ProcessResult(process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
    }
}
