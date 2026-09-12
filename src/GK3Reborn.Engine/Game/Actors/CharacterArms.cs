using System.Numerics;
using GK3Reborn.Formats.Models;

namespace GK3Reborn.Game.Actors;

/// <summary>
/// Which parts of a character are their arms.
/// </summary>
public static class CharacterArms
{
    /// <summary>How far out from the spine an arm sits, as a share of the character's height.</summary>
    private const float Aside = 0.04f;

    /// <summary>And how far up it starts, as the same share, which leaves the thighs out of it.</summary>
    private const float Above = 0.4f;

    /// <summary>Finds the mesh groups that are a character's arms.</summary>
    /// <param name="model">The character.</param>
    /// <returns>Their indices among the model's meshes, in the order they are built, or nothing when the model does not read as somebody with two arms.</returns>
    public static IReadOnlyList<int> Find(ModFile model)
    {
        ArgumentNullException.ThrowIfNull(model);

        (List<int> left, List<int> right) = Sides(model);

        return left.Count > 0 && right.Count > 0 ? [.. left.Concat(right).Order()] : [];
    }

    /// <summary>Finds the arm groups a character's own eye is inside, which are the shoulders.</summary>
    /// <param name="model">The character.</param>
    /// <returns>
    /// The topmost group down each arm, when there is more of the arm below it to draw.
    /// The eye sits six units above Gabriel's shoulder and eight to the side of it, so a
    /// shoulder drawn to its owner is a slab of skin across the near plane; what a person
    /// can usefully see of themselves is their arm from the elbow down.
    /// </returns>
    public static IReadOnlyList<int> Shoulders(ModFile model)
    {
        ArgumentNullException.ThrowIfNull(model);

        (List<int> left, List<int> right) = Sides(model);

        if (left.Count < 2 || right.Count < 2)
        {
            return [];
        }

        return [Highest(model, left), Highest(model, right)];
    }

    /// <summary>Which mesh group of an arm is built highest.</summary>
    private static int Highest(ModFile model, List<int> side) =>
        side.MaxBy(mesh => model.Meshes[mesh].MeshToLocal.Translation.Y);

    /// <summary>Splits a character's arm groups into the two sides they hang from.</summary>
    private static (List<int> Left, List<int> Right) Sides(ModFile model)
    {
        // Named by texture the way a head is, this finds nothing: Gabriel's arms are GABRIGHTARM and GABPALM, and every one of Grace's is GRA_SKIN,
        // which is also her neck. What tells an arm from the rest of a body is where it is built — out to the side, above the hips.
        if (CharacterHead.Find(model) is not { } head)
        {
            return ([], []);
        }

        // The head stands in for the spine: it is the one group a person is certain to have, in the middle and at the top.
        Vector3 top = CharacterHead.PivotOf(model, head);

        if (top.Y <= 0f)
        {
            return ([], []);
        }

        float aside = top.Y * Aside;
        float above = top.Y * Above;

        List<int> left = [];
        List<int> right = [];

        for (int i = 0; i < model.Meshes.Count; i++)
        {
            if (i == head)
            {
                continue;
            }

            Vector3 pivot = model.Meshes[i].MeshToLocal.Translation;
            float across = pivot.X - top.X;

            if (pivot.Y < above || MathF.Abs(across) < aside)
            {
                continue;
            }

            (across < 0f ? left : right).Add(i);
        }

        // Arms down one side only is a model this rule has not understood, and half a body is worse than none.
        return left.Count > 0 && right.Count > 0 ? (left, right) : ([], []);
    }
}
