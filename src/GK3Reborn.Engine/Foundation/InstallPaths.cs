using System.Security;

namespace GK3Reborn.Foundation;

/// <summary>
/// Where the game may read from and where it may write, given where it was installed.
/// </summary>
/// <remarks>
/// <para>
/// Everything the game reaches for is derived from <see cref="AppContext.BaseDirectory"/>
/// - content, saves, the shader cache. That works on Windows and Linux, where a game is a
/// directory somebody unpacked and owns. It does not work on macOS, where an installed
/// application is a signed, read-only <c>.app</c> bundle in <c>/Applications</c>: writing
/// into it either fails outright or breaks the signature, and the executable does not even
/// sit at the root of the tree but three levels down in <c>Contents/MacOS</c>.
/// </para>
/// <para>
/// So there are two roots rather than one. <see cref="BundleResources"/> is the read-only
/// half - the bundle's own <c>Contents/Resources</c>, where a packaged build puts whatever
/// it ships. <see cref="UserData"/> is the writable half, per user and outside any install,
/// which is where settings have always gone and where saves and the shader cache go when
/// the game cannot write beside itself.
/// </para>
/// <para>
/// Nothing here is macOS-only in effect. A Windows or Linux game still finds its writable
/// directories beside the executable, because that is where they are writable; the fallback
/// simply stops being hypothetical on a Mac.
/// </para>
/// </remarks>
public static class InstallPaths
{
    private const string ApplicationName = "GK3Reborn";

    private static readonly string? _bundleResources = FindBundleResources(AppContext.BaseDirectory);

    /// <summary>
    /// The per-user directory for anything the game writes that is not a saved game.
    /// </summary>
    /// <remarks>
    /// <c>%AppData%\GK3Reborn</c> on Windows and <c>~/.config/GK3Reborn</c> on Linux, both
    /// of which are what <see cref="Environment.SpecialFolder.ApplicationData"/> gives.
    /// On macOS that same folder is also <c>~/.config</c>, which is not where a Mac keeps
    /// per-application state - <c>~/Library/Application Support</c> is, and a file put
    /// anywhere else is invisible to Time Machine's migration and to the user. Named
    /// explicitly for that reason rather than trusted to the BCL's Unix mapping.
    /// </remarks>
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
    /// <remarks>The directory returned exists by the time it is returned.</remarks>
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
    /// <remarks>
    /// Beside the executable first because that keeps an unpacked install self-contained:
    /// somebody who moves the folder takes their shader cache with them. The probe is a
    /// real write rather than a permissions check, because a permissions check answers a
    /// different question than the one being asked on a read-only volume, an unsigned
    /// bundle or a directory behind a consent prompt nobody is there to answer.
    /// </remarks>
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
    /// <remarks>
    /// The shape of the path is the test, not the operating system: a bundle laid out on a
    /// Windows machine by the macOS publish profile is still a bundle, and being able to
    /// reason about one without a Mac is the difference between this being testable and
    /// not. Public for that reason - <see cref="BundleResources"/> is the answer for this
    /// process, and this is the answer for any path.
    /// </remarks>
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
