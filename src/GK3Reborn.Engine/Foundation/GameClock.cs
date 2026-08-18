namespace GK3Reborn.Foundation;

/// <summary>
/// The game's notion of time, kept separate from wall-clock time.
/// </summary>
/// <remarks>
/// Plan/01-architecture.md section 3: simulation time, real time, cinematic time
/// and pause state must be distinguishable, and a headless deterministic mode must
/// advance by explicit ticks so tests never depend on the machine's speed.
/// </remarks>
public sealed class GameClock
{
    private readonly double _fixedStepSeconds;

    /// <summary>Creates a clock with the given fixed simulation step.</summary>
    /// <param name="fixedStepSeconds">Simulation step, in seconds. Must be positive.</param>
    public GameClock(double fixedStepSeconds = 1.0 / 60.0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fixedStepSeconds);
        _fixedStepSeconds = fixedStepSeconds;
    }

    /// <summary>Total simulated time, excluding paused spans.</summary>
    public double SimulationTimeSeconds { get; private set; }

    /// <summary>Total real time observed, including paused spans.</summary>
    public double RealTimeSeconds { get; private set; }

    /// <summary>Number of fixed simulation steps taken.</summary>
    public long Tick { get; private set; }

    /// <summary>Whether simulation time is currently frozen.</summary>
    public bool IsPaused { get; set; }

    /// <summary>The fixed simulation step, in seconds.</summary>
    public double FixedStepSeconds => _fixedStepSeconds;

    /// <summary>
    /// Advances by one fixed step. Deterministic: never reads the system clock.
    /// </summary>
    public void AdvanceFixed()
    {
        RealTimeSeconds += _fixedStepSeconds;
        if (IsPaused)
        {
            return;
        }

        SimulationTimeSeconds += _fixedStepSeconds;
        Tick++;
    }

    /// <summary>Advances by <paramref name="steps"/> fixed steps.</summary>
    public void AdvanceFixed(int steps)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(steps);
        for (int i = 0; i < steps; i++)
        {
            AdvanceFixed();
        }
    }
}
