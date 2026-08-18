using GK3Reborn.Foundation;
using Xunit;

namespace GK3Reborn.Tests.Foundation;

public sealed class GameClockTests
{
    [Fact]
    public void Fixed_steps_accumulate_deterministically()
    {
        var clock = new GameClock(1.0 / 60.0);
        clock.AdvanceFixed(60);

        Assert.Equal(60, clock.Tick);
        Assert.Equal(1.0, clock.SimulationTimeSeconds, 9);
    }

    [Fact]
    public void Pausing_freezes_simulation_time_but_not_real_time()
    {
        var clock = new GameClock(0.5) { IsPaused = true };
        clock.AdvanceFixed(4);

        Assert.Equal(0, clock.Tick);
        Assert.Equal(0.0, clock.SimulationTimeSeconds);
        Assert.Equal(2.0, clock.RealTimeSeconds, 9);
    }

    [Fact]
    public void Rejects_a_non_positive_step() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new GameClock(0));
}
