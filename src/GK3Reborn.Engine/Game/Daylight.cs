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
/// <remarks>
/// <para>
/// <b>A baker does not care where a light stands; a tracer does.</b> GK3's rooms fake
/// daylight with lights the artists named for the window they belong to — CS3's attic has
/// <c>cs3_turret_window_special_outside</c> at intensity 3 — and they placed them wherever
/// made the lightmap look right. That one stands at <c>y = 632</c>, which is above the
/// roof. Nothing checked in 1999 whether it could see the room. Tracing does, finds the
/// roof, and the attic gets no daylight at all: measured with <c>--no-sun</c>, the interior
/// is identical with the sun and without it, and doubling that light's range changes
/// nothing, because range is not what is stopping it.
/// </para>
/// <para>
/// <b>So the light is moved to the window it is named after</b>, and a little outside it.
/// From there the wall stops most of it and the opening does not, which is what a shaft
/// <em>is</em>: the beam is shaped by the hole rather than by a number. Nothing else about
/// the light changes hands — its colour is the artists' answer to what the daylight outside
/// that room looks like, and their relative strengths say which window matters.
/// </para>
/// <para>
/// <b>Only the misplaced ones.</b> A window light standing inside the room it lights is
/// already where it can do its job — R25's morning sun lays a window's shape across the
/// carpet exactly as it should — and moving it would be fixing something that is not
/// broken. The test is whether the room's own geometry is between the light and the room,
/// which is what <em>outside the box</em> is a cheap and sufficient proxy for.
/// </para>
/// </remarks>
public static class Daylight
{
    /// <summary>The words the artists use for a window, in an object name or a light's.</summary>
    /// <remarks>
    /// Both spellings, because the corpus uses both and often in one room: CS3 has
    /// <c>cs3_wndwfrms01</c> for the frame and <c>turret_window_special</c> for the light.
    /// </remarks>
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
    /// <remarks>
    /// A window is <em>in</em> the wall, so a light the artists put right at one is a few
    /// units outside the box and is not misplaced at all. This is well past that and well
    /// short of the six hundred units CS3's is out by.
    /// </remarks>
    private const float Outside = 60f;

    /// <summary>How far outside its window a moved light is put, as a multiple of the opening.</summary>
    /// <remarks>
    /// Outside rather than in the plane, and that is the whole trick: from outside, the wall
    /// stops the light everywhere but the opening, so the opening shapes it. In the plane it
    /// would light the room from the window like any other lamp, which is what the artists'
    /// own indoor window lights already do.
    /// </remarks>
    private const float StandOff = 1.5f;

    /// <summary>How bright a moved light is allowed to be.</summary>
    /// <remarks>
    /// The artists' number was chosen for a light six hundred units away that reached
    /// nothing; at the window it is a few units from the room and the same number would be
    /// a floodlight. Their <em>relative</em> strengths are kept and the scale is set here.
    /// </remarks>
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
    /// <remarks>
    /// Nearest rather than by name. The names pair up in some rooms and not others —
    /// <c>cs3_turret_window_special_outside</c> and <c>cs3_wndwfrms02</c> share nothing but
    /// the word — and where a room has one window the question does not arise.
    /// </remarks>
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
