using System.Numerics;
using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Formats.Lightmaps;
using GK3Reborn.Formats.Models;
using GK3Reborn.Formats.Scenes;

namespace GK3Reborn.Rendering;

/// <summary>
/// Somewhere to put a scene when there is nothing to draw it with.
/// </summary>
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

    /// <summary>How many of the room's objects were drawn from improved geometry.</summary>
    public int EnhancedObjects { get; private set; }

    /// <summary>What those objects came to.</summary>
    public int EnhancedTriangles { get; private set; }

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
    public void TurnMesh(ModelPlacement placement, int mesh, Matrix4x4 turn)
    {
    }

    /// <inheritdoc/>
    public void BeginTextures()
    {
    }

    /// <inheritdoc/>
    public void EndTextures()
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

    /// <summary>How many textures were named as the ground of a room out of doors.</summary>
    public int GroundTextureCount { get; private set; }

    /// <inheritdoc/>
    public void VaryGround(IReadOnlyDictionary<string, float> textures)
    {
        ArgumentNullException.ThrowIfNull(textures);
        GroundTextureCount = textures.Count;
    }

    /// <summary>How many models were named as holding the ground still.</summary>
    public int GroundAnchorCount { get; private set; }

    /// <inheritdoc/>
    public void HoldGround(IReadOnlyList<GroundAnchor> anchors, Func<float, float, bool>? walkable)
    {
        ArgumentNullException.ThrowIfNull(anchors);
        GroundAnchorCount = anchors.Count;
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
    public bool HasTexture(string name) => false;

    /// <inheritdoc/>
    public void ShapeMesh(
        ModelPlacement placement, int mesh, int submesh, IReadOnlyList<Vector3> positions)
    {
    }

    /// <inheritdoc/>
    public void PoseMesh(ModelPlacement placement, int mesh, Matrix4x4 meshToLocal)
    {
    }

    /// <summary>How many times something asked for a texture to be painted over.</summary>
    public int RepaintCount { get; private set; }

    /// <inheritdoc/>
    public void Repaint(ModelPlacement placement, string texture, string? painted) => RepaintCount++;

    /// <summary>How many models the scene asked to be kept out of sight.</summary>
    public int HiddenCount { get; private set; }

    /// <inheritdoc/>
    public void SetVisible(ModelPlacement placement, bool visible)
    {
        if (!visible)
        {
            HiddenCount++;
        }
    }

    /// <summary>How many single submeshes something has hidden.</summary>
    public int HiddenPartCount { get; private set; }

    /// <inheritdoc/>
    public void SetPartVisible(ModelPlacement placement, int mesh, int submesh, bool visible)
    {
        if (!visible)
        {
            HiddenPartCount++;
        }
    }

    /// <summary>How many models something has made their own light source.</summary>
    public int SelfLitCount { get; private set; }

    /// <inheritdoc/>
    public void SetSelfLit(ModelPlacement placement, bool selfLit)
    {
        if (selfLit)
        {
            SelfLitCount++;
        }
    }

    /// <summary>How many models were left turning to face the camera.</summary>
    public int BillboardCount => _billboards.Count;

    /// <summary>Which placements are billboards, in the order they were declared.</summary>
    public IReadOnlyList<ModelPlacement> Billboards => _billboards;

    private readonly List<ModelPlacement> _billboards = [];

    /// <inheritdoc/>
    public void FaceCamera(ModelPlacement placement)
    {
        if (placement.Exists)
        {
            _billboards.Add(placement);
        }
    }

    /// <inheritdoc/>
    public void MoveModel(ModelPlacement placement, Matrix4x4 transform)
    {
        _standing[placement.Id] = transform;
    }

    /// <inheritdoc/>
    public Matrix4x4 TransformOf(ModelPlacement placement) =>
        _standing.TryGetValue(placement.Id, out Matrix4x4 where) ? where : Matrix4x4.Identity;

    /// <inheritdoc/>
    public void KeepRelief(IReadOnlySet<string> textures)
    {
        ArgumentNullException.ThrowIfNull(textures);

        ReliefTextureCount = textures.Count;
    }

    /// <summary>How many textures the loader asked to keep a readable height map for.</summary>
    public int ReliefTextureCount { get; private set; }

    /// <summary>Names of the room's own objects a script has shown or hidden.</summary>
    public List<string> SceneObjectsToggled { get; } = [];

    /// <inheritdoc/>
    public bool SetSceneObjectVisible(string objectName, bool visible)
    {
        ArgumentNullException.ThrowIfNull(objectName);

        SceneObjectsToggled.Add(objectName);

        return _sceneObjects.Contains(objectName);
    }

    /// <summary>The room's own objects a script or an animation has repainted, and with what.</summary>
    public List<(string Object, string? Texture)> SceneObjectsPainted { get; } = [];

    /// <inheritdoc/>
    public bool PaintSceneObject(string objectName, string? texture)
    {
        ArgumentNullException.ThrowIfNull(objectName);

        SceneObjectsPainted.Add((objectName, texture));

        return _sceneObjects.Contains(objectName);
    }

    /// <inheritdoc/>
    public IReadOnlyList<(string Name, Vector3 Minimum, Vector3 Maximum)> SceneObjectBoxes() =>
        [];

    /// <summary>The replacement bakes a script has handed the room, in order.</summary>
    public List<string> LightmapsSwapped { get; } = [];

    /// <inheritdoc/>
    public bool SwapLightmaps(MulFile lightmaps)
    {
        ArgumentNullException.ThrowIfNull(lightmaps);

        LightmapsSwapped.Add(lightmaps.Name);

        return _baked;
    }

    private bool _baked;

    private readonly HashSet<string> _sceneObjects = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public void AddScene(
        BspFile scene,
        MulFile? lightmaps = null,
        IReadOnlySet<string>? hiddenObjects = null,
        string? floorObject = null,
        IReadOnlySet<int>? hiddenSurfaces = null,
        SceneOverlay? enhanced = null)
    {
        ArgumentNullException.ThrowIfNull(scene);

        EnhancedObjects = enhanced?.Objects.Count ?? 0;
        EnhancedTriangles = enhanced?.TriangleCount ?? 0;

        foreach (string name in scene.ObjectNames)
        {
            _sceneObjects.Add(name);
        }

        _baked = _baked || lightmaps is not null;

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
