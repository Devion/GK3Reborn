using System.Globalization;
using System.Runtime.InteropServices;
using System.Security;

namespace GK3Reborn.Foundation.Diagnostics;

/// <summary>
/// What the game found where, said once at startup.
/// </summary>
/// <remarks>
/// <para>
/// Almost every report of "it will not start" is a path: content that was never copied,
/// a native payload that was not unpacked, a directory the player cannot write to. Those
/// are cheap to diagnose and expensive to guess at, so each one is named in the log
/// whether it was found or not - a log that only mentions what went wrong cannot be told
/// apart from a log that stopped before it got there.
/// </para>
/// <para>
/// The case check exists because Windows is the platform this is developed on and Linux
/// is not. <c>Data</c> and <c>data</c> are the same directory on one and two different
/// ones on the other, and a player who unpacked an archive that spelled it the other way
/// gets a missing-content message pointing at a directory they can plainly see. Saying
/// "there is a directory here that differs only in case" turns that into a fix.
/// </para>
/// </remarks>
public static class StartupReport
{
    /// <summary>
    /// Reports the machine, the process and the native payload.
    /// </summary>
    /// <param name="nativeLibraryRoot">
    /// The <c>libs/&lt;rid&gt;</c> directory the host resolves native libraries from, or
    /// null when no resolver was installed.
    /// </param>
    /// <remarks>
    /// The environment goes to the file alone; a player has not asked which build of the
    /// runtime they have. The two lines that reach the console are the ones they may need
    /// to act on: where the log is, and whether the native libraries are missing - which on
    /// Linux and macOS is the difference between a window opening and a silent exit,
    /// because GLFW, OpenAL and shaderc are loaded before anything can be drawn.
    /// </remarks>
    public static void Begin(string? nativeLibraryRoot)
    {
        Log.Info(Log.FilePath is { } file
            ? $"Log: {file}"
            : $"Log: none, this run is not being written down ({Log.Unavailable ?? "no writable directory"})");

        Log.Detail($"base directory {AppContext.BaseDirectory}");
        Log.Detail($"process        {System.Environment.ProcessPath ?? "(unknown)"}");
        Log.Detail($"working dir    {System.Environment.CurrentDirectory}");
        Log.Detail($"user data      {InstallPaths.UserData}");
        Log.Detail($"writable root  {Safely(() => InstallPaths.WritableRoot)}");
        Log.Detail($"app bundle     {InstallPaths.BundleResources ?? "(not in one)"}");

        NativeLibraries(nativeLibraryRoot);
    }

    /// <summary>
    /// Reports a directory the game cannot do without.
    /// </summary>
    /// <param name="what">What it holds, as a player would name it.</param>
    /// <param name="path">Where it was expected.</param>
    /// <param name="remedy">What to do about it being missing.</param>
    /// <returns>True when the directory is there.</returns>
    public static bool Needed(string what, string? path, string? remedy = null)
    {
        if (Exists(path))
        {
            Log.Detail($"{what}: {path}");

            return true;
        }

        Log.Error($"{what}: there is no directory at {path ?? "(nowhere - no path was worked out)"}.");
        Explain(path);

        if (remedy is { Length: > 0 })
        {
            Log.Error(remedy);
        }

        return false;
    }

    /// <summary>
    /// Reports a directory the game can start without.
    /// </summary>
    /// <param name="what">What it holds, as a player would name it.</param>
    /// <param name="path">Where it was expected, or null when nobody asked for it.</param>
    /// <param name="note">What its absence costs, said on the console when it is absent.</param>
    /// <returns>True when the directory is there.</returns>
    /// <remarks>
    /// Absent is not a failure here, so nothing reaches the console unless <paramref
    /// name="note"/> says the player loses something by it. The file records both cases:
    /// "the enhanced textures were not where the run expected them" is exactly the kind of
    /// thing that otherwise gets discovered by looking at screenshots a week later.
    /// </remarks>
    public static bool Optional(string what, string? path, string? note = null)
    {
        if (path is not { Length: > 0 })
        {
            Log.Detail($"{what}: not asked for");

            return false;
        }

        if (Exists(path))
        {
            Log.Detail($"{what}: {path}");

            return true;
        }

        Log.Detail($"{what}: there is no directory at {path}");
        Explain(path);

        if (note is { Length: > 0 })
        {
            Log.Warning($"{what}: there is no directory at {path}. {note}");
        }

        return false;
    }

    /// <summary>
    /// Reports a directory the game means to write to, and whether it actually can.
    /// </summary>
    /// <param name="what">What goes in it, as a player would name it.</param>
    /// <param name="path">The directory, already chosen by <see cref="InstallPaths"/>.</param>
    /// <returns>True when a file could be written there and removed again.</returns>
    /// <remarks>
    /// A save that cannot be written is discovered when somebody tries to save, which is
    /// after they have played for an hour. The probe costs one file and answers it now.
    /// This is a warning rather than an error everywhere: a read-only install still plays,
    /// it just cannot remember anything, and saying so is better than refusing to run.
    /// </remarks>
    public static bool Writable(string what, string path)
    {
        if (InstallPaths.CanWrite(path))
        {
            Log.Detail($"{what}: {path} (writable)");

            return true;
        }

        Log.Warning($"{what}: {path} cannot be written to.");
        Explain(path);

        Log.Warning(OperatingSystem.IsWindows()
            ? "Check the folder's permissions, or move the game out of Program Files."
            : "Check the directory's owner and permissions - an install unpacked as one "
              + "user and run as another is the usual cause. HOME, and XDG_CONFIG_HOME on "
              + $"Linux, are what decide {InstallPaths.UserData}.");

        return false;
    }

    /// <summary>
    /// Records the places something was looked for, and which one answered.
    /// </summary>
    /// <param name="what">What was being looked for.</param>
    /// <param name="candidates">Every path tried, in the order they were tried.</param>
    /// <param name="chosen">The one that answered, or null when none did.</param>
    /// <remarks>
    /// File only, and always - including the runs that succeed. The question this answers
    /// is "why is it reading that copy and not the one I just built", which is asked about
    /// a run that worked, and there is no way to answer it after the fact from a log that
    /// only recorded the winner.
    /// </remarks>
    public static void Searched(string what, IEnumerable<string> candidates, string? chosen)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        Log.Detail($"{what}: looked in");

        foreach (string candidate in candidates)
        {
            if (candidate is not { Length: > 0 })
            {
                continue;
            }

            bool taken = chosen is not null &&
                string.Equals(candidate, chosen, StringComparison.Ordinal);

            Log.Detail(string.Create(
                CultureInfo.InvariantCulture,
                $"  {(taken ? '*' : ' ')} {candidate} {(Exists(candidate) ? "exists" : "missing")}"));
        }

        if (chosen is null)
        {
            Log.Detail($"{what}: nothing found");
        }
    }

    /// <summary>Reports the native payload, which is what a broken Unix install lacks.</summary>
    /// <param name="root">The <c>libs/&lt;rid&gt;</c> directory, or null.</param>
    /// <remarks>
    /// <para>
    /// A published game keeps GLFW, OpenAL and shaderc in <c>libs/&lt;rid&gt;</c>; a build
    /// straight out of the compiler has not been through the publish that moves them there
    /// and keeps them in the stock <c>runtimes/&lt;rid&gt;/native</c>, or flat beside the
    /// executable. All three are fine. Only the fourth case - none of them - is a problem,
    /// and it is the one worth being loud about: on Linux and macOS it is the commonest way
    /// a copied install fails, and the process dies inside the first P/Invoke with a
    /// <c>DllNotFoundException</c> naming a library the player has never heard of.
    /// </para>
    /// <para>
    /// Which is why the empty case is not treated as the missing case. Warning about a
    /// layout that works would teach whoever reads these logs to skip this line.
    /// </para>
    /// </remarks>
    private static void NativeLibraries(string? root)
    {
        if (root is not { Length: > 0 })
        {
            Log.Warning("Native libraries: no resolver was installed, so the system loader "
                + "will be asked for GLFW, OpenAL and shaderc.");

            return;
        }

        string[] payload = Files(root);

        if (payload.Length > 0)
        {
            Log.Detail(string.Create(
                CultureInfo.InvariantCulture,
                $"Native libraries: {root} ({payload.Length} files)"));

            foreach (string file in payload)
            {
                Log.Detail($"  {Path.GetFileName(file)}");
            }

            return;
        }

        string stock = Path.Combine(
            AppContext.BaseDirectory, "runtimes", RuntimeInformation.RuntimeIdentifier, "native");

        if (Files(stock) is { Length: > 0 } stocked)
        {
            Log.Detail(string.Create(
                CultureInfo.InvariantCulture,
                $"Native libraries: nothing under {root}; the stock layout at {stock} has "
                    + $"{stocked.Length} files and will be used instead"));

            return;
        }

        if (Beside() is { Length: > 0 } loose)
        {
            Log.Detail(string.Create(
                CultureInfo.InvariantCulture,
                $"Native libraries: nothing under {root}; {loose.Length} {Extension} files "
                    + $"sit beside the executable and will be used instead"));

            return;
        }

        Log.Error($"Native libraries: nothing at {root}, and none beside the executable.");
        Explain(root);

        Log.Error("That directory holds the GLFW, OpenAL and shaderc builds for "
            + $"{RuntimeInformation.RuntimeIdentifier}. Without them the game can only "
            + "start if the system happens to have all three installed.");

        Log.Error("Run build/fetch-native.sh, or copy libs/ from a published build.");
    }

    /// <summary>What a native library is called on this platform.</summary>
    private static string Extension =>
        OperatingSystem.IsWindows() ? ".dll" : OperatingSystem.IsMacOS() ? ".dylib" : ".so";

    /// <summary>Native libraries lying flat beside the executable, as a RID publish leaves them.</summary>
    private static string[] Beside() => Files(AppContext.BaseDirectory, "*" + Extension);

    /// <summary>The files in a directory, or none when there is no directory to read.</summary>
    private static string[] Files(string directory, string pattern = "*") =>
        Exists(directory)
            ? Safely(directory, d => Directory.GetFiles(d, pattern, SearchOption.TopDirectoryOnly)) ?? []
            : [];

    /// <summary>Says as much about a path that is not there as the filesystem knows.</summary>
    /// <param name="path">The path that was expected.</param>
    /// <remarks>
    /// Two things are worth saying and neither is obvious from the path alone: how far up
    /// the tree exists at all, which separates "wrong directory" from "nothing was
    /// installed", and whether the missing name is sitting right there under a different
    /// case, which is the one failure a Windows build never reproduces.
    /// </remarks>
    private static void Explain(string? path)
    {
        if (path is not { Length: > 0 })
        {
            return;
        }

        if (Nearest(path) is { } nearest)
        {
            Log.Detail($"  the deepest part of that path which does exist is {nearest}");
        }
        else
        {
            Log.Detail("  no part of that path exists");
        }

        if (OtherCase(path) is { } other)
        {
            Log.Error($"There is a directory at {other}, which differs only in case. "
                + "Linux and macOS treat those as different directories; rename it or "
                + "point the game at it.");
        }
    }

    /// <summary>
    /// The deepest ancestor of a path that exists, or null when none does.
    /// </summary>
    /// <param name="path">The path that was expected.</param>
    /// <returns>An existing directory on the way to it, or null.</returns>
    /// <remarks>
    /// Public so it can be checked without arranging a broken install, on the same grounds
    /// as <see cref="InstallPaths.FindBundleResources"/>: it is a question about a path,
    /// and a question about a path has an answer on any machine.
    /// </remarks>
    public static string? Nearest(string path)
    {
        try
        {
            for (string? walk = Path.GetFullPath(path); walk is not null; walk = Path.GetDirectoryName(walk))
            {
                if (Directory.Exists(walk))
                {
                    return walk;
                }
            }
        }
        catch (Exception e) when (Unreadable(e))
        {
            return null;
        }

        return null;
    }

    /// <summary>
    /// A sibling directory whose name differs from the wanted one only in case.
    /// </summary>
    /// <param name="path">The directory that was expected and is not there.</param>
    /// <returns>The near miss, or null when there is none.</returns>
    /// <remarks>
    /// The failure this catches cannot happen on the machine this is developed on - NTFS
    /// answers to either spelling - so it is public for the same reason the bundle check
    /// is: being able to check it from Windows is the difference between it being tested
    /// and being hoped for.
    /// </remarks>
    public static string? OtherCase(string path)
    {
        try
        {
            string full = Path.GetFullPath(path);
            string? parent = Path.GetDirectoryName(full);
            string leaf = Path.GetFileName(full);

            if (parent is null || leaf.Length == 0 || !Directory.Exists(parent))
            {
                return null;
            }

            foreach (string sibling in Directory.EnumerateDirectories(parent))
            {
                string name = Path.GetFileName(sibling);

                if (string.Equals(name, leaf, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(name, leaf, StringComparison.Ordinal))
                {
                    return sibling;
                }
            }
        }
        catch (Exception e) when (Unreadable(e))
        {
            return null;
        }

        return null;
    }

    /// <summary>Whether a directory is there, without ever throwing about it.</summary>
    private static bool Exists(string? path)
    {
        if (path is not { Length: > 0 })
        {
            return false;
        }

        try
        {
            return Directory.Exists(path);
        }
        catch (Exception e) when (Unreadable(e))
        {
            return false;
        }
    }

    private static string Safely(Func<string> read)
    {
        try
        {
            return read();
        }
        catch (Exception e) when (Unreadable(e))
        {
            return $"(unavailable: {e.Message})";
        }
    }

    private static T? Safely<T>(string argument, Func<string, T> read)
        where T : class
    {
        try
        {
            return read(argument);
        }
        catch (Exception e) when (Unreadable(e))
        {
            Log.Detail($"  could not be read: {e.Message}");

            return null;
        }
    }

    /// <summary>The failures a filesystem question can raise and still have an answer.</summary>
    private static bool Unreadable(Exception e) =>
        e is IOException or UnauthorizedAccessException or NotSupportedException
            or ArgumentException or SecurityException;
}
