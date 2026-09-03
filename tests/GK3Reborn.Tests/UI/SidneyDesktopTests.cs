using System.Numerics;
using GK3Reborn.Game;
using GK3Reborn.Game.Sidney;
using GK3Reborn.Rendering;
using GK3Reborn.UI;
using GK3Reborn.UI.Sidney;
using Xunit;

namespace GK3Reborn.Tests.UI;

/// <summary>
/// Tests for Sidney drawn as a laptop with a desktop on it.
/// </summary>
/// <remarks>
/// <para>
/// Two things here are worth a test rather than an eye. The first is that every button the
/// puzzle needs is registered where it is drawn: the search button and the print match were
/// both drawn, both clickable and both wired to a dispatcher that dropped them, so the
/// screens looked finished and did nothing.
/// </para>
/// <para>
/// The second is that <b>nothing the machine holds can be out of reach</b>. The suspects
/// list stopped at the bottom of the panel and silently dropped its tenth name at ordinary
/// window sizes — and that name is the one whose print is worth linking. A list that is too
/// long now scrolls, and the test scrolls it.
/// </para>
/// </remarks>
public sealed class SidneyDesktopTests
{
    private const string Text = """
        [Main Screen]
        MenuItem1 = SEARCH
        MenuItem2 = SUSPECTS
        MenuItem3 = EXIT

        [Search Screen]
        ScreenName = SEARCH
        NotFound   = Subject not found.

        [Suspects Screen]
        ScreenName   = SUSPECTS
        Name1        = Madeline Buthane
        Name2        = Vittorio Buchelli
        Name3        = Emilio Baza
        Name4        = Abbe Arnaud
        Name5        = Lady Howard
        Name6        = Estelle Stiles
        Name7        = John Wilkes
        Name8        = Larry Chester
        Name9        = Excelsior Montreaux
        Name10       = Franklin Mosely
        NoLinks      = There are no linked files for this suspect.
        MatchNone    = ** No Match Found **
        """;

    private const string Mail = """
        [EMail Files]
        EMail1 = Hello!

        [EMail1]
        From    = RT_Nakimura@aol.com
        To      = Grace.Nakimura@Euroserve.com
        Date    = Jul 1, 1998, 7:25am
        Subject = Hello!
        Body1   = Grace: your father had a wonderful idea.
        """;

    private static ScreenPainter Painter() => new(new Overlay(MenuPageTests.Font()));

    private static SidneyMachine Machine(out GameState state)
    {
        state = new GameState { Ego = "GRACE" };

        return new SidneyMachine(SidneyLibrary.From(Text, Mail), state);
    }

    private static ScreenView View(SidneyMachine machine) =>
        new(new Screen(ScreenKind.Sidney), [], null, machine);

    /// <summary>Where on the screen something answers to an identifier, found by sweeping.</summary>
    /// <remarks>
    /// By sweeping rather than by asking, because the painter exposes where it put things
    /// only through the hit test — which is the thing being checked.
    /// </remarks>
    private static Vector2? Middle(ScreenPainter painter, string id, int width = 1280, int height = 720)
    {
        for (int y = 0; y < height; y += 3)
        {
            for (int x = 0; x < width; x += 3)
            {
                if (painter.HitAt(new Vector2(x, y)) == id)
                {
                    return new Vector2(x, y);
                }
            }
        }

        return null;
    }

    [Fact]
    public void The_desktop_offers_all_eight_programs_and_a_way_out()
    {
        ScreenPainter painter = Painter();

        painter.Build(View(Machine(out _)), 1280, 720);

        foreach (SidneyScreen screen in new[]
        {
            SidneyScreen.Search, SidneyScreen.EMail, SidneyScreen.Files, SidneyScreen.Analyze,
            SidneyScreen.Translate, SidneyScreen.AddData, SidneyScreen.MakeId, SidneyScreen.Suspects,
        })
        {
            Assert.NotNull(Middle(painter, $"sidney:screen:{screen}"));
        }

        // The power button, which is the EXIT row the game's own menu carries.
        Assert.NotNull(Middle(painter, "close"));
    }

    [Fact]
    public void The_search_button_is_where_it_is_drawn()
    {
        // Drawn, clickable, and answered by nothing: the dispatcher wanted a subject after
        // the command and dropped every command that had none.
        SidneyMachine sidney = Machine(out _);
        sidney.Screen = SidneyScreen.Search;

        ScreenPainter painter = Painter();

        painter.Build(View(sidney), 1280, 720);

        Assert.NotNull(Middle(painter, "sidney:look"));
        Assert.NotNull(Middle(painter, "sidney:type"));
    }

    [Fact]
    public void The_print_match_is_where_it_is_drawn()
    {
        SidneyMachine sidney = Machine(out _);
        sidney.Screen = SidneyScreen.Suspects;
        sidney.OpenSuspect(sidney.Library.Suspects()[0]);

        ScreenPainter painter = Painter();

        painter.Build(View(sidney), 1280, 720);

        Assert.NotNull(Middle(painter, "sidney:match"));
    }

    [Fact]
    public void Every_one_of_the_ten_suspects_is_reachable()
    {
        // The regression: the list drew until it ran out of panel and then stopped, which
        // lost the tenth name at ordinary window sizes — and his print is the one that
        // names him.
        SidneyMachine sidney = Machine(out _);
        sidney.Screen = SidneyScreen.Suspects;

        ScreenPainter painter = Painter();

        painter.Build(View(sidney), 1280, 720);

        for (int i = 1; i <= 10; i++)
        {
            Assert.NotNull(Middle(painter, $"sidney:suspect:{i}"));
        }
    }

    [Fact]
    public void A_list_longer_than_the_screen_scrolls_rather_than_stopping()
    {
        // Thirty names, so that no window size and no font can make them all fit and the
        // wheel is the only way to the end of the list.
        var state = new GameState { Ego = "GRACE" };

        string many = string.Join(
            Environment.NewLine,
            Enumerable.Range(1, 30).Select(i => $"Name{i} = Suspect Number {i}"));

        string text = string.Join(
            Environment.NewLine,
            "[Main Screen]",
            "MenuItem1 = SUSPECTS",
            string.Empty,
            "[Suspects Screen]",
            many);

        var sidney = new SidneyMachine(SidneyLibrary.From(text), state)
        {
            Screen = SidneyScreen.Suspects,
        };

        ScreenPainter painter = Painter();
        ScreenView view = View(sidney);

        painter.Build(view, 1280, 720);

        Vector2? first = Middle(painter, "sidney:suspect:1");

        Assert.NotNull(first);
        Assert.Null(Middle(painter, "sidney:suspect:30"));

        // Turned over the list, which is where the pointer would be.
        for (int i = 0; i < 30; i++)
        {
            painter.SidneyWheel(first.Value, -1);
            painter.Build(view, 1280, 720);
        }

        Assert.NotNull(Middle(painter, "sidney:suspect:30"));

        // And back, without running off the top or sticking there.
        for (int i = 0; i < 90; i++)
        {
            painter.SidneyWheel(first.Value, 1);
        }

        painter.Build(view, 1280, 720);

        Assert.Equal(first, Middle(painter, "sidney:suspect:1"));
    }

    [Fact]
    public void Nothing_a_program_draws_leaves_the_laptops_screen()
    {
        // The interface is drawn inside a picture of a laptop. A list that overran would
        // draw its rows across the bezel, which is worse than a list that stops.
        SidneyMachine sidney = Machine(out _);
        sidney.Screen = SidneyScreen.Suspects;

        ScreenPainter painter = Painter();

        painter.Build(View(sidney), 1024, 600);

        Vector4 laptop = SidneyLaptop.Fit(1024, 600);
        Vector4 screen = SidneyLaptop.ScreenOf(laptop);

        foreach (OverlayQuad quad in painter.Overlay.Quads)
        {
            // The room behind and the laptop itself are drawn before the clip goes on.
            if (quad.Destination.Z >= screen.Z || quad.Destination.W >= screen.W)
            {
                continue;
            }

            Assert.True(quad.Destination.X + quad.Destination.Z <= screen.X + screen.Z + 1);
            Assert.True(quad.Destination.Y + quad.Destination.W <= screen.Y + screen.W + 1);
        }
    }

    [Fact]
    public void The_mail_light_is_only_offered_while_something_is_unread()
    {
        SidneyMachine sidney = Machine(out _);

        ScreenPainter painter = Painter();

        painter.Build(View(sidney), 1280, 720);

        Assert.NotNull(Middle(painter, $"sidney:screen:{SidneyScreen.EMail}"));

        sidney.ReadMail(sidney.Library.Mail()[0]);
        painter.Build(View(sidney), 1280, 720);

        // The icon on the desktop stays; the notification over it does not.
        Assert.Equal(0, sidney.Unread);
    }

    [Fact]
    public void A_figure_that_cannot_be_fitted_does_not_paint_the_screen()
    {
        // Three marked places in very nearly a straight line have a circle through them
        // whose centre is off in the next country. Sizing each step of an arc to close the
        // gap left by a fixed number of steps then drew that circle as a few hundred solid
        // blue blocks the size of the window.
        var state = new GameState { Ego = "GRACE" };

        string text = string.Join(
            Environment.NewLine,
            "[Main Screen]",
            "MenuItem1 = ANALYZE",
            string.Empty,
            "[Analyze Screen]",
            "GeometryParch2 = square and circle");

        var sidney = new SidneyMachine(SidneyLibrary.From(text), state)
        {
            Screen = SidneyScreen.Analyze,
        };

        sidney.Scan("PARCHMENT_2");
        sidney.Scan("MAP");
        sidney.OpenFile(sidney.Files.First(f => f.Item == "PARCHMENT_2"));
        sidney.Perform(SidneyAction.ViewGeometry);
        sidney.OpenFile(sidney.Files.First(f => f.Item == "MAP"));

        sidney.Mark(new Vector2(400, 400));
        sidney.Mark(new Vector2(700, 410));
        sidney.Mark(new Vector2(1000, 420));
        sidney.LayShape(MapShape.Circle);

        ScreenPainter painter = Painter();

        painter.Build(View(sidney), 1280, 720);

        Vector4 screen = SidneyLaptop.ScreenOf(SidneyLaptop.Fit(1280, 720));

        // Nothing the map draws is bigger than the map, and the whole screen stays inside
        // the display list's budget.
        foreach (OverlayQuad quad in painter.Overlay.Quads)
        {
            if (quad.Destination.Z < screen.Z && quad.Destination.W < screen.W)
            {
                Assert.InRange(quad.Destination.Z, 0, screen.Z);
                Assert.InRange(quad.Destination.W, 0, screen.W);
            }
        }

        Assert.InRange(painter.Overlay.Quads.Count, 1, 2048);
    }

    [Fact]
    public void The_map_takes_a_click_only_while_it_has_been_armed_for_one()
    {
        // Reported: places could be marked before ENTER POINTS had ever been chosen, so a
        // click meant for something else put a village on the map. The original's menu item
        // exists precisely because clicking a map is otherwise ambiguous.
        var state = new GameState { Ego = "GRACE" };

        var sidney = new SidneyMachine(
            SidneyLibrary.From(string.Join(
                Environment.NewLine, "[Main Screen]", "MenuItem1 = ANALYZE")),
            state)
        {
            Screen = SidneyScreen.Analyze,
        };

        sidney.Scan("MAP");
        sidney.OpenFile(sidney.Files[0]);

        ScreenPainter painter = Painter();

        painter.Build(View(sidney), 1600, 900);

        Vector4 map = painter.MapBounds;
        var middle = new Vector2(map.X + (map.Z / 2), map.Y + (map.W / 2));

        Assert.False(sidney.Marking);
        Assert.NotEqual("sidney:mark", painter.HitAt(middle));

        sidney.Perform(SidneyAction.EnterPoints);
        painter.Build(View(sidney), 1600, 900);

        Assert.True(sidney.Marking);
        Assert.Equal("sidney:mark", painter.HitAt(middle));

        // And it is a toggle: choosing it again disarms the map.
        sidney.Perform(SidneyAction.EnterPoints);
        painter.Build(View(sidney), 1600, 900);

        Assert.False(sidney.Marking);
        Assert.NotEqual("sidney:mark", painter.HitAt(middle));
    }

    [Fact]
    public void A_marked_place_answers_to_a_press_on_it_rather_than_to_the_map()
    {
        // What makes dragging possible at all: the place has to win the hit over the
        // picture it sits on, whether or not the map is armed for marking.
        var state = new GameState { Ego = "GRACE" };

        var sidney = new SidneyMachine(
            SidneyLibrary.From(string.Join(
                Environment.NewLine, "[Main Screen]", "MenuItem1 = ANALYZE")),
            state)
        {
            Screen = SidneyScreen.Analyze,
        };

        sidney.Scan("MAP");
        sidney.OpenFile(sidney.Files[0]);
        sidney.Perform(SidneyAction.EnterPoints);
        sidney.Mark(new Vector2(700, 700));

        ScreenPainter painter = Painter();

        painter.Build(View(sidney), 1600, 900);

        Vector4 map = painter.MapBounds;
        float scale = map.Z / SidneyMap.Extent;

        // Minus one is the working set: a place not yet given to a figure.
        Assert.Equal(
            "sidney:point:-1:0",
            painter.HitAt(new Vector2(map.X + (700 * scale), map.Y + (700 * scale))));

        // Still, once the map has been disarmed: correcting a place is not marking one.
        sidney.Perform(SidneyAction.EnterPoints);
        painter.Build(View(sidney), 1600, 900);

        Assert.Equal(
            "sidney:point:-1:0",
            painter.HitAt(new Vector2(map.X + (700 * scale), map.Y + (700 * scale))));
    }

    [Fact]
    public void Two_marked_places_draw_the_line_between_them_across_the_country()
    {
        // The first step of the whole map puzzle is the sunrise line from the church at
        // Rennes-le-Château to the tower at Blanchefort. The machine has always recognised
        // that two places make a line and never drawn one, so the player was told their two
        // points made a line and shown nothing.
        var state = new GameState { Ego = "GRACE" };

        var sidney = new SidneyMachine(
            SidneyLibrary.From(string.Join(
                Environment.NewLine, "[Main Screen]", "MenuItem1 = ANALYZE")),
            state)
        {
            Screen = SidneyScreen.Analyze,
        };

        sidney.Scan("MAP");
        sidney.OpenFile(sidney.Files[0]);

        ScreenPainter painter = Painter();

        painter.Build(View(sidney), 1600, 900);

        Assert.DoesNotContain(
            painter.Overlay.Quads, quad => quad.Color == SidneyPalette.Finding);

        sidney.Mark(new Vector2(380, 470));
        sidney.Mark(new Vector2(690, 395));

        painter.Build(View(sidney), 1600, 900);

        Vector4 map = painter.MapBounds;
        float leftmost = float.MaxValue;
        float rightmost = float.MinValue;

        foreach (OverlayQuad quad in painter.Overlay.Quads)
        {
            if (quad.Color != SidneyPalette.Finding)
            {
                continue;
            }

            leftmost = MathF.Min(leftmost, quad.Destination.X);
            rightmost = MathF.Max(rightmost, quad.Destination.X + quad.Destination.Z);
        }

        Assert.True(leftmost < float.MaxValue, "the line was not drawn at all");

        // Right across the country, not just between the two places: what the puzzle asks
        // is what else the join passes through.
        Assert.InRange(leftmost, map.X - 1, map.X + (map.Z * 0.1f));
        Assert.InRange(rightmost, map.X + (map.Z * 0.9f), map.X + map.Z + 1);
    }

    [Fact]
    public void A_figure_that_runs_off_the_map_is_cut_at_its_edge()
    {
        // A figure is fitted to places the player chose and no arrangement of them keeps it
        // inside the picture — a circle through marks near one edge is mostly somewhere
        // else. It used to be drawn across the rest of Sidney and out over the title bar.
        var state = new GameState { Ego = "GRACE" };

        string text = string.Join(
            Environment.NewLine,
            "[Main Screen]",
            "MenuItem1 = ANALYZE",
            string.Empty,
            "[Analyze Screen]",
            "GeometryParch2 = square and circle");

        var sidney = new SidneyMachine(SidneyLibrary.From(text), state)
        {
            Screen = SidneyScreen.Analyze,
        };

        sidney.Scan("PARCHMENT_2");
        sidney.Scan("MAP");
        sidney.OpenFile(sidney.Files.First(f => f.Item == "PARCHMENT_2"));
        sidney.Perform(SidneyAction.ViewGeometry);
        sidney.OpenFile(sidney.Files.First(f => f.Item == "MAP"));

        // A shallow arc of places, whose circle is far bigger than the country they are in.
        sidney.Mark(new Vector2(120, 760));
        sidney.Mark(new Vector2(330, 610));
        sidney.Mark(new Vector2(620, 540));
        sidney.Mark(new Vector2(980, 600));
        sidney.LayShape(MapShape.Circle);

        ScreenPainter painter = Painter();

        painter.Build(View(sidney), 1600, 900);

        Vector4 map = painter.MapBounds;

        Assert.True(map.Z > 0);

        // The same two colours draw the row of figure buttons beside the map, which is the
        // one place they are meant to be outside it.
        foreach (OverlayQuad quad in painter.Overlay.Quads)
        {
            if (quad.Color != SidneyPalette.Figure && quad.Color != SidneyPalette.Confirmed)
            {
                continue;
            }

            if (quad.Destination.X >= map.X + map.Z)
            {
                continue;
            }

            Assert.InRange(quad.Destination.X, map.X, map.X + map.Z);
            Assert.InRange(quad.Destination.Y, map.Y, map.Y + map.W);
            Assert.InRange(quad.Destination.X + quad.Destination.Z, map.X, map.X + map.Z + 1);
            Assert.InRange(quad.Destination.Y + quad.Destination.W, map.Y, map.Y + map.W + 1);
        }
    }

    [Fact]
    public void The_map_with_every_figure_on_it_stays_inside_the_display_list()
    {
        // A circle drawn as axis-aligned rectangles costs about its circumference, so the
        // map is far and away the most expensive thing the interface draws: four figures
        // over a 4K one came to five and a half thousand rectangles and ran the buffer out,
        // which took the taskbar off the bottom of the screen with it.
        var state = new GameState { Ego = "GRACE" };

        string text = string.Join(
            Environment.NewLine,
            "[Main Screen]",
            "MenuItem1 = ANALYZE",
            string.Empty,
            "[Analyze Screen]",
            "GeometryParch2 = square and circle",
            "GeometryPous = triangle and hexagram");

        var sidney = new SidneyMachine(SidneyLibrary.From(text), state)
        {
            Screen = SidneyScreen.Analyze,
        };

        sidney.Scan("PARCHMENT_2");
        sidney.Scan("POUSSIN_POSTCARD");
        sidney.Scan("MAP");
        sidney.OpenFile(sidney.Files.First(f => f.Item == "PARCHMENT_2"));
        sidney.Perform(SidneyAction.ViewGeometry);
        sidney.OpenFile(sidney.Files.First(f => f.Item == "POUSSIN_POSTCARD"));
        sidney.Perform(SidneyAction.ViewGeometry);
        sidney.OpenFile(sidney.Files.First(f => f.Item == "MAP"));

        for (int i = 0; i < 6; i++)
        {
            float angle = i * 60 * MathF.PI / 180f;

            sidney.Mark(new Vector2(
                700 + (300 * MathF.Cos(angle)), 700 + (300 * MathF.Sin(angle))));
        }

        sidney.Perform(SidneyAction.DrawGrid);

        foreach (MapShape shape in new[]
        {
            MapShape.Circle, MapShape.Square, MapShape.Triangle, MapShape.Hexagram,
        })
        {
            sidney.LayShape(shape);
        }

        ScreenPainter painter = Painter();

        foreach ((int width, int height) in new[]
        {
            (1280, 720), (1600, 900), (1920, 1080), (2560, 1440), (3840, 2160),
        })
        {
            painter.Build(View(sidney), width, height);

            // Comfortably inside the sixteen thousand both backends now hold, with the room
            // that lets the warning about running out mean something.
            Assert.InRange(painter.Overlay.Quads.Count, 1, 8192);
        }
    }

    [Fact]
    public void A_window_the_whole_laptop_fits_in_centres_the_whole_laptop()
    {
        // Reported after a resize. On a window taller than the picture's shape nothing is
        // cropped, and centring on the band that would have been cropped pushed the laptop
        // up the screen and left a dead strip of black under the keyboard.
        foreach ((int width, int height) in new[] { (1600, 1564), (1200, 1600), (1024, 1200) })
        {
            Vector4 laptop = SidneyLaptop.Fit(width, height);

            Assert.True(laptop.W <= height);
            Assert.Equal(
                MathF.Round((height - laptop.W) / 2), laptop.Y);
        }
    }

    [Fact]
    public void A_window_that_crops_the_case_keeps_the_screen_on_it()
    {
        // And the other way: when the height runs out, some case is lost and what has to
        // stay on the window is the screen with its band of lid and its photograph.
        foreach ((int width, int height) in new[] { (1280, 720), (1920, 1080), (2560, 1080) })
        {
            Vector4 laptop = SidneyLaptop.Fit(width, height);
            Vector4 screen = SidneyLaptop.ScreenOf(laptop);

            Assert.True(screen.Y >= 0);
            Assert.True(screen.Y + screen.W <= height);
        }
    }

    [Fact]
    public void The_desktop_stays_inside_the_display_lists_budget()
    {
        // The display list is capped and the cap cuts from the end, which is the taskbar and
        // whatever else is drawn on top. The eight icons used to be drawn a pixel of arc at
        // a time and ran the budget out on a large window: the bottom of the screen simply
        // stopped being there, and came back when the window was made smaller again.
        SidneyMachine sidney = Machine(out _);

        ScreenPainter painter = Painter();

        foreach ((int width, int height) in new[]
        {
            (1024, 600), (1280, 720), (1920, 1080), (2560, 1440), (3840, 2160), (1600, 1564),
        })
        {
            painter.Build(View(sidney), width, height);

            // Half the smaller of the two backends' buffers, so that the room the rest of
            // the interface needs is still there.
            Assert.InRange(painter.Overlay.Quads.Count, 1, 2048);
        }
    }

    [Fact]
    public void The_screen_keeps_its_shape_at_every_window_size()
    {
        foreach ((int width, int height) in new[] { (800, 600), (1280, 720), (2560, 1440), (1200, 1600) })
        {
            Vector4 screen = SidneyLaptop.ScreenOf(SidneyLaptop.Fit(width, height));

            Assert.True(screen.Z > 0 && screen.W > 0);
            Assert.InRange(screen.Z / screen.W, 1.32f, 1.34f);
            Assert.True(screen.Z <= width + 1);
            Assert.True(screen.W <= height + 1);
        }
    }
}
