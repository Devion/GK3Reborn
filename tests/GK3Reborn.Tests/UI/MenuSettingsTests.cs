using System.Numerics;
using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Formats.Ui;
using GK3Reborn.Foundation.Diagnostics;
using GK3Reborn.Game;
using GK3Reborn.Rendering;
using GK3Reborn.UI;
using Xunit;

namespace GK3Reborn.Tests.UI;

/// <summary>
/// Tests that the settings screen stays still as its sections are walked, and that a
/// slider is drawn as three separate things across its row.
/// </summary>
/// <remarks>
/// <para>
/// Both were reported together and both come from the same habit: the page measured itself
/// against the section that happened to be showing. Fitted to its own rows the panel was a
/// different size on every section and, being centred, it moved — so clicking a name in the
/// sidebar took the sidebar out from under the pointer. And a slider's row was measured as
/// a label and a reading with nothing between them, so the bar was drawn across the middle
/// of whatever width that came to, which on the Sound page was the middle of the words.
/// </para>
/// <para>
/// Checked through what the page says it drew and what a click on it does, rather than
/// against the layout arithmetic. A test that works the layout out for itself only proves
/// the two agree.
/// </para>
/// </remarks>
public sealed class MenuSettingsTests
{
    private const int Width = 1920;
    private const int Height = 1080;

    /// <summary>
    /// A font of fixed four-pixel characters, covering everything these pages say.
    /// </summary>
    /// <remarks>
    /// Its own rather than the one the other menu tests share, which carries the alphabet
    /// and four lower-case letters. What is being measured here is what fits beside what,
    /// and a font that measures "Music and cutscenes" as if it were "Mucacd" answers a
    /// question about a shorter page than the one the player has in front of them.
    /// </remarks>
    private static OverlayAtlas Font()
    {
        const string Characters =
            "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789 %.,:;()<>-+/'";

        // One marker per character and one to say where the last of them stops.
        const int Cell = 4;
        int width = Cell * (Characters.Length + 1);
        const int Height = 12;

        byte[] pixels = new byte[width * Height * 4];

        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i + 3] = 255;
        }

        for (int x = 1; x < width; x += Cell)
        {
            pixels[x * 4] = 255;
        }

        var sheet = new DecodedImage(width, Height, pixels, HasAlpha: false, "test");

        return OverlayAtlas.Build(
            FontFile.Parse($"Font={Characters}\n", sheet, "TEST", new DiagnosticBag()));
    }

    private static MenuPage Page() =>
        new(new Overlay(Font()))
        {
            Behind = MenuBehind.Picture,
            Sections = FrontEnd.Sections,
        };

    /// <summary>Draws one section of the settings screen, as the game draws it.</summary>
    private static IReadOnlyList<MenuItem> Show(
        MenuPage page, FrontEndPage which, int width = Width, int height = Height)
    {
        var front = new FrontEnd(new Settings());

        front.Show(which);

        IReadOnlyList<MenuItem> items = front.Items;

        page.Section = front.Section;
        page.Reset(items);
        page.Build(front.Title, items, width, height, Vector2.Zero);

        return items;
    }

    [Theory]
    [InlineData(FrontEndPage.Display)]
    [InlineData(FrontEndPage.Audio)]
    [InlineData(FrontEndPage.Gameplay)]
    [InlineData(FrontEndPage.Controls)]
    public void A_section_name_is_in_the_same_place_whichever_section_is_showing(
        FrontEndPage other)
    {
        // The complaint itself: the pointer is on a name in the sidebar, it is clicked, and
        // the name must still be under the pointer afterwards. Sound is six short rows and
        // Picture is thirty long ones, which is the pair that moved the panel furthest.
        MenuPage page = Page();

        Show(page, FrontEndPage.Video);

        Vector2 before = page.Aside("audio") ?? Vector2.Zero;

        Assert.NotEqual(Vector2.Zero, before);

        Show(page, other);

        Assert.Equal(before, page.Aside("audio") ?? Vector2.Zero);
    }

    [Theory]
    [InlineData(800, 600)]
    [InlineData(1024, 480)]
    [InlineData(1280, 720)]
    [InlineData(1920, 1080)]
    [InlineData(3840, 2160)]
    public void A_section_is_drawn_entirely_inside_the_window(int width, int height)
    {
        // What a panel that takes the room it is allowed rather than the room it needs has
        // to be held to: taking all of it and no more. Checked on the longest section there
        // is, which is the one with the least room to spare.
        MenuPage page = Page();

        Show(page, FrontEndPage.Controls, width, height);

        float left = float.MaxValue;
        float top = float.MaxValue;
        float right = float.MinValue;
        float bottom = float.MinValue;

        foreach (OverlayQuad quad in page.Overlay.Quads)
        {
            left = MathF.Min(left, quad.Destination.X);
            top = MathF.Min(top, quad.Destination.Y);
            right = MathF.Max(right, quad.Destination.X + quad.Destination.Z);
            bottom = MathF.Max(bottom, quad.Destination.Y + quad.Destination.W);
        }

        Assert.True(left >= 0, $"the page started {-left} pixels off the left edge");
        Assert.True(top >= 0, $"the page started {-top} pixels above the top edge");
        Assert.True(right <= width, $"the page ran {right - width} pixels off the right edge");
        Assert.True(bottom <= height, $"the page ran {bottom - height} pixels off the bottom");
    }

    [Fact]
    public void Every_section_name_is_inside_the_panel_it_belongs_to()
    {
        // A section whose own rows come to less than the sidebar does used to have the last
        // of the names drawn below the bottom edge of the panel, on the screen behind it.
        MenuPage page = Page();

        Show(page, FrontEndPage.Audio);

        foreach (MenuSection section in FrontEnd.Sections)
        {
            Vector2? at = page.Aside(section.Id);

            Assert.NotNull(at);
            Assert.True(
                page.Covers(at.Value),
                $"the sidebar's \"{section.Text}\" was drawn outside the panel");
        }
    }

    [Theory]
    [InlineData(1024, 600)]
    [InlineData(1280, 720)]
    [InlineData(1920, 1080)]
    [InlineData(2560, 1440)]
    public void A_sliders_bar_starts_after_the_end_of_its_label(int width, int height)
    {
        // The overlap, as two assertions. A slider is a label, then a bar, then a reading,
        // and the bar was the part that was never measured: it was drawn from halfway
        // across whatever width the label and the reading came to, which on the Sound page
        // was halfway through "Music and cutscenes".
        //
        // Said as what a click means, because that is the same arithmetic the bar is drawn
        // with: the end of the longest label is before the bar, so a click there means
        // nought, and the start of the reading is past the end of it, so a click there
        // means all of it.
        MenuPage page = Page();

        IReadOnlyList<MenuItem> items = Show(page, FrontEndPage.Audio, width, height);

        // The longest of each, over the whole page rather than over the row: the bars on a
        // page are aligned with one another, so they clear the worst label on it and stop
        // short of the widest reading on it.
        float longest = 0f;
        float reading = page.Overlay.Measure("100%");

        foreach (MenuItem item in items)
        {
            if (item.Kind == MenuItemKind.Slider)
            {
                longest = MathF.Max(longest, page.Overlay.Measure(item.Text));
                reading = MathF.Max(reading, page.Overlay.Measure(item.Value));
            }
        }

        int sliders = 0;

        for (int i = 0; i < items.Count; i++)
        {
            if (items[i].Kind != MenuItemKind.Slider || page.Where(i) is not { } row)
            {
                continue;
            }

            sliders++;

            float unit = page.Overlay.LineHeight;
            float middle = row.Y + (row.W / 2f);

            MenuAction before = page.Click(
                new Vector2(row.X + unit + longest, middle), items);

            Assert.Equal(items[i].Id, before.Id);
            Assert.True(
                before.Fraction <= 0f,
                $"the bar on \"{items[i].Text}\" had already started {before.Fraction:P0} " +
                "of the way along by the end of the labels");

            MenuAction after = page.Click(
                new Vector2(row.X + row.Z - unit - reading, middle), items);

            Assert.Equal(items[i].Id, after.Id);
            Assert.True(
                after.Fraction >= 1f,
                $"the bar on \"{items[i].Text}\" was only {after.Fraction:P0} of the way " +
                "along where the reading begins, so its far end is under the reading");
        }

        Assert.True(sliders >= 5, $"the Sound section should be sliders, and had {sliders}");
    }

    [Fact]
    public void A_slider_reads_the_same_wherever_it_is_dragged_from()
    {
        // The bar the player drags has to be the bar they were shown. Dragging to the far
        // end of it means all of it, and to the near end means none — which is only true
        // while the hit test and the drawing agree about where the bar is.
        MenuPage page = Page();

        IReadOnlyList<MenuItem> items = Show(page, FrontEndPage.Audio);

        int master = 0;

        while (master < items.Count && items[master].Kind != MenuItemKind.Slider)
        {
            master++;
        }

        Vector4 row = page.Where(master)!.Value;
        float middle = row.Y + (row.W / 2f);

        // A pixel inside each end of the row rather than on it: the sidebar's right-hand
        // edge and the content's left-hand edge are the same column of pixels, and the
        // sidebar is asked first.
        Assert.Equal(
            0f,
            page.Click(new Vector2(row.X + 1f, middle), items).Fraction);

        Assert.Equal(
            1f,
            page.Click(new Vector2(row.X + row.Z - 1f, middle), items).Fraction);
    }
}
