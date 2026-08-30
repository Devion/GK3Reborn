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
/// not obvious. The triads settle it: the three points make a triangle, and <b>its normal
/// flattened onto the floor is the facing outright</b> — no half turn, no convention to
/// choose between, and no reading taken off any mesh's own axes. That is
/// <c>GKActor::GetModelFacingDirection</c>, and it is the same measure
/// <c>SceneUpdate.Playing.Correction</c> draws the body by, which is the point: two ways of
/// asking where somebody is facing is one too many, and the two answered a half turn apart.
/// Getting it wrong walks the actor to the right spot with their back to the thing.
/// </para>
/// </remarks>
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
    /// <remarks>
    /// The lower of the two, which is the one taking their weight: mid-stride the other is
    /// in the air, and averaging them would have every walk bob half a step deep into the
    /// floor and half a step above it.
    /// </remarks>
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
    /// <remarks>
    /// The same reckoning the opening frame gets, at any frame. A head's glance is measured
    /// against the body's facing, and while an absolute clip has the body somewhere other
    /// than its placement the placement's heading is the wrong number to measure against.
    /// </remarks>
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
    /// <remarks>
    /// In degrees, for a diagnostic to print. It used to be a dot product, because the
    /// heading used to be the hip mesh's rotation with the triangle only choosing a sign;
    /// see <see cref="Facing"/> for why it is no longer. A large number here is a clip whose
    /// hips are turned relative to the stance, which is ordinary and no longer changes the
    /// answer.
    /// </remarks>
    public static float Reading { get; private set; }

    /// <summary>Whether the last facing read had a stance to read it from.</summary>
    /// <remarks>
    /// False means the clip poses no shoes on that frame and the answer came from the hip
    /// mesh's own rotation instead, which is a different measurement and routinely a long
    /// way off. Worth being able to see: it is not an error and it is not the answer either.
    /// </remarks>
    public static bool Stance { get; private set; }

    /// <summary>
    /// Which way the body is facing on the opening frame.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The shoes and the hips make a triangle, and <b>its normal flattened onto the floor is
    /// the facing outright</b> — <c>GKActor::GetModelFacingDirection</c>, the branch it takes
    /// whenever nothing is animating the facing helper. No dot product and no rare branch.
    /// </para>
    /// <para>
    /// It used to read the heading off the hip mesh's own rotation and use the triangle only
    /// to choose between that and a half turn from it, which is a different measurement and
    /// <b>came out a half turn from this one for Gabriel</b> — measured, on <c>gab_GabYawn</c>:
    /// the triangle says −179.9° and the mesh rotation said −3.2°. That was one measurement
    /// too many. <see cref="SceneUpdate.Playing.Correction"/> already draws the body by the
    /// triangle, so everything that asked here instead was aimed a half turn from the body it
    /// was aimed at, and every one of those is a reported bug: a head glance turned Emilio's
    /// head backwards over his shoulders as he crossed the lobby; an <c>approach=anim</c>
    /// stood Gabriel at the wardrobe facing away from it, so his clip played correctly and
    /// his idle spun him round the moment it ended; and the opening-pose report accused half
    /// the cast of facing the wrong way.
    /// </para>
    /// </remarks>
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
