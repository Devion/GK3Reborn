using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Silk.NET.Core.Loader;

namespace GK3Reborn.Foundation;

/// <summary>
/// Teaches Silk.NET where this build keeps its native libraries.
/// </summary>
/// <remarks>
/// <para>
/// Silk.NET does not load its natives through the BCL, so neither a
/// <see cref="System.Runtime.InteropServices.NativeLibrary"/> resolver nor the search
/// directories the host computes from <c>deps.json</c> are consulted for glfw3, soft_oal,
/// shaderc_shared or spirv-cross. It has a resolver of its own, and this adds directories
/// to it.
/// </para>
/// <para>
/// It has to, because that resolver cannot find them on Linux. Silk asks
/// <c>Microsoft.DotNet.PlatformAbstractions</c> for the running RID, which answers with the
/// distribution's — <c>ubuntu.24.04-x64</c> — and then maps it to a portable one through a
/// hard-coded list of distributions that has alpine, arch, debian, fedora, gentoo, rhel and
/// eleven others in it but not ubuntu. The map fails, no fallback RID is produced, and the
/// only directory it looks in is <c>runtimes/ubuntu.24.04-x64/native</c>, which no package
/// has ever shipped. Windows and macOS are unaffected — <c>win10-x64</c> and
/// <c>osx.14-arm64</c> both fall back correctly — which is why this reads as a Linux-only
/// failure to load a library that is plainly sitting there.
/// </para>
/// <para>
/// The BCL's own <see cref="RuntimeInformation.RuntimeIdentifier"/> is portable on every
/// platform (<c>linux-x64</c>, not <c>ubuntu.24.04-x64</c>), so it names the directory the
/// build actually produced. That is the whole fix: the same directory the loader was
/// already meant to find, reached by a RID that is not guessed.
/// </para>
/// <para>
/// Installed by a module initializer rather than by a call, because the thing that needs it
/// is a static constructor several layers down — <c>Shaderc.GetApi()</c> — and every entry
/// point into the engine would otherwise have to remember. A published game adds
/// <c>libs/&lt;rid&gt;</c> to the same list; see <c>NativeLibraryLocator</c> in the host.
/// </para>
/// </remarks>
public static class NativeLibraries
{
    private static readonly Lock Gate = new();

    /// <summary>Where to look, in order. Read under <see cref="Gate"/>.</summary>
    private static readonly List<string> Directories = [];

    private static bool _installed;

    /// <summary>The directories the resolver searches, in the order it searches them.</summary>
    public static IReadOnlyList<string> SearchDirectories
    {
        get
        {
            lock (Gate)
            {
                return [.. Directories];
            }
        }
    }

    /// <summary>Adds a directory, ahead of everything already registered.</summary>
    /// <param name="directory">Where native libraries are, which need not exist yet.</param>
    /// <remarks>
    /// Ahead, because a caller that knows where its payload is put it there on purpose and
    /// should not lose to a copy the build left lying around. Whether the directory exists
    /// is decided per lookup rather than here, so a run started before a payload was
    /// dropped in still picks it up.
    /// </remarks>
    public static void AddSearchDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);

        Install();

        lock (Gate)
        {
            Directories.Remove(directory);
            Directories.Insert(0, directory);
        }
    }

    /// <summary>Installs the resolver, once.</summary>
    /// <remarks>
    /// Safe to call at any time and from anywhere; the module initializer has almost
    /// certainly called it already.
    /// </remarks>
    // CA2255 is about a library surprising an application that did not ask for it. This
    // assembly is not that kind of library: it is the game, split from its host only so the
    // host can be an executable, and every process that loads it - the game, the tools, the
    // tests - needs Silk.NET to be able to find a native library before anything it can
    // usefully call. An initializer is what makes that true of all of them rather than of
    // whichever ones remembered.
#pragma warning disable CA2255
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Install()
    {
        lock (Gate)
        {
            if (_installed)
            {
                return;
            }

            _installed = true;

            // What a build produces: the packages' own runtimes/<rid>/native tree, copied
            // beside the assemblies. What a publish produces is libs/<rid>, and the host
            // registers that itself.
            Directories.Add(
                Path.Combine(
                    AppContext.BaseDirectory,
                    "runtimes",
                    RuntimeInformation.RuntimeIdentifier,
                    "native"));

            if (PathResolver.Default is not DefaultPathResolver resolver)
            {
                return;
            }

            // First, so that a directory named here beats both the bare name — which would
            // let a stray system copy win — and Silk's own guesses. A resolver at the front
            // is the one handed the library's actual name; the rest are handed the
            // candidates their predecessors produced, which is why this refuses anything
            // that already carries a directory.
            resolver.Resolvers.Insert(0, Resolve);
        }
    }

    private static IEnumerable<string> Resolve(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || !string.IsNullOrWhiteSpace(Path.GetDirectoryName(name)))
        {
            return [];
        }

        string[] directories;

        lock (Gate)
        {
            directories = [.. Directories];
        }

        List<string> found = [];

        foreach (string directory in directories)
        {
            string candidate = Path.Combine(directory, name);

            if (File.Exists(candidate))
            {
                found.Add(candidate);
            }
        }

        return found;
    }
}
