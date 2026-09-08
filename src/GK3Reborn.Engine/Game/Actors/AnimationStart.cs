using System.Numerics;
using GK3Reborn.Content;
using GK3Reborn.Formats.Animation;
using GK3Reborn.Game.Navigation;

namespace GK3Reborn.Game.Actors;

/// <summary>
/// Where an animation expects somebody to be standing before it starts.
/// </summary>
public static class AnimationStart
{
    /// <summary>Reads where and how an animation stands the actor it moves.</summary>
    /// <param name="animation">The animation the approach named.</param>
    /// <param name="clips">Where its vertex animations come from.</param>
    /// <param name="model">The actor's model name, which picks their clip out of it.</param>
    /// <param name="character">Their entry in <c>CHARACTERS.TXT</c>, for the axis triads.</param>
    /// <param name="built">
    /// Which way their model is built to face, out of its own arrow — see
    /// <see cref="FacingArrow"/>. Null falls back to measuring it from the shoes and hips,
    /// which is what the reference does for an actor the game ships no arrow for.
    /// </param>
    /// <returns>
    /// The spot and the heading, or null when the animation moves nobody by that name — a
    /// scenery animation, or one whose actor is not in this room.
    /// </returns>
    public static (Vector3 Position, float Heading)? Of(
        AnimationFile animation,
        ClipLibrary clips,
        string model,
        CharacterConfig character,
        float? built = null)
    {
        ArgumentNullException.ThrowIfNull(animation);
        ArgumentNullException.ThrowIfNull(clips);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(character);

        if (character.Hips is not { } hips)
        {
            return null;
        }

        foreach (AnimationAction action in animation.Actions)
        {
            if (clips.Read(action.Name) is not { } clip ||
                !string.Equals(clip.ModelName, model, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (clip.PoseOf(hips.Mesh, 0) is not { } hipPose)
            {
                continue;
            }

            // Where the room puts the clip. An absolute animation carries its own placement
            // and the clip's coordinates are corrections to it; the other 92% are already
            // in the room's own space and this is the identity.
            Matrix4x4 toWorld = action.Placement is { } spot
                ? Matrix4x4.CreateRotationY(spot.Heading) * Matrix4x4.CreateTranslation(spot.Position)
                : Matrix4x4.Identity;

            Matrix4x4 basis = hipPose * toWorld;

            // The triad's own point rather than the mesh's origin. A rigid clip records no
            // vertices, and the origin is the same rigid motion away from the point, so
            // falling back to it costs a constant offset rather than an answer.
            Vector3 local = Point(clip, hips) ?? Vector3.Zero;
            Vector3 position = Vector3.Transform(local, basis);

            return (position, Facing(clip, character, basis, toWorld, 0f, repeat: false, built));
        }

        return null;
    }

    /// <summary>
    /// Where a clip stands somebody at a moment part-way through it.
    /// </summary>
    /// <param name="clip">The clip posing them.</param>
    /// <param name="frame">Which frame, with the fraction of the way to the next.</param>
    /// <param name="repeat">Whether the clip loops, which decides how it reads past its end.</param>
    /// <param name="character">Their entry in <c>CHARACTERS.TXT</c>, for the hip triad.</param>
    /// <param name="toWorld">Where in the room the clip is being played.</param>
    /// <returns>The spot, or null when the clip says nothing about where their hips are.</returns>
    public static Vector3? Standing(
        Formats.Animation.ActFile clip,
        float frame,
        bool repeat,
        CharacterConfig character,
        Matrix4x4 toWorld)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(character);

        if (character.Hips is not { } hips ||
            clip.PoseAt(hips.Mesh, frame, repeat) is not { } pose ||
            Point(clip, hips) is not { } local)
        {
            return null;
        }

        Vector3 standing = Vector3.Transform(local, pose * toWorld);

        // The hips are a third of a metre up, so their height is not the ground's. The
        // reference takes the position from the hips and the height from the lower shoe
        // less its sole — GKActor::GetModelFloorAndShoePositions — and it matters because
        // this is the height a walk's first floor query is asked about: handed a Y thirty-
        // four units above the feet, a room whose floor covers the same ground twice
        // answers about the wrong storey.
        if (Soles(clip, frame, repeat, character, toWorld) is { } ground)
        {
            standing.Y = ground;
        }

        return standing;
    }

    /// <summary>The height a pose puts a character's soles at, in the room.</summary>
    /// <returns>The height, or null when the clip poses neither shoe.</returns>
    private static float? Soles(
        Formats.Animation.ActFile clip,
        float frame,
        bool repeat,
        CharacterConfig character,
        Matrix4x4 toWorld)
    {
        float? left = Sole(clip, frame, repeat, character.LeftShoe, toWorld);
        float? right = Sole(clip, frame, repeat, character.RightShoe, toWorld);

        return (left, right) switch
        {
            ({ } l, { } r) => MathF.Min(l, r) - character.ShoeThickness,
            ({ } l, null) => l - character.ShoeThickness,
            (null, { } r) => r - character.ShoeThickness,
            _ => null,
        };
    }

    /// <summary>Where one shoe's triad sits, vertically, in the room.</summary>
    private static float? Sole(
        Formats.Animation.ActFile clip,
        float frame,
        bool repeat,
        CharacterAxes? axes,
        Matrix4x4 toWorld) =>
        axes is { } shoe &&
        clip.PoseAt(shoe.Mesh, frame, repeat) is { } pose &&
        Point(clip, shoe) is { } local
            ? Vector3.Transform(local, pose * toWorld).Y
            : null;

    /// <summary>
    /// Which way a character is facing at a moment of a clip.
    /// </summary>
    /// <param name="clip">The clip.</param>
    /// <param name="frame">How far into it.</param>
    /// <param name="repeat">Whether it loops.</param>
    /// <param name="character">Their entry in <c>CHARACTERS.TXT</c>, for the axis triads.</param>
    /// <param name="toWorld">Where the clip's space sits in the room.</param>
    /// <param name="built">Which way their model is built to face, or null to measure it.</param>
    /// <returns>The heading, or null when the clip does not pose the hips.</returns>
    public static float? FacingAt(
        Formats.Animation.ActFile clip,
        float frame,
        bool repeat,
        CharacterConfig character,
        Matrix4x4 toWorld,
        float? built)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(character);

        if (character.Hips is not { } hips ||
            clip.PoseAt(hips.Mesh, frame, repeat) is not { } pose)
        {
            return null;
        }

        return Facing(clip, character, pose * toWorld, toWorld, frame, repeat, built);
    }

    /// <summary>The triad's point on the opening frame, if the clip records vertices.</summary>
    private static Vector3? Point(Formats.Animation.ActFile clip, CharacterAxes axes) =>
        clip.ShapeOf(axes.Mesh, axes.Group, 0) is { } shape && axes.Point < shape.Count
            ? shape[axes.Point]
            : null;

    /// <summary>How far the triangle's answer stood from the hip mesh's, last time it was read.</summary>
    public static float Reading { get; private set; }

    /// <summary>Whether the last facing read had a stance to read it from.</summary>
    public static bool Stance { get; private set; }

    /// <summary>
    /// Which way the body is facing on the opening frame.
    /// </summary>
    private static float Facing(
        Formats.Animation.ActFile clip,
        CharacterConfig character,
        Matrix4x4 basis,
        Matrix4x4 toWorld,
        float frame,
        bool repeat,
        float? built)
    {
        // Which way the model is built to face is the placement's business — see
        // FacingArrow — and not this one. What comes back here is where the clip has the
        // body pointing, in the room.
        _ = built;

        float turned = Walker.HeadingOf(basis);

        Stance = false;

        // All three corners on the same frame. The shoes used to be read on frame zero
        // whatever frame the hips were asked about, which is a triangle that never existed:
        // right for an opening pose, and wrong by however much the clip has turned the
        // character by the frame it is really on. A clip whose whole purpose is a turn is
        // the worst case, and the museum has one — `Lh2MusEstTurn2Gab` ends with Lady
        // Howard and Estelle facing Gabriel, and the frame-zero feet under the last frame's
        // hips put them 165 and 99 degrees away from him. Which is what a head glance was
        // measured against, and, once a finished clip's facing began to be kept, what they
        // were left standing at.
        if (character.LeftShoe is not { } left ||
            character.RightShoe is not { } right ||
            clip.PoseAt(left.Mesh, frame, repeat) is not { } leftPose ||
            clip.PoseAt(right.Mesh, frame, repeat) is not { } rightPose)
        {
            return turned;
        }

        Vector3 hip = basis.Translation;
        Vector3 leftFoot = (leftPose * toWorld).Translation;
        Vector3 rightFoot = (rightPose * toWorld).Translation;

        Vector3 across = rightFoot - leftFoot;
        Vector3 up = hip - leftFoot;

        Vector3 normal = Vector3.Cross(across, up) with { Y = 0 };

        // A clip that records no stance to read — the feet on top of each other, or a rigid
        // pose with nothing between them — has no triangle, and the mesh's own rotation is
        // the only thing left to answer with.
        if (normal.LengthSquared() < 1e-6f)
        {
            return turned;
        }

        float facing = Walker.Heading(normal);

        Reading = Walker.Wrapped(facing - turned) * 180f / MathF.PI;
        Stance = true;

        return facing;
    }
}
