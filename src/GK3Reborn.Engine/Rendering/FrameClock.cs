namespace GK3Reborn.Rendering;

/// <summary>The shader and temporal-filter clock of a recorded frame.</summary>
public readonly record struct FrameClock(float Seconds, float DeltaSeconds)
{
    /// <summary>Advances exactly once per recorded frame, regardless of rendering cost.</summary>
    public static FrameClock At(int frame, int fps)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(frame);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fps);
        return new FrameClock((float)frame / fps, 1f / fps);
    }
}
