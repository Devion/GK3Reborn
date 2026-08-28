using System.Reflection;
using System.Runtime.InteropServices;
using Silk.NET.Core.Loader;

namespace GK3Reborn.Bootstrap;

/// <summary>
/// Resolves native libraries out of <c>libs/&lt;rid&gt;</c> so the install root stays clean.
/// </summary>
/// <remarks>
/// <para>
/// The brief asks for native and managed clutter to live under <c>libs/</c> rather than
/// beside the executable. The per-RID subdirectory is a deliberate refinement so one
/// tree can carry both <c>win-x64</c> and <c>linux-x64</c> payloads.
/// </para>
/// <para>
/// Resolution is by absolute path. The global <c>PATH</c> is never modified: mutating
/// it would change how every other process on the machine loads libraries, and it
/// fails silently when it fails at all.
/// </para>
/// <para>
/// Silk.NET does not route all of its loading through the BCL resolver - it has its own
/// search mechanism, so the hook below never sees glfw3, soft_oal or shaderc_shared.
/// <see cref="Install"/> therefore teaches Silk.NET's resolver about the same directory.
/// </para>
/// </remarks>
public static class NativeLibraryLocator
{
    private static readonly string[] Prefixes = OperatingSystem.IsWindows()
        ? [string.Empty]
        : ["lib", string.Empty];

    private static readonly string[] Extensions = OperatingSystem.IsWindows()
        ? [".dll"]
        : OperatingSystem.IsMacOS() ? [".dylib"] : [".so"];

    /// <summary>Library names already written down, so that a log says each thing once.</summary>
    private static readonly HashSet<string> _reported = new(StringComparer.Ordinal);

    private static string? _libsRoot;
    private static bool _installed;

    /// <summary>The directory native libraries are resolved from, once installed.</summary>
    public static string? LibsRoot => _libsRoot;

    /// <summary>
    /// Installs the resolver for an assembly. Call before any P/Invoke that could
    /// trigger a native load.
    /// </summary>
    /// <param name="assembly">Assembly whose imports should resolve from <c>libs</c>.</param>
    /// <param name="baseDirectory">Install root; defaults to the app's base directory.</param>
    public static void Install(Assembly assembly, string? baseDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        _libsRoot = Path.Combine(
            baseDirectory ?? AppContext.BaseDirectory,
            "libs",
            RuntimeInformation.RuntimeIdentifier);

        NativeLibrary.SetDllImportResolver(assembly, Resolve);

        if (!_installed)
        {
            _installed = true;
            InstallSilkResolver();
        }
    }

    /// <summary>
    /// Adds <c>libs/&lt;rid&gt;</c> to Silk.NET's own search, which is how GLFW, OpenAL
    /// Soft and shaderc are found once the publish has moved them out of
    /// <c>runtimes/&lt;rid&gt;/native</c>.
    /// </summary>
    /// <remarks>
    /// <see cref="DefaultPathResolver.Resolvers"/> is a public, mutable list of candidate
    /// generators; prepending puts an absolute path from the install root ahead of both
    /// the bare name - which would let a stray system copy win - and the now-absent
    /// runtimes tree. The directory check happens per call rather than once, so a run
    /// started before the payload was dropped in still picks it up.
    /// </remarks>
    private static void InstallSilkResolver()
    {
        if (PathResolver.Default is not DefaultPathResolver resolver)
        {
            return;
        }

        string root = _libsRoot!;

        resolver.Resolvers.Insert(
            0,
            name => Directory.Exists(root) ? [Path.Combine(root, name)] : []);
    }

    /// <summary>Finds a candidate file for a native library name, or null.</summary>
    /// <param name="libraryName">Name as written in the <c>DllImport</c>.</param>
    /// <returns>An absolute path, or null when nothing matches.</returns>
    public static string? FindCandidate(string libraryName)
    {
        if (string.IsNullOrEmpty(_libsRoot) || !Directory.Exists(_libsRoot))
        {
            return null;
        }

        // The name may already carry a platform-correct extension.
        string direct = Path.Combine(_libsRoot, libraryName);
        if (File.Exists(direct))
        {
            return direct;
        }

        foreach (string prefix in Prefixes)
        {
            foreach (string extension in Extensions)
            {
                string candidate = Path.Combine(_libsRoot, prefix + libraryName + extension);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        string? candidate = FindCandidate(libraryName);
        if (candidate is null)
        {
            // Fall through to the default probing logic rather than failing here. Recorded
            // rather than ignored: when the default probing then fails too, the exception
            // says only that a library was not found, and this line says where the game
            // had expected it to be.
            Note(libraryName, $"{libraryName}: not under {_libsRoot}, asking the system loader");

            return IntPtr.Zero;
        }

        if (NativeLibrary.TryLoad(candidate, out IntPtr handle))
        {
            Note(libraryName, $"{libraryName}: loaded {candidate}");

            return handle;
        }

        // The file is there and will not load, which on Linux almost always means one of
        // its own dependencies is missing - libX11 for GLFW, a stale glibc for anything.
        // The default probing runs next and fails with a message that names neither, so
        // this is the only place the difference between "absent" and "unloadable" exists.
        Warn(libraryName, $"{libraryName}: {candidate} exists but could not be loaded. "
            + "Its own dependencies are probably missing; on Linux, `ldd` on that file "
            + "names them.");

        return IntPtr.Zero;
    }

    /// <summary>Records what happened to one library, once per library.</summary>
    /// <remarks>
    /// A resolver is asked about the same name repeatedly, and once is all a log needs. The
    /// engine's log is reached from here rather than the other way round because this runs
    /// long after startup, when the assembly it lives in is loaded and open.
    /// </remarks>
    private static void Note(string libraryName, string message)
    {
        if (FirstTime(libraryName))
        {
            GK3Reborn.Foundation.Diagnostics.Log.Detail(message);
        }
    }

    private static void Warn(string libraryName, string message)
    {
        if (FirstTime(libraryName))
        {
            GK3Reborn.Foundation.Diagnostics.Log.Warning(message);
        }
    }

    private static bool FirstTime(string libraryName)
    {
        lock (_reported)
        {
            return _reported.Add(libraryName);
        }
    }
}
