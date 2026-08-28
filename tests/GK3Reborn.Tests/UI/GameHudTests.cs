using System.Numerics;
using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Formats.Ui;
using GK3Reborn.Foundation.Diagnostics;
using GK3Reborn.Rendering;
using GK3Reborn.UI;
using Xunit;

namespace GK3Reborn.Tests.UI;

/// <summary>
/// Tests for the interface's layout.
/// </summary>
/// <remarks>
/// What matters here is not how it looks but that what you click is what you saw. The
/// menu's rows are laid out and hit-tested from the same pass, so these check that the two
/// agree — and that the interface stays inside the window, which is the one way a label
/// that follows the pointer can go wrong.
/// </remarks>
public sealed class GameHudTests
{
    /// <summary>A font of fixed four-pixel characters.</summary>
    private static FontFile Font()
    {
        const int Width = 128;
        const int Height = 12;
        byte[] pixels = new byte[Width * Height * 4];

        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i + 3] = 255;
        }

        // A marker every four pixels along the top row: thirty-one glyphs of equal width.
        for (int x = 1; x < Width; x += 4)
        {
            pixels[x * 4] = 255;
        }

        var sheet = new DecodedImage(Width, Height, pixels, HasAlpha: false, "test");
        string characters = new(
            [.. "ABCDEFGHIJKLMNOPQRSTUVWXYZ abcdefghijklmnopqrstuvwxyz".Distinct().Take(31)]);

        return FontFile.Parse($"Font={characters}\n", sheet, "TEST", new DiagnosticBag());
    }

    private static GameHud Hud() => new(new Overlay(OverlayAtlas.Build(Font())));

    private static HudState State(
        string? noun = "DOOR",
        IReadOnlyList<string>? verbs = null,
        bool menu = false,
        Vector2 at = default,
        Vector2? menuAt = null,
        int index = 0,
        string? caption = null,
        IReadOnlyList<string>? items = null,
        IReadOnlyList<string>? carrying = null,
        Func<string, ItemIcon>? icons = null,
        Func<string, bool, ItemIcon>? verbIcons = null) =>
        new(noun, verbs ?? ["LOOK", "OPEN"], "LOOK", at, menu, index, menuAt ?? at,
            caption is null ? null : "GABRIEL", caption, carrying ?? [], null,
            InventoryOpen: true, "R25 - 110A", null, null, items, null, icons, verbIcons);

    [Fact]
    public void An_empty_room_still_draws_the_bars_that_are_always_there()
    {
        GameHud hud = Hud();
        hud.Build(State(noun: null, verbs: []), 800, 600);

        // The place, the hint, and the inventory strip. Never nothing: a frame that draws
        // no interface at all is indistinguishable from one where the interface broke.
        Assert.NotEmpty(hud.Overlay.Quads);
    }

    /// <summary>The foot of the screen is the room, now that nothing is drawn over it.</summary>
    /// <remarks>
    /// The inventory strip used to be an opaque bar across the bottom, and it lay over
    /// exactly the part of the picture where the floor at the player's feet is drawn — so
    /// every click on the ground in front of you was tested against it first and a good many
    /// were swallowed. It duplicated the right-click menu, which already says which of your
    /// things a noun will take, so it went.
    /// </remarks>
    [Fact]
    public void The_foot_of_the_screen_is_the_room()
    {
        GameHud hud = Hud();
        hud.Build(State(), 800, 600);

        Assert.False(
            hud.OverInterface(new Vector2(400, 599)),
            "the floor at the player's feet is the room, not the interface");

        Assert.False(
            hud.OverInterface(new Vector2(400, 300)),
            "the middle of the screen is the room");
    }

    [Fact]
    public void The_label_stays_inside_the_window_when_the_pointer_is_at_the_edge()
    {
        GameHud hud = Hud();
        hud.Build(State(at: new Vector2(799, 599)), 800, 600);

        foreach (OverlayQuad quad in hud.Overlay.Quads)
        {
            Assert.True(quad.Destination.X >= 0, "a rectangle started left of the window");
            Assert.True(
                quad.Destination.X + quad.Destination.Z <= 800.5f,
                "a rectangle ran off the right of the window");
        }
    }

    [Fact]
    public void A_verb_can_be_clicked_where_it_was_drawn()
    {
        GameHud hud = Hud();
        var at = new Vector2(100, 100);

        hud.Build(State(menu: true, at: at), 800, 600);

        // The rows are under the heading, one line apart. Asking for the middle of each
        // must give back the verb that was written there.
        string?[] found =
        [
            hud.VerbAt(new Vector2(at.X + 10, at.Y + hud.Overlay.LineHeight + 12)),
            hud.VerbAt(new Vector2(at.X + 10, at.Y + (hud.Overlay.LineHeight * 2) + 22)),
        ];

        Assert.Equal(["LOOK", "OPEN"], found);
    }

    [Fact]
    public void The_menu_stays_where_it_was_opened_when_the_pointer_moves()
    {
        // Anchoring it to the live pointer means it slides away from the hand reaching for
        // it, and no row can ever be clicked.
        GameHud hud = Hud();
        var opened = new Vector2(100, 100);

        hud.Build(State(menu: true, at: opened, menuAt: opened), 800, 600);
        string? before = hud.VerbAt(new Vector2(opened.X + 10, opened.Y + hud.Overlay.LineHeight + 12));

        // The pointer has travelled towards the row; the menu must not have travelled too.
        hud.Build(
            State(menu: true, at: new Vector2(140, 160), menuAt: opened),
            800,
            600);

        Assert.Equal("LOOK", before);
        Assert.Equal(
            before,
            hud.VerbAt(new Vector2(opened.X + 10, opened.Y + hud.Overlay.LineHeight + 12)));
    }

    [Fact]
    public void The_row_under_a_point_can_be_found_by_index()
    {
        // What lets the pointer move the same selection the wheel moves, so the highlight
        // and the click never disagree about which verb is chosen.
        GameHud hud = Hud();
        var at = new Vector2(100, 100);

        hud.Build(State(menu: true, at: at, menuAt: at), 800, 600);

        Assert.Equal(0, hud.RowAt(new Vector2(at.X + 10, at.Y + hud.Overlay.LineHeight + 12)));
        Assert.Equal(
            1,
            hud.RowAt(new Vector2(at.X + 10, at.Y + (hud.Overlay.LineHeight * 2) + 22)));
        Assert.Equal(-1, hud.RowAt(new Vector2(500, 400)));
    }

    [Fact]
    public void The_chosen_row_is_the_one_the_selection_names_not_the_one_under_the_pointer()
    {
        // Otherwise the wheel can move the selection and nothing on screen changes.
        GameHud hud = Hud();
        var at = new Vector2(100, 100);

        hud.Build(State(menu: true, at: at, menuAt: at, index: 0), 800, 600);
        int first = hud.Overlay.Quads.Count;

        hud.Build(State(menu: true, at: at, menuAt: at, index: 1), 800, 600);

        // Same number of rectangles either way — the highlight moved rather than appearing.
        Assert.Equal(first, hud.Overlay.Quads.Count);
    }

    [Fact]
    public void Clicking_away_from_the_menu_chooses_nothing()
    {
        GameHud hud = Hud();
        hud.Build(State(menu: true, at: new Vector2(100, 100)), 800, 600);

        Assert.Null(hud.VerbAt(new Vector2(500, 400)));
    }

    [Fact]
    public void Nothing_can_be_clicked_when_the_menu_is_closed()
    {
        GameHud hud = Hud();
        hud.Build(State(at: new Vector2(100, 100)), 800, 600);

        Assert.Null(hud.VerbAt(new Vector2(110, 130)));
    }

    [Fact]
    public void A_caption_adds_rectangles_and_no_caption_does_not()
    {
        GameHud hud = Hud();

        hud.Build(State(), 800, 600);
        int quiet = hud.Overlay.Quads.Count;

        hud.Build(State(caption: "He said something about the abbey"), 800, 600);

        Assert.True(
            hud.Overlay.Quads.Count > quiet,
            "a spoken line drew no more than a silent one");
    }

    [Fact]
    public void The_display_list_is_rebuilt_rather_than_added_to()
    {
        // It is a function of what the game is doing, so two identical frames must produce
        // identical lists. Anything else means the interface accumulates.
        GameHud hud = Hud();

        hud.Build(State(), 800, 600);
        int first = hud.Overlay.Quads.Count;

        hud.Build(State(), 800, 600);

        Assert.Equal(first, hud.Overlay.Quads.Count);
    }

    [Fact]
    public void The_menu_is_as_wide_as_its_heading_when_the_noun_is_the_longest_thing_in_it()
    {
        // Reported: right-clicking the coffee pot drew "Coffee Pot" over a background that
        // stopped after "Coffee". The panel was sized to the widest verb, and a noun is
        // very often longer than any verb offered for it.
        GameHud hud = Hud();
        var at = new Vector2(100, 100);

        hud.Build(
            State(noun: "COFFEE POT", verbs: ["LOOK", "POUR"], menu: true, at: at),
            800, 600);

        float heading = hud.Overlay.Measure("Coffee Pot");

        // The widest rectangle drawn at the menu's own left edge is its background.
        float panel = 0;

        foreach (OverlayQuad quad in hud.Overlay.Quads)
        {
            if (Math.Abs(quad.Destination.X - at.X) < 0.5f)
            {
                panel = Math.Max(panel, quad.Destination.Z);
            }
        }

        Assert.True(
            panel > heading,
            $"the panel is {panel} wide and the heading needs {heading}");

        // And padded rather than exactly the text's width, or the letters touch the edge.
        Assert.True(panel >= heading + 8, $"no padding: {panel} against {heading}");
    }

    [Fact]
    public void The_menu_offers_the_bag_behind_one_row_rather_than_beside_the_verbs()
    {
        // An action file writes "use the wallet on Buthane" as a rule whose verb is
        // WALLET, so an item and a verb are indistinguishable in the data. Listed flat
        // they read as the same kind of thing, and late in the game there are thirty of
        // them against three real verbs.
        GameHud hud = Hud();
        var at = new Vector2(100, 100);

        hud.Build(
            State(noun: "BUTHANE", verbs: ["LOOK", "TALK"], menu: true, at: at,
                  items: ["WALLET", "BINOCULARS"]),
            800, 600);

        Assert.Equal(3, hud.RowCount);
        Assert.Equal("LOOK", hud.RowNamed(0));
        Assert.Equal("TALK", hud.RowNamed(1));
        Assert.Equal(GameHud.UseRow, hud.RowNamed(2));
    }

    [Fact]
    public void Selecting_that_row_opens_the_things_in_it()
    {
        GameHud hud = Hud();
        var at = new Vector2(100, 100);

        hud.Build(
            State(noun: "BUTHANE", verbs: ["LOOK", "TALK"], menu: true, at: at, index: 2,
                  items: ["WALLET", "BINOCULARS"]),
            800, 600);

        Assert.Equal(5, hud.RowCount);
        Assert.Equal("WALLET", hud.RowNamed(3));
        Assert.Equal("BINOCULARS", hud.RowNamed(4));
    }

    [Fact]
    public void An_item_row_can_be_clicked_where_it_was_drawn()
    {
        // The whole point of laying out and hit-testing in one pass. A second column that
        // draws in one place and answers in another is worse than no second column.
        GameHud hud = Hud();
        var at = new Vector2(100, 100);

        hud.Build(
            State(noun: "BUTHANE", verbs: ["LOOK"], menu: true, at: at, index: 1,
                  items: ["WALLET"]),
            800, 600);

        int row = hud.RowCount - 1;

        Assert.Equal("WALLET", hud.RowNamed(row));

        // Its middle, found from where the hit test says it is.
        Vector2 middle = hud.RowMiddle(row);

        Assert.Equal(row, hud.RowAt(middle));
    }

    [Fact]
    public void An_item_row_with_a_picture_is_still_clicked_where_it_was_drawn()
    {
        // The picture widens the column and moves the name along it, and both the drawing
        // and the hit testing come out of the same pass — so what this guards is that they
        // stayed the same pass.
        GameHud hud = Hud();
        var at = new Vector2(100, 100);

        hud.Build(
            State(noun: "BUTHANE", verbs: ["LOOK"], menu: true, at: at, index: 1,
                  items: ["WALLET"],
                  icons: item => item == "WALLET" ? new ItemIcon(1, 94, 94) : default),
            800, 600);

        int row = hud.RowCount - 1;

        Assert.Equal("WALLET", hud.RowNamed(row));
        Assert.Equal(row, hud.RowAt(hud.RowMiddle(row)));

        // And the picture itself was drawn, inside the row it belongs to.
        Assert.Contains(hud.Overlay.Quads, q => q.Picture == 1);
    }

    [Fact]
    public void A_menu_with_nothing_to_use_has_no_row_for_it()
    {
        GameHud hud = Hud();

        hud.Build(
            State(noun: "DOOR", verbs: ["LOOK", "OPEN"], menu: true, at: new Vector2(10, 10)),
            800, 600);

        Assert.Equal(2, hud.RowCount);
        Assert.Null(hud.RowNamed(2));
    }

    /// <summary>Nothing along the bottom answers to a click any more.</summary>
    /// <remarks>
    /// The pockets are a key away and a screen of their own, which is where a list of twelve
    /// things belongs. What this guards is that the strip is gone from the click path as well
    /// as from the picture — a bar that is invisible and still takes clicks would be the
    /// worst of both.
    /// </remarks>
    [Fact]
    public void No_inventory_slot_takes_a_click_along_the_bottom()
    {
        GameHud hud = Hud();

        hud.Build(State(carrying: ["WALLET", "BINOCULARS"]), 800, 600);

        Assert.Null(hud.ItemAt(new Vector2(40, 592)));
        Assert.Null(hud.ItemAt(new Vector2(400, 599)));
    }

    /// <summary>The original's own verb art: a 32-pixel square, one per verb.</summary>
    private static Func<string, bool, ItemIcon> VerbArt(params string[] drawn) =>
        (verb, lit) => Array.IndexOf(drawn, verb) >= 0
            ? new ItemIcon(lit ? 2 : 1, 32, 32)
            : default;

    [Fact]
    public void A_verb_with_a_picture_is_still_clicked_where_it_was_drawn()
    {
        // The picture makes the rows taller and pushes the words along them, and the
        // drawing and the hit testing come out of the same pass — so what this guards is
        // that they stayed the same pass.
        GameHud hud = Hud();
        var at = new Vector2(100, 100);

        hud.Build(
            State(menu: true, at: at, verbIcons: VerbArt("LOOK", "OPEN")), 800, 600);

        int row = hud.RowAt(hud.RowMiddle(1));

        Assert.Equal(1, row);
        Assert.Equal("OPEN", hud.RowNamed(row));
        Assert.Equal("OPEN", hud.VerbAt(hud.RowMiddle(1)));
    }

    [Fact]
    public void A_verbs_picture_is_drawn_at_the_size_it_was_painted()
    {
        // They are 32-pixel squares in the archives, and the row is built around one rather
        // than the other way about. Drawing them into a line-height row would resample
        // every icon in the game to fit a font.
        GameHud hud = Hud();
        var at = new Vector2(100, 100);

        hud.Build(State(menu: true, at: at, verbIcons: VerbArt("LOOK", "OPEN")), 800, 600);

        List<OverlayQuad> pictures = [.. hud.Overlay.Quads.Where(q => q.Picture > 0)];

        Assert.Equal(2, pictures.Count);
        Assert.All(pictures, q => Assert.Equal(32f * hud.Scale, q.Destination.Z));
        Assert.All(pictures, q => Assert.Equal(32f * hud.Scale, q.Destination.W));
    }

    [Fact]
    public void The_picked_out_row_is_drawn_with_the_lit_picture()
    {
        // The second thing the original's ring did with these: the icon itself brightens
        // under the pointer. Without it the only thing saying which row a click takes is
        // the bar behind the words.
        GameHud hud = Hud();
        var at = new Vector2(100, 100);

        hud.Build(
            State(menu: true, at: at, index: 1, verbIcons: VerbArt("LOOK", "OPEN")), 800, 600);

        List<int> pictures = [.. hud.Overlay.Quads.Where(q => q.Picture > 0).Select(q => q.Picture)];

        // One resting and one lit, and the lit one is the row the selection names.
        Assert.Equal([1, 2], pictures);
    }

    [Fact]
    public void A_verb_with_no_picture_keeps_the_words_in_line_with_the_ones_that_have()
    {
        // Three verbs in the game name no art, and the row that stands for the bag names
        // none either. A row that closed the gap would put its word where no other row's
        // word is, and the column would read as ragged rather than as sparse.
        GameHud hud = Hud();
        var at = new Vector2(100, 100);

        hud.Build(
            State(menu: true, at: at, verbs: ["LOOK", "CLICK"], verbIcons: VerbArt("LOOK")),
            800, 600);

        Assert.Single(hud.Overlay.Quads, q => q.Picture > 0);

        // Where each row's word starts, found in the band that row was drawn in.
        float Word(int row)
        {
            float middle = hud.RowMiddle(row).Y;

            return hud.Overlay.Quads
                .Where(q => q.Picture == 0 &&
                            q.Destination.X > at.X + 1 &&
                            q.Destination.Y < middle &&
                            q.Destination.Y + q.Destination.W > middle)
                .Min(q => q.Destination.X);
        }

        Assert.Equal(Word(0), Word(1), 1);
    }

    [Fact]
    public void A_menu_too_long_for_the_screen_is_no_taller_for_having_pictures()
    {
        // A character late in the game answers to thirty topics, and thirty rows built
        // around a 32-pixel icon reach past the bottom of a short window — where a row
        // cannot be clicked at all. The words are the floor and cannot shrink, so what the
        // art has to promise is that it never costs a row: below the height the text alone
        // needs, the icons give way instead.
        GameHud hud = Hud();
        string[] verbs = [.. Enumerable.Range(0, 30).Select(i => "TOPIC" + i)];

        float Panel(Func<string, bool, ItemIcon>? art)
        {
            hud.Build(
                State(menu: true, at: new Vector2(10, 10), verbs: verbs, verbIcons: art),
                800, 400);

            // The tallest rectangle drawn at the menu's own left edge is its background.
            return hud.Overlay.Quads
                .Where(q => Math.Abs(q.Destination.X - 10) < 0.5f)
                .Max(q => q.Destination.W);
        }

        Assert.Equal(Panel(null), Panel(VerbArt(verbs)), 1);

        // And the last row is still the one the hit test finds where it was drawn.
        Assert.Equal("TOPIC29", hud.RowNamed(hud.RowAt(hud.RowMiddle(29))));
    }
}
