using System.Numerics;
using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Formats.Fonts;
using GK3Reborn.Formats.Ui;

namespace GK3Reborn.Rendering;

/// <summary>
/// A character, ready to draw: where it is in the sheet and where it goes against the pen.
/// </summary>
/// <param name="Uv">Where it is cut from, in texture coordinates.</param>
/// <param name="Width">How wide to draw it, in pixels of the sheet.</param>
/// <param name="Height">How tall.</param>
/// <param name="Left">
/// Pixels from the pen to its left edge. Negative for a letter that leans back over the
/// one before it, which no bitmap sheet does and many outlines do.
/// </param>
/// <param name="Top">Pixels from the top of the line down to its top edge.</param>
/// <param name="Advance">How far the pen moves afterwards, including the space after it.</param>
/// <remarks>
/// The whole of what the two kinds of font have to agree about. A bitmap sheet's cells all
/// sit on the line's top with no bearing and step by their own width; an outline's letters
/// each sit somewhere of their own against a baseline. Saying it this way lets one drawing
/// routine serve both, rather than the interface knowing which sort of font it has.
/// </remarks>
public readonly record struct AtlasGlyph(
    Vector4 Uv, float Width, float Height, float Left, float Top, float Advance);

/// <summary>One rectangle of the overlay.</summary>
/// <param name="Destination">Where it goes on screen: x, y, width, height in pixels.</param>
/// <param name="Source">Where it comes from in the atlas, in texture coordinates.</param>
/// <param name="Color">What to tint it, straight alpha.</param>
public readonly record struct OverlayQuad(Vector4 Destination, Vector4 Source, Vector4 Color);

/// <summary>
/// Everything the interface draws, as one sheet and one list of rectangles.
/// </summary>
/// <remarks>
/// <para>
/// A font's sheet with a block of white added under it. Every rectangle the interface
/// draws — a letter, a panel, a divider — is then a piece of the same texture, which means
/// the whole interface is one draw call and needs no state changes in the middle of it.
/// The white block is what makes a solid rectangle possible without a second texture.
/// </para>
/// <para>
/// GK3's fonts come two ways and both have to work. Most are white letters on magenta,
/// which decodes with the magenta already transparent. Sidney's are antialiased grey on
/// black with no transparency at all. Multiplying the texture's alpha by its brightness
/// covers both: the magenta ones keep their crisp edges, the black-backed ones get their
/// antialiasing turned into alpha, and the black glyph markers along the top of a sheet
/// disappear on their own.
/// </para>
/// </remarks>
public sealed class OverlayAtlas
{
    private const int WhiteSize = 4;

    private readonly Dictionary<char, AtlasGlyph>? _drawn;

    private OverlayAtlas(
        DecodedImage image,
        FontFile? font,
        Vector4 white,
        string name,
        int height,
        Dictionary<char, AtlasGlyph>? drawn = null)
    {
        Image = image;
        Font = font;
        White = white;
        Name = name;
        Height = height;
        _drawn = drawn;
    }

    /// <summary>The sheet everything is cut from.</summary>
    public DecodedImage Image { get; }

    /// <summary>
    /// The bitmap font it carries, or null when it was cut from an outline.
    /// </summary>
    public FontFile? Font { get; }

    /// <summary>What to call it in a report.</summary>
    public string Name { get; }

    /// <summary>How tall one line of it is, in pixels.</summary>
    public int Height { get; }

    /// <summary>Whether it was drawn from an outline rather than taken from a sheet.</summary>
    public bool Scalable => _drawn is not null;

    /// <summary>How many characters it can draw.</summary>
    public int Count => _drawn?.Count ?? Font?.Count ?? 0;

    /// <summary>Texture coordinates of a texel that is opaque white.</summary>
    public Vector4 White { get; }

    /// <summary>Builds an atlas around a font.</summary>
    /// <param name="font">The font.</param>
    /// <returns>The atlas.</returns>
    public static OverlayAtlas Build(FontFile font)
    {
        ArgumentNullException.ThrowIfNull(font);

        DecodedImage sheet = font.Sheet;
        int width = Math.Max(WhiteSize, sheet.Width);
        int height = sheet.Height + WhiteSize;
        byte[] pixels = new byte[width * height * 4];

        for (int y = 0; y < sheet.Height; y++)
        {
            Array.Copy(
                sheet.Pixels,
                y * sheet.Width * 4,
                pixels,
                y * width * 4,
                sheet.Width * 4);
        }

        for (int y = sheet.Height; y < height; y++)
        {
            for (int x = 0; x < WhiteSize; x++)
            {
                int at = ((y * width) + x) * 4;
                pixels[at] = 255;
                pixels[at + 1] = 255;
                pixels[at + 2] = 255;
                pixels[at + 3] = 255;
            }
        }

        var image = new DecodedImage(width, height, pixels, HasAlpha: true, "overlay-atlas");

        // The middle of the white block rather than its edge, so no amount of filtering
        // can reach the letters above it.
        float u = 2f / width;
        float v = (sheet.Height + 2f) / height;

        return new OverlayAtlas(
            image,
            font,
            new Vector4(u, v, 0f, 0f),
            font.Name,
            font.Height + font.LineSpacing);
    }

    /// <summary>
    /// The characters an interface atlas carries.
    /// </summary>
    /// <remarks>
    /// Latin-1 and no more. It covers the game's own language and the French it is set in —
    /// the accented letters of Hôtel de Rennes-le-Château — and stops well short of
    /// rasterising two thousand glyphs to draw a menu of five words. Anything outside it
    /// falls back to the bitmap sheets, which carry the same set.
    /// </remarks>
    public const string Latin =
        " !\"#$%&'()*+,-./0123456789:;<=>?@ABCDEFGHIJKLMNOPQRSTUVWXYZ[\\]^_`" +
        "abcdefghijklmnopqrstuvwxyz{|}~" +
        "\u00a1\u00a2\u00a3\u00a7\u00a9\u00ab\u00ae\u00b0\u00b1\u00b7\u00bb" +
        "\u00bf\u00c0\u00c1\u00c2\u00c3\u00c4\u00c5\u00c6\u00c7\u00c8\u00c9" +
        "\u00ca\u00cb\u00cc\u00cd\u00ce\u00cf\u00d1\u00d2\u00d3\u00d4\u00d5" +
        "\u00d6\u00d8\u00d9\u00da\u00db\u00dc\u00dd\u00df\u00e0\u00e1\u00e2" +
        "\u00e3\u00e4\u00e5\u00e6\u00e7\u00e8\u00e9\u00ea\u00eb\u00ec\u00ed" +
        "\u00ee\u00ef\u00f1\u00f2\u00f3\u00f4\u00f5\u00f6\u00f8\u00f9\u00fa" +
        "\u00fb\u00fc\u00fd\u00ff\u2013\u2014\u2018\u2019\u201c\u201d\u2026";

    /// <summary>
    /// Builds an atlas by drawing an outline font at a size.
    /// </summary>
    /// <param name="face">The font.</param>
    /// <param name="pixels">How tall an em should be, in pixels.</param>
    /// <param name="characters">Which characters to draw, or null for <see cref="Latin"/>.</param>
    /// <returns>The atlas, or null when nothing could be drawn.</returns>
    /// <remarks>
    /// <para>
    /// Built once for a size and thrown away when the window wants another, exactly as the
    /// bitmap ladder is: a menu at a fixed size costs one atlas, and a window being dragged
    /// between two sizes costs one more each time it settles.
    /// </para>
    /// <para>
    /// The glyphs are packed into shelves — a row at a time, wrapping when the row is full.
    /// It wastes a little of the sheet and is a dozen lines; a tighter packer would save
    /// memory nobody is short of.
    /// </para>
    /// </remarks>
    public static OverlayAtlas? Build(TrueTypeFile face, int pixels, string? characters = null)
    {
        ArgumentNullException.ThrowIfNull(face);

        if (pixels <= 0)
        {
            return null;
        }

        string wanted = characters ?? Latin;
        float scale = pixels / (float)face.UnitsPerEm;

        int ascent = (int)MathF.Ceiling(face.Ascender * scale);
        int descent = (int)MathF.Ceiling(-face.Descender * scale);
        int line = Math.Max(1, ascent + descent + (int)MathF.Round(face.LineGap * scale));

        // One pixel of air around every glyph, so filtering at a fractional position can
        // never reach the letter next to it. The same defect the bitmap sheets had.
        const int Gap = 1;

        List<(char Character, RasterGlyph Raster, float Advance)> drawn = [];

        foreach (char c in wanted.Distinct())
        {
            int glyph = face.GlyphOf(c);

            if (glyph == 0 && c != ' ')
            {
                continue;
            }

            drawn.Add((
                c,
                GlyphRasterizer.Render(face.OutlineOf(glyph), scale),
                face.AdvanceOf(glyph) * scale));
        }

        if (drawn.Count == 0)
        {
            return null;
        }

        // Wide enough that the shelves are not one glyph each, and square enough that no
        // device is asked for a texture it will not take.
        int width = 256;

        while (width < 4096 && width * width < drawn.Sum(g => (g.Raster.Width + Gap) * (line + Gap)))
        {
            width *= 2;
        }

        int penX = Gap;
        int penY = Gap;
        int shelf = 0;

        foreach ((_, RasterGlyph raster, _) in drawn)
        {
            if (penX + raster.Width + Gap > width)
            {
                penX = Gap;
                penY += shelf + Gap;
                shelf = 0;
            }

            penX += raster.Width + Gap;
            shelf = Math.Max(shelf, raster.Height);
        }

        int height = penY + shelf + Gap + WhiteSize;

        // Powers of two are not required and are kind to a driver's allocator.
        int rounded = 1;

        while (rounded < height)
        {
            rounded *= 2;
        }

        height = Math.Min(rounded, 8192);

        byte[] sheet = new byte[width * height * 4];
        Dictionary<char, AtlasGlyph> placed = new(drawn.Count);

        penX = Gap;
        penY = Gap;
        shelf = 0;

        foreach ((char c, RasterGlyph raster, float advance) in drawn)
        {
            if (penX + raster.Width + Gap > width)
            {
                penX = Gap;
                penY += shelf + Gap;
                shelf = 0;
            }

            for (int y = 0; y < raster.Height; y++)
            {
                int row = penY + y;

                if (row >= height)
                {
                    break;
                }

                for (int x = 0; x < raster.Width; x++)
                {
                    int at = (((row * width) + penX + x) * 4);

                    // White with the coverage as alpha. The overlay's shader multiplies
                    // alpha by brightness, so anything less than white here would square
                    // the antialiasing and leave the letters thin and dark-edged.
                    sheet[at] = 255;
                    sheet[at + 1] = 255;
                    sheet[at + 2] = 255;
                    sheet[at + 3] = raster.Coverage[(y * raster.Width) + x];
                }
            }

            placed[c] = new AtlasGlyph(
                new Vector4(
                    penX / (float)width,
                    penY / (float)height,
                    raster.Width / (float)width,
                    raster.Height / (float)height),
                raster.Width,
                raster.Height,
                raster.Left,
                ascent - raster.Top,
                advance);

            penX += raster.Width + Gap;
            shelf = Math.Max(shelf, raster.Height);
        }

        // The white block, in the bottom-left corner where no glyph reaches.
        for (int y = height - WhiteSize; y < height; y++)
        {
            for (int x = 0; x < WhiteSize; x++)
            {
                int at = ((y * width) + x) * 4;

                sheet[at] = 255;
                sheet[at + 1] = 255;
                sheet[at + 2] = 255;
                sheet[at + 3] = 255;
            }
        }

        var image = new DecodedImage(width, height, sheet, HasAlpha: true, "overlay-atlas");

        return new OverlayAtlas(
            image,
            null,
            new Vector4(2f / width, (height - (WhiteSize / 2f)) / height, 0f, 0f),
            face.Family,
            line,
            placed);
    }

    /// <summary>What to draw for a character.</summary>
    /// <param name="c">The character.</param>
    /// <returns>Where it is and where it goes, or null when the font has not got it.</returns>
    public AtlasGlyph? Glyph(char c)
    {
        if (_drawn is not null)
        {
            return _drawn.TryGetValue(c, out AtlasGlyph found) ? found : null;
        }

        if (Font?[c] is not { } cell)
        {
            return null;
        }

        // A sheet's cells sit on the top of the line and step by their own width. Saying
        // so here is what lets one drawing routine serve both kinds.
        return new AtlasGlyph(
            Uv(cell), cell.Width, cell.Height, 0, 0, cell.Width + Font.CharacterSpacing);
    }

    /// <summary>Texture coordinates of a character.</summary>
    /// <param name="glyph">Where it is in the font's sheet.</param>
    /// <returns>Left, top, width and height, in texture coordinates.</returns>
    /// <remarks>
    /// <b>Half a texel in on every side.</b> A glyph's rectangle runs from one pixel below
    /// its row's marker strip to the top of the next row's, with nothing between them, and
    /// the sampler filters linearly — so a sample taken at the glyph's very edge reaches
    /// half a texel past it and brings a quarter of a marker strip back with it. That drew
    /// a dotted line over and under every line of text, invisible at the size the layout
    /// was authored at and plain at the sizes where a sheet pixel covers two screen ones.
    ///
    /// Insetting rather than switching the sampler to nearest: the caption sheets are
    /// antialiased grey rather than hard-edged, and filtering them is what makes a doubled
    /// one read as a larger version of itself instead of as a magnified bitmap.
    /// </remarks>
    public Vector4 Uv(Glyph glyph)
    {
        // Never past the middle: a one-pixel glyph has no interior to inset into.
        float inset = MathF.Min(0.5f, MathF.Min(glyph.Width, glyph.Height) * 0.25f);

        return new(
            (glyph.X + inset) / Image.Width,
            (glyph.Y + inset) / Image.Height,
            (glyph.Width - (2f * inset)) / Image.Width,
            (glyph.Height - (2f * inset)) / Image.Height);
    }
}

/// <summary>
/// The interface's display list, rebuilt every frame.
/// </summary>
/// <remarks>
/// Immediate rather than retained: the interface is a function of what the game is doing,
/// so describing it fresh each frame is both simpler and impossible to leave stale. There
/// is nothing to invalidate and no widget tree to keep in step with the world.
/// </remarks>
public sealed class Overlay
{
    private readonly List<OverlayQuad> _quads = [];

    private int _magnify = 1;

    /// <summary>Creates an overlay over an atlas.</summary>
    /// <param name="atlas">The sheet everything is drawn from.</param>
    public Overlay(OverlayAtlas atlas)
    {
        ArgumentNullException.ThrowIfNull(atlas);
        Atlas = atlas;
    }

    /// <summary>The sheet.</summary>
    public OverlayAtlas Atlas { get; }

    /// <summary>Width of the surface being drawn on, in pixels.</summary>
    public int Width { get; private set; }

    /// <summary>Height of the surface being drawn on, in pixels.</summary>
    public int Height { get; private set; }

    /// <summary>The rectangles, in the order they were added.</summary>
    public IReadOnlyList<OverlayQuad> Quads => _quads;

    /// <summary>
    /// How many screen pixels one pixel of the font's sheet covers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A whole number, and one by default. GK3's largest caption sheet cuts to 33-pixel
    /// letters, which is 3.5% of the 480-line screen it was drawn for and 1.5% of a 4K one:
    /// past a point the ladder of sheets runs out and the only way to keep the text the
    /// same apparent size is to draw each sheet pixel as more than one.
    /// </para>
    /// <para>
    /// Whole numbers because a fraction lands glyph edges between pixels and the sampler
    /// then averages neighbouring letters into each other. The caption sheets are
    /// antialiased grey rather than hard-edged, so a doubled one reads as a larger version
    /// of itself rather than as a magnified bitmap.
    /// </para>
    /// <para>
    /// It multiplies <see cref="LineHeight"/> and <see cref="Measure"/> as well as the
    /// glyphs, so anything laying out against those numbers grows with it and nothing has
    /// to know this exists. <see cref="Rect"/> is deliberately <em>not</em> multiplied:
    /// its arguments are already in screen pixels, computed from those same numbers, and
    /// scaling them again would apply the factor twice.
    /// </para>
    /// </remarks>
    public int Magnify
    {
        get => _magnify;
        set => _magnify = Math.Max(1, value);
    }

    /// <summary>How tall a line of text is.</summary>
    public int LineHeight => Atlas.Height * _magnify;

    /// <summary>Starts a frame.</summary>
    /// <param name="width">Width of the surface.</param>
    /// <param name="height">Height of the surface.</param>
    public void Begin(int width, int height)
    {
        Width = width;
        Height = height;
        _quads.Clear();
    }

    /// <summary>Draws a solid rectangle.</summary>
    /// <param name="x">Pixels from the left.</param>
    /// <param name="y">Pixels from the top.</param>
    /// <param name="width">How wide.</param>
    /// <param name="height">How tall.</param>
    /// <param name="color">What colour, straight alpha.</param>
    public void Rect(float x, float y, float width, float height, Vector4 color) =>
        _quads.Add(new OverlayQuad(
            new Vector4(x, y, width, height), Atlas.White, color));

    /// <summary>Draws a line of text.</summary>
    /// <param name="text">What to write.</param>
    /// <param name="x">Pixels from the left of the first character.</param>
    /// <param name="y">Pixels from the top of the line.</param>
    /// <param name="color">What colour, straight alpha.</param>
    /// <returns>Where the next character would start.</returns>
    public float Text(string text, float x, float y, Vector4 color)
    {
        ArgumentNullException.ThrowIfNull(text);

        // Whole pixels, always. A bitmap glyph drawn at a fractional position samples
        // between texels, and with the sheets stacked in rows what is half a texel above a
        // letter is the red marker strip belonging to it — so a caption laid out at
        // y=17.36 came with a dotted line over it. Rounding is also the difference between
        // crisp letters and slightly soft ones everywhere else.
        float at = MathF.Round(x);
        float top = MathF.Round(y);

        foreach (char c in text)
        {
            if (Atlas.Glyph(c) is not { } glyph)
            {
                continue;
            }

            // A sheet's cells have no bearing and sit on the line's top; an outline's
            // letters each sit somewhere of their own. Both are said the same way, so
            // this does not know which sort it has.
            if (glyph.Width > 0 && glyph.Height > 0)
            {
                _quads.Add(new OverlayQuad(
                    new Vector4(
                        MathF.Round(at + (glyph.Left * _magnify)),
                        MathF.Round(top + (glyph.Top * _magnify)),
                        glyph.Width * _magnify,
                        glyph.Height * _magnify),
                    glyph.Uv,
                    color));
            }

            at += glyph.Advance * _magnify;
        }

        return at;
    }

    /// <summary>How wide a string will be.</summary>
    /// <param name="text">The string.</param>
    /// <returns>Width in pixels.</returns>
    public int Measure(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        float width = 0;

        foreach (char c in text)
        {
            if (Atlas.Glyph(c) is { } glyph)
            {
                width += glyph.Advance;
            }
        }

        return (int)MathF.Round(width * _magnify);
    }

    /// <summary>Draws a panel with text on it.</summary>
    /// <param name="text">The line.</param>
    /// <param name="x">Pixels from the left.</param>
    /// <param name="y">Pixels from the top.</param>
    /// <param name="background">Panel colour.</param>
    /// <param name="foreground">Text colour.</param>
    /// <param name="padding">Pixels of space around the text.</param>
    /// <returns>How wide the panel is.</returns>
    public float Label(
        string text,
        float x,
        float y,
        Vector4 background,
        Vector4 foreground,
        float padding = 6f)
    {
        ArgumentNullException.ThrowIfNull(text);

        float width = Measure(text) + (padding * 2);
        float height = LineHeight + padding;

        Rect(x, y, width, height, background);
        Text(text, x + padding, y + (padding / 2), foreground);

        return width;
    }
}
