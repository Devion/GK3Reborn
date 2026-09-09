// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Numerics;
using GK3Reborn.Content;
using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Rendering;
using GK3Reborn.UI;
using Xunit;

namespace GK3Reborn.Tests.UI;

/// <summary>
/// Tests for the word that can be spelled on the title screen, and for what the screen
/// does when it is.
/// </summary>
public sealed class DanceCodeTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "gk3r-dance-" + Guid.NewGuid().ToString("N"));

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Every_letter_of_the_word_is_on_the_sheet()
    {
        foreach (char wanted in TitleLetters.Word)
        {
            Assert.Contains(TitleLetters.All, letter => letter.Character == wanted);
        }

        // And the sheet is the whole title: three lines of it, and no box outside it.
        Assert.Equal(45, TitleLetters.All.Count);

        foreach (TitleLetter letter in TitleLetters.All)
        {
            Assert.InRange(letter.Box.X, 0f, 1f);
            Assert.InRange(letter.Box.Y, 0f, 1f);
            Assert.InRange(letter.Box.X + letter.Box.Z, 0f, 1f);
            Assert.InRange(letter.Box.Y + letter.Box.W, 0f, 1f);
        }
    }

    [Fact]
    public void A_click_lands_on_the_nearest_letter_and_nowhere_else_on_nothing()
    {
        var sheet = new Vector4(100f, 50f, 1448f, 1086f);

        for (int i = 0; i < TitleLetters.All.Count; i++)
        {
            Vector4 box = TitleLetters.All[i].On(sheet);
            var middle = new Vector2(box.X + (box.Z / 2f), box.Y + (box.W / 2f));

            Assert.Equal(i, TitleLetters.At(middle, sheet));
        }

        // The corner of the sheet is transparent, and so is the black between the lines.
        Assert.Equal(-1, TitleLetters.At(new Vector2(101f, 51f), sheet));
        Assert.Equal(-1, TitleLetters.At(new Vector2(100f + 700f, 50f + 200f), sheet));
    }

    [Fact]
    public void The_word_is_spelled_in_order_and_a_wrong_letter_starts_it_again()
    {
        var code = new DanceCode();

        Assert.True(code.Click(Index('d')));
        Assert.True(code.Click(Index('a')));
        Assert.Equal(2, code.Spelled);

        // The wrong letter takes the lot out.
        Assert.False(code.Click(Index('x', fallback: 'b')));
        Assert.Equal(0, code.Spelled);

        // Any 'd' will do, including the one that is nowhere near the first.
        Assert.True(code.Click(Index('d', last: true)));

        foreach (char next in TitleLetters.Word[1..])
        {
            Assert.True(code.Click(Index(next)));
        }

        Assert.True(code.Complete);

        // Once spelled, it stays spelled.
        Assert.False(code.Click(Index('b')));
        Assert.True(code.Complete);
    }

    [Fact]
    public void A_wrong_letter_that_is_the_first_letter_begins_the_word_again()
    {
        var code = new DanceCode();

        Assert.True(code.Click(Index('d')));
        Assert.True(code.Click(Index('a')));
        Assert.False(code.Click(Index('d')));

        // d-a-d is somebody who mis-clicked, and the last d counts as a fresh start.
        Assert.Equal(1, code.Spelled);
    }

    [Fact]
    public void A_click_on_nothing_is_not_a_wrong_letter()
    {
        var code = new DanceCode();

        Assert.True(code.Click(Index('d')));
        Assert.False(code.Click(-1));
        Assert.Equal(1, code.Spelled);
    }

    [Fact]
    public void Clicking_the_letters_lights_them_and_spelling_the_word_throws_the_party()
    {
        TitleScene scene = Scene();
        int asked = 0;
        int started = 0;

        // No party can be built here — there is no device — but the asking is the point.
        scene.PartyMaker = _ =>
        {
            asked++;
            return null;
        };

        scene.PartyStarted = () => started++;

        foreach (char wanted in TitleLetters.Word)
        {
            int letter = Index(wanted);
            Vector4 box = scene.LetterBox(letter, 1280, 720);

            Assert.True(scene.Click(
                new Vector2(box.X + (box.Z / 2f), box.Y + (box.W / 2f)), 1280, 720));
        }

        Assert.True(scene.Spelled);
        Assert.Equal(TitleLetters.Word.Length, scene.Lit.Count);
        Assert.Equal(1, asked);
        Assert.Equal(0, started);
        Assert.Null(scene.Party);

        // Every lit letter is drawn again in a colour: over itself, and screened round
        // itself. The screen has no party, so the black and the wall are still there.
        Overlay drawn = Draw(scene, 1280, 720);
        int name = scene.Layers[2].Picture;

        Assert.True(
            drawn.Quads.Count(q => q.Picture == name && q.Blend == OverlayBlend.Screen)
                >= TitleLetters.Word.Length * 3);
        Assert.Contains(drawn.Quads, q => q.Blend == OverlayBlend.Screen && q.Picture == scene.Layers[1].Picture);

        // Asked once and not again: a party that could not be built is not asked for on
        // every click after.
        Assert.True(scene.Click(
            Middle(scene.LetterBox(Index('b'), 1280, 720)), 1280, 720));
        Assert.Equal(1, asked);
    }

    [Fact]
    public void A_click_beside_the_title_is_nothing()
    {
        TitleScene scene = Scene();

        Assert.False(scene.Click(new Vector2(10f, 10f), 1280, 720));
        Assert.Empty(scene.Lit);
    }

    [Fact]
    public void Spelling_the_word_outright_lights_it_and_starts_the_party_once()
    {
        TitleScene scene = Scene();
        int started = 0;

        scene.PartyMaker = _ => null;
        scene.PartyStarted = () => started++;

        scene.Spell();
        scene.Spell();

        Assert.True(scene.Spelled);
        Assert.Equal(TitleLetters.Word.Length, scene.Lit.Count);

        // The party could not be built, so nothing started; and nothing was asked twice.
        Assert.Equal(0, started);
    }

    private static Vector2 Middle(Vector4 box) =>
        new(box.X + (box.Z / 2f), box.Y + (box.W / 2f));

    /// <summary>The index of a letter on the sheet.</summary>
    private static int Index(char wanted, char fallback = '\0', bool last = false)
    {
        int found = -1;

        for (int i = 0; i < TitleLetters.All.Count; i++)
        {
            if (TitleLetters.All[i].Character == wanted)
            {
                found = i;

                if (!last)
                {
                    return i;
                }
            }
        }

        return found >= 0 || fallback == '\0' ? found : Index(fallback);
    }

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

    private static Overlay Draw(TitleScene scene, int width, int height)
    {
        var overlay = new Overlay(OverlayAtlas.Blank());

        overlay.Begin(width, height);
        scene.Draw(overlay);

        return overlay;
    }

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
