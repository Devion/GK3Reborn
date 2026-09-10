using GK3Reborn.Formats.Bitmaps;

namespace GK3Reborn.Platform;

/// <summary>One pointer picture at one size, ready to be handed to the platform.</summary>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
/// <param name="Pixels">8-bit RGBA, straight alpha, top row first.</param>
/// <param name="HotspotX">The pixel the click lands on, from the left.</param>
/// <param name="HotspotY">And from the top.</param>
public sealed record PointerImage(int Width, int Height, byte[] Pixels, int HotspotX, int HotspotY);

/// <summary>
/// The pictures the pointer is drawn as. They are the port's own and the assembly
/// carries them, so a shipped game has a pointer whatever content it finds and nothing
/// in a pack can replace them. The sources are 512 square so that they downsample
/// cleanly to any size a display or a player asks for. See docs/interface-cursors.md.
/// </summary>
public static class PointerArt
{
    /// <summary>
    /// How tall the pointer is at scale one, as a fraction of the framebuffer's height. The
    /// interface is sized against the framebuffer rather than the monitor's DPI, and the
    /// pointer follows it so the two stay in proportion at every resolution.
    /// </summary>
    public const float HeightFraction = 1f / 24f;

    /// <summary>The smallest a pointer is ever made, in pixels.</summary>
    public const int Smallest = 16;

    /// <summary>And the largest.</summary>
    public const int Largest = 256;

    /// <summary>The side every source is drawn at.</summary>
    public const int SourceSide = 512;

    /// <summary>
    /// Where each shape's picture is, and where in it the click lands, as fractions of
    /// the picture. The arrow's tip is its corner; the hand points with a fingertip, the
    /// glass with the middle of its lens, the bubble with its tail and the other hand
    /// with the knob it is holding.
    /// </summary>
    private static readonly Dictionary<PointerShape, (string File, float X, float Y)> Shapes = new()
    {
        [PointerShape.Default] = ("Default", 0f, 0f),
        [PointerShape.Exit] = ("Exit", 0.79f, 0.75f),
        [PointerShape.Look] = ("Look", 0.37f, 0.37f),
        [PointerShape.Interact] = ("Interact", 0.50f, 0.03f),
        [PointerShape.Talk] = ("Talk", 0.23f, 0.82f),
    };

    /// <summary>The decoded sources, each read once and only when first wanted.</summary>
    private static readonly Dictionary<PointerShape, Lazy<DecodedImage?>> Sources =
        Shapes.ToDictionary(
            pair => pair.Key,
            pair => new Lazy<DecodedImage?>(() => Read(pair.Value.File), isThreadSafe: true));

    /// <summary>Where the click lands in a shape, as fractions of its picture.</summary>
    /// <param name="shape">The shape.</param>
    /// <returns>Across and down, each between nought and one.</returns>
    public static (float X, float Y) HotspotOf(PointerShape shape) =>
        Shapes.TryGetValue(shape, out (string File, float X, float Y) known)
            ? (known.X, known.Y)
            : (0f, 0f);

    /// <summary>How big a pointer should be on a framebuffer.</summary>
    /// <param name="framebufferHeight">The framebuffer's height in pixels.</param>
    /// <param name="scale">The player's multiplier, one for the usual size.</param>
    /// <returns>The side of the square, in pixels.</returns>
    public static int SizeFor(int framebufferHeight, float scale)
    {
        float wanted = framebufferHeight * HeightFraction * (float.IsFinite(scale) ? scale : 1f);

        return Math.Clamp((int)MathF.Round(wanted), Smallest, Largest);
    }

    /// <summary>Decodes every source, for calling off the main thread before any is wanted.</summary>
    public static void Warm()
    {
        foreach (Lazy<DecodedImage?> source in Sources.Values)
        {
            _ = source.Value;
        }
    }

    /// <summary>A shape, at a size.</summary>
    /// <param name="shape">Which one.</param>
    /// <param name="size">The side of the square, in pixels.</param>
    /// <returns>The picture, or null when the assembly does not carry one.</returns>
    public static PointerImage? Load(PointerShape shape, int size)
    {
        if (!Sources.TryGetValue(shape, out Lazy<DecodedImage?>? source) || source.Value is not { } picture)
        {
            return null;
        }

        (float x, float y) = HotspotOf(shape);

        return Resample(picture, Math.Clamp(size, 1, Largest), x, y);
    }

    /// <summary>
    /// Shrinks a picture to a square, averaging the source under each new pixel.
    /// </summary>
    /// <param name="source">The picture. Need not be square.</param>
    /// <param name="size">The side of the result.</param>
    /// <param name="hotspotX">Where the click lands, as a fraction across.</param>
    /// <param name="hotspotY">And down.</param>
    /// <returns>The picture at the new size.</returns>
    public static PointerImage Resample(DecodedImage source, int size, float hotspotX, float hotspotY)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);

        byte[] pixels = new byte[size * size * 4];
        float acrossRatio = (float)source.Width / size;
        float downRatio = (float)source.Height / size;

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                Average(
                    source,
                    x * acrossRatio,
                    (x + 1) * acrossRatio,
                    y * downRatio,
                    (y + 1) * downRatio,
                    pixels.AsSpan(((y * size) + x) * 4, 4));
            }
        }

        return new PointerImage(
            size,
            size,
            pixels,
            Math.Clamp((int)MathF.Round(hotspotX * (size - 1)), 0, size - 1),
            Math.Clamp((int)MathF.Round(hotspotY * (size - 1)), 0, size - 1));
    }

    /// <summary>
    /// The colour under a rectangle of the source, weighted by how much of each source
    /// pixel the rectangle covers. Colour is averaged with alpha applied, so the invisible
    /// colour a transparent pixel happens to hold cannot bleed into an edge.
    /// </summary>
    private static void Average(
        DecodedImage source, float left, float right, float top, float bottom, Span<byte> into)
    {
        double r = 0, g = 0, b = 0, a = 0, covered = 0;

        int firstRow = Math.Max(0, (int)MathF.Floor(top));
        int lastRow = Math.Min(source.Height - 1, (int)MathF.Ceiling(bottom) - 1);
        int firstColumn = Math.Max(0, (int)MathF.Floor(left));
        int lastColumn = Math.Min(source.Width - 1, (int)MathF.Ceiling(right) - 1);

        for (int row = firstRow; row <= lastRow; row++)
        {
            float rowWeight = MathF.Min(bottom, row + 1) - MathF.Max(top, row);

            for (int column = firstColumn; column <= lastColumn; column++)
            {
                float weight = rowWeight * (MathF.Min(right, column + 1) - MathF.Max(left, column));
                int at = ((row * source.Width) + column) * 4;
                float alpha = source.Pixels[at + 3] / 255f;

                r += source.Pixels[at] * alpha * weight;
                g += source.Pixels[at + 1] * alpha * weight;
                b += source.Pixels[at + 2] * alpha * weight;
                a += alpha * weight;
                covered += weight;
            }
        }

        if (a <= 0 || covered <= 0)
        {
            into.Clear();

            return;
        }

        into[0] = (byte)Math.Clamp(Math.Round(r / a), 0, 255);
        into[1] = (byte)Math.Clamp(Math.Round(g / a), 0, 255);
        into[2] = (byte)Math.Clamp(Math.Round(b / a), 0, 255);
        into[3] = (byte)Math.Clamp(Math.Round(a / covered * 255), 0, 255);
    }

    /// <summary>Reads one source out of the assembly.</summary>
    private static DecodedImage? Read(string file)
    {
        using Stream? carried = typeof(PointerArt).Assembly.GetManifestResourceStream(
            $"GK3Reborn.Assets.Cursors.{file}.png");

        if (carried is null)
        {
            return null;
        }

        using var copy = new MemoryStream();
        carried.CopyTo(copy);

        // A picture that will not decode is a build fault, not a reason the game cannot
        // start: the shape is left to the platform's own arrow, which Load reports as null.
        try
        {
            DecodedImage decoded = PngReader.Decode(copy.ToArray(), $"{file}.png");

            return decoded.Width > 0 && decoded.Height > 0 ? decoded : null;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return null;
        }
    }
}
