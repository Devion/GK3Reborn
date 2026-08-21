using System.Globalization;
using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Foundation.Diagnostics;

namespace GK3Reborn.Formats.Ui;

/// <summary>Where one character sits in the font's bitmap.</summary>
/// <param name="X">Pixels from the left of the sheet.</param>
/// <param name="Y">Pixels from the top of the sheet.</param>
/// <param name="Width">How wide the character is.</param>
/// <param name="Height">How tall it is.</param>
public readonly record struct Glyph(int X, int Y, int Width, int Height);

/// <summary>
/// One of GK3's 137 bitmap fonts.
/// </summary>
/// <remarks>
/// <para>
/// A <c>.FON</c> is a handful of keys and one long <c>Font=</c> line listing, in order,
/// every character the sheet contains. The sheet itself is an ordinary texture. What is
/// not written down anywhere is where each character starts and stops: the top row of the
/// sheet carries a marker colour at the left edge of every glyph, and the width of a
/// character is the distance to the next marker.
/// </para>
/// <para>
/// So reading a font means scanning a row of pixels. The first pixel of the sheet is the
/// background; the first pixel along the top row that is not the background is the marker;
/// and from there each run between markers is one character, taken in the order the
/// <c>Font=</c> line gives them.
/// </para>
/// <para>
/// Some sheets stack several rows of glyphs, which <c>Line Count</c> gives. The top pixel
/// of each row is the marker strip and not part of the letter, so a glyph is one pixel
/// shorter than the row that holds it.
/// </para>
/// </remarks>
public sealed class FontFile
{
    private readonly Dictionary<char, Glyph> _glyphs = [];

    private FontFile(string name, DecodedImage sheet)
    {
        Name = name;
        Sheet = sheet;
    }

    /// <summary>Name it was read under.</summary>
    public string Name { get; }

    /// <summary>The texture the characters are cut from.</summary>
    public DecodedImage Sheet { get; }

    /// <summary>The bitmap this font asked for.</summary>
    public string? BitmapName { get; private init; }

    /// <summary>How tall one character is, in pixels.</summary>
    public int Height { get; private set; }

    /// <summary>Extra pixels between characters.</summary>
    public int CharacterSpacing { get; private init; }

    /// <summary>Extra pixels between lines.</summary>
    public int LineSpacing { get; private init; }

    /// <summary>How many characters were found.</summary>
    public int Count => _glyphs.Count;

    /// <summary>The character drawn in place of one the font does not have.</summary>
    public char Fallback { get; private init; } = '?';

    /// <summary>Looks a character up.</summary>
    /// <param name="c">The character.</param>
    /// <returns>Where it is, or the fallback's place, or null when there is neither.</returns>
    public Glyph? this[char c] =>
        _glyphs.TryGetValue(c, out Glyph glyph) ? glyph
        : _glyphs.TryGetValue(Fallback, out Glyph other) ? other
        : null;

    /// <summary>How wide a string is when drawn.</summary>
    /// <param name="text">The string.</param>
    /// <returns>Width in pixels.</returns>
    public int Measure(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        int width = 0;

        foreach (char c in text)
        {
            if (this[c] is { } glyph)
            {
                width += glyph.Width + CharacterSpacing;
            }
        }

        return width;
    }

    /// <summary>Reads a font.</summary>
    /// <param name="definition">The <c>.FON</c> text.</param>
    /// <param name="sheet">Its bitmap, already decoded.</param>
    /// <param name="name">Name used in diagnostics.</param>
    /// <param name="diagnostics">Receives a reason when the sheet cannot be cut up.</param>
    /// <returns>The font.</returns>
    public static FontFile Parse(
        string definition, DecodedImage sheet, string name, DiagnosticBag diagnostics)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(diagnostics);

        Dictionary<string, string> keys = Keys(definition);

        string characters = keys.GetValueOrDefault("FONT", string.Empty);
        int lines = Number(keys, "LINE COUNT", 1);

        var font = new FontFile(name, sheet)
        {
            BitmapName = keys.GetValueOrDefault("BITMAP NAME"),
            CharacterSpacing = Number(keys, "CHAR EXTRA", 0),
            LineSpacing = Number(keys, "LINE EXTRA", 0),
            Fallback = keys.GetValueOrDefault("DEFAULT CHAR") is { Length: > 0 } d ? d[0] : '?',
        };

        if (characters.Length == 0 || sheet.Width <= 0 || sheet.Height <= 0)
        {
            diagnostics.Add(new Diagnostic(
                "GK3R1140", DiagnosticSeverity.Warning,
                "A font has no characters or no bitmap, so nothing can be drawn with it.",
                name, null, "a Font= line and a sheet to cut it from",
                string.Create(CultureInfo.InvariantCulture,
                    $"{characters.Length} character(s), {sheet.Width}x{sheet.Height}"),
                "Check that the font's bitmap is in the archives under its Bitmap Name."));

            return font;
        }

        font.Cut(characters, Math.Max(1, lines), diagnostics);
        return font;
    }

    /// <summary>
    /// Walks the marker strip and cuts the sheet into characters.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The marker colour is whatever the first non-background pixel of the top row is —
    /// most fonts use pure red, but not all of them do, and a couple are a few units off
    /// it, so the comparison allows a little slack rather than demanding an exact match.
    /// </para>
    /// <para>
    /// <b>A row's last marker may be a terminator rather than a glyph.</b> On a sheet with
    /// one row the last letter ends at the sheet's right edge and nothing has to say so; on
    /// a sheet with several, the rows are different lengths and each needs a mark saying
    /// where its last letter stops, with padding after it. Counting that mark as a glyph
    /// costs the row one character and shifts every character after it — which is why the
    /// caption sheets, the only multi-row fonts the interface draws with, wrote
    /// <c>Gabqiel Lnnk</c> where they meant <c>Gabriel Look</c>.
    /// </para>
    /// <para>
    /// Which of the two a sheet is doing is decided by counting rather than guessing: the
    /// <c>Font=</c> line says how many characters there are, so a sheet with exactly that
    /// many markers has no terminators and one with that many plus a marker per row has one
    /// each. Neither, and each row is judged on whether there is any ink after its last
    /// mark. Across the corpus the count settles 112 of the 136 fonts outright.
    /// </para>
    /// </remarks>
    private void Cut(string characters, int lines, DiagnosticBag diagnostics)
    {
        int rowHeight = Sheet.Height / lines;
        Height = Math.Max(1, rowHeight - 1);

        (byte R, byte G, byte B) background = Pixel(0, 0);
        int start = -1;

        for (int x = 0; x < Sheet.Width; x++)
        {
            if (Pixel(x, 0) != background)
            {
                start = x;
                break;
            }
        }

        if (start < 0)
        {
            diagnostics.Add(new Diagnostic(
                "GK3R1141", DiagnosticSeverity.Warning,
                "A font's bitmap has no glyph markers, so its characters cannot be found.",
                Name, null, "a marker colour somewhere along the top row",
                "the whole row is the background colour",
                "The sheet may be the wrong bitmap for this font."));

            return;
        }

        (byte R, byte G, byte B) marker = Pixel(start, 0);
        List<int>[] marks = new List<int>[lines];
        int total = 0;

        for (int line = 0; line < lines; line++)
        {
            marks[line] = Marks(marker, line * rowHeight, start);
            total += marks[line].Count;
        }

        // Every row terminated, no row terminated, or work it out row by row.
        bool? terminated =
            total == characters.Length + lines ? true :
            total == characters.Length ? false :
            null;

        int at = 0;

        for (int line = 0; line < lines && at < characters.Length; line++)
        {
            List<int> row = marks[line];

            if (row.Count == 0)
            {
                continue;
            }

            bool ends = terminated ?? !HasInk(background, row[^1], line * rowHeight, rowHeight);
            int glyphs = ends ? row.Count - 1 : row.Count;

            for (int i = 0; i < glyphs && at < characters.Length; i++)
            {
                int from = row[i];
                int to = i + 1 < row.Count ? row[i + 1] : Sheet.Width;

                _glyphs[characters[at]] = new Glyph(
                    from, (line * rowHeight) + 1, Math.Max(1, to - from), Height);

                at++;
            }
        }

        // The one check that would have caught the terminator. A sheet that cuts into a
        // different number of pieces than the font says it has is a font whose letters are
        // all somebody else's, and nothing else about it looks wrong.
        if (at != characters.Length)
        {
            diagnostics.Add(new Diagnostic(
                "GK3R1142", DiagnosticSeverity.Warning,
                "A font's bitmap does not cut into as many characters as the font declares, " +
                "so its letters are shifted.",
                Name, null,
                string.Create(CultureInfo.InvariantCulture, $"{characters.Length} character(s)"),
                string.Create(CultureInfo.InvariantCulture, $"{at} cut from the sheet"),
                "The sheet may be the wrong bitmap, or its marker colour may appear inside " +
                "a letter."));
        }
    }

    /// <summary>Where the markers along one row's top edge are.</summary>
    /// <remarks>
    /// The starts of runs, not every marked pixel: a marker two pixels wide is one marker.
    /// The scan begins where the first row's did, because a row whose first letter is
    /// narrower than another's still starts in the same column.
    /// </remarks>
    private List<int> Marks(
        (byte R, byte G, byte B) marker, int y, int start)
    {
        List<int> marks = [];

        if (y >= Sheet.Height)
        {
            return marks;
        }

        for (int x = start; x < Sheet.Width; x++)
        {
            if (Near(Pixel(x, y), marker) && (x == start || !Near(Pixel(x - 1, y), marker)))
            {
                marks.Add(x);
            }
        }

        return marks;
    }

    /// <summary>Whether anything is drawn between a row's last marker and the sheet's edge.</summary>
    private bool HasInk(
        (byte R, byte G, byte B) background, int from, int y, int rowHeight)
    {
        for (int row = y + 1; row < Math.Min(Sheet.Height, y + rowHeight); row++)
        {
            for (int x = from; x < Sheet.Width; x++)
            {
                if (Pixel(x, row) != background)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private (byte R, byte G, byte B) Pixel(int x, int y)
    {
        int at = ((y * Sheet.Width) + x) * 4;
        return (Sheet.Pixels[at], Sheet.Pixels[at + 1], Sheet.Pixels[at + 2]);
    }

    /// <summary>Whether two colours are the same marker, allowing for a sloppy sheet.</summary>
    private static bool Near((byte R, byte G, byte B) a, (byte R, byte G, byte B) b) =>
        Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B) < 10;

    /// <summary>
    /// Reads the key/value lines.
    /// </summary>
    /// <remarks>
    /// Not the INI reader: keys have spaces in them, the <c>Font=</c> value is a run of
    /// characters that includes <c>;</c> and <c>,</c> and everything else the reader would
    /// treat as punctuation, and the whole point is to take it exactly as written.
    /// </remarks>
    private static Dictionary<string, string> Keys(string definition)
    {
        Dictionary<string, string> keys = new(StringComparer.OrdinalIgnoreCase);

        foreach (string line in definition.Split('\n'))
        {
            string text = line.TrimEnd('\r');

            if (text.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            int equals = text.IndexOf('=', StringComparison.Ordinal);

            if (equals > 0)
            {
                keys[text[..equals].Trim().ToUpperInvariant()] = text[(equals + 1)..];
            }
        }

        return keys;
    }

    private static int Number(Dictionary<string, string> keys, string key, int fallback) =>
        keys.TryGetValue(key, out string? text) &&
        int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : fallback;
}
