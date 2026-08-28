using GK3Reborn.Foundation.Diagnostics;
using Xunit;

namespace GK3Reborn.Tests.Foundation;

/// <summary>
/// The two questions the startup report asks about a path that is not there.
/// </summary>
/// <remarks>
/// Both exist for the platforms this is not developed on. "Which part of this path does
/// exist" separates a mistyped argument from an install that was never unpacked, and the
/// case check catches the one failure a Windows machine cannot reproduce: on Linux and
/// macOS <c>Data</c> and <c>data</c> are two directories, and a player looking at the one
/// the game says is missing is looking at the other one.
/// </remarks>
public sealed class StartupReportTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "reborn-startup-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // A temporary directory that will not go away is not a test failure.
        }
    }

    [Fact]
    public void The_deepest_part_of_a_path_that_exists_is_found()
    {
        string real = Path.Combine(_directory, "install");
        Directory.CreateDirectory(real);

        Assert.Equal(real, StartupReport.Nearest(Path.Combine(real, "Data", "deeper")));
    }

    [Fact]
    public void A_path_that_exists_is_its_own_deepest_part()
    {
        Directory.CreateDirectory(_directory);

        Assert.Equal(_directory, StartupReport.Nearest(_directory));
    }

    [Fact]
    public void An_empty_path_has_no_answer_and_does_not_throw() =>
        Assert.Null(StartupReport.OtherCase(string.Empty));

    [Fact]
    public void A_directory_differing_only_in_case_is_found()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "Data"));

        string wanted = Path.Combine(_directory, "data");

        Assert.Equal(Path.Combine(_directory, "Data"), StartupReport.OtherCase(wanted));
    }

    [Fact]
    public void The_same_name_spelled_the_same_way_is_not_a_near_miss()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "Data"));

        Assert.Null(StartupReport.OtherCase(Path.Combine(_directory, "Data")));
    }

    [Fact]
    public void A_different_name_is_not_a_near_miss()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "Data"));

        Assert.Null(StartupReport.OtherCase(Path.Combine(_directory, "Assets")));
    }

    [Fact]
    public void A_parent_that_does_not_exist_has_no_near_miss_to_offer() =>
        Assert.Null(StartupReport.OtherCase(Path.Combine(_directory, "nowhere", "Data")));
}
