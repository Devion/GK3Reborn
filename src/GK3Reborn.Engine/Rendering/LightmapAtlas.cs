using System.Numerics;
using GK3Reborn.Formats.Bitmaps;

namespace GK3Reborn.Rendering;

/// <summary>
/// Packs a scene's per-surface lightmaps into one texture.
/// </summary>
/// <remarks>
/// <para>
/// A scene has one lightmap per surface — 925 of them in R25 alone — and binding each as
/// its own texture would mean a descriptor set and a draw call per surface, plus one
/// device allocation per lightmap. Drivers guarantee only a few thousand allocations in
/// total, so a handful of scenes would exhaust them.
/// </para>
/// <para>
/// Packing them instead lets surfaces sharing a diffuse texture be drawn together, which
/// is what makes a scene a few dozen draws rather than a thousand.
/// </para>
/// <para>
/// Tiles are separated by a one-texel gutter and their UVs inset by half a texel. Without
/// that, bilinear filtering at a tile's edge reaches into its neighbour, and a wall picks
/// up the lighting of whatever surface happened to be packed beside it — a bug that only
/// appears at glancing angles and is very hard to attribute after the fact.
/// </para>
/// </remarks>
public sealed class LightmapAtlas
{
    private const int Gutter = 1;

    private LightmapAtlas(DecodedImage image, IReadOnlyList<Vector4> regions)
    {
        Image = image;
        Regions = regions;
    }

    /// <summary>The packed texture.</summary>
    public DecodedImage Image { get; }

    /// <summary>
    /// Per-lightmap placement as (offsetU, offsetV, scaleU, scaleV): a tile-local UV
    /// becomes an atlas UV as <c>offset + uv * scale</c>.
    /// </summary>
    public IReadOnlyList<Vector4> Regions { get; }

    /// <summary>Packs a set of lightmaps.</summary>
    /// <param name="lightmaps">The lightmaps, in surface order.</param>
    /// <param name="maximumWidth">Widest the atlas may be.</param>
    /// <returns>The atlas.</returns>
    public static LightmapAtlas Pack(IReadOnlyList<DecodedImage> lightmaps, int maximumWidth = 4096)
    {
        ArgumentNullException.ThrowIfNull(lightmaps);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumWidth);

        // Tallest first, so each shelf is filled by tiles of similar height and little
        // vertical space is wasted above the short ones.
        int[] order = Enumerable.Range(0, lightmaps.Count)
            .OrderByDescending(i => lightmaps[i].Height)
            .ThenByDescending(i => lightmaps[i].Width)
            .ToArray();

        var placements = new (int X, int Y, int Width, int Height)[lightmaps.Count];

        int shelfX = Gutter;
        int shelfY = Gutter;
        int shelfHeight = 0;
        int usedWidth = 0;

        foreach (int index in order)
        {
            DecodedImage tile = lightmaps[index];
            int width = Math.Max(1, tile.Width);
            int height = Math.Max(1, tile.Height);

            if (shelfX + width + Gutter > maximumWidth)
            {
                shelfX = Gutter;
                shelfY += shelfHeight + Gutter;
                shelfHeight = 0;
            }

            placements[index] = (shelfX, shelfY, width, height);
            shelfX += width + Gutter;
            shelfHeight = Math.Max(shelfHeight, height);
            usedWidth = Math.Max(usedWidth, shelfX);
        }

        int atlasWidth = Math.Max(1, usedWidth + Gutter);
        int atlasHeight = Math.Max(1, shelfY + shelfHeight + Gutter);

        byte[] pixels = new byte[atlasWidth * atlasHeight * 4];

        // White, so any surface whose lightmap is missing or whose UVs land in a gutter
        // renders at full brightness rather than black.
        Array.Fill(pixels, (byte)255);

        var regions = new Vector4[lightmaps.Count];

        for (int index = 0; index < lightmaps.Count; index++)
        {
            (int x, int y, int width, int height) = placements[index];
            DecodedImage tile = lightmaps[index];

            for (int row = 0; row < height; row++)
            {
                for (int column = 0; column < width; column++)
                {
                    int source = ((Math.Min(row, tile.Height - 1) * tile.Width) +
                                  Math.Min(column, tile.Width - 1)) * 4;

                    int destination = (((y + row) * atlasWidth) + x + column) * 4;

                    if (source + 3 < tile.Pixels.Length && destination + 3 < pixels.Length)
                    {
                        pixels[destination] = tile.Pixels[source];
                        pixels[destination + 1] = tile.Pixels[source + 1];
                        pixels[destination + 2] = tile.Pixels[source + 2];
                        pixels[destination + 3] = 255;
                    }
                }
            }

            float insetU = 0.5f / atlasWidth;
            float insetV = 0.5f / atlasHeight;

            regions[index] = new Vector4(
                ((float)x / atlasWidth) + insetU,
                ((float)y / atlasHeight) + insetV,
                ((float)width / atlasWidth) - (2 * insetU),
                ((float)height / atlasHeight) - (2 * insetV));
        }

        return new LightmapAtlas(
            new DecodedImage(atlasWidth, atlasHeight, pixels, HasAlpha: false, "lightmap-atlas"),
            regions);
    }
}
