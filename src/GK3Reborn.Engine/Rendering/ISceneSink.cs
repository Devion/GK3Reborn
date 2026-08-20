using System.Numerics;
using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Formats.Lightmaps;
using GK3Reborn.Formats.Models;
using GK3Reborn.Formats.Scenes;

namespace GK3Reborn.Rendering;

/// <summary>A model that has been put into a scene, and can still be moved.</summary>
/// <param name="Id">Which placement, as the sink numbers them.</param>
/// <remarks>
/// Deliberately opaque. What a placement <em>is</em> depends entirely on how the backend
/// keeps its geometry, and the loader has no business knowing.
/// </remarks>
public readonly record struct ModelPlacement(int Id)
{
    /// <summary>A placement that refers to nothing.</summary>
    public static ModelPlacement None => new(-1);

    /// <summary>Whether it refers to anything.</summary>
    public bool Exists => Id >= 0;
}

/// <summary>
/// Somewhere a loaded scene can be put.
/// </summary>
/// <remarks>
/// <para>
/// Assembling a scene means reading half a dozen file formats and deciding which of them
/// apply; putting it on a GPU means buffers, descriptor sets and a specific graphics API.
/// Those are separate jobs, and this interface is the seam between them: loading talks to
/// this, and only the backend implements it.
/// </para>
/// <para>
/// The seam is enforced rather than assumed — the layering tests forbid anything under
/// <c>Game</c> from naming the Vulkan backend, which is what pushed this interface into
/// existence rather than letting the loader write to the backend directly.
/// </para>
/// </remarks>
public interface ISceneSink
{
    /// <summary>Lower corner of everything loaded, in world space.</summary>
    Vector3 Minimum { get; }

    /// <summary>Upper corner of everything loaded, in world space.</summary>
    Vector3 Maximum { get; }

    /// <summary>How many distinct textures have been given to it.</summary>
    int TextureCount { get; }

    /// <summary>Total triangles loaded.</summary>
    int TriangleCount { get; }

    /// <summary>Adds a texture under a name meshes can reference.</summary>
    /// <param name="name">Texture name, matched case-insensitively.</param>
    /// <param name="image">The decoded image.</param>
    void AddTexture(string name, DecodedImage image);

    /// <summary>Adds a model.</summary>
    /// <param name="model">The parsed model.</param>
    /// <param name="transform">Where to place it, or null for its authored position.</param>
    /// <param name="meshTurns">
    /// Extra rotations for particular meshes, applied about each mesh's own origin before
    /// it is placed on the model. GK3's people have no skeleton — a character is a dozen
    /// separate meshes, each with its own transform — so this is how a head turns.
    /// </param>
    /// <returns>A handle for moving the model's parts once it is standing.</returns>
    ModelPlacement Add(
        ModFile model,
        Matrix4x4? transform = null,
        IReadOnlyDictionary<int, Matrix4x4>? meshTurns = null);

    /// <summary>Moves one mesh of a model that is already standing.</summary>
    /// <param name="placement">The handle its <see cref="Add"/> returned.</param>
    /// <param name="mesh">Which of the model's meshes.</param>
    /// <param name="turn">
    /// A rotation about the mesh's own origin, replacing whatever it was placed with.
    /// </param>
    /// <remarks>
    /// What makes a glance a movement rather than a pose. A character has no skeleton, so
    /// there is nothing else to animate: the head is a mesh, and turning it is putting it
    /// somewhere else between one frame and the next.
    /// </remarks>
    void TurnMesh(ModelPlacement placement, int mesh, Matrix4x4 turn);

    /// <summary>Moves a whole model that is already standing.</summary>
    /// <param name="placement">The handle its <see cref="Add"/> returned.</param>
    /// <param name="transform">Where it is now, replacing where it was placed.</param>
    /// <remarks>
    /// <para>
    /// An actor walking. Every mesh moves together, keeping whatever <see cref="TurnMesh"/>
    /// has done to any of them, because a head that is turned stays turned while its owner
    /// crosses the room.
    /// </para>
    /// <para>
    /// The same reasoning as turning a head: GK3's characters have no skeleton, so there is
    /// nothing to animate but where their meshes are.
    /// </para>
    /// </remarks>
    void MoveModel(ModelPlacement placement, Matrix4x4 transform);

    /// <summary>Adds a scene's geometry and its baked lighting.</summary>
    /// <param name="scene">The parsed scene.</param>
    /// <param name="lightmaps">Its lightmaps, in surface order, if any.</param>
    /// <param name="hiddenObjects">
    /// Names of objects inside the geometry that must not be drawn, such as hit-test
    /// volumes.
    /// </param>
    void AddScene(BspFile scene, MulFile? lightmaps = null, IReadOnlySet<string>? hiddenObjects = null);
}
