using System.Numerics;
using GK3Reborn.Game;
using GK3Reborn.Rendering;
using GK3Reborn.UI;
using Xunit;

namespace GK3Reborn.Tests.UI;

/// <summary>
/// Tests that a long settings page moves under the player only when it has to.
/// </summary>
/// <remarks>
/// <para>
/// Reported as "the scroll loves to immediately jump, which for a user isn't nice". It
/// did: the page kept a single row index and recomputed it from the selection every frame,
/// growing a window of rows outwards from the chosen one — so the chosen row was always in
/// the middle of the panel and <em>every</em> step of the selection scrolled the whole page
/// by one row. The list moved and the cursor stood still, which is the wrong way round.
/// </para>
/// <para>
/// What is checked here is the behaviour and not the arithmetic: where the rows were
/// actually drawn, frame by frame, as the selection walks down a page taller than the
/// window.
/// </para>
/// </remarks>
public sealed class MenuScrollTests
{
    private const int Width = 1280;
    private const int Height = 720;

    private static MenuPage Page() =>
        new(new Overlay(MenuPageTests.Font())) { Behind = MenuBehind.Picture };

    /// <summary>A page of plain rows, long enough that it cannot all be shown.</summary>
    private static MenuItem[] Long(int rows) =>
        [.. Enumerable.Range(0, rows).Select(i => MenuItem.Toggle($"row{i}", $"Setting {i}", true))];

    [Fact]
    public void Stepping_through_the_middle_of_a_page_does_not_move_the_page()
    {
        // The whole complaint, as one assertion. A row that is already on the screen, with
        // room below it, is stepped onto without the page going anywhere.
        MenuPage page = Page();
        MenuItem[] items = Long(40);

        page.Reset(items);
        page.Build("Settings", items, Width, Height, Vector2.Zero);

        Assert.Equal(0f, page.Scrolled);

        float first = page.Where(0)!.Value.Y;

        page.Move(items, 1);
        page.Build("Settings", items, Width, Height, Vector2.Zero);

        // The page has not moved: the first row is exactly where it was.
        Assert.Equal(0f, page.Scrolled);
        Assert.Equal(first, page.Where(0)!.Value.Y, 1);

        // And the selection has: the row below is lower than the row above.
        Assert.True(page.Where(1)!.Value.Y > first);
    }

    [Fact]
    public void The_page_moves_only_when_the_selection_would_leave_it()
    {
        MenuPage page = Page();
        MenuItem[] items = Long(40);

        page.Reset(items);
        page.Build("Settings", items, Width, Height, Vector2.Zero);

        Assert.True(page.Scrolls, "a page of forty rows should not fit a 720-line window");

        float previous = page.Scrolled;
        int moved = 0;
        int steps = 0;

        // Walk the whole page a row at a time. Every step that moves the page is a step the
        // player sees the list slide under them, and only the steps that reach the bottom of
        // what is showing may do that.
        for (int i = 0; i < items.Length - 1; i++)
        {
            page.Move(items, 1);
            page.Build("Settings", items, Width, Height, Vector2.Zero);

            steps++;

            if (Math.Abs(page.Scrolled - previous) > 0.5f)
            {
                moved++;
            }

            previous = page.Scrolled;
        }

        // Before this it was every step of the way, because the window of rows was grown
        // outwards from the chosen one and the chosen one was therefore always in the
        // middle.
        Assert.True(
            moved < steps,
            $"the page moved on all {steps} steps, which is the jumping this fixes");

        Assert.True(
            moved <= steps * 3 / 4,
            $"the page moved on {moved} of {steps} steps");
    }

    [Fact]
    public void The_selection_is_always_on_the_part_of_the_page_that_is_showing()
    {
        // The other half of it: a page that only scrolls when it must has to actually
        // scroll when it must.
        MenuPage page = Page();
        MenuItem[] items = Long(40);

        page.Reset(items);

        for (int i = 0; i < items.Length + 4; i++)
        {
            page.Build("Settings", items, Width, Height, Vector2.Zero);

            Assert.True(
                page.Where(page.Index) is not null,
                $"the chosen row was not drawn after {i} steps down the page");

            page.Move(items, 1);
        }
    }

    [Fact]
    public void The_wheel_scrolls_the_page_and_leaves_the_selection_alone()
    {
        MenuPage page = Page();
        MenuItem[] items = Long(40);

        page.Reset(items);
        page.Build("Settings", items, Width, Height, Vector2.Zero);

        int chosen = page.Index;

        page.Wheel(-3);
        page.Build("Settings", items, Width, Height, Vector2.Zero);

        // Turning the wheel over a page to see what is on it must not change what pressing
        // Enter would do.
        Assert.Equal(chosen, page.Index);
        Assert.True(page.Scrolled > 0f, "the wheel did not move the page");

        float where = page.Scrolled;

        // And it must not be dragged straight back to the selection on the very next frame,
        // which is what a page that revealed its selection every frame would do.
        page.Build("Settings", items, Width, Height, Vector2.Zero);

        Assert.Equal(chosen, page.Index);
        Assert.Equal(where, page.Scrolled, 1);
    }

    [Fact]
    public void A_page_that_fits_does_not_scroll_at_all()
    {
        MenuPage page = Page();
        MenuItem[] items = Long(4);

        page.Reset(items);
        page.Build("Settings", items, Width, Height, Vector2.Zero);

        Assert.False(page.Scrolls);

        // And the wheel does nothing to it, rather than scrolling a page with nowhere to go.
        page.Wheel(-5);
        page.Build("Settings", items, Width, Height, Vector2.Zero);

        Assert.Equal(0f, page.Scrolled);
    }

    [Fact]
    public void The_page_slides_rather_than_arriving()
    {
        // A frame's worth of time closes part of the distance and not all of it. Nought
        // snaps, which is what every other test here relies on and what a photograph wants:
        // neither has a second frame for the page to have settled on.
        MenuPage page = Page();
        MenuItem[] items = Long(40);

        page.Reset(items);
        page.Build("Settings", items, Width, Height, Vector2.Zero, seconds: 0f);

        // Far enough that the page has a long way to go.
        for (int i = 0; i < 30; i++)
        {
            page.Move(items, 1);
        }

        page.Build("Settings", items, Width, Height, Vector2.Zero, seconds: 1f / 60f);

        float partly = page.Scrolled;

        Assert.True(partly > 0f, "the page did not start moving");

        for (int i = 0; i < 60; i++)
        {
            page.Build("Settings", items, Width, Height, Vector2.Zero, seconds: 1f / 60f);
        }

        Assert.True(
            page.Scrolled > partly + 1f,
            $"the page arrived in one frame ({partly} then {page.Scrolled}), so nothing was animated");
    }
}

/// <summary>
/// Tests that the settings screen lays its rows in two columns where that helps.
/// </summary>
public sealed class MenuColumnTests
{
    private static MenuPage Page() =>
        new(new Overlay(MenuPageTests.Font()))
        {
            Behind = MenuBehind.Picture,
            Sections = FrontEnd.Sections,
        };

    /// <summary>How many rows were drawn on the same line as another one.</summary>
    /// <remarks>
    /// Counted from the highlight-free geometry: two rows share a line when their recorded
    /// rectangles have the same top. Taken from where the page says it put them, which is
    /// the same list the pointer is hit-tested against — so a test that passes here is a
    /// test that the pointer agrees with.
    /// </remarks>
    private static int Paired(MenuPage page, IReadOnlyList<MenuItem> items)
    {
        Dictionary<float, int> lines = [];

        for (int i = 0; i < items.Count; i++)
        {
            if (page.Where(i) is not { } bounds)
            {
                continue;
            }

            lines[bounds.Y] = lines.GetValueOrDefault(bounds.Y) + 1;
        }

        return lines.Values.Count(n => n > 1);
    }

    [Fact]
    public void A_page_of_short_rows_is_laid_in_two_columns()
    {
        MenuPage page = Page();

        var front = new FrontEnd(new Settings());
        front.Show(FrontEndPage.Video);

        IReadOnlyList<MenuItem> items = front.Items;

        page.Reset(items);
        page.Build("Settings", items, 1920, 1080, Vector2.Zero);

        Assert.True(
            Paired(page, items) > 2,
            "the Picture section was drawn as a single column");
    }

    [Fact]
    public void A_page_with_no_sections_is_one_column()
    {
        // The title screen, the pause menu and the save slots are each one list. A sidebar
        // over one list is a margin, and two columns of five buttons is not a menu.
        var page = new MenuPage(new Overlay(MenuPageTests.Font()))
        {
            Behind = MenuBehind.Picture,
        };

        var front = new FrontEnd(new Settings());
        IReadOnlyList<MenuItem> items = front.Items;

        page.Reset(items);
        page.Build(string.Empty, items, 1920, 1080, Vector2.Zero);

        Assert.Equal(0, Paired(page, items));
    }

    [Fact]
    public void Headings_and_explanations_take_the_whole_width()
    {
        MenuPage page = Page();

        var front = new FrontEnd(new Settings());
        front.Show(FrontEndPage.Video);

        IReadOnlyList<MenuItem> items = front.Items;

        page.Reset(items);
        page.Build("Settings", items, 1920, 1080, Vector2.Zero);

        float widest = 0f;

        for (int i = 0; i < items.Count; i++)
        {
            if (!items[i].Spans && page.Where(i) is { } row)
            {
                widest = Math.Max(widest, row.Z);
            }
        }

        for (int i = 0; i < items.Count; i++)
        {
            if (items[i].Spans && page.Where(i) is { } band)
            {
                Assert.True(
                    band.Z > widest,
                    $"row {i} is a {items[i].Kind} and was drawn only {band.Z} wide");
            }
        }
    }

    [Fact]
    public void A_section_is_chosen_by_clicking_its_name_in_the_list()
    {
        MenuPage page = Page();

        var front = new FrontEnd(new Settings());
        front.Show(FrontEndPage.Video);

        IReadOnlyList<MenuItem> items = front.Items;

        page.Reset(items);
        page.Build("Settings", items, 1920, 1080, Vector2.Zero);

        Vector2 at = page.Aside("audio") ?? Vector2.Zero;

        Assert.NotEqual(Vector2.Zero, at);

        MenuAction chose = page.Click(at, items);

        Assert.Equal("tab:audio", chose.Id);
    }
}
