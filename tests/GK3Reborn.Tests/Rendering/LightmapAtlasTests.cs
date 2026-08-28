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
    public void A_second_bake_lands_where_the_first_one_did()
    {
        // What a light switch and a disco ball both are: the same room lit again. Where
        // each tile sits is written into the vertices, so a replacement that repacked would
        // light every surface with some other surface's bake.
        DecodedImage[] first = [Solid(8, 8, 10), Solid(16, 4, 20), Solid(4, 4, 30)];
        DecodedImage[] second = [Solid(8, 8, 110), Solid(16, 4, 120), Solid(4, 4, 130)];

        LightmapAtlas atlas = LightmapAtlas.Pack(first);
        DecodedImage relit = atlas.Repack(second);

        Assert.Equal(atlas.Image.Width, relit.Width);
        Assert.Equal(atlas.Image.Height, relit.Height);

        for (int tile = 0; tile < second.Length; tile++)
        {
            Assert.Equal(second[tile].Pixels[0], At(relit, atlas.Regions[tile]));
        }
    }

    [Fact]
    public void A_second_bake_whose_tiles_are_a_different_size_is_sampled_into_the_slot()
    {
        // 86 of RL2's 479 surfaces are a different size between its ordinary bake and its
        // disco one: a wall lit evenly exports as a single texel and the same wall under a
        // mirror ball as eight. Skipping those would leave a fifth of the room lit by the
        // scene it has just left.
        DecodedImage[] first = [Solid(8, 8, 10), Solid(1, 1, 20)];
        DecodedImage[] second = [Solid(2, 2, 110), Solid(16, 16, 120)];

        LightmapAtlas atlas = LightmapAtlas.Pack(first);
        DecodedImage relit = atlas.Repack(second);

        Assert.Equal(110, At(relit, atlas.Regions[0]));
        Assert.Equal(120, At(relit, atlas.Regions[1]));
    }

    /// <summary>Reads the middle of a region out of a packed atlas.</summary>
    private static byte At(DecodedImage atlas, Vector4 region)
    {
        int x = (int)((region.X + (region.Z / 2f)) * atlas.Width);
        int y = (int)((region.Y + (region.W / 2f)) * atlas.Height);

        return atlas.Pixels[(((y * atlas.Width) + x) * 4) + 1];
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
