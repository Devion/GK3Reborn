using System.Numerics;
using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Formats.Scenes;
using GK3Reborn.Game.Navigation;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Tests for where an actor is allowed to stand.
/// </summary>
/// <remarks>
/// The mapping is the part worth testing. A boundary applied upside down, or with the
/// offset's sign the wrong way, still produces a mask that covers plausible-looking ground
/// — so these pin the corners rather than the shape.
/// </remarks>
public sealed class WalkBoundaryTests
{
    /// <summary>
    /// A four-by-four boundary: open along the top row of the image, wall below it.
    /// </summary>
    /// <remarks>
    /// The image's top row is the <em>far</em> end of the room in world space, because the
    /// bitmap's rows run from the bottom of the covered area upward.
    /// </remarks>
    private static WalkBoundary Fixture(Vector2? offset = null)
    {
        byte[] indices =
        [
            0, 0, 0, 0,
            1, 7, 200, 8,
            255, 255, 255, 255,
            255, 255, 255, 255,
        ];

        return new WalkBoundary(
            new IndexedImage(4, 4, indices), new Vector2(40, 40), offset ?? Vector2.Zero);
    }

    [Fact]
    public void The_top_row_of_the_bitmap_is_the_far_end_of_the_room()
    {
        WalkBoundary boundary = Fixture();

        // Covered area runs 0..40 on both axes. The image's top row maps to the highest Z.
        Assert.Equal(0, boundary.RegionAt(new Vector3(5, 0, 35)));
        Assert.Equal(255, boundary.RegionAt(new Vector3(5, 0, 5)));
    }

    [Fact]
    public void The_offset_moves_the_world_origin_within_the_covered_area()
    {
        // Shifting the origin twenty units puts what was the middle of the bitmap at zero.
        WalkBoundary boundary = Fixture(new Vector2(20, 20));

        Assert.Equal(0, boundary.RegionAt(new Vector3(-15, 0, 15)));
        Assert.Equal(255, boundary.RegionAt(new Vector3(-15, 0, -15)));
    }

    [Fact]
    public void Anywhere_outside_the_bitmap_is_wall()
    {
        WalkBoundary boundary = Fixture();

        Assert.Equal(255, boundary.RegionAt(new Vector3(-1, 0, 20)));
        Assert.Equal(255, boundary.RegionAt(new Vector3(41, 0, 20)));
        Assert.False(boundary.IsWalkable(new Vector3(20, 0, 1000)));
    }

    [Fact]
    public void The_gradient_towards_a_wall_is_walkable_until_it_is_not()
    {
        WalkBoundary boundary = Fixture();

        Assert.True(boundary.IsRegionOpen(0));
        Assert.True(boundary.IsRegionOpen(7));
        Assert.False(boundary.IsRegionOpen(8));
        Assert.False(boundary.IsRegionOpen(9));
        Assert.False(boundary.IsRegionOpen(255));
    }

    [Fact]
    public void A_scripted_region_can_be_closed_and_opened_again()
    {
        WalkBoundary boundary = Fixture();
        var inRegion = new Vector3(25, 0, 25);

        Assert.Equal(200, boundary.RegionAt(inRegion));
        Assert.True(boundary.IsWalkable(inRegion));

        boundary.SetRegionOpen(200, open: false);
        Assert.False(boundary.IsWalkable(inRegion));

        boundary.SetRegionOpen(200, open: true);
        Assert.True(boundary.IsWalkable(inRegion));
    }

    [Fact]
    public void Wall_is_not_a_region_a_script_can_open()
    {
        WalkBoundary boundary = Fixture();

        boundary.SetRegionOpen(255, open: true);
        boundary.SetRegionOpen(8, open: true);

        Assert.False(boundary.IsRegionOpen(255));
        Assert.False(boundary.IsRegionOpen(8));
    }

    [Fact]
    public void A_texel_and_the_world_point_at_its_centre_agree()
    {
        WalkBoundary boundary = Fixture(new Vector2(20, 20));

        for (int y = 0; y < boundary.Height; y++)
        {
            for (int x = 0; x < boundary.Width; x++)
            {
                Assert.Equal((x, y), boundary.ToTexel(boundary.ToWorld(x, y)));
            }
        }
    }

    [Fact]
    public void Counting_the_open_texels_reflects_what_scripts_have_closed()
    {
        WalkBoundary boundary = Fixture();

        // Four zeros, a one, a seven and the scripted region; the 8 and the eight 255s are
        // shut.
        Assert.Equal(7, boundary.WalkableTexels());

        boundary.SetRegionOpen(200, open: false);
        Assert.Equal(6, boundary.WalkableTexels());
    }

    [Fact]
    public void A_boundary_is_read_from_the_scene_however_its_keys_are_spelt()
    {
        // RC1 capitalises Size and Offset where every other scene does not.
        SceneBoundary boundary = Assert.IsType<SceneBoundary>(
            SceneInitFile.Parse(
                """
                [GENERAL]
                boundary=Rc1_wlkBnds,Size={2349.97, 3042.44},Offset={-1388.04, 4348.15}
                """,
                "RC1.SIF").Boundary());

        Assert.Equal("Rc1_wlkBnds", boundary.Texture);
        Assert.Equal(new Vector2(2349.97f, 3042.44f), boundary.Size);
        Assert.Equal(new Vector2(-1388.04f, 4348.15f), boundary.Offset);
    }

    [Fact]
    public void A_scene_with_no_boundary_says_so()
    {
        Assert.Null(SceneInitFile.Parse("[GENERAL]\nfloor=r25_floor\n", "R25.SIF").Boundary());
    }

    [Fact]
    public void An_eight_bit_bitmap_keeps_its_palette_indices()
    {
        // 3x2, bottom-up, so the first row on disk is the bottom of the image. The stride
        // pads to four bytes, which is what a naive reader gets wrong.
        var output = new MemoryStream();
        var writer = new BinaryWriter(output);
        writer.Write("BM"u8);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write(1078u);         // pixel data offset, past a 256-entry palette
        writer.Write(40u);           // DIB header size
        writer.Write(3);             // width
        writer.Write(2);             // height, positive means bottom-up
        writer.Write((ushort)1);     // planes
        writer.Write((ushort)8);     // bits per pixel
        writer.Write(0u);            // no compression
        writer.Write(new byte[20]);
        writer.Write(new byte[1078 - 54]);
        writer.Write(new byte[] { 255, 255, 255, 0 });   // bottom row, plus stride padding
        writer.Write(new byte[] { 0, 7, 200, 0 });       // top row
        writer.Flush();

        IndexedImage image = BitmapDecoder.DecodeIndexed(output.ToArray(), "test.BMP");

        Assert.Equal(3, image.Width);
        Assert.Equal(2, image.Height);
        Assert.Equal<byte[]>([0, 7, 200, 255, 255, 255], image.Indices);
    }
}
