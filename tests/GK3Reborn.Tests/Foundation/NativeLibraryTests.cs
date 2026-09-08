using System.Runtime.InteropServices;
using GK3Reborn.Foundation;
using Silk.NET.Core.Loader;
using Xunit;

namespace GK3Reborn.Tests.Foundation;

/// <summary>
/// That Silk.NET can find the native libraries this build produced.
/// </summary>
public sealed class NativeLibraryTests
{
    /// <summary>Where the build put the natives: the packages' own shape, beside the assemblies.</summary>
    private static string RuntimesDirectory => Path.Combine(
        AppContext.BaseDirectory, "runtimes", RuntimeInformation.RuntimeIdentifier, "native");

    [Fact]
    public void The_resolver_searches_this_platforms_runtimes_directory()
    {
        // RuntimeInformation.RuntimeIdentifier rather than a guessed one. It is portable on
        // every platform — linux-x64, not ubuntu.24.04-x64 — and portable is what names the
        // directory a restore actually produces.
        Assert.Contains(RuntimesDirectory, NativeLibraries.SearchDirectories);
    }

    [Fact]
    public void Every_native_the_build_produced_can_be_found_by_name()
    {
        Assert.SkipUnless(
            Directory.Exists(RuntimesDirectory),
            $"nothing was restored into {RuntimesDirectory}");

        string[] natives = [.. Directory.EnumerateFiles(RuntimesDirectory).Select(Path.GetFileName)!];

        Assert.NotEmpty(natives);

        List<string> lost = [];

        foreach (string native in natives)
        {
            // What Silk.NET itself asks, in the order it tries the answers. A name that
            // yields no path that exists is a library the loader will not find however
            // plainly it is sitting on the disk.
            bool found = PathResolver.Default
                .EnumeratePossibleLibraryLoadTargets(native)
                .Any(candidate => Path.IsPathRooted(candidate) && File.Exists(candidate));

            if (!found)
            {
                lost.Add(native);
            }
        }

        Assert.Empty(lost);
    }
}
