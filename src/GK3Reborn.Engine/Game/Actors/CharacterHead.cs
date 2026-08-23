using System.Numerics;
using GK3Reborn.Formats.Models;

namespace GK3Reborn.Game.Actors;

/// <summary>
/// Which part of a character is the head.
/// </summary>
/// <remarks>
/// <para>
/// GK3's people have no skeleton. A character is a dozen or so separate meshes, each with
/// its own transform, and an animation moves those transforms and the vertices inside them
/// — Gabriel is thirteen meshes. That is worse than a skeleton for most purposes and
/// better for this one: turning a head is moving one mesh, and the pivot it turns about is
/// the mesh's own origin, which sits where the neck is because that is where the artist
/// put it.
/// </para>
/// <para>
/// The meshes are not named — the format has no room for it — so the head is found by what
/// it is painted with. Every character in the game has a face, eyelids, a mouth and a
/// forehead as separate textures, named for them: <c>GAB_FACE</c>, <c>GRA_EYELIDS</c>,
/// <c>MOS_MOUTH00</c>. A mesh wearing those is a head.
/// </para>
/// </remarks>
public static class CharacterHead
{
    /// <summary>What a head is painted with.</summary>
    /// <remarks>
    /// Ordered by how sure each makes it. A mouth or an eyelid is only ever on a head; hair
    /// is nearly always, but a hat or a wig on a shelf would wear it too, so it counts for
    /// less.
    /// </remarks>
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
    /// <remarks>
    /// Null rather than a guess. A prop has no head, and turning some arbitrary part of a
    /// chair towards the player would be a stranger bug than a chair that does not move.
    /// </remarks>
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
