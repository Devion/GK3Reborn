using GK3Reborn.Formats.Animation;
using GK3Reborn.Formats.Models;

namespace GK3Reborn.Game.Actors;

/// <summary>
/// Puts a character into the clothes their point in the story calls for.
/// </summary>
/// <remarks>
/// <para>
/// GK3's people own one model each and change their clothes by repainting it. A character
/// who has more than one outfit ships with a one-frame animation per outfit — Grace's are
/// <c>GraClothes110a</c>, <c>GraClothes207a</c> and <c>GraClothes307a</c> — holding nothing
/// but <c>[MTEXTURES]</c> lines naming a mesh, a group within it and the texture to draw it
/// with. <c>CHARACTERS.TXT</c> says which one applies when, and
/// <see cref="CharacterConfig.ClothingFor"/> decides between them.
/// </para>
/// <para>
/// <b>The default outfit is one of these too</b>, which is why leaving the whole mechanism
/// out was not a subtle defect: the shipped models are painted in undyed placeholder
/// textures, so every one of the nine people standing round Poussin's tomb on the second
/// morning wore a plain white shirt, and Grace wore the first day's clothes for all three
/// days.
/// </para>
/// <para>
/// The repaint is done to the model as it is read rather than played into the room the way
/// the original plays it, because a change of clothes is a fact about the character and not
/// something that happens: baking it in means the room's textures are loaded once, already
/// correct, and that a still rendered without ever running a frame is dressed the same as
/// the game.
/// </para>
/// </remarks>
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
    /// <remarks>
    /// <para>
    /// Only lines naming <paramref name="name"/> are applied. The original resolves the
    /// model name against the whole room, so a character whose entry points at somebody
    /// else's animation dresses that somebody instead — <c>[WIL]</c>'s clothes are
    /// <c>Wi2ClothesCamo</c>, which paints <c>wi2</c> — and the two readings only differ
    /// when both models stand in one room, which none of them ever do.
    /// </para>
    /// <para>
    /// Every line is applied whatever frame it names. These animations are one frame long
    /// and all 34 of the corpus's lines are on frame zero; a later one would be a change of
    /// clothes that happened a fraction of a second into the scene, which is not what the
    /// files describe.
    /// </para>
    /// </remarks>
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
