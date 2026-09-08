// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Numerics;
using GK3Reborn.Formats.Scenes;
using GK3Reborn.Rendering;
using GK3Reborn.Rendering.Geometry;

namespace GK3Reborn.Game;

/// <summary>One of a room's windows, as the geometry has it.</summary>
/// <param name="Owner">What the room calls it.</param>
/// <param name="Centre">The middle of it, in world space.</param>
/// <param name="Radius">Half its diagonal: how big an opening it is.</param>
public readonly record struct Window(string Owner, Vector3 Centre, float Radius);

/// <summary>
/// Puts a room's daylight at its windows.
/// </summary>
public static class Daylight
{
    /// <summary>The words the artists use for a window, in an object name or a light's.</summary>
    private static readonly string[] Named = ["window", "wndw"];

    /// <summary>Whether a name is about a window.</summary>
    /// <param name="name">An object's name or a light's.</param>
    /// <returns>True when it says window either way round.</returns>
    public static bool IsWindow(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        foreach (string word in Named)
        {
            if (name.Contains(word, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// How far outside the room a light has to stand to count as misplaced.
    /// </summary>
    private const float Outside = 60f;

    /// <summary>How far outside its window a moved light is put, as a multiple of the opening.</summary>
    private const float StandOff = 1.5f;

    /// <summary>How bright a moved light is allowed to be.</summary>
    private const float Brightest = 1.6f;

    /// <summary>
    /// Moves a room's misplaced daylight to its windows.
    /// </summary>
    /// <param name="rig">The room's lights.</param>
    /// <param name="windows">The window objects the room's geometry has.</param>
    /// <param name="room">What the room occupies.</param>
    /// <param name="moved">How many lights were moved.</param>
    /// <returns>The rig, with its daylight standing where the daylight comes in.</returns>
    public static IReadOnlyList<AuthoredLight> Rig(
        IReadOnlyList<AuthoredLight> rig,
        IReadOnlyList<Window> windows,
        SceneExtent room,
        out int moved)
    {
        ArgumentNullException.ThrowIfNull(rig);
        ArgumentNullException.ThrowIfNull(windows);

        moved = 0;

        if (windows.Count == 0)
        {
            return rig;
        }

        var balanced = new List<AuthoredLight>(rig.Count);

        // Their relative strengths are kept, so the scale is taken from the brightest of
        // them rather than applied to each on its own.
        float strongest = 0;

        foreach (AuthoredLight light in rig)
        {
            if (Misplaced(light, room))
            {
                strongest = MathF.Max(strongest, light.Intensity);
            }
        }

        Vector3 middle = (room.Minimum + room.Maximum) / 2f;
        float across = Vector3.Distance(room.Minimum, room.Maximum);

        foreach (AuthoredLight light in rig)
        {
            if (!Misplaced(light, room))
            {
                balanced.Add(light);
                continue;
            }

            balanced.Add(At(Nearest(windows, light.Position), light, middle, across, strongest));
            moved++;
        }

        return balanced;
    }

    /// <summary>Whether a light is daylight the artists put where it cannot reach.</summary>
    private static bool Misplaced(AuthoredLight light, SceneExtent room) =>
        IsWindow(light.Name) &&
        Vector3.Distance(light.Position, Vector3.Clamp(light.Position, room.Minimum, room.Maximum))
            > Outside;

    /// <summary>Which window a light belongs to: the one it is nearest.</summary>
    private static Window Nearest(IReadOnlyList<Window> windows, Vector3 from)
    {
        Window best = windows[0];
        float least = float.MaxValue;

        foreach (Window window in windows)
        {
            float distance = Vector3.DistanceSquared(window.Centre, from);

            if (distance < least)
            {
                least = distance;
                best = window;
            }
        }

        return best;
    }

    /// <summary>The same light, standing outside the window it is named for.</summary>
    private static AuthoredLight At(
        Window window, AuthoredLight light, Vector3 middle, float across, float strongest)
    {
        // Which way is out. Away from the middle of the room, which for a window in a wall
        // is through the wall — and a window in the middle of a room is not a thing.
        Vector3 outward = window.Centre - middle;

        outward = outward.LengthSquared() > 0.001f
            ? Vector3.Normalize(outward)
            : Vector3.UnitY;

        float standOff = MathF.Max(window.Radius * StandOff, 12f);

        return light with
        {
            Position = window.Centre + (outward * standOff),
            Direction = -outward,

            // Across the room and no further. It has to carry the width of what it is
            // lighting; past that it is lighting the room beyond, which in a shared asset
            // is a real room.
            AttenuationStart = standOff,
            AttenuationEnd = standOff + across,
            UsesAttenuation = true,

            // The shaft is the wall's shadow with a hole in it, so this must be traced or
            // there is no shape at all — only a lamp hanging outside a window.
            CastsShadows = true,

            Intensity = strongest > 0.0001f
                ? Brightest * (light.Intensity / strongest)
                : light.Intensity,

            // The opening's own size, which is what the soft-shadow sampling jitters across:
            // a window casts a soft-edged shaft and a point source casts a hard one.
            Radius = MathF.Max(window.Radius * 0.5f, 4f),
        };
    }
}
