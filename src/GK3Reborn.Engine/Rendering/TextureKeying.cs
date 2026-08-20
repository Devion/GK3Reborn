using GK3Reborn.Formats.Bitmaps;

namespace GK3Reborn.Rendering;

/// <summary>
/// Turns GK3's magenta colour key into an alpha channel that survives filtering.
/// </summary>
/// <remarks>
/// <para>
/// The original discards magenta texels in the fragment shader and never builds mips, so
/// the key colour is either sampled exactly or not at all. A modern renderer does both:
/// linear filtering between a magenta texel and its neighbour produces a colour that is
/// neither, and mip generation spreads it further. The result is the magenta fringe that
/// appears around window mullions and railings once mips are switched on.
/// </para>
/// <para>
/// The fix is to remove the colour before it can be blended: keyed texels get zero alpha,
/// and their colour is replaced by the nearest opaque colour so that filtering pulls in
/// something plausible instead. The shader then tests alpha, which blurs gracefully,
/// rather than testing for a colour, which does not.
/// </para>
/// </remarks>
public static class TextureKeying
{
    private const int KeyTolerance = 24;

    /// <summary>Whether an image has anything the colour key would remove.</summary>
    /// <param name="image">The decoded image.</param>
    /// <returns>True when at least one texel is magenta or already transparent.</returns>
    /// <remarks>
    /// Asked of the <em>original</em>, so that the loader can decide whether a texture may
    /// take the block-compressed path. Blocks cannot be keyed, and a keyed texture that
    /// skips this comes out with GK3's magenta painted where its holes should be.
    /// </remarks>
    public static bool NeedsKey(DecodedImage image)
    {
        ArgumentNullException.ThrowIfNull(image.Pixels);

        byte[] pixels = image.Pixels;

        for (int at = 0; at + 3 < pixels.Length; at += 4)
        {
            if (IsKey(pixels[at], pixels[at + 1], pixels[at + 2]) || pixels[at + 3] < 128)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Applies the colour key, if the image uses one.</summary>
    /// <param name="image">The decoded image.</param>
    /// <returns>The image with keyed texels made transparent, or the original.</returns>
    public static DecodedImage Apply(DecodedImage image)
    {
        ArgumentNullException.ThrowIfNull(image.Pixels);

        byte[] pixels = image.Pixels;
        bool[] keyed = new bool[image.Width * image.Height];
        bool any = false;

        for (int i = 0; i < keyed.Length; i++)
        {
            int at = i * 4;
            if (IsKey(pixels[at], pixels[at + 1], pixels[at + 2]) || pixels[at + 3] < 128)
            {
                keyed[i] = true;
                any = true;
            }
        }

        if (!any)
        {
            return image;
        }

        byte[] output = (byte[])pixels.Clone();

        for (int i = 0; i < keyed.Length; i++)
        {
            if (keyed[i])
            {
                output[(i * 4) + 3] = 0;
            }
        }

        Bleed(output, keyed, image.Width, image.Height);

        return image with { Pixels = output, HasAlpha = true };
    }

    private static bool IsKey(byte r, byte g, byte b) =>
        r >= 255 - KeyTolerance && b >= 255 - KeyTolerance && g <= KeyTolerance;

    /// <summary>
    /// Spreads opaque colour into the transparent texels, a ring at a time.
    /// </summary>
    /// <remarks>
    /// Four passes is enough for the fringe: filtering only ever reaches a texel or two,
    /// and the coarsest mips of a small texture are a flat average anyway. Running it to
    /// completion would fill entire transparent regions for no visible benefit.
    /// </remarks>
    private static void Bleed(byte[] pixels, bool[] keyed, int width, int height)
    {
        bool[] transparent = (bool[])keyed.Clone();

        for (int pass = 0; pass < 4; pass++)
        {
            bool changed = false;

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int index = (y * width) + x;
                    if (!transparent[index])
                    {
                        continue;
                    }

                    int r = 0, g = 0, b = 0, count = 0;

                    for (int dy = -1; dy <= 1; dy++)
                    {
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = x + dx;
                            int ny = y + dy;

                            if (nx < 0 || ny < 0 || nx >= width || ny >= height)
                            {
                                continue;
                            }

                            int neighbour = (ny * width) + nx;
                            if (transparent[neighbour])
                            {
                                continue;
                            }

                            int at = neighbour * 4;
                            r += pixels[at];
                            g += pixels[at + 1];
                            b += pixels[at + 2];
                            count++;
                        }
                    }

                    if (count == 0)
                    {
                        continue;
                    }

                    int target = index * 4;
                    pixels[target] = (byte)(r / count);
                    pixels[target + 1] = (byte)(g / count);
                    pixels[target + 2] = (byte)(b / count);
                    changed = true;
                }
            }

            if (!changed)
            {
                break;
            }

            // Texels filled this pass become sources for the next, so the colour spreads
            // outward rather than every ring averaging the same few originals.
            for (int i = 0; i < transparent.Length; i++)
            {
                if (transparent[i] && pixels[(i * 4) + 3] == 0)
                {
                    transparent[i] = !IsFilled(pixels, i);
                }
            }
        }
    }

    private static bool IsFilled(byte[] pixels, int index)
    {
        int at = index * 4;
        return !IsKey(pixels[at], pixels[at + 1], pixels[at + 2]);
    }
}
