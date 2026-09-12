// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Numerics;
using GK3Reborn.Formats.Scenes;
using GK3Reborn.Rendering;

namespace GK3Reborn.Game;

/// <summary>
/// Which camera to watch a conversation from.
/// </summary>
public static class ConversationCamera
{
    /// <summary>How far off the middle of the view a speaker may be and still count.</summary>
    private const float Within = 20f * MathF.PI / 180f;

    /// <summary>
    /// And how far off it they may be to be merely in shot rather than well framed. Half the
    /// camera's own field of view: the tighter number above ranks one authored camera
    /// against another, and a wide two-shot that puts somebody near the edge of the picture
    /// loses that contest while holding them perfectly well.
    /// </summary>
    private const float InFrame = 30f * MathF.PI / 180f;

    /// <summary>How far a camera may be from a speaker before it stops being about them.</summary>
    private const float Far = 900f;

    /// <summary>
    /// How far round from a speaker's own line of sight a camera may sit and still see them.
    /// </summary>
    private const float Ahead = 0f;

    /// <summary>
    /// And how far behind square a camera may sit and still be holding somebody. Two people
    /// talking face each other, so a shot square on to the pair is at right angles to both
    /// of them and the rule above refuses it on a rounding error. A little past square is
    /// the over-the-shoulder framing every conversation in the game is cut with.
    /// </summary>
    private const float Shoulder = -0.25f;

    /// <summary>
    /// Picks the camera that best shows everyone talking.
    /// </summary>
    /// <param name="cameras">Every camera the scene names.</param>
    /// <param name="speakers">Where the people talking are standing.</param>
    /// <param name="looking">
    /// Which way each of them is facing, in the same order. Optional: without it a shot is
    /// judged on framing alone, which is what picked the back of Gabriel's head.
    /// </param>
    /// <returns>The camera's name, or null when none of them shows the conversation.</returns>
    public static string? Framing(
        IEnumerable<SceneCamera> cameras,
        IReadOnlyList<Vector3> speakers,
        IReadOnlyList<Vector3>? looking = null)
    {
        ArgumentNullException.ThrowIfNull(cameras);
        ArgumentNullException.ThrowIfNull(speakers);

        if (speakers.Count == 0)
        {
            return null;
        }

        string? best = null;
        float bestScore = float.MinValue;

        foreach (SceneCamera camera in cameras)
        {
            if (Scores(camera, speakers, looking) is not { } score || score <= bestScore)
            {
                continue;
            }

            best = camera.Name;
            bestScore = score;
        }

        return best;
    }

    /// <summary>Whether one camera actually holds everybody talking.</summary>
    /// <param name="camera">The shot.</param>
    /// <param name="speakers">Where the people talking are standing.</param>
    /// <param name="looking">Which way each is facing, in the same order.</param>
    /// <returns>True when all of them are in frame and none is seen from behind.</returns>
    public static bool Frames(
        SceneCamera camera,
        IReadOnlyList<Vector3> speakers,
        IReadOnlyList<Vector3>? looking = null)
    {
        ArgumentNullException.ThrowIfNull(camera);
        ArgumentNullException.ThrowIfNull(speakers);

        return speakers.Count > 0 &&
               Scores(camera, speakers, looking, InFrame, Shoulder) is not null;
    }

    /// <summary>How high above the floor a head is, which is what a shot is framed on.</summary>
    private const float Head = 60f;

    /// <summary>The widest a composed two-shot stands back, in scene units.</summary>
    private const float Furthest = 520f;

    /// <summary>And the nearest, so two people standing together are not filmed from inside.</summary>
    private const float Nearest = 170f;

    /// <summary>
    /// Builds a shot that holds two people talking, for when no camera the scene names does.
    /// </summary>
    /// <param name="speakers">Where they are standing. The first two are what is framed.</param>
    /// <param name="from">
    /// Where the view is now. Only its side of the line between the speakers is read, so
    /// that moving to the shot does not cross the axis and swap which of them is which.
    /// </param>
    /// <param name="template">A camera to take the lens, the planes and the light from.</param>
    /// <param name="clear">
    /// Whether the camera may stand at a point, or null to let it stand anywhere. The
    /// room's camera shell, where it has one: a two-shot worked out from two positions
    /// alone lands inside a wall often enough to need checking.
    /// </param>
    /// <returns>The shot, or null when there is nowhere to put one.</returns>
    public static Camera? Composed(
        IReadOnlyList<Vector3> speakers,
        Vector3 from,
        Camera template,
        Func<Vector3, bool>? clear = null)
    {
        ArgumentNullException.ThrowIfNull(speakers);
        ArgumentNullException.ThrowIfNull(template);

        if (speakers.Count < 2)
        {
            return null;
        }

        Vector3 one = speakers[0];
        Vector3 two = speakers[1];

        var axis = new Vector3(two.X - one.X, 0f, two.Z - one.Z);
        float apart = axis.Length();

        // Two people at the same point are one subject, and a two-shot of one subject is a
        // shot with no axis to stand off: whoever is asking gets nothing and keeps its view.
        if (apart < 1f)
        {
            return null;
        }

        axis /= apart;

        // A hair below the heads, so the shot looks very slightly down on the pair rather
        // than dead level, which is what every conversation in the game is framed as.
        var middle = new Vector3(
            (one.X + two.X) * 0.5f,
            MathF.Max(one.Y, two.Y) + (Head * 0.9f),
            (one.Z + two.Z) * 0.5f);

        // Square on to the line between them, so both are in profile and neither is hidden
        // behind the other. cross(up, axis) is the left-handed right; see FreeCamera.
        var side = Vector3.Normalize(new Vector3(axis.Z, 0f, -axis.X));

        // The side the view is already on. Crossing the line would swap left and right
        // between the two shots, which reads as the pair having changed places.
        var towards = new Vector3(from.X - middle.X, 0f, from.Z - middle.Z);

        if (Vector3.Dot(side, towards) < 0f)
        {
            side = -side;
        }

        float back = Math.Clamp(apart * 1.15f, Nearest, Furthest);

        // Pulled in until the shot is inside the room, and across to the other side of the
        // line before it is given up on: a wall on one side is the usual reason a composed
        // shot is no good, and the far side of the pair is a better answer than none.
        foreach (Vector3 which in (Vector3[])[side, -side])
        {
            foreach (float part in (float[])[1f, 0.78f, 0.58f, 0.42f])
            {
                float far = back * part;

                Vector3 eye = middle + (which * far) + (Vector3.UnitY * far * 0.18f);

                if (clear?.Invoke(eye) == false)
                {
                    continue;
                }

                return new Camera
                {
                    Position = eye,
                    Target = middle,
                    Up = Vector3.UnitY,
                    FieldOfView = template.FieldOfView,
                    NearPlane = template.NearPlane,
                    FarPlane = template.FarPlane,
                    LightDirection = template.LightDirection,
                    Background = template.Background,
                };
            }
        }

        return null;
    }

    /// <summary>How well one camera holds a conversation, or null when it does not.</summary>
    private static float? Scores(
        SceneCamera camera,
        IReadOnlyList<Vector3> speakers,
        IReadOnlyList<Vector3>? looking,
        float within = Within,
        float ahead = Ahead)
    {
        Vector3 forward = camera.Forward;

        if (forward.LengthSquared() < 1e-6f)
        {
            return null;
        }

        forward = Vector3.Normalize(forward);

        float worst = float.MaxValue;
        float nearest = float.MaxValue;

        for (int who = 0; who < speakers.Count; who++)
        {
            Vector3 speaker = speakers[who];

            // The head, near enough: the shots are framed for faces and a scene position
            // is the floor somebody stands on.
            Vector3 toward = speaker with { Y = speaker.Y + Head } - camera.Position;
            float distance = toward.Length();

            if (distance < 1e-3f || distance > Far)
            {
                return null;
            }

            float off = MathF.Acos(
                Math.Clamp(Vector3.Dot(forward, toward / distance), -1f, 1f));

            if (off > within)
            {
                return null;
            }

            // And in front of them. A camera behind a speaker frames them as well as one in
            // front does and shows the back of their head, which reads as the character
            // having turned round rather than as a choice of shot.
            //
            // By index, because two speakers may be standing at the same point — an actor
            // whose position nothing has updated is at the origin, and so is the other one.
            if (looking is not null &&
                who < looking.Count &&
                looking[who].LengthSquared() > 1e-6f &&
                Vector3.Dot(
                    Vector3.Normalize(looking[who]),
                    Vector3.Normalize(camera.Position - speaker)) <= ahead)
            {
                return null;
            }

            worst = MathF.Min(worst, within - off);
            nearest = MathF.Min(nearest, distance);
        }

        // How well the worst-placed speaker sits in frame, and then how close the shot is.
        // The distance term is small enough that framing decides first and only a tie is
        // settled by tightness.
        return worst + ((Far - nearest) / Far * 0.05f);
    }
}
