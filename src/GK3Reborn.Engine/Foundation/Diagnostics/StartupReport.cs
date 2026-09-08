using System.Globalization;
using System.Runtime.InteropServices;
using System.Security;

namespace GK3Reborn.Foundation.Diagnostics;

/// <summary>
/// What the game found where, said once at startup.
/// </summary>
public static class StartupReport
{
    /// <summary>
    /// Reports the machine, the process and the native payload.
    /// </summary>
    /// <param name="nativeLibraryRoot">
    /// The <c>libs/&lt;rid&gt;</c> directory the host resolves native libraries from, or
    /// null when no resolver was installed.
    /// </param>
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
