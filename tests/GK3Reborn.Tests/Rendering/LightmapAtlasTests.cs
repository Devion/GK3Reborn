using System.Numerics;
using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Rendering;
using Xunit;

namespace GK3Reborn.Tests.Rendering;

/// <summary>
/// Tests for lightmap packing.
/// </summary>
/// <remarks>
/// A packer that overlaps tiles produces a picture that looks lit but is wrong in ways
/// nobody can attribute, so the tests check the invariant that matters — that no two tiles
/// claim the same texel — rather than any particular arrangement.
/// </remarks>
public sealed class LightmapAtlasTests
{
    private static DecodedImage Solid(int width, int height, byte level)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = level;
            pixels[i + 1] = level;
            pixels[i + 2] = level;
            pixels[i + 3] = 255;
        }

        return new DecodedImage(width, height, pixels, HasAlpha: false, "test");
    }

    [Fact]
    public void Every_tile_gets_a_region_inside_the_atlas()
    {
        DecodedImage[] tiles =
        [
            Solid(8, 8, 10), Solid(16, 4, 20), Solid(3, 9, 30), Solid(32, 32, 40),
        ];

        LightmapAtlas atlas = LightmapAtlas.Pack(tiles);

        Assert.Equal(tiles.Length, atlas.Regions.Count);

        foreach (Vector4 region in atlas.Regions)
        {
            Assert.InRange(region.X, 0f, 1f);
            Assert.InRange(region.Y, 0f, 1f);
            Assert.InRange(region.X + region.Z, 0f, 1f);
            Assert.InRange(region.Y + region.W, 0f, 1f);
        }
    }

    [Fact]
    public void Tiles_do_not_overlap()
    {
        DecodedImage[] tiles = Enumerable.Range(1, 40)
            .Select(i => Solid((i % 7) + 1, (i % 5) + 1, (byte)i))
            .ToArray();

        LightmapAtlas atlas = LightmapAtlas.Pack(tiles, maximumWidth: 32);

        bool[] claimed = new bool[atlas.Image.Width * atlas.Image.Height];

        for (int i = 0; i < tiles.Length; i++)
        {
            Vector4 region = atlas.Regions[i];

            // Convert back to texels through the centre of the region's corners, which is
            // what the shader's inset coordinates address.
            int x0 = (int)MathF.Round(region.X * atlas.Image.Width);
            int y0 = (int)MathF.Round(region.Y * atlas.Image.Height);

            for (int y = 0; y < tiles[i].Height; y++)
            {
                for (int x = 0; x < tiles[i].Width; x++)
                {
                    int at = ((y0 + y) * atlas.Image.Width) + x0 + x;

                    Assert.False(claimed[at], $"tile {i} overlaps another at {x0 + x},{y0 + y}");
                    claimed[at] = true;
                }
            }
        }
    }

    [Fact]
    public void A_tiles_pixels_survive_packing()
    {
        DecodedImage[] tiles = [Solid(4, 4, 77), Solid(8, 8, 200)];

        LightmapAtlas atlas = LightmapAtlas.Pack(tiles);
        Vector4 region = atlas.Regions[0];

        int x = (int)MathF.Round(region.X * atlas.Image.Width);
        int y = (int)MathF.Round(region.Y * atlas.Image.Height);
        int at = (((y + 1) * atlas.Image.Width) + x + 1) * 4;

        Assert.Equal(77, atlas.Image.Pixels[at]);
        Assert.Equal(255, atlas.Image.Pixels[at + 3]);
    }

    [Fact]
    public void Gutters_are_white_so_a_stray_sample_does_not_darken_a_surface()
    {
        LightmapAtlas atlas = LightmapAtlas.Pack([Solid(4, 4, 0)]);

        // The top-left texel is gutter, never tile.
        Assert.Equal(255, atlas.Image.Pixels[0]);
    }

    [Fact]
    public void An_empty_set_still_produces_a_usable_texture()
    {
        LightmapAtlas atlas = LightmapAtlas.Pack([]);

        Assert.Empty(atlas.Regions);
        Assert.True(atlas.Image.Width > 0);
        Assert.True(atlas.Image.Height > 0);
    }
}
