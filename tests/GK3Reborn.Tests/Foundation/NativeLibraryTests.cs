using System.Runtime.InteropServices;
using GK3Reborn.Foundation;
using Silk.NET.Core.Loader;
using Xunit;

namespace GK3Reborn.Tests.Foundation;

/// <summary>
/// That Silk.NET can find the native libraries this build produced.
/// </summary>
/// <remarks>
/// <para>
/// Worth a test of its own because the failure is silent until it is total, and because it
/// is a failure on one platform only. Silk.NET does its own loading and asks
/// <c>Microsoft.DotNet.PlatformAbstractions</c> which RID it is running on; on Linux that
/// answers with the distribution's — <c>ubuntu.24.04-x64</c> — and Silk's map from that to a
/// portable RID lists sixteen distributions without ubuntu among them. The map fails, no
/// fallback is produced, and the only directory it looks in is one no package has ever
/// shipped. Every shader in the tree then fails to compile with "could not load from any of
/// the possible library names", which reads like a missing package rather than a RID.
/// </para>
/// <para>
/// Both tests below pass on Windows and macOS with or without
/// <see cref="NativeLibraries"/>, which is the point: this is the only thing in the suite
/// that would have caught it, and it catches it on the machine where it is wrong.
/// </para>
/// </remarks>
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
