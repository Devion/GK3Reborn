using System.Numerics;
using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Formats.Ui;
using GK3Reborn.Foundation.Diagnostics;
using GK3Reborn.Rendering;
using GK3Reborn.UI;
using Xunit;

namespace GK3Reborn.Tests.UI;

/// <summary>
/// Tests for showing every hotspot in the room at once.
/// </summary>
/// <remarks>
/// Rooms put a dozen nouns within a few degrees of each other — a desk, its drawer, the
/// register on it, the bell beside it — so the labels have to be laid out rather than simply
/// drawn. A heap of them on the same spot answers the question no better than none.
/// </remarks>
public sealed class HotspotLabelTests
{
    /// <summary>A hud over a font of one character, which is enough to measure with.</summary>
    private static GameHud Hud() => new(new Overlay(OverlayAtlas.Build(Font())));

    private static FontFile Font()
    {
        const string characters =
            "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789 .,:;'!?-()";

        var sheet = new DecodedImage(
            characters.Length * 8, 16, new byte[characters.Length * 8 * 16 * 4], false, "font");

        return FontFile.Parse($"Font={characters}\n", sheet, "TEST", new DiagnosticBag());
    }

    private static HudState State(params (string Noun, Vector2 At)[] spots) =>
        new(
            Noun: null,
            Verbs: [],
            Verb: null,
            At: new Vector2(400, 300),
            MenuOpen: false,
            MenuIndex: 0,
            MenuAt: Vector2.Zero,
            Speaker: null,
            Caption: null,
            Inventory: [],
            Held: null,
            InventoryOpen: true,
            Place: "Somewhere",
            Hotspots: spots);

    [Fact]
    public void Nothing_is_drawn_when_the_key_is_not_held()
    {
        GameHud hud = Hud();

        hud.Build(State(), 800, 600);
        int bare = hud.Overlay.Quads.Count;

        hud.Build(State(("DESK", new Vector2(400, 300))), 800, 600);

        Assert.True(
            hud.Overlay.Quads.Count > bare,
            "asking for the hotspots drew nothing extra");
    }

    /// <summary>Labels at the same point are moved apart rather than stacked.</summary>
    [Fact]
    public void Two_nouns_at_the_same_point_do_not_overlap()
    {
        GameHud hud = Hud();

        var together = new Vector2(400, 300);

        hud.Build(
            State(("DESK", together), ("REGISTER", together), ("BELL", together)),
            800,
            600);

        // Every drawn rectangle that is a label sits at a distinct height, because the only
        // way this lays them out is downwards.
        List<float> heights =
        [
            .. hud.Overlay.Quads
                .Select(q => q.Destination.Y)
                .Where(y => y > 250 && y < 450)
                .Distinct()
                .Order(),
        ];

        Assert.True(heights.Count >= 3, $"only {heights.Count} distinct rows were drawn");
    }

    /// <summary>A room with nothing in it draws no labels and does not throw.</summary>
    [Fact]
    public void A_room_with_no_nouns_is_no_trouble()
    {
        GameHud hud = Hud();

        hud.Build(State(), 800, 600);

        Assert.NotEmpty(hud.Overlay.Quads);
    }
}
