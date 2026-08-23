using System.Numerics;
using GK3Reborn.Content;
using GK3Reborn.Formats.Animation;
using GK3Reborn.Game.Navigation;

namespace GK3Reborn.Game.Actors;

/// <summary>
/// Where an animation expects somebody to be standing before it starts.
/// </summary>
/// <remarks>
/// <para>
/// <c>approach=anim</c> is the third most common approach in the game — 398 of the
/// corpus's 3,617 — and it is the only one whose target is not a place. It names an
/// animation, and what it means is <b>walk to where that animation begins, then play
/// it</b>. Pouring the coffee in the hotel dining room is the case: without this Gabriel
/// starts pouring from wherever he is standing, and the pot is across the room.
/// </para>
/// <para>
/// The animation does not say where it begins in so many words. It says it in the clip:
/// the first frame poses every mesh, and among a character's meshes are three that are not
/// body parts at all — an axis triad at the hips and one under each shoe, which is the only
/// thing in a GK3 character that stands for a skeleton. <c>CHARACTERS.TXT</c> names which
/// mesh, group and point they are, per character. The hip triad's point is where the actor
/// stands.
/// </para>
/// <para>
/// Which way they face has to be worked out rather than read, and this is the part that is
/// not obvious. A character's model normally faces <b>−Z</b>, so a heading is the mesh's
/// rotation plus a half turn — but the game is not consistent about it and a few animations
/// are authored the other way round. The triads settle it: the three points make a triangle
/// whose normal is the way the body is actually facing, and comparing that against the hip
/// mesh's own Y axis says which of the two conventions this clip was authored in. Getting
/// it wrong walks the actor to the right spot with their back to the thing.
/// </para>
/// </remarks>
public static class AnimationStart
{
    /// <summary>Reads where and how an animation stands the actor it moves.</summary>
    /// <param name="animation">The animation the approach named.</param>
    /// <param name="clips">Where its vertex animations come from.</param>
    /// <param name="model">The actor's model name, which picks their clip out of it.</param>
    /// <param name="character">Their entry in <c>CHARACTERS.TXT</c>, for the axis triads.</param>
    /// <returns>
    /// The spot and the heading, or null when the animation moves nobody by that name — a
    /// scenery animation, or one whose actor is not in this room.
    /// </returns>
    public static (Vector3 Position, float Heading)? Of(
        AnimationFile animation,
        ClipLibrary clips,
        string model,
        CharacterConfig character)
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

            return (position, Facing(clip, character, basis, toWorld));
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
    /// <remarks>
    /// <para>
    /// The same measure <see cref="Of"/> takes, at any frame rather than the first: an
    /// actor stands where the triad under their hips is.
    /// </para>
    /// <para>
    /// It matters wherever a position is read out of a clip rather than differenced across
    /// one. The average of a character's mesh-group origins moves with exactly the same
    /// rigid motion, so a <em>difference</em> of two averages is the same answer and much
    /// cheaper — but one average on its own is that answer plus a constant, and the
    /// constant is most of a torso. Emilio walked out of the hotel and then set off for his
    /// bench from a couple of feet behind where he was standing.
    /// </para>
    /// </remarks>
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

        return Vector3.Transform(local, pose * toWorld);
    }

    /// <summary>The triad's point on the opening frame, if the clip records vertices.</summary>
    private static Vector3? Point(Formats.Animation.ActFile clip, CharacterAxes axes) =>
        clip.ShapeOf(axes.Mesh, axes.Group, 0) is { } shape && axes.Point < shape.Count
            ? shape[axes.Point]
            : null;

    /// <summary>How clear the facing test has to be before it takes the rare answer.</summary>
    /// <remarks>
    /// The reference calls a model facing along its own hip axis the rare case and everything
    /// else the vast majority. A reading near zero is not evidence of the rare case; it is
    /// evidence that the three points used to measure it are nearly in a line.
    /// </remarks>
    private const float Confident = 0.9f;

    /// <summary>The last dot product the facing test read, for a diagnostic to print.</summary>
    /// <remarks>
    /// The reference says the vast majority of models face opposite the hip mesh's Y axis,
    /// which is a negative reading. If ours come out positive the sign is inverted somewhere
    /// and every character is being turned the wrong way, which is not something to argue
    /// about when it can be measured.
    /// </remarks>
    public static float Reading { get; private set; }

    /// <summary>
    /// Which way the body is facing on the opening frame.
    /// </summary>
    /// <remarks>
    /// The shoes and the hips make a triangle, and its normal flattened onto the floor is
    /// the direction the body faces. Nearly every model in the game has that opposite the
    /// hip mesh's Y axis, which is what makes a heading "the mesh's rotation plus a half
    /// turn"; a few have it the same way round, and for those the half turn must not be
    /// applied. The dot product is the whole of the test, and it is the original's.
    /// </remarks>
    private static float Facing(
        Formats.Animation.ActFile clip,
        CharacterConfig character,
        Matrix4x4 basis,
        Matrix4x4 toWorld)
    {
        float turned = Walker.HeadingOf(basis);

        if (character.LeftShoe is not { } left ||
            character.RightShoe is not { } right ||
            clip.PoseOf(left.Mesh, 0) is not { } leftPose ||
            clip.PoseOf(right.Mesh, 0) is not { } rightPose)
        {
            return turned;
        }

        Vector3 hip = basis.Translation;
        Vector3 leftFoot = (leftPose * toWorld).Translation;
        Vector3 rightFoot = (rightPose * toWorld).Translation;

        Vector3 across = rightFoot - leftFoot;
        Vector3 up = hip - leftFoot;

        Vector3 normal = Vector3.Cross(across, up) with { Y = 0 };

        if (normal.LengthSquared() < 1e-6f)
        {
            return turned;
        }

        var axis = new Vector3(basis.M21, basis.M22, basis.M23);

        // Facing along the mesh's own Y axis is the rare case, and the one where the half
        // turn is wrong. Walker.HeadingOf is the half turn, so this undoes it.
        Reading = Vector3.Dot(axis, Vector3.Normalize(normal));

        // A confident reading, or the answer that is true of nearly every model in the game.
        //
        // The test asks whether a model faces along its hip mesh's Y axis or opposite it, and
        // a clean answer is near plus or minus one: the corpus reads -1.00 for Emilio, Jean
        // and Buthane. The museum's Estelle and Lady Howard read +0.55, which is not a model
        // built the rare way — it is a shoe-and-hip triangle too flat to give a normal worth
        // trusting, because the pose has them standing close together and angled. Believing
        // it turned both of them to face the wall.
        //
        // So the rare branch needs to be earned. Anything short of a clear positive falls
        // back to the common case, which cannot disturb a model that reads -1.00 either way.
        return Reading > Confident
            ? Walker.HeadingOf(basis) - MathF.PI
            : turned;
    }
}
