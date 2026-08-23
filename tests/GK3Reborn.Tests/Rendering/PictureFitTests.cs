using GK3Reborn.Rendering.Vulkan;
using Xunit;

namespace GK3Reborn.Tests.Rendering;

/// <summary>
/// Tests for fitting a picture to a window.
/// </summary>
/// <remarks>
/// Nobody notices this is broken until everybody in a cutscene is short and wide, and by
/// then it looks like a bad video rather than a bad number. The one property worth checking
/// is that the shape never changes, whatever the window is doing.
/// </remarks>
public sealed class PictureFitTests
{
    /// <summary>Every combination worth worrying about, and a few that are not.</summary>
    public static TheoryData<int, int, int, int> Shapes()
    {
        var data = new TheoryData<int, int, int, int>();

        (int Width, int Height)[] pictures =
        [
            (640, 480),      // the title art, and every cutscene the game shipped
            (2048, 1536),    // an upscale of it
            (1440, 1080),
            (320, 240),
            (41, 51),        // the smallest movie in the archives, taller than it is wide
            (1920, 1080),
        ];

        (int Width, int Height)[] windows =
        [
            (1280, 720), (1920, 1080), (1024, 768), (1600, 1000),
            (3440, 1440),                                          // ultrawide
            (2160, 3840),                                          // a display on its side
            (800, 600), (7680, 4320),
        ];

        foreach ((int pw, int ph) in pictures)
        {
            foreach ((int ww, int wh) in windows)
            {
                data.Add(pw, ph, ww, wh);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public void The_shape_of_the_picture_never_changes(int pw, int ph, int ww, int wh)
    {
        foreach (bool cover in new[] { false, true })
        {
            (float x, float y) = MoviePipeline.Fit(pw, ph, ww, wh, cover);

            // What is drawn, in window pixels. Its shape has to be the picture's own.
            float drawn = ww * x / (wh * y);

            Assert.Equal((float)pw / ph, drawn, 3);
            Assert.True(x > 0 && y > 0, "the picture was given no room at all");
        }
    }

    [Fact]
    public void Fitting_shows_the_whole_picture_and_covering_fills_the_window()
    {
        // 4:3 in 16:9. Fitted, the height is the window's and the width falls short, which
        // is the bar down each side.
        (float x, float y) = MoviePipeline.Fit(640, 480, 1280, 720, cover: false);

        Assert.Equal(0.75f, x, 3);
        Assert.Equal(1f, y, 3);

        // Covered, the width is the window's and the height overshoots, which is the crop.
        (x, y) = MoviePipeline.Fit(640, 480, 1280, 720, cover: true);

        Assert.Equal(1f, x, 3);
        Assert.Equal(4f / 3f, y, 3);
    }

    [Fact]
    public void A_picture_the_shape_of_the_window_is_left_alone()
    {
        foreach (bool cover in new[] { false, true })
        {
            (float x, float y) = MoviePipeline.Fit(1920, 1080, 1280, 720, cover);

            Assert.Equal(1f, x, 3);
            Assert.Equal(1f, y, 3);
        }
    }

    [Fact]
    public void Covering_stops_before_it_crops_the_name_off_the_title_screen()
    {
        // An ultrawide display with the game's 4:3 art. Covering it outright would mean
        // showing 56% of the picture's height, which cuts the lettering.
        (float x, float y) = MoviePipeline.Fit(640, 480, 3440, 1440, cover: true);

        Assert.Equal(MoviePipeline.MostCropped, y, 3);
        Assert.True(x < 1f, "it filled the window by cropping further than it is allowed to");

        // Two thirds of the height is still on screen, which is enough to keep the name.
        Assert.True(1f / y > 0.7f, $"only {1f / y:P0} of the picture's height survived");
    }

    [Fact]
    public void Nothing_asked_of_it_makes_it_divide_by_zero()
    {
        foreach (bool cover in new[] { false, true })
        {
            Assert.Equal((1f, 1f), MoviePipeline.Fit(0, 0, 1280, 720, cover));
            Assert.Equal((1f, 1f), MoviePipeline.Fit(640, 480, 0, 0, cover));
            Assert.Equal((1f, 1f), MoviePipeline.Fit(-1, 480, 1280, 720, cover));
        }
    }
}
