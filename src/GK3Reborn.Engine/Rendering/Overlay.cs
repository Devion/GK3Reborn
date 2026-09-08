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
public readonly record struct AtlasGlyph(
    Vector4 Uv, float Width, float Height, float Left, float Top, float Advance);

/// <summary>How a rectangle is combined with what is already on the screen.</summary>
/// <remarks>
/// Fixed-function blend state, not arithmetic in the shader: nothing drawn here can read
/// what is under it. Each of these is one pipeline in each backend, and the display list is
/// cut into runs on this as well as on the picture — so a screen that uses none of them,
/// which is every screen but the title, still costs exactly what it did.
/// </remarks>
public enum OverlayBlend
{
    /// <summary>Over what is behind it, by its own alpha. Everything the interface draws.</summary>
    Alpha,

    /// <summary>
    /// Lightens: <c>1 - (1 - source)(1 - destination)</c>, which is Photoshop's Screen.
    /// Exact, including at partial opacity — see the factors in the pipelines.
    /// </summary>
    Screen,

    /// <summary>
    /// Darkens: the destination multiplied by the source, faded towards leaving it alone.
    /// <b>The stand-in for Photoshop's Colour Burn</b>, which is
    /// <c>1 - (1 - destination) / source</c> and cannot be reached by any pair of blend
    /// factors. At the opacities the sigils are drawn at the two are within a step of each
    /// other; at full opacity they are not, which is why nothing here draws at full opacity.
    /// </summary>
    Multiply,
}

/// <summary>One rectangle of the interface.</summary>
/// <param name="Destination">Where it goes, in pixels from the top left.</param>
/// <param name="Source">Which part of its picture to take, in texture coordinates.</param>
/// <param name="Color">What to tint it, straight alpha.</param>
/// <param name="Picture">
/// Which picture it is drawn from: zero for the sheet of letters, and otherwise one of the
/// screens' own images. The interface is nearly all letters and flat colour, so keeping
/// this on the quad rather than splitting the display list means a screen that shows a map
/// costs one extra draw call and nothing else.
/// </param>
/// <param name="Blend">How it is combined with what is already there.</param>
/// <param name="Turn">
/// How far it is turned about its own centre, in radians, clockwise on the screen. Nought
/// for everything the interface draws but the title screen's sigils.
/// </param>
/// <param name="Gradient">
/// The colour at the far edge, or null for one flat colour. <see cref="Color"/> is then the
/// colour at the near edge, and every pixel between them is interpolated.
/// </param>
/// <param name="GradientDown">
/// Whether <see cref="Gradient"/> runs from the top edge to the bottom rather than from the
/// left edge to the right.
/// </param>
/// <remarks>
/// The gradient exists because of what the title screen does: a wall drawn in slices whose
/// opacity drifts across the window. One colour a slice makes each slice a flat band, and
/// on a screen blend a two-percent step between neighbours is a visible upright line —
/// which is what the first version of that screen looked like. Interpolated, two slices
/// that meet agree exactly at the edge they share and there is nothing to see.
/// </remarks>
public readonly record struct OverlayQuad(
    Vector4 Destination,
    Vector4 Source,
    Vector4 Color,
    int Picture = 0,
    OverlayBlend Blend = OverlayBlend.Alpha,
    float Turn = 0f,
    Vector4? Gradient = null,
    bool GradientDown = false);

/// <summary>
/// Everything the interface draws, as one sheet and one list of rectangles.
/// </summary>
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
    /// An atlas with no letters in it: a block of white, and nothing else.
    /// </summary>
    /// <returns>The atlas.</returns>
    public static OverlayAtlas Blank()
    {
        byte[] pixels = new byte[WhiteSize * WhiteSize * 4];
        Array.Fill(pixels, (byte)255);

        return new OverlayAtlas(
            new DecodedImage(WhiteSize, WhiteSize, pixels, HasAlpha: true, "overlay-blank"),
            null,

            // The middle of it. There is nothing else on the sheet to sample by mistake,
            // but a texel centre is what every other atlas hands out and a rule with one
            // exception in it is a rule somebody has to remember.
            new Vector4(0.5f, 0.5f, 0f, 0f),
            "blank",
            WhiteSize);
    }

    /// <summary>
    /// The characters an interface atlas carries whatever the language.
    /// </summary>
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
    /// The characters an interface atlas should carry to draw one language.
    /// </summary>
    /// <param name="codePage">The language's code page — see <c>GameLanguage.CodePage</c>.</param>
    /// <returns><see cref="Latin"/>, and the rest of that page's letters after it.</returns>
    public static string Of(int codePage) =>
        Latin + Foundation.Gk3Encoding.Repertoire(codePage);

    /// <summary>
    /// Every character any language the port knows can be written in.
    /// </summary>
    public static string Everything { get; } =
        Latin +
        Foundation.Gk3Encoding.Repertoire(1252) +
        Foundation.Gk3Encoding.Repertoire(1250) +
        Foundation.Gk3Encoding.Repertoire(1251);

    /// <summary>
    /// Builds an atlas by drawing an outline font at a size.
    /// </summary>
    /// <param name="face">The font.</param>
    /// <param name="pixels">How tall an em should be, in pixels.</param>
    /// <param name="characters">Which characters to draw, or null for <see cref="Latin"/>.</param>
    /// <returns>The atlas, or null when nothing could be drawn.</returns>
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
public sealed class Overlay
{
    private readonly List<OverlayQuad> _quads = [];
    private readonly List<Vector4> _clips = [];

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
        _clips.Clear();
    }

    /// <summary>
    /// Confines everything drawn until the matching <see cref="PopClip"/> to a rectangle.
    /// </summary>
    /// <param name="bounds">The rectangle, as x, y, width, height.</param>
    public void PushClip(Vector4 bounds)
    {
        _clips.Add(_clips.Count == 0 ? bounds : Intersect(_clips[^1], bounds));
    }

    /// <summary>Lifts the last clip.</summary>
    public void PopClip()
    {
        if (_clips.Count > 0)
        {
            _clips.RemoveAt(_clips.Count - 1);
        }
    }

    /// <summary>Adds a rectangle, trimmed to whatever clip is in force.</summary>
    private void Add(OverlayQuad quad)
    {
        if (_clips.Count == 0)
        {
            _quads.Add(quad);

            return;
        }

        Vector4 clip = _clips[^1];
        Vector4 to = quad.Destination;

        // A turned rectangle cannot be trimmed to an upright box by moving its corners, so
        // it is kept whole or dropped whole. Binary rather than wrong: a caller that wants
        // a turned sprite confined to something has to keep it inside itself, and one that
        // does not is told by the sprite disappearing rather than by it being sheared.
        if (quad.Turn != 0f)
        {
            float reach = MathF.Max(to.Z, to.W);
            float middleX = to.X + (to.Z / 2f);
            float middleY = to.Y + (to.W / 2f);

            if (middleX - reach >= clip.X && middleX + reach <= clip.X + clip.Z &&
                middleY - reach >= clip.Y && middleY + reach <= clip.Y + clip.W)
            {
                _quads.Add(quad);
            }

            return;
        }

        float left = MathF.Max(to.X, clip.X);
        float top = MathF.Max(to.Y, clip.Y);
        float right = MathF.Min(to.X + to.Z, clip.X + clip.Z);
        float bottom = MathF.Min(to.Y + to.W, clip.Y + clip.W);

        if (right <= left || bottom <= top)
        {
            return;
        }

        if (left == to.X && top == to.Y && right == to.X + to.Z && bottom == to.Y + to.W)
        {
            _quads.Add(quad);

            return;
        }

        // What fraction of the original each edge moved by, which is the same fraction of
        // the source to take. Guarded against a zero-sized original, which cannot be
        // clipped into anything and is dropped above anyway.
        Vector4 from = quad.Source;

        float u = to.Z > 0 ? (left - to.X) / to.Z : 0;
        float v = to.W > 0 ? (top - to.Y) / to.W : 0;
        float du = to.Z > 0 ? (right - left) / to.Z : 0;
        float dv = to.W > 0 ? (bottom - top) / to.W : 0;

        // And the same fraction of the gradient, or the clipped-off part of a wall slice
        // takes its neighbour's colour and the fade shows a step exactly where the clip is.
        float near = quad.GradientDown ? v : u;
        float far = quad.GradientDown ? v + dv : u + du;

        (Vector4 color, Vector4? gradient) = quad.Gradient is { } end
            ? (Mix(quad.Color, end, near), Mix(quad.Color, end, far))
            : (quad.Color, quad.Gradient);

        _quads.Add(quad with
        {
            Destination = new Vector4(left, top, right - left, bottom - top),
            Source = new Vector4(
                from.X + (from.Z * u), from.Y + (from.W * v), from.Z * du, from.W * dv),
            Color = color,
            Gradient = gradient,
        });
    }

    /// <summary>One colour part of the way to another.</summary>
    private static Vector4 Mix(Vector4 from, Vector4 to, float part) =>
        from + ((to - from) * Math.Clamp(part, 0f, 1f));

    /// <summary>The overlap of two rectangles, which may be empty.</summary>
    private static Vector4 Intersect(Vector4 a, Vector4 b)
    {
        float left = MathF.Max(a.X, b.X);
        float top = MathF.Max(a.Y, b.Y);
        float right = MathF.Min(a.X + a.Z, b.X + b.Z);
        float bottom = MathF.Min(a.Y + a.W, b.Y + b.W);

        return new Vector4(left, top, MathF.Max(0, right - left), MathF.Max(0, bottom - top));
    }

    /// <summary>Draws a solid rectangle.</summary>
    /// <param name="x">Pixels from the left.</param>
    /// <param name="y">Pixels from the top.</param>
    /// <param name="width">How wide.</param>
    /// <param name="height">How tall.</param>
    /// <param name="color">What colour, straight alpha.</param>
    public void Rect(float x, float y, float width, float height, Vector4 color) =>
        Add(new OverlayQuad(
            new Vector4(x, y, width, height), Atlas.White, color));

    /// <summary>Draws a rectangle that fades from one colour to another.</summary>
    /// <param name="x">Pixels from the left.</param>
    /// <param name="y">Pixels from the top.</param>
    /// <param name="width">How wide.</param>
    /// <param name="height">How tall.</param>
    /// <param name="from">The colour at the near edge, straight alpha.</param>
    /// <param name="to">The colour at the far edge.</param>
    /// <param name="down">Whether it runs top to bottom rather than left to right.</param>
    public void Fade(
        float x,
        float y,
        float width,
        float height,
        Vector4 from,
        Vector4 to,
        bool down = true) =>
        Add(new OverlayQuad(
            new Vector4(x, y, width, height),
            Atlas.White,
            from,
            0,
            OverlayBlend.Alpha,
            0f,
            to,
            down));

    /// <summary>
    /// Draws part of one of the screens' own pictures.
    /// </summary>
    /// <param name="picture">Which picture, as <c>OverlayImages</c> numbers them.</param>
    /// <param name="x">Pixels from the left.</param>
    /// <param name="y">Pixels from the top.</param>
    /// <param name="width">How wide to draw it.</param>
    /// <param name="height">How tall.</param>
    /// <param name="tint">What to multiply it by; white leaves it alone.</param>
    /// <param name="source">
    /// Which part of the picture to take, in texture coordinates, or null for all of it.
    /// </param>
    /// <param name="blend">How it is combined with what is already on the screen.</param>
    /// <param name="gradient">
    /// What to tint the far edge, or null to tint the whole rectangle the same.
    /// </param>
    /// <param name="down">
    /// Whether the gradient runs to the bottom edge rather than to the right-hand one.
    /// </param>
    public void Picture(
        int picture,
        float x,
        float y,
        float width,
        float height,
        Vector4 tint,
        Vector4? source = null,
        OverlayBlend blend = OverlayBlend.Alpha,
        Vector4? gradient = null,
        bool down = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(picture);

        Add(new OverlayQuad(
            new Vector4(x, y, width, height),
            source ?? new Vector4(0, 0, 1, 1),
            tint,
            picture,
            blend,
            0f,
            gradient,
            down));
    }

    /// <summary>
    /// Draws one of the screens' own pictures about a point, turned.
    /// </summary>
    /// <param name="picture">Which picture, as <c>OverlayImages</c> numbers them.</param>
    /// <param name="centre">Where its middle goes, in pixels from the top left.</param>
    /// <param name="size">How wide and how tall to draw it.</param>
    /// <param name="tint">What to multiply it by; white leaves it alone.</param>
    /// <param name="turn">How far to turn it about that middle, in radians, clockwise.</param>
    /// <param name="blend">How it is combined with what is already on the screen.</param>
    /// <remarks>
    /// The one thing the interface draws that is not square to the screen. A turned quad is
    /// kept whole or dropped whole by a clip — see <c>Add</c> — so a caller that wants it
    /// confined has to place it inside whatever is confining it.
    /// </remarks>
    public void Sprite(
        int picture,
        Vector2 centre,
        Vector2 size,
        Vector4 tint,
        float turn = 0f,
        OverlayBlend blend = OverlayBlend.Alpha)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(picture);

        Add(new OverlayQuad(
            new Vector4(centre.X - (size.X / 2f), centre.Y - (size.Y / 2f), size.X, size.Y),
            new Vector4(0, 0, 1, 1),
            tint,
            picture,
            blend,
            turn));
    }

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
                Add(new OverlayQuad(
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
