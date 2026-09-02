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
/// <remarks>
/// <b>The shell and the occluders are two renderings of one outline and neither is the
/// other.</b> What is drawn is a keyed quad either side of the plane with a rim between
/// them, and a ray cannot be pointed at that: the acceleration structure has no any-hit
/// shader, so a keyed triangle in it casts the shadow of its whole quad. What is traced is
/// therefore a second, opaque, patch-by-patch copy of the drawn texels, lying flat on the
/// plane the card always occupied. It is never drawn and the shell is never traced.
/// </remarks>
public sealed record ThickCard(
    IReadOnlyList<CardTriangle> Triangles,
    float Thickness,
    int RimQuads,
    IReadOnlyList<Vector3> Occluders);

/// <summary>
/// What a keyed texture's holes say about the shape drawn on it.
/// </summary>
/// <remarks>
/// <para>
/// Measured once per texture, because it is a fact about the drawing rather than about any
/// card the drawing is on: the same <c>CS3STAIRRAIL</c> hangs on four surfaces in two rooms
/// and its balusters are the same width in all of them. What varies per card is how large a
/// texel is in the room, and that is the card's business, not the texture's.
/// </para>
/// <para>
/// <b><see cref="FeatureTexels"/> is the whole decision.</b> It is the width of the bars the
/// artist drew, and it settles both questions this file has to answer: whether the texture
/// is a lattice of bars at all — a railing, a fence, a chain, a window mullion — or a solid
/// panel with a hole punched in it; and, if it is bars, how deep to make them. Nothing here
/// reads a texture's name.
/// </para>
/// </remarks>
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
    /// <remarks>
    /// Below three per cent the holes are a detail of the picture — a gap under a door, a
    /// nick out of a corner — and above ninety-seven per cent there is nothing left to give
    /// a thickness to.
    /// </remarks>
    private const float LeastKeyed = 0.03f;

    /// <summary>See <see cref="LeastKeyed"/>.</summary>
    private const float MostKeyed = 0.97f;

    /// <summary>
    /// Wider than this share of the texture and the drawing is a panel rather than a
    /// lattice of bars.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Measured across the corpus rather than chosen. As a fraction of the texture's
    /// shorter side, the things this pass exists for come out between 0.01 and 0.25 —
    /// <c>MCBFENCE</c> 0.008, <c>POUBRBRDWR</c> 0.016, <c>CS3STAIRRAIL</c> 0.031,
    /// <c>RC1IRONFENCE</c> 0.063, <c>CS3DBLHNGINV</c> 0.14, <c>MS3MUSWIN</c> 0.22,
    /// <c>CHAINS</c> 0.25 — and the things it must not touch come out between 0.55 and
    /// 1.0: <c>WOODROCK</c> 0.55, <c>CHESTDRWERS</c> 0.86, <c>LIGHTBULB</c> 0.86,
    /// <c>RL1_SCRAPE01</c> 0.89, <c>RC1BOOKSHOP</c> 1.0. Nothing falls in the gap.
    /// </para>
    /// <para>
    /// <b>A share and not a count of texels, because the game ships two of every texture.</b>
    /// The enhanced set is the 1999 drawing at eight to thirty-two times the resolution —
    /// <c>CS3STAIRRAIL</c> is 128 square in the barns and 2,048 square in the packs — so a
    /// baluster four texels wide is seventy-four texels wide in a shipped build. A count
    /// calibrated on the barns therefore rejected every railing in the game the moment the
    /// content packs were installed, and rejected it silently: renders made without the
    /// packs went on showing the pass working perfectly. A share is the same number for
    /// both, which is what it has to be.
    /// </para>
    /// </remarks>
    public const float WidestFeatureShare = 0.35f;

    /// <summary>
    /// The longest side this is measured at, in texels; larger masks are halved down to it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The resolution the corpus was drawn at, and the resolution the silhouette is
    /// therefore <em>authored</em> at: the enhanced textures upscale the 1999 picture, so
    /// nothing about a railing's outline exists above this that was not invented by an
    /// upscaler.
    /// </para>
    /// <para>
    /// It is also what keeps the cost down at both ends. A 2,048-square mask is four
    /// megabytes of it and sixty-four times the texels for the rim to scan, and expanding
    /// that level out of its blocks to look at it cost more than a second of a room's load
    /// on its own.
    /// </para>
    /// </remarks>
    public const int ReferenceTexels = 256;

    /// <summary>
    /// The coarsest a bar may be left, in texels, as the mask is reduced towards the
    /// resolution its outline was drawn at.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A ceiling alone is not enough, because the enhanced set enlarges a small texture
    /// most: <c>CHAINS</c> is 16 by 32 in the barns and 512 by 1,024 in the packs, so
    /// stopping at 256 leaves a mask eight times finer than anything ever drawn — and a card
    /// tiling it down a terrace is then a grid of two million texels for the rim to walk,
    /// which is where a tenth of a second of a room's load went.
    /// </para>
    /// <para>
    /// So the mask is halved while its bars stay at least this wide, and where that stops is
    /// the resolution the outline was authored at. It is not told what that is and it lands
    /// on it anyway: <c>CHAINS</c> comes back to exactly 16 by 32, <c>MCBFENCE</c> to 256
    /// square, <c>RC1IRONFENCE</c> to 64 by 128, <c>LBYSTRRAIL01</c> to 128 by 256 — every
    /// one of them the size of the 1999 file. Which also means a build with the packs and a
    /// build without measure very nearly the same thing, where before this they differed by
    /// a factor of three.
    /// </para>
    /// </remarks>
    public const float CoarsestBar = 4f;

    /// <summary>
    /// Measures a texture, if there is a lattice of bars drawn on it.
    /// </summary>
    /// <param name="image">The decoded texture, <em>after</em> the colour key was applied.</param>
    /// <returns>The mask, or null when this texture is nobody's railing.</returns>
    /// <remarks>
    /// The alpha channel is authoritative because <see cref="TextureKeying"/> has already
    /// run; the magenta test is kept as the same backstop the fragment shader keeps, for
    /// anything the conversion missed.
    /// </remarks>
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
    /// <remarks>
    /// Majority rather than "any of the four", which would fatten a bar by a texel at every
    /// step and close the gaps in a chain-link fence altogether after three of them.
    /// </remarks>
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

    /// <summary>
    /// How far each drawn texel is from the nearest hole, in texels.
    /// </summary>
    /// <remarks>
    /// Chamfer 3-4 in two passes, which is within about six per cent of the true Euclidean
    /// distance — far closer than anything downstream can use — and needs no library.
    /// <b>Outside the texture counts as a hole</b>, so a bar that runs off the edge of its
    /// tile is measured by its width and not by the accident of where the tile was cut.
    /// </remarks>
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
    /// <remarks>
    /// <para>
    /// A texel no nearer a hole than any of its neighbours sits on the spine of whatever it
    /// is part of, and twice its distance is that thing's width. Taking the spine rather
    /// than every drawn texel is what makes the answer the width of a <em>feature</em>
    /// instead of an average over area: a stair rail is mostly handrail by area and mostly
    /// baluster by spine, and it is the balusters that have to be given a thickness.
    /// </para>
    /// <para>
    /// The low quartile rather than the median, because a card carrying one thick member
    /// and a dozen thin ones — which is what a railing is — should be measured by the thin
    /// ones. A baluster given the handrail's width comes out a post.
    /// </para>
    /// </remarks>
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
