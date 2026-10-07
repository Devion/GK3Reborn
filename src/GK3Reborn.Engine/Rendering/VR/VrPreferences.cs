namespace GK3Reborn.Rendering.VR;

public enum VrLocomotionMode { Teleport, Smooth, Both, Roomscale }

public sealed record VrPreferences
{
    public bool SmoothTurning { get; init; } = true;
    public float TurnDegreesPerSecond { get; init; } = 75;
    public VrLocomotionMode Locomotion { get; init; } = VrLocomotionMode.Teleport;
    public float MoveMetresPerSecond { get; init; } = 1.25f;
    public float HeightOffsetMetres { get; init; }
    public bool ShowHands { get; init; } = true;

    public VrPreferences Clamped() => this with
    {
        TurnDegreesPerSecond = Finite(TurnDegreesPerSecond, 30, 180, 75),
        MoveMetresPerSecond = Finite(MoveMetresPerSecond, 0.5f, 2.5f, 1.25f),
        HeightOffsetMetres = Finite(HeightOffsetMetres, -0.5f, 1.5f, 0),
        Locomotion = Enum.IsDefined(Locomotion) ? Locomotion : VrLocomotionMode.Teleport,
    };
    private static float Finite(float value, float low, float high, float fallback) =>
        float.IsFinite(value) ? Math.Clamp(value, low, high) : fallback;
}
