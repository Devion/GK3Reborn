using GK3Reborn.Formats.Animation;
using GK3Reborn.Formats.Models;

namespace GK3Reborn.Game.Actors;

/// <summary>
/// Puts a character into the clothes their point in the story calls for.
/// </summary>
public static class Wardrobe
{
    /// <summary>Repaints a character's model with what a clothes animation says.</summary>
    /// <param name="model">The model as read from its file.</param>
    /// <param name="name">The name the scene placed it under — <c>gra</c>.</param>
    /// <param name="clothes">The clothes animation.</param>
    /// <param name="skipped">Told about a line that names nothing this model has.</param>
    /// <returns>
    /// The model wearing them, or the same model when the animation changes nothing on it.
    /// </returns>
    public static ModFile Dress(
        ModFile model,
        string name,
        AnimationFile clothes,
        Action<AnimationTexture>? skipped = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(clothes);

        List<ModMesh>? meshes = null;

        foreach (AnimationTexture line in clothes.Textures)
        {
            if (!name.Equals(line.Model, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (line.Mesh < 0 || line.Mesh >= model.Meshes.Count ||
                line.Submesh < 0 || line.Submesh >= model.Meshes[line.Mesh].Submeshes.Count)
            {
                skipped?.Invoke(line);
                continue;
            }

            meshes ??= [.. model.Meshes];

            ModMesh mesh = meshes[line.Mesh];
            List<ModSubmesh> groups = [.. mesh.Submeshes];
            groups[line.Submesh] = groups[line.Submesh] with { TextureName = line.Texture };
            meshes[line.Mesh] = mesh with { Submeshes = groups };
        }

        return meshes is null
            ? model
            : ModFile.FromMeshes(model.Name, meshes, model.IsBillboard);
    }
}
