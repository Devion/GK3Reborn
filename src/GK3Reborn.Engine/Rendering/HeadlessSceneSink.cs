using System.Numerics;
using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Formats.Lightmaps;
using GK3Reborn.Formats.Models;
using GK3Reborn.Formats.Scenes;

namespace GK3Reborn.Rendering;

/// <summary>
/// Somewhere to put a scene when there is nothing to draw it with.
/// </summary>
/// <remarks>
/// Loading a scene and drawing it are separate jobs, and only the second needs a graphics
/// device. This takes everything the loader produces, measures it and throws it away, so
/// the whole of loading — the geometry, the bakes, every texture, the props and the people
/// — can be exercised on a build agent with no GPU. That is what makes "every scene loads
/// headlessly" a thing a command can answer rather than a thing somebody checks by
/// looking.
/// </remarks>
public sealed class HeadlessSceneSink : ISceneSink
{
    private readonly HashSet<string> _textures = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Where each model stands, for the clips that need to know.</summary>
    private readonly Dictionary<int, Matrix4x4> _standing = [];

    private Vector3 _minimum = new(float.MaxValue);
    private Vector3 _maximum = new(float.MinValue);

    /// <inheritdoc/>
    public Action? Progress { get; set; }

    /// <inheritdoc/>
    public Vector3 Minimum => _textures.Count == 0 && TriangleCount == 0 ? Vector3.Zero : _minimum;

    /// <inheritdoc/>
    public Vector3 Maximum => _textures.Count == 0 && TriangleCount == 0 ? Vector3.Zero : _maximum;

    /// <inheritdoc/>
    public int TextureCount => _textures.Count;

    /// <inheritdoc/>
    public int TriangleCount { get; private set; }

    /// <summary>How many models were placed in it.</summary>
    public int ModelCount { get; private set; }

    /// <summary>How many texels the textures cover, as a measure of what was decoded.</summary>
    public long TextureTexels { get; private set; }

    /// <inheritdoc/>
    public void AddTexture(string name, DecodedImage image)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (_textures.Add(name))
        {
            TextureTexels += (long)image.Width * image.Height;
        }
    }

    /// <inheritdoc/>
    public ModelPlacement Add(
        ModFile model,
        Matrix4x4? transform = null,
        IReadOnlyDictionary<int, Matrix4x4>? meshTurns = null)
    {
        ArgumentNullException.ThrowIfNull(model);

        Matrix4x4 placement = transform ?? Matrix4x4.Identity;
        ModelCount++;

        // Where it stands, from the start rather than only once something moves it: an
        // absolute clip is corrected against this, and a model that has never walked is
        // still somewhere.
        _standing[ModelCount - 1] = placement;

        for (int index = 0; index < model.Meshes.Count; index++)
        {
            ModMesh mesh = model.Meshes[index];

            Matrix4x4 toWorld = meshTurns is not null && meshTurns.TryGetValue(index, out Matrix4x4 turn)
                ? turn * mesh.MeshToLocal * placement
                : mesh.MeshToLocal * placement;

            foreach (ModSubmesh submesh in mesh.Submeshes)
            {
                TriangleCount += submesh.Indices.Length / 3;

                foreach (Vector3 position in submesh.Positions)
                {
                    Grow(Vector3.Transform(position, toWorld));
                }
            }
        }

        return new ModelPlacement(ModelCount - 1);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Nothing to move: this measures what was loaded rather than keeping it. A sweep that
    /// wanted to know where a head ended up would have to draw it.
    /// </remarks>
    public void TurnMesh(ModelPlacement placement, int mesh, Matrix4x4 turn)
    {
    }

    /// <summary>How many normal maps the scene gave, for a sweep that wants to count.</summary>
    public int NormalMapCount { get; private set; }

    /// <summary>How many ORM maps the scene gave.</summary>
    public int OrmMapCount { get; private set; }

    /// <summary>How many height maps the scene gave.</summary>
    public int HeightMapCount { get; private set; }

    /// <inheritdoc/>
    public void AddNormalMap(string name, DecodedImage image) => NormalMapCount++;

    /// <inheritdoc/>
    public void AddTexture(string name, CompressedImage image) =>
        AddTexture(name, new DecodedImage(image.Width, image.Height, [], false, "block"));

    /// <inheritdoc/>
    public void AddNormalMap(string name, CompressedImage image) => NormalMapCount++;

    /// <inheritdoc/>
    public bool HasNormalMap(string name) => false;

    /// <inheritdoc/>
    public void AddOrmMap(string name, DecodedImage image) => OrmMapCount++;

    /// <inheritdoc/>
    public void AddOrmMap(string name, CompressedImage image) => OrmMapCount++;

    /// <inheritdoc/>
    public bool HasOrmMap(string name) => false;

    /// <inheritdoc/>
    public void AddHeightMap(string name, DecodedImage image) => HeightMapCount++;

    /// <inheritdoc/>
    public void AddHeightMap(string name, CompressedImage image) => HeightMapCount++;

    /// <inheritdoc/>
    public bool HasHeightMap(string name) => false;

    /// <summary>How many textures were named for relief beyond the floor.</summary>
    public int EverywhereReliefCount { get; private set; }

    /// <inheritdoc/>
    public void ReliefEverywhere(IReadOnlySet<string> textures)
    {
        ArgumentNullException.ThrowIfNull(textures);
        EverywhereReliefCount = textures.Count;
    }

    /// <summary>How many textures were named as foliage that moves.</summary>
    public int WindTextureCount { get; private set; }

    /// <inheritdoc/>
    public void MoveInWind(IReadOnlySet<string> textures)
    {
        ArgumentNullException.ThrowIfNull(textures);
        WindTextureCount = textures.Count;
    }

    /// <summary>How many sides of a sky the scene gave, for a sweep that wants to count.</summary>
    public int SkyboxFaces { get; private set; }

    /// <inheritdoc/>
    public void SetSkybox(IReadOnlyList<DecodedImage> faces, float azimuth)
    {
        ArgumentNullException.ThrowIfNull(faces);
        SkyboxFaces = faces.Count;
    }

    /// <summary>Whether the scene gave a reconstructed horizon, for a sweep to count.</summary>
    public bool HasTerrain { get; private set; }

    /// <inheritdoc/>
    public void SetTerrain(TerrainBackdrop backdrop)
    {
        ArgumentNullException.ThrowIfNull(backdrop);
        HasTerrain = true;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// A sweep counts distinct textures, so it wants to be told about every one; saying no
    /// here keeps the count honest and costs a decode nobody is timing.
    /// </remarks>
    public bool HasTexture(string name) => false;

    /// <inheritdoc/>
    /// <remarks>Nothing to reshape, for the same reason as posing.</remarks>
    public void ShapeMesh(
        ModelPlacement placement, int mesh, int submesh, IReadOnlyList<Vector3> positions)
    {
    }

    /// <inheritdoc/>
    /// <remarks>Nothing to pose: a sweep measures what was loaded rather than keeping it.</remarks>
    public void PoseMesh(ModelPlacement placement, int mesh, Matrix4x4 meshToLocal)
    {
    }

    /// <summary>How many times something asked for a texture to be painted over.</summary>
    public int RepaintCount { get; private set; }

    /// <inheritdoc/>
    /// <remarks>Counted rather than obeyed; there is no picture here to change.</remarks>
    public void Repaint(ModelPlacement placement, string texture, string? painted) => RepaintCount++;

    /// <summary>How many models the scene asked to be kept out of sight.</summary>
    public int HiddenCount { get; private set; }

    /// <inheritdoc/>
    /// <remarks>
    /// Counted rather than obeyed. A sweep wants to know a scene hides things — the
    /// staging a script later shows — and has nothing to hide them from.
    /// </remarks>
    public void SetVisible(ModelPlacement placement, bool visible)
    {
        if (!visible)
        {
            HiddenCount++;
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Nothing to do: this counts what a scene contains rather than drawing it, and an
    /// actor who has walked contains exactly what they did before.
    /// </remarks>
    public void MoveModel(ModelPlacement placement, Matrix4x4 transform)
    {
        _standing[placement.Id] = transform;
    }

    /// <inheritdoc/>
    public Matrix4x4 TransformOf(ModelPlacement placement) =>
        _standing.TryGetValue(placement.Id, out Matrix4x4 where) ? where : Matrix4x4.Identity;

    /// <inheritdoc/>
    /// <remarks>Nothing to keep: this counts a scene rather than displacing one.</remarks>
    public void KeepRelief(IReadOnlySet<string> textures)
    {
        ArgumentNullException.ThrowIfNull(textures);

        ReliefTextureCount = textures.Count;
    }

    /// <summary>How many textures the loader asked to keep a readable height map for.</summary>
    public int ReliefTextureCount { get; private set; }

    /// <summary>Names of the room's own objects a script has shown or hidden.</summary>
    /// <remarks>
    /// Counted rather than drawn, like everything else here. What it is for is the sweep:
    /// a scene whose script hides an object the geometry does not contain is a name that
    /// will silently do nothing in the game.
    /// </remarks>
    public List<string> SceneObjectsToggled { get; } = [];

    /// <inheritdoc/>
    public bool SetSceneObjectVisible(string objectName, bool visible)
    {
        ArgumentNullException.ThrowIfNull(objectName);

        SceneObjectsToggled.Add(objectName);

        return _sceneObjects.Contains(objectName);
    }

    private readonly HashSet<string> _sceneObjects = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public void AddScene(
        BspFile scene,
        MulFile? lightmaps = null,
        IReadOnlySet<string>? hiddenObjects = null,
        string? floorObject = null,
        IReadOnlySet<int>? hiddenSurfaces = null)
    {
        ArgumentNullException.ThrowIfNull(scene);

        foreach (string name in scene.ObjectNames)
        {
            _sceneObjects.Add(name);
        }

        TriangleCount += scene.TriangleCount;

        foreach (Vector3 vertex in scene.Vertices)
        {
            Grow(vertex);
        }
    }

    private void Grow(Vector3 point)
    {
        _minimum = Vector3.Min(_minimum, point);
        _maximum = Vector3.Max(_maximum, point);
    }
}
