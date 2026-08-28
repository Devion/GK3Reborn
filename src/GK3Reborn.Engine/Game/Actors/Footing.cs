using System.Numerics;
using GK3Reborn.Formats.Models;

namespace GK3Reborn.Game.Actors;

/// <summary>
/// Where a character's model stands relative to its own origin, and how to take that out.
/// </summary>
/// <remarks>
/// <para>
/// <b>A character's model is not always drawn around its own origin.</b> The reference says
/// so in as many words — "the 3D model's <em>visual</em> position IS NOT always identical to
/// the 3D model's <em>actor</em> position", <c>GKActor::SetModelPositionToActorPosition</c>
/// — and it is measurable: of the 43 characters <c>CHARACTERS.TXT</c> gives axis triads,
/// Gabriel, Grace, Estelle and Madeline stand within a unit of their origin, Lady Howard
/// stands 83.6 units from hers, the taxi driver 871 and the sitting Wilkes 522.
/// </para>
/// <para>
/// So a scene placing an actor at a spot cannot simply translate the model there. The
/// original moves the model by the difference between its origin and its <em>floor
/// position</em> — the hip triad's ground plane, at the height of the lower shoe less its
/// sole, which is <see cref="AnimationStart.Standing"/>'s measure read out of the model
/// instead of out of a clip.
/// </para>
/// <para>
/// Rather than carry the offset alongside every placement, it is taken out of the model as
/// it is read: <see cref="OnItsFeet"/> returns the same model with its feet at its own
/// origin, and everything downstream — the scene's placement, a walk's, a script's, the
/// space a held prop is pinned to — then means what it says. Doing it at the placement
/// instead would fix the spot the actor is stood on and lose it again the moment they
/// walked, because <c>Walker.Transform</c> builds its own.
/// </para>
/// </remarks>
public static class Footing
{
    /// <summary>Where a model's rest pose puts a character's feet, in the model's space.</summary>
    /// <param name="model">The model, as read.</param>
    /// <param name="character">Their entry in <c>CHARACTERS.TXT</c>, for the axis triads.</param>
    /// <returns>The spot, or null when the file gives no triads or names ones it has not got.</returns>
    /// <remarks>
    /// The hips across and the shoes down, which is <c>GetModelFloorAndShoePositions</c>: a
    /// character stands where the triad under their hips is, at the height of whichever sole
    /// is lower. The hips are a third of a metre up, so their own height is not the ground's.
    /// </remarks>
    public static Vector3? Of(ModFile model, CharacterConfig character)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(character);

        if (character.Hips is not { } hips || Point(model, hips) is not { } standing)
        {
            return null;
        }

        float? left = Point(model, character.LeftShoe)?.Y;
        float? right = Point(model, character.RightShoe)?.Y;

        return (left, right) switch
        {
            ({ } l, { } r) => standing with { Y = MathF.Min(l, r) - character.ShoeThickness },
            ({ } l, null) => standing with { Y = l - character.ShoeThickness },
            (null, { } r) => standing with { Y = r - character.ShoeThickness },

            // No shoes to stand on. The hips across are still the better answer than the
            // origin, and their height is left alone rather than guessed at.
            _ => standing,
        };
    }

    /// <summary>The same model, with the character's feet at its origin.</summary>
    /// <param name="model">The model, as read.</param>
    /// <param name="character">Their entry in <c>CHARACTERS.TXT</c>.</param>
    /// <param name="moved">How far it had to be shifted, for whoever wants to say so.</param>
    /// <returns>The model, standing on its own origin.</returns>
    /// <remarks>
    /// Applied to every character and a change to few: it is under a unit for most of the
    /// cast and exactly zero for anyone the file gives no triads. The clips are unaffected —
    /// a relative one is corrected onto the model's rest, which has moved with it, and an
    /// absolute one says where in the room it happens and was never about the model's own
    /// space at all.
    /// </remarks>
    public static ModFile OnItsFeet(ModFile model, CharacterConfig? character, out Vector3 moved)
    {
        ArgumentNullException.ThrowIfNull(model);

        moved = Vector3.Zero;

        if (character is null || Of(model, character) is not { } feet || feet == Vector3.Zero)
        {
            return model;
        }

        moved = -feet;

        Matrix4x4 back = Matrix4x4.CreateTranslation(-feet);

        return ModFile.FromMeshes(
            model.Name,
            [.. model.Meshes.Select(mesh => mesh with { MeshToLocal = mesh.MeshToLocal * back })],
            model.IsBillboard);
    }

    /// <summary>Where one triad's own vertex sits in the model's space.</summary>
    /// <remarks>
    /// The triad's point rather than its mesh's origin, because the two are up to a torso
    /// apart: the triads are separate scraps of geometry sitting some tens of units out from
    /// the body they belong to, which is the whole reason this offset exists.
    /// </remarks>
    private static Vector3? Point(ModFile model, CharacterAxes? axes)
    {
        if (axes is not { } triad ||
            triad.Mesh < 0 || triad.Mesh >= model.Meshes.Count)
        {
            return null;
        }

        ModMesh mesh = model.Meshes[triad.Mesh];

        if (triad.Group < 0 || triad.Group >= mesh.Submeshes.Count)
        {
            return null;
        }

        Vector3[] points = mesh.Submeshes[triad.Group].Positions;

        return triad.Point < 0 || triad.Point >= points.Length
            ? null
            : Vector3.Transform(points[triad.Point], mesh.MeshToLocal);
    }
}
