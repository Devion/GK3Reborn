// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Numerics;
using GK3Reborn.Formats.Scenes;
using GK3Reborn.Game.Navigation;

namespace GK3Reborn.Game;

/// <summary>What is flying over a room, and what it is like.</summary>
/// <param name="Birds">How many are up.</param>
/// <param name="Clearance">
/// The least the middle of the wheel will fly above the room's roofline, in world units.
/// A floor rather than the height itself: how high the flock actually goes is measured off
/// the room's own cameras, and this is only what stops that answer putting birds through a
/// roof. Off the roofline rather than off the highest point of the room, because RC1's
/// highest point is the Tour Magdala and the birds are meant to go past it rather than over
/// it. See <see cref="SceneBirds.Over"/>.
/// </param>
/// <param name="Spread">
/// How wide the wheel is, as a fraction of the room's own half-diagonal. A fraction rather
/// than a distance: the same wheel in RC1 and in the cemetery is two thirds of a village and
/// the whole of a walled yard, and it is the fraction that is the same thing in both.
/// </param>
/// <param name="Rise">
/// How far a bird climbs and falls within it, in world units. Absolute, unlike the spread:
/// how high a bird goes is a fact about the bird and not about how big the square is.
/// </param>
/// <param name="Wingspan">Tip to tip, in world units. A character in this game is 70 tall.</param>
/// <param name="Speed">How fast one flies, in world units a second.</param>
/// <param name="Turning">
/// How hard the flock wheels about its own middle, from nought for a loose mill to one for
/// a tight circle. It is the single number that decides whether a flock reads as birds or
/// as insects: real birds over a village go round.
/// </param>
public readonly record struct Flock(
    int Birds, float Clearance, float Spread, float Rise, float Wingspan, float Speed, float Turning)
{
    /// <summary>Nothing is flying.</summary>
    public static Flock None { get; }

    /// <summary>Whether anything is.</summary>
    public bool Any => Birds > 0;
}

/// <summary>Where a room's flock wheels, measured from the room.</summary>
/// <param name="Centre">The middle of the wheel, in world space.</param>
/// <param name="Radius">How far out from it a bird flies, in world units.</param>
/// <param name="Rise">How far above and below it one climbs, in world units.</param>
public readonly record struct BirdWheel(Vector3 Centre, float Radius, float Rise)
{
    /// <summary>No wheel, for a room with no birds or nothing to measure.</summary>
    public static BirdWheel Nowhere { get; }
}

/// <summary>Where a room's own cameras say the sky is.</summary>
/// <param name="At">The point to put the middle of the flock at, in world space.</param>
/// <param name="Eye">
/// How high the cameras that said so are standing. Wanted because the angle a bird is seen
/// at is measured from the eye, and a room whose cameras stand on a tower has an eye a long
/// way off the ground.
/// </param>
public readonly record struct BirdAim(Vector3 At, float Eye);

/// <summary>
/// Which rooms have birds in the sky over them, and what kind.
/// </summary>
public static class SceneBirds
{
    /// <summary>
    /// Swifts round the rooftops: the village, its cemetery and the tower above it.
    /// </summary>
    private static readonly Flock Rooftops = new(
        Birds: 14,
        Clearance: 120f,
        Spread: 0.34f,
        Rise: 120f,
        Wingspan: 30f,
        Speed: 290f,
        Turning: 0.8f);

    /// <summary>
    /// Soaring birds over open country: the tomb, the dig, and the two hilltops.
    /// </summary>
    private static readonly Flock OpenCountry = new(
        Birds: 6,
        Clearance: 100f,
        Spread: 0.55f,
        Rise: 240f,
        Wingspan: 62f,
        Speed: 170f,
        Turning: 0.62f);

    /// <summary>What is flying over a room at a point in the story.</summary>
    /// <param name="scene">The scene's name, as the SIF has it.</param>
    /// <param name="when">
    /// Where the story stands. A caller with no story state gets the daylight answer, for
    /// the reason <see cref="SceneFog.For"/> gives: a room drawn without an hour is drawn
    /// as it is most often seen.
    /// </param>
    /// <returns>The flock, or <see cref="Flock.None"/> for a room or an hour with none.</returns>
    public static Flock For(string? scene, Timeblock? when = null)
    {
        if (scene is null || when is { } hour && !IsDaylight(hour))
        {
            return Flock.None;
        }

        return scene switch
        {
            _ when Named(scene, Village) => Rooftops,
            _ when Named(scene, Country) => OpenCountry,
            _ => Flock.None,
        };
    }

    /// <summary>Whether birds are up at an hour of the story.</summary>
    /// <param name="when">The point in the story.</param>
    /// <returns>True through the day, false in the evening and the small hours.</returns>
    public static bool IsDaylight(Timeblock when)
    {
        int hour = (when.IsAfternoon && when.Hour != 12 ? when.Hour + 12 : when.Hour) % 24;

        return hour is >= 7 and < 18;
    }

    /// <summary>The village rooms, where the birds are swifts.</summary>
    public static IReadOnlyList<string> Village { get; } =
        ["RC1", "RC2", "RC3", "RC4", "MAG", "MA3", "CEM"];

    /// <summary>The open ones, where they are soaring birds.</summary>
    public static IReadOnlyList<string> Country { get; } =
        ["POU", "WOD", "CD1", "MCF", "CSD", "PL2", "LER", "PL4"];

    private static bool Named(string scene, IReadOnlyList<string> rooms)
    {
        for (int i = 0; i < rooms.Count; i++)
        {
            if (scene.Equals(rooms[i], StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Where a room's flock wheels, measured from the room itself.</summary>
    /// <param name="flock">What is flying, from <see cref="For"/>.</param>
    /// <param name="room">The room's geometry, as the BSP has it.</param>
    /// <param name="walkable">Its walk boundary, where it has one.</param>
    /// <param name="cameras">
    /// The room's own fixed cameras, which decide which part of the sky is ever looked at.
    /// See <see cref="Framed"/>, which is what makes the difference between a flock and an
    /// empty sky.
    /// </param>
    /// <returns>
    /// The wheel, or <see cref="BirdWheel.Nowhere"/> when there are no birds or nothing to
    /// measure them against.
    /// </returns>
    public static BirdWheel Over(
        Flock flock,
        BspFile? room,
        WalkBoundary? walkable,
        IReadOnlyList<SceneCamera>? cameras = null)
    {
        if (!flock.Any || room is not { Vertices.Length: > 0 })
        {
            return BirdWheel.Nowhere;
        }

        Vector3 low = room.Vertices[0];
        Vector3 high = low;

        foreach (Vector3 corner in room.Vertices)
        {
            low = Vector3.Min(low, corner);
            high = Vector3.Max(high, corner);
        }

        // How far a bird may get from the middle. Off the room's own half-diagonal, so that
        // the wheel is the same shape in a village and in a walled yard, and never smaller
        // than a flock can actually fly in: fourteen birds three wingspans apart need room.
        var across = new Vector2(high.X - low.X, high.Z - low.Z);

        float reach = MathF.Max(
            across.Length() * 0.5f * flock.Spread, flock.Wingspan * flock.Birds * 1.5f);

        // Where the room is looking, and how high above the roofs the flock has to be to
        // be over them rather than among them. The two argue, and the way they are settled
        // is the whole of what follows.
        BirdAim? aimed = Framed(cameras, reach);

        if (aimed is not { } first)
        {
            Vector3 anywhere = Middle(walkable) ?? ((low + high) / 2f);

            return new BirdWheel(
                new Vector3(
                    anywhere.X,
                    Roofline(room, anywhere, reach) + flock.Clearance,
                    anywhere.Z),
                reach,
                flock.Rise);
        }

        float floor = Roofline(room, first.At, reach) + flock.Clearance;

        // **Lifting the flock without moving it out puts it straight overhead.** The aim
        // above stands the birds a fixed part of the way up the frame, which is the right
        // angle; raising them to clear a roof and leaving them where they were turns that
        // angle into whatever it happens to become — measured, seven times the height of
        // the frame over the Tour Magdala's square, and nothing in the shot at all. So the
        // wheel is pushed out by however much the roofs pushed the flock up, and the angle
        // is what is preserved.
        if (floor > first.At.Y)
        {
            float wanted = MathF.Max(floor - first.Eye, 1f);
            float had = MathF.Max(first.At.Y - first.Eye, 1f);

            // And no further than seven tenths again. The angle is what this is trying to
            // hold, but distance is what it costs: at two and a half times, RC1's flock
            // cleared the Tour Magdala from two thousand six hundred units away and every
            // bird in it was six pixels of haze. Seventeen hundred is twelve pixels and
            // twenty-two degrees up, which is in the shot and legible in it, and that is
            // the trade the whole of this is for.
            reach *= Math.Clamp(wanted / had, 1f, 1.7f);
        }

        BirdAim aim = Framed(cameras, reach) ?? first;
        float over = Roofline(room, aim.At, reach) + flock.Clearance;

        return new BirdWheel(
            new Vector3(aim.At.X, MathF.Max(aim.At.Y, over), aim.At.Z),
            reach,
            flock.Rise);
    }

    /// <summary>Half the vertical angle a GK3 camera sees, in radians.</summary>
    private const float HalfView = MathF.PI / 6f;

    /// <summary>How far up the frame the flock is aimed, as a fraction of that.</summary>
    private const float Lift = 0.35f;

    /// <summary>The part of the sky a room's own cameras are pointed at.</summary>
    /// <param name="cameras">The room's cameras, from the scene file.</param>
    /// <param name="reach">How far in front of them to measure, in world units.</param>
    /// <returns>Where to put the flock, or null where the room names no cameras.</returns>
    public static BirdAim? Framed(IReadOnlyList<SceneCamera>? cameras, float reach)
    {
        if (cameras is not { Count: > 0 })
        {
            return null;
        }

        var total = Vector3.Zero;
        float eye = 0f;
        float counted = 0f;

        foreach (SceneCamera camera in cameras)
        {
            Vector3 forward = camera.Forward;
            var flat = new Vector3(forward.X, 0f, forward.Z);

            if (flat.LengthSquared() < 1e-6f)
            {
                continue;
            }

            // And how high, so that the flock lands in the sky each shot actually
            // contains rather than above the top of it. A camera pointed at the ground has
            // its sky in a band just under its top edge, and that is where this puts them.
            float elevation = MathF.Atan2(forward.Y, flat.Length()) + (HalfView * Lift);

            // The shot the room opens on counts for three of the others. It is the one
            // every player sees and the only one some of them ever see, and at Poussin's
            // tomb the plain average of four cameras pointing four ways came back to a
            // patch of sky the arrival shot has its back to.
            float weight = camera.IsDefault ? 3f : 1f;

            total += weight * (camera.Position
                + (Vector3.Normalize(flat) * reach)
                + (Vector3.UnitY * reach * MathF.Tan(Math.Clamp(elevation, -0.6f, 0.9f))));

            eye += weight * camera.Position.Y;
            counted += weight;
        }

        return counted > 0f ? new BirdAim(total / counted, eye / counted) : null;
    }

    /// <summary>How high the roofs stand under a point in the sky.</summary>
    /// <param name="room">The room's geometry.</param>
    /// <param name="over">The middle of the wheel, in world space; only X and Z are read.</param>
    /// <param name="reach">How far out from it the flock flies, in world units.</param>
    /// <returns>The height a bird has to be above to be clear of the buildings there.</returns>
    public static float Roofline(BspFile? room, Vector3 over, float reach)
    {
        if (room is not { Vertices.Length: > 0 })
        {
            return 0f;
        }

        float square = reach * reach;
        var under = new List<float>(room.Vertices.Length / 4);

        foreach (Vector3 corner in room.Vertices)
        {
            float x = corner.X - over.X;
            float z = corner.Z - over.Z;

            if ((x * x) + (z * z) <= square)
            {
                under.Add(corner.Y);
            }
        }

        // Nothing beneath it — the flock is out over a valley — so there is nothing to
        // clear and the room's own skyline is the honest fallback.
        return Percentile(under.Count > 64 ? under : Heights(room), 0.995f);
    }

    /// <summary>Every corner's height, for a flock with nothing under it.</summary>
    private static List<float> Heights(BspFile room)
    {
        var all = new List<float>(room.Vertices.Length);

        foreach (Vector3 corner in room.Vertices)
        {
            all.Add(corner.Y);
        }

        return all;
    }

    private static float Percentile(List<float> heights, float at)
    {
        if (heights.Count == 0)
        {
            return 0f;
        }

        heights.Sort();

        return heights[Math.Min(heights.Count - 1, (int)(heights.Count * at))];
    }

    /// <summary>The middle of the ground the player can walk on.</summary>
    /// <param name="walkable">The room's walk boundary, or null.</param>
    /// <returns>The point, with its height left at nought, or null where there is none.</returns>
    public static Vector3? Middle(WalkBoundary? walkable)
    {
        if (walkable is null)
        {
            return null;
        }

        var total = Vector3.Zero;
        int open = 0;

        for (int y = 0; y < walkable.Height; y++)
        {
            for (int x = 0; x < walkable.Width; x++)
            {
                if (!walkable.IsTexelWalkable(x, y))
                {
                    continue;
                }

                total += walkable.ToWorld(x, y);
                open++;
            }
        }

        return open > 0 ? total / open : null;
    }
}
