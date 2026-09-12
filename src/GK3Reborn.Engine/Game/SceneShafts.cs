// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Numerics;
using GK3Reborn.Formats.Scenes;
using GK3Reborn.Game.Navigation;
using GK3Reborn.Rendering;

namespace GK3Reborn.Game;

/// <summary>
/// Which of a room's windows the daylight comes in at, and as what.
/// </summary>
public static class SceneShafts
{
    /// <summary>How brightly the sun's own shaft is drawn.</summary>
    private const float Direct = 1f;

    /// <summary>How brightly a window the sun does not reach draws its diffuse light.</summary>
    private const float Diffuse = 0.6f;

    /// <summary>How steeply skylight comes down through a window the sun does not reach, in radians.</summary>
    private const float Skyward = 0.62f;

    /// <summary>The smallest opening that draws a shaft, in world units across.</summary>
    private const float SmallestPane = 12f;

    /// <summary>How thin a window's box is along its normal, at most, as a fraction of its face.</summary>
    private const float Flatness = 0.5f;

    /// <summary>
    /// The other words the artists use for an opening the daylight comes in at. The church's
    /// windows are <c>chu_roundstained</c>, which <see cref="Daylight.IsWindow"/> does not
    /// know, and should not: no light is named for them.
    /// </summary>
    private static readonly string[] Glazed = ["stained", "glass"];

    /// <summary>Whether an object is one the daylight comes in at.</summary>
    /// <param name="name">The object's name.</param>
    /// <returns>True for a window by any of the artists' words for one.</returns>
    public static bool IsPane(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (Daylight.IsWindow(name))
        {
            return true;
        }

        foreach (string word in Glazed)
        {
            if (name.Contains(word, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The shafts of a room's windows.</summary>
    /// <param name="windows">Each window object's name and world-space box.</param>
    /// <param name="sun">The room's sun, from <see cref="Sunlight"/>, or null for none.</param>
    /// <param name="room">The room's corners, which decide which way is inward.</param>
    /// <param name="floor">How high the floor is under a point, or null for the room's lowest corner.</param>
    /// <returns>One shaft per window that has one; empty with no sun.</returns>
    public static IReadOnlyList<LightShaft> For(
        IReadOnlyList<(string Name, Vector3 Minimum, Vector3 Maximum)> windows,
        AuthoredLight? sun,
        (Vector3 Minimum, Vector3 Maximum) room,
        Func<Vector3, float?>? floor)
    {
        ArgumentNullException.ThrowIfNull(windows);

        if (sun is null || sun.Direction.LengthSquared() < 1e-6f)
        {
            return [];
        }

        Vector3 travelling = Vector3.Normalize(sun.Direction);
        Vector3 middle = (room.Minimum + room.Maximum) / 2f;
        var found = new List<LightShaft>();

        foreach ((string name, Vector3 low, Vector3 high) in windows)
        {
            Vector3 size = high - low;
            Vector3 centre = (low + high) / 2f;

            // A pane is a rectangle standing in a wall: thin one way and wide the other two.
            // A box as deep as it is wide is a window seat, a dormer or a whole room named
            // for its window, and none of those has a plane to send a shaft through.
            bool thinX = size.X <= size.Z * Flatness && size.X <= size.Y * Flatness;
            bool thinZ = size.Z <= size.X * Flatness && size.Z <= size.Y * Flatness;

            if (!thinX && !thinZ)
            {
                continue;
            }

            Vector3 normal = thinX ? Vector3.UnitX : Vector3.UnitZ;

            // Inward is toward the middle of the room.
            if (Vector3.Dot(middle - centre, normal) < 0f)
            {
                normal = -normal;
            }

            float halfWidth = (thinX ? size.Z : size.X) / 2f;
            float halfHeight = size.Y / 2f;

            if (halfWidth * 2f < SmallestPane || halfHeight * 2f < SmallestPane)
            {
                continue;
            }

            // The sun's own light where it reaches this wall; otherwise the sky's, which
            // comes in at whatever angle a window lets it and is drawn as if from a little
            // above the horizon.
            float facing = Vector3.Dot(travelling, normal);
            Vector3 direction;
            float strength;

            if (facing > 0.08f && travelling.Y < -0.05f)
            {
                direction = travelling;
                strength = Direct;
            }
            else
            {
                direction = Vector3.Normalize(
                    (normal * MathF.Cos(Skyward)) - (Vector3.UnitY * MathF.Sin(Skyward)));
                strength = Diffuse;
            }

            // The cross-section is the pane seen along the light. Its width survives
            // whole — the pane's horizontal edge is level and the light's sideways slant
            // only skews it — and its height is foreshortened by however steeply the light
            // comes down.
            float projectedHeight = halfHeight * MathF.Sqrt(
                MathF.Max(1f - (direction.Y * direction.Y), 0.05f));

            // How far it goes: to the floor under where it lands, and never beyond the
            // room.
            float ground = floor?.Invoke(new Vector3(centre.X, room.Minimum.Y + 1f, centre.Z))
                ?? room.Minimum.Y;

            float drop = centre.Y - ground;
            float length = direction.Y < -0.05f
                ? drop / -direction.Y
                : Vector3.Distance(room.Minimum, room.Maximum);

            length = Math.Clamp(length, halfHeight, Vector3.Distance(room.Minimum, room.Maximum));

            found.Add(new LightShaft(
                centre + (normal * 0.5f),
                direction,
                halfWidth,
                MathF.Max(projectedHeight, halfWidth * 0.2f),
                length,
                sun.Color,
                strength));

            if (found.Count == LightShaft.Capacity)
            {
                break;
            }
        }

        return found;
    }

    /// <summary>How hot the air is at an hour of the story, for the haze over the far ground.</summary>
    /// <param name="when">Where the story stands, or null for the daylight answer.</param>
    /// <returns>Nought outside the hot part of the day; one at noon and two; less at ten and four.</returns>
    public static float Heat(Timeblock? when)
    {
        if (when is null)
        {
            return 1f;
        }

        int hour = (when.Value.IsAfternoon && when.Value.Hour != 12
            ? when.Value.Hour + 12
            : when.Value.Hour) % 24;

        return hour switch
        {
            12 or 13 or 14 => 1f,
            10 or 11 or 15 or 16 => 0.6f,
            _ => 0f,
        };
    }

    /// <summary>
    /// Whether a room has a roof over it: geometry above most of the ground the player
    /// can walk on.
    /// </summary>
    /// <param name="room">The room's geometry.</param>
    /// <param name="walkable">Where the player can stand, or null.</param>
    /// <returns>True indoors. A wood with a canopy over half of it is still outdoors.</returns>
    public static bool IsRoofed(BspFile? room, WalkBoundary? walkable)
    {
        if (room is null || walkable is null)
        {
            return false;
        }

        // A spread of points across the walkable ground, rather than the middle alone:
        // the middle of a cloister is its garth, which is open to the sky.
        var samples = new List<Vector3>();
        int stepX = Math.Max(walkable.Width / 6, 1);
        int stepY = Math.Max(walkable.Height / 6, 1);

        for (int y = stepY / 2; y < walkable.Height; y += stepY)
        {
            for (int x = stepX / 2; x < walkable.Width; x += stepX)
            {
                if (walkable.IsTexelWalkable(x, y))
                {
                    samples.Add(walkable.ToWorld(x, y));
                }
            }
        }

        if (samples.Count < 4)
        {
            return false;
        }

        int covered = 0;

        foreach (Vector3 at in samples)
        {
            if (Overhead(room, at))
            {
                covered++;
            }
        }

        return covered * 10 >= samples.Count * 6;
    }

    /// <summary>Whether any of the room's geometry stands over a point.</summary>
    private static bool Overhead(BspFile room, Vector3 at)
    {
        // Well above the ground, so a kerb or a step under the point is not a roof, and
        // below the sky.
        float above = at.Y + 40f;

        foreach (BspPolygon polygon in room.Polygons)
        {
            if (polygon.VertexIndexCount < 3)
            {
                continue;
            }

            for (int corner = 1; corner + 1 < polygon.VertexIndexCount; corner++)
            {
                if (!Corner(room, polygon.VertexIndexOffset, out Vector3 a) ||
                    !Corner(room, polygon.VertexIndexOffset + corner, out Vector3 b) ||
                    !Corner(room, polygon.VertexIndexOffset + corner + 1, out Vector3 c))
                {
                    continue;
                }

                // All three corners below the height, and the triangle cannot be over it.
                if (a.Y < above && b.Y < above && c.Y < above)
                {
                    continue;
                }

                if (!ContainsOnGround(a, b, c, at))
                {
                    continue;
                }

                // The triangle's own height over the point.
                Vector3 normal = Vector3.Cross(b - a, c - a);

                if (MathF.Abs(normal.Y) < 1e-6f)
                {
                    continue;
                }

                float height = a.Y - (((normal.X * (at.X - a.X)) + (normal.Z * (at.Z - a.Z))) / normal.Y);

                if (height > above)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool Corner(BspFile room, int index, out Vector3 at)
    {
        at = default;

        if (index < 0 || index >= room.VertexIndices.Length)
        {
            return false;
        }

        ushort vertex = room.VertexIndices[index];

        if (vertex >= room.Vertices.Length)
        {
            return false;
        }

        at = room.Vertices[vertex];
        return true;
    }

    private static bool ContainsOnGround(Vector3 a, Vector3 b, Vector3 c, Vector3 p)
    {
        float d1 = Sign(p, a, b);
        float d2 = Sign(p, b, c);
        float d3 = Sign(p, c, a);

        bool negative = d1 < 0 || d2 < 0 || d3 < 0;
        bool positive = d1 > 0 || d2 > 0 || d3 > 0;

        return !(negative && positive);

        static float Sign(Vector3 p, Vector3 q, Vector3 r) =>
            ((p.X - r.X) * (q.Z - r.Z)) - ((q.X - r.X) * (p.Z - r.Z));
    }
}
