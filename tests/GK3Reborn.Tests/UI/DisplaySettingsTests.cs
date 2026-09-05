using System.Numerics;
using GK3Reborn.Game;
using GK3Reborn.Platform;
using GK3Reborn.Rendering;
using GK3Reborn.Rendering.Upscaling;
using GK3Reborn.UI;
using Xunit;

namespace GK3Reborn.Tests.UI;

/// <summary>
/// Tests for the two picture pages: the display, and what upscales it.
/// </summary>
/// <remarks>
/// The same argument the rest of the front end is tested on. Every row here reaches a real
/// field, and a row that quietly does not is exactly the kind of thing that survives every
/// amount of looking at the screen — which is why the check is that the setting moved and
/// not that the row drew.
/// </remarks>
public sealed class DisplaySettingsTests
{
    private static FrontEnd Front(Settings? settings = null) => new(settings ?? new Settings());

    /// <summary>
    /// The row with a given identifier, or null.
    /// </summary>
    /// <remarks>
    /// Cast to a nullable before the search, because a row is a value type and
    /// <c>FirstOrDefault</c> on one hands back a default-constructed row rather than
    /// nothing — whose identifier is the empty string, which is what every label on the
    /// page has. Written the obvious way, "is there a paper white row" answered yes on
    /// every page that has an explanation on it.
    /// </remarks>
    private static MenuItem? Row(FrontEnd front, string id) =>
        front.Items.Cast<MenuItem?>().FirstOrDefault(i => i!.Value.Id == id);

    private static FrontEnd On(FrontEndPage page, Settings? settings = null)
    {
        FrontEnd front = Front(settings);
        front.Show(page);

        return front;
    }

    private static FrontEnd Step(FrontEnd front, string id, int by = 1)
    {
        front.Choose(new MenuAction(id, by));
        return front;
    }

    [Fact]
    public void Every_section_is_one_click_from_every_other_and_back_is_the_way_out()
    {
        // What the sidebar buys, stated as a test: the settings used to be five pages
        // reached one at a time from a menu of five buttons, so comparing a Picture row
        // against a Display row was four keystrokes. Now every section is one.
        FrontEnd front = On(FrontEndPage.Video);

        foreach (MenuSection section in FrontEnd.Sections)
        {
            front.Choose(new MenuAction("tab:" + section.Id));
            Assert.True(front.OnSettings, $"{section.Id} is not a settings section");
            Assert.Equal(section.Id, FrontEnd.Sections[front.Section].Id);
        }

        // And out of the settings altogether, not up to a menu of five buttons that is no
        // longer there.
        front.Back();
        Assert.Equal(FrontEndPage.Main, front.Page);
    }

    [Fact]
    public void The_settings_open_again_where_they_were_left()
    {
        FrontEnd front = On(FrontEndPage.Main);

        front.Choose(new MenuAction("options"));
        front.Choose(new MenuAction("tab:audio"));
        Assert.Equal(FrontEndPage.Audio, front.Page);

        front.Back();
        Assert.Equal(FrontEndPage.Main, front.Page);

        front.Choose(new MenuAction("options"));
        Assert.Equal(FrontEndPage.Audio, front.Page);
    }

    [Fact]
    public void The_shoulder_buttons_walk_the_sections_round()
    {
        FrontEnd front = On(FrontEndPage.Video);

        // Round rather than stopping, so a player holding one down never has to notice
        // where they started.
        for (int i = 0; i < FrontEnd.Sections.Count; i++)
        {
            Assert.True(front.StepSection(1));
        }

        Assert.Equal(FrontEndPage.Video, front.Page);

        front.StepSection(-1);
        Assert.Equal(FrontEndPage.Controls, front.Page);

        // And nothing at all where there are no sections to step.
        FrontEnd main = On(FrontEndPage.Main);
        Assert.False(main.StepSection(1));
        Assert.Equal(FrontEndPage.Main, main.Page);
    }

    [Fact]
    public void The_window_steps_through_its_three_modes()
    {
        FrontEnd front = On(FrontEndPage.Display);

        Assert.Equal(WindowMode.Windowed, front.Settings.Display);

        Step(front, "window");
        Assert.Equal(WindowMode.BorderlessFullscreen, front.Settings.Display);

        Step(front, "window");
        Assert.Equal(WindowMode.ExclusiveFullscreen, front.Settings.Display);

        // Round rather than stopping at the end, like every other choice on these pages.
        Step(front, "window");
        Assert.Equal(WindowMode.Windowed, front.Settings.Display);

        Step(front, "window", -1);
        Assert.Equal(WindowMode.ExclusiveFullscreen, front.Settings.Display);
    }

    [Fact]
    public void A_borderless_window_has_no_size_to_choose_and_the_row_is_dead()
    {
        // Said by the row rather than by a sentence under it: a borderless window is the
        // size of the monitor by definition, so the row reads that way and cannot be
        // landed on. The page used to spend three lines explaining the same thing.
        FrontEnd front = On(
            FrontEndPage.Display,
            new Settings
            {
                Display = WindowMode.BorderlessFullscreen,
                DisplayWidth = 1280,
                DisplayHeight = 720,
            });

        MenuItem? row = Row(front, "size");

        Assert.NotNull(row);
        Assert.False(row!.Value.Selectable);
        Assert.Equal("The monitor's own", row.Value.Value);

        // And the size in the file is remembered rather than cleared: it is what choosing
        // windowed again goes back to.
        Assert.Equal(1280, front.Settings.DisplayWidth);

        FrontEnd windowed = On(
            FrontEndPage.Display,
            new Settings { DisplayWidth = 1280, DisplayHeight = 720 });

        Assert.True(Row(windowed, "size")!.Value.Selectable);
        Assert.Equal("1280x720", Row(windowed, "size")!.Value.Value);
    }

    [Fact]
    public void No_settings_page_explains_what_its_own_rows_do()
    {
        // A page read by somebody looking for one thing should not make them scroll past a
        // paragraph under every row to find it. What is allowed to stay is what the player
        // cannot see for themselves: a runtime that is missing, a colour space the display
        // refused, a setting that waits for the next door or the next start.
        // Picture carries the upscaling rows now, so it is allowed the two lines those
        // rows were allowed on a page of their own: which file is missing, and why a row
        // is dead.
        //
        // Playing earns its one line the same way Sound earns its one: the language row
        // takes effect at the next start, and a player who changes it and hears the same
        // voices would reasonably conclude the row is broken. On an installation with only
        // English the same line says why the row will not step, which is the other thing
        // the player cannot see for themselves.
        (FrontEndPage Page, int Most)[] pages =
        [
            (FrontEndPage.Video, 3),
            (FrontEndPage.Display, 0),
            (FrontEndPage.Audio, 1),
            (FrontEndPage.Gameplay, 1),
            (FrontEndPage.Controls, 1),
        ];

        foreach ((FrontEndPage page, int most) in pages)
        {
            int labels = On(page).Items.Count(i => i.Kind == MenuItemKind.Label);

            Assert.True(
                labels <= most,
                $"{page} draws {labels} lines of prose, and {most} is the most it may");
        }
    }

    [Fact]
    public void The_resolution_is_one_decision_and_steps_as_one()
    {
        FrontEnd front = On(FrontEndPage.Display);

        // Nought and nought is the monitor's own, which is where somebody who has never
        // touched the row already is.
        Assert.Equal(0, front.Settings.DisplayWidth);
        Assert.Equal("The monitor's own", Row(front, "size")?.Value);

        Step(front, "size");

        Assert.Equal(1280, front.Settings.DisplayWidth);
        Assert.Equal(720, front.Settings.DisplayHeight);
        Assert.Equal("1280x720", Row(front, "size")?.Value);

        // And backwards from the first entry wraps to the last rather than stopping.
        FrontEnd back = Step(On(FrontEndPage.Display), "size", -1);
        Assert.Equal(3840, back.Settings.DisplayWidth);
    }

    [Fact]
    public void The_luminances_only_appear_once_there_is_somewhere_to_put_them()
    {
        // Four rows about candelas on a page with no HDR to spend them on is how a settings
        // screen teaches somebody that rows can be dead.
        FrontEnd off = On(FrontEndPage.Display);

        Assert.Null(Row(off, "paperwhite"));
        Assert.Null(Row(off, "sun"));
        Assert.NotNull(Row(off, "tonemap"));

        FrontEnd on = On(FrontEndPage.Display, new Settings { HighDynamicRange = true });

        Assert.NotNull(Row(on, "paperwhite"));
        Assert.NotNull(Row(on, "peak"));
        Assert.NotNull(Row(on, "sun"));
        Assert.NotNull(Row(on, "lights"));

        // And the SDR tone curve is not offered where it means nothing.
        Assert.Null(Row(on, "tonemap"));
    }

    [Fact]
    public void The_page_says_whether_the_display_actually_took_the_colour_space()
    {
        // Asking for HDR on a monitor in SDR mode changes nothing, and a page that shows
        // the switch on and says nothing else has told the player their display is the
        // problem in the least useful way available.
        var settings = new Settings { HighDynamicRange = true };

        FrontEnd refused = On(FrontEndPage.Display, settings);
        refused.HighDynamicRangeActive = false;

        Assert.Contains(
            refused.Items,
            i => i.Text.Contains("did not offer it", StringComparison.Ordinal));

        FrontEnd taken = On(FrontEndPage.Display, settings);
        taken.HighDynamicRangeActive = true;

        Assert.Contains(
            taken.Items,
            i => i.Text.Contains("took it", StringComparison.Ordinal));
    }

    [Fact]
    public void A_luminance_slider_moves_the_luminance_it_is_labelled_with()
    {
        FrontEnd front = On(FrontEndPage.Display, new Settings { HighDynamicRange = true });

        float before = front.Settings.SunNits;

        front.Choose(new MenuAction("sun", 1));

        Assert.True(front.Settings.SunNits > before, "the sun should have brightened");
        Assert.Equal(front.Settings.PeakNits, On(FrontEndPage.Display).Settings.PeakNits);

        // Rounded to ten candelas: a slider that reads 843 nits pretends to a precision
        // nobody's eye or monitor has.
        Assert.Equal(0f, front.Settings.SunNits % 10f);
    }

    [Fact]
    public void The_text_size_row_moves_the_text_size_and_stops_at_both_ends()
    {
        FrontEnd front = On(FrontEndPage.Display);

        Assert.NotNull(Row(front, "textsize"));
        Assert.Equal(1f, front.Settings.TextScale);

        Step(front, "textsize", -1);
        Assert.True(front.Settings.TextScale < 1f, "the letters should have got smaller");

        // Held against both ends rather than stepped once: a slider that runs off the end
        // of what the settings will keep is a row whose reading and whose effect disagree
        // the moment the file is written.
        for (int i = 0; i < 40; i++)
        {
            Step(front, "textsize", -1);
        }

        Assert.Equal(Settings.SmallestText, front.Settings.TextScale, 3);

        for (int i = 0; i < 80; i++)
        {
            Step(front, "textsize", 1);
        }

        Assert.Equal(Settings.LargestText, front.Settings.TextScale, 3);

        // And what it says is what it is.
        Assert.Equal("160%", Row(front, "textsize")!.Value.Value);
    }

    [Fact]
    public void The_text_size_can_be_put_back_to_the_automatic_one()
    {
        // Rounded to a twentieth, so stepping lands on whole fives and a player who has
        // dragged their menu to something unreadable can get back to where they started.
        // A slider that stopped at 97% would leave them unable to undo it.
        FrontEnd front = On(FrontEndPage.Display);

        front.Choose(new MenuAction("textsize", 0, 0f));
        Assert.Equal(Settings.SmallestText, front.Settings.TextScale, 3);

        for (int i = 0; i < 30 && MathF.Abs(front.Settings.TextScale - 1f) > 0.001f; i++)
        {
            Step(front, "textsize", 1);
        }

        Assert.Equal(1f, front.Settings.TextScale, 3);
        Assert.Equal("100%", Row(front, "textsize")!.Value.Value);
    }

    [Fact]
    public void A_hand_written_text_size_is_clamped_rather_than_refused()
    {
        // A settings file is a text file somebody may edit, and a typed 40x is not a reason
        // to start the game with no interface.
        Assert.Equal(
            Settings.LargestText, new Settings { TextScale = 40f }.Sane().TextScale, 3);

        Assert.Equal(
            Settings.SmallestText, new Settings { TextScale = 0f }.Sane().TextScale, 3);

        Assert.Equal(1f, new Settings { TextScale = float.NaN }.Sane().TextScale, 3);
    }

    [Fact]
    public void The_upscaler_row_offers_only_what_this_machine_can_run()
    {
        // DLSS is NVIDIA's and runs on nothing else. A permanently unavailable row reads as
        // something the game has failed to do rather than as something the hardware cannot.
        FrontEnd front = On(FrontEndPage.Video);
        front.Offered = [UpscalerKind.Off, UpscalerKind.Spatial, UpscalerKind.Fsr];

        var seen = new List<UpscalerKind>();

        for (int i = 0; i < 6; i++)
        {
            front.Choose(new MenuAction("upscaler", 1));
            seen.Add(front.Settings.Upscaler);
        }

        Assert.DoesNotContain(UpscalerKind.Dlss, seen);
        Assert.Contains(UpscalerKind.Fsr, seen);
        Assert.Contains(UpscalerKind.Spatial, seen);
    }

    [Fact]
    public void Fsr_is_offered_on_every_card_and_dlss_on_an_nvidia_one()
    {
        FrontEnd front = On(FrontEndPage.Video);

        // The default, before anybody has narrowed it, is everything.
        var seen = new List<UpscalerKind>();

        for (int i = 0; i < 8; i++)
        {
            front.Choose(new MenuAction("upscaler", 1));
            seen.Add(front.Settings.Upscaler);
        }

        Assert.Contains(UpscalerKind.Dlss, seen);
        Assert.Contains(UpscalerKind.Fsr, seen);
    }

    [Fact]
    public void The_quality_rows_only_appear_when_something_is_upscaling()
    {
        FrontEnd off = On(FrontEndPage.Video);

        Assert.Null(Row(off, "ratio"));
        Assert.Null(Row(off, "sharpen"));

        FrontEnd on = On(
            FrontEndPage.Video, new Settings { Upscaler = UpscalerKind.Spatial });

        Assert.NotNull(Row(on, "ratio"));
        Assert.NotNull(Row(on, "sharpen"));
        Assert.NotNull(Row(on, "sharpness"));

        // And the sharpness only when there is sharpening to set.
        FrontEnd blunt = On(
            FrontEndPage.Video,
            new Settings { Upscaler = UpscalerKind.Spatial, Sharpening = false });

        Assert.Null(Row(blunt, "sharpness"));
    }

    [Fact]
    public void The_dlss_rows_only_appear_for_dlss()
    {
        FrontEnd fsr = On(FrontEndPage.Video, new Settings { Upscaler = UpscalerKind.Fsr });

        Assert.Null(Row(fsr, "preset"));
        Assert.Null(Row(fsr, "reconstruction"));

        FrontEnd dlss = On(FrontEndPage.Video, new Settings { Upscaler = UpscalerKind.Dlss });

        Assert.NotNull(Row(dlss, "preset"));
        Assert.NotNull(Row(dlss, "reconstruction"));
    }

    [Fact]
    public void A_missing_runtime_says_which_files_and_where_they_go()
    {
        FrontEnd front = On(FrontEndPage.Video, new Settings { Upscaler = UpscalerKind.Fsr });

        string text = string.Join(" ", front.Items.Select(i => i.Text));

        Assert.Contains("amd_fidelityfx_vk.dll", text, StringComparison.Ordinal);
        Assert.Contains("libs", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Ray_reconstruction_is_disabled_with_a_reason_rather_than_hidden()
    {
        FrontEnd front = On(FrontEndPage.Video, new Settings { Upscaler = UpscalerKind.Dlss });

        front.DlssRayReconstruction = false;
        front.DlssRayReconstructionNote = "the plugin is a variant this build cannot drive";

        MenuItem? row = Row(front, "reconstruction");

        Assert.NotNull(row);
        Assert.False(row!.Value.Enabled);

        Assert.Contains(
            front.Items,
            i => i.Text.Contains("cannot drive", StringComparison.Ordinal));

        front.DlssRayReconstruction = true;
        Assert.True(Row(front, "reconstruction")!.Value.Enabled);
    }

    [Fact]
    public void Frame_generation_is_disabled_until_a_runtime_can_do_it()
    {
        FrontEnd front = On(FrontEndPage.Video, new Settings { Upscaler = UpscalerKind.Dlss });

        front.DlssFrameGeneration = false;
        Assert.False(Row(front, "generation")!.Value.Enabled);

        front.DlssFrameGeneration = true;
        Assert.True(Row(front, "generation")!.Value.Enabled);

        front.Choose(new MenuAction("generation", 1));
        Assert.Equal(FrameGeneration.Interpolated, front.Settings.FrameGeneration);
    }

    [Fact]
    public void The_page_says_the_two_resolutions_it_will_draw_at()
    {
        FrontEnd front = On(
            FrontEndPage.Video,
            new Settings { Upscaler = UpscalerKind.Fsr, UpscalerQuality = UpscalerQuality.Performance });

        front.Window = (2560, 1440);

        Assert.Contains(
            front.Items,
            i => i.Text == "1280x720 to 2560x1440");
    }

    [Fact]
    public void Every_row_on_both_pages_reaches_a_field()
    {
        // The rule the rest of the front end is held to: a setting with no destination is a
        // promise the interface cannot keep.
        var settings = new Settings
        {
            HighDynamicRange = true,
            Upscaler = UpscalerKind.Dlss,
        };

        foreach (FrontEndPage page in (FrontEndPage[])[FrontEndPage.Display, FrontEndPage.Video])
        {
            FrontEnd front = On(page, settings);
            front.DlssRayReconstruction = true;
            front.DlssFrameGeneration = true;

            foreach (MenuItem item in front.Items.Where(i => i.Selectable && i.Id != "back"))
            {
                FrontEnd one = On(page, settings);
                one.DlssRayReconstruction = true;
                one.DlssFrameGeneration = true;

                Settings before = one.Settings;
                one.Choose(new MenuAction(item.Id, 1));

                Assert.True(
                    one.Settings != before,
                    $"the row {item.Id} on {page} changed nothing");
            }
        }
    }

    [Fact]
    public void The_settings_become_the_two_plans_the_renderer_reads()
    {
        var settings = new Settings
        {
            Upscaler = UpscalerKind.Fsr,
            UpscalerQuality = UpscalerQuality.Balanced,
            Sharpening = false,
            HighDynamicRange = true,
            PaperWhiteNits = 250f,
            SunNits = 1000f,
        };

        UpscalePlan upscaling = settings.Upscaling;

        Assert.Equal(UpscalerKind.Fsr, upscaling.Kind);
        Assert.Equal(UpscalerQuality.Balanced, upscaling.Quality);
        Assert.False(upscaling.Sharpen);

        // Not asserted from the file: whether the colour is high dynamic range is a fact
        // about the output chain the renderer owns.
        Assert.False(upscaling.HighDynamicRange);

        OutputPlan output = settings.Output;

        Assert.True(output.HighDynamicRange);
        Assert.Equal(250f, output.PaperWhiteNits);
        Assert.Equal(4f, output.SunGain);
    }

    [Fact]
    public void The_new_settings_survive_being_written_and_read_back()
    {
        string path = Path.Combine(
            Path.GetTempPath(), "gk3reborn-display-" + Guid.NewGuid().ToString("N") + ".json");

        var written = new Settings
        {
            Display = WindowMode.BorderlessFullscreen,
            DisplayWidth = 2560,
            DisplayHeight = 1440,
            VerticalSync = false,
            Upscaler = UpscalerKind.Dlss,
            UpscalerQuality = UpscalerQuality.UltraQuality,
            Sharpness = 0.25f,
            FrameGeneration = FrameGeneration.Interpolated,
            RayReconstruction = false,
            DlssPreset = 11,
            HighDynamicRange = true,
            HdrTransfer = HdrTransfer.ExtendedLinear,
            ToneMapping = ToneMapping.Filmic,
            PaperWhiteNits = 240f,
            PeakNits = 1400f,
            SunNits = 900f,
            LightNits = 1200f,
        };

        try
        {
            Assert.True(written.Save(path));

            Settings read = Settings.Load(path);

            Assert.Equal(written.Display, read.Display);
            Assert.Equal(written.DisplayWidth, read.DisplayWidth);
            Assert.Equal(written.VerticalSync, read.VerticalSync);
            Assert.Equal(written.Upscaler, read.Upscaler);
            Assert.Equal(written.UpscalerQuality, read.UpscalerQuality);
            Assert.Equal(written.Sharpness, read.Sharpness);
            Assert.Equal(written.FrameGeneration, read.FrameGeneration);
            Assert.Equal(written.RayReconstruction, read.RayReconstruction);
            Assert.Equal(written.DlssPreset, read.DlssPreset);
            Assert.Equal(written.HighDynamicRange, read.HighDynamicRange);
            Assert.Equal(written.HdrTransfer, read.HdrTransfer);
            Assert.Equal(written.ToneMapping, read.ToneMapping);
            Assert.Equal(written.PaperWhiteNits, read.PaperWhiteNits);
            Assert.Equal(written.SunNits, read.SunNits);

            // And the derived views are not in the file: they are the same decisions said
            // twice, and a hand-edited copy of one of them would silently do nothing.
            string json = File.ReadAllText(path);

            Assert.DoesNotContain("\"Upscaling\"", json, StringComparison.Ordinal);
            Assert.DoesNotContain("\"Output\"", json, StringComparison.Ordinal);
            Assert.DoesNotContain("\"Quality\"", json, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

/// <summary>Tests for where a jittered camera puts the picture.</summary>
public sealed class CameraJitterTests
{
    [Fact]
    public void An_unjittered_camera_projects_exactly_as_it_always_did()
    {
        // Every reference image in the corpus was taken through this matrix, and nothing
        // that does not upscale sets a jitter at all.
        var camera = new Camera();

        Assert.Equal(camera.ProjectionWithoutJitter(1.6f), camera.Projection(1.6f));
    }

    [Fact]
    public void The_jitter_moves_the_sample_point_in_proportion_to_depth()
    {
        // Added to the z-to-x and z-to-y terms rather than to the translation row, so the
        // offset is proportional to w. A constant added there would move a wall by a pixel
        // and a distant hillside by a hundred.
        var camera = new Camera { Jitter = new Vector2(0.01f, -0.02f) };

        Matrix4x4 steady = camera.ProjectionWithoutJitter(1.6f);
        Matrix4x4 jittered = camera.Projection(1.6f);

        Assert.Equal(steady.M31 + 0.01f, jittered.M31, 6);
        Assert.Equal(steady.M32 - 0.02f, jittered.M32, 6);

        // And nothing else moves.
        Assert.Equal(steady.M11, jittered.M11);
        Assert.Equal(steady.M22, jittered.M22);
        Assert.Equal(steady.M41, jittered.M41);
        Assert.Equal(steady.M42, jittered.M42);
    }

    [Fact]
    public void A_near_point_and_a_far_point_move_by_the_same_number_of_pixels()
    {
        // The property the whole arrangement rests on: a jitter is a screen-space offset,
        // and it has to be the same offset for everything on the screen.
        var camera = new Camera { Position = Vector3.Zero, Target = new Vector3(0, 0, 10) };
        var jittered = new Camera
        {
            Position = camera.Position,
            Target = camera.Target,
            Jitter = new Vector2(0.01f, 0f),
        };

        foreach (float depth in (float[])[2f, 50f, 5000f])
        {
            var point = new Vector4(1f, 0.5f, depth, 1f);

            Vector4 before = Vector4.Transform(point, camera.View * camera.Projection(1.6f));
            Vector4 after = Vector4.Transform(point, jittered.View * jittered.Projection(1.6f));

            Assert.Equal(0.01f, (after.X / after.W) - (before.X / before.W), 4);
        }
    }
}
