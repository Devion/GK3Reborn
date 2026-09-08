using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Silk.NET.Core.Loader;

namespace GK3Reborn.Foundation;

/// <summary>
/// Teaches Silk.NET where this build keeps its native libraries.
/// </summary>
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
