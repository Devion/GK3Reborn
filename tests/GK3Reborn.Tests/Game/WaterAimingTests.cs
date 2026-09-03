using System.Numerics;
using GK3Reborn.Game;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Tests for the aiming the cut crow's-nest puzzle ends in.
/// </summary>
/// <remarks>
/// One number in it is not a choice — the case in the game's own action file is called
/// <c>ON_NEST_FOR_10_SECONDS</c> — and everything else is, so what is pinned here is that
/// it is a thing to do rather than a thing to wait through, that it is forgiving, and that
/// it does not get easier on a faster machine.
/// </remarks>
public sealed class WaterAimingTests
{
    [Fact]
    public void It_wants_the_ten_seconds_its_own_case_is_named_for()
    {
        Assert.Equal(10f, WaterAiming.SecondsNeeded);
    }

    [Fact]
    public void Water_held_on_the_nest_finishes_and_says_so_once()
    {
        var aiming = new WaterAiming();
        int finished = 0;

        // Fifteen seconds of frames for a ten-second hold, with room for the jet to catch
        // up at the start.
        for (int i = 0; i < 900; i++)
        {
            // Follow the nest, which is what the player is doing.
            aiming.PointAt(aiming.Nest);

            if (aiming.Advance(1f / 60f))
            {
                finished++;
            }
        }

        Assert.True(aiming.Done);
        Assert.Equal(1, finished);
        Assert.Equal(1f, aiming.Progress);
    }

    [Fact]
    public void Water_pointed_somewhere_else_never_finishes()
    {
        var aiming = new WaterAiming();

        for (int i = 0; i < 2000; i++)
        {
            aiming.PointAt(new Vector2(0.95f, 0.95f));
            aiming.Advance(1f / 60f);
        }

        Assert.False(aiming.Done);
        Assert.Equal(0f, aiming.Held);
    }

    [Fact]
    public void A_wobble_costs_a_moment_rather_than_the_attempt()
    {
        // Banked time bleeds at half the rate it fills. Somebody who loses the nest for a
        // second has lost half a second, not the ten they were most of the way through.
        var aiming = new WaterAiming();

        for (int i = 0; i < 300; i++)
        {
            aiming.PointAt(aiming.Nest);
            aiming.Advance(1f / 60f);
        }

        float banked = aiming.Held;
        Assert.True(banked > 3f);

        for (int i = 0; i < 60; i++)
        {
            aiming.PointAt(new Vector2(0.95f, 0.95f));
            aiming.Advance(1f / 60f);
        }

        Assert.True(aiming.Held > banked - 1f);
        Assert.True(aiming.Held < banked);
    }

    [Fact]
    public void The_jet_trails_the_aim_rather_than_arriving_at_it()
    {
        // A hose under pressure does, and without it the whole thing is parking a cursor.
        var aiming = new WaterAiming();

        aiming.PointAt(new Vector2(0.1f, 0.1f));
        aiming.Advance(1f / 60f);

        Assert.NotEqual(aiming.Aim, aiming.Jet);
        Assert.True(Vector2.Distance(aiming.Jet, aiming.Aim) > 0.1f);
    }

    [Fact]
    public void The_hose_is_no_easier_to_hold_on_a_faster_machine()
    {
        // The jet chases by an exponential rather than a fixed fraction, so sixty small
        // steps and six large ones land in the same place.
        var fast = new WaterAiming();
        var slow = new WaterAiming();

        for (int i = 0; i < 60; i++)
        {
            fast.PointAt(new Vector2(0.2f, 0.2f));
            fast.Advance(1f / 60f);
        }

        for (int i = 0; i < 6; i++)
        {
            slow.PointAt(new Vector2(0.2f, 0.2f));
            slow.Advance(1f / 6f);
        }

        Assert.Equal(fast.Jet.X, slow.Jet.X, 3);
        Assert.Equal(fast.Jet.Y, slow.Jet.Y, 3);
    }

    [Fact]
    public void The_nest_never_leaves_the_panel_and_never_stops_moving()
    {
        var aiming = new WaterAiming();
        var seen = new HashSet<(int, int)>();

        for (int i = 0; i < 600; i++)
        {
            aiming.Advance(1f / 30f);

            Assert.InRange(aiming.Nest.X, 0f, 1f);
            Assert.InRange(aiming.Nest.Y, 0f, 1f);

            seen.Add(((int)(aiming.Nest.X * 100), (int)(aiming.Nest.Y * 100)));
        }

        // Two unequal periods, so it wanders rather than retracing one line.
        Assert.True(seen.Count > 20);
    }

    [Fact]
    public void Pointing_outside_the_panel_holds_the_aim_at_the_edge()
    {
        var aiming = new WaterAiming();

        aiming.PointAt(new Vector2(-3f, 12f));

        Assert.Equal(new Vector2(0f, 1f), aiming.Aim);
    }
}
