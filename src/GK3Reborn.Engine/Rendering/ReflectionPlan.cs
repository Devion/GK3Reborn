namespace GK3Reborn.Rendering;

/// <summary>
/// What the renderer should do about reflections.
/// </summary>
/// <param name="Strength">
/// How much of a reflection to show, where one is the physical answer. See
/// <see cref="Sane"/> for the range.
/// </param>
/// <param name="PlanarFloors">
/// Whether a large flat polished floor is given a rendered reflection of its own rather
/// than being left to the screen-space march.
/// </param>
public readonly record struct ReflectionPlan(float Strength = 1f, bool PlanarFloors = true)
{
    /// <summary>The strongest a reflection may be made.</summary>
    public const float Strongest = 2f;

    /// <summary>What a renderer nobody has told does.</summary>
    public static ReflectionPlan Default => new();

    /// <summary>Nothing reflects anything.</summary>
    public static ReflectionPlan None => new(0f, false);

    /// <summary>The same plan with its number inside the range.</summary>
    public ReflectionPlan Sane() => this with
    {
        Strength = float.IsFinite(Strength) ? Math.Clamp(Strength, 0f, Strongest) : 1f,
    };
}
