using System.Globalization;
using System.Text.RegularExpressions;
using GK3Reborn.Content;
using GK3Reborn.Content.Manifests;
using GK3Reborn.Formats.Rebarn;
using GK3Reborn.Tools.Media;

namespace GK3Reborn.Tools.Stages;

/// <summary>
/// Decides, for each language, whether a cutscene needs its own picture, only its own
/// sound, or nothing at all — and produces whichever it is.
/// </summary>
public sealed partial class LocalizationVideoStage
{
    private readonly FfmpegTools _tools;
    private readonly Action<string> _log;

    /// <summary>Decoded-stream hashes, so a baseline movie is decoded once for all languages.</summary>
    private readonly Dictionary<string, string?> _hashes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates the stage.</summary>
    /// <param name="tools">Located FFmpeg toolchain.</param>
    /// <param name="log">Where progress is written.</param>
    public LocalizationVideoStage(FfmpegTools tools, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(log);
        _tools = tools;
        _log = log;
    }

    /// <summary>Produces one language's soundtracks and re-cuts.</summary>
    /// <param name="media">
    /// Where this language's own <c>.bik</c> and <c>.avi</c> files are: the <c>Data</c>
    /// directory of its release, or the directory a dump left them in.
    /// </param>
    /// <param name="baseline">
    /// The same, for the release the shared pictures were imported from. Searched in order,
    /// so the installed game can come first and a dumped baseline second.
    /// </param>
    /// <param name="workspace">The content workspace root.</param>
    /// <param name="language">Which language.</param>
    /// <param name="force">Produce every track again rather than keeping what is there.</param>
    /// <param name="dryRun">Report what would happen and write nothing.</param>
    /// <returns>What became of each of the language's movies.</returns>
    public IReadOnlyList<LocalizationMovieEntry> Run(
        string media,
        IReadOnlyList<string> baseline,
        string workspace,
        GameLanguage language,
        bool force,
        bool dryRun)
    {
        ArgumentNullException.ThrowIfNull(language);
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspace);

        if (media.Length == 0 || !Directory.Exists(media))
        {
            return [];
        }

        List<FileInfo> movies = [.. new DirectoryInfo(media)
            .EnumerateFiles("*", SearchOption.TopDirectoryOnly)
            .Where(f => LocaleSource.IsMovie(f.Name))
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)];

        if (movies.Count == 0)
        {
            return [];
        }

        // Where the shared pictures are. The enhanced set is what ships; build/video is
        // what the import writes before anybody has chosen which of them to keep, and is
        // the fallback so this works in a tree where the enhanced set is not filled in yet.
        string shared = Path.Combine(workspace, "enhanced", "video");

        if (!Directory.Exists(shared))
        {
            shared = Path.Combine(workspace, "build", "video");
        }

        string root = Path.Combine(workspace, "enhanced", "localized", language.FileCode);
        string sounds = Path.Combine(root, RebarnFormat.DirectoryOf(RebarnKind.MovieAudio));
        string recuts = Path.Combine(root, RebarnFormat.DirectoryOf(RebarnKind.Video));

        List<LocalizationMovieEntry> entries = [];

        foreach (FileInfo movie in movies)
        {
            entries.Add(One(movie, baseline, shared, sounds, recuts, language, force, dryRun));
        }

        // Anything a previous run wrote that this one no longer claims. Taken away rather
        // than left: a stale soundtrack is read, and it would play the words of whichever
        // release the directory was last built from with nothing to say so.
        int stale = 0;

        if (!dryRun)
        {
            stale += Sweep(sounds, entries, LocalizationMovieDisposition.Soundtrack);
            stale += Sweep(recuts, entries, LocalizationMovieDisposition.Recut);
        }

        int soundtracks = entries.Count(e => e.Disposition == LocalizationMovieDisposition.Soundtrack);
        int recut = entries.Count(e => e.Disposition == LocalizationMovieDisposition.Recut);
        int shares = entries.Count(e => e.Disposition == LocalizationMovieDisposition.Shared);

        _log($"{language.FileCode}: {soundtracks} soundtrack(s), {recut} with a picture of "
            + $"their own, {shares} shared with the original"
            + (stale > 0 ? $", {stale} stale file(s) removed" : string.Empty));

        return entries;
    }

    private LocalizationMovieEntry One(
        FileInfo movie,
        IReadOnlyList<string> baseline,
        string shared,
        string sounds,
        string recuts,
        GameLanguage language,
        bool force,
        bool dryRun)
    {
        string name = Path.GetFileNameWithoutExtension(movie.Name).ToUpperInvariant();

        // The release the shared picture was imported from. Without it there is nothing to
        // compare against and no way to tell a dub from a re-cut.
        string? original = Find(baseline, name);

        // And the import's own output, because a soundtrack has to be laid over something.
        bool imported = Directory.Exists(shared) && Directory.EnumerateFiles(shared).Any(
            f => Path.GetFileNameWithoutExtension(f).Equals(name, StringComparison.OrdinalIgnoreCase));

        if (original is null || !imported)
        {
            // Reported rather than fixed. Importing this language's movie as the shared one
            // would make the whole game's copy of that cutscene French, which is a decision
            // for whoever is running the pipeline and not for this.
            _log($"    {name}: no shared cut to compare against");

            return new LocalizationMovieEntry
            {
                Name = name,
                Disposition = LocalizationMovieDisposition.Unmatched,
            };
        }

        string? mine = Hash(movie.FullName, sound: false);
        string? theirs = Hash(original, sound: false);

        if (mine is null || theirs is null)
        {
            _log($"    {name}: will not decode");

            return new LocalizationMovieEntry
            {
                Name = name,
                Disposition = LocalizationMovieDisposition.Failed,
                Error = "the picture would not decode",
            };
        }

        double seconds = Duration(movie.FullName);
        double sharedSeconds = Duration(original);

        if (!string.Equals(mine, theirs, StringComparison.Ordinal))
        {
            // A different edit, or the same edit with different words painted into it. The
            // two are not worth telling apart: either way this language needs its own
            // picture, and the length says which it was for anybody reading the report.
            return Recut(movie, name, recuts, language, seconds, sharedSeconds, force, dryRun);
        }

        // The same pixels. Now: did anybody re-record the words?
        string? myVoice = Hash(movie.FullName, sound: true);
        string? theirVoice = Hash(original, sound: true);

        if (myVoice is null)
        {
            // A silent movie has nothing to localise. Every Sidney scan is one.
            return new LocalizationMovieEntry
            {
                Name = name,
                Disposition = LocalizationMovieDisposition.Shared,
                Seconds = seconds,
                SharedSeconds = sharedSeconds,
            };
        }

        if (string.Equals(myVoice, theirVoice, StringComparison.Ordinal))
        {
            // This release did not dub it. Spanish did not dub eleven of its cutscenes, and
            // shipping a byte-identical copy of the English soundtrack for each would be
            // twenty-five megabytes saying nothing.
            return new LocalizationMovieEntry
            {
                Name = name,
                Disposition = LocalizationMovieDisposition.Shared,
                Seconds = seconds,
                SharedSeconds = sharedSeconds,
            };
        }

        return Soundtrack(movie, name, sounds, seconds, sharedSeconds, force, dryRun);
    }

    /// <summary>
    /// A hash of everything one of a movie's streams decodes to.
    /// </summary>
    /// <param name="path">The movie.</param>
    /// <param name="sound">True for the soundtrack, false for the picture.</param>
    /// <returns>The hash, or null when there is no such stream or it will not decode.</returns>
    private string? Hash(string path, bool sound)
    {
        string key = (sound ? "a:" : "v:") + path;

        if (_hashes.TryGetValue(key, out string? known))
        {
            return known;
        }

        string[] arguments = sound
            ? ["-v", "error", "-nostdin", "-i", path, "-vn", "-map", "0:a:0",
               "-c:a", "pcm_s16le", "-ar", "22050", "-ac", "2", "-f", "md5", "-"]
            : ["-v", "error", "-nostdin", "-i", path, "-an", "-map", "0:v:0",
               "-c:v", "rawvideo", "-pix_fmt", "rgb24", "-f", "md5", "-"];

        ProcessResult result = _tools.RunFfmpeg(arguments);

        string? hash = result.Succeeded && Digest().Match(result.StandardOutput) is { Success: true } m
            ? m.Groups[1].Value
            : null;

        _hashes[key] = hash;
        return hash;
    }

    /// <summary>How long a movie runs, for the report rather than for the decision.</summary>
    private double Duration(string path)
    {
        using System.Text.Json.JsonDocument? probe = _tools.Probe(path, out _);

        return probe is null ? 0 : MediaProbe.FromJson(probe)?.DurationSeconds ?? 0;
    }

    /// <summary>The baseline's copy of a movie, searched in the order given.</summary>
    private static string? Find(IReadOnlyList<string> directories, string name)
    {
        foreach (string directory in directories)
        {
            if (directory.Length == 0 || !Directory.Exists(directory))
            {
                continue;
            }

            string? found = Directory.EnumerateFiles(directory).FirstOrDefault(
                f => LocaleSource.IsMovie(f) &&
                     Path.GetFileNameWithoutExtension(f)
                         .Equals(name, StringComparison.OrdinalIgnoreCase));

            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>Takes away what a previous run left and this one does not claim.</summary>
    private static int Sweep(
        string directory,
        IReadOnlyList<LocalizationMovieEntry> entries,
        LocalizationMovieDisposition kept)
    {
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        HashSet<string> wanted = new(
            entries.Where(e => e.Disposition == kept).Select(e => e.Name),
            StringComparer.OrdinalIgnoreCase);

        int removed = 0;

        foreach (string file in Directory.EnumerateFiles(directory))
        {
            if (!wanted.Contains(Path.GetFileNameWithoutExtension(file)))
            {
                File.Delete(file);
                removed++;
            }
        }

        return removed;
    }

    /// <summary>Copies one language's words out from under the shared picture.</summary>
    private LocalizationMovieEntry Soundtrack(
        FileInfo movie,
        string name,
        string directory,
        double seconds,
        double sharedSeconds,
        bool force,
        bool dryRun)
    {
        string output = Path.Combine(directory, name + ".m4a");

        var entry = new LocalizationMovieEntry
        {
            Name = name,
            Disposition = LocalizationMovieDisposition.Soundtrack,
            Seconds = seconds,
            SharedSeconds = sharedSeconds,
            Output = $"movie-audio/{name}.m4a",
        };

        if (dryRun)
        {
            _log($"    {name}: would write {Path.GetFileName(output)}");
            return entry with { Output = null };
        }

        Directory.CreateDirectory(directory);

        if (!force && File.Exists(output) &&
            new FileInfo(output).LastWriteTimeUtc >= movie.LastWriteTimeUtc)
        {
            return entry;
        }

        ProcessResult result = _tools.RunFfmpeg([
            "-y", "-hide_banner", "-nostdin", "-loglevel", "error",
            "-i", movie.FullName,
            "-vn", "-map", "0:a:0",
            "-c:a", "aac", "-b:a", "192k", "-ar", "48000",
            "-movflags", "+faststart",
            output,
        ]);

        if (!result.Succeeded)
        {
            _log($"    {name}: SOUNDTRACK FAILED");

            return new LocalizationMovieEntry
            {
                Name = name,
                Disposition = LocalizationMovieDisposition.Failed,
                Seconds = seconds,
                SharedSeconds = sharedSeconds,
                Error = Truncate(result.StandardError, 2000),
            };
        }

        _log(string.Create(CultureInfo.InvariantCulture,
            $"    {name}: dubbed, {seconds:F1}s, "
            + $"{new FileInfo(output).Length / 1_000_000.0:F1} MB"));

        return entry;
    }

    /// <summary>Imports a whole movie for a language whose picture is its own.</summary>
    private LocalizationMovieEntry Recut(
        FileInfo movie,
        string name,
        string directory,
        GameLanguage language,
        double seconds,
        double sharedSeconds,
        bool force,
        bool dryRun)
    {
        string output = Path.Combine(directory, name + ".mp4");

        string why = Math.Abs(seconds - sharedSeconds) > 0.05
            ? string.Create(
                CultureInfo.InvariantCulture,
                $" ({seconds:F1}s against {sharedSeconds:F1}s, a different edit)")
            : " (the same edit, with different words in the frame)";

        _log($"    {name}: the {language.Name} picture is not the shared one{why}");

        var entry = new LocalizationMovieEntry
        {
            Name = name,
            Disposition = LocalizationMovieDisposition.Recut,
            Seconds = seconds,
            SharedSeconds = sharedSeconds,
            Output = $"video/{name}.mp4",
        };

        if (dryRun)
        {
            return entry with { Output = null };
        }

        Directory.CreateDirectory(directory);

        if (!force && File.Exists(output) &&
            new FileInfo(output).LastWriteTimeUtc >= movie.LastWriteTimeUtc)
        {
            return entry;
        }

        using System.Text.Json.JsonDocument? probeJson = _tools.Probe(movie.FullName, out _);
        MediaProbe? probe = probeJson is null ? null : MediaProbe.FromJson(probeJson);

        if (probe is null)
        {
            return new LocalizationMovieEntry
            {
                Name = name,
                Disposition = LocalizationMovieDisposition.Failed,
                Seconds = seconds,
                SharedSeconds = sharedSeconds,
                Error = "the movie would not probe",
            };
        }

        // The import's own recipe, so a movie that is this language's own is encoded exactly
        // as the shared pictures were and the two are comparable.
        ProcessResult result = _tools.RunFfmpeg(
            [.. VideoImportStage.BuildArguments(movie.FullName, probe), output]);

        if (!result.Succeeded)
        {
            _log($"    {name}: IMPORT FAILED");

            return new LocalizationMovieEntry
            {
                Name = name,
                Disposition = LocalizationMovieDisposition.Failed,
                Seconds = seconds,
                SharedSeconds = sharedSeconds,
                Error = Truncate(result.StandardError, 2000),
            };
        }

        return entry;
    }

    [GeneratedRegex(@"MD5=([0-9a-f]{32})", RegexOptions.IgnoreCase)]
    private static partial Regex Digest();

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value.Trim() : value[..max].Trim();
}
