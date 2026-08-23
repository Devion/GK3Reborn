using System.Buffers.Binary;
using System.Text;
using GK3Reborn.Foundation.Diagnostics;

namespace GK3Reborn.Formats.Fonts;

/// <summary>One point of a glyph's outline, in the font's own units.</summary>
/// <param name="X">Distance from the origin.</param>
/// <param name="Y">Distance from the baseline.</param>
/// <param name="OnCurve">
/// Whether the outline passes through it. A point that is not on the curve is the control
/// point of a quadratic curve between the points either side of it.
/// </param>
public readonly record struct GlyphPoint(float X, float Y, bool OnCurve);

/// <summary>A glyph's shape, in the font's own units.</summary>
/// <param name="Points">Every point of every contour, in order.</param>
/// <param name="Ends">The index of the last point of each contour.</param>
/// <param name="Left">The leftmost point.</param>
/// <param name="Bottom">The lowest point.</param>
/// <param name="Right">The rightmost point.</param>
/// <param name="Top">The highest point.</param>
public sealed record GlyphOutline(
    IReadOnlyList<GlyphPoint> Points,
    IReadOnlyList<int> Ends,
    float Left,
    float Bottom,
    float Right,
    float Top);

/// <summary>
/// A TrueType font: the outlines, the metrics, and which glyph a character is.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists at all.</b> GK3's own fonts are bitmap sheets drawn for a 640x480
/// screen. Magnifying one by a whole number is the best that can be done with it, and on a
/// modern display the interface is visibly made of enlarged pixels. An outline font is
/// rasterised at whatever size the window actually is, so the text is crisp everywhere and
/// the caption ladder stops being a ladder.
/// </para>
/// <para>
/// Written rather than taken from a package, in the same way every other format in this
/// project is: the game's own parsers are here and this is no harder than the BSP.
/// </para>
/// <para>
/// <b>What is deliberately not read.</b> Hinting instructions are ignored — they are a
/// bytecode interpreter's worth of work to serve 96-dpi screens that no longer exist, and
/// a well-made font at menu sizes reads perfectly without them. Kerning lives in
/// <c>GPOS</c> in modern fonts and is not read either, which costs a little air around a
/// few pairs and nothing else. <c>CFF</c> outlines — an OpenType font whose curves are
/// cubic — are refused rather than half-read.
/// </para>
/// </remarks>
public sealed class TrueTypeFile
{
    private readonly byte[] _data;
    private readonly Dictionary<string, (int Offset, int Length)> _tables;
    private readonly Dictionary<int, int> _characters = [];
    private readonly int _loca;
    private readonly int _glyf;
    private readonly int _hmtx;
    private readonly int _metrics;
    private readonly bool _longOffsets;

    private TrueTypeFile(
        byte[] data,
        string name,
        Dictionary<string, (int Offset, int Length)> tables,
        int unitsPerEm,
        bool longOffsets)
    {
        _data = data;
        _tables = tables;
        _longOffsets = longOffsets;

        Name = name;
        UnitsPerEm = unitsPerEm;

        _loca = tables["loca"].Offset;
        _glyf = tables["glyf"].Offset;
        _hmtx = tables["hmtx"].Offset;

        int hhea = tables["hhea"].Offset;

        Ascender = Signed(hhea + 4);
        Descender = Signed(hhea + 6);
        LineGap = Signed(hhea + 8);
        _metrics = Unsigned(hhea + 34);

        GlyphCount = Unsigned(tables["maxp"].Offset + 4);

        Family = Text(4) ?? name;
        Licence = Text(13);

        ReadCharacters();
    }

    /// <summary>What the file was called.</summary>
    public string Name { get; }

    /// <summary>The family name the font gives itself.</summary>
    public string Family { get; }

    /// <summary>The licence the font states, if it states one.</summary>
    public string? Licence { get; }

    /// <summary>How many units make an em, which every other measurement is in.</summary>
    public int UnitsPerEm { get; }

    /// <summary>How far above the baseline the tallest letters reach.</summary>
    public int Ascender { get; }

    /// <summary>How far below it the descenders go; negative.</summary>
    public int Descender { get; }

    /// <summary>The air the font asks for between one line and the next.</summary>
    public int LineGap { get; }

    /// <summary>How many glyphs it has.</summary>
    public int GlyphCount { get; }

    /// <summary>How many characters it can draw.</summary>
    public int CharacterCount => _characters.Count;

    /// <summary>Reads a font.</summary>
    /// <param name="data">The file.</param>
    /// <param name="name">What to call it in a diagnostic.</param>
    /// <param name="diagnostics">Where a refusal is reported.</param>
    /// <returns>The font, or null when it is not one this can draw.</returns>
    /// <remarks>
    /// Refused rather than guessed at. A font that half-loads draws a menu of blanks, and
    /// blanks look exactly like a layout bug a long way from here.
    /// </remarks>
    public static TrueTypeFile? Parse(byte[] data, string name, DiagnosticBag diagnostics)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(diagnostics);

        void Refuse(string wanted, string got) => diagnostics.Add(new Diagnostic(
            "GK3R1200", DiagnosticSeverity.Warning,
            "A font could not be read, so the interface falls back to the game's own.",
            name, null, wanted, got, "Any TrueType font with glyf outlines will do."));

        if (data.Length < 12)
        {
            Refuse("a font file", $"{data.Length} bytes");
            return null;
        }

        uint version = BinaryPrimitives.ReadUInt32BigEndian(data);

        // 0x00010000 is TrueType and "true" is the same thing from Apple. "OTTO" is an
        // OpenType font with CFF outlines, which are cubic and a different format again;
        // "ttcf" is a collection of several fonts in one file.
        if (version is not (0x00010000 or 0x74727565))
        {
            Refuse(
                "TrueType outlines",
                version == 0x4F54544F ? "an OpenType font with CFF outlines"
                    : version == 0x74746366 ? "a font collection"
                    : $"format {version:X8}");

            return null;
        }

        int count = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(4));
        Dictionary<string, (int Offset, int Length)> tables = new(StringComparer.Ordinal);

        for (int i = 0; i < count; i++)
        {
            int at = 12 + (16 * i);

            if (at + 16 > data.Length)
            {
                break;
            }

            string tag = Encoding.ASCII.GetString(data, at, 4);
            int offset = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(at + 8));
            int length = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(at + 12));

            if (offset >= 0 && length >= 0 && offset + length <= data.Length)
            {
                tables[tag] = (offset, length);
            }
        }

        foreach (string wanted in (string[])["head", "hhea", "maxp", "hmtx", "loca", "glyf", "cmap"])
        {
            if (!tables.ContainsKey(wanted))
            {
                Refuse($"a {wanted} table", "it is not there");
                return null;
            }
        }

        int head = tables["head"].Offset;
        int units = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(head + 18));
        int format = BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(head + 50));

        if (units <= 0)
        {
            Refuse("an em of some size", $"{units} units");
            return null;
        }

        var font = new TrueTypeFile(data, name, tables, units, format != 0);

        if (font.CharacterCount == 0)
        {
            Refuse("a character map this can read", "none of format 4, 6 or 12");
            return null;
        }

        return font;
    }

    /// <summary>Which glyph draws a character.</summary>
    /// <param name="codepoint">The character.</param>
    /// <returns>The glyph, or zero for the font's own "not in this font" box.</returns>
    public int GlyphOf(int codepoint) =>
        _characters.TryGetValue(codepoint, out int glyph) ? glyph : 0;

    /// <summary>Whether the font can draw a character.</summary>
    /// <param name="codepoint">The character.</param>
    /// <returns>True when it has a glyph of its own for it.</returns>
    public bool Has(int codepoint) => _characters.ContainsKey(codepoint);

    /// <summary>How far the pen moves after a glyph, in font units.</summary>
    /// <param name="glyph">The glyph.</param>
    /// <returns>The advance width.</returns>
    /// <remarks>
    /// The last entry of <c>hmtx</c> stands for every glyph after it, which is how a font
    /// of monospaced digits or a column of identical accents costs two bytes each.
    /// </remarks>
    public int AdvanceOf(int glyph)
    {
        if (_metrics <= 0)
        {
            return 0;
        }

        int at = _hmtx + (4 * Math.Min(glyph, _metrics - 1));

        return at + 2 <= _data.Length ? Unsigned(at) : 0;
    }

    /// <summary>Reads a glyph's outline.</summary>
    /// <param name="glyph">Which glyph.</param>
    /// <returns>The outline, or null when the glyph is blank.</returns>
    /// <remarks>
    /// A space has no outline and is not an error; nor is a composite glyph, which is one
    /// or more other glyphs placed and possibly scaled — every accented letter in the
    /// French this game is set in is one.
    /// </remarks>
    public GlyphOutline? OutlineOf(int glyph) => Outline(glyph, depth: 0);

    private GlyphOutline? Outline(int glyph, int depth)
    {
        // A composite that names itself, or a ring of them. Five deep is more than any real
        // font needs and stops a malformed one from running out of stack.
        if (depth > 5 || glyph < 0 || glyph >= GlyphCount)
        {
            return null;
        }

        (int start, int end) = Location(glyph);

        if (end <= start || start < 0 || end > _tables["glyf"].Length)
        {
            return null;
        }

        int at = _glyf + start;
        int contours = Signed(at);

        float left = Signed(at + 2);
        float bottom = Signed(at + 4);
        float right = Signed(at + 6);
        float top = Signed(at + 8);

        return contours >= 0
            ? Simple(at + 10, contours, left, bottom, right, top)
            : Composite(at + 10, depth, left, bottom, right, top);
    }

    private GlyphOutline? Simple(
        int at, int contours, float left, float bottom, float right, float top)
    {
        if (contours == 0)
        {
            return null;
        }

        var ends = new int[contours];

        for (int i = 0; i < contours; i++)
        {
            ends[i] = Unsigned(at + (2 * i));
        }

        at += 2 * contours;

        int points = ends[^1] + 1;

        if (points <= 0 || points > 10000)
        {
            return null;
        }

        // The hinting bytecode, which is skipped rather than run.
        at += 2 + Unsigned(at);

        var flags = new byte[points];

        for (int i = 0; i < points;)
        {
            if (at >= _data.Length)
            {
                return null;
            }

            byte flag = _data[at++];
            flags[i++] = flag;

            // Bit 3: the same flag again, as many times as the next byte says. A straight
            // edge of a dozen points costs three bytes rather than twelve.
            if ((flag & 0x08) != 0 && at < _data.Length)
            {
                int again = _data[at++];

                for (int r = 0; r < again && i < points; r++)
                {
                    flags[i++] = flag;
                }
            }
        }

        var xs = new float[points];
        float x = 0;

        for (int i = 0; i < points; i++)
        {
            byte flag = flags[i];

            if ((flag & 0x02) != 0)
            {
                // A byte, whose sign is bit 4.
                if (at >= _data.Length)
                {
                    return null;
                }

                int step = _data[at++];
                x += (flag & 0x10) != 0 ? step : -step;
            }
            else if ((flag & 0x10) == 0)
            {
                x += Signed(at);
                at += 2;
            }

            xs[i] = x;
        }

        var ys = new float[points];
        float y = 0;

        for (int i = 0; i < points; i++)
        {
            byte flag = flags[i];

            if ((flag & 0x04) != 0)
            {
                if (at >= _data.Length)
                {
                    return null;
                }

                int step = _data[at++];
                y += (flag & 0x20) != 0 ? step : -step;
            }
            else if ((flag & 0x20) == 0)
            {
                y += Signed(at);
                at += 2;
            }

            ys[i] = y;
        }

        var shape = new GlyphPoint[points];

        for (int i = 0; i < points; i++)
        {
            shape[i] = new GlyphPoint(xs[i], ys[i], (flags[i] & 0x01) != 0);
        }

        return new GlyphOutline(shape, ends, left, bottom, right, top);
    }

    private GlyphOutline? Composite(
        int at, int depth, float left, float bottom, float right, float top)
    {
        List<GlyphPoint> points = [];
        List<int> ends = [];

        while (true)
        {
            if (at + 4 > _data.Length)
            {
                break;
            }

            int flags = Unsigned(at);
            int index = Unsigned(at + 2);
            at += 4;

            float dx;
            float dy;

            // Bit 0: the arguments are words rather than bytes. Bit 1: they are an offset
            // rather than a pair of points to match up, which is what every font in
            // practice uses.
            if ((flags & 0x0001) != 0)
            {
                dx = Signed(at);
                dy = Signed(at + 2);
                at += 4;
            }
            else
            {
                dx = (sbyte)_data[at];
                dy = (sbyte)_data[at + 1];
                at += 2;
            }

            float a = 1;
            float b = 0;
            float c = 0;
            float d = 1;

            if ((flags & 0x0008) != 0)
            {
                a = d = F2Dot14(at);
                at += 2;
            }
            else if ((flags & 0x0040) != 0)
            {
                a = F2Dot14(at);
                d = F2Dot14(at + 2);
                at += 4;
            }
            else if ((flags & 0x0080) != 0)
            {
                a = F2Dot14(at);
                b = F2Dot14(at + 2);
                c = F2Dot14(at + 4);
                d = F2Dot14(at + 6);
                at += 8;
            }

            if (Outline(index, depth + 1) is { } part)
            {
                int began = points.Count;

                foreach (GlyphPoint point in part.Points)
                {
                    points.Add(new GlyphPoint(
                        (a * point.X) + (c * point.Y) + dx,
                        (b * point.X) + (d * point.Y) + dy,
                        point.OnCurve));
                }

                foreach (int end in part.Ends)
                {
                    ends.Add(end + began);
                }
            }

            if ((flags & 0x0020) == 0)
            {
                break;
            }
        }

        return points.Count > 0
            ? new GlyphOutline(points, ends, left, bottom, right, top)
            : null;
    }

    private (int Start, int End) Location(int glyph)
    {
        if (_longOffsets)
        {
            int at = _loca + (4 * glyph);

            return at + 8 <= _data.Length
                ? ((int)BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(at)),
                   (int)BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(at + 4)))
                : (0, 0);
        }

        // The short form stores half the offset, because glyphs are word-aligned.
        int here = _loca + (2 * glyph);

        return here + 4 <= _data.Length
            ? (Unsigned(here) * 2, Unsigned(here + 2) * 2)
            : (0, 0);
    }

    private void ReadCharacters()
    {
        int cmap = _tables["cmap"].Offset;
        int tables = Unsigned(cmap + 2);

        int best = -1;
        int score = -1;

        for (int i = 0; i < tables; i++)
        {
            int at = cmap + 4 + (8 * i);

            if (at + 8 > _data.Length)
            {
                break;
            }

            int platform = Unsigned(at);
            int encoding = Unsigned(at + 2);
            int offset = (int)BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(at + 4));

            // Windows Unicode first, then anything else Unicode, then Macintosh Roman as a
            // last resort — which is the order of how much of the alphabet they carry.
            int worth = (platform, encoding) switch
            {
                (3, 10) => 5,
                (3, 1) => 4,
                (0, _) => 3,
                (3, 0) => 2,
                (1, 0) => 1,
                _ => 0,
            };

            if (worth > score && cmap + offset < _data.Length)
            {
                score = worth;
                best = cmap + offset;
            }
        }

        if (best < 0)
        {
            return;
        }

        switch (Unsigned(best))
        {
            case 4:
                Format4(best);
                break;

            case 6:
                Format6(best);
                break;

            case 12:
                Format12(best);
                break;

            default:
                break;
        }
    }

    /// <summary>The one every font has: characters below 0xFFFF, in ranges.</summary>
    private void Format4(int at)
    {
        int segments = Unsigned(at + 6) / 2;
        int ends = at + 14;
        int starts = ends + (2 * segments) + 2;
        int deltas = starts + (2 * segments);
        int ranges = deltas + (2 * segments);

        for (int i = 0; i < segments; i++)
        {
            int last = Unsigned(ends + (2 * i));
            int first = Unsigned(starts + (2 * i));
            int delta = Signed(deltas + (2 * i));
            int range = Unsigned(ranges + (2 * i));

            if (first > last || last == 0xFFFF && first == 0xFFFF)
            {
                continue;
            }

            for (int c = first; c <= last && c <= 0xFFFF; c++)
            {
                int glyph;

                if (range == 0)
                {
                    glyph = (c + delta) & 0xFFFF;
                }
                else
                {
                    int where = ranges + (2 * i) + range + (2 * (c - first));

                    if (where + 2 > _data.Length)
                    {
                        continue;
                    }

                    glyph = Unsigned(where);

                    if (glyph != 0)
                    {
                        glyph = (glyph + delta) & 0xFFFF;
                    }
                }

                if (glyph != 0)
                {
                    _characters[c] = glyph;
                }
            }
        }
    }

    /// <summary>A single run of characters, which small fonts use.</summary>
    private void Format6(int at)
    {
        int first = Unsigned(at + 6);
        int count = Unsigned(at + 8);

        for (int i = 0; i < count; i++)
        {
            int glyph = Unsigned(at + 10 + (2 * i));

            if (glyph != 0)
            {
                _characters[first + i] = glyph;
            }
        }
    }

    /// <summary>Ranges over the whole of Unicode, which fonts with emoji need.</summary>
    private void Format12(int at)
    {
        int groups = (int)BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(at + 12));

        for (int i = 0; i < groups; i++)
        {
            int where = at + 16 + (12 * i);

            if (where + 12 > _data.Length)
            {
                break;
            }

            uint first = BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(where));
            uint last = BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(where + 4));
            uint glyph = BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(where + 8));

            // Whole planes of characters nothing here will ever draw. The interface needs
            // Latin and its accents; reading a million entries to find them is waste.
            for (uint c = first; c <= last && c <= 0x2FFF; c++)
            {
                _characters[(int)c] = (int)(glyph + (c - first));
            }
        }
    }

    /// <summary>A string from the name table, in whichever encoding it was written.</summary>
    private string? Text(int wanted)
    {
        if (!_tables.TryGetValue("name", out (int Offset, int Length) name))
        {
            return null;
        }

        int count = Unsigned(name.Offset + 2);
        int strings = name.Offset + Unsigned(name.Offset + 4);

        foreach (bool unicode in (bool[])[true, false])
        {
            for (int i = 0; i < count; i++)
            {
                int at = name.Offset + 6 + (12 * i);

                if (at + 12 > _data.Length)
                {
                    break;
                }

                int platform = Unsigned(at);
                int id = Unsigned(at + 6);
                int length = Unsigned(at + 8);
                int offset = Unsigned(at + 10);

                if (id != wanted || strings + offset + length > _data.Length)
                {
                    continue;
                }

                bool wide = platform is 0 or 3;

                if (wide != unicode)
                {
                    continue;
                }

                return (wide ? Encoding.BigEndianUnicode : Encoding.ASCII)
                    .GetString(_data, strings + offset, length);
            }
        }

        return null;
    }

    private int Unsigned(int at) =>
        at + 2 <= _data.Length ? BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(at)) : 0;

    private int Signed(int at) =>
        at + 2 <= _data.Length ? BinaryPrimitives.ReadInt16BigEndian(_data.AsSpan(at)) : 0;

    /// <summary>A fixed-point number with two bits before the point and fourteen after.</summary>
    private float F2Dot14(int at) => Signed(at) / 16384f;
}
