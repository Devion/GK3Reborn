using GK3Reborn.Content;
using Xunit;

namespace GK3Reborn.Tests.Content;

/// <summary>
/// Tests for where a movie comes from.
/// </summary>
/// <remarks>
/// Two sources and one rule between them: <c>--rebarn</c> means the packs and nothing else,
/// and without it the loose <c>enhanced/video</c> directory is read as well and wins. That
/// is the same way round as every other enhanced kind, and getting it backwards is the sort
/// of thing nobody notices until a re-imported cutscene refuses to change.
/// </remarks>
public sealed class VideoLibraryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "gk3r-video-" + Guid.NewGuid().ToString("N"));

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void A_directory_of_movies_is_keyed_by_name_without_the_container()
    {
        // The game says PlayFullScreenMovie("212pBegin") and the disc holds 212pbegin.bik.
        // The name is the identity; what it is stored in is nobody's business but the
        // decoder's.
        string loose = Make("INTRO.mp4", "Day3-6.mp4");

        VideoLibrary videos = VideoLibrary.Open(loose);

        Assert.Equal(2, videos.Count);
        Assert.Equal(2, videos.LooseCount);
        Assert.Equal(0, videos.PackedCount);

        Assert.True(videos.Has("INTRO"));
        Assert.True(videos.Has("intro"));
        Assert.True(videos.Has("INTRO.mp4"));
        Assert.True(videos.Has("DAY3-6"));

        Assert.False(videos.Has("nothing"));
        Assert.False(videos.Has(null));
        Assert.False(videos.Has(string.Empty));
    }

    [Fact]
    public void Nothing_but_a_movie_is_taken_for_one()
    {
        // The import leaves its own workings beside the output, and a log file is not a
        // cutscene however hopefully it is opened.
        string loose = Make("INTRO.mp4", "video.json", "_notes.txt", "INTRO.mp4.bak");

        VideoLibrary videos = VideoLibrary.Open(loose);

        Assert.Equal(1, videos.Count);
        Assert.True(videos.Has("INTRO"));
    }

    [Fact]
    public void The_preferred_container_wins_over_the_one_listed_last()
    {
        // Two containers of one movie is the import having been run twice. Which of them
        // is played should not depend on what order the directory happens to enumerate in.
        string loose = Make("INTRO.avi", "INTRO.mp4");

        VideoLibrary videos = VideoLibrary.Open(loose);

        Assert.Equal(1, videos.Count);
        Assert.EndsWith("INTRO.mp4", videos.Source("INTRO"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void No_directory_at_all_is_not_an_error()
    {
        // What --rebarn passes, and what a shipped game has: the packs are the whole of
        // the answer and there is no workspace to read.
        VideoLibrary videos = VideoLibrary.Open(string.Empty);

        Assert.Equal(0, videos.Count);
        Assert.False(videos.Has("INTRO"));
        Assert.Null(videos.Open("INTRO"));

        Assert.Equal(0, VideoLibrary.Open(Path.Combine(_root, "never-made")).Count);
    }

    [Fact]
    public void A_movie_opens_as_a_seekable_stream()
    {
        // Seekable because a decoder has to be: an MP4's index may sit at either end of
        // the file, and one that came from somewhere other than this pipeline need not
        // have been written front-loaded.
        string loose = Make("INTRO.mp4");
        File.WriteAllBytes(Path.Combine(loose, "INTRO.mp4"), [1, 2, 3, 4, 5, 6, 7, 8]);

        using Stream? stream = VideoLibrary.Open(loose).Open("INTRO");

        Assert.NotNull(stream);
        Assert.True(stream.CanSeek);
        Assert.True(stream.CanRead);
        Assert.Equal(8, stream.Length);

        stream.Seek(-2, SeekOrigin.End);
        Assert.Equal(7, stream.ReadByte());
    }

    /// <summary>Makes a directory holding empty files of those names.</summary>
    private string Make(params string[] names)
    {
        string directory = Path.Combine(_root, "video");
        Directory.CreateDirectory(directory);

        foreach (string name in names)
        {
            File.WriteAllBytes(Path.Combine(directory, name), []);
        }

        return directory;
    }
}

/// <summary>
/// Tests for reading a movie out of memory somebody else owns.
/// </summary>
/// <remarks>
/// A movie in a pack is a window onto a mapping, and a decoder wants a stream. The
/// framework has no read-only stream over <see cref="ReadOnlyMemory{T}"/>, so this is one —
/// and a stream that seeks wrongly gives a decoder a file that looks corrupt.
/// </remarks>
public sealed class MappedStreamTests
{
    private static MappedStream Over(params byte[] bytes) => new(bytes);

    [Fact]
    public void It_reads_from_wherever_it_is_told_to()
    {
        using var stream = Over(0, 1, 2, 3, 4, 5, 6, 7, 8, 9);

        Assert.Equal(10, stream.Length);
        Assert.Equal(0, stream.Position);

        byte[] taken = new byte[4];
        Assert.Equal(4, stream.Read(taken, 0, 4));
        Assert.Equal([0, 1, 2, 3], taken);
        Assert.Equal(4, stream.Position);

        Assert.Equal(6, stream.Seek(-4, SeekOrigin.End));
        Assert.Equal(6, stream.ReadByte());

        Assert.Equal(2, stream.Seek(-5, SeekOrigin.Current));
        Assert.Equal(2, stream.ReadByte());

        Assert.Equal(9, stream.Seek(9, SeekOrigin.Begin));
        Assert.Equal(9, stream.ReadByte());
    }

    [Fact]
    public void Reading_past_the_end_gives_nothing_rather_than_throwing()
    {
        // What a decoder does when it reaches the end of a file, and it must be told so
        // rather than being given an exception to interpret.
        using var stream = Over(1, 2, 3);

        stream.Position = 3;

        Assert.Equal(0, stream.Read(new byte[4], 0, 4));
        Assert.Equal(-1, stream.ReadByte());
    }

    [Fact]
    public void A_short_read_at_the_end_gives_what_is_left()
    {
        using var stream = Over(1, 2, 3);

        byte[] taken = new byte[8];

        Assert.Equal(3, stream.Read(taken, 0, 8));
        Assert.Equal([1, 2, 3], taken[..3]);
    }

    [Fact]
    public void It_refuses_to_seek_outside_itself()
    {
        using var stream = Over(1, 2, 3);

        Assert.Throws<ArgumentOutOfRangeException>(() => stream.Position = -1);
        Assert.Throws<ArgumentOutOfRangeException>(() => stream.Position = 4);

        // The end itself is a legitimate place to be: it is where a read returns nothing.
        stream.Position = 3;
        Assert.Equal(3, stream.Position);
    }

    [Fact]
    public void It_is_read_only()
    {
        // The memory belongs to the mapping, and a stream that could write into it would
        // be writing into a file the pack has open.
        using var stream = Over(1, 2, 3);

        Assert.False(stream.CanWrite);
        Assert.Throws<NotSupportedException>(() => stream.Write(new byte[1], 0, 1));
        Assert.Throws<NotSupportedException>(() => stream.SetLength(1));
    }
}
