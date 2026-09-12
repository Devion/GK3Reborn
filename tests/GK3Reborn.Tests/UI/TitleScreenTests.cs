// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Numerics;
using GK3Reborn.Content;
using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Formats.Ui;
using GK3Reborn.Foundation.Diagnostics;
using GK3Reborn.Game;
using GK3Reborn.Formats.Rebarn;
using GK3Reborn.Rendering;
using GK3Reborn.Tools.Stages;
using GK3Reborn.UI;
using Xunit;

namespace GK3Reborn.Tests.UI;

/// <summary>
/// Tests for the title screen the port draws for itself, and for the row of buttons the
/// menu lays across the bottom of it.
/// </summary>
public sealed class TitleScreenTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "gk3r-title-" + Guid.NewGuid().ToString("N"));

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    // ---------------------------------------------------------------------------------
    // What the game has to have before the screen exists at all
    // ---------------------------------------------------------------------------------

    [Fact]
    public void The_screen_is_all_six_layers_or_none_of_them()
    {
        string directory = Path.Combine(_root, "menu");
        Directory.CreateDirectory(directory);

        // Five of the six. A menu missing its statue is not a cheaper menu.
        foreach (string layer in MenuArt.Layers.Take(5))
        {
            Paint(Path.Combine(directory, layer + ".png"), 8, 8);
        }

        MenuArt five = MenuArt.Open(null, directory, null);

        Assert.Equal(5, five.Count);
        Assert.False(five.Complete);
        Assert.Equal([MenuArt.Layers[^1]], five.Missing);
        Assert.Null(TitleScene.Build(five, (_, _) => 1));

        Paint(Path.Combine(directory, MenuArt.Layers[^1] + ".png"), 8, 8);

        MenuArt all = MenuArt.Open(null, directory, null);

        Assert.True(all.Complete);
        Assert.NotNull(TitleScene.Build(all, Uploader()));
    }

    [Fact]
    public void A_device_that_refuses_one_layer_gets_the_original_title_screen()
    {
        // Not five layers drawn and the sixth left out: the picture list is full, and the
        // answer to that is the screen the game already had.
        MenuArt art = Loose();
        int given = 0;

        Assert.Null(TitleScene.Build(art, (_, _) => ++given <= 4 ? given : 0));
    }

    [Fact]
    public void Every_layer_goes_on_the_device_under_a_name_of_its_own()
    {
        List<string> names = [];

        Assert.NotNull(TitleScene.Build(Loose(), (name, _) =>
        {
            names.Add(name);

            return names.Count;
        }));

        Assert.Equal(MenuArt.Layers.Count, names.Distinct(StringComparer.Ordinal).Count());

        // Prefixed, because these share the interface's picture list with the driving map,
        // the save slots' thumbnails and the timeblock cards.
        Assert.All(names, name => Assert.StartsWith("title:", name, StringComparison.Ordinal));
    }

    [Fact]
    public void A_layer_the_player_dropped_into_overrides_wins()
    {
        string workspace = Path.Combine(_root, "menu");
        Directory.CreateDirectory(workspace);

        foreach (string layer in MenuArt.Layers)
        {
            Paint(Path.Combine(workspace, layer + ".png"), 8, 8);
        }

        // Under a menu/ directory, which is what says which kind it is: the name alone is
        // "ANGEL", and the game has textures with names like that.
        string dropped = Path.Combine(_root, "overrides", "menu");
        Directory.CreateDirectory(dropped);
        Paint(Path.Combine(dropped, MenuArt.Statue + ".png"), 32, 16);

        ContentOverrides overrides = ContentOverrides.Open(Path.Combine(_root, "overrides"));

        MenuArt art = MenuArt.Open(null, workspace, overrides);

        Assert.True(art.Complete);
        Assert.Equal(32, art[MenuArt.Statue]!.Value.Width);
        Assert.Equal(8, art[MenuArt.Wall]!.Value.Width);
    }

    [Fact]
    public void The_layers_travel_in_the_shared_volume_and_are_read_back_out_of_it()
    {
        // What a player has: two .rebarn files and no workspace. The screen has to survive
        // the whole road -- the packer's plan, the volume, and the reader -- because a
        // development machine reads the same PNGs loose and would never notice it did not.
        string workspace = Path.Combine(_root, "ContentWorkspace");
        string menu = Path.Combine(workspace, "enhanced", "menu");

        Directory.CreateDirectory(menu);
        Directory.CreateDirectory(Path.Combine(workspace, "enhanced", "textures"));

        foreach (string layer in MenuArt.Layers)
        {
            Paint(Path.Combine(menu, layer + ".png"), 16, 12);
        }

        IReadOnlyList<PackKind> plan =
            [.. ContentPackStage.DefaultPlan.Where(kind => kind.Kind == RebarnKind.Menu)];

        Assert.Single(plan);

        string output = Path.Combine(_root, "packs");

        Assert.True(new ContentPackStage(_ => { }).Run(
            workspace, output, plan, texconv: "unused", useSizePlan: false));

        using RebarnContent packs = RebarnContent.Open(output);

        // Under a kind of their own, so that "ANGEL" here and a room texture called ANGEL
        // are two entries and not one.
        Assert.Equal(MenuArt.Layers.Count, packs.CountOf(RebarnKind.Menu));

        MenuArt art = MenuArt.Open(packs, string.Empty, null);

        Assert.True(art.Complete);
        Assert.Equal(16, art[MenuArt.Statue]!.Value.Width);
        Assert.NotNull(TitleScene.Build(art, Uploader()));
    }

    // ---------------------------------------------------------------------------------
    // What it draws
    // ---------------------------------------------------------------------------------

    [Fact]
    public void The_wall_covers_the_window_however_far_it_has_scrolled()
    {
        // The one thing an infinite scroll can get wrong that nothing else catches: a gap
        // at the seam, which is one frame in several hundred and is exactly the frame a
        // photograph is not taken on.
        TitleScene scene = Scene();

        for (int step = 0; step < 40; step++)
        {
            Overlay drawn = Draw(scene, 1920, 1080);

            float leftmost = float.MaxValue;
            float rightmost = float.MinValue;

            foreach (OverlayQuad quad in drawn.Quads.Where(q => q.Blend == OverlayBlend.Screen))
            {
                leftmost = MathF.Min(leftmost, quad.Destination.X);
                rightmost = MathF.Max(rightmost, quad.Destination.X + quad.Destination.Z);
            }

            Assert.True(leftmost <= 0f, $"the wall starts at {leftmost} after {step} steps");
            Assert.True(rightmost >= 1920f, $"the wall ends at {rightmost} after {step} steps");

            // Five seconds a step, so forty of them is longer than the wall's own period.
            scene.Advance(5f);
        }
    }

    [Fact]
    public void Nothing_is_drawn_outside_the_window()
    {
        // Every layer is either clipped to the wall's band or laid out against the window,
        // so a quad whose whole body is off the screen is a layout mistake rather than an
        // overdraw one. The statue is the exception it is allowed to be: it bleeds off the
        // left edge and the bottom on purpose.
        Overlay drawn = Draw(Scene(), 1280, 720);

        foreach (OverlayQuad quad in drawn.Quads)
        {
            Assert.True(
                quad.Destination.X < 1280f && quad.Destination.X + quad.Destination.Z > 0f,
                $"a quad at {quad.Destination} is off the side of a 1280-wide window");
        }
    }

    [Fact]
    public void The_wall_is_screened_and_a_sigil_is_multiplied()
    {
        // The two blends this screen exists to need. Both are fixed-function pipelines, so
        // what a test can check is that the display list actually asks for them: a screen
        // drawn over alpha is a red rectangle over the statue rather than a wall behind it.
        TitleScene scene = Scene();

        Assert.Contains(Draw(scene, 1600, 900).Quads, q => q.Blend == OverlayBlend.Screen);

        Assert.Equal(-1, scene.Showing);

        // Past the longest wait there can be, so one of them is certainly up.
        scene.Advance(0.05f);

        for (int i = 0; i < 700; i++)
        {
            scene.Advance(0.05f);

            if (scene.Showing >= 0)
            {
                break;
            }
        }

        Assert.True(scene.Showing >= 0, "no sigil came up within thirty-five seconds");

        // And half its life in, so it is past its fade and certainly drawn.
        scene.Advance(5f);

        OverlayQuad sigil = Assert.Single(
            Draw(scene, 1600, 900).Quads, q => q.Blend == OverlayBlend.Multiply);

        Assert.NotEqual(0f, sigil.Turn);
        Assert.True(sigil.Color.W is > 0f and < 1f, "a sigil is never fully opaque");
    }

    [Fact]
    public void One_sigil_at_a_time_never_twice_running_and_never_opaque()
    {
        TitleScene scene = Scene();

        List<int> order = [];
        List<float> waits = [];
        float since = 0f;
        int last = -1;

        // An hour of menu at a twentieth of a second a frame.
        for (int frame = 0; frame < 72_000; frame++)
        {
            scene.Advance(0.05f);

            if (scene.Showing == last)
            {
                since += 0.05f;

                continue;
            }

            if (scene.Showing >= 0)
            {
                order.Add(scene.Showing);

                if (order.Count > 1)
                {
                    waits.Add(since);
                }
            }

            last = scene.Showing;
            since = 0f;
        }

        Assert.True(order.Count > 40, $"only {order.Count} sigils in an hour");

        // All three of them get a turn, and none of them gets two turns running.
        Assert.Equal(3, order.Distinct().Count());

        for (int i = 1; i < order.Count; i++)
        {
            Assert.NotEqual(order[i - 1], order[i]);
        }

        // Fifteen to thirty seconds of empty wall between one going and the next arriving,
        // measured from the frame the last one faded out on. Both ends of the range are
        // reached across an hour, so this is the rule and not just the middle of it.
        foreach (float wait in waits)
        {
            Assert.InRange(wait, 15f - 0.1f, 30f + 0.1f);
        }

        Assert.True(waits.Min() < 18f, $"the shortest wait was {waits.Min():F1}s");
        Assert.True(waits.Max() > 27f, $"the longest wait was {waits.Max():F1}s");
    }

    [Fact]
    public void The_lettering_is_laid_out_by_its_paint_and_not_by_its_sheet()
    {
        // titlename.png is a wide strip of words in the middle of a tall transparent
        // sheet. Laid out by the sheet it is half the height of the window and mostly
        // nothing; laid out by the paint it is a line of lettering beside the statue.
        TitleScene scene = Scene();

        Overlay drawn = Draw(scene, 1920, 1080);

        // The last quad drawn is the name, and it takes the painted part of its source.
        OverlayQuad name = drawn.Quads[^1];

        Assert.True(name.Source.W < 0.9f, "the name is taking its whole sheet");
        Assert.True(
            name.Destination.X + name.Destination.Z <= 1920f,
            "the name runs off the right of the window");

        Assert.True(name.Destination.X > 1920f / 2f, "the name is not on the right");
    }

    // ---------------------------------------------------------------------------------
    // The row of buttons under it
    // ---------------------------------------------------------------------------------

    [Fact]
    public void The_first_page_is_one_line_of_buttons_across_the_bottom()
    {
        var front = new FrontEnd(new Settings()) { Illustrated = true };
        MenuPage page = Page();

        page.Behind = MenuBehind.Modern;
        page.Horizontal = true;
        page.Down = 0.905f;

        IReadOnlyList<MenuItem> items = front.Items;

        page.Build(front.Title, items, 1920, 1080, new Vector2(-1, -1));

        List<Vector4> where = [.. Enumerable.Range(0, items.Count)
            .Select(i => page.Where(i))
            .Where(r => r is not null)
            .Select(r => r!.Value)];

        Assert.Equal(items.Count, where.Count);

        // One line: every button shares a top edge.
        Assert.Single(where.Select(r => r.Y).Distinct());

        // In order, left to right, and none of them overlapping the next.
        for (int i = 1; i < where.Count; i++)
        {
            Assert.True(
                where[i].X >= where[i - 1].X + where[i - 1].Z,
                "two buttons overlap");
        }

        // Centred: the same air to the left of the first as to the right of the last.
        float left = where[0].X;
        float right = 1920f - (where[^1].X + where[^1].Z);

        Assert.True(MathF.Abs(left - right) <= 1f, $"{left} of air on the left and {right} on the right");

        // And down in the black under the wall, which starts at 0.83 of the window.
        Assert.True(where[0].Y > 1080f * 0.83f, "the buttons are drawn over the wall");
        Assert.True(where[0].Y + where[0].W < 1080f, "the buttons run off the bottom");
    }

    [Fact]
    public void A_click_on_a_button_in_the_line_chooses_it()
    {
        var front = new FrontEnd(new Settings()) { Illustrated = true };
        MenuPage page = Page();

        page.Behind = MenuBehind.Modern;
        page.Horizontal = true;
        page.Down = 0.905f;

        IReadOnlyList<MenuItem> items = front.Items;

        page.Build(front.Title, items, 1920, 1080, new Vector2(-1, -1));

        int play = -1;

        for (int i = 0; i < items.Count; i++)
        {
            if (items[i].Id == "play")
            {
                play = i;
            }
        }

        Assert.True(play >= 0);

        Vector4 row = page.Where(play)!.Value;
        var middle = new Vector2(row.X + (row.Z / 2f), row.Y + (row.W / 2f));

        Assert.Equal("play", page.Click(middle, items).Id);

        // And a click in the black beside the line is not a click on anything: the row is
        // as wide as its word, not as wide as its share of the window.
        Assert.Equal(
            string.Empty, page.Click(new Vector2(4f, row.Y + (row.W / 2f)), items).Id);
    }

    [Fact]
    public void A_narrow_window_closes_the_air_between_the_buttons_before_the_buttons()
    {
        var front = new FrontEnd(new Settings()) { Illustrated = true };
        IReadOnlyList<MenuItem> items = front.Items;

        MenuPage wide = Page();
        MenuPage narrow = Page();

        foreach (MenuPage page in new[] { wide, narrow })
        {
            page.Behind = MenuBehind.Modern;
            page.Horizontal = true;
            page.Down = 0.905f;
        }

        wide.Build(front.Title, items, 2560, 1080, new Vector2(-1, -1));
        narrow.Build(front.Title, items, 1024, 1080, new Vector2(-1, -1));

        Vector4 first = wide.Where(0)!.Value;
        Vector4 squeezed = narrow.Where(0)!.Value;

        // The word is the same size on both; what gave way is the gap.
        Assert.Equal(first.Z, squeezed.Z, 1);

        float wideGap = wide.Where(1)!.Value.X - (first.X + first.Z);
        float narrowGap = narrow.Where(1)!.Value.X - (squeezed.X + squeezed.Z);

        Assert.True(narrowGap < wideGap, "the gap did not close");
        Assert.True(narrowGap > 0f, "the buttons are touching");
    }

    // ---------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------

    /// <summary>A page over a font whose every character is four pixels wide.</summary>
    private static MenuPage Page() => new(new Overlay(Font()));

    /// <summary>
    /// A font of fixed four-pixel characters, covering everything this page says.
    /// </summary>
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

    /// <summary>An upload that hands out numbers from one.</summary>
    private static Func<string, DecodedImage, int> Uploader()
    {
        int next = 0;

        return (_, _) => ++next;
    }

    /// <summary>A set of six layers, shaped like the real ones.</summary>
    private MenuArt Loose()
    {
        string directory = Path.Combine(_root, "menu");
        Directory.CreateDirectory(directory);

        // The proportions matter to the layout and nothing else does: the wall is wider
        // than it is tall, the statue is about square, and the name is a strip of words in
        // the middle of a tall transparent sheet.
        Paint(Path.Combine(directory, MenuArt.Statue + ".png"), 164, 152);
        Paint(Path.Combine(directory, MenuArt.Wall + ".png"), 204, 119);
        Paint(Path.Combine(directory, MenuArt.Name + ".png"), 144, 108, top: 0.31f, depth: 0.41f);

        foreach (string sigil in MenuArt.Sigils)
        {
            Paint(Path.Combine(directory, sigil + ".png"), 74, 78);
        }

        return MenuArt.Open(null, directory, null);
    }

    private TitleScene Scene() =>
        TitleScene.Build(Loose(), Uploader())
        ?? throw new InvalidOperationException("the six layers would not build a screen");

    /// <summary>One frame of the screen, as a display list.</summary>
    private static Overlay Draw(TitleScene scene, int width, int height)
    {
        var overlay = new Overlay(OverlayAtlas.Blank());

        overlay.Begin(width, height);
        scene.Draw(overlay);

        return overlay;
    }

    /// <summary>Writes a PNG with paint in the middle of it and nothing round the edges.</summary>
    /// <param name="path">Where to write it.</param>
    /// <param name="width">How wide.</param>
    /// <param name="height">How tall.</param>
    /// <param name="top">How far down the paint starts, as a part of the height.</param>
    /// <param name="depth">How much of the height it takes.</param>
    private static void Paint(
        string path, int width, int height, float top = 0f, float depth = 1f)
    {
        byte[] pixels = new byte[width * height * 4];

        int from = (int)(height * top);
        int to = Math.Min(height, from + Math.Max(1, (int)(height * depth)));

        for (int y = from; y < to; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int at = ((y * width) + x) * 4;

                pixels[at] = 200;
                pixels[at + 1] = 40;
                pixels[at + 2] = 30;
                pixels[at + 3] = 255;
            }
        }

        File.WriteAllBytes(
            path,
            PngWriter.Encode(new DecodedImage(width, height, pixels, true, "test")));
    }
}
