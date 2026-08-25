using System.Numerics;
using GK3Reborn.Audio;
using GK3Reborn.Formats.Audio;
using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Formats.Ui;
using GK3Reborn.Foundation.Diagnostics;
using GK3Reborn.Game;
using GK3Reborn.Rendering;
using GK3Reborn.UI;
using Xunit;

namespace GK3Reborn.Tests.UI;

/// <summary>
/// Tests for the menu in front of the game.
/// </summary>
/// <remarks>
/// The whole point of keeping the front end free of a window is that this can be checked
/// without drawing anything: a slider has to move the thing it is labelled with, and a
/// setting has to survive being written and read back. Both are the sort of thing that is
/// tedious to verify by hand and silently wrong for months when it breaks.
/// </remarks>
public sealed class FrontEndTests
{
    private static FrontEnd Front(Settings? settings = null, bool inGame = false) =>
        new(settings ?? new Settings(), inGame);

    private static MenuItem Row(FrontEnd front, string id) =>
        front.Items.Single(i => i.Id == id);

    [Fact]
    public void The_first_page_is_the_originals_own_five_and_the_pause_page_is_not()
    {
        // The names the 1999 title screen used, in its order.
        Assert.Equal(
            ["intro", "play", "load", "options", "quit"],
            Front().Items.Select(i => i.Id));

        Assert.Equal("Play", Row(Front(), "play").Text);

        // Restoring works now, so the row is live. It was drawn and disabled while saving
        // was unbuilt, because a menu that simply omits it leaves the player looking.
        Assert.True(Row(Front(), "load").Enabled);

        // Paused, where the intro would be an odd thing to offer somebody in the middle of
        // the game, and the first row is the one that gives it back to them. Saving belongs
        // here and not on the title screen: there is nothing to write until there is a game.
        FrontEnd paused = Front(inGame: true);

        Assert.Equal(
            ["resume", "save", "load", "options", "quit"],
            paused.Items.Select(i => i.Id));

        Assert.Equal("Resume", Row(paused, "resume").Text);
        Assert.Equal("Leave the Game", Row(paused, "quit").Text);
    }

    [Fact]
    public void The_intro_can_be_watched_again_from_the_menu()
    {
        Assert.Equal(FrontEndOutcome.Intro, Front().Choose(new MenuAction("intro")));

        // And is not offered while the game is on, so nothing can ask for it there.
        Assert.DoesNotContain(Front(inGame: true).Items, i => i.Id == "intro");
    }

    [Fact]
    public void The_art_carries_the_name_where_there_is_art()
    {
        FrontEnd front = Front();

        Assert.Equal("Gabriel Knight 3", front.Title);

        // The picture has the game's name painted into it, so the menu draws no heading of
        // its own over the top of it.
        front.Illustrated = true;
        Assert.Equal(string.Empty, front.Title);

        // Except where the picture is not showing: a settings page is a settings page.
        front.Show(FrontEndPage.Audio);
        Assert.Equal("Sound", front.Title);

        // And paused, where what is behind is the room.
        front.Show(FrontEndPage.Main);
        front.InGame = true;
        Assert.Equal("Paused", front.Title);
    }

    [Fact]
    public void A_page_with_no_heading_still_draws_its_rows_in_the_window()
    {
        var page = new MenuPage(new Overlay(MenuPageTests.Font()))
        {
            Behind = MenuBehind.Picture,
            Down = 0.74f,
        };

        IReadOnlyList<MenuItem> items = Front().Items;

        page.Build(string.Empty, items, 1280, 720, Vector2.Zero);

        Assert.NotEmpty(page.Overlay.Quads);

        foreach (OverlayQuad quad in page.Overlay.Quads)
        {
            Assert.InRange(quad.Destination.Y, 0f, 720f);
            Assert.InRange(quad.Destination.Y + quad.Destination.W, 0f, 720f);
        }

        // Low, so the game's name in the middle of the art is not covered by its own menu.
        Assert.True(
            page.Overlay.Quads.Min(q => q.Destination.Y) > 720 * 0.4f,
            "the menu sat over the middle of the title art");

        // Nothing is painted over the picture but the panel and its rows.
        Assert.DoesNotContain(
            page.Overlay.Quads,
            q => q.Destination.Z >= 1280 && q.Destination.W >= 720);
    }

    [Fact]
    public void Choosing_a_row_walks_into_its_page_and_escape_walks_back_out()
    {
        FrontEnd front = Front();

        Assert.Equal(FrontEndOutcome.Stay, front.Choose(new MenuAction("options")));
        Assert.Equal(FrontEndPage.Options, front.Page);

        front.Choose(new MenuAction("audio"));
        Assert.Equal(FrontEndPage.Audio, front.Page);

        // Out of the sound page to the settings page, then to the top, and no further.
        Assert.True(front.Back());
        Assert.Equal(FrontEndPage.Options, front.Page);

        Assert.True(front.Back());
        Assert.Equal(FrontEndPage.Main, front.Page);

        Assert.False(front.Back());
    }

    [Fact]
    public void A_slider_moves_the_setting_it_is_labelled_with()
    {
        FrontEnd front = Front();

        // Dragged to a position outright, which is what clicking halfway along means.
        front.Choose(new MenuAction("music", Fraction: 0.25f));

        Assert.Equal(0.25f, front.Settings.MusicVolume, 3);

        // And nothing else moved with it.
        Assert.Equal(1f, front.Settings.MasterVolume, 3);
        Assert.Equal(1f, front.Settings.DialogueVolume, 3);

        // Stepped, which is what an arrow key means.
        front.Choose(new MenuAction("music", Step: 1));
        Assert.Equal(0.30f, front.Settings.MusicVolume, 3);

        front.Choose(new MenuAction("music", Step: -1));
        Assert.Equal(0.25f, front.Settings.MusicVolume, 3);
    }

    [Fact]
    public void A_slider_stops_at_both_ends()
    {
        FrontEnd front = Front();

        for (int i = 0; i < 40; i++)
        {
            front.Choose(new MenuAction("effects", Step: -1));
        }

        Assert.Equal(0f, front.Settings.EffectsVolume, 3);

        for (int i = 0; i < 60; i++)
        {
            front.Choose(new MenuAction("effects", Step: 1));
        }

        Assert.Equal(1f, front.Settings.EffectsVolume, 3);
    }

    [Fact]
    public void The_hurrying_pace_runs_from_the_authored_speed_to_a_sprint()
    {
        FrontEnd front = Front();

        front.Choose(new MenuAction("hurry", Fraction: 0f));
        Assert.Equal(1f, front.Settings.HurryFactor, 2);

        front.Choose(new MenuAction("hurry", Fraction: 1f));
        Assert.Equal(4f, front.Settings.HurryFactor, 2);

        // One means a double-click does nothing, which is a legitimate answer for somebody
        // who wants the pace the game was authored at.
        front.Choose(new MenuAction("hurry", Fraction: 1f / 3f));
        Assert.Equal(2f, front.Settings.HurryFactor, 2);
    }

    [Fact]
    public void A_choice_steps_round_rather_than_stopping()
    {
        FrontEnd front = Front(new Settings { Picture = PictureQuality.Highest });

        front.Choose(new MenuAction("picture", Step: 1));
        Assert.Equal(PictureQuality.Original, front.Settings.Picture);

        front.Choose(new MenuAction("picture", Step: -1));
        Assert.Equal(PictureQuality.Highest, front.Settings.Picture);
    }

    [Fact]
    public void Every_picture_quality_names_a_ray_tracing_level()
    {
        Assert.Equal(RayTracingQuality.None, new Settings { Picture = PictureQuality.Original }.Quality);
        Assert.Equal(RayTracingQuality.Low, new Settings { Picture = PictureQuality.Improved }.Quality);
        Assert.Equal(RayTracingQuality.Medium, new Settings { Picture = PictureQuality.High }.Quality);
        Assert.Equal(RayTracingQuality.High, new Settings { Picture = PictureQuality.Highest }.Quality);
    }

    [Fact]
    public void A_toggle_turns_over()
    {
        FrontEnd front = Front();

        Assert.True(front.Settings.Cinematics);
        front.Choose(new MenuAction("cinematics"));
        Assert.False(front.Settings.Cinematics);
        front.Choose(new MenuAction("cinematics"));
        Assert.True(front.Settings.Cinematics);
    }

    [Fact]
    public void The_easter_eggs_are_off_until_the_player_asks_for_them()
    {
        // The game as it shipped is the game as it shipped: somebody meeting GK3 for the
        // first time should not be offered a verb its authors switched off.
        FrontEnd front = Front();

        front.Choose(new MenuAction("options"));
        front.Choose(new MenuAction("gameplay"));

        Assert.False(front.Settings.EasterEggs);
        Assert.Equal("Off", Row(front, "eggs").Value);

        front.Choose(new MenuAction("eggs"));

        Assert.True(front.Settings.EasterEggs);
        Assert.Equal("On", Row(front, "eggs").Value);
    }

    [Fact]
    public void Play_quit_and_resume_are_the_only_things_that_leave_the_menu()
    {
        Assert.Equal(FrontEndOutcome.Play, Front().Choose(new MenuAction("play")));
        Assert.Equal(FrontEndOutcome.Quit, Front().Choose(new MenuAction("quit")));
        Assert.Equal(FrontEndOutcome.Resume, Front(inGame: true).Choose(new MenuAction("resume")));

        Assert.Equal(FrontEndOutcome.Stay, Front().Choose(new MenuAction("video")));
        Assert.Equal(FrontEndOutcome.Stay, Front().Choose(MenuAction.None));
    }

    [Fact]
    public void Nothing_is_written_until_something_changes()
    {
        string path = Path.Combine(
            Path.GetTempPath(), "gk3r-settings-" + Guid.NewGuid().ToString("N") + ".json");

        try
        {
            FrontEnd front = Front();

            // Walking through the pages is not a change.
            front.Choose(new MenuAction("options"));
            front.Choose(new MenuAction("audio"));

            Assert.False(front.Dirty);
            Assert.False(front.Commit(path));
            Assert.False(File.Exists(path));

            front.Choose(new MenuAction("dialogue", Fraction: 0.4f));

            Assert.True(front.Dirty);
            Assert.True(front.Commit(path));
            Assert.False(front.Dirty);

            Assert.Equal(0.4f, Settings.Load(path).DialogueVolume, 3);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Settings_survive_being_written_and_read_back()
    {
        string path = Path.Combine(
            Path.GetTempPath(), "gk3r-settings-" + Guid.NewGuid().ToString("N") + ".json");

        try
        {
            var settings = new Settings
            {
                MasterVolume = 0.3f,
                Picture = PictureQuality.Original,
                Speakers = SpeakerLayout.Surround51,
                HurryFactor = 3.25f,
                Cinematics = false,
                PlayIntro = false,
            };

            Assert.True(settings.Save(path));

            Settings read = Settings.Load(path);

            Assert.Equal(0.3f, read.MasterVolume, 3);
            Assert.Equal(PictureQuality.Original, read.Picture);
            Assert.Equal(SpeakerLayout.Surround51, read.Speakers);
            Assert.Equal(3.25f, read.HurryFactor, 2);
            Assert.False(read.Cinematics);
            Assert.False(read.PlayIntro);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_settings_file_that_is_nonsense_costs_the_settings_and_not_the_game()
    {
        string path = Path.Combine(
            Path.GetTempPath(), "gk3r-settings-" + Guid.NewGuid().ToString("N") + ".json");

        try
        {
            // Somebody has edited it by hand, which they are entitled to do.
            File.WriteAllText(path, "{ \"MasterVolume\": 40, \"HurryFactor\": -3, \"Picture\": 99 }");

            Settings read = Settings.Load(path);

            // Clamped to the nearest thing that is allowed rather than thrown away: forty
            // means "as loud as it goes" and a negative pace means the slowest there is.
            Assert.Equal(1f, read.MasterVolume, 3);
            Assert.Equal(1f, read.HurryFactor, 2);

            // Except an enumeration, where there is no nearest: 99 is not a picture quality
            // at all, so it falls back to the default.
            Assert.Equal(PictureQuality.High, read.Picture);

            // And something that is not JSON at all.
            File.WriteAllText(path, "not a settings file");
            Assert.Equal(1f, Settings.Load(path).MasterVolume, 3);

            // And one that is not there.
            Assert.Equal(1f, Settings.Load(path + ".missing").MasterVolume, 3);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void The_levels_reach_the_mixer()
    {
        var device = new Levels();

        new Settings
        {
            MasterVolume = 0.5f,
            MusicVolume = 0.25f,
            AmbienceVolume = 0.75f,
            EffectsVolume = 0.1f,
            DialogueVolume = 0.9f,
        }.ApplyTo(device);

        Assert.Equal(0.5f, device.Gains[AudioBus.Master], 3);
        Assert.Equal(0.25f, device.Gains[AudioBus.Music], 3);
        Assert.Equal(0.75f, device.Gains[AudioBus.Ambience], 3);

        // Speech is played on the centred bus, so a dialogue slider that only set the
        // in-world one would move nothing at all.
        Assert.Equal(0.9f, device.Gains[AudioBus.DialogueCentered], 3);
        Assert.Equal(0.9f, device.Gains[AudioBus.DialogueInWorld], 3);

        // Foley is effects by another name; a player turning effects down means both.
        Assert.Equal(0.1f, device.Gains[AudioBus.Effects], 3);
        Assert.Equal(0.1f, device.Gains[AudioBus.Foley], 3);

        // Nothing may be left unreachable: a bus with no slider is a sound that cannot be
        // turned down.
        foreach (AudioBus bus in Enum.GetValues<AudioBus>())
        {
            Assert.True(device.Gains.ContainsKey(bus), $"{bus} was never set");
        }

        // And no device at all is not an error: the game runs silent.
        new Settings().ApplyTo(null);
    }

    /// <summary>An audio device that only remembers what it was told.</summary>
    private sealed class Levels : IAudioBackend
    {
        public Dictionary<AudioBus, float> Gains { get; } = [];

        public SpeakerLayout RequestedLayout => SpeakerLayout.Stereo;

        public SpeakerLayout ActualLayout => SpeakerLayout.Stereo;

        public int Playing => 0;

        public void SetBusGain(AudioBus bus, float gain) => Gains[bus] = gain;

        public void SetVoiceGain(AudioVoice voice, float gain)
        {
        }

        public AudioVoice Play(
            WavFile sound, AudioBus bus, bool repeat = false, AudioPlacement? at = null) =>
            AudioVoice.None;

        public void Move(AudioVoice voice, Vector3 position)
        {
        }

        public void Listen(Vector3 position, Vector3 forward, Vector3 up)
        {
        }

        public void Silence(AudioVoice voice)
        {
        }

        public void StopBus(AudioBus bus)
        {
        }

        public bool IsPlaying(AudioVoice voice) => false;

        public void Update()
        {
        }

        public void Dispose()
        {
        }
    }
}

/// <summary>
/// Tests for the drawn menu: what you click is what you saw.
/// </summary>
public sealed class MenuPageTests
{
    /// <summary>A font of fixed four-pixel characters.</summary>
    internal static OverlayAtlas Font()
    {
        const int Width = 128;
        const int Height = 12;
        byte[] pixels = new byte[Width * Height * 4];

        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i + 3] = 255;
        }

        for (int x = 1; x < Width; x += 4)
        {
            pixels[x * 4] = 255;
        }

        var sheet = new DecodedImage(Width, Height, pixels, HasAlpha: false, "test");
        string characters = new(
            [.. "ABCDEFGHIJKLMNOPQRSTUVWXYZ abcdefghijklmnopqrstuvwxyz".Distinct().Take(31)]);

        return OverlayAtlas.Build(
            FontFile.Parse($"Font={characters}\n", sheet, "TEST", new DiagnosticBag()));
    }

    private static MenuPage Page() => new(new Overlay(Font()));

    [Fact]
    public void The_timeblock_card_is_lettered_large_and_puts_the_size_back()
    {
        // Two hours of the story have just gone by and the point of the card is that it
        // cannot be missed, so it is not drawn at the size a list of settings is read at.
        MenuPage page = Page();

        int ordinary = page.Overlay.LineHeight;

        page.Announcing("Day 1, 12pm - 2pm", 1280, 720);

        Assert.NotEmpty(page.Overlay.Quads);

        // And the page is left as it was: the same overlay draws the menu next.
        Assert.Equal(ordinary, page.Overlay.LineHeight);

        // The letters themselves were bigger than that while they were drawn — a fifteenth
        // of 720 lines is 48, against a sheet cut at twelve.
        Assert.True(
            page.Overlay.Quads.Max(q => q.Destination.W) > ordinary,
            "the card's lettering should be taller than a menu row");
    }

    private static IReadOnlyList<MenuItem> Items() =>
    [
        MenuItem.Button("play", "New Game"),
        MenuItem.Label("a heading nobody can land on"),
        MenuItem.Slider("music", "Music", 0.5f, "50%"),
        MenuItem.Button("quit", "Quit"),
    ];

    [Fact]
    public void A_row_can_be_clicked_where_it_was_drawn()
    {
        MenuPage page = Page();
        IReadOnlyList<MenuItem> items = Items();

        page.Build("Title", items, 800, 600, Vector2.Zero);

        // Walk down the panel and collect what each height reports, which must be the rows
        // in the order they were drawn with the label skipped.
        var found = new List<string>();

        for (int y = 0; y < 600; y++)
        {
            MenuAction hit = page.Click(new Vector2(400, y), items);

            if (hit.Happened && (found.Count == 0 || found[^1] != hit.Id))
            {
                found.Add(hit.Id);
            }
        }

        Assert.Equal(["play", "music", "quit"], found);
    }

    [Fact]
    public void Moving_the_selection_skips_what_cannot_be_landed_on()
    {
        MenuPage page = Page();
        IReadOnlyList<MenuItem> items = Items();

        page.Build("Title", items, 800, 600, Vector2.Zero);
        page.Reset(items);

        Assert.Equal(0, page.Index);

        // Over the label, not onto it.
        page.Move(items, 1);
        Assert.Equal(2, page.Index);

        page.Move(items, 1);
        Assert.Equal(3, page.Index);

        // And round, rather than stopping at the end.
        page.Move(items, 1);
        Assert.Equal(0, page.Index);

        page.Move(items, -1);
        Assert.Equal(3, page.Index);
    }

    [Fact]
    public void Clicking_along_a_slider_asks_for_that_position()
    {
        MenuPage page = Page();
        IReadOnlyList<MenuItem> items = Items();

        page.Build("Title", items, 800, 600, Vector2.Zero);

        // Find the slider's row by walking down until the click reports it.
        float y = Enumerable.Range(0, 600)
            .First(row => page.Click(new Vector2(400, row), items).Id == "music");

        // Walk across the row and keep what each position asks for. Only the panel counts:
        // the window is wider than the page, and a click beside it is not a drag.
        float[] asked = [.. Enumerable.Range(0, 800)
            .Select(x => page.Click(new Vector2(x, y), items))
            .Where(hit => hit.Id == "music")
            .Select(hit => hit.Fraction)];

        Assert.NotEmpty(asked);

        // Both ends are reachable, and nothing in between goes backwards or leaves the
        // range — dragging to the far left has to mean off and the far right has to mean
        // full, or the last percent of a volume cannot be set.
        Assert.Equal(0f, asked[0], 2);
        Assert.Equal(1f, asked[^1], 2);
        Assert.All(asked, f => Assert.InRange(f, 0f, 1f));

        for (int i = 1; i < asked.Length; i++)
        {
            Assert.True(asked[i] >= asked[i - 1], "the bar ran backwards");
        }
    }

    [Fact]
    public void The_page_stays_inside_the_window()
    {
        MenuPage page = Page();

        page.Build("A rather long title for a narrow window", Items(), 320, 240, Vector2.Zero);

        foreach (OverlayQuad quad in page.Overlay.Quads)
        {
            Assert.True(quad.Destination.X >= -0.5f, "a rectangle started left of the window");
            Assert.True(
                quad.Destination.X + quad.Destination.Z <= 320.5f,
                "a rectangle ran off the right of the window");
        }
    }

    [Fact]
    public void A_page_goes_where_it_is_put_and_no_further()
    {
        MenuPage page = Page();
        IReadOnlyList<MenuItem> items = Items();

        page.Behind = MenuBehind.Picture;
        page.Down = 0.72f;
        page.Across = 0.17f;
        page.Build("", items, 1280, 720, Vector2.Zero);

        Vector4 panel = page.Overlay.Quads[0].Destination;

        // Left and low, which is where the title art has room for it.
        Assert.InRange(panel.X + (panel.Z / 2f), 1280 * 0.1f, 1280 * 0.25f);
        Assert.InRange(panel.Y + (panel.W / 2f), 720 * 0.6f, 720 * 0.85f);

        // And asked for the impossible, it stays on the screen rather than half off it.
        page.Down = 1f;
        page.Across = 0f;
        page.Build("", items, 1280, 720, Vector2.Zero);

        panel = page.Overlay.Quads[0].Destination;

        Assert.True(panel.X >= 0, "the page ran off the left of the window");
        Assert.True(panel.Y + panel.W <= 720, "the page ran off the bottom of the window");
    }

    [Fact]
    public void Drawing_the_page_again_does_not_pile_it_up()
    {
        MenuPage page = Page();
        IReadOnlyList<MenuItem> items = Items();

        page.Build("Title", items, 800, 600, Vector2.Zero);
        int once = page.Overlay.Quads.Count;

        for (int i = 0; i < 20; i++)
        {
            page.Build("Title", items, 800, 600, Vector2.Zero);
        }

        // A menu is drawn every frame it is up. Without a clear it is the same page sixty
        // times over a second later, which costs the frame rate and nothing on screen says
        // why.
        Assert.Equal(once, page.Overlay.Quads.Count);
    }

    [Fact]
    public void Behind_the_page_is_either_the_room_or_a_screen_of_its_own()
    {
        MenuPage page = Page();

        page.Behind = MenuBehind.Room;
        page.Build("Title", Items(), 800, 600, Vector2.Zero);

        // Paused over a room: one wash over the whole window, and see-through, or the
        // player cannot see where they were.
        OverlayQuad wash = page.Overlay.Quads[0];

        Assert.Equal(new Vector4(0, 0, 800, 600), wash.Destination);
        Assert.InRange(wash.Color.W, 0.2f, 0.95f);

        page.Behind = MenuBehind.Nothing;
        page.Build("Title", Items(), 800, 600, Vector2.Zero);

        // The first menu of all has no room behind it, so the window has to be covered
        // outright rather than left as whatever the swapchain was cleared to.
        float opaque = page.Overlay.Quads
            .Where(q => q.Color.W >= 1f && q.Destination.X <= 0 && q.Destination.Z >= 800)
            .Sum(q => q.Destination.W);

        Assert.True(opaque >= 600, $"only {opaque} lines of an 600-line window were covered");
    }

    [Fact]
    public void A_film_says_how_to_skip_it_and_then_shows_the_holding()
    {
        MenuPage page = Page();

        page.Skipping("Hold to skip", 0f, 800, 600);

        // Not holding: the words, low down and out of the way of the film.
        Assert.NotEmpty(page.Overlay.Quads);
        Assert.All(page.Overlay.Quads, q => Assert.True(
            q.Destination.Y > 450, "the hint was drawn over the middle of the film"));

        float words = page.Overlay.Quads.Count;

        page.Skipping("Hold to skip", 0.5f, 800, 600);

        // Holding: a bar, half filled. A hold with nothing on screen is indistinguishable
        // from a hold that is not working.
        Assert.Equal(2, page.Overlay.Quads.Count);
        Assert.True(words > 2, "the words were not drawn as text");

        float track = page.Overlay.Quads[0].Destination.Z;
        float fill = page.Overlay.Quads[1].Destination.Z;

        Assert.Equal(track / 2f, fill, 1);

        page.Skipping("Hold to skip", 1f, 800, 600);
        Assert.Equal(track, page.Overlay.Quads[1].Destination.Z, 1);

        // And past the end, which is what one frame too many of holding gives.
        page.Skipping("Hold to skip", 1.4f, 800, 600);
        Assert.Equal(track, page.Overlay.Quads[1].Destination.Z, 1);
    }

    [Fact]
    public void Pressing_a_choice_steps_it_forward()
    {
        MenuPage page = Page();
        IReadOnlyList<MenuItem> items = [MenuItem.Choice("picture", "Lighting", "Shadows")];

        page.Build("Title", items, 800, 600, Vector2.Zero);
        page.Reset(items);

        // Enter on a choice has to mean something, and the only sensible thing it can mean
        // is the same as the right arrow.
        Assert.Equal(new MenuAction("picture", 1), page.Chose(items));
        Assert.Equal(new MenuAction("picture", -1), page.Chose(items, -1));
    }
}
