namespace GK3Reborn.Sheep;

/// <summary>A timed call in one script wait block, including the work its clock drives.</summary>
internal sealed class SheepWait(string name, double seconds)
{
    internal string Name { get; } = name;
    internal double Seconds { get; set; } = seconds;
    internal SheepWaitWork? Work { get; set; }
    internal object? Owner { get; set; }
}

/// <summary>Playback attached to a script call, which can advance with skipped dialogue.</summary>
/// <param name="Advance">Advances this playback only, applying its authored events and final pose.</param>
/// <param name="Speaks">Whether this playback contains further dialogue that must not be skipped wholesale.</param>
public sealed record SheepWaitWork(Action<double> Advance, bool Speaks = false);
