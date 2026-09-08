using System.Numerics;
using GK3Reborn.Formats.Models;

namespace GK3Reborn.Game.Actors;

/// <summary>
/// Where a character's model stands relative to its own origin, and how to take that out.
/// </summary>
public static class Footing
{
    /// <summary>Where a model's rest pose puts a character's feet, in the model's space.</summary>
    /// <param name="model">The model, as read.</param>
    /// <param name="character">Their entry in <c>CHARACTERS.TXT</c>, for the axis triads.</param>
    /// <returns>The spot, or null when the file gives no triads or names ones it has not got.</returns>
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
