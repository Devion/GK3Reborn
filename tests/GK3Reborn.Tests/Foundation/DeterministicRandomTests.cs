using GK3Reborn.Foundation;
using Xunit;

namespace GK3Reborn.Tests.Foundation;

public sealed class DeterministicRandomTests
{
    [Fact]
    public void Same_seed_produces_the_same_stream()
    {
        var a = new DeterministicRandom(12345);
        var b = new DeterministicRandom(12345);

        for (int i = 0; i < 1000; i++)
        {
            Assert.Equal(a.NextUInt64(), b.NextUInt64());
        }
    }

    [Fact]
    public void Different_seeds_diverge()
    {
        var a = new DeterministicRandom(1);
        var b = new DeterministicRandom(2);
        Assert.NotEqual(a.NextUInt64(), b.NextUInt64());
    }

    [Fact]
    public void State_round_trips_so_saves_can_restore_the_stream()
    {
        var random = new DeterministicRandom(99);
        random.NextUInt64();

        var saved = random.CaptureState();
        ulong expected = random.NextUInt64();

        random.RestoreState(saved);
        Assert.Equal(expected, random.NextUInt64());
    }

    [Fact]
    public void NextDouble_stays_in_range()
    {
        var random = new DeterministicRandom(7);
        for (int i = 0; i < 10_000; i++)
        {
            double value = random.NextDouble();
            Assert.InRange(value, 0.0, 0.9999999999);
        }
    }

    [Fact]
    public void NextInt32_respects_bounds()
    {
        var random = new DeterministicRandom(7);
        for (int i = 0; i < 10_000; i++)
        {
            Assert.InRange(random.NextInt32(-5, 5), -5, 4);
        }
    }

    [Fact]
    public void NextInt32_rejects_an_empty_range() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new DeterministicRandom(1).NextInt32(3, 3));
}
