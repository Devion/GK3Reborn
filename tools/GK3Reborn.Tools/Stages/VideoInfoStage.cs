using System.Globalization;
using GK3Reborn.Content;
using GK3Reborn.Formats.Audio;
using GK3Reborn.Formats.Rebarn;
using GK3Reborn.Foundation.Diagnostics;

namespace GK3Reborn.Tools.Stages;

/// <summary>
/// Says which movies a run could play, where each comes from, and whether it decodes.
/// </summary>
/// <remarks>
/// <para>
/// The same question the game asks, asked without a window: a movie has to be found — in a
/// ReBarn pack or loose in the workspace — opened, and decoded to pixels. Each of those can
/// fail on its own and the failures look alike from the outside, which is what this is for.
/// </para>
/// <para>
/// It decodes rather than probing. A container's header will happily report a resolution
/// and a duration for a file whose frames are missing or whose codec the decoder was not
/// built with, so the only answer worth having comes from asking for the pixels and
/// looking at them.
/// </para>
/// </remarks>
public sealed class VideoInfoStage
{
    private readonly Action<string> _log;

    /// <summary>Creates the stage.</summary>
    /// <param name="log">Progress sink.</param>
    public VideoInfoStage(Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
    }

    /// <summary>Reports on the movies.</summary>
    /// <param name="workspace">Content workspace root, or null for packs only.</param>
    /// <param name="packDirectory">Where the ReBarn volumes are, or null for none.</param>
    /// <param name="only">One movie's name, or null for all of them.</param>
    /// <param name="deep">Decode every movie rather than reporting what is there.</param>
    /// <param name="diagnostics">Receives what went wrong.</param>
    /// <returns>True when every movie asked about decoded.</returns>
    public bool Run(
        string? workspace,
        string? packDirectory,
        string? only,
        bool deep,
        DiagnosticBag diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);

        using RebarnContent packs = packDirectory is { Length: > 0 }
            ? RebarnContent.Open(packDirectory, diagnostics)
            : RebarnContent.OpenFiles([]);

        string loose = workspace is { Length: > 0 }
            ? Path.Combine(workspace, "enhanced", "video")
            : string.Empty;

        VideoLibrary videos = VideoLibrary.Open(loose, packs);

        _log($"packs: {packs.VolumeCount} volume(s) in " +
             $"{(packDirectory is { Length: > 0 } ? packDirectory : "(none given)")}, " +
             $"{videos.PackedCount} movie(s)");

        _log($"loose: {(loose.Length > 0 ? loose : "(not read)")}, {videos.LooseCount} movie(s)");
        _log($"{videos.Count} movie(s) in all; a loose file wins where both have one");

        if (!MoviePlayback.Prepare(null, diagnostics))
        {
            _log("no decoder, so nothing below could be played");
            return false;
        }

        _log($"decoder: FFmpeg from {MoviePlayback.LoadedFrom}");
        _log(string.Empty);

        IReadOnlyList<string> wanted = only is { Length: > 0 }
            ? [only]
            : videos.Names;

        if (wanted.Count == 0)
        {
            _log("nothing to report on.");
            return true;
        }

        int decoded = 0;
        int failed = 0;
        double seconds = 0;

        foreach (string name in wanted)
        {
            if (!videos.Has(name))
            {
                _log($"  {name,-16} not in the packs or the workspace");
                failed++;
                continue;
            }

            string from = Describe(videos.Source(name), loose);

            using Movie? movie = Movie.Open(videos, name, diagnostics);

            if (movie is null)
            {
                _log($"  {name,-16} {from,-10} would not open");
                failed++;
                continue;
            }

            seconds += movie.Duration.TotalSeconds;

            // The first frame always, because that is what a player sees and it is the one
            // most likely to be missing. The rest only when asked, since it is the whole
            // corpus otherwise.
            string looked = Look(movie, deep);

            _log($"  {name,-16} {from,-10} {movie.Describe()}; {looked}");

            if (looked.StartsWith("no ", StringComparison.Ordinal))
            {
                failed++;
            }
            else
            {
                decoded++;
            }
        }

        _log(string.Empty);
        _log(string.Create(
            CultureInfo.InvariantCulture,
            $"{decoded} of {wanted.Count} decoded, {failed} did not, " +
            $"{seconds / 60:F1} minutes in all"));

        return failed == 0;
    }

    /// <summary>Which source a movie came from, said shortly.</summary>
    private static string Describe(string? source, string loose) =>
        source is null ? "?"
        : loose.Length > 0 && source.StartsWith(loose, StringComparison.OrdinalIgnoreCase) ? "workspace"
        : "pack";

    /// <summary>Decodes and says what came out.</summary>
    private static string Look(Movie movie, bool deep)
    {
        if (!movie.TryReadFrame(TimeSpan.Zero, out MovieFrame first))
        {
            return "no first frame";
        }

        string opening = string.Create(
            CultureInfo.InvariantCulture,
            $"opens at {Brightness(first):F0}/255");

        if (!deep)
        {
            return opening;
        }

        // Four moments across the movie, because a file can decode its opening frame and
        // nothing else — a truncated import looks exactly like a good one until it is
        // asked for the end.
        int found = 0;
        double total = 0;

        foreach (double part in (double[])[0.25, 0.5, 0.75, 0.95])
        {
            var at = TimeSpan.FromSeconds(movie.Duration.TotalSeconds * part);

            if (movie.TryReadFrame(at, out MovieFrame frame))
            {
                found++;
                total += Brightness(frame);
            }
        }

        return found == 4
            ? string.Create(CultureInfo.InvariantCulture, $"{opening}, four more at {total / 4:F0}/255")
            : string.Create(CultureInfo.InvariantCulture, $"no frame at {4 - found} of four later moments");
    }

    /// <summary>Mean luminance, which is how a decoded frame is told from an empty one.</summary>
    private static double Brightness(MovieFrame frame)
    {
        ReadOnlySpan<byte> pixels = frame.Rgba.Span;

        if (pixels.Length < 4)
        {
            return 0;
        }

        double total = 0;

        for (int i = 0; i + 3 < pixels.Length; i += 4)
        {
            total += (0.2126 * pixels[i]) + (0.7152 * pixels[i + 1]) + (0.0722 * pixels[i + 2]);
        }

        return total / (pixels.Length / 4);
    }
}
