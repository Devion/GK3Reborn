using System.Numerics;
using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Formats.Lightmaps;
using GK3Reborn.Formats.Models;
using GK3Reborn.Formats.Scenes;

namespace GK3Reborn.Rendering;

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
    void Add(
        ModFile model,
        Matrix4x4? transform = null,
        IReadOnlyDictionary<int, Matrix4x4>? meshTurns = null);

    /// <summary>Adds a scene's geometry and its baked lighting.</summary>
    /// <param name="scene">The parsed scene.</param>
    /// <param name="lightmaps">Its lightmaps, in surface order, if any.</param>
    /// <param name="hiddenObjects">
    /// Names of objects inside the geometry that must not be drawn, such as hit-test
    /// volumes.
    /// </param>
    void AddScene(BspFile scene, MulFile? lightmaps = null, IReadOnlySet<string>? hiddenObjects = null);
}
