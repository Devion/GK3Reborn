using GK3Reborn.Formats.Animation;
using GK3Reborn.Foundation.Diagnostics;
using Xunit;

namespace GK3Reborn.Tests.Content;

/// <summary>
/// Tests for the subtitles GK3 wrote for its cutscenes and never showed.
/// </summary>
/// <remarks>
/// Fourteen of the films carry a <c>.YAK</c> of the film's own name whose <c>[GK3]</c>
/// section is a list of <c>SpeakerCaption</c> nodes — a start frame, an end frame, who is
/// speaking and what they say — and every release translates them. They matter most where a
/// language never dubbed its cutscenes: Spanish and Portuguese films are spoken in English,
/// and these are the whole of what those two have.
/// </remarks>
public sealed class MovieSubtitleTests
{
    /// <summary>A cutscene YAK, in the shape the shipped ones are in.</summary>
    private const string Film = """
        [HEADER]
        900

        [GK3]
        3
        30,SpeakerCaption, 90, GRACE,Perfekt, Kleiner.
        90,SpeakerCaption, 210, WILKES,Hey, Girlie.  You seen Madeline?
        180,SpeakerCaption, 300, UNKNOWN,Somebody off screen.

        [OPTIONS]
        1
        0,FRAMERATE,30
        """;

    [Fact]
    public void A_films_captions_are_read_with_the_frame_rate_the_film_states()
    {
        // Thirty, not the fifteen an ordinary animation runs at. Every cutscene YAK in the
        // game says so in its own [OPTIONS], and reading them at fifteen would put every
        // subtitle at twice its proper time — the last line of a seven-minute film would
        // arrive after it had ended.
        AnimationFile film = AnimationFile.Parse(Film, "205PEND", new DiagnosticBag());

        Assert.Equal(30, film.Rate);
        Assert.Equal(3, film.Captions.Count);
        Assert.Equal(30, film.Captions[0].Frame);
        Assert.Equal(90, film.Captions[0].EndFrame);
        Assert.Equal("GRACE", film.Captions[0].Speaker);
    }

    [Fact]
    public void A_caption_keeps_the_space_after_its_commas()
    {
        // The reader trims every comma-separated field and puts the caption back together,
        // so a bare comma gave "Perfekt,Kleiner." — in every caption in the game that
        // contains one, the room's as well as the films'. It was invisible until the
        // cutscene subtitles started drawing these at four times the size.
        AnimationFile film = AnimationFile.Parse(Film, "205PEND", new DiagnosticBag());

        Assert.Equal("Perfekt, Kleiner.", film.Captions[0].Text);
        Assert.Equal("Hey, Girlie.  You seen Madeline?", film.Captions[1].Text);
    }

    [Fact]
    public void The_last_caption_that_has_started_is_the_one_on_screen()
    {
        // GK3's cutscene captions overlap: one speaker's line often ends after the next has
        // begun, because that is how people talk. Two rows and a rule about which goes
        // where would be a worse answer than showing whoever spoke most recently.
        AnimationFile film = AnimationFile.Parse(Film, "205PEND", new DiagnosticBag());

        Assert.Equal("GRACE", Showing(film, 1.5).Speaker);
        Assert.Equal("WILKES", Showing(film, 3.5).Speaker);

        // 180 to 210 is inside both the second and the third; the third started later.
        Assert.Equal("UNKNOWN", Showing(film, 6.5).Speaker);

        // And nothing before the first or after the last.
        Assert.Equal(default, Showing(film, 0.5));
        Assert.Equal(default, Showing(film, 20));
    }

    [Fact]
    public void Subtitles_are_their_own_row_and_not_the_rooms_captions()
    {
        // Two different decisions. A caption is small and beside whoever is speaking; a
        // subtitle is across the bottom of a full-screen picture with nothing else on it,
        // and somebody may well want the first and not the second over a cutscene they can
        // hear perfectly well. Both default on: they are what the game says, and for Spanish
        // and Portuguese the subtitles are the only thing that says it.
        var settings = new GK3Reborn.Game.Settings();

        Assert.True(settings.Captions);
        Assert.True(settings.MovieSubtitles);

        var front = new GK3Reborn.UI.FrontEnd(settings);
        front.Show(GK3Reborn.UI.FrontEndPage.Gameplay);

        front.Choose(new GK3Reborn.UI.MenuAction("filmcaptions", 0));

        Assert.False(front.Settings.MovieSubtitles);
        Assert.True(front.Settings.Captions);

        front.Show(GK3Reborn.UI.FrontEndPage.Gameplay);
        front.Choose(new GK3Reborn.UI.MenuAction("captions", 0));

        Assert.False(front.Settings.Captions);
        Assert.False(front.Settings.MovieSubtitles);

        // And it survives being written and read back, which is the whole of what a
        // settings file is for.
        string file = Path.Combine(Path.GetTempPath(), "gk3r-subs-" + Guid.NewGuid().ToString("N"));

        try
        {
            Assert.True(front.Settings.Save(file));
            Assert.False(GK3Reborn.Game.Settings.Load(file).MovieSubtitles);
        }
        finally
        {
            File.Delete(file);
        }
    }

    /// <summary>The same rule <c>MoviePlayer</c> applies, over the same data.</summary>
    private static AnimationCaption Showing(AnimationFile film, double seconds)
    {
        int frame = (int)(seconds * film.Rate);
        AnimationCaption showing = default;

        foreach (AnimationCaption caption in film.Captions)
        {
            if (caption.Frame <= frame && frame < caption.EndFrame)
            {
                showing = caption;
            }
        }

        return showing;
    }
}
