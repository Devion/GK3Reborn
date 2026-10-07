using GK3Reborn.Formats.Bitmaps;

namespace GK3Reborn.Rendering;

/// <summary>Reduces resident texture storage before any GPU allocation.</summary>
public static class TextureResolution
{
    /// <summary>RGBA8 payload including each resident mip, excluding allocation alignment.</summary>
    public static long RgbaBytes(int width, int height, bool mipmaps)
    {
        long bytes = 0;
        while (true)
        {
            bytes += (long)width * height * 4;
            if (!mipmaps || (width == 1 && height == 1)) { return bytes; }
            width = Math.Max(1, width / 2);
            height = Math.Max(1, height / 2);
        }
    }

    public static int ForMemory(ulong bytes) => bytes switch
    {
        > 0 and <= 2UL * 1024 * 1024 * 1024 => 512,
        > 0 and <= 4UL * 1024 * 1024 * 1024 => 1024,
        _ => int.MaxValue,
    };

    public static CompressedImage Limit(CompressedImage image, int maximum)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximum, 1);
        int level = 0;
        while (level + 1 < image.Mips)
        {
            var extent = image.Level(level);
            if (Math.Max(extent.Width, extent.Height) <= maximum)
            {
                break;
            }
            level++;
        }
        var chosen = image.Level(level);
        return image with
        {
            Width = chosen.Width,
            Height = chosen.Height,
            Mips = image.Mips - level,
            Blocks = image.Blocks[chosen.Offset..]
        };
    }

    public static DecodedImage Limit(DecodedImage image, int maximum)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximum, 1);
        while (Math.Max(image.Width, image.Height) > maximum)
        {
            int width = Math.Max(1, image.Width / 2), height = Math.Max(1, image.Height / 2);
            byte[] pixels = new byte[checked(width * height * 4)];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    for (int channel = 0; channel < 4; channel++)
                    {
                        int total = 0, count = 0;
                        for (int sy = y * image.Height / height; sy < (y + 1) * image.Height / height; sy++)
                        {
                            for (int sx = x * image.Width / width; sx < (x + 1) * image.Width / width; sx++)
                            {
                                total += image.Pixels[((sy * image.Width + sx) * 4) + channel];
                                count++;
                            }
                        }
                        pixels[((y * width + x) * 4) + channel] = (byte)((total + count / 2) / count);
                    }
                }
            }
            image = image with { Width = width, Height = height, Pixels = pixels };
        }
        return image;
    }
}
