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
public sealed class Glances
{
    private readonly Dictionary<string, Glance> _looking = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _remaining = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How far round a head will turn, in radians.</summary>
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
    /// <param name="seconds">
    /// How long for, or zero to hold the look until something cancels it. The scripts say
    /// which they mean: <c>LOOKAT GABRIEL EH 5</c> is a five-second glance, and dropping
    /// the five left Emilio watching Gabriel over his shoulder for the rest of the scene —
    /// through standing up, through the whole of his walk to the hotel door.
    /// </param>
    public void Look(Glance glance, double seconds = 0)
    {
        _looking[glance.Actor] = glance;

        if (seconds > 0)
        {
            _remaining[glance.Actor] = seconds;
        }
        else
        {
            _remaining.Remove(glance.Actor);
        }
    }

    /// <summary>Lets the timed glances run out.</summary>
    /// <param name="seconds">How long has passed.</param>
    public void Tick(double seconds)
    {
        foreach (string actor in _remaining.Keys.ToList())
        {
            double left = _remaining[actor] - seconds;

            if (left > 0)
            {
                _remaining[actor] = left;
            }
            else
            {
                _remaining.Remove(actor);
                _looking.Remove(actor);
            }
        }
    }

    /// <summary>Stops an actor looking at anything.</summary>
    /// <param name="actor">Who to stop.</param>
    /// <returns>True when they were looking at something.</returns>
    public bool Cancel(string actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        _remaining.Remove(actor);
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
    public void Clear()
    {
        _looking.Clear();
        _remaining.Clear();
    }

    /// <summary>
    /// How far a head has to turn to look at something.
    /// </summary>
    /// <param name="standing">Where the actor is.</param>
    /// <param name="facing">Which way their body faces, in radians about the up axis.</param>
    /// <param name="eyes">How far above their feet their head is.</param>
    /// <param name="target">What they are looking at.</param>
    /// <returns>Yaw and pitch for the head, both already clamped to what a neck allows.</returns>
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
