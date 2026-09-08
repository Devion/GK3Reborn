// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Numerics;
using GK3Reborn.Formats.Scenes;

namespace GK3Reborn.Game;

/// <summary>
/// Which camera to watch a conversation from.
/// </summary>
public static class ConversationCamera
{
    /// <summary>How far off the middle of the view a speaker may be and still count.</summary>
    private const float Within = 20f * MathF.PI / 180f;

    /// <summary>How far a camera may be from a speaker before it stops being about them.</summary>
    private const float Far = 900f;

    /// <summary>
    /// How far round from a speaker's own line of sight a camera may sit and still see them.
    /// </summary>
    private const float Ahead = 0f;

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

    /// <summary>How well one camera holds a conversation, or null when it does not.</summary>
    private static float? Scores(
        SceneCamera camera, IReadOnlyList<Vector3> speakers, IReadOnlyList<Vector3>? looking)
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
            Vector3 toward = speaker with { Y = speaker.Y + 60f } - camera.Position;
            float distance = toward.Length();

            if (distance < 1e-3f || distance > Far)
            {
                return null;
            }

            float off = MathF.Acos(
                Math.Clamp(Vector3.Dot(forward, toward / distance), -1f, 1f));

            if (off > Within)
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
                    Vector3.Normalize(camera.Position - speaker)) <= Ahead)
            {
                return null;
            }

            worst = MathF.Min(worst, Within - off);
            nearest = MathF.Min(nearest, distance);
        }

        // How well the worst-placed speaker sits in frame, and then how close the shot is.
        // The distance term is small enough that framing decides first and only a tie is
        // settled by tightness.
        return worst + ((Far - nearest) / Far * 0.05f);
    }
}
