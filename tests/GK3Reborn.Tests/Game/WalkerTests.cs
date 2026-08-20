using System.Numerics;
using GK3Reborn.Game.Navigation;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Tests for an actor crossing a room.
/// </summary>
/// <remarks>
/// <see cref="WalkPath"/> found routes and nothing moved along them. What matters here is
/// that a walk covers the distance it was given whatever the frame rate does — a slow frame
/// should cost smoothness and never distance — and that an actor turns into a corner rather
/// than snapping round it.
/// </remarks>
public sealed class WalkerTests
{
    private static WalkRoute Route(params Vector3[] points) => new(true, points);

    private static Walker Walking(params Vector3[] points) =>
        new("GABRIEL", Route(points), Vector3.Zero, 0f);

    [Fact]
    public void An_actor_walks_towards_the_first_corner()
    {
        Walker walker = Walking(new Vector3(100, 0, 0));

        Assert.True(walker.Advance(0.5f));
        Assert.Equal(Walker.Speed * 0.5f, walker.Position.X, 2);
        Assert.Equal(0, walker.Position.Z, 3);
    }

    [Fact]
    public void A_walk_ends_when_the_last_corner_is_reached()
    {
        Walker walker = Walking(new Vector3(10, 0, 0));

        Assert.False(walker.Advance(10f));
        Assert.False(walker.Walking);
        Assert.Equal(10, walker.Position.X, 1);
    }

    [Fact]
    public void One_slow_frame_covers_as_much_ground_as_many_quick_ones()
    {
        // A frame long enough to cross several corners must cross them all rather than
        // stopping at the first: dropping a frame should cost smoothness, not distance.
        Walker slow = Walking(new Vector3(30, 0, 0), new Vector3(30, 0, 30), new Vector3(60, 0, 30));
        Walker quick = Walking(new Vector3(30, 0, 0), new Vector3(30, 0, 30), new Vector3(60, 0, 30));

        slow.Advance(1f);

        for (int i = 0; i < 100; i++)
        {
            quick.Advance(0.01f);
        }

        Assert.Equal(slow.Position.X, quick.Position.X, 1);
        Assert.Equal(slow.Position.Z, quick.Position.Z, 1);
    }

    [Fact]
    public void An_actor_turns_into_a_corner_rather_than_snapping_round_it()
    {
        // Facing is a rate, so after a short step it is on its way and not yet arrived.
        var walker = new Walker(
            "GABRIEL", Route(new Vector3(0, 0, -100)), Vector3.Zero, facing: 0f);

        walker.Advance(0.05f);

        Assert.NotEqual(0f, walker.Facing, 3);
        Assert.True(MathF.Abs(walker.Facing) < MathF.PI, "the turn went the long way round");
    }

    [Fact]
    public void Walking_the_other_way_ends_up_facing_the_other_way()
    {
        var walker = new Walker(
            "GABRIEL", Route(new Vector3(0, 0, -100)), Vector3.Zero, facing: 0f);

        walker.Advance(2f);

        Assert.Equal(MathF.PI, MathF.Abs(walker.Facing), 2);
    }

    [Fact]
    public void A_route_that_starts_where_the_actor_is_does_not_waste_a_step()
    {
        // WalkPath's first corner is usually the actor's own position, and walking to where
        // you already are costs a turn towards nothing in particular.
        var walker = new Walker(
            "GABRIEL",
            Route(Vector3.Zero, new Vector3(0, 0, 100)),
            Vector3.Zero,
            facing: 0f);

        walker.Advance(0.1f);

        Assert.True(walker.Position.Z > 0, "the actor did not set off");
    }

    [Fact]
    public void The_length_of_a_walk_is_the_length_of_the_route()
    {
        Walker walker = Walking(new Vector3(0, 0, 65), new Vector3(65, 0, 65));

        Assert.Equal(130f, walker.Remaining, 1);
        Assert.Equal(2.0, walker.Seconds, 2);
    }

    [Fact]
    public void An_actor_sent_to_a_named_spot_arrives_facing_the_way_it_says()
    {
        // A scene's spots each carry a heading — the way somebody standing there is meant
        // to face. Without it an actor arrives facing whichever way the last corner of the
        // route pointed, which is usually a wall.
        var walker = new Walker(
            "GABRIEL",
            Route(new Vector3(0, 0, 100)),
            Vector3.Zero,
            facing: 0f,
            arriveFacing: MathF.PI / 2);

        walker.Advance(5f);

        Assert.False(walker.Walking);
        Assert.Equal(MathF.PI / 2, walker.Facing, 2);
    }

    [Fact]
    public void An_actor_sent_to_look_at_something_faces_it_from_where_they_stop()
    {
        // The heading cannot be worked out in advance: the boundary stops an actor short of
        // anything solid, so a heading aimed from the requested destination points at a
        // place they never reach — and when destination and target are the same point, at
        // nothing at all.
        var walker = new Walker(
            "GABRIEL",
            Route(new Vector3(0, 0, 100)),
            Vector3.Zero,
            facing: 0f,
            arriveLookingAt: new Vector3(50, 0, 100));

        walker.Advance(5f);

        Assert.False(walker.Walking);

        // Due east of where they stopped.
        Assert.Equal(MathF.PI / 2, walker.Facing, 2);
    }

    [Fact]
    public void The_arrival_turn_is_part_of_the_walk_rather_than_after_it()
    {
        // Reporting the walk finished before the turn would let the next thing start while
        // the actor still had their back to it.
        var walker = new Walker(
            "GABRIEL",
            Route(new Vector3(0, 0, 10)),
            Vector3.Zero,
            facing: 0f,
            arriveFacing: MathF.PI);

        // Far enough to cover the ground, nowhere near enough to complete the turn.
        Assert.True(walker.Advance(0.2f), "the walk ended before the turn did");
    }

    [Fact]
    public void An_actor_with_nothing_to_face_keeps_the_way_they_were_going()
    {
        Walker walker = Walking(new Vector3(0, 0, 100));

        walker.Advance(5f);

        Assert.Equal(0f, walker.Facing, 2);
    }

    [Fact]
    public void A_turn_on_the_spot_is_a_walk_with_nowhere_to_go()
    {
        // What TurnToModel is: 394 of the corpus's 3,617 approaches. Walking to the thing
        // instead puts the actor on top of what they meant to look at.
        var walker = new Walker(
            "GABRIEL",
            Route(Vector3.Zero),
            Vector3.Zero,
            facing: 0f,
            arriveFacing: MathF.PI / 2);

        walker.Advance(5f);

        Assert.Equal(Vector3.Zero, walker.Position);
        Assert.Equal(MathF.PI / 2, walker.Facing, 2);
    }

    [Fact]
    public void Stopping_leaves_the_actor_where_they_stand()
    {
        Walker walker = Walking(new Vector3(1000, 0, 0));

        walker.Advance(0.5f);
        Vector3 where = walker.Position;
        walker.Stop();

        Assert.False(walker.Walking);
        Assert.False(walker.Advance(10f));
        Assert.Equal(where, walker.Position);
    }

    [Fact]
    public void An_empty_route_is_not_a_walk()
    {
        Assert.False(Walking().Walking);
    }

    [Fact]
    public void The_transform_puts_the_actor_where_they_are_at_the_size_they_were()
    {
        Walker walker = Walking(new Vector3(100, 0, 0));
        walker.Advance(0.5f);

        Matrix4x4 transform = walker.Transform(2f);

        Assert.Equal(walker.Position, transform.Translation);

        // The scale survives: rebuilding the transform without it resizes the actor the
        // moment they take a step.
        Assert.Equal(2f, new Vector3(transform.M11, transform.M12, transform.M13).Length(), 3);
    }
}
