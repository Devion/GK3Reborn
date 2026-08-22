using System.Numerics;
using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Formats.Ui;

namespace GK3Reborn.Rendering;

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

    private OverlayAtlas(DecodedImage image, FontFile font, Vector4 white)
    {
        Image = image;
        Font = font;
        White = white;
    }

    /// <summary>The sheet everything is cut from.</summary>
    public DecodedImage Image { get; }

    /// <summary>The font it carries.</summary>
    public FontFile Font { get; }

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

        return new OverlayAtlas(image, font, new Vector4(u, v, 0f, 0f));
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
    public int LineHeight => (Atlas.Font.Height + Atlas.Font.LineSpacing) * _magnify;

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
            if (Atlas.Font[c] is not { } glyph)
            {
                continue;
            }

            _quads.Add(new OverlayQuad(
                new Vector4(at, top, glyph.Width * _magnify, glyph.Height * _magnify),
                Atlas.Uv(glyph),
                color));

            at += (glyph.Width + Atlas.Font.CharacterSpacing) * _magnify;
        }

        return at;
    }

    /// <summary>How wide a string will be.</summary>
    /// <param name="text">The string.</param>
    /// <returns>Width in pixels.</returns>
    public int Measure(string text) => Atlas.Font.Measure(text) * _magnify;

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
        float height = Atlas.Font.Height + padding;

        Rect(x, y, width, height, background);
        Text(text, x + padding, y + (padding / 2), foreground);

        return width;
    }
}
