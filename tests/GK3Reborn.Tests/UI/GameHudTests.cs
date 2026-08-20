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
        string? caption = null) =>
        new(noun, verbs ?? ["LOOK", "OPEN"], "LOOK", at, menu, caption is null ? null : "GABRIEL",
            caption, [], null, InventoryOpen: true, "R25 - 110A");

    [Fact]
    public void An_empty_room_still_draws_the_bars_that_are_always_there()
    {
        GameHud hud = Hud();
        hud.Build(State(noun: null, verbs: []), 800, 600);

        // The place, the hint, and the inventory strip. Never nothing: a frame that draws
        // no interface at all is indistinguishable from one where the interface broke.
        Assert.NotEmpty(hud.Overlay.Quads);
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
}
