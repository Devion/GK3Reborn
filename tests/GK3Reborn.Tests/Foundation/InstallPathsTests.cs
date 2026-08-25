using GK3Reborn.Foundation;
using Xunit;

namespace GK3Reborn.Tests.Foundation;

/// <summary>
/// The read-only-install rules, checked on a machine that is not a Mac.
/// </summary>
/// <remarks>
/// Bundle detection is deliberately a question about the shape of a path rather than about
/// the operating system, which is what makes it checkable here at all. The rest is the
/// writable-directory fallback, which has nothing macOS-specific in it either: a directory
/// that cannot be written to behaves the same way everywhere.
/// </remarks>
public sealed class InstallPathsTests
{
    [Theory]
    [InlineData("GK3Reborn.app")]
    [InlineData("Some Game.app")]
    public void A_bundle_layout_is_recognised_and_resolves_to_its_resources(string bundle)
    {
        string root = Path.Combine(Path.GetTempPath(), "reborn-bundle-test");
        string macOs = Path.Combine(root, bundle, "Contents", "MacOS");

        string? resources = InstallPaths.FindBundleResources(macOs);

        Assert.Equal(Path.Combine(root, bundle, "Contents", "Resources"), resources);
    }

    [Fact]
    public void A_trailing_separator_does_not_hide_the_bundle()
    {
        string macOs = Path.Combine("/opt", "GK3Reborn.app", "Contents", "MacOS")
            + Path.DirectorySeparatorChar;

        Assert.NotNull(InstallPaths.FindBundleResources(macOs));
    }

    [Theory]
    [InlineData("")]
    [InlineData("/usr/local/games/GK3Reborn")]
    // The right leaf under the wrong parents: an ordinary directory called MacOS is not a
    // bundle, and treating one as a bundle would send content lookups off into a sibling
    // Resources directory that has nothing to do with the game.
    [InlineData("/usr/local/games/MacOS")]
    [InlineData("/usr/local/GK3Reborn.app/MacOS")]
    // Contents/MacOS under something that is not an .app is the same mistake.
    [InlineData("/usr/local/GK3Reborn/Contents/MacOS")]
    public void Anything_else_is_not_a_bundle(string directory) =>
        Assert.Null(InstallPaths.FindBundleResources(directory.Replace('/', Path.DirectorySeparatorChar)));

    [Fact]
    public void A_writable_directory_is_used_where_it_is_asked_for()
    {
        // The test host writes to its own output directory, so this is the case a normal
        // unpacked install is in: the answer is beside the executable.
        string directory = InstallPaths.WritableDirectory("reborn-writable-test");

        try
        {
            Assert.Equal(
                Path.Combine(AppContext.BaseDirectory, "reborn-writable-test"),
                directory);

            Assert.True(Directory.Exists(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void The_writable_root_exists_and_can_be_written_to()
    {
        string root = InstallPaths.WritableRoot;

        Assert.True(Directory.Exists(root));
        Assert.True(InstallPaths.CanWrite(root));
    }

    [Fact]
    public void Nothing_is_writable_when_there_is_nowhere_to_write()
    {
        // A path with a NUL in it cannot be created on any platform this runs on, which is
        // the cheapest way to reach the failure branch without needing a read-only volume.
        Assert.False(InstallPaths.CanWrite("\0"));
        Assert.False(InstallPaths.CanWrite(null));
        Assert.False(InstallPaths.CanWrite(string.Empty));
    }

    [Fact]
    public void The_user_directory_is_named_for_the_game_and_is_absolute()
    {
        Assert.True(Path.IsPathFullyQualified(InstallPaths.UserData));
        Assert.Equal("GK3Reborn", Path.GetFileName(InstallPaths.UserData));
    }
}
