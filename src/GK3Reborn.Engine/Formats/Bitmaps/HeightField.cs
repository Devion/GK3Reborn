// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

namespace GK3Reborn.Formats.Bitmaps;

/// <summary>
/// A height map as a number the CPU can ask for, rather than a picture for the device.
/// </summary>
/// <remarks>
/// <para>
/// Everything else a height field is for happens in a shader, which reads it from a sampler
/// and needs nothing here. Displacement is the exception: it moves vertices, and vertices
/// are built once at load on this side of the seam. So the same map has to be readable
/// twice, in two forms.
/// </para>
/// <para>
/// <b>Mid grey is the modelled surface</b>, which is the convention the whole pipeline
/// shares: <c>PbrLab</c> integrates a normal map into a field and high-passes it back to a
/// half, the shader subtracts a half before offsetting, and <see cref="At"/> returns the
/// same signed quantity — minus a half to plus a half of the field's full depth.
/// </para>
/// <para>
/// <b>It is deliberately small.</b> The shipped maps are 512 pixels and the workspace's are
/// 2,048, and displacement samples at whatever spacing its triangle budget affords — six
/// units on a street, which is a dozen texels apart at best. Reading a level low in the
/// chain costs a fraction of the memory and answers the same question, so
/// <see cref="From(CompressedImage, int)"/> takes the smallest level that still has more
/// texels than the geometry can use.
/// </para>
/// </remarks>
public sealed class HeightField
{
    private readonly float[] _values;

    private HeightField(int width, int height, float[] values)
    {
        Width = width;
        Height = height;
        _values = values;
    }

    /// <summary>Width in texels.</summary>
    public int Width { get; }

    /// <summary>Height in texels.</summary>
    public int Height { get; }

    /// <summary>
    /// The field at a texture coordinate, as a signed fraction of its full depth.
    /// </summary>
    /// <param name="u">Horizontal coordinate; wraps.</param>
    /// <param name="v">Vertical coordinate; wraps.</param>
    /// <returns>Minus a half to plus a half, zero being the modelled surface.</returns>
    /// <remarks>
    /// Bilinear and wrapping. Wrapping because a floor tiles its texture dozens of times
    /// across a street and the sampler that draws it repeats; a clamped read would flatten
    /// the relief along every tile's far edge into a smear of its last row.
    /// </remarks>
    public float At(float u, float v)
    {
        float x = (Wrap(u) * Width) - 0.5f;
        float y = (Wrap(v) * Height) - 0.5f;

        int x0 = (int)MathF.Floor(x);
        int y0 = (int)MathF.Floor(y);

        float fx = x - x0;
        float fy = y - y0;

        int left = Index(x0, Width);
        int right = Index(x0 + 1, Width);
        int top = Index(y0, Height) * Width;
        int bottom = Index(y0 + 1, Height) * Width;

        float upper = Lerp(_values[top + left], _values[top + right], fx);
        float lower = Lerp(_values[bottom + left], _values[bottom + right], fx);

        return Lerp(upper, lower, fy) - 0.5f;
    }

    /// <summary>
    /// The field averaged over a square, as a signed fraction of its full depth.
    /// </summary>
    /// <param name="u">Horizontal coordinate of the centre; wraps.</param>
    /// <param name="v">Vertical coordinate of the centre; wraps.</param>
    /// <param name="span">Width of the square, in texture coordinates.</param>
    /// <returns>Minus a half to plus a half, zero being the modelled surface.</returns>
    /// <remarks>
    /// What displacement wants rather than <see cref="At"/>. A vertex stands for a whole
    /// cell of the surface, and reading one texel at its centre makes the geometry a point
    /// sample of a field with detail far finer than the cell: the same street tessellated
    /// twice at slightly different densities comes out a different shape, and that shape is
    /// noise as often as it is cobbles. Averaging over the cell takes the part of the field
    /// the geometry can carry and leaves the rest to the parallax and the normal map, which
    /// is the division of labour those two were always meant to have.
    /// </remarks>
    public float Over(float u, float v, float span)
    {
        // Enough samples to cover the cell at about a texel each, and never so many that a
        // coarse tessellation over a large texture turns into a full-image average.
        int taps = Math.Clamp((int)MathF.Ceiling(span * Width), 1, 8);

        if (taps <= 1)
        {
            return At(u, v);
        }

        float total = 0f;
        float step = span / taps;
        float origin = -(span * 0.5f) + (step * 0.5f);

        for (int y = 0; y < taps; y++)
        {
            for (int x = 0; x < taps; x++)
            {
                total += At(u + origin + (x * step), v + origin + (y * step));
            }
        }

        return total / (taps * taps);
    }

    /// <summary>Reads a decoded map's red channel, shrinking it to about a wanted size.</summary>
    /// <param name="image">The decoded map. Grey, so only red is read.</param>
    /// <param name="wanted">The largest extent worth keeping, in texels.</param>
    /// <returns>The field.</returns>
    public static HeightField From(DecodedImage image, int wanted = 512)
    {
        ArgumentNullException.ThrowIfNull(image.Pixels);
        ArgumentOutOfRangeException.ThrowIfLessThan(wanted, 1);

        // Box filtered down by whole factors, which is what a mip chain would have given
        // had the source arrived with one. A 2,048-pixel workspace map is 16 MB as floats
        // and answers no question a 512-pixel one does not.
        int factor = 1;

        while (image.Width / (factor * 2) >= wanted && image.Height / (factor * 2) >= wanted)
        {
            factor *= 2;
        }

        int width = Math.Max(1, image.Width / factor);
        int height = Math.Max(1, image.Height / factor);
        var values = new float[width * height];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int total = 0;

                for (int j = 0; j < factor; j++)
                {
                    int row = Math.Min(image.Height - 1, (y * factor) + j) * image.Width;

                    for (int i = 0; i < factor; i++)
                    {
                        int column = Math.Min(image.Width - 1, (x * factor) + i);

                        total += image.Pixels[(row + column) * 4];
                    }
                }

                values[(y * width) + x] = total / (255f * factor * factor);
            }
        }

        return new HeightField(width, height, values);
    }

    /// <summary>Decodes a block-compressed map, taking a level near a wanted size.</summary>
    /// <param name="image">The compressed levels.</param>
    /// <param name="wanted">The largest extent worth decoding, in texels.</param>
    /// <returns>The field, or null if it is in a format this cannot read.</returns>
    /// <remarks>
    /// BC4 only, which is what the content pipeline compresses height to and the one block
    /// format that is a single channel. Eight bytes a block: two endpoints and sixteen
    /// three-bit indices. The six-endpoint form interpolates between them, and the
    /// eight-endpoint form reserves two of its codes for the ends of the range — which is
    /// the part of BC4 that gets written wrong.
    /// </remarks>
    public static HeightField? From(CompressedImage image, int wanted = 512)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(wanted, 1);

        if (image.Format != BlockFormat.Bc4Unorm)
        {
            return null;
        }

        // The smallest level that still has at least as many texels as asked for, and the
        // last one if none does.
        int level = 0;

        while (level + 1 < image.Mips)
        {
            (_, _, int nextWidth, int nextHeight) = image.Level(level + 1);

            if (nextWidth < wanted || nextHeight < wanted)
            {
                break;
            }

            level++;
        }

        (int offset, int length, int width, int height) = image.Level(level);

        if (offset + length > image.Blocks.Length)
        {
            return null;
        }

        ReadOnlySpan<byte> blocks = image.Blocks.Span.Slice(offset, length);

        var values = new float[width * height];
        int across = CompressedImage.Blocks4(width);
        int down = CompressedImage.Blocks4(height);

        Span<float> palette = stackalloc float[8];

        for (int block = 0; block < across * down; block++)
        {
            ReadOnlySpan<byte> bytes = blocks.Slice(block * 8, 8);

            float first = bytes[0] / 255f;
            float second = bytes[1] / 255f;

            palette[0] = first;
            palette[1] = second;

            if (bytes[0] > bytes[1])
            {
                for (int i = 1; i < 7; i++)
                {
                    palette[i + 1] = (((7 - i) * first) + (i * second)) / 7f;
                }
            }
            else
            {
                for (int i = 1; i < 5; i++)
                {
                    palette[i + 1] = (((5 - i) * first) + (i * second)) / 5f;
                }

                palette[6] = 0f;
                palette[7] = 1f;
            }

            // Forty-eight bits of three-bit indices, lowest first.
            ulong indices = bytes[2]
                            | ((ulong)bytes[3] << 8)
                            | ((ulong)bytes[4] << 16)
                            | ((ulong)bytes[5] << 24)
                            | ((ulong)bytes[6] << 32)
                            | ((ulong)bytes[7] << 40);

            int originX = (block % across) * 4;
            int originY = (block / across) * 4;

            for (int texel = 0; texel < 16; texel++)
            {
                int x = originX + (texel % 4);
                int y = originY + (texel / 4);

                // A block runs past a texture whose extent is not a multiple of four. Those
                // texels exist in the block and nowhere in the image.
                if (x >= width || y >= height)
                {
                    continue;
                }

                values[(y * width) + x] = palette[(int)((indices >> (texel * 3)) & 7)];
            }
        }

        return new HeightField(width, height, values);
    }

    private static float Lerp(float a, float b, float t) => a + ((b - a) * t);

    private static float Wrap(float coordinate)
    {
        float wrapped = coordinate - MathF.Floor(coordinate);

        // The floor of a large negative float can round to the value itself, which leaves a
        // coordinate of exactly one rather than of zero.
        return wrapped is >= 1f or < 0f ? 0f : wrapped;
    }

    private static int Index(int coordinate, int extent)
    {
        int wrapped = coordinate % extent;

        return wrapped < 0 ? wrapped + extent : wrapped;
    }
}
