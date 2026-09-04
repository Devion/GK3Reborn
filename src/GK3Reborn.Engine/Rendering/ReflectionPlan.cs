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
/// <remarks>
/// <para>
/// A plan rather than two loose properties, for the same reason <see cref="OutputPlan"/> is
/// one: it is handed over whole, so nothing can see half of a change, and it is a value, so
/// handing over one that has not changed costs nothing.
/// </para>
/// <para>
/// <b>The two are not the same setting.</b> The strength scales every reflection in the
/// picture, screen-space and planar alike, and is a matter of taste. Whether a floor gets a
/// pass of its own is a matter of what the machine can afford: it is a second draw of the
/// room.
/// </para>
/// </remarks>
public readonly record struct ReflectionPlan(float Strength = 1f, bool PlanarFloors = true)
{
    /// <summary>The strongest a reflection may be made.</summary>
    /// <remarks>
    /// Twice the physical answer. Past that a floor stops reading as stone with a shine on
    /// it and starts reading as water.
    /// </remarks>
    public const float Strongest = 2f;

    /// <summary>What a renderer nobody has told does.</summary>
    public static ReflectionPlan Default => new();

    /// <summary>Nothing reflects anything.</summary>
    /// <remarks>
    /// For a device with no reflection pass and for a run that has asked for none. Nought
    /// strength is the switch as well as the amount: a reflection scaled to nothing is a
    /// reflection nobody can see, and a second flag saying the same thing would be a second
    /// thing to keep in step.
    /// </remarks>
    public static ReflectionPlan None => new(0f, false);

    /// <summary>The same plan with its number inside the range.</summary>
    /// <remarks>
    /// Clamped here rather than where it is set, because this is the type that knows what
    /// the range means. A settings file is a text file somebody may edit.
    /// </remarks>
    public ReflectionPlan Sane() => this with
    {
        Strength = float.IsFinite(Strength) ? Math.Clamp(Strength, 0f, Strongest) : 1f,
    };
}
