using GK3Reborn.Foundation.Diagnostics;
using Xunit;

namespace GK3Reborn.Tests.Foundation;

/// <summary>
/// The log file, which is the half of logging that can get things wrong.
/// </summary>
/// <remarks>
/// The point of the file is to be readable by somebody who was not there when the game
/// ran, which puts three things under test: that a restart does not destroy the log of the
/// crash it is being restarted after, that two processes do not scribble over each other,
/// and that a message spanning lines arrives as lines rather than as one run-on entry.
/// </remarks>
public sealed class LogFileTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "reborn-log-tests", Guid.NewGuid().ToString("N"));

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
    public void A_run_writes_log_txt()
    {
        using (LogFile? file = LogFile.Open(_directory, out string? failure))
        {
            Assert.Null(failure);
            Assert.NotNull(file);
            Assert.Equal(Path.Combine(_directory, LogFile.FileName), file.Path);
            Assert.True(file.Write("info  ", "hello"));
        }

        Assert.Contains("hello", Text(Path.Combine(_directory, LogFile.FileName)), StringComparison.Ordinal);
    }

    [Fact]
    public void The_previous_run_is_kept_rather_than_overwritten()
    {
        using (LogFile? first = LogFile.Open(_directory, out _))
        {
            first!.Write("info  ", "the run that crashed");
        }

        using (LogFile? second = LogFile.Open(_directory, out _))
        {
            second!.Write("info  ", "the run somebody started to see what happened");
        }

        // The restart is the thing that would destroy the evidence, so this is the case
        // that matters: the crash is still on disk after the game has been run again.
        Assert.Contains(
            "the run that crashed",
            Text(Path.Combine(_directory, LogFile.PreviousFileName)),
            StringComparison.Ordinal);

        Assert.Contains(
            "somebody started",
            Text(Path.Combine(_directory, LogFile.FileName)),
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_second_copy_of_the_game_gets_a_file_of_its_own()
    {
        using LogFile? first = LogFile.Open(_directory, out _);
        using LogFile? second = LogFile.Open(_directory, out string? failure);

        Assert.Null(failure);
        Assert.NotNull(second);
        Assert.NotEqual(first!.Path, second.Path);

        first.Write("info  ", "one");
        second.Write("info  ", "two");

        // Both are still being written, and neither has the other's lines in it.
        Assert.Contains("one", Text(first.Path), StringComparison.Ordinal);
        Assert.DoesNotContain("two", Text(first.Path), StringComparison.Ordinal);
        Assert.Contains("two", Text(second.Path), StringComparison.Ordinal);
    }

    [Fact]
    public void Every_line_of_a_message_is_timestamped_and_tagged()
    {
        using (LogFile? file = LogFile.Open(_directory, out _))
        {
            file!.Write("ERROR ", "first line\nsecond line");
        }

        // Written with the platform's own newline, which is what a player's text editor
        // expects; normalised here so the assertions read the same on either.
        string[] lines = Text(Path.Combine(_directory, LogFile.FileName))
            .ReplaceLineEndings("\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(2, lines.Length);

        foreach (string line in lines)
        {
            // hh:mm:ss.fff, two spaces, the tag: enough that a reader can sort and grep.
            Assert.Matches(@"^\d\d:\d\d:\d\d\.\d\d\d  ERROR  ", line);
        }

        Assert.EndsWith("first line", lines[0], StringComparison.Ordinal);
        Assert.EndsWith("second line", lines[1], StringComparison.Ordinal);
    }

    [Theory]
    // One trailing newline is the writer's, not the message's, and would otherwise become
    // a timestamped blank line after every block written with Write.
    [InlineData("one\n", 1)]
    [InlineData("one\r\n", 1)]
    [InlineData("one\ntwo", 2)]
    // A blank line inside a message is deliberate - it is how the startup report separates
    // its paragraphs - so it survives.
    [InlineData("one\n\ntwo", 3)]
    [InlineData("", 1)]
    public void A_message_is_split_into_the_lines_it_will_be_written_as(string message, int expected) =>
        Assert.Equal(expected, LogFile.Lines(message).Length);

    [Fact]
    public void A_directory_that_cannot_be_made_is_reported_rather_than_thrown()
    {
        // A file where the directory should be. Nothing can be created underneath it on any
        // platform, which is the portable way to arrange the failure a read-only install or
        // a revoked permission would produce.
        Directory.CreateDirectory(_directory);

        string blocked = Path.Combine(_directory, "in-the-way");
        File.WriteAllText(blocked, string.Empty);

        LogFile? file = LogFile.Open(Path.Combine(blocked, "logs"), out string? failure);

        Assert.Null(file);
        Assert.False(string.IsNullOrWhiteSpace(failure));
    }

    /// <summary>Reads a log that may still be open for writing.</summary>
    private static string Text(string path)
    {
        // The writer holds the file with FileShare.Read, so a reader has to allow its write
        // access in turn. File.ReadAllText does not, and would fail on an open log.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);

        return reader.ReadToEnd();
    }
}
