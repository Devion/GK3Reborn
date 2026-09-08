// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Numerics;
using GK3Reborn.Rendering;
using GK3Reborn.Rendering.Shaders;
using Xunit;

namespace GK3Reborn.Tests.Rendering;

/// <summary>
/// Tests for the two things the title screen asked the interface's display list for: a
/// rectangle combined with the screen some other way than by its alpha, and a rectangle
/// that is not square to the screen.
/// </summary>
public sealed class OverlayBlendTests
{
    private static Overlay Begun(int width = 800, int height = 600)
    {
        var overlay = new Overlay(OverlayAtlas.Blank());

        overlay.Begin(width, height);

        return overlay;
    }

    [Fact]
    public void The_interface_still_costs_one_run()
    {
        // The whole reason the blend lives on the quad rather than in a second display
        // list: a screen that uses none of this must cost exactly what it did.
        Overlay overlay = Begun();

        for (int i = 0; i < 40; i++)
        {
            overlay.Text("Settings", 10, 10 + (i * 20), Vector4.One);
            overlay.Rect(10, 10 + (i * 20), 200, 2, Vector4.One);
        }

        List<OverlayRun> runs = [];

        OverlayMesh.Build(overlay, 16384, 0, runs);

        Assert.Single(runs);
        Assert.Equal(OverlayBlend.Alpha, runs[0].Blend);
    }

    [Fact]
    public void A_change_of_blend_breaks_the_run()
    {
        Overlay overlay = Begun();

        overlay.Picture(1, 0, 0, 10, 10, Vector4.One);
        overlay.Picture(1, 0, 0, 10, 10, Vector4.One, null, OverlayBlend.Screen);
        overlay.Picture(1, 0, 0, 10, 10, Vector4.One, null, OverlayBlend.Screen);
        overlay.Picture(1, 0, 0, 10, 10, Vector4.One, null, OverlayBlend.Multiply);

        List<OverlayRun> runs = [];

        OverlayMesh.Build(overlay, 16384, 1, runs);

        Assert.Equal(3, runs.Count);
        Assert.Equal(
            [OverlayBlend.Alpha, OverlayBlend.Screen, OverlayBlend.Multiply],
            runs.Select(r => r.Blend));

        // The two screened ones are one run of two rectangles.
        Assert.Equal(12, runs[1].Count);
    }

    [Fact]
    public void A_glyph_never_asks_for_a_blend()
    {
        // A stray Screen on a text quad would cut the whole interface into runs, and it
        // would not do anything either: the sheet is a stencil.
        Overlay overlay = Begun();

        overlay.Text("Play", 10, 10, Vector4.One);
        overlay.Rect(10, 10, 20, 20, Vector4.One);

        List<OverlayRun> runs = [];

        OverlayMesh.Build(overlay, 16384, 0, runs);

        Assert.Single(runs);
        Assert.Equal(OverlayShaders.Glyphs, runs[0].Picture == 0 ? OverlayShaders.Glyphs : 1);
    }

    [Fact]
    public void Each_blend_tells_the_shader_which_it_is()
    {
        // The blend state does the combining and the shader hands it the colour that makes
        // it come out right at less than full opacity, so the two have to agree about which
        // of the three is being drawn.
        Assert.Equal(OverlayShaders.PictureOver, OverlayShaders.PictureMode(OverlayBlend.Alpha));

        // Screen and multiply want the same thing written, and the blend factors are the
        // whole difference. One branch, so that no run can have its shader take one of them
        // while its pipeline carries the other: that wrote a factor where a colour belonged
        // and drew the sigils as flat dark squares the size of their own quads.
        Assert.Equal(
            OverlayShaders.PictureBlended, OverlayShaders.PictureMode(OverlayBlend.Screen));

        Assert.Equal(
            OverlayShaders.PictureBlended, OverlayShaders.PictureMode(OverlayBlend.Multiply));

        // And the fragment stage reads it as the number it is, once.
        Assert.Contains("draw.picture == 2", OverlayShaders.Fragment, StringComparison.Ordinal);
        Assert.DoesNotContain("draw.picture == 3", OverlayShaders.Fragment, StringComparison.Ordinal);
    }

    [Fact]
    public void A_turned_sprite_is_turned_in_pixels_and_not_in_clip_space()
    {
        // A quarter turn of a square on a 2:1 window. Turned in clip space it comes back an
        // oblong; turned in pixels it comes back the same square, stood on its corner or
        // not at all.
        Overlay overlay = Begun(800, 400);

        overlay.Sprite(1, new Vector2(400, 200), new Vector2(100, 100), Vector4.One, MathF.PI / 2f);

        List<OverlayRun> runs = [];
        OverlayVertex[] vertices = OverlayMesh.Build(overlay, 16384, 1, runs);

        // Back to pixels, which is where the shape is supposed to be square.
        static Vector2 Pixels(OverlayVertex vertex) => new(
            (vertex.Position.X + 1f) * 800f / 2f,
            (vertex.Position.Y + 1f) * 400f / 2f);

        Vector2 topLeft = Pixels(vertices[0]);
        Vector2 topRight = Pixels(vertices[2]);
        Vector2 bottomLeft = Pixels(vertices[1]);

        Assert.Equal(100f, (topRight - topLeft).Length(), 2);
        Assert.Equal(100f, (bottomLeft - topLeft).Length(), 2);

        // A quarter turn: what was the top edge now runs down the screen.
        Assert.Equal(0f, topRight.X - topLeft.X, 2);
        Assert.Equal(100f, topRight.Y - topLeft.Y, 2);
    }

    [Fact]
    public void A_turned_sprite_is_kept_whole_or_dropped_whole_by_a_clip()
    {
        // A turned rectangle cannot be trimmed to an upright box by moving its corners, so
        // the display list will not pretend it can. Binary is a fault a caller can see; a
        // sheared sigil is one nobody would attribute to the clip.
        Overlay overlay = Begun();

        overlay.PushClip(new Vector4(100, 100, 200, 200));
        overlay.Sprite(1, new Vector2(200, 200), new Vector2(50, 50), Vector4.One, 0.3f);
        overlay.Sprite(1, new Vector2(110, 110), new Vector2(50, 50), Vector4.One, 0.3f);
        overlay.PopClip();

        Assert.Single(overlay.Quads);
        Assert.Equal(200f, overlay.Quads[0].Destination.X + (overlay.Quads[0].Destination.Z / 2f));
    }

    [Fact]
    public void An_upright_picture_is_still_trimmed_to_its_clip()
    {
        // The turn is the exception; the rule it is an exception to still holds, source
        // rectangle and all.
        Overlay overlay = Begun();

        overlay.PushClip(new Vector4(0, 0, 100, 100));
        overlay.Picture(1, 50, 0, 100, 100, Vector4.One);
        overlay.PopClip();

        OverlayQuad quad = Assert.Single(overlay.Quads);

        Assert.Equal(50f, quad.Destination.Z);
        Assert.Equal(0.5f, quad.Source.Z, 3);
    }
}
