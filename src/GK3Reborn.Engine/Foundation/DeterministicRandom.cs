namespace GK3Reborn.Foundation;

/// <summary>
/// The engine's only source of randomness: explicitly seeded and reproducible.
/// </summary>
/// <remarks>
/// <para>
/// Plan/04-execution-and-quality.md P4: GK3Reborn deliberately does NOT reproduce
/// GEngine's random streams. GEngine seeds <c>std::default_random_engine</c> from
/// the wall clock, declares it <c>static</c> in a header (so every translation unit
/// gets its own generator), and uses <c>std::uniform_*_distribution</c>, whose
/// output is implementation-defined. That stream is unreproducible even against
/// itself, and it is not the 1999 executable's stream either.
/// </para>
/// <para>
/// Instead: one documented algorithm (xoshiro256++), one explicit seed, saved and
/// restored with the game state. Differential tests compare RNG-dependent outcomes
/// as equivalence classes, not exact streams.
/// </para>
/// </remarks>
public sealed class DeterministicRandom
{
    private ulong _s0, _s1, _s2, _s3;

    /// <summary>Creates a generator from a 64-bit seed.</summary>
    public DeterministicRandom(ulong seed)
    {
        Seed = seed;

        // SplitMix64 expansion, the standard way to seed xoshiro from one word.
        ulong z = seed;
        _s0 = NextSplitMix(ref z);
        _s1 = NextSplitMix(ref z);
        _s2 = NextSplitMix(ref z);
        _s3 = NextSplitMix(ref z);
    }

    /// <summary>The seed this generator was created from.</summary>
    public ulong Seed { get; }

    /// <summary>Returns the next raw 64-bit value.</summary>
    public ulong NextUInt64()
    {
        ulong result = System.Numerics.BitOperations.RotateLeft(_s0 + _s3, 23) + _s0;
        ulong t = _s1 << 17;

        _s2 ^= _s0;
        _s3 ^= _s1;
        _s1 ^= _s2;
        _s0 ^= _s3;
        _s2 ^= t;
        _s3 = System.Numerics.BitOperations.RotateLeft(_s3, 45);

        return result;
    }

    /// <summary>Returns a value in [0, 1).</summary>
    public double NextDouble() => (NextUInt64() >> 11) * (1.0 / (1UL << 53));

    /// <summary>Returns a value in [minInclusive, maxExclusive).</summary>
    public int NextInt32(int minInclusive, int maxExclusive)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(minInclusive, maxExclusive);
        ulong range = (ulong)((long)maxExclusive - minInclusive);
        return (int)(minInclusive + (long)(NextUInt64() % range));
    }

    /// <summary>Captures the full generator state so it can be saved.</summary>
    public (ulong S0, ulong S1, ulong S2, ulong S3) CaptureState() => (_s0, _s1, _s2, _s3);

    /// <summary>Restores a previously captured state.</summary>
    public void RestoreState((ulong S0, ulong S1, ulong S2, ulong S3) state) =>
        (_s0, _s1, _s2, _s3) = state;

    private static ulong NextSplitMix(ref ulong z)
    {
        z += 0x9E3779B97F4A7C15UL;
        ulong r = z;
        r = (r ^ (r >> 30)) * 0xBF58476D1CE4E5B9UL;
        r = (r ^ (r >> 27)) * 0x94D049BB133111EBUL;
        return r ^ (r >> 31);
    }
}
