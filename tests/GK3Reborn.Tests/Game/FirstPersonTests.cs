using System.Numerics;
using GK3Reborn.Game.Navigation;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Tests for the player walking a room themselves.
/// </summary>
public sealed class FirstPersonTests
{
    /// <summary>Forward, back, left, right, with no stick and no turn.</summary>
    private static FirstPersonInput Walk(float right, float ahead, bool fast = false) =>
        new(new Vector2(right, ahead), Vector2.Zero, fast);

    /// <summary>A player standing at the origin looking along positive Z.</summary>
    private static FirstPerson Standing() => new();

    [Fact]
    public void Walking_forward_goes_where_the_player_is_looking()
    {
        FirstPerson player = Standing();

        player.Turn(new Vector2(MathF.PI / 2f, 0f));
        player.Advance(Walk(0f, 1f), 1f);

        // A quarter turn from looking along +Z is looking along +X.
        Assert.Equal(FirstPerson.Pace, player.Position.X, 1f);
        Assert.Equal(0f, player.Position.Z, 1f);
    }

    [Fact]
    public void Looking_up_and_down_stops_short_of_straight_up()
    {
        FirstPerson player = Standing();

        player.Turn(new Vector2(0f, 10f));

        Assert.InRange(player.Pitch, 1.5f, MathF.PI / 2f);

        player.Turn(new Vector2(0f, -20f));

        Assert.InRange(player.Pitch, -MathF.PI / 2f, -1.5f);
    }

    [Fact]
    public void Tilting_the_view_does_not_walk_into_the_floor()
    {
        FirstPerson player = Standing();

        player.Turn(new Vector2(0f, -1.2f));
        player.Advance(Walk(0f, 1f), 1f);

        Assert.Equal(0f, player.Position.Y, 3);
        Assert.Equal(FirstPerson.Pace, player.Position.Z, 1f);
    }

    [Fact]
    public void A_diagonal_is_no_faster_than_a_straight_line()
    {
        FirstPerson straight = Standing();
        FirstPerson corner = Standing();

        straight.Advance(Walk(0f, 1f), 1f);
        corner.Advance(Walk(1f, 1f), 1f);

        Assert.Equal(
            straight.Position.Length(), corner.Position.Length(), 1f);
    }

    [Fact]
    public void Half_a_push_asks_for_half_the_pace()
    {
        FirstPerson player = Standing();

        player.Advance(Walk(0f, 0.5f), 1f);

        Assert.Equal(FirstPerson.Pace / 2f, player.Position.Z, 1f);
    }

    [Fact]
    public void Hurrying_covers_more_ground()
    {
        FirstPerson player = Standing();

        player.Advance(Walk(0f, 1f, fast: true), 1f);

        Assert.Equal(FirstPerson.Pace * FirstPerson.Sprint, player.Position.Z, 1f);
    }

    [Fact]
    public void The_boundary_refuses_a_step_into_a_wall()
    {
        var player = new FirstPerson { CanStand = at => at.Z < 100f };

        player.Advance(Walk(0f, 1f), 1f);

        Assert.InRange(player.Position.Z, 0f, 100f);
    }

    [Fact]
    public void Walking_at_a_wall_slides_along_it()
    {
        // A wall across the room at Z = 50, walked into at forty-five degrees. The step
        // into it is refused; the step along it is not, so the player ends up sideways of
        // where they started rather than stopped dead in front of it.
        var player = new FirstPerson { CanStand = at => at.Z < 50f };

        player.Advance(Walk(1f, 1f), 1f);

        Assert.InRange(player.Position.Z, 0f, 50f);
        Assert.True(
            player.Position.X > FirstPerson.Pace * 0.5f,
            $"expected to have slid along the wall, got X = {player.Position.X}");
    }

    [Fact]
    public void Being_refused_is_reported_and_walking_freely_is_not()
    {
        var stopped = new FirstPerson { CanStand = at => at.Z < 10f };
        FirstPerson free = Standing();

        Assert.True(stopped.Advance(Walk(0f, 1f), 1f).Blocked);
        Assert.False(free.Advance(Walk(0f, 1f), 1f).Blocked);
    }

    [Fact]
    public void Standing_still_is_not_being_blocked()
    {
        var player = new FirstPerson { CanStand = _ => false };

        FirstPersonStep went = player.Advance(Walk(0f, 0f), 1f);

        Assert.False(went.Blocked);
        Assert.Equal(0f, went.Travelled, 3);
    }

    [Fact]
    public void One_slow_frame_is_stopped_by_the_same_wall_as_many_quick_ones()
    {
        // A one-texel wall, which a step longer than the texel could straddle: open floor
        // on both sides of it. Dropping a frame must cost smoothness, not a wall.
        static bool Open(Vector3 at) => at.Z < 40f || at.Z > 60f;

        var slow = new FirstPerson { CanStand = Open };
        var quick = new FirstPerson { CanStand = Open };

        slow.Advance(Walk(0f, 1f), 1f);

        for (int i = 0; i < 60; i++)
        {
            quick.Advance(Walk(0f, 1f), 1f / 60f);
        }

        // Both stop at the wall rather than through it, and both get within a sub-step of
        // it. The last couple of units differ with the frame length and are not the point;
        // crossing a wall that is thinner than one frame's travel would be.
        Assert.InRange(slow.Position.Z, 35f, 40f);
        Assert.InRange(quick.Position.Z, 35f, 40f);
    }

    [Fact]
    public void The_floor_decides_how_high_the_player_stands()
    {
        var player = new FirstPerson { Ground = at => at.Z * 0.1f };

        player.Advance(Walk(0f, 1f), 0.5f);

        Assert.Equal(player.Position.Z * 0.1f, player.Position.Y, 2);
    }

    [Fact]
    public void Ground_that_rises_more_than_a_stair_is_a_wall()
    {
        var player = new FirstPerson { Ground = at => at.Z < 30f ? 0f : 400f };

        player.Advance(Walk(0f, 1f), 1f);

        Assert.InRange(player.Position.Z, 0f, 30f);
        Assert.Equal(0f, player.Position.Y, 2);
    }

    [Fact]
    public void The_eyes_are_above_the_feet()
    {
        var player = new FirstPerson { Position = new Vector3(10f, 5f, 20f) };

        Assert.Equal(new Vector3(10f, 77f, 20f), player.Eye(72f));
    }

    [Fact]
    public void A_foot_lands_every_stride()
    {
        FirstPerson player = Standing();

        player.Advance(Walk(0f, 1f), FirstPerson.Stride / FirstPerson.Pace * 0.9f);

        Assert.False(player.Footfall(), "not a whole stride yet");

        player.Advance(Walk(0f, 1f), FirstPerson.Stride / FirstPerson.Pace * 0.2f);

        Assert.True(player.Footfall());
        Assert.False(player.Footfall(), "the stride was taken off the tally");
    }

    [Fact]
    public void A_long_frame_does_not_swallow_a_stride()
    {
        FirstPerson player = Standing();

        player.Advance(Walk(0f, 1f), FirstPerson.Stride * 2f / FirstPerson.Pace);

        Assert.True(player.Footfall());
        Assert.True(player.Footfall(), "two strides were walked, so two feet landed");
        Assert.False(player.Footfall());
    }

    [Fact]
    public void A_stick_inside_the_dead_zone_asks_for_nothing()
    {
        Assert.Equal(Vector2.Zero, FirstPerson.Pushed(new Vector2(FirstPerson.DeadZone * 0.9f, 0f)));
    }

    [Fact]
    public void A_stick_just_past_the_dead_zone_asks_for_a_little()
    {
        Vector2 pushed = FirstPerson.Pushed(new Vector2(FirstPerson.DeadZone + 0.02f, 0f));

        Assert.InRange(pushed.X, 0.001f, 0.1f);
    }

    [Fact]
    public void A_stick_pushed_all_the_way_asks_for_all_of_it()
    {
        Vector2 pushed = FirstPerson.Pushed(new Vector2(0f, -1f));

        Assert.Equal(1f, pushed.Length(), 3);
        Assert.Equal(-1f, pushed.Y, 3);
    }

    [Fact]
    public void The_view_comes_back_to_the_eyes_rather_than_arriving_in_them()
    {
        var player = new FirstPerson { Position = new Vector3(0f, 0f, 100f), Yaw = 0f };

        player.ReturnFrom(new Vector3(500f, 200f, 500f), 1f, 0.3f);

        Assert.True(player.Returning);

        (Vector3 started, _, _) = player.Shot(72f, 0f);

        Assert.Equal(500f, started.X, 1f);

        player.Shot(72f, FirstPerson.ReturnSeconds / 2f);

        (Vector3 halfway, _, _) = player.Shot(72f, 0f);

        Assert.InRange(halfway.X, 1f, 499f);
        Assert.True(player.Returning, "still on its way");

        player.Shot(72f, FirstPerson.ReturnSeconds);

        (Vector3 ended, float yaw, float pitch) = player.Shot(72f, 0f);

        Assert.False(player.Returning);
        Assert.Equal(player.Eye(72f), ended);
        Assert.Equal(0f, yaw, 3);
        Assert.Equal(0f, pitch, 3);
    }

    [Fact]
    public void The_view_turns_back_the_short_way_round()
    {
        // Half a turn and a little more apart. Interpolating the numbers would swing the
        // room most of the way round; the short way is a few degrees.
        var player = new FirstPerson { Yaw = 3f };

        player.ReturnFrom(Vector3.Zero, -3f, 0f);
        player.Shot(0f, FirstPerson.ReturnSeconds / 2f);

        (_, float yaw, _) = player.Shot(0f, 0f);

        Assert.True(
            MathF.Abs(yaw) > 3f || MathF.Abs(yaw) <= MathF.PI,
            $"the turn should not have crossed zero, got {yaw}");

        Assert.True(Walker.Wrapped(yaw - 3f) is > -0.7f and < 0.7f, $"got {yaw}");
    }

    [Fact]
    public void A_view_nobody_has_taken_away_is_the_eyes_themselves()
    {
        var player = new FirstPerson { Position = new Vector3(3f, 0f, 4f), Yaw = 0.5f, Pitch = -0.2f };

        (Vector3 eye, float yaw, float pitch) = player.Shot(72f, 1f / 60f);

        Assert.False(player.Returning);
        Assert.Equal(player.Eye(72f), eye);
        Assert.Equal(0.5f, yaw, 3);
        Assert.Equal(-0.2f, pitch, 3);
    }
}
