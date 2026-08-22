using GK3Reborn.Formats.Fonts;
using GK3Reborn.Foundation.Diagnostics;
using GK3Reborn.Rendering;
using Xunit;

namespace GK3Reborn.Tests.Formats;

/// <summary>
/// Tests for reading an outline font and drawing it.
/// </summary>
/// <remarks>
/// <para>
/// The font the interface uses is carried inside the assembly, so these read the real one
/// rather than a fixture: a hand-made TrueType file would test the parser against my own
/// idea of the format, which is the one thing that cannot be wrong twice in the same way.
/// </para>
/// <para>
/// What they check is what silently breaks: metrics that are nonsense, a character map
/// that misses the accents this game is set among, and a rasteriser that draws either
/// nothing at all or a solid block — both of which look like a layout bug from the far end.
/// </para>
/// </remarks>
public sealed class TrueTypeTests
{
    private static TrueTypeFile Font()
    {
        using Stream carried = typeof(GK3Reborn.Application).Assembly
            .GetManifestResourceStream("GK3Reborn.Assets.Fonts.NotoSerif-Regular.ttf")
            ?? throw new InvalidOperationException("The interface's font is not in the assembly.");

        using var copy = new MemoryStream();
        carried.CopyTo(copy);

        return TrueTypeFile.Parse(copy.ToArray(), "NotoSerif-Regular.ttf", new DiagnosticBag())
            ?? throw new InvalidOperationException("The interface's font would not read.");
    }

    [Fact]
    public void The_font_the_game_ships_reads_and_says_what_it_is()
    {
        TrueTypeFile font = Font();

        Assert.Equal("Noto Serif", font.Family);
        Assert.Equal(2048, font.UnitsPerEm);

        // Up from the baseline and down from it. A font whose ascender is not positive is
        // one that has been read at the wrong offset.
        Assert.True(font.Ascender > 0, "the ascender does not go up");
        Assert.True(font.Descender < 0, "the descender does not go down");
        Assert.True(font.GlyphCount > 1000, $"only {font.GlyphCount} glyphs");

        // The licence it states, which is why it may be shipped at all.
        Assert.Contains("SIL Open Font License", font.Licence, StringComparison.Ordinal);
    }

    [Fact]
    public void It_has_the_letters_the_game_is_written_in()
    {
        TrueTypeFile font = Font();

        // Hôtel de Rennes-le-Château, Château de Serras, Montréal: the game is set in
        // France and the interface says so. A font missing these draws the place names
        // with holes in them, which is what the ARIAL sheets used to do.
        foreach (char c in "ABCabc0123 ,.!?'\"-—àâçèéêëîïôùûüÀÂÇÈÉÊËÎÏÔÙÛÜ«»")
        {
            Assert.True(font.Has(c), $"the font has no {c}");
            Assert.True(font.GlyphOf(c) > 0, $"{c} maps to the missing-character box");
        }
    }

    [Fact]
    public void A_letter_is_wider_than_a_full_stop_and_a_space_marks_nothing()
    {
        TrueTypeFile font = Font();

        int m = font.AdvanceOf(font.GlyphOf('M'));
        int stop = font.AdvanceOf(font.GlyphOf('.'));

        Assert.True(m > stop, $"an M ({m}) is no wider than a full stop ({stop})");

        // A space has an advance and no outline, which is the one glyph that must draw
        // nothing without being a failure to draw.
        Assert.True(font.AdvanceOf(font.GlyphOf(' ')) > 0, "a space is no wider than nothing");
        Assert.Null(font.OutlineOf(font.GlyphOf(' ')));
    }

    [Fact]
    public void An_accented_letter_is_built_from_the_letter_and_the_accent()
    {
        TrueTypeFile font = Font();

        // Composite glyphs: é is e with an acute placed over it, and if the components are
        // not read it comes out as a bare e or as nothing.
        GlyphOutline plain = Assert.IsType<GlyphOutline>(font.OutlineOf(font.GlyphOf('e')));
        GlyphOutline accented = Assert.IsType<GlyphOutline>(font.OutlineOf(font.GlyphOf('é')));

        Assert.True(
            accented.Ends.Count > plain.Ends.Count,
            "the accented letter has no more contours than the plain one");

        Assert.True(accented.Top > plain.Top, "the accent does not rise above the letter");
    }

    [Fact]
    public void A_drawn_letter_is_grey_at_the_edges_and_solid_in_the_middle()
    {
        TrueTypeFile font = Font();

        // An em of 64 pixels: large enough that the shapes are unambiguous.
        RasterGlyph o = GlyphRasterizer.Render(
            font.OutlineOf(font.GlyphOf('o')), 64f / font.UnitsPerEm);

        Assert.True(o.Marks, "nothing was drawn");
        Assert.InRange(o.Width, 20, 64);
        Assert.InRange(o.Height, 20, 64);

        int solid = o.Coverage.Count(c => c == 255);
        int part = o.Coverage.Count(c => c is > 0 and < 255);
        int empty = o.Coverage.Count(c => c == 0);

        Assert.True(solid > 0, "no pixel is fully inside the letter");
        Assert.True(part > 0, "no pixel is partly covered, so nothing is antialiased");

        // The counter of an 'o' is a hole. Without the nonzero winding rule it fills in
        // and the letter becomes a blob, which is the classic way a rasteriser is wrong.
        Assert.True(empty > 0, "the letter has no hole in it");

        int middle = ((o.Height / 2) * o.Width) + (o.Width / 2);
        Assert.True(o.Coverage[middle] < 128, "the middle of the o is filled in");
    }

    [Fact]
    public void A_letter_sits_where_the_metrics_say()
    {
        TrueTypeFile font = Font();
        float scale = 64f / font.UnitsPerEm;

        RasterGlyph x = GlyphRasterizer.Render(font.OutlineOf(font.GlyphOf('x')), scale);
        RasterGlyph p = GlyphRasterizer.Render(font.OutlineOf(font.GlyphOf('p')), scale);

        // Both sit on the baseline; only the p goes below it. Top is measured up from the
        // baseline, so a descender is what tells a bearing from a bounding box.
        Assert.True(x.Top > 0, "the x is not above the baseline");
        Assert.True(x.Top - x.Height >= -1, "the x hangs below the baseline");
        Assert.True(p.Top - p.Height < -1, "the p does not descend");
    }

    [Fact]
    public void An_atlas_carries_the_letters_and_a_white_pixel()
    {
        OverlayAtlas atlas = Assert.IsType<OverlayAtlas>(OverlayAtlas.Build(Font(), 24));

        Assert.True(atlas.Scalable);
        Assert.Equal("Noto Serif", atlas.Name);
        Assert.True(atlas.Height >= 24, $"a line is only {atlas.Height} pixels of a 24-pixel em");
        Assert.True(atlas.Count > 90, $"only {atlas.Count} characters were drawn");

        // Every glyph is inside the sheet, which shelf packing gets wrong by one at the
        // end of a row and nobody notices until a letter is drawn as a piece of another.
        foreach (char c in "Mgé,")
        {
            AtlasGlyph glyph = Assert.IsType<AtlasGlyph>(atlas.Glyph(c));

            Assert.InRange(glyph.Uv.X, 0f, 1f);
            Assert.InRange(glyph.Uv.Y, 0f, 1f);
            Assert.InRange(glyph.Uv.X + glyph.Uv.Z, 0f, 1f);
            Assert.InRange(glyph.Uv.Y + glyph.Uv.W, 0f, 1f);
            Assert.True(glyph.Advance > 0, $"{c} advances nowhere");
        }

        // A comma hangs below the line and a capital does not.
        Assert.True(atlas.Glyph(',')!.Value.Top > atlas.Glyph('M')!.Value.Top);

        // The white texel every solid rectangle is drawn from has to be opaque white, or
        // every panel in the interface comes out as a smear of some letter.
        int x = (int)(atlas.White.X * atlas.Image.Width);
        int y = (int)(atlas.White.Y * atlas.Image.Height);
        int at = (((y * atlas.Image.Width) + x) * 4);

        Assert.Equal(255, atlas.Image.Pixels[at]);
        Assert.Equal(255, atlas.Image.Pixels[at + 3]);
    }

    [Fact]
    public void A_file_that_is_not_a_font_is_refused_rather_than_half_read()
    {
        var bag = new DiagnosticBag();

        Assert.Null(TrueTypeFile.Parse([1, 2, 3], "rubbish.ttf", bag));
        Assert.Contains(bag.Items, d => d.Code == "GK3R1200");

        // An OpenType font with CFF outlines is a real font this cannot draw, and saying
        // so is the difference between falling back to the bitmap sheets and drawing a
        // menu of blanks.
        byte[] otto = [0x4F, 0x54, 0x54, 0x4F, 0, 0, 0, 0, 0, 0, 0, 0];

        Assert.Null(TrueTypeFile.Parse(otto, "postscript.otf", new DiagnosticBag()));
    }
}
