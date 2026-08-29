using System.Numerics;
using GK3Reborn.Game;
using GK3Reborn.Rendering;
using GK3Reborn.Rendering.Upscaling;
using GK3Reborn.UI;
using Xunit;

namespace GK3Reborn.Tests.UI;

/// <summary>
/// Tests that a settings page stays on the screen.
/// </summary>
/// <remarks>
/// <para>
/// The picture pages carry a dozen rows and half of them carry an explanation, which is
/// several times what the menu was built for. Two things used to go wrong and both are
/// checked here: a page too tall for the window ran off the bottom with its last rows
/// unreachable, and a sentence wider than the panel was drawn straight through the side of
/// it.
/// </para>
/// <para>
/// Checked against the quads the page actually emitted rather than against its arithmetic.
/// A layout test that reimplements the layout only proves the two agree.
/// </para>
/// </remarks>
public sealed class MenuBoundsTests
{
    /// <summary>
    /// A page with nothing drawn behind it.
    /// </summary>
    /// <remarks>
    /// Over a room or over its own gradient the page fills the window with a wash first,
    /// and every measurement of "what was drawn" then comes back as the whole window
    /// whatever the panel did. Over the title art there is no wash, so what is left is the
    /// panel and its contents — which is the thing being measured.
    /// </remarks>
    private static MenuPage Page() =>
        new(new Overlay(MenuPageTests.Font())) { Behind = MenuBehind.Picture };

    /// <summary>The page a settings screen with everything switched on comes to.</summary>
    private static IReadOnlyList<MenuItem> Crowded()
    {
        var front = new FrontEnd(new Settings
        {
            HighDynamicRange = true,
            Upscaler = UpscalerKind.Dlss,
        });

        front.Show(FrontEndPage.Display);

        return front.Items;
    }

    private static (float Left, float Top, float Right, float Bottom) Drawn(MenuPage page)
    {
        float left = float.MaxValue;
        float top = float.MaxValue;
        float right = float.MinValue;
        float bottom = float.MinValue;

        foreach (OverlayQuad quad in page.Overlay.Quads)
        {
            left = Math.Min(left, quad.Destination.X);
            top = Math.Min(top, quad.Destination.Y);
            right = Math.Max(right, quad.Destination.X + quad.Destination.Z);
            bottom = Math.Max(bottom, quad.Destination.Y + quad.Destination.W);
        }

        return (left, top, right, bottom);
    }

    [Theory]
    [InlineData(1280, 720)]
    [InlineData(1920, 1080)]
    [InlineData(2560, 1440)]
    [InlineData(3840, 2160)]
    [InlineData(1024, 480)]
    [InlineData(800, 600)]
    public void A_crowded_page_is_drawn_entirely_inside_the_window(int width, int height)
    {
        MenuPage page = Page();

        page.Build("Display", Crowded(), width, height, Vector2.Zero);

        (float left, float top, float right, float bottom) = Drawn(page);

        Assert.True(left >= 0, $"the page started {-left} pixels off the left edge");
        Assert.True(top >= 0, $"the page started {-top} pixels above the top edge");
        Assert.True(right <= width, $"the page ran {right - width} pixels off the right edge");
        Assert.True(bottom <= height, $"the page ran {bottom - height} pixels off the bottom");
    }

    [Fact]
    public void A_page_with_more_rows_than_will_fit_scrolls_rather_than_overflowing()
    {
        // Forty rows will not fit in a short window at any spacing, which is the case the
        // old arrangement had no answer for: it tightened the rows until they touched and
        // then ran off the bottom anyway.
        List<MenuItem> many = [];

        for (int i = 0; i < 40; i++)
        {
            many.Add(MenuItem.Toggle("row" + i, "Setting number " + i, on: i % 2 == 0));
        }

        MenuPage page = Page();

        page.Build("Long", many, 800, 400, Vector2.Zero);

        (_, float top, _, float bottom) = Drawn(page);

        Assert.True(top >= 0 && bottom <= 400, "a long page must still fit the window");
    }

    [Fact]
    public void The_row_the_player_is_on_is_always_one_of_the_rows_drawn()
    {
        // The point of scrolling: stepping down a list has to reveal the row being stepped
        // onto. A page that scrolls and leaves the selection off it is worse than one that
        // does not scroll.
        List<MenuItem> many = [];

        for (int i = 0; i < 40; i++)
        {
            many.Add(MenuItem.Button("row" + i, "Setting number " + i));
        }

        MenuPage page = Page();

        for (int step = 0; step < 40; step++)
        {
            page.Build("Long", many, 800, 400, Vector2.Zero);

            // The chosen row is the only one drawn with a bar down its side, so finding it
            // is finding a quad the width of a quarter of a line at the panel's left edge.
            Assert.True(
                page.Click(Middle(page, many), many).Id == many[page.Index].Id,
                $"row {page.Index} was not where it could be clicked");

            page.Move(many, 1);
        }
    }

    /// <summary>Where the chosen row was drawn, found by clicking down the page.</summary>
    /// <remarks>
    /// Walks the window looking for the point at which the page reports the chosen row.
    /// Deliberately does not ask the page where it put it: what is being checked is that
    /// the row can be reached with a pointer, which is the thing that broke.
    /// </remarks>
    private static Vector2 Middle(MenuPage page, List<MenuItem> items)
    {
        string wanted = items[page.Index].Id;

        for (int y = 0; y < 400; y++)
        {
            var at = new Vector2(400, y);

            if (page.Click(at, items).Id == wanted)
            {
                return at;
            }
        }

        return new Vector2(-1, -1);
    }

    [Fact]
    public void An_explanation_too_wide_for_the_panel_is_broken_rather_than_run_through_it()
    {
        MenuPage page = Page();

        IReadOnlyList<MenuItem> items =
        [
            MenuItem.Toggle("hdr", "High dynamic range", on: true),
            MenuItem.Label(
                "Where a white wall and the menu sit, which is the one number a player " +
                "notices most, and which two hundred candelas matches on the desktop that " +
                "Windows gives standard-range content on a display in high dynamic range."),
            MenuItem.Button("back", "Back"),
        ];

        page.Build("Display", items, 900, 700, Vector2.Zero);

        (float left, _, float right, _) = Drawn(page);

        Assert.True(left >= 0 && right <= 900, "the sentence ran outside the window");

        // And the panel is not simply as wide as the sentence: a page whose width is set by
        // its longest explanation is a page-wide slab on every screen that has one.
        Assert.True(
            right - left < 900,
            "the panel should be narrower than the window rather than sized to the prose");
    }

    [Fact]
    public void A_short_page_is_not_padded_out_to_the_size_of_a_long_one()
    {
        // The other half of the complaint: nothing here should make the ordinary four-row
        // pages bigger than they were.
        MenuPage page = Page();

        IReadOnlyList<MenuItem> few =
        [
            MenuItem.Button("play", "Play"),
            MenuItem.Button("quit", "Quit"),
        ];

        page.Build("Gabriel Knight 3", few, 1920, 1080, Vector2.Zero);

        (_, float top, _, float bottom) = Drawn(page);

        Assert.True(
            bottom - top < 1080 / 2f,
            "a two-row page should not take half the screen");
    }

    [Fact]
    public void The_pointer_lands_on_the_row_it_is_over_even_after_a_wrapped_explanation()
    {
        // Rows are no longer all the same height, so the hit test cannot divide by one row
        // and has to walk them. Dividing put the pointer on the wrong setting for every row
        // below the first sentence that wrapped.
        MenuPage page = Page();

        IReadOnlyList<MenuItem> items =
        [
            MenuItem.Toggle("first", "The first setting", on: true),
            MenuItem.Label(
                "An explanation long enough that it certainly has to be broken across " +
                "more than one line before it will fit inside the panel it is drawn in."),
            MenuItem.Toggle("second", "The second setting", on: false),
            MenuItem.Button("back", "Back"),
        ];

        page.Build("Display", items, 700, 600, Vector2.Zero);

        var found = new List<string>();

        for (int y = 0; y < 600; y++)
        {
            string id = page.Click(new Vector2(350, y), items).Id;

            if (id.Length > 0 && (found.Count == 0 || found[^1] != id))
            {
                found.Add(id);
            }
        }

        // Down the page in order, with nothing reachable twice: a row appearing again below
        // another one is the signature of a hit test that has lost track of the heights.
        Assert.Equal(["first", "second", "back"], found);
    }
}
