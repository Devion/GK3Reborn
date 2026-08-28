using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;

namespace GK3Reborn.Foundation.Diagnostics;

/// <summary>
/// Everything the game says about itself: on the console, and in <c>log.txt</c>.
/// </summary>
/// <remarks>
/// <para>
/// A console line is gone the moment the window closes, and on Linux and macOS a game
/// started from a desktop launcher or from Finder has no console at all - stdout goes
/// nowhere a player can reach. Somebody whose game will not start therefore has nothing
/// to send back, and every report of it reduces to a guess. A file fixes that: it is
/// written whether or not anybody is watching, it survives the crash that produced it,
/// and it can be attached to a bug report.
/// </para>
/// <para>
/// The console text is deliberately unchanged by going through here - same wording, same
/// stream, same order, no timestamps or severity tags in front of it. The file is the one
/// that carries the machinery, because the file is the one being read after the fact by
/// somebody who was not there. <see cref="Detail"/> is the other half of that split: the
/// candidates a search walked through and the sizes of things belong in the file and
/// nowhere near a player's screen.
/// </para>
/// <para>
/// One log per process, so this is static: everything that has something to say already
/// reaches <c>Console</c> from wherever it is, and threading a logger through the engine
/// to replace a call that was already global would buy nothing. <see cref="LogFile"/> is
/// the part with decisions in it, and that is an ordinary object.
/// </para>
/// </remarks>
public static class Log
{
    private static readonly Lock _gate = new();

    private static LogFile? _file;
    private static string? _unavailable;
    private static bool _opened;

    /// <summary>How severe a line is, as recorded in the file.</summary>
    private enum Level
    {
        Detail,
        Info,
        Warning,
        Error,
    }

    /// <summary>The log file being written, or null when there is none.</summary>
    public static string? FilePath
    {
        get
        {
            lock (_gate)
            {
                return _file?.Path;
            }
        }
    }

    /// <summary>Why there is no log file, or null when there is one.</summary>
    public static string? Unavailable
    {
        get
        {
            lock (_gate)
            {
                return _unavailable;
            }
        }
    }

    /// <summary>
    /// Opens the log file. Safe to call more than once; only the first call does anything.
    /// </summary>
    /// <param name="directory">
    /// Where to write it, or null for <see cref="InstallPaths.WritableRoot"/> - beside the
    /// executable on an ordinary install, and in the user's own directory when the install
    /// is read-only, which is what a macOS <c>.app</c> in <c>/Applications</c> is.
    /// </param>
    /// <remarks>
    /// Failing to open the file is not a failure to start the game. The reason is kept in
    /// <see cref="Unavailable"/> so that whoever reports the environment can say it out
    /// loud once, and everything afterwards goes to the console alone.
    /// </remarks>
    public static void Open(string? directory = null)
    {
        lock (_gate)
        {
            if (_opened)
            {
                return;
            }

            _opened = true;

            string root;

            try
            {
                root = directory ?? InstallPaths.WritableRoot;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                          or NotSupportedException or ArgumentException)
            {
                _unavailable = e.Message;

                return;
            }

            _file = LogFile.Open(root, out _unavailable);

            if (_file is null)
            {
                return;
            }

            AppDomain.CurrentDomain.ProcessExit += static (_, _) => Close();

            Header();
        }
    }

    /// <summary>Says something, on the console and in the file.</summary>
    /// <param name="message">The line, as the player should read it.</param>
    public static void Info(string message) => Emit(Level.Info, message, error: false);

    /// <summary>Leaves a blank line, as a paragraph break.</summary>
    public static void Info() => Emit(Level.Info, string.Empty, error: false);

    /// <summary>
    /// Says something without ending the line on the console.
    /// </summary>
    /// <param name="text">Text that already carries whatever newlines it wants.</param>
    /// <remarks>
    /// For blocks that are assembled whole, such as the graphics survey.
    /// </remarks>
    public static void Write(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        lock (_gate)
        {
            Console.Out.Write(text);
            Record(Level.Info, text);
        }
    }

    /// <summary>Says something is off, on the console's error stream and in the file.</summary>
    /// <param name="message">The line, as the player should read it.</param>
    public static void Warning(string message) => Emit(Level.Warning, message, error: true);

    /// <summary>Says something failed, on the console's error stream and in the file.</summary>
    /// <param name="message">The line, as the player should read it.</param>
    public static void Error(string message) => Emit(Level.Error, message, error: true);

    /// <summary>Leaves a blank line on the error stream, as a paragraph break.</summary>
    public static void Error() => Emit(Level.Error, string.Empty, error: true);

    /// <summary>Says what a diagnostic says, at the severity it carries.</summary>
    /// <param name="diagnostic">The diagnostic.</param>
    /// <remarks>
    /// All three severities go to the error stream, which is where diagnostics have always
    /// gone and where anybody redirecting them expects them. The severity decides how the
    /// line is tagged in the file, which is what makes a log greppable for the errors in
    /// among a scene's ordinary complaints about missing assets.
    /// </remarks>
    public static void Report(Diagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);

        Emit(
            diagnostic.Severity switch
            {
                DiagnosticSeverity.Error => Level.Error,
                DiagnosticSeverity.Warning => Level.Warning,
                _ => Level.Info,
            },
            diagnostic.ToString(),
            error: true);
    }

    /// <summary>
    /// Records something in the file that the player has no reason to read.
    /// </summary>
    /// <param name="message">The line, as somebody debugging should read it.</param>
    /// <remarks>
    /// The directories a search looked in, the sizes of what it found, the order things
    /// happened in. Nothing here is a problem; all of it is what turns "the game will not
    /// start" into a diagnosis without another round trip to the person reporting it.
    /// </remarks>
    public static void Detail(string message)
    {
        ArgumentNullException.ThrowIfNull(message);

        lock (_gate)
        {
            Record(Level.Detail, message);
        }
    }

    /// <summary>Records an exception with its stack, and says so on the console.</summary>
    /// <param name="context">What was being done when it was thrown.</param>
    /// <param name="error">The exception.</param>
    public static void Exception(string context, Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);

        Emit(Level.Error, $"{context}: {error}", error: true);
    }

    /// <summary>Flushes and closes the file.</summary>
    /// <remarks>
    /// Every write is flushed already, so this is tidiness rather than safety. It runs on
    /// process exit, which a crash does not always reach - hence the flushing.
    /// </remarks>
    public static void Close()
    {
        lock (_gate)
        {
            _file?.Dispose();
            _file = null;
        }
    }

    /// <summary>Writes what a reader needs before the first line means anything.</summary>
    /// <remarks>Call under <c>_gate</c>.</remarks>
    private static void Header()
    {
        Assembly engine = typeof(Log).Assembly;

        string version = engine.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? engine.GetName().Version?.ToString() ?? "unknown";

        Record(Level.Info, $"GK3Reborn {version}");
        Record(Level.Info, $"started        {DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)}");
        Record(Level.Info, $"os             {RuntimeInformation.OSDescription}");
        Record(Level.Info, $"architecture   {RuntimeInformation.OSArchitecture} host, {RuntimeInformation.ProcessArchitecture} process");
        Record(Level.Info, $"runtime        {RuntimeInformation.FrameworkDescription}, RID {RuntimeInformation.RuntimeIdentifier}");
        Record(Level.Info, $"culture        {CultureInfo.CurrentCulture.Name}");
        Record(Level.Info, $"command line   {string.Join(' ', System.Environment.GetCommandLineArgs())}");
        Record(Level.Info, string.Empty);
    }

    /// <summary>Says one line, to the console and to the file.</summary>
    private static void Emit(Level level, string message, bool error)
    {
        ArgumentNullException.ThrowIfNull(message);

        lock (_gate)
        {
            if (error)
            {
                Console.Error.WriteLine(message);
            }
            else
            {
                Console.Out.WriteLine(message);
            }

            Record(level, message);
        }
    }

    /// <summary>Writes to the file, if there still is one.</summary>
    /// <remarks>Call under <c>_gate</c>.</remarks>
    private static void Record(Level level, string message)
    {
        if (_file is null)
        {
            return;
        }

        if (!_file.Write(Tag(level), message))
        {
            // The disk filled, the drive went away, the permission was revoked. The game
            // keeps running and keeps talking to the console; it simply stops being
            // written down, and says so if anybody asks.
            _unavailable = $"writing to {_file.Path} failed";
            _file = null;
        }
    }

    /// <summary>The severity column, padded so the message column lines up.</summary>
    private static string Tag(Level level) => level switch
    {
        Level.Detail => "detail",
        Level.Warning => "warn  ",
        Level.Error => "ERROR ",
        _ => "info  ",
    };
}
