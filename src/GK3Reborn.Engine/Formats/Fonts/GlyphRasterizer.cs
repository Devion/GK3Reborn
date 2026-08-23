using System.Numerics;

namespace GK3Reborn.Formats.Fonts;

/// <summary>
/// A glyph drawn at a size: coverage, and where it sits against the pen.
/// </summary>
/// <param name="Width">How wide the drawn part is, in pixels.</param>
/// <param name="Height">How tall it is.</param>
/// <param name="Left">
/// Pixels from the pen to the left edge of the drawn part. Negative for a letter that
/// leans back over the one before it.
/// </param>
/// <param name="Top">Pixels from the baseline up to the top edge of the drawn part.</param>
/// <param name="Coverage">
/// How much of each pixel the glyph covers, one byte each, left to right and top to bottom.
/// </param>
public readonly record struct RasterGlyph(
    int Width, int Height, int Left, int Top, byte[] Coverage)
{
    /// <summary>Whether anything was drawn.</summary>
    public bool Marks => Width > 0 && Height > 0;
}

/// <summary>
/// Turns an outline into pixels.
/// </summary>
/// <remarks>
/// <para>
/// A scanline fill with the nonzero winding rule, sampled several times down each pixel
/// row and exactly across it. Vertical accuracy comes from the number of samples and
/// horizontal accuracy is free, because a span's ends are known to the fraction of a
/// pixel — which is the right trade for text, where the strokes that matter are vertical.
/// </para>
/// <para>
/// <b>No hinting.</b> The outline is not snapped to the pixel grid, so stems land where
/// the design puts them and are grey at the edges rather than crisp. That is the modern
/// answer — it is what a phone does — and it is the only one that keeps a letter the same
/// shape at every size. The alternative is a bytecode interpreter serving screens that no
/// longer exist.
/// </para>
/// </remarks>
public static class GlyphRasterizer
{
    /// <summary>How many times each pixel row is sampled.</summary>
    /// <remarks>
    /// Five is where the eye stops noticing on a horizontal edge, and each one costs a
    /// pass over the edges. Atlases are built once, so this is not a frame cost.
    /// </remarks>
    private const int Samples = 5;

    /// <summary>How finely a curve is broken into straight pieces.</summary>
    /// <remarks>
    /// In pixels of the drawn size: a curve is split until no piece bulges further than
    /// this from the line across it. At a fifth of a pixel the flattening is under the
    /// antialiasing.
    /// </remarks>
    private const float Flatness = 0.2f;

    /// <summary>Draws a glyph.</summary>
    /// <param name="outline">The shape, in font units.</param>
    /// <param name="scale">How many pixels one font unit is.</param>
    /// <returns>The coverage and where it goes, or an empty glyph for a blank one.</returns>
    public static RasterGlyph Render(GlyphOutline? outline, float scale)
    {
        if (outline is null || scale <= 0 || outline.Points.Count == 0)
        {
            return default;
        }

        List<(Vector2 From, Vector2 To)> edges = Flatten(outline, scale);

        if (edges.Count == 0)
        {
            return default;
        }

        float left = float.MaxValue;
        float right = float.MinValue;
        float top = float.MaxValue;
        float bottom = float.MinValue;

        foreach ((Vector2 from, Vector2 to) in edges)
        {
            left = MathF.Min(left, MathF.Min(from.X, to.X));
            right = MathF.Max(right, MathF.Max(from.X, to.X));
            top = MathF.Min(top, MathF.Min(from.Y, to.Y));
            bottom = MathF.Max(bottom, MathF.Max(from.Y, to.Y));
        }

        // The pixels the outline touches. Y grows downwards here and upwards in the font,
        // so the flattening has already turned it over: `top` is the smallest Y.
        int x0 = (int)MathF.Floor(left);
        int y0 = (int)MathF.Floor(top);
        int width = (int)MathF.Ceiling(right) - x0;
        int height = (int)MathF.Ceiling(bottom) - y0;

        if (width <= 0 || height <= 0 || (long)width * height > 4_000_000)
        {
            return default;
        }

        var coverage = new byte[width * height];
        var accumulator = new float[width];
        List<(float X, int Winding)> crossings = [];

        for (int row = 0; row < height; row++)
        {
            Array.Clear(accumulator);

            for (int sample = 0; sample < Samples; sample++)
            {
                // The middle of each of the row's slices, so no sample lands exactly on a
                // vertex — where an edge would be counted twice or not at all.
                float y = y0 + row + ((sample + 0.5f) / Samples);

                crossings.Clear();

                foreach ((Vector2 from, Vector2 to) in edges)
                {
                    if (from.Y == to.Y)
                    {
                        continue;
                    }

                    float lower = MathF.Min(from.Y, to.Y);
                    float upper = MathF.Max(from.Y, to.Y);

                    if (y < lower || y >= upper)
                    {
                        continue;
                    }

                    float t = (y - from.Y) / (to.Y - from.Y);

                    crossings.Add((from.X + (t * (to.X - from.X)), to.Y > from.Y ? 1 : -1));
                }

                if (crossings.Count < 2)
                {
                    continue;
                }

                crossings.Sort((a, b) => a.X.CompareTo(b.X));

                int winding = 0;
                float began = 0;

                foreach ((float at, int direction) in crossings)
                {
                    if (winding == 0)
                    {
                        began = at;
                    }

                    winding += direction;

                    // The nonzero rule: inside until the windings cancel. It is what makes
                    // the counter of an 'o' a hole rather than a second fill.
                    if (winding == 0)
                    {
                        Span(accumulator, began - x0, at - x0, 1f / Samples);
                    }
                }
            }

            for (int x = 0; x < width; x++)
            {
                coverage[(row * width) + x] =
                    (byte)Math.Clamp((int)((accumulator[x] * 255f) + 0.5f), 0, 255);
            }
        }

        return new RasterGlyph(width, height, x0, -y0, coverage);
    }

    /// <summary>Adds a horizontal run to a row, with its ends to the fraction.</summary>
    private static void Span(float[] row, float from, float to, float weight)
    {
        if (to <= from)
        {
            return;
        }

        from = Math.Clamp(from, 0, row.Length);
        to = Math.Clamp(to, 0, row.Length);

        int first = (int)MathF.Floor(from);
        int last = (int)MathF.Ceiling(to) - 1;

        for (int x = first; x <= last && x < row.Length; x++)
        {
            if (x < 0)
            {
                continue;
            }

            // How much of this pixel the run covers: the overlap of [from, to] with the
            // pixel's own [x, x+1].
            float covered = MathF.Min(to, x + 1) - MathF.Max(from, x);

            if (covered > 0)
            {
                row[x] += covered * weight;
            }
        }
    }

    /// <summary>Turns the contours into straight edges at the drawn size.</summary>
    /// <remarks>
    /// TrueType curves are quadratic and are written with the on-curve points between them
    /// left out wherever they are the midpoint of two controls — so two control points in
    /// a row imply a point halfway between them. Missing that draws spikes.
    /// </remarks>
    private static List<(Vector2 From, Vector2 To)> Flatten(GlyphOutline outline, float scale)
    {
        List<(Vector2, Vector2)> edges = [];
        int start = 0;

        foreach (int end in outline.Ends)
        {
            if (end < start || end >= outline.Points.Count)
            {
                start = end + 1;
                continue;
            }

            int count = end - start + 1;

            if (count < 2)
            {
                start = end + 1;
                continue;
            }

            // Y is turned over here and nowhere else: a font measures up from the baseline
            // and a bitmap measures down from its top.
            Vector2 At(int i)
            {
                GlyphPoint p = outline.Points[start + (((i % count) + count) % count)];
                return new Vector2(p.X * scale, -p.Y * scale);
            }

            bool On(int i) =>
                outline.Points[start + (((i % count) + count) % count)].OnCurve;

            // Where the contour begins: the first point that is on the curve, or the
            // midpoint of the first pair when the whole contour is control points — which
            // is what a perfect circle looks like in this format.
            int first = -1;

            for (int i = 0; i < count; i++)
            {
                if (On(i))
                {
                    first = i;
                    break;
                }
            }

            Vector2 began = first >= 0 ? At(first) : (At(0) + At(1)) / 2f;
            Vector2 pen = began;

            int from = first >= 0 ? first : 0;

            for (int step = 1; step <= count; step++)
            {
                int i = from + step;

                if (On(i))
                {
                    edges.Add((pen, At(i)));
                    pen = At(i);
                    continue;
                }

                // A control point. The next on-curve point is either the one after it or
                // the midpoint between the two controls.
                Vector2 control = At(i);
                Vector2 next = On(i + 1) ? At(i + 1) : (control + At(i + 1)) / 2f;

                Curve(edges, pen, control, next);
                pen = next;

                if (On(i + 1))
                {
                    step++;
                }
            }

            if (pen != began)
            {
                edges.Add((pen, began));
            }

            start = end + 1;
        }

        return edges;
    }

    /// <summary>Breaks one quadratic curve into straight pieces.</summary>
    private static void Curve(
        List<(Vector2, Vector2)> edges, Vector2 from, Vector2 control, Vector2 to)
    {
        // How far the curve bulges from the straight line across it decides how many
        // pieces it needs. A nearly straight curve costs one.
        float bulge = Vector2.Distance((from + to) / 2f, control);
        int steps = Math.Clamp((int)MathF.Ceiling(MathF.Sqrt(bulge / Flatness)), 1, 64);

        Vector2 pen = from;

        for (int i = 1; i <= steps; i++)
        {
            float t = i / (float)steps;
            float u = 1 - t;

            Vector2 at = (u * u * from) + (2 * u * t * control) + (t * t * to);

            edges.Add((pen, at));
            pen = at;
        }
    }
}
