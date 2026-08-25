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

    /// <summary>Gives a surface a normal map.</summary>
    /// <param name="name">The <em>colour</em> texture it belongs to.</param>
    /// <param name="image">The decoded map.</param>
    /// <remarks>
    /// Uploaded as data rather than colour. A normal map's channels are a direction, and
    /// putting one through the sRGB path bends every normal towards flat — which reads as a
    /// weak, waxy surface rather than as the colour-space bug it is.
    /// </remarks>
    void AddNormalMap(string name, DecodedImage image);

    /// <summary>Adds a texture that is already in a block format.</summary>
    /// <param name="name">Texture name, matched case-insensitively.</param>
    /// <param name="image">The compressed levels.</param>
    /// <remarks>
    /// Uploaded as it stands, mip chain and all. Nothing may be done to it on the way — a
    /// colour key cannot be applied to blocks — so a texture that needs one is the loader's
    /// business to send down the decoded path instead.
    /// </remarks>
    void AddTexture(string name, CompressedImage image);

    /// <summary>Gives a surface a normal map that is already in a block format.</summary>
    /// <param name="name">The <em>colour</em> texture it belongs to.</param>
    /// <param name="image">The compressed levels.</param>
    void AddNormalMap(string name, CompressedImage image);

    /// <summary>Whether a surface's normal map has already been given.</summary>
    /// <param name="name">The colour texture's name.</param>
    /// <returns>True when there is nothing to read, decode or upload.</returns>
    bool HasNormalMap(string name);

    /// <summary>Gives a surface its packed occlusion, roughness and metalness.</summary>
    /// <param name="name">The <em>colour</em> texture it belongs to.</param>
    /// <param name="image">The decoded map.</param>
    /// <remarks>
    /// <para>
    /// Red is ambient occlusion, green is roughness, blue is metalness — the glTF packing,
    /// which every generator and every authoring tool already speaks.
    /// </para>
    /// <para>
    /// Uploaded as data rather than colour, for the same reason a normal map is: these are
    /// three measurements that happen to be stored in a picture.
    /// </para>
    /// </remarks>
    void AddOrmMap(string name, DecodedImage image);

    /// <summary>Gives a surface an ORM map that is already in a block format.</summary>
    /// <param name="name">The <em>colour</em> texture it belongs to.</param>
    /// <param name="image">The compressed levels.</param>
    void AddOrmMap(string name, CompressedImage image);

    /// <summary>Whether a surface's ORM map has already been given.</summary>
    /// <param name="name">The colour texture's name.</param>
    /// <returns>True when there is nothing to read, decode or upload.</returns>
    bool HasOrmMap(string name);

    /// <summary>Gives a surface a height map.</summary>
    /// <param name="name">The <em>colour</em> texture it belongs to.</param>
    /// <param name="image">The decoded map.</param>
    /// <remarks>
    /// A distance either side of the modelled surface, mid grey being the surface itself.
    /// What reads it is parallax, which is a texture-coordinate offset rather than real
    /// displacement: it deepens mortar courses and floorboards convincingly from most
    /// angles and does nothing whatever to a silhouette.
    /// </remarks>
    void AddHeightMap(string name, DecodedImage image);

    /// <summary>Gives a surface a height map that is already in a block format.</summary>
    /// <param name="name">The <em>colour</em> texture it belongs to.</param>
    /// <param name="image">The compressed levels.</param>
    void AddHeightMap(string name, CompressedImage image);

    /// <summary>Whether a surface's height map has already been given.</summary>
    /// <param name="name">The colour texture's name.</param>
    /// <returns>True when there is nothing to read, decode or upload.</returns>
    bool HasHeightMap(string name);

    /// <summary>Whether a texture has already been given, under this or an earlier room.</summary>
    /// <param name="name">Its name.</param>
    /// <returns>True when there is nothing to read, decode or upload.</returns>
    /// <remarks>
    /// So a loader can skip the reading and decoding as well as the upload. Asking after
    /// decoding saves the device's time and not the CPU's, and the decoding is a third of
    /// what a room costs.
    /// </remarks>
    bool HasTexture(string name);

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

    /// <summary>Draws a model, or stops drawing it.</summary>
    /// <param name="placement">The handle its <see cref="Add"/> returned.</param>
    /// <param name="visible">Whether it is drawn.</param>
    /// <remarks>
    /// <para>
    /// GK3 scenes declare models <c>hidden</c> and scripts call <c>ShowModel</c> to bring
    /// them out. Both are ordinary staging: RC1 keeps Wilkes's moped out of sight until
    /// the scripted moment it rides past, at which point the scene shows it, plays its
    /// clip, has Gabriel watch it and hides it again.
    /// </para>
    /// <para>
    /// Hiding must take the model out of the traced world as well as out of the picture.
    /// A model that is not drawn but is still traced lies its shadow on the floor, which
    /// is a stranger thing to look at than the model would have been.
    /// </para>
    /// </remarks>
    void SetVisible(ModelPlacement placement, bool visible);

    /// <summary>Paints one of a standing model's textures with something else.</summary>
    /// <param name="placement">The handle its <see cref="Add"/> returned.</param>
    /// <param name="texture">The texture the model was built with, such as <c>GAB_FACE</c>.</param>
    /// <param name="painted">What to draw instead, or null to put the model's own back.</param>
    /// <remarks>
    /// <para>
    /// What makes a character's face move. GK3's heads have no facial geometry — a head is
    /// one mesh with one bitmap on it — so talking, blinking and raising an eyebrow are all
    /// the same operation: draw a different picture on the same triangles.
    /// </para>
    /// <para>
    /// By texture rather than by submesh, because that is the thing the caller actually
    /// knows. A face is "wherever this model draws <c>GAB_FACE</c>"; which submesh of which
    /// mesh group that happens to be is the model's business and varies per character.
    /// </para>
    /// <para>
    /// The replacement must have been given to the sink already. Normal maps stay with the
    /// <em>original</em> texture's name: a repainted face is the same surface with a
    /// different picture on it, and its bumps have not changed.
    /// </para>
    /// </remarks>
    void Repaint(ModelPlacement placement, string texture, string? painted);

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

    /// <summary>Puts one mesh of a model where an animation says it goes.</summary>
    /// <param name="placement">The handle its <see cref="Add"/> returned.</param>
    /// <param name="mesh">Which of the model's meshes.</param>
    /// <param name="meshToLocal">
    /// Where the mesh sits in the model's own space, <em>replacing</em> the one the model
    /// was built with.
    /// </param>
    /// <remarks>
    /// Distinct from <see cref="TurnMesh"/>, which applies a rotation on top of the mesh's
    /// own transform. A vertex animation stores the transform outright, and getting there
    /// through a rotation would mean inverting the model's own basis every frame to cancel
    /// it out again.
    /// </remarks>
    void PoseMesh(ModelPlacement placement, int mesh, Matrix4x4 meshToLocal);

    /// <summary>Changes the shape of one submesh of a model that is already standing.</summary>
    /// <param name="placement">The handle its <see cref="Add"/> returned.</param>
    /// <param name="mesh">Which of the model's meshes.</param>
    /// <param name="submesh">Which submesh within that mesh.</param>
    /// <param name="positions">Every vertex, in mesh space, in the model's own order.</param>
    /// <remarks>
    /// <para>
    /// What makes a character animate rather than slide. GK3's characters have no skeleton,
    /// so a clip stores where every vertex is on every frame and playing one means putting
    /// them there.
    /// </para>
    /// <para>
    /// Positions only. Normals stay as the model authored them, which is what the original
    /// does — it swaps the position stream and leaves the rest — so lighting on a deformed
    /// character is as right or wrong as it was in 1999.
    /// </para>
    /// <para>
    /// A count that does not match the submesh is ignored rather than partly applied. A
    /// clip aimed at the wrong model would otherwise rewrite whatever it happened to
    /// overlap.
    /// </para>
    /// </remarks>
    void ShapeMesh(
        ModelPlacement placement, int mesh, int submesh, IReadOnlyList<Vector3> positions);

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

    /// <summary>Where a model stands now.</summary>
    /// <param name="placement">Which model.</param>
    /// <returns>Its transform, or the identity when there is no such model.</returns>
    /// <remarks>
    /// The live one: what <see cref="Add"/> placed it with, and then whatever
    /// <see cref="MoveModel"/> last moved it to. A clip authored in the room's own
    /// coordinates has to be corrected against this, because posing a mesh places it
    /// relative to the model rather than in the room.
    /// </remarks>
    Matrix4x4 TransformOf(ModelPlacement placement);

    /// <summary>Gives the room its sky.</summary>
    /// <param name="faces">
    /// The six sides in the order the hardware wants them — right, left, up, down, front,
    /// back — all square and all the same size.
    /// </param>
    /// <param name="azimuth">How far the sky is turned, in radians about the vertical.</param>
    /// <remarks>
    /// 177 of the game's 229 scene assets name a sky. Which one depends on the time of day,
    /// and that is already decided by which asset the timeblock chose, so nothing here needs
    /// to know what time it is.
    /// </remarks>
    void SetSkybox(IReadOnlyList<DecodedImage> faces, float azimuth);

    /// <summary>
    /// Says which surfaces' height maps will be wanted as numbers rather than as pictures.
    /// </summary>
    /// <param name="textures">The colour texture names.</param>
    /// <remarks>
    /// Called before the textures themselves, because whether to keep a decoded copy of a
    /// height map has to be decided as it goes past. It is the floor's textures and no
    /// others: only a floor is displaced, and a decoded field costs a quarter of a megabyte
    /// against the game's 2,905 maps.
    /// </remarks>
    void KeepRelief(IReadOnlySet<string> textures);

    /// <summary>
    /// Draws one of the room's own named objects, or stops drawing it.
    /// </summary>
    /// <param name="objectName">The object's name, as the geometry file records it.</param>
    /// <param name="visible">Whether it is drawn.</param>
    /// <returns>True when the room has an object by that name.</returns>
    /// <remarks>
    /// Not the same as <see cref="SetVisible"/>, which is about a model the scene loaded
    /// from a file of its own. This is about the room: a curtain, a door, a van, all of
    /// which are runs of surfaces inside one mesh with a name over them. Scripts show and
    /// hide those 287 times across the corpus, and until this existed every one of those
    /// calls was recorded and dropped.
    /// </remarks>
    bool SetSceneObjectVisible(string objectName, bool visible);

    /// <summary>Adds a scene's geometry and its baked lighting.</summary>
    /// <param name="scene">The parsed scene.</param>
    /// <param name="lightmaps">Its lightmaps, in surface order, if any.</param>
    /// <param name="hiddenObjects">
    /// Names of objects inside the geometry that must not be drawn, such as hit-test
    /// volumes.
    /// </param>
    /// <param name="floorObject">
    /// The object the scene calls its floor, whose surfaces may have their relief cut into
    /// the geometry rather than only sampled by the shader, or null to displace nothing.
    /// </param>
    /// <param name="hiddenSurfaces">
    /// Individual surfaces that must not be drawn, by their index in the geometry.
    /// </param>
    /// <remarks>
    /// <para>
    /// Hiding by surface exists because hiding by name is too coarse for the thing that
    /// needed it. <c>pou_trees01</c> is two trees and a painted strip of distant hillside in
    /// one object: the trees can be replaced by modelled ones and the strip cannot, and
    /// there was no way to say so. Nineteen objects across the corpus are shaped like that,
    /// and each of them kept its flat trees because one surface in it was a backdrop.
    /// </para>
    /// <para>
    /// Indices rather than names because a surface has no name — the name belongs to the
    /// object it is part of, which is exactly the granularity this is escaping.
    /// </para>
    /// </remarks>
    void AddScene(
        BspFile scene,
        MulFile? lightmaps = null,
        IReadOnlySet<string>? hiddenObjects = null,
        string? floorObject = null,
        IReadOnlySet<int>? hiddenSurfaces = null);
}
