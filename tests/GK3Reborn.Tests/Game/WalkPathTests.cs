using System.Numerics;
using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Game.Navigation;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Tests for finding a way across a walk boundary.
/// </summary>
/// <remarks>
/// The boundaries here are drawn as text, one character per texel and the first row at the
/// top of the image, because a pathfinding fixture written as an array of bytes is
/// unreadable and a test you cannot read is a test nobody will fix. Each texel is ten
/// scene units square, so texel (2, 3) is the world point (25, 0, 35) in a five-row map.
/// </remarks>
public sealed class WalkPathTests
{
    /// <summary>
    /// Builds a boundary from a drawing.
    /// </summary>
    /// <remarks>
    /// <c>#</c> is wall, <c>.</c> is the middle of the floor, and a digit is that region —
    /// the gradient that says how near a wall a texel is. <c>D</c> is region 200, one of
    /// the regions a script may open and shut.
    /// </remarks>
    private static WalkBoundary Map(params string[] rows)
    {
        int width = rows[0].Length;
        byte[] indices = new byte[width * rows.Length];

        for (int y = 0; y < rows.Length; y++)
        {
            for (int x = 0; x < width; x++)
            {
                indices[(y * width) + x] = rows[y][x] switch
                {
                    '#' => 255,
                    '.' => 0,
                    'D' => 200,
                    char c => (byte)(c - '0'),
                };
            }
        }

        return new WalkBoundary(
            new IndexedImage(width, rows.Length, indices),
            new Vector2(width * 10, rows.Length * 10),
            Vector2.Zero);
    }

    /// <summary>The route's corners, back in texels.</summary>
    private static (int X, int Y)[] Corners(WalkBoundary boundary, WalkRoute route) =>
        [.. route.Points.Select(boundary.ToTexel)];

    /// <summary>Every texel the route actually crosses, corners included.</summary>
    /// <remarks>
    /// The straight line between corners, because that is what the walker walks: it heads
    /// at each corner in turn and does not care what the bitmap looks like on the way. This
    /// used to step towards the corner diagonally-then-straight, which for anything but a
    /// pure axis or a pure diagonal is a different path — so it agreed with a bug in the
    /// route finder instead of catching it.
    /// </remarks>
    private static List<(int X, int Y)> Walked(WalkBoundary boundary, WalkRoute route)
    {
        (int X, int Y)[] corners = Corners(boundary, route);
        List<(int X, int Y)> walked = [];

        if (corners.Length > 0)
        {
            walked.Add(corners[0]);
        }

        for (int i = 1; i < corners.Length; i++)
        {
            (int fromX, int fromY) = corners[i - 1];
            int spanX = corners[i].X - fromX;
            int spanY = corners[i].Y - fromY;
            int steps = Math.Max(Math.Abs(spanX), Math.Abs(spanY));

            for (int step = 1; step <= steps; step++)
            {
                walked.Add((
                    fromX + (int)MathF.Round(spanX * (float)step / steps),
                    fromY + (int)MathF.Round(spanY * (float)step / steps)));
            }
        }

        return walked;
    }

    [Fact]
    public void An_open_room_needs_no_corners()
    {
        WalkBoundary boundary = Map(
            ".........",
            ".........",
            ".........",
            ".........",
            ".........",
            ".........",
            ".........",
            ".........",
            ".........");

        WalkRoute route = WalkPath.Find(boundary, boundary.ToWorld(0, 0), boundary.ToWorld(8, 8));

        Assert.True(route.ReachedGoal);
        Assert.Equal([(0, 0), (8, 8)], Corners(boundary, route));
    }

    [Fact]
    public void A_route_goes_through_a_doorway_the_sparse_search_cannot_see()
    {
        // The gap is one texel wide and two deep, at an odd column. Neither the search
        // that steps four texels at a time nor the one that steps two has a node inside
        // it, so this only comes out if the fallback down to every texel really happens.
        WalkBoundary boundary = Map(
            "................",
            "................",
            "................",
            "................",
            "#####.##########",
            "#####.##########",
            "................",
            "................",
            "................",
            "................");

        WalkRoute route = WalkPath.Find(boundary, boundary.ToWorld(1, 1), boundary.ToWorld(14, 8));

        Assert.True(route.ReachedGoal);

        List<(int X, int Y)> walked = Walked(boundary, route);

        Assert.All(walked, texel => Assert.True(
            boundary.IsTexelWalkable(texel.X, texel.Y),
            $"the route crosses ({texel.X}, {texel.Y}), which is wall"));

        Assert.Contains((5, 4), walked);
        Assert.Contains((5, 5), walked);
    }

    [Fact]
    public void A_route_that_cannot_arrive_gets_as_close_as_it_can()
    {
        WalkBoundary boundary = Map(
            "..........",
            "..........",
            "##########",
            "..........",
            "..........");

        WalkRoute route = WalkPath.Find(boundary, boundary.ToWorld(0, 0), boundary.ToWorld(0, 4));

        Assert.False(route.ReachedGoal);
        Assert.NotEmpty(route.Points);

        // Everything it does walk is on this side of the wall, and it ends up against it.
        Assert.All(Corners(boundary, route), texel => Assert.True(texel.Y < 2));
        Assert.Equal(1, Corners(boundary, route)[^1].Y);
    }

    [Fact]
    public void A_route_keeps_clear_of_the_walls_where_the_room_lets_it()
    {
        // The middle of this corridor is open floor and its edges are all but wall. A
        // route between two points on the near edge should bulge into the middle rather
        // than scrape along the wall it started on.
        WalkBoundary boundary = Map(
            "#########",
            "777777777",
            "000000000",
            "777777777",
            "#########");

        WalkRoute route = WalkPath.Find(boundary, boundary.ToWorld(0, 1), boundary.ToWorld(8, 1));

        Assert.True(route.ReachedGoal);

        (int X, int Y)[] corners = Corners(boundary, route);

        Assert.Equal((0, 1), corners[0]);
        Assert.Equal((8, 1), corners[^1]);
        Assert.Contains(corners[1..^1], texel => boundary.RegionOf(texel.X, texel.Y) == 0);
    }

    [Fact]
    public void The_ends_of_a_route_are_left_exactly_where_they_were_asked_for()
    {
        // Even against a wall: an actor told to stand on a mark stands on the mark, and
        // conditioning that quietly moved it half a metre would be a bug, not a polish.
        WalkBoundary boundary = Map(
            "#########",
            "777777777",
            "000000000",
            "777777777",
            "#########");

        WalkRoute route = WalkPath.Find(boundary, boundary.ToWorld(1, 3), boundary.ToWorld(7, 3));

        Assert.Equal(boundary.ToWorld(1, 3), route.Points[0]);
        Assert.Equal(boundary.ToWorld(7, 3), route.Points[^1]);
    }

    [Fact]
    public void A_walk_that_starts_inside_a_wall_walks_out_of_it()
    {
        WalkBoundary boundary = Map(
            "############",
            "#..........#",
            "#..........#",
            "#..........#",
            "#..........#",
            "#..........#",
            "#..........#",
            "#..........#",
            "#..........#",
            "#..........#",
            "#..........#",
            "############");

        // Off the bitmap entirely, which is where a badly placed actor or a click on the
        // scenery lands.
        WalkRoute route = WalkPath.Find(
            boundary, new Vector3(-500, 0, 55), boundary.ToWorld(10, 9));

        Assert.True(route.ReachedGoal);
        Assert.Equal((1, 6), Corners(boundary, route)[0]);
        Assert.All(Walked(boundary, route), texel => Assert.True(
            boundary.IsTexelWalkable(texel.X, texel.Y)));
    }

    [Fact]
    public void A_goal_a_script_has_shut_off_is_walked_up_to_rather_than_into()
    {
        WalkBoundary boundary = Map(
            "#########",
            "#...D...#",
            "#...D...#",
            "#...D...#",
            "#########");

        Vector3 from = boundary.ToWorld(1, 2);
        Vector3 to = boundary.ToWorld(7, 2);

        Assert.True(WalkPath.Find(boundary, from, to).ReachedGoal);

        // The doorway is a scripted region, so shutting it cuts off the far half of the
        // room without anything about the bitmap changing.
        boundary.SetRegionOpen(200, open: false);
        WalkRoute blocked = WalkPath.Find(boundary, from, to);

        Assert.False(blocked.ReachedGoal);
        Assert.All(Corners(boundary, blocked), texel => Assert.True(texel.X < 4));
    }

    [Fact]
    public void A_boundary_with_nowhere_to_stand_has_no_routes()
    {
        WalkBoundary boundary = Map("###", "###", "###");

        WalkRoute route = WalkPath.Find(boundary, Vector3.Zero, new Vector3(20, 0, 20));

        Assert.Equal(WalkRoute.None, route);
        Assert.True(route.IsEmpty);
        Assert.False(route.ReachedGoal);
    }

    [Fact]
    public void A_walk_to_where_you_already_are_is_one_point_long()
    {
        WalkBoundary boundary = Map(
            ".....",
            ".....",
            ".....",
            ".....",
            ".....");

        WalkRoute route = WalkPath.Find(boundary, boundary.ToWorld(2, 2), boundary.ToWorld(2, 2));

        Assert.True(route.ReachedGoal);
        Assert.Equal([(2, 2)], Corners(boundary, route));
        Assert.Equal(0f, route.Length());
    }

    [Fact]
    public void The_length_of_a_route_is_the_distance_walked()
    {
        WalkBoundary boundary = Map(
            ".....",
            ".....",
            ".....",
            ".....",
            ".....");

        WalkRoute route = WalkPath.Find(boundary, boundary.ToWorld(0, 2), boundary.ToWorld(4, 2));

        // Four texels of ten units each, in a straight line across open floor.
        Assert.Equal(40f, route.Length(), 3);
    }

    [Fact]
    public void The_nearest_place_to_stand_is_where_you_are_when_you_may_stand_there()
    {
        WalkBoundary boundary = Map(
            "#####",
            "#...#",
            "#...#",
            "#...#",
            "#####");

        var inside = new Vector3(23, 0, 27);

        Assert.Equal(inside, boundary.NearestWalkable(inside));
        Assert.Equal(boundary.ToWorld(1, 1), boundary.NearestWalkable(new Vector3(-100, 0, 500)));
        Assert.Null(Map("###").NearestWalkable(Vector3.Zero));
    }

    [Fact]
    public void A_shortcut_is_only_taken_when_the_line_it_stands_for_is_clear()
    {
        // The bug this exists for. A route across this room used to be smoothed into one
        // long diagonal-ish line from the bottom left to the top right, because the test
        // that was supposed to check the line walked to (2,2) first and then straight along
        // the row — which misses the pillar the real line goes through. Gabriel then walked
        // the real line, through the pillar.
        WalkBoundary boundary = Map(
            "#############",
            "#...........#",
            "#...........#",
            "#.....##....#",
            "#.....##....#",
            "#...........#",
            "#...........#",
            "#############");

        WalkRoute route = WalkPath.Find(
            boundary, boundary.ToWorld(1, 6), boundary.ToWorld(11, 1));

        Assert.True(route.ReachedGoal);

        foreach ((int x, int y) in Walked(boundary, route))
        {
            Assert.True(
                boundary.IsTexelWalkable(x, y),
                $"the route walks through ({x}, {y}), which is not open");
        }
    }

    [Fact]
    public void A_diagonal_may_not_squeeze_between_two_walls_meeting_at_a_corner()
    {
        // Two blocks touching at a corner are a wall, whatever the texels say. A line that
        // steps diagonally across the join passes through solid geometry without ever
        // sampling a blocked texel, which is how an actor ends up inside the scenery.
        WalkBoundary boundary = Map(
            "#####",
            "#.#.#",
            "#...#",
            "#.#.#",
            "#####");

        WalkRoute route = WalkPath.Find(
            boundary, boundary.ToWorld(1, 1), boundary.ToWorld(3, 1));

        Assert.True(route.ReachedGoal);

        // Through the middle, not across either corner where the two pillars meet the row.
        Assert.Contains((2, 2), Walked(boundary, route));
    }

    [Fact]
    public void A_clear_room_is_still_smoothed_to_one_straight_line()
    {
        // The other half of the fix: being stricter must not stop the smoothing working.
        // An empty room is one line however tight the corner check is.
        WalkBoundary boundary = Map(
            ".............",
            ".............",
            ".............",
            ".............",
            ".............",
            ".............",
            ".............");

        WalkRoute route = WalkPath.Find(
            boundary, boundary.ToWorld(0, 6), boundary.ToWorld(12, 0));

        Assert.Equal([(0, 6), (12, 0)], Corners(boundary, route));
    }
}
