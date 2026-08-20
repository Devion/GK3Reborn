using System.Numerics;

namespace GK3Reborn.Game.Actors;

/// <summary>What an actor is looking at.</summary>
/// <param name="Actor">Who is looking.</param>
/// <param name="Target">
/// What they are looking at — another actor or a model in the room — or null when the
/// point was given outright.
/// </param>
/// <param name="Point">Where that is, once it is known.</param>
/// <param name="Quick">Whether the head snaps round rather than easing.</param>
public readonly record struct Glance(string Actor, string? Target, Vector3 Point, bool Quick)
{
    /// <inheritdoc/>
    public override string ToString() => $"{Actor} -> {Target ?? "a point"}";
}

/// <summary>
/// Who is looking at what.
/// </summary>
/// <remarks>
/// <para>
/// A glance is the smallest thing that makes a room feel inhabited: somebody turns their
/// head when you come in, or looks at the thing they are talking about. GK3's scripts ask
/// for it constantly — <c>LookitActor</c>, <c>LookitModel</c>, <c>TurnHead</c> — and in
/// the reference implementation every one of those functions is an empty body that returns
/// zero, so none of it has ever happened.
/// </para>
/// <para>
/// It is scene state, not story state. Who somebody is looking at means nothing once the
/// room has changed, and there is no save in the game that records it.
/// </para>
/// </remarks>
public sealed class Glances
{
    private readonly Dictionary<string, Glance> _looking = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How far round a head will turn, in radians.</summary>
    /// <remarks>
    /// Eighty degrees. A person can manage rather more with their shoulders, but a head on
    /// its own that goes past this reads as broken rather than as attentive, and the mesh
    /// has no neck to stretch. Anything further is simply looked at as far as possible.
    /// </remarks>
    public const float YawLimit = 80f * (MathF.PI / 180f);

    /// <summary>How far up or down, in radians.</summary>
    public const float PitchLimit = 35f * (MathF.PI / 180f);

    /// <summary>Everyone who is looking at something, in a stable order.</summary>
    public IReadOnlyList<Glance> All =>
        [.. _looking.Values.OrderBy(g => g.Actor, StringComparer.OrdinalIgnoreCase)];

    /// <summary>How many actors are looking at something.</summary>
    public int Count => _looking.Count;

    /// <summary>Points an actor at something.</summary>
    /// <param name="glance">Who is looking at what.</param>
    /// <remarks>
    /// One at a time: an actor asked to look somewhere else stops looking where they were,
    /// which is what a person does.
    /// </remarks>
    public void Look(Glance glance) => _looking[glance.Actor] = glance;

    /// <summary>Stops an actor looking at anything.</summary>
    /// <param name="actor">Who to stop.</param>
    /// <returns>True when they were looking at something.</returns>
    public bool Cancel(string actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return _looking.Remove(actor);
    }

    /// <summary>What an actor is looking at, if anything.</summary>
    /// <param name="actor">Who to ask about.</param>
    /// <returns>The glance, or null.</returns>
    public Glance? Of(string actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return _looking.TryGetValue(actor, out Glance glance) ? glance : null;
    }

    /// <summary>Stops everyone looking.</summary>
    public void Clear() => _looking.Clear();

    /// <summary>
    /// How far a head has to turn to look at something.
    /// </summary>
    /// <param name="standing">Where the actor is.</param>
    /// <param name="facing">Which way their body faces, in radians about the up axis.</param>
    /// <param name="eyes">How far above their feet their head is.</param>
    /// <param name="target">What they are looking at.</param>
    /// <returns>Yaw and pitch for the head, both already clamped to what a neck allows.</returns>
    /// <remarks>
    /// Relative to the body, because the head is a child of it: an actor already facing the
    /// thing turns their head not at all. Something directly overhead or underfoot gives no
    /// meaningful direction to turn towards, so the head stays where it is.
    /// </remarks>
    public static (float Yaw, float Pitch) Turn(
        Vector3 standing, float facing, float eyes, Vector3 target)
    {
        Vector3 toTarget = target - (standing + new Vector3(0, eyes, 0));

        float flat = MathF.Sqrt((toTarget.X * toTarget.X) + (toTarget.Z * toTarget.Z));

        if (flat < 1e-3f)
        {
            return (0f, 0f);
        }

        // The scene files measure a heading the same way the cameras do: yaw about the up
        // axis, zero along +Z, increasing towards +X.
        float wanted = MathF.Atan2(toTarget.X, toTarget.Z);
        float yaw = Wrap(wanted - facing);
        float pitch = MathF.Atan2(toTarget.Y, flat);

        return (
            Math.Clamp(yaw, -YawLimit, YawLimit),
            Math.Clamp(pitch, -PitchLimit, PitchLimit));
    }

    /// <summary>Brings an angle back into the half-turn either side of straight ahead.</summary>
    /// <remarks>
    /// Without this, something a little to the left of an actor facing north reads as
    /// almost a full turn to the right, and the clamp then holds the head at its limit
    /// facing the wrong way.
    /// </remarks>
    private static float Wrap(float radians)
    {
        while (radians > MathF.PI)
        {
            radians -= 2f * MathF.PI;
        }

        while (radians < -MathF.PI)
        {
            radians += 2f * MathF.PI;
        }

        return radians;
    }
}
