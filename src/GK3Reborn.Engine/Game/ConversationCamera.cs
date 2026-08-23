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
/// <remarks>
/// <para>
/// A scene's <c>[DIALOGUE_CAMERAS]</c> names one or more shots per conversation and marks
/// one <c>initial</c>, and where a conversation says which one it is, that is the answer.
/// Plenty of exchanges do not: the lobby's introduction to Emilio calls
/// <c>SetDefaultDialogueCamera</c> and then starts talking, and never names a conversation
/// at all — and <c>SetDefaultDialogueCamera</c> does nothing in the original either. So
/// there is no faithful answer to fall back on, and the game was carrying on the
/// conversation wherever the camera happened to be pointing, often at neither speaker.
/// </para>
/// <para>
/// <b>The port decides, out of the artists' own cameras.</b> Every camera the scene names is
/// scored on whether it shows both speakers and how well, and the best is used. Nothing is
/// invented: a shot that was never authored is never framed, which is what <c>Plan/03</c>
/// section 5 asks for. Where no authored camera can see both of them, the view is left
/// alone — a bad cut is worse than no cut.
/// </para>
/// <para>
/// <b>And from in front of them.</b> Scoring on framing alone picked shots from behind, which
/// is how a conversation ended up watching the back of Gabriel's head — reported as him
/// turning round, because from the player's chair that is exactly what it looks like. A
/// camera behind a speaker holds them in frame perfectly well; what it does not do is show
/// their face, and a shot of two people talking is a shot of at least one face.
/// </para>
/// </remarks>
public static class ConversationCamera
{
    /// <summary>How far off the middle of the view a speaker may be and still count.</summary>
    /// <remarks>
    /// Sixty degrees is the game's own default field of view, so half of it is the edge of
    /// the frame. Two thirds of that keeps a speaker off the very edge, which is where a
    /// shot stops reading as being about them.
    /// </remarks>
    private const float Within = 20f * MathF.PI / 180f;

    /// <summary>How far a camera may be from a speaker before it stops being about them.</summary>
    private const float Far = 900f;

    /// <summary>
    /// How far round from a speaker's own line of sight a camera may sit and still see them.
    /// </summary>
    /// <remarks>
    /// A right angle either side, so anything from a full profile forwards counts as a face
    /// and anything behind the shoulders does not. Generous on purpose: three-quarter views
    /// are most of the game's authored shots and a stricter rule would throw them away.
    /// </remarks>
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
    /// <remarks>
    /// <para>
    /// A camera scores by the worst-framed speaker rather than the average, because a shot
    /// that frames one person beautifully and leaves the other out of it is not a shot of a
    /// conversation. Ties go to the closer camera: two shots that both hold everybody make
    /// the tighter one the better one.
    /// </para>
    /// <para>
    /// Heads rather than feet. A camera aimed at where somebody stands is aimed at the
    /// floor in front of them, and every one of these shots was framed for faces.
    /// </para>
    /// </remarks>
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
