using System.Security;

namespace GK3Reborn.Foundation;

/// <summary>
/// Where the game may read from and where it may write, given where it was installed.
/// </summary>
public static class InstallPaths
{
    private const string ApplicationName = "GK3Reborn";

    private static readonly string? _bundleResources = FindBundleResources(AppContext.BaseDirectory);

    /// <summary>
    /// The per-user directory for anything the game writes that is not a saved game.
    /// </summary>
    public static string UserData { get; } = OperatingSystem.IsMacOS()
        ? Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.UserProfile,
                Environment.SpecialFolderOption.DoNotVerify),
            "Library",
            "Application Support",
            ApplicationName)
        : Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.ApplicationData,
                Environment.SpecialFolderOption.DoNotVerify),
            ApplicationName);

    /// <summary>
    /// The <c>Contents/Resources</c> of the <c>.app</c> bundle the game is running from,
    /// or null when it is not running from one.
    /// </summary>
    public static string? BundleResources => _bundleResources;

    /// <summary>Whether the executable is inside a macOS application bundle.</summary>
    public static bool InAppBundle => _bundleResources is not null;

    /// <summary>
    /// The directory a loose file the game writes belongs in: the executable's own when
    /// that can be written to, and <see cref="UserData"/> when it cannot.
    /// </summary>
    public static string WritableRoot
    {
        get
        {
            if (CanWrite(AppContext.BaseDirectory))
            {
                return AppContext.BaseDirectory;
            }

            Directory.CreateDirectory(UserData);

            return UserData;
        }
    }

    /// <summary>
    /// A directory the game may write to, named beside the executable when that is
    /// possible and under <see cref="UserData"/> when it is not.
    /// </summary>
    /// <param name="name">Directory name, such as <c>shader-cache</c>.</param>
    /// <returns>An absolute path to a directory that exists and can be written to.</returns>
    public static string WritableDirectory(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        string beside = Path.Combine(AppContext.BaseDirectory, name);

        if (CanWrite(beside))
        {
            return beside;
        }

        string fallback = Path.Combine(UserData, name);

        // Probed rather than merely created, so that a caller is told the same thing about
        // both candidates. If neither can be written to there is nothing better to return,
        // and the caller's own write is where that has to surface.
        CanWrite(fallback);

        return fallback;
    }

    /// <summary>Whether a directory can be created and written to.</summary>
    /// <param name="directory">Directory to probe. Created if it does not exist.</param>
    /// <returns>True when a file was written there and removed again.</returns>
    public static bool CanWrite(string? directory)
    {
        if (string.IsNullOrEmpty(directory))
        {
            return false;
        }

        try
        {
            Directory.CreateDirectory(directory);

            string probe = Path.Combine(directory, ".writable");

            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);

            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or
                                      NotSupportedException or ArgumentException or
                                      SecurityException)
        {
            return false;
        }
    }

    /// <summary>
    /// Recognises <c>&lt;name&gt;.app/Contents/MacOS</c> and returns the sibling
    /// <c>Resources</c>.
    /// </summary>
    /// <param name="baseDirectory">The executable's own directory.</param>
    /// <returns>The bundle's resources directory, or null.</returns>
    public static string? FindBundleResources(string baseDirectory)
    {
        if (string.IsNullOrEmpty(baseDirectory))
        {
            return null;
        }

        var executable = new DirectoryInfo(baseDirectory);

        if (!string.Equals(executable.Name, "MacOS", StringComparison.Ordinal))
        {
            return null;
        }

        DirectoryInfo? contents = executable.Parent;

        if (contents is null || !string.Equals(contents.Name, "Contents", StringComparison.Ordinal))
        {
            return null;
        }

        DirectoryInfo? bundle = contents.Parent;

        return bundle is not null && bundle.Name.EndsWith(".app", StringComparison.Ordinal)
            ? Path.Combine(contents.FullName, "Resources")
            : null;
    }
}
