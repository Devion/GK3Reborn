using System.Globalization;
using System.Security;
using System.Text;

namespace GK3Reborn.Foundation.Diagnostics;

/// <summary>
/// The file half of <see cref="Log"/>: where it goes, what it replaces, how a line reads.
/// </summary>
/// <remarks>
/// <para>
/// Separate from <see cref="Log"/> because everything here has a wrong answer worth
/// catching - a run that silently overwrote the crash it was opened to explain, two
/// processes interleaving into one file, a multi-line message that arrives as one
/// unreadable line - and none of it can be tested through a static that opens itself once
/// per process.
/// </para>
/// <para>
/// Nothing here throws. A game that cannot write a log is a game with no log, not a game
/// that will not start, and the caller learns why through <c>failure</c> rather than
/// through an exception it would have to handle at every call.
/// </para>
/// </remarks>
public sealed class LogFile : IDisposable
{
    /// <summary>The name of the file a run writes.</summary>
    public const string FileName = "log.txt";

    /// <summary>The name the previous run's file is kept under.</summary>
    /// <remarks>
    /// A player whose game crashes restarts it, and a restart that overwrote the log would
    /// destroy the evidence in the moment it was asked for. One generation back is enough
    /// to survive that reflex.
    /// </remarks>
    public const string PreviousFileName = "log.previous.txt";

    private readonly StreamWriter _writer;

    private LogFile(string path, StreamWriter writer)
    {
        Path = path;
        _writer = writer;
    }

    /// <summary>The file being written.</summary>
    public string Path { get; }

    /// <summary>
    /// Moves the last run's log aside and opens a new one.
    /// </summary>
    /// <param name="directory">Where to write. Created if it does not exist.</param>
    /// <param name="failure">Why there is no file, when none could be opened.</param>
    /// <returns>The open file, or null.</returns>
    /// <remarks>
    /// The fallback to a per-process name covers two copies of the game running at once,
    /// which is ordinary enough - a player comparing settings, a developer with a headless
    /// run beside a windowed one. Two processes interleaving lines into one file would
    /// produce something nobody can read, so the second one gets its own rather than
    /// spoiling the first one's.
    /// </remarks>
    public static LogFile? Open(string directory, out string? failure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        try
        {
            Directory.CreateDirectory(directory);

            Keep(
                System.IO.Path.Combine(directory, FileName),
                System.IO.Path.Combine(directory, PreviousFileName));
        }
        catch (Exception e) when (Expected(e))
        {
            failure = e.Message;

            return null;
        }

        foreach (string name in Names())
        {
            string path = System.IO.Path.Combine(directory, name);

            try
            {
                failure = null;

                return new LogFile(path, Create(path));
            }
            catch (IOException)
            {
                // Held by another process. Try the next name.
            }
            catch (Exception e) when (Expected(e))
            {
                failure = e.Message;

                return null;
            }
        }

        failure = $"every candidate name in {directory} is in use";

        return null;
    }

    /// <summary>Writes a message, one timestamped line per line of it.</summary>
    /// <param name="tag">The severity column, already padded.</param>
    /// <param name="message">The message, which may span lines.</param>
    /// <returns>True while the file is still being written.</returns>
    /// <remarks>
    /// A message is split rather than written whole because a timestamped line is the unit
    /// a log is read in: a grep for an error should not return a fragment of a paragraph
    /// whose first line carried the time.
    /// </remarks>
    public bool Write(string tag, string message)
    {
        ArgumentNullException.ThrowIfNull(message);

        try
        {
            string stamp = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);

            foreach (string line in Lines(message))
            {
                _writer.WriteLine($"{stamp}  {tag}  {line}");
            }

            return true;
        }
        catch (Exception e) when (Expected(e))
        {
            // A full disk, a removed drive, a revoked permission. Saying so is the caller's
            // business; all this can report is that there is no point asking again.
            return false;
        }
    }

    /// <summary>Splits a message into the lines it will be written as.</summary>
    /// <param name="message">The message.</param>
    /// <returns>One entry per line, with a single trailing newline dropped.</returns>
    public static string[] Lines(string message)
    {
        ArgumentNullException.ThrowIfNull(message);

        return message
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .TrimEnd('\n')
            .Split('\n');
    }

    /// <summary>Flushes and closes the file.</summary>
    public void Dispose()
    {
        try
        {
            _writer.Flush();
            _writer.Dispose();
        }
        catch (Exception e) when (Expected(e))
        {
            // Nothing can be done about a file that will not close, and throwing on the way
            // out of a process would turn a clean shutdown into a crash report.
        }
    }

    /// <summary>The names to try, in order.</summary>
    private static IEnumerable<string> Names()
    {
        yield return FileName;

        yield return string.Create(
            CultureInfo.InvariantCulture, $"log.{System.Environment.ProcessId}.txt");

        for (int nth = 2; nth <= 8; nth++)
        {
            yield return string.Create(
                CultureInfo.InvariantCulture, $"log.{System.Environment.ProcessId}-{nth}.txt");
        }
    }

    /// <summary>Moves the last run's log aside, if there is one.</summary>
    private static void Keep(string current, string previous)
    {
        if (!File.Exists(current))
        {
            return;
        }

        try
        {
            File.Move(current, previous, overwrite: true);
        }
        catch (Exception e) when (Expected(e))
        {
            // The old file is locked, or another process has it open. Losing one generation
            // of history is not worth refusing to log this run.
        }
    }

    /// <summary>Opens one file for writing, readable by anybody who wants to watch it.</summary>
    private static StreamWriter Create(string path)
    {
        // FileShare.Read so the file can be tailed, copied or attached to a report while
        // the game is still running. Not Write: refusing that is what makes a second
        // instance fall through to a name of its own instead of scribbling over this one.
        var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);

        // Flushed per write. Buffering would lose exactly the last few lines before a
        // crash, which are the only ones anybody wanted.
        return new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true,
        };
    }

    /// <summary>The failures that mean "no file", as opposed to a bug in this code.</summary>
    private static bool Expected(Exception e) =>
        e is IOException or UnauthorizedAccessException or NotSupportedException
            or ArgumentException or SecurityException or ObjectDisposedException;
}
