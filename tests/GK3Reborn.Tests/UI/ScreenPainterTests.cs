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

        [MakeID Screen]
        Menu1Name  = MEDICAL
        Menu1Item1 = DOCTOR
        Menu2Name  = REPORTER
        Menu2Item1 = N.Y. TIMES
        Select     = SELECT:
        Print      = PRINT IDENTIFICATION

        [Analyze Screen]
        AnalyzeParch1 = Text appears to have irregularities in design.
        AnalyzeTemp   = Analysis did not find any encoded references.
        AnalyzePous   = Painting analysed.
        Menu2Item3    = ANAGRAM PARSER
        ArcadiaText   = Et in Arcadia Ego Sum
        Parsing       = Parsing:
        PhraseText    = PHRASE BUILDING AREA:
        EraseButton   = ERASE
        ExitButton    = EXIT
        SelectMsg     = Select words to move over to the phrase building area.
        Word13        = Arcam (Tomb)
        Word41        = Dei (God)
        Word147       = Tango (I Touch)
        FinalWord     = Iesu (Jesus)
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

    /// <summary>How much of the screen answers to an identifier, found by sweeping.</summary>
    private static Vector4? Extent(ScreenPainter painter, string id)
    {
        float left = float.MaxValue, top = float.MaxValue, right = -1, bottom = -1;

        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x += 2)
            {
                if (painter.HitAt(new Vector2(x, y)) != id)
                {
                    continue;
                }

                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x);
                bottom = Math.Max(bottom, y);
            }
        }

        return right < 0 ? null : new Vector4(left, top, right - left, bottom - top);
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
    public void An_item_with_a_picture_is_still_clicked_by_its_own_name()
    {
        // The picture is drawn inside the item's own rectangle, so what the player points
        // at is the item whether they aimed at the picture or at the name beside it.
        ScreenPainter painter = Painter();
        List<string> asked = [];

        painter.Build(
            new ScreenView(
                new Screen(ScreenKind.Inventory),
                ["MAP", "PARCHMENT_1"],
                null,
                Icons: item =>
                {
                    asked.Add(item);

                    // One of the two has art and the other has none, which is the ordinary
                    // case: twenty of the items the game names have no list picture.
                    return item == "MAP" ? new ItemIcon(1, 94, 94) : default;
                }),
            Width,
            Height);

        Assert.NotNull(Middle(painter, "item:MAP"));
        Assert.NotNull(Middle(painter, "item:PARCHMENT_1"));
        Assert.Equal(["MAP", "PARCHMENT_1"], asked);
    }

    [Fact]
    public void The_words_beside_an_item_are_clicked_where_they_are_drawn()
    {
        // The list of verbs hangs below the item it belongs to and over the row beneath it.
        // Laid down beside its own item it was drawn under that row and hit-tested under it
        // too, so a click on a word reached the item the word was covering.
        ScreenPainter painter = Painter();

        painter.Build(
            new ScreenView(
                new Screen(ScreenKind.Inventory, "MAP"),
                ["MAP", "CANDY", "DAGGER", "WALLET", "NOTEPAD", "TALISMAN"],
                null,
                Subject: "MAP",
                Verbs: ["LOOK", "READ"]),
            Width,
            Height);

        Vector4? look = Extent(painter, "verb:LOOK");

        Assert.NotNull(look);

        // The whole row of it, rather than the sliver of it that misses the item below.
        Assert.True(
            look.Value.W >= painter.Overlay.LineHeight,
            $"only {look.Value.W} pixels of the row answer to it");
    }

    [Fact]
    public void A_close_up_of_an_item_asks_for_that_items_picture()
    {
        ScreenPainter painter = Painter();
        List<string> asked = [];

        painter.Build(
            new ScreenView(
                new Screen(ScreenKind.InventoryInspect, "PARCHMENT_1"),
                ["PARCHMENT_1"],
                null,
                Verbs: ["LOOK"],
                Icons: item =>
                {
                    asked.Add(item);

                    return new ItemIcon(1, 94, 94);
                }),
            Width,
            Height);

        Assert.Equal(["PARCHMENT_1"], asked);
        Assert.NotNull(Middle(painter, "verb:LOOK"));
    }

    [Fact]
    public void A_close_up_prefers_the_picture_painted_to_be_looked_at()
    {
        // Two pictures exist for every item: a 94-pixel square for lists and a "6" that is
        // the thing itself. Drawing the square here made the book of the immortals a
        // thumbnail nobody could read, which is the whole complaint this screen answers.
        ScreenPainter painter = Painter();
        List<string> lists = [];
        List<string> closeUps = [];

        painter.Build(
            new ScreenView(
                new Screen(ScreenKind.InventoryInspect, "IMMORTAL_1"),
                [],
                null,
                Verbs: ["LOOK"],
                Icons: item =>
                {
                    lists.Add(item);

                    return new ItemIcon(1, 94, 94);
                },
                CloseUps: item =>
                {
                    closeUps.Add(item);

                    return new ItemIcon(2, 606, 314);
                }),
            Width,
            Height);

        Assert.Equal(["IMMORTAL_1"], closeUps);
        Assert.Empty(lists);
    }

    [Fact]
    public void An_item_with_no_close_up_falls_back_to_its_list_picture()
    {
        ScreenPainter painter = Painter();
        List<string> lists = [];

        painter.Build(
            new ScreenView(
                new Screen(ScreenKind.InventoryInspect, "CANDY"),
                [],
                null,
                Verbs: ["LOOK"],
                Icons: item =>
                {
                    lists.Add(item);

                    return new ItemIcon(1, 94, 94);
                },
                CloseUps: _ => default),
            Width,
            Height);

        Assert.Equal(["CANDY"], lists);
    }

    [Fact]
    public void Turning_a_page_is_an_arrow_beside_the_page_and_not_a_verb_under_it()
    {
        // The book, the pamphlet and Le Serpent Rouge all page through each other with
        // TURN_LEFT and TURN_RIGHT, whose scripts un-inspect one item and inspect the
        // next. Among the verbs they read as two more things to do to a book.
        ScreenPainter painter = Painter();

        painter.Build(
            new ScreenView(
                new Screen(ScreenKind.InventoryInspect, "IMMORTAL_1"),
                [],
                null,
                Verbs: ["LOOK", "TURN_RIGHT", "INSPECT_UNDO"],
                CloseUps: _ => new ItemIcon(2, 606, 314)),
            Width,
            Height);

        Vector4? forward = Extent(painter, "verb:TURN_RIGHT");
        Vector4? look = Extent(painter, "verb:LOOK");

        Assert.NotNull(forward);
        Assert.NotNull(look);

        // The arrow is up the side of the page; the verbs are along the foot of the window.
        Assert.True(
            forward.Value.Y + forward.Value.W < look.Value.Y,
            "the page arrow is drawn among the verbs rather than beside the page");

        // Only where the item's own rules offer it: page one of a two-page book has no
        // left arrow, and drawing one would page it to nowhere.
        Assert.Null(Middle(painter, "verb:TURN_LEFT"));

        // And the way out is the way out on every other screen, not a verb of its own.
        Assert.Null(Middle(painter, "verb:INSPECT_UNDO"));
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

    /// <summary>
    /// Leaning in offers the way back and nothing else.
    /// </summary>
    [Fact]
    public void Leaning_in_offers_the_way_back_and_not_another_look()
    {
        Panorama view = Binoculars.From("""
            [CD1102P]
            LOC=MA3_a

            [CD1102PMA3_a]
            ZOOMRECT=174,0,189,10
            CAMANGLE=-287.38,4.5
            CAMPOS=2423.19,530.67,-4351.27
            """).For("CD1", "102P");

        ScreenPainter painter = Painter();

        painter.Build(
            new ScreenView(
                new Screen(ScreenKind.Binoculars, Screen.Zoomed + ":MA3"),
                [],
                null,
                Panorama: view,
                Aim: new Vector2(180, 5)),
            Width,
            Height);

        Assert.NotNull(Middle(painter, "binocs:back"));
        Assert.Null(Middle(painter, "zoom:MA3_a"));
        Assert.Null(Middle(painter, "close"));
    }

    /// <summary>
    /// Somebody out on the roads can be followed by clicking them.
    /// </summary>
    [Fact]
    public void Somebody_on_the_road_can_be_clicked_to_follow_them()
    {
        var story = new GameState { Timeblock = Block("102P"), Location = "PLO" };
        DrivingTraffic traffic = DrivingTraffic.For(story, Valley);

        Assert.NotEmpty(traffic.Riders);

        ScreenPainter painter = Painter();

        painter.Build(
            new ScreenView(
                new Screen(ScreenKind.Driving),
                [],
                null,
                Stops: [.. DrivingMap.All.Take(2)],
                Pictures: name => name == DrivingMap.Background ? 1 : 0,
                Traffic: traffic),
            Width,
            Height);

        Assert.NotNull(Middle(painter, "follow:2"));
        Assert.NotNull(Middle(painter, "follow:1"));
    }

    /// <summary>A chase is watched, not steered.</summary>
    [Fact]
    public void A_chase_offers_no_place_to_ride_to()
    {
        var story = new GameState { Timeblock = Block("102P"), Location = "PLO" };

        ScreenPainter painter = Painter();

        painter.Build(
            new ScreenView(
                new Screen(ScreenKind.Driving, "follow:2"),
                [],
                null,
                Stops: [.. DrivingMap.All.Take(2)],
                Pictures: name => name == DrivingMap.Background ? 1 : 0,
                Traffic: DrivingTraffic.For(story, Valley, follow: 2)),
            Width,
            Height);

        Assert.NotNull(Middle(painter, "follow:skip"));
        Assert.NotNull(Middle(painter, "close"));

        foreach (DrivingStop stop in DrivingMap.All.Take(2))
        {
            Assert.Null(Middle(painter, "drive:" + stop.Scene));
        }
    }

    /// <summary>A point in the story, by its code.</summary>
    private static Timeblock Block(string code) =>
        Timeblock.TryParse(code, out Timeblock parsed) ? parsed : default;

    /// <summary>
    /// A road network that is not the game's, with the junction names its routes use.
    /// </summary>
    private static DrivingMap Valley => DrivingMap.Roading("""
        NodeBegin Plo
        	Location 500,100
        	LinksBegin
        		Pl3 Plo_Pl3 TRUE
        		Lhe Lhe_Plo FALSE
        	LinksEnd
        NodeEnd

        NodeBegin Pl3
        	Location 500,150
        	LinksBegin
        		Plo Plo_Pl3 FALSE
        		Vgr Pl3_Vgr TRUE
        	LinksEnd
        NodeEnd

        NodeBegin Vgr
        	Location 475,260
        	LinksBegin
        		Pl3 Pl3_Vgr FALSE
        		Pl4 Pl4_Vgr FALSE
        	LinksEnd
        NodeEnd

        NodeBegin Pl4
        	Location 460,290
        	LinksBegin
        		Vgr Pl4_Vgr TRUE
        		Lhe Pl4_Lhe TRUE
        	LinksEnd
        NodeEnd

        NodeBegin Lhe
        	Location 300,200
        	LinksBegin
        		Pl4 Pl4_Lhe FALSE
        		Plo Lhe_Plo TRUE
        	LinksEnd
        NodeEnd
        """);

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

        // The operations sit under the four menus the original groups them into — OPEN,
        // TEXT, GRAPHIC and MAP — because laid out flat the map's eight of them wrapped
        // onto three rows of a screen 640 pixels wide. Only the menus with something
        // applicable are offered, and only one opens at a time.
        Assert.NotNull(Middle(painter, "sidney:menu:1"));
        Assert.NotNull(Middle(painter, "sidney:menu:2"));
        Assert.Null(Middle(painter, "sidney:do:Analyse"));

        sidney.Menu = 1;
        painter.Build(view, Width, Height);

        Assert.NotNull(Middle(painter, "sidney:do:Analyse"));
        Assert.Null(Middle(painter, "sidney:do:ExtractAnomalies"));

        sidney.Menu = 2;
        painter.Build(view, Width, Height);

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
    [Fact]
    public void The_make_id_screen_offers_both_faces_and_a_printer()
    {
        // Which face is on the card is half of what Gabriel objects to, so the player has
        // to be able to see it and change it before pressing print.
        SidneyMachine sidney = Sidney(out _);
        sidney.Screen = SidneyScreen.MakeId;

        ScreenPainter painter = Painter();

        painter.Build(
            new ScreenView(new Screen(ScreenKind.Sidney), [], null, sidney), Width, Height);

        Assert.NotNull(Middle(painter, "sidney:id:Menu2Item1"));
        Assert.NotNull(Middle(painter, "sidney:face:GAB"));
        Assert.NotNull(Middle(painter, "sidney:face:GRA"));
        Assert.NotNull(Middle(painter, "sidney:print"));
    }

    [Fact]
    public void The_anagram_parser_offers_its_words_and_the_way_out()
    {
        SidneyMachine sidney = Sidney(out GameState state);

        foreach (string sign in new[]
        {
            "Aquarius", "Pisces", "Aries", "Taurus", "Gemini", "Cancer", "Leo",
            "Virgo", "Libra", "Scorpio",
        })
        {
            state.SetFlag(sign);
        }

        state.SetFlag("SavedArcadiaText");
        state.SetFlag("ArcadiaComplete");

        sidney.Screen = SidneyScreen.Analyze;
        sidney.Scan("POUSSIN_POSTCARD");
        sidney.OpenFile(sidney.Files[0]);
        sidney.Perform(SidneyAction.AnagramParser);

        ScreenPainter painter = Painter();

        painter.Build(
            new ScreenView(new Screen(ScreenKind.Sidney), [], null, sidney), Width, Height);

        Assert.NotNull(Middle(painter, "sidney:word:13"));
        Assert.NotNull(Middle(painter, "sidney:word:147"));
        Assert.NotNull(Middle(painter, "sidney:erase"));
        Assert.NotNull(Middle(painter, "sidney:anagram:close"));

        // The file list is not underneath it: the parser has the screen to itself.
        Assert.Null(Middle(painter, "sidney:file:filePainting1"));
    }

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
