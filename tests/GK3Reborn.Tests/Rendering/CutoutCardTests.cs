using System.Numerics;
using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Rendering;
using Xunit;

namespace GK3Reborn.Tests.Rendering;

/// <summary>
/// Tests for giving a keyed card the thickness of the thing drawn on it.
/// </summary>
/// <remarks>
/// GK3 draws a railing as a picture of one on a single quad with the gaps between the
/// balusters cut out of the magenta key, which is convincing from in front and a sheet of
/// paper from anywhere else. The measurement that decides whether a texture is a lattice of
/// bars, and how deep to make them, is what most of this exercises: it is one number, it
/// separates a railing from a chest of drawers with a keyhole in it, and getting it wrong
/// is silent in both directions.
/// </remarks>
public sealed class CutoutCardTests
{
    /// <summary>A texture whose texels are keyed where the mask says so.</summary>
    private static DecodedImage Picture(int width, int height, Func<int, int, bool> drawn)
    {
        byte[] pixels = new byte[width * height * 4];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int at = ((y * width) + x) * 4;
                bool here = drawn(x, y);

                pixels[at] = 180;
                pixels[at + 1] = 140;
                pixels[at + 2] = 90;
                pixels[at + 3] = here ? (byte)255 : (byte)0;
            }
        }

        return new DecodedImage(width, height, pixels, HasAlpha: true, "test");
    }

    /// <summary>Upright bars four texels wide, sixteen apart: a railing, in miniature.</summary>
    private static DecodedImage Railing(int size = 64) =>
        Picture(size, size, (x, y) => y < 6 || y >= size - 6 || x % 16 < 4);

    /// <summary>A quad on the x/y plane at z, mapped to one whole tile of its texture.</summary>
    private static (Vector3[] Positions, Vector2[] TexCoords, int[] Indices) Card(
        float width, float height, float z = 0f, float tiles = 1f)
    {
        Vector3[] positions =
        [
            new(0f, 0f, z), new(width, 0f, z), new(width, height, z), new(0f, height, z),
        ];

        Vector2[] texCoords =
        [
            new(0f, 0f), new(tiles, 0f), new(tiles, 1f), new(0f, 1f),
        ];

        return (positions, texCoords, [0, 1, 2, 0, 2, 3]);
    }

    [Fact]
    public void A_railing_measures_as_the_width_of_its_balusters()
    {
        CutoutMask? mask = CutoutMask.Measure(Railing());

        Assert.NotNull(mask);

        // The bars are four texels across and the rails top and bottom are six deep, so a
        // measurement that answered by area would say six and this one says four: the
        // spine of a bar is longer than the spine of the rail it hangs from.
        Assert.Equal(4f, mask.FeatureTexels, 1);
    }

    [Fact]
    public void A_panel_with_a_hole_in_it_is_not_a_railing()
    {
        // What CHESTDRWERS is: a drawer front, opaque but for a keyhole. It is keyed, it is
        // a card, and giving it a thickness would make a slab of it.
        DecodedImage drawers = Picture(
            64, 64, (x, y) => !(x is > 28 and < 36 && y is > 28 and < 36));

        Assert.Null(CutoutMask.Measure(drawers));
    }

    [Fact]
    public void The_same_railing_upscaled_is_still_a_railing()
    {
        // The game ships two of every texture: the 1999 drawing, and an upscale of it at
        // eight to thirty-two times the resolution. A limit counted in texels rather than
        // taken as a share of the texture therefore rejected every railing in the game the
        // moment the content packs were installed — and rejected it silently, because every
        // render made without the packs went on showing the pass working.
        CutoutMask small = CutoutMask.Measure(Railing(64))!;
        CutoutMask large = CutoutMask.Measure(
            Picture(1024, 1024, (x, y) => y < 96 || y >= 928 || x % 256 < 64))!;

        Assert.Equal(
            small.FeatureTexels / small.Width,
            large.FeatureTexels / large.Width,
            2);
    }

    [Fact]
    public void A_large_texture_is_measured_at_the_resolution_it_was_drawn_at()
    {
        CutoutMask mask = CutoutMask.Measure(
            Picture(2048, 2048, (x, y) => y < 192 || y >= 1856 || x % 512 < 128))!;

        // Brought down rather than measured where it is: a 2,048-square mask is four
        // megabytes of it and sixty-four times the texels for the rim to walk.
        Assert.True(
            Math.Max(mask.Width, mask.Height) <= CutoutMask.ReferenceTexels,
            $"mask is {mask.Width}x{mask.Height}");
    }

    [Fact]
    public void A_texture_with_no_holes_is_not_a_railing()
    {
        Assert.Null(CutoutMask.Measure(Picture(64, 64, (_, _) => true)));
        Assert.Null(CutoutMask.Measure(Picture(64, 64, (_, _) => false)));
    }

    [Fact]
    public void Magenta_is_read_as_a_hole_even_where_the_alpha_was_not_converted()
    {
        byte[] pixels = new byte[64 * 64 * 4];

        for (int y = 0; y < 64; y++)
        {
            for (int x = 0; x < 64; x++)
            {
                int at = ((y * 64) + x) * 4;
                bool drawn = y < 6 || y >= 58 || x % 16 < 4;

                pixels[at] = drawn ? (byte)180 : (byte)255;
                pixels[at + 1] = drawn ? (byte)140 : (byte)0;
                pixels[at + 2] = drawn ? (byte)90 : (byte)255;
                pixels[at + 3] = 255;
            }
        }

        CutoutMask? mask = CutoutMask.Measure(
            new DecodedImage(64, 64, pixels, HasAlpha: false, "magenta"));

        Assert.NotNull(mask);
        Assert.Equal(4f, mask.FeatureTexels, 1);
    }

    [Fact]
    public void A_card_becomes_a_shell_of_two_faces_and_a_rim()
    {
        CutoutMask mask = CutoutMask.Measure(Railing())!;
        (Vector3[] positions, Vector2[] texCoords, int[] indices) = Card(64f, 64f);

        ThickCard? shell = CutoutCards.Thicken(positions, texCoords, indices, mask);

        Assert.NotNull(shell);

        // The card's own two triangles, both ways, and two triangles for every rim quad.
        Assert.Equal(4 + (shell.RimQuads * 2), shell.Triangles.Count);
        Assert.True(shell.RimQuads > 0, "a railing has a silhouette to build a rim around");

        // A texel is one unit here and the bars are four texels, so the bars come out four
        // units thick — the measurement carried through the card's own scale, not a
        // constant.
        Assert.Equal(4f, shell.Thickness, 2);
    }

    [Fact]
    public void The_shell_straddles_the_plane_the_card_was_on()
    {
        CutoutMask mask = CutoutMask.Measure(Railing())!;
        (Vector3[] positions, Vector2[] texCoords, int[] indices) = Card(64f, 64f, z: 25f);

        ThickCard shell = CutoutCards.Thicken(positions, texCoords, indices, mask)!;

        float least = float.MaxValue;
        float most = float.MinValue;

        foreach (CardTriangle triangle in shell.Triangles)
        {
            foreach (CurvedCorner corner in
                     (ReadOnlySpan<CurvedCorner>)[triangle.A, triangle.B, triangle.C])
            {
                least = Math.Min(least, corner.Position.Z);
                most = Math.Max(most, corner.Position.Z);
            }
        }

        // Half a thickness each way and no further. Which side of a card is its outside is
        // not in this data, so the shell may not favour one: extruding the wrong way lifts
        // a rail off its posts, and nothing about the card says which way is wrong.
        Assert.Equal(25f - (shell.Thickness / 2f), least, 3);
        Assert.Equal(25f + (shell.Thickness / 2f), most, 3);
    }

    [Fact]
    public void The_rim_takes_its_texture_coordinate_from_inside_the_bar()
    {
        CutoutMask mask = CutoutMask.Measure(Railing())!;
        (Vector3[] positions, Vector2[] texCoords, int[] indices) = Card(64f, 64f);

        ThickCard shell = CutoutCards.Thicken(positions, texCoords, indices, mask)!;

        // Every corner of the shell must land on a texel the key did not remove, rim
        // included. A rim coordinate on the boundary itself is as likely to be discarded by
        // the shader's alpha test as kept, and a wall that disappears is a wall that was
        // not worth building.
        foreach (CardTriangle triangle in shell.Triangles.Skip(4))
        {
            foreach (CurvedCorner corner in
                     (ReadOnlySpan<CurvedCorner>)[triangle.A, triangle.B, triangle.C])
            {
                int x = (int)MathF.Floor(corner.TexCoord.X * mask.Width);
                int y = (int)MathF.Floor(corner.TexCoord.Y * mask.Height);

                Assert.True(
                    mask.At(x, y),
                    $"rim coordinate ({corner.TexCoord.X:F3}, {corner.TexCoord.Y:F3}) " +
                    $"is texel ({x}, {y}), which the key removes");
            }
        }
    }

    [Fact]
    public void The_rim_faces_out_of_the_card_rather_than_along_it()
    {
        CutoutMask mask = CutoutMask.Measure(Railing())!;
        (Vector3[] positions, Vector2[] texCoords, int[] indices) = Card(64f, 64f);

        ThickCard shell = CutoutCards.Thicken(positions, texCoords, indices, mask)!;

        foreach (CardTriangle triangle in shell.Triangles.Skip(4))
        {
            // The card is on the x/y plane, so a rim normal must lie in it: a rim pointing
            // out of the plane is a face, and one of those is already there.
            Assert.Equal(0f, triangle.A.Normal.Z, 3);
            Assert.Equal(1f, triangle.A.Normal.Length(), 3);
        }
    }

    [Fact]
    public void A_card_that_covers_part_of_a_tile_gets_no_rim_outside_itself()
    {
        CutoutMask mask = CutoutMask.Measure(Railing())!;

        // Half a tile across, which is what a stair rail is: 55% of CS3STAIRRAIL and the
        // rest of the outline belongs to a rail that is not there.
        Vector3[] positions =
            [new(0f, 0f, 0f), new(32f, 0f, 0f), new(32f, 64f, 0f), new(0f, 64f, 0f)];

        Vector2[] texCoords = [new(0f, 0f), new(0.5f, 0f), new(0.5f, 1f), new(0f, 1f)];

        ThickCard shell = CutoutCards.Thicken(positions, texCoords, [0, 1, 2, 0, 2, 3], mask)!;

        foreach (CardTriangle triangle in shell.Triangles)
        {
            foreach (CurvedCorner corner in
                     (ReadOnlySpan<CurvedCorner>)[triangle.A, triangle.B, triangle.C])
            {
                Assert.InRange(corner.Position.X, -0.01f, 32.01f);
                Assert.InRange(corner.Position.Y, -0.01f, 64.01f);
            }
        }
    }

    [Fact]
    public void A_card_whose_coordinates_start_away_from_the_origin_keeps_its_rim_on_itself()
    {
        // Reported as RC1's "COMPLET / NO VACANCIES" sign growing a card's height of loose
        // yellow facets in the air below it, and the hotel sign above it doing the same
        // over the brickwork. GK3's rooms store v running from -1 to 0 rather than 0 to 1 —
        // 2,767 of the corpus's 2,781 keyed surfaces do — so the grid the silhouette is
        // walked in starts at texel (0, -height) and not at (0, 0). The runs it finds are
        // numbered from the corner of that grid; read back as texture coordinates without
        // the offset, the whole rim is built exactly one tile away from the card it belongs
        // to. Every earlier test here draws its card at the texture's origin, where the
        // offset is zero and the mistake is invisible.
        CutoutMask mask = CutoutMask.Measure(Railing())!;

        Vector3[] positions =
            [new(0f, 0f, 0f), new(64f, 0f, 0f), new(64f, 64f, 0f), new(0f, 64f, 0f)];

        Vector2[] texCoords = [new(0f, -1f), new(1f, -1f), new(1f, 0f), new(0f, 0f)];

        ThickCard shell = CutoutCards.Thicken(positions, texCoords, [0, 1, 2, 0, 2, 3], mask)!;

        foreach (CardTriangle triangle in shell.Triangles)
        {
            foreach (CurvedCorner corner in
                     (ReadOnlySpan<CurvedCorner>)[triangle.A, triangle.B, triangle.C])
            {
                Assert.InRange(corner.Position.X, -0.01f, 64.01f);
                Assert.InRange(corner.Position.Y, -0.01f, 64.01f);
            }
        }
    }

    [Fact]
    public void A_card_placed_away_from_the_origin_is_thickened_the_same_as_one_at_it()
    {
        // The same card twice, once at the texture's origin and once a tile down, which is
        // the only difference between a railing in this corpus and the sign beside it. The
        // rim is a property of the drawing, so the two must come out identical bar the
        // offset — a count that differs means the grid moved something other than itself.
        CutoutMask mask = CutoutMask.Measure(Railing())!;

        (Vector3[] positions, Vector2[] texCoords, int[] indices) = Card(64f, 64f);
        ThickCard atOrigin = CutoutCards.Thicken(positions, texCoords, indices, mask)!;

        Vector2[] moved = [new(0f, -1f), new(1f, -1f), new(1f, 0f), new(0f, 0f)];
        ThickCard aTileDown = CutoutCards.Thicken(positions, moved, indices, mask)!;

        Assert.Equal(atOrigin.RimQuads, aTileDown.RimQuads);
        Assert.Equal(atOrigin.Thickness, aTileDown.Thickness, 4);
    }

    [Fact]
    public void A_tiled_card_grows_no_wall_across_its_seams()
    {
        // One horizontal bar running the whole width of its tile: a handrail. Tiled, it is
        // one continuous handrail, and its rim is the two long faces and the two ends —
        // four runs, however many times the texture repeats along it.
        CutoutMask mask = CutoutMask.Measure(
            Picture(64, 64, (_, y) => y is >= 28 and < 36))!;

        ThickCard one = CutoutCards.Thicken(
            Card(32f, 32f).Positions,
            Card(32f, 32f).TexCoords,
            Card(32f, 32f).Indices,
            mask)!;

        Assert.Equal(4, one.RimQuads);

        (Vector3[] positions, Vector2[] texCoords, int[] indices) = Card(128f, 32f, tiles: 4f);
        ThickCard four = CutoutCards.Thicken(positions, texCoords, indices, mask)!;

        // Still four. Measured tile by tile it would be sixteen: every seam would grow an
        // end cap facing into the next tile and another facing back out of it, walling off
        // a rail that does not stop there. That is the whole reason the mask is scanned in
        // one grid spanning every tile the card covers.
        Assert.Equal(4, four.RimQuads);
    }

    [Fact]
    public void A_card_that_is_not_flat_is_left_alone()
    {
        CutoutMask mask = CutoutMask.Measure(Railing())!;
        (Vector3[] positions, Vector2[] texCoords, int[] indices) = Card(64f, 64f);

        positions[2] = positions[2] with { Z = 20f };

        Assert.Null(CutoutCards.Thicken(positions, texCoords, indices, mask));
    }

    [Fact]
    public void A_card_wound_both_ways_is_still_flat()
    {
        CutoutMask mask = CutoutMask.Measure(Railing())!;
        (Vector3[] positions, Vector2[] texCoords, _) = Card(64f, 64f);

        // GK3's scene geometry is not wound consistently, and a card whose two triangles
        // face opposite ways is ordinary. Summing the normals as authored cancels a
        // perfectly flat card to nothing, which used to refuse it.
        ThickCard? shell = CutoutCards.Thicken(
            positions, texCoords, [0, 1, 2, 0, 3, 2], mask);

        Assert.NotNull(shell);
        Assert.True(shell.RimQuads > 0);
    }

    [Fact]
    public void A_bar_too_wide_in_the_room_is_left_alone()
    {
        CutoutMask mask = CutoutMask.Measure(Railing())!;

        // The same drawing stretched over a wall: four texels is now forty units, which is
        // a panel painted to look like a railing rather than a railing.
        (Vector3[] positions, Vector2[] texCoords, int[] indices) = Card(640f, 640f);

        Assert.Null(CutoutCards.Thicken(positions, texCoords, indices, mask));
    }

    [Fact]
    public void The_rim_stays_within_its_budget()
    {
        // Noise at the texel scale, which is what a chain-link fence measures as. Every
        // texel is its own run, so an unbudgeted rim would be tens of thousands of quads.
        var random = new Random(1);
        DecodedImage speckle = Picture(256, 256, (_, _) => random.Next(3) > 0);

        CutoutMask? mask = CutoutMask.Measure(speckle);

        Assert.NotNull(mask);

        (Vector3[] positions, Vector2[] texCoords, int[] indices) = Card(256f, 256f);
        ThickCard? shell = CutoutCards.Thicken(positions, texCoords, indices, mask);

        // Reduced rather than refused: the budget raises the shortest run it will build
        // until the rim fits, so the bars survive and the stipple between them does not.
        if (shell is not null)
        {
            Assert.True(
                shell.RimQuads <= CutoutCards.MostRimQuads,
                $"{shell.RimQuads} rim quads is over the budget of {CutoutCards.MostRimQuads}");
        }
    }

    /// <summary>How much room the occluder triangles cover, in square units.</summary>
    private static float OccluderArea(ThickCard shell)
    {
        float total = 0f;

        for (int at = 0; at + 2 < shell.Occluders.Count; at += 3)
        {
            total += 0.5f * Vector3.Cross(
                shell.Occluders[at + 1] - shell.Occluders[at],
                shell.Occluders[at + 2] - shell.Occluders[at]).Length();
        }

        return total;
    }

    /// <summary>What share of a mask's texels are drawn.</summary>
    private static float DrawnShare(CutoutMask mask) =>
        mask.Opaque.Count(drawn => drawn) / (float)mask.Opaque.Length;

    [Fact]
    public void A_thickened_card_is_given_the_silhouette_to_cast_as_opaque_triangles()
    {
        CutoutMask mask = CutoutMask.Measure(Railing())!;
        (Vector3[] positions, Vector2[] texCoords, int[] indices) = Card(64f, 64f);

        ThickCard shell = CutoutCards.Thicken(positions, texCoords, indices, mask)!;

        // The whole point of them. The shell itself cannot be traced: it is keyed, the
        // acceleration structure runs no any-hit shader, and a keyed triangle in it casts
        // the shadow of its whole quad — which is why keyed geometry was kept out
        // altogether and why a thickened railing went on casting no shadow at all.
        Assert.NotEmpty(shell.Occluders);
        Assert.Equal(0, shell.Occluders.Count % 3);

        // And they are the drawn texels and not the card: a texel is one unit here, so the
        // area they cover is the number of texels the key left. Exact rather than
        // approximate, because the patches are whole texels and the merge neither overlaps
        // them nor leaves a gap.
        Assert.Equal(DrawnShare(mask) * 64f * 64f, OccluderArea(shell), 1);
    }

    [Fact]
    public void The_shadow_is_cast_from_the_plane_the_card_was_always_on()
    {
        CutoutMask mask = CutoutMask.Measure(Railing())!;
        (Vector3[] positions, Vector2[] texCoords, int[] indices) = Card(64f, 64f, z: 25f);

        ThickCard shell = CutoutCards.Thicken(positions, texCoords, indices, mask)!;

        // Not at either face of the shell. Two planes would double the cost to widen a
        // shadow by the width of a baluster, and a plane between the two faces cannot
        // shadow either of them: a shadow ray leaves a face along its own normal, away from
        // the plane behind it.
        Assert.All(shell.Occluders, corner => Assert.Equal(25f, corner.Z, 3));
    }

    [Fact]
    public void The_shadow_is_merged_into_far_fewer_quads_than_it_has_texels()
    {
        CutoutMask mask = CutoutMask.Measure(Railing())!;
        (Vector3[] positions, Vector2[] texCoords, int[] indices) = Card(64f, 64f);

        ThickCard shell = CutoutCards.Thicken(positions, texCoords, indices, mask)!;

        // A quad per drawn texel would be sixteen hundred of them. The greedy merge finds
        // each baluster as a single rectangle, which is the answer anybody would draw by
        // hand, and it is what makes this affordable at all.
        int quads = shell.Occluders.Count / 6;

        Assert.True(
            quads < 40,
            $"{quads} quads for a railing whose bars should merge into about a dozen");
    }

    [Fact]
    public void A_lattice_too_fine_to_afford_keeps_its_holes()
    {
        // A chain-link fence is about half drawn, so a coarsening that rounds a
        // half-covered cell up makes every cell solid and the fence casts the shadow of a
        // wall — worse than the nothing it cast before. This is the pattern that would do
        // it: a texel on, a texel off, at a resolution no budget can mesh one quad at a
        // time.
        CutoutMask mask = CutoutMask.Measure(
            Picture(256, 256, (x, y) => ((x / 2) + (y / 2)) % 2 == 0))!;

        (Vector3[] positions, Vector2[] texCoords, int[] indices) = Card(256f, 256f);
        ThickCard? shell = CutoutCards.Thicken(positions, texCoords, indices, mask);

        if (shell is null)
        {
            return;
        }

        Assert.True(
            shell.Occluders.Count / 6 <= CutoutCards.MostShadowQuads,
            $"{shell.Occluders.Count / 6} quads is over the budget of " +
            $"{CutoutCards.MostShadowQuads}");

        // Reduced downwards, never upwards. Half of this card is a hole and the shadow it
        // casts may cover less of the ground than that but never more.
        Assert.True(
            OccluderArea(shell) <= DrawnShare(mask) * 256f * 256f * 1.02f,
            $"{OccluderArea(shell):F0} square units of shadow from " +
            $"{DrawnShare(mask) * 256f * 256f:F0} square units of drawing");
    }

    [Fact]
    public void The_leaves_are_named_rather_than_measured()
    {
        // The one thing the measurement cannot answer: a maple sprite's edge is a smooth
        // curve, which merges into longer straighter runs than the hotel's wrought-iron
        // balustrade does. Worth a test because the list is the whole defence.
        Assert.Contains("PINE2", CutoutCards.Leaves);
        Assert.Contains("maple", CutoutCards.Leaves);
        Assert.DoesNotContain("CS3STAIRRAIL", CutoutCards.Leaves);
        Assert.DoesNotContain("RC1IRONFENCE", CutoutCards.Leaves);
    }
}
