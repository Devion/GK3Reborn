using System.Numerics;
using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Formats.Lightmaps;
using GK3Reborn.Formats.Models;
using GK3Reborn.Formats.Scenes;

namespace GK3Reborn.Rendering;

/// <summary>A model that has been put into a scene, and can still be moved.</summary>
/// <param name="Id">Which placement, as the sink numbers them.</param>
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
public interface ISceneSink
{
    /// <summary>
    /// Something to do between pieces of work, offered often while a scene is assembled.
    /// </summary>
    Action? Progress { get; set; }

    /// <summary>Lower corner of everything loaded, in world space.</summary>
    Vector3 Minimum { get; }

    /// <summary>Upper corner of everything loaded, in world space.</summary>
    Vector3 Maximum { get; }

    /// <summary>How many distinct textures have been given to it.</summary>
    int TextureCount { get; }

    /// <summary>Total triangles loaded.</summary>
    int TriangleCount { get; }

    /// <summary>Begins a run of texture uploads to be recorded together and submitted once.</summary>
    /// <remarks>
    /// Every texture submitted on its own drains the queue, and a room is hundreds of them.
    /// Whatever is added between this and <see cref="EndTextures"/> goes into one submission,
    /// or into as few as the staging it holds allows. Nothing may present a frame while a run
    /// is open: a frame of its own would ask the device for the list this is holding.
    /// </remarks>
    void BeginTextures();

    /// <summary>Submits the run and waits for it, leaving nothing open.</summary>
    void EndTextures();

    /// <summary>Adds a texture under a name meshes can reference.</summary>
    /// <param name="name">Texture name, matched case-insensitively.</param>
    /// <param name="image">The decoded image.</param>
    void AddTexture(string name, DecodedImage image);

    /// <summary>Gives a surface a normal map.</summary>
    /// <param name="name">The <em>colour</em> texture it belongs to.</param>
    /// <param name="image">The decoded map.</param>
    void AddNormalMap(string name, DecodedImage image);

    /// <summary>Adds a texture that is already in a block format.</summary>
    /// <param name="name">Texture name, matched case-insensitively.</param>
    /// <param name="image">The compressed levels.</param>
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
    void SetVisible(ModelPlacement placement, bool visible);

    /// <summary>Draws one submesh of a model, or stops drawing it.</summary>
    /// <param name="placement">The handle its <see cref="Add"/> returned.</param>
    /// <param name="mesh">Which mesh group.</param>
    /// <param name="submesh">Which submesh within it.</param>
    /// <param name="visible">Whether that part is drawn.</param>
    void SetPartVisible(ModelPlacement placement, int mesh, int submesh, bool visible);

    /// <summary>Makes a standing model its own light source, or stops.</summary>
    /// <param name="placement">The handle its <see cref="Add"/> returned.</param>
    /// <param name="selfLit">Whether it is drawn at full brightness and never shaded.</param>
    void SetSelfLit(ModelPlacement placement, bool selfLit);

    /// <summary>Turns a standing model to the camera, and keeps turning it.</summary>
    /// <param name="placement">The handle its <see cref="Add"/> returned.</param>
    void FaceCamera(ModelPlacement placement);

    /// <summary>Paints one of a standing model's textures with something else.</summary>
    /// <param name="placement">The handle its <see cref="Add"/> returned.</param>
    /// <param name="texture">The texture the model was built with, such as <c>GAB_FACE</c>.</param>
    /// <param name="painted">What to draw instead, or null to put the model's own back.</param>
    void Repaint(ModelPlacement placement, string texture, string? painted);

    /// <summary>Moves one mesh of a model that is already standing.</summary>
    /// <param name="placement">The handle its <see cref="Add"/> returned.</param>
    /// <param name="mesh">Which of the model's meshes.</param>
    /// <param name="turn">
    /// A rotation about the mesh's own origin, replacing whatever it was placed with.
    /// </param>
    void TurnMesh(ModelPlacement placement, int mesh, Matrix4x4 turn);

    /// <summary>Puts one mesh of a model where an animation says it goes.</summary>
    /// <param name="placement">The handle its <see cref="Add"/> returned.</param>
    /// <param name="mesh">Which of the model's meshes.</param>
    /// <param name="meshToLocal">
    /// Where the mesh sits in the model's own space, <em>replacing</em> the one the model
    /// was built with.
    /// </param>
    void PoseMesh(ModelPlacement placement, int mesh, Matrix4x4 meshToLocal);

    /// <summary>Changes the shape of one submesh of a model that is already standing.</summary>
    /// <param name="placement">The handle its <see cref="Add"/> returned.</param>
    /// <param name="mesh">Which of the model's meshes.</param>
    /// <param name="submesh">Which submesh within that mesh.</param>
    /// <param name="positions">Every vertex, in mesh space, in the model's own order.</param>
    void ShapeMesh(
        ModelPlacement placement, int mesh, int submesh, IReadOnlyList<Vector3> positions);

    /// <summary>Moves a whole model that is already standing.</summary>
    /// <param name="placement">The handle its <see cref="Add"/> returned.</param>
    /// <param name="transform">Where it is now, replacing where it was placed.</param>
    void MoveModel(ModelPlacement placement, Matrix4x4 transform);

    /// <summary>Where a model stands now.</summary>
    /// <param name="placement">Which model.</param>
    /// <returns>Its transform, or the identity when there is no such model.</returns>
    Matrix4x4 TransformOf(ModelPlacement placement);

    /// <summary>Gives the room its sky.</summary>
    /// <param name="faces">
    /// The six sides in the order the hardware wants them — right, left, up, down, front,
    /// back — all square and all the same size.
    /// </param>
    /// <param name="azimuth">How far the sky is turned, in radians about the vertical.</param>
    void SetSkybox(IReadOnlyList<DecodedImage> faces, float azimuth);

    /// <summary>Gives the room a reconstructed horizon: terrain where the painting was.</summary>
    /// <param name="backdrop">The terrain, its blend layers and its tiles.</param>
    void SetTerrain(TerrainBackdrop backdrop);

    /// <summary>
    /// Says which surfaces' height maps will be wanted as numbers rather than as pictures.
    /// </summary>
    /// <param name="textures">The colour texture names.</param>
    void KeepRelief(IReadOnlySet<string> textures);

    /// <summary>
    /// Says which textures' relief is cut into the geometry wherever they appear,
    /// rather than only on the floor.
    /// </summary>
    /// <param name="textures">The colour texture names.</param>
    void ReliefEverywhere(IReadOnlySet<string> textures);

    /// <summary>Says which textures are foliage, and so move in the wind.</summary>
    /// <param name="textures">The colour texture names.</param>
    void MoveInWind(IReadOnlySet<string> textures);

    /// <summary>
    /// Says which textures are the ground of a room that is out of doors, and how far each
    /// may vary from the picture painted on it.
    /// </summary>
    /// <param name="textures">
    /// Colour texture names against how much they vary, nought to one. See
    /// <see cref="GroundVariation"/>; a texture that is not named varies not at all, which
    /// is every surface in the game before this.
    /// </param>
    void VaryGround(IReadOnlyDictionary<string, float> textures);

    /// <summary>
    /// Says what holds an outdoor room's ground still, so that weathering it leaves
    /// everything standing on it where it stands.
    /// </summary>
    /// <param name="anchors">
    /// Where the scene places models, whose feet were set against the ground as the 1999
    /// files describe it.
    /// </param>
    /// <param name="walkable">
    /// Whether an actor may stand at a point on X and Z, or null where the room lets them
    /// stand nowhere. What is walked on is read from the original geometry, so what is
    /// walked on may not move.
    /// </param>
    void HoldGround(IReadOnlyList<GroundAnchor> anchors, Func<float, float, bool>? walkable);

    /// <summary>
    /// Draws one of the room's own named objects, or stops drawing it.
    /// </summary>
    /// <param name="objectName">The object's name, as the geometry file records it.</param>
    /// <param name="visible">Whether it is drawn.</param>
    /// <returns>True when the room has an object by that name.</returns>
    bool SetSceneObjectVisible(string objectName, bool visible);

    /// <summary>
    /// Paints one of the room's own named objects with something else.
    /// </summary>
    /// <param name="objectName">The object's name, as the geometry file records it.</param>
    /// <param name="texture">What to draw on it, or null to put the room's own back.</param>
    /// <returns>True when the room has an object by that name.</returns>
    bool PaintSceneObject(string objectName, string? texture);

    /// <summary>Every one of the room's own named objects, with the box it fills.</summary>
    /// <returns>The objects, in no particular order; empty when the room has none.</returns>
    IReadOnlyList<(string Name, Vector3 Minimum, Vector3 Maximum)> SceneObjectBoxes();


    /// <summary>
    /// Gives the room a different bake of the lighting it already has.
    /// </summary>
    /// <param name="lightmaps">The replacement lightmaps, in surface order.</param>
    /// <returns>True when the room had a bake to replace.</returns>
    bool SwapLightmaps(MulFile lightmaps);

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
    /// <param name="enhanced">
    /// Improved geometry for some of the room's objects, or null to draw every object from
    /// the room itself.
    /// </param>
    void AddScene(
        BspFile scene,
        MulFile? lightmaps = null,
        IReadOnlySet<string>? hiddenObjects = null,
        string? floorObject = null,
        IReadOnlySet<int>? hiddenSurfaces = null,
        SceneOverlay? enhanced = null);
}
