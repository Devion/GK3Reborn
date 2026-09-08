// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Numerics;
using GK3Reborn.Formats.Bitmaps;

namespace GK3Reborn.Rendering;

/// <summary>One triangle of a card that has been given a thickness.</summary>
/// <param name="A">First corner.</param>
/// <param name="B">Second corner.</param>
/// <param name="C">Third corner.</param>
public readonly record struct CardTriangle(CurvedCorner A, CurvedCorner B, CurvedCorner C);

/// <summary>What one card came out as.</summary>
/// <param name="Triangles">The shell: a front face, a back face, and a rim joining them.</param>
/// <param name="Thickness">How far apart the two faces were put, in scene units.</param>
/// <param name="RimQuads">How many rim quads the silhouette earned.</param>
/// <param name="Occluders">
/// The same silhouette as opaque triangles for a shadow ray, three vertices to a triangle.
/// </param>
public sealed record ThickCard(
    IReadOnlyList<CardTriangle> Triangles,
    float Thickness,
    int RimQuads,
    IReadOnlyList<Vector3> Occluders);

/// <summary>
/// What a keyed texture's holes say about the shape drawn on it.
/// </summary>
public sealed class CutoutMask
{
    /// <summary>Texels across.</summary>
    public required int Width { get; init; }

    /// <summary>Texels down.</summary>
    public required int Height { get; init; }

    /// <summary>Which texels are drawn, row-major, false where the key shows through.</summary>
    public required bool[] Opaque { get; init; }

    /// <summary>How wide the bars are, in texels.</summary>
    public required float FeatureTexels { get; init; }

    /// <summary>What proportion of the texture the colour key removes.</summary>
    public required float KeyedFraction { get; init; }

    /// <summary>
    /// A texture keyed over less than this, or more, is not a lattice.
    /// </summary>
    private const float LeastKeyed = 0.03f;

    /// <summary>See <see cref="LeastKeyed"/>.</summary>
    private const float MostKeyed = 0.97f;

    /// <summary>
    /// Wider than this share of the texture and the drawing is a panel rather than a
    /// lattice of bars.
    /// </summary>
    public const float WidestFeatureShare = 0.35f;

    /// <summary>
    /// The longest side this is measured at, in texels; larger masks are halved down to it.
    /// </summary>
    public const int ReferenceTexels = 256;

    /// <summary>
    /// The coarsest a bar may be left, in texels, as the mask is reduced towards the
    /// resolution its outline was drawn at.
    /// </summary>
    public const float CoarsestBar = 4f;

    /// <summary>
    /// Measures a texture, if there is a lattice of bars drawn on it.
    /// </summary>
    /// <param name="image">The decoded texture, <em>after</em> the colour key was applied.</param>
    /// <returns>The mask, or null when this texture is nobody's railing.</returns>
    public static CutoutMask? Measure(DecodedImage image)
    {
        ArgumentNullException.ThrowIfNull(image.Pixels);

        int width = image.Width;
        int height = image.Height;

        if (width < 4 || height < 4)
        {
            return null;
        }

        byte[] pixels = image.Pixels;
        bool[] opaque = new bool[width * height];
        int keyed = 0;

        for (int i = 0; i < opaque.Length; i++)
        {
            int at = i * 4;

            bool hole = pixels[at + 3] < 128 ||
                        (pixels[at] >= 231 && pixels[at + 2] >= 231 && pixels[at + 1] <= 24);

            opaque[i] = !hole;

            if (hole)
            {
                keyed++;
            }
        }

        float fraction = (float)keyed / opaque.Length;

        if (fraction is <= LeastKeyed or >= MostKeyed)
        {
            return null;
        }

        while (Math.Max(width, height) > ReferenceTexels && width >= 8 && height >= 8)
        {
            opaque = Halve(opaque, ref width, ref height);
        }

        float feature = Feature(Chamfer(opaque, width, height), opaque, width, height);

        // Down to the resolution the outline was actually drawn at, which is found by
        // halving while the bars survive it rather than by being told. See CoarsestBar.
        while (feature / 2f >= CoarsestBar && Math.Min(width, height) >= 32)
        {
            int wide = width;
            int tall = height;
            bool[] smaller = Halve(opaque, ref wide, ref tall);

            opaque = smaller;
            width = wide;
            height = tall;
            feature = Feature(Chamfer(opaque, width, height), opaque, width, height);
        }

        if (feature <= 0f || feature > WidestFeatureShare * Math.Min(width, height))
        {
            return null;
        }

        return new CutoutMask
        {
            Width = width,
            Height = height,
            Opaque = opaque,
            FeatureTexels = feature,
            KeyedFraction = fraction,
        };
    }

    /// <summary>
    /// Halves the mask, keeping a texel that most of its four agreed was drawn.
    /// </summary>
    private static bool[] Halve(bool[] opaque, ref int width, ref int height)
    {
        int half = width / 2;
        int down = height / 2;
        bool[] smaller = new bool[half * down];

        for (int y = 0; y < down; y++)
        {
            for (int x = 0; x < half; x++)
            {
                int at = (y * 2 * width) + (x * 2);

                int drawn = (opaque[at] ? 1 : 0) +
                            (opaque[at + 1] ? 1 : 0) +
                            (opaque[at + width] ? 1 : 0) +
                            (opaque[at + width + 1] ? 1 : 0);

                smaller[(y * half) + x] = drawn >= 2;
            }
        }

        width = half;
        height = down;

        return smaller;
    }

    /// <summary>Whether a texel is drawn. Outside the texture, nothing is.</summary>
    /// <param name="x">Column.</param>
    /// <param name="y">Row.</param>
    /// <returns>True where the texture is painted rather than keyed away.</returns>
    public bool At(int x, int y) =>
        x >= 0 && y >= 0 && x < Width && y < Height && Opaque[(y * Width) + x];

    /// <summary>Whether the texture is painted at a texture coordinate.</summary>
    /// <param name="uv">Where on the texture, in the usual 0-1 with V down.</param>
    /// <returns>True where the drawing is, false where the key shows through.</returns>
    public bool Covers(System.Numerics.Vector2 uv) =>
        At(Wrapped(uv.X, Width), Wrapped(uv.Y, Height));

    /// <summary>One axis of a texture coordinate, as a texel of a tiling texture.</summary>
    private static int Wrapped(float coordinate, int texels)
    {
        int at = (int)MathF.Floor(coordinate * texels) % texels;

        return at < 0 ? at + texels : at;
    }

    /// <summary>
    /// Every hole a keyed texture has, whatever shape they are.
    /// </summary>
    /// <param name="image">The decoded texture, <em>after</em> the colour key was applied.</param>
    /// <returns>The mask, or null when the drawing has no holes to speak of.</returns>
    public static CutoutMask? Silhouette(DecodedImage image)
    {
        ArgumentNullException.ThrowIfNull(image.Pixels);

        int width = image.Width;
        int height = image.Height;

        if (width < 2 || height < 2)
        {
            return null;
        }

        byte[] pixels = image.Pixels;
        bool[] opaque = new bool[width * height];
        int keyed = 0;

        for (int i = 0; i < opaque.Length; i++)
        {
            int at = i * 4;

            // The same test Measure makes, and for the same reason: alpha is authoritative
            // because TextureKeying has run, and the magenta backstop catches whatever the
            // conversion missed.
            bool hole = pixels[at + 3] < 128 ||
                        (pixels[at] >= 231 && pixels[at + 2] >= 231 && pixels[at + 1] <= 24);

            opaque[i] = !hole;

            if (hole)
            {
                keyed++;
            }
        }

        // Nothing keyed means the texture covers its whole surface, which is what a caller
        // assumes when it has no mask — so saying so costs it a lookup and tells it nothing.
        if (keyed == 0)
        {
            return null;
        }

        while (Math.Max(width, height) > ReferenceTexels && width >= 8 && height >= 8)
        {
            opaque = Halve(opaque, ref width, ref height);
        }

        return new CutoutMask
        {
            Width = width,
            Height = height,
            Opaque = opaque,
            FeatureTexels = 0f,
            KeyedFraction = (float)keyed / (image.Width * image.Height),
        };
    }

    /// <summary>
    /// How far each drawn texel is from the nearest hole, in texels.
    /// </summary>
    private static float[] Chamfer(bool[] opaque, int width, int height)
    {
        const int Near = 3;
        const int Diagonal = 4;
        const int Far = int.MaxValue / 4;

        int[] d = new int[opaque.Length];

        for (int i = 0; i < d.Length; i++)
        {
            d[i] = opaque[i] ? Far : 0;
        }

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int at = (y * width) + x;

                if (d[at] == 0)
                {
                    continue;
                }

                int best = d[at];

                if (y > 0)
                {
                    if (x > 0)
                    {
                        best = Math.Min(best, d[at - width - 1] + Diagonal);
                    }

                    best = Math.Min(best, d[at - width] + Near);

                    if (x < width - 1)
                    {
                        best = Math.Min(best, d[at - width + 1] + Diagonal);
                    }
                }

                if (x > 0)
                {
                    best = Math.Min(best, d[at - 1] + Near);
                }

                if (y == 0 || x == 0)
                {
                    best = Math.Min(best, Near);
                }

                d[at] = best;
            }
        }

        for (int y = height - 1; y >= 0; y--)
        {
            for (int x = width - 1; x >= 0; x--)
            {
                int at = (y * width) + x;

                if (d[at] == 0)
                {
                    continue;
                }

                int best = d[at];

                if (y < height - 1)
                {
                    if (x < width - 1)
                    {
                        best = Math.Min(best, d[at + width + 1] + Diagonal);
                    }

                    best = Math.Min(best, d[at + width] + Near);

                    if (x > 0)
                    {
                        best = Math.Min(best, d[at + width - 1] + Diagonal);
                    }
                }

                if (x < width - 1)
                {
                    best = Math.Min(best, d[at + 1] + Near);
                }

                if (y == height - 1 || x == width - 1)
                {
                    best = Math.Min(best, Near);
                }

                d[at] = best;
            }
        }

        float[] spread = new float[d.Length];

        for (int i = 0; i < d.Length; i++)
        {
            spread[i] = d[i] / (float)Near;
        }

        return spread;
    }

    /// <summary>
    /// How wide the bars are, from the distance transform's own ridge.
    /// </summary>
    private static float Feature(float[] distance, bool[] opaque, int width, int height)
    {
        var spine = new List<float>();

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int at = (y * width) + x;

                if (!opaque[at] || distance[at] <= 0.9f)
                {
                    continue;
                }

                float here = distance[at];
                bool ridge = true;

                for (int dy = -1; dy <= 1 && ridge; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx;
                        int ny = y + dy;

                        if (nx < 0 || ny < 0 || nx >= width || ny >= height)
                        {
                            continue;
                        }

                        if (distance[(ny * width) + nx] > here + 1e-6f)
                        {
                            ridge = false;
                            break;
                        }
                    }
                }

                if (ridge)
                {
                    spine.Add(here);
                }
            }
        }

        if (spine.Count == 0)
        {
            return 0f;
        }

        spine.Sort();

        float quartile = spine[Math.Min(spine.Count - 1, spine.Count / 4)];

        // Never less than two: a bar one texel across is still two texels of silhouette,
        // and a thickness of nothing is what this pass exists to remove.
        return Math.Max(2f, 2f * quartile);
    }
}
