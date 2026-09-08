using System.Linq;
using System.Numerics;
using GK3Reborn.Rendering;
using Xunit;

namespace GK3Reborn.Tests.UI;

/// <summary>
/// Tests for the sheet the loading screen draws with before the game has one.
/// </summary>
public sealed class LoadingScreenTests
{
    /// <summary>The white block is opaque white, at the coordinate it hands out.</summary>
    [Fact]
    public void The_blank_atlas_is_white_where_it_says_it_is()
    {
        OverlayAtlas atlas = OverlayAtlas.Blank();

        int x = (int)(atlas.White.X * atlas.Image.Width);
        int y = (int)(atlas.White.Y * atlas.Image.Height);
        int at = ((y * atlas.Image.Width) + x) * 4;

        Assert.Equal<byte>(255, atlas.Image.Pixels[at]);
        Assert.Equal<byte>(255, atlas.Image.Pixels[at + 1]);
        Assert.Equal<byte>(255, atlas.Image.Pixels[at + 2]);
        Assert.Equal<byte>(255, atlas.Image.Pixels[at + 3]);
    }

    /// <summary>
    /// It draws no letters at all, rather than drawing the wrong ones.
    /// </summary>
    [Fact]
    public void The_blank_atlas_draws_no_letters()
    {
        OverlayAtlas atlas = OverlayAtlas.Blank();

        Assert.Equal(0, atlas.Count);
        Assert.Null(atlas.Font);

        foreach (char c in "Loading Chargement Wczytywanie 0123456789")
        {
            Assert.Null(atlas.Glyph(c));
        }

        var overlay = new Overlay(atlas);
        overlay.Begin(1920, 1080);

        Assert.Equal(0, overlay.Measure("Loading"));

        overlay.Text("Loading", 10, 10, Vector4.One);
        Assert.Empty(overlay.Quads);
    }

    /// <summary>And rectangles drawn from it are the colour they were asked for.</summary>
    [Fact]
    public void The_blank_atlas_draws_rectangles()
    {
        var overlay = new Overlay(OverlayAtlas.Blank());
        overlay.Begin(1920, 1080);

        var gold = new Vector4(0.85f, 0.68f, 0.36f, 1f);
        overlay.Rect(100, 200, 300, 8, gold);

        OverlayQuad quad = Assert.Single(overlay.Quads);

        Assert.Equal(new Vector4(100, 200, 300, 8), quad.Destination);
        Assert.Equal(gold, quad.Color);
        Assert.Equal(0, quad.Picture);
    }
}
