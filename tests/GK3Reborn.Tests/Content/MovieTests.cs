using GK3Reborn.Content;
using GK3Reborn.Formats.Audio;
using GK3Reborn.Foundation.Diagnostics;
using GK3Reborn.Tests.Formats;
using Xunit;

namespace GK3Reborn.Tests.Content;

/// <summary>
/// Tests for playing a movie end to end: the container, both decoders, the clock.
/// </summary>
/// <remarks>
/// These need one of the game's converted clips, so they are skipped where the content
/// workspace is not beside the checkout; the decoders themselves are covered without it.
/// </remarks>
public sealed class MovieTests
{
    private static VideoLibrary? OpenLibrary(out string name)
    {
        name = "TENIERGEOD";
        string? clip = H264DecoderTests.FindClip(name + ".mp4");

        return clip is null ? null : VideoLibrary.Open(Path.GetDirectoryName(clip)!);
    }

    [Fact]
    public void A_movie_opens_and_describes_itself()
    {
        VideoLibrary? videos = OpenLibrary(out string name);
        Assert.SkipUnless(videos is not null, "needs the game's converted clips");

        var diagnostics = new DiagnosticBag();
        using Movie? movie = Movie.Open(videos!, name, diagnostics);

        Assert.NotNull(movie);
        Assert.Empty(diagnostics.Items);
        Assert.Equal(464, movie.Width);
        Assert.Equal(350, movie.Height);
        Assert.True(movie.HasAudio);
        Assert.InRange(movie.Duration.TotalSeconds, 0.3, 0.4);
        Assert.InRange(movie.FrameRate, 14.5, 15.5);
        Assert.Contains("464x350", movie.Describe(), StringComparison.Ordinal);
        Assert.Contains("48000 Hz 2 ch", movie.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void Frames_arrive_by_time_and_the_sound_is_whole()
    {
        VideoLibrary? videos = OpenLibrary(out string name);
        Assert.SkipUnless(videos is not null, "needs the game's converted clips");

        using Movie movie = Movie.Open(videos!, name)!;

        // Five frames at 15 fps. Ask a little after each one's time, waiting for the
        // decode thread when it has not got there yet.
        int frames = 0;

        for (int i = 0; i < 5; i++)
        {
            TimeSpan at = TimeSpan.FromSeconds(i / 15.0 + 0.01);
            bool got = false;

            for (int attempt = 0; attempt < 200 && !got; attempt++)
            {
                got = movie.TryReadFrame(at, out MovieFrame frame);

                if (got)
                {
                    Assert.Equal(464, frame.Width);
                    Assert.Equal(350, frame.Height);
                    Assert.Equal(464 * 350 * 4, frame.Rgba.Length);
                    Assert.Equal(255, frame.Rgba.Span[3]);
                    frames++;
                }
                else
                {
                    Thread.Sleep(5);
                }
            }
        }

        Assert.Equal(5, frames);
        Assert.False(movie.TryReadFrame(movie.Duration, out _));

        WavFile? sound = movie.ReadSound();
        Assert.NotNull(sound);
        Assert.Equal(2, sound.Channels);
        Assert.Equal(48000, sound.SampleRate);

        // 17 access units of 1024 samples, less the 1024-sample priming delay the edit
        // list declares: a third of a second, like the picture.
        Assert.Equal(16 * 1024 * 2, sound.Samples.Length);
    }

    [Fact]
    public void A_missing_movie_is_reported_not_thrown()
    {
        VideoLibrary? videos = OpenLibrary(out _);
        Assert.SkipUnless(videos is not null, "needs the game's converted clips");

        var diagnostics = new DiagnosticBag();
        using Movie? movie = Movie.Open(videos!, "NOSUCHMOVIE", diagnostics);

        Assert.Null(movie);
        Assert.Contains(diagnostics.Items, d => d.Code == "GK3R1161");
    }
}
