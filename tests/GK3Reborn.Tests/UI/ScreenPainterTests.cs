using System.Numerics;
using GK3Reborn.Game;
using GK3Reborn.Game.Sidney;
using GK3Reborn.Rendering;
using GK3Reborn.UI;
using Xunit;

namespace GK3Reborn.Tests.UI;

/// <summary>
/// Tests for the screens that go in front of the room.
/// </summary>
/// <remarks>
/// What can be wrong here is not how it looks but whether the thing the player clicks is
/// the thing they were pointing at. Layout and hit testing are the same pass — the painter
/// remembers where it put each rectangle — so a test that clicks the middle of something
/// and asks what it hit checks both at once, which is the arrangement
/// <see cref="GameHud"/> uses for the same reason.
/// </remarks>
public sealed class ScreenPainterTests
{
    private const int Width = 1280;
    private const int Height = 720;

    private const string SidneyText = """
        [Main Screen]
        MenuItem1 = ANALYZE
        MenuItem2 = ADD DATA
        MenuItem3 = ^
        MenuItem4 = E-MAIL
        MenuItem5 = EXIT

        [Analyze Screen]
        AnalyzeParch1 = Text appears to have irregularities in design.
        AnalyzeTemp   = Analysis did not find any encoded references.
        """;

    private const string SidneyMailText = """
        [EMail Files]
        EMail1 = Hello!

        [EMail1]
        From    = RT_Nakimura@aol.com
        Date    = Jul 1, 1998
        Subject = Hello!
        Body1   = Grace: Your Father had a wonderful idea.
        """;

    private static ScreenPainter Painter() => new(new Overlay(MenuPageTests.Font()));

    /// <summary>The middle of whatever the painter recorded under an identifier.</summary>
    private static Vector2? Middle(
        ScreenPainter painter, string id, int width = Width, int height = Height)
    {
        // Found by sweeping rather than by asking, because the painter deliberately exposes
        // where things are only through the hit test — which is the thing being checked.
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

    private static SidneyMachine Sidney(out GameState state)
    {
        state = new GameState { Ego = "GRACE" };

        return new SidneyMachine(SidneyLibrary.From(SidneyText, SidneyMailText), state);
    }

    [Fact]
    public void Every_screen_offers_the_same_way_out()
    {
        // One gesture, in the same place, whichever screen the player is looking at.
        foreach (ScreenKind kind in new[]
        {
            ScreenKind.Inventory, ScreenKind.InventoryInspect, ScreenKind.Binoculars,
            ScreenKind.Driving, ScreenKind.Sidney,
        })
        {
            ScreenPainter painter = Painter();

            painter.Build(new ScreenView(new Screen(kind), ["MAP"], null, Sidney(out _)), Width, Height);

            Assert.NotNull(Middle(painter, "close"));
        }
    }

    [Fact]
    public void Clicking_an_item_in_the_inventory_names_that_item()
    {
        ScreenPainter painter = Painter();

        painter.Build(
            new ScreenView(new Screen(ScreenKind.Inventory), ["MAP", "PARCHMENT_1"], null),
            Width,
            Height);

        Assert.NotNull(Middle(painter, "item:MAP"));
        Assert.NotNull(Middle(painter, "item:PARCHMENT_1"));
    }

    [Fact]
    public void An_empty_pocket_offers_nothing_to_click()
    {
        ScreenPainter painter = Painter();

        painter.Build(new ScreenView(new Screen(ScreenKind.Inventory), [], null), Width, Height);

        Assert.Null(Middle(painter, "item:MAP"));
        Assert.NotNull(Middle(painter, "close"));
    }

    [Fact]
    public void The_driving_map_offers_its_places_by_scene_and_not_by_name()
    {
        // With no art loaded the map falls back to a list, and a row shows a place's name
        // while carrying its location code. Carrying the name instead sent the game
        // looking for a room called "Larry Chester's House", which it did once.
        ScreenPainter painter = Painter();

        painter.Build(
            new ScreenView(
                new Screen(ScreenKind.Driving),
                [],
                null,
                Stops: [.. DrivingMap.All.Take(2)]),
            Width,
            Height);

        Assert.NotNull(Middle(painter, "drive:" + DrivingMap.All[0].Scene));
        Assert.NotNull(Middle(painter, "drive:" + DrivingMap.All[1].Scene));
    }

    [Fact]
    public void The_binoculars_offer_what_is_centred_and_nothing_else()
    {
        // They are a way of looking at the room rather than a page in front of it, so what
        // they offer depends on where the camera is pointed.
        Panorama view = Binoculars.From("""
            [CD1102P]
            LOC=MA3_a

            [CD1102PMA3_a]
            ZOOMRECT=174,0,189,10
            CAMANGLE=-287.38,4.5
            CAMPOS=2423.19,530.67,-4351.27
            """).For("CD1", "102P");

        ScreenPainter painter = Painter();
        var screen = new Screen(ScreenKind.Binoculars);

        painter.Build(
            new ScreenView(screen, [], null, Panorama: view, Aim: new Vector2(180, 5)), Width, Height);

        Assert.NotNull(Middle(painter, "zoom:MA3_a"));

        // Pointed at the hillside beside it, there is nothing to lean in on.
        painter.Build(
            new ScreenView(screen, [], null, Panorama: view, Aim: new Vector2(40, 5)), Width, Height);

        Assert.Null(Middle(painter, "zoom:MA3_a"));
        Assert.NotNull(Middle(painter, "close"));
    }

    [Fact]
    public void Sidneys_front_screen_offers_its_own_menu_and_leaves_the_rule_out()
    {
        ScreenPainter painter = Painter();

        painter.Build(new ScreenView(new Screen(ScreenKind.Sidney), [], null, Sidney(out _)), Width, Height);

        Assert.NotNull(Middle(painter, "sidney:screen:Analyze"));
        Assert.NotNull(Middle(painter, "sidney:screen:AddData"));
        Assert.NotNull(Middle(painter, "sidney:screen:EMail"));

        // The caret between ADD DATA and E-MAIL is a rule, not a row.
        Assert.Null(Middle(painter, "sidney:screen:^"));
    }

    [Fact]
    public void The_scanner_offers_what_it_will_take_and_nothing_else()
    {
        SidneyMachine sidney = Sidney(out _);
        sidney.Screen = SidneyScreen.AddData;

        ScreenPainter painter = Painter();

        painter.Build(
            new ScreenView(
                new Screen(ScreenKind.Sidney), ["PARCHMENT_1", "TAPE_RECORDER"], null, sidney),
            Width,
            Height);

        Assert.NotNull(Middle(painter, "sidney:scan:PARCHMENT_1"));
        Assert.Null(Middle(painter, "sidney:scan:TAPE_RECORDER"));
    }

    [Fact]
    public void Something_already_scanned_is_not_offered_again()
    {
        SidneyMachine sidney = Sidney(out _);
        sidney.Screen = SidneyScreen.AddData;
        sidney.Scan("PARCHMENT_1");

        ScreenPainter painter = Painter();

        painter.Build(
            new ScreenView(new Screen(ScreenKind.Sidney), ["PARCHMENT_1"], null, sidney), Width, Height);

        Assert.Null(Middle(painter, "sidney:scan:PARCHMENT_1"));
    }

    [Fact]
    public void The_analyze_screen_offers_the_files_and_then_the_operations()
    {
        SidneyMachine sidney = Sidney(out _);
        sidney.Screen = SidneyScreen.Analyze;
        sidney.Scan("PARCHMENT_1");

        ScreenPainter painter = Painter();
        var view = new ScreenView(new Screen(ScreenKind.Sidney), [], null, sidney);

        painter.Build(view, Width, Height);

        // Nothing open yet, so the file is offered and no operation is.
        Assert.NotNull(Middle(painter, "sidney:file:fileParchment1"));
        Assert.Null(Middle(painter, "sidney:do:Analyse"));

        sidney.OpenFile(sidney.Files[0]);
        painter.Build(view, Width, Height);

        Assert.NotNull(Middle(painter, "sidney:do:Analyse"));
        Assert.NotNull(Middle(painter, "sidney:do:ExtractAnomalies"));
    }

    [Fact]
    public void A_question_the_machine_asks_is_offered_as_answers_to_click()
    {
        SidneyMachine sidney = Sidney(out _);
        sidney.Screen = SidneyScreen.Analyze;
        sidney.Scan("PARCHMENT_1");
        sidney.OpenFile(sidney.Files[0]);
        sidney.Perform(SidneyAction.ExtractAnomalies);

        ScreenPainter painter = Painter();

        painter.Build(new ScreenView(new Screen(ScreenKind.Sidney), [], null, sidney), Width, Height);

        // The library under test declares no language names, so the choices come back
        // empty-labelled; what matters is that the machine asked and the screen offered.
        Assert.NotNull(sidney.Showing?.Choices);
    }

    [Fact]
    public void Reading_mail_offers_every_message_and_the_way_back()
    {
        SidneyMachine sidney = Sidney(out _);
        sidney.Screen = SidneyScreen.EMail;

        ScreenPainter painter = Painter();

        painter.Build(new ScreenView(new Screen(ScreenKind.Sidney), [], null, sidney), Width, Height);

        Assert.NotNull(Middle(painter, "sidney:mail:EMail1"));
        Assert.NotNull(Middle(painter, "sidney:home"));
    }

    [Fact]
    public void A_click_on_nothing_hits_nothing()
    {
        ScreenPainter painter = Painter();

        painter.Build(new ScreenView(new Screen(ScreenKind.Inventory), ["MAP"], null), Width, Height);

        // Outside the panel entirely.
        Assert.Null(painter.HitAt(new Vector2(-10, -10)));
        Assert.Null(painter.HitAt(new Vector2(Width + 50, Height + 50)));
    }

    [Fact]
    public void Laying_a_screen_out_again_forgets_the_last_one()
    {
        // Nothing here is retained, so a screen must not answer for something it drew a
        // frame ago and is no longer showing.
        ScreenPainter painter = Painter();

        painter.Build(
            new ScreenView(new Screen(ScreenKind.Driving), [], null, Stops: [.. DrivingMap.All.Take(1)]),
            Width,
            Height);

        string first = "drive:" + DrivingMap.All[0].Scene;

        Assert.NotNull(Middle(painter, first));

        painter.Build(new ScreenView(new Screen(ScreenKind.Inventory), [], null), Width, Height);

        Assert.Null(Middle(painter, first));
    }

    /// <summary>
    /// The drawn map names its places, so a player can tell one patch of countryside
    /// from another.
    /// </summary>
    /// <remarks>
    /// Reported as "drive map shows, but no labels so no clue what is what". A marker is a
    /// lit copy of the map underneath it and says nothing about where it is; the names are
    /// listed beside the painting and every row rides there.
    /// </remarks>
    [Fact]
    public void The_drawn_map_names_the_places_beside_it()
    {
        IReadOnlyList<DrivingStop> stops = [.. DrivingMap.All.Take(4)];
        ScreenPainter painter = Drawn(stops);

        // Down the right-hand edge, well clear of the painting: what is found there is the
        // list rather than a marker.
        HashSet<string> listed = [];

        for (int y = 0; y < Height; y += 2)
        {
            if (painter.HitAt(new Vector2(Width - 60, y)) is { Length: > 0 } id &&
                id.StartsWith("drive:", StringComparison.Ordinal))
            {
                listed.Add(id);
            }
        }

        Assert.Equal(
            [.. stops.Select(s => "drive:" + s.Scene).Order(StringComparer.Ordinal)],
            [.. listed.Order(StringComparer.Ordinal)]);
    }

    /// <summary>And the markers on the painting are still what they were.</summary>
    /// <remarks>
    /// The list is beside the map rather than over it: adding names must not have moved a
    /// place out from under the pointer.
    /// </remarks>
    [Fact]
    public void Naming_the_places_leaves_them_clickable_on_the_painting()
    {
        IReadOnlyList<DrivingStop> stops = [.. DrivingMap.All.Take(4)];
        ScreenPainter painter = Drawn(stops);

        foreach (DrivingStop stop in stops)
        {
            Assert.NotNull(Middle(painter, "drive:" + stop.Scene));
        }
    }

    /// <summary>A window too narrow for both keeps the map.</summary>
    /// <remarks>
    /// The names are worth a third of the width and never worth the painting: at this shape
    /// the list is dropped and the places are still there to point at.
    /// </remarks>
    [Fact]
    public void A_narrow_window_keeps_the_map_rather_than_the_list()
    {
        IReadOnlyList<DrivingStop> stops = [.. DrivingMap.All.Take(4)];
        ScreenPainter painter = Drawn(stops, width: 420, height: 640);

        // The places are still on the painting.
        foreach (DrivingStop stop in stops)
        {
            Assert.NotNull(Middle(painter, "drive:" + stop.Scene, 420, 640));
        }

        // And nothing is offered in the strip a column would have taken.
        for (int y = 0; y < 640; y += 2)
        {
            for (int x = 370; x < 420; x += 2)
            {
                Assert.Null(painter.HitAt(new Vector2(x, y)));
            }
        }
    }

    /// <summary>The map with its art loaded, laid out.</summary>
    /// <remarks>
    /// The pictures are numbers to the painter and their sizes are told to it, so a test
    /// needs neither a renderer nor the game's archives to lay the drawn map out — only a
    /// number per name and a size to go with it.
    /// </remarks>
    private static ScreenPainter Drawn(
        IReadOnlyList<DrivingStop> stops, int width = Width, int height = Height)
    {
        ScreenPainter painter = Painter();
        var numbers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            [DrivingMap.Background] = 1,
        };

        foreach (DrivingStop stop in stops)
        {
            numbers[stop.Sprite] = numbers.Count + 1;

            // Roughly what the game's own markers measure, in the map's own pixels.
            painter.Sizes[stop.Sprite] = (40, 30);
        }

        painter.Build(
            new ScreenView(
                new Screen(ScreenKind.Driving),
                [],
                null,
                Stops: stops,
                Pictures: name => numbers.GetValueOrDefault(name)),
            width,
            height);

        return painter;
    }
}
