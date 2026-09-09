using System.Numerics;
using GK3Reborn.Formats.Scenes;
using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Game;
using GK3Reborn.Game.Navigation;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Tests for where a walk to see part of the room is aimed.
/// </summary>
public sealed class WalkToSeeTests
{
    /// <summary>A boundary drawn as rows, ten units a texel, the top row at the highest Z.</summary>
    private static WalkBoundary Map(params string[] rows)
    {
        int width = rows[0].Length;
        byte[] indices = new byte[width * rows.Length];

        for (int y = 0; y < rows.Length; y++)
        {
            for (int x = 0; x < width; x++)
            {
                indices[(y * width) + x] = rows[y][x] == '#' ? (byte)255 : (byte)0;
            }
        }

        return new WalkBoundary(
            new IndexedImage(width, rows.Length, indices),
            new Vector2(width * 10, rows.Length * 10),
            Vector2.Zero);
    }

    private static LoadedScene Scene(WalkBoundary? boundary) =>
        new(
            "TEST",
            new SceneDefinition(SceneInitFile.Parse(
                "[ROOM_CAMERAS]\nA, angle={0,0}, pos={0,0,0}, Default\n", "T.SIF")),
            Asset: null,
            Lightmaps: null,
            ModelsPlaced: 0,
            Walkable: boundary);

    [Fact]
    public void A_walk_to_see_is_aimed_half_a_diagonal_past_the_middle()
    {
        // The retail rule: the middle of the box, plus half its horizontal diagonal along
        // +Z. A box 30 by 40 has a diagonal of 50, so the point is 25 past the middle.
        Vector3 aimed = SceneScripting.VantageFor(
            Scene(null), (new Vector3(10, 0, 20), new Vector3(40, 100, 60)));

        Assert.Equal(new Vector3(25, 50, 65), aimed);
    }

    [Fact]
    public void And_walked_on_until_the_floor_is_walkable()
    {
        // A house: its inside is open floor, its walls are not, and the box is the inside.
        // Half a diagonal past its middle lands in the back wall; the walk is aimed at the
        // first open ground beyond it rather than at the wall, and never at the inside.
        WalkBoundary boundary = Map(
            "..........",
            "..........",
            "..######..",
            "..#....#..",
            "..#....#..",
            "..#....#..",
            "..######..",
            "..........");

        Vector3 aimed = SceneScripting.VantageFor(
            Scene(boundary), (new Vector3(30, 0, 20), new Vector3(70, 100, 50)));

        Assert.True(boundary.IsWalkable(aimed));
        Assert.Equal(50, aimed.X, 0.01);
        Assert.True(aimed.Z >= 60, $"aimed at Z {aimed.Z}, which is not past the wall");
    }
}
