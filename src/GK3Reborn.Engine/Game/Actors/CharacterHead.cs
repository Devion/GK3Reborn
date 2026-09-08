using System.Numerics;
using GK3Reborn.Formats.Models;

namespace GK3Reborn.Game.Actors;

/// <summary>
/// Which part of a character is the head.
/// </summary>
public static class CharacterHead
{
    /// <summary>What a head is painted with.</summary>
    private static readonly (string Fragment, int Weight)[] Marks =
    [
        ("EYELID", 4),
        ("MOUTH", 4),
        ("FOREHEAD", 4),
        ("FACE", 3),
        ("EYE", 2),
        ("HAIR", 1),
        ("HEAD", 3),
    ];

    /// <summary>Finds the mesh that is the character's head.</summary>
    /// <param name="model">The character.</param>
    /// <returns>Its index among the model's meshes, or null when nothing looks like one.</returns>
    public static int? Find(ModFile model)
    {
        ArgumentNullException.ThrowIfNull(model);

        int best = -1;
        int bestScore = 0;

        for (int i = 0; i < model.Meshes.Count; i++)
        {
            int score = 0;

            foreach (ModSubmesh submesh in model.Meshes[i].Submeshes)
            {
                string texture = submesh.TextureName.ToUpperInvariant();

                foreach ((string fragment, int weight) in Marks)
                {
                    if (texture.Contains(fragment, StringComparison.Ordinal))
                    {
                        score += weight;
                        break;
                    }
                }
            }

            if (score > bestScore)
            {
                bestScore = score;
                best = i;
            }
        }

        return best >= 0 ? best : null;
    }

    /// <summary>Where a mesh's own origin sits in the model.</summary>
    /// <param name="model">The character.</param>
    /// <param name="mesh">Which mesh.</param>
    /// <returns>The point the mesh turns about, in the model's space.</returns>
    public static Vector3 PivotOf(ModFile model, int mesh)
    {
        ArgumentNullException.ThrowIfNull(model);

        return mesh >= 0 && mesh < model.Meshes.Count
            ? model.Meshes[mesh].MeshToLocal.Translation
            : Vector3.Zero;
    }
}
