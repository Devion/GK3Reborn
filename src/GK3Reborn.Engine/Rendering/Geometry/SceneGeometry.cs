using System.Numerics;
using System.Runtime.InteropServices;
using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Formats.Lightmaps;
using GK3Reborn.Formats.Models;
using GK3Reborn.Formats.Scenes;
using GK3Reborn.Foundation.Diagnostics;
using GK3Reborn.Rendering.Materials;

namespace GK3Reborn.Rendering.Geometry;

/// <summary>
/// Everything a scene needs on the GPU: its meshes, its textures and its baked lighting.
/// </summary>
/// <remarks>
/// <para>
/// This is where the content pipeline and the renderer meet. A parsed model, scene or
/// lightmap set becomes vertex buffers, textures and descriptor sets here, and nothing
/// goes through an intermediate format on the way — the same parsers that produce the glTF
/// exports feed the renderer directly.
/// </para>
/// <para>
/// It knows nothing about where it is drawn. The same loaded scene records into an
/// offscreen target or into a swapchain image, which is what makes a headless regression
/// render and the running game the same code path rather than two that drift.
/// </para>
/// </remarks>
public sealed unsafe class SceneGeometry : ISceneSink, IDisposable
{
    /// <summary>The original multiplies texture by lightmap by two, in gamma space.</summary>
    /// <remarks>
    /// Doing the same multiplication in linear space needs the constant raised to the
    /// gamma, or a fully lit surface comes out at about 70% of the brightness the game
    /// showed.
    /// </remarks>
    private const float LightmapMultiplier = 4.59f;

    /// <summary>How much of a displaced surface's depth is left for the shader to march.</summary>
    /// <remarks>
    /// A quarter. The geometry is cut at whatever spacing the triangle budget affords — six
    /// or seven units on a street — and the field is averaged over a cell before a vertex is
    /// moved, so what the vertices carry is the part of the relief coarser than that and
    /// what remains is the part finer. The remainder is nearly all of the field's detail and
    /// a small share of its amplitude, and this is an estimate of that share rather than a
    /// measurement: splitting a single map into two bands exactly would mean handing the
    /// shader the complement of what the geometry took, and there is nowhere to put it.
    /// </remarks>
    private const float ResidualRelief = 0.25f;

    private readonly IGeometryDevice _device;
    private readonly List<Batch> _batches = [];

    /// <summary>
    /// Which batches belong to each of the room's own named objects.
    /// </summary>
    /// <remarks>
    /// The room is one mesh as far as the file is concerned, and a name over a run of its
    /// surfaces is all that separates the front desk from the wall behind it. Scripts show
    /// and hide those names 287 times across the corpus — a curtain drawn back, a door that
    /// becomes a prop for a cutscene — so the batches are cut along the same lines and this
    /// says which is which.
    /// </remarks>
    private readonly Dictionary<string, List<int>> _sceneObjects =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Which batches belong to which mesh of which placed model.</summary>
    /// <remarks>
    /// A mesh becomes one batch per submesh, so moving a head means moving all of them
    /// together. Kept beside the batches rather than inside them because only the handful
    /// of models that can move ever need it.
    /// </remarks>
    private readonly List<Dictionary<int, List<int>>> _placements = [];

    /// <summary>Which batch is which submesh of which placed model.</summary>
    /// <remarks>
    /// A second index over the same batches as <see cref="_placements"/>, because an
    /// <c>[MVISIBILITY]</c> line names a mesh <em>and</em> a submesh and the batch list a
    /// mesh owns cannot be indexed by submesh number: an empty group is skipped as the
    /// model is read, so the two run out of step on any model that has one.
    /// </remarks>
    private readonly List<Dictionary<(int Mesh, int Submesh), int>> _parts = [];

    /// <summary>What each placement was, so a mesh can be re-placed from its own space.</summary>
    private readonly List<(ModFile Model, Matrix4x4 Transform)> _placed = [];

    /// <summary>Shapes given since the last flush, by batch.</summary>
    private readonly Dictionary<int, IReadOnlyList<Vector3>> _pendingShapes = [];

    /// <summary>Batches whose traced geometry no longer matches what is drawn.</summary>
    private readonly HashSet<int> _posed = [];

    /// <summary>How many frames the renderer keeps in flight.</summary>
    /// <remarks>
    /// Must match <c>VulkanRenderer.FramesInFlight</c>. An animated batch keeps one vertex
    /// buffer per frame so that writing this frame's pose cannot disturb one the device has
    /// not finished reading — and one more besides, because the frame still in flight is
    /// also reading the pose before it, to know how far the surface moved.
    /// </remarks>
    private const int FramesInFlight = 2;
    private readonly TextureCache _textures;


    private readonly List<TraceableMesh> _traceable = [];

    /// <summary>The textures of this room's floor, whose height maps are kept readable.</summary>
    private readonly HashSet<string> _relief = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _rounded = [];

    private IGeometryAccelerationStructure? _rayTracing;
    private bool _finished;
    private IGeometryTexture? _lightmap;
    private IReadOnlyList<Vector4>? _lightmapRegions;
    private LightmapAtlas? _lightmapAtlas;

    /// <summary>Material sets for repainted surfaces, by what they draw.</summary>
    /// <remarks>
    /// Kept because a face comes back to the same mouth shape a dozen times a sentence, and
    /// a set that is only a handful of image views is far cheaper to keep than to build.
    /// </remarks>
    private readonly Dictionary<(string Painted, string Of, bool Lit), IGeometryMaterial> _repainted =
        new();
    private Vector3 _minimum = new(float.MaxValue);
    private Vector3 _maximum = new(float.MinValue);

    private SceneGeometry(IGeometryDevice device, TextureCache textures)
    {
        _device = device;

        // The renderer's, not this room's. A room that threw its textures away on the way
        // out spent most of the next room's load getting them back.
        _textures = textures;
    }

    /// <summary>Total triangles loaded.</summary>
    public int TriangleCount => _batches.Where(b => !b.Hidden).Sum(b => (int)b.IndexCount / 3);

    /// <summary>How many triangles are loaded, drawn or not.</summary>
    /// <remarks>
    /// The room's hit-test volumes and whatever the story is holding back are in the
    /// buffers and not in the picture, so the two numbers differ — by 7,000 triangles in
    /// the lobby. <see cref="TriangleCount"/> is what is drawn, because that is what every
    /// caller means by it.
    /// </remarks>
    public int LoadedTriangleCount => _batches.Sum(b => (int)b.IndexCount / 3);

    /// <summary>How many draws a frame costs.</summary>
    public int BatchCount => _batches.Count;

    /// <summary>Distinct textures uploaded.</summary>
    public int TextureCount => _textures.Count;

    /// <summary>How many of this room's textures the device already had.</summary>
    public int TexturesReused { get; private set; }

    /// <summary>The scene as rays see it, once <see cref="Finish"/> has built it.</summary>
    public IGeometryAccelerationStructure? RayTracing => _rayTracing;

    /// <summary>Whether and how finely a floor's relief becomes geometry.</summary>
    /// <remarks>
    /// Set by whoever loads the scene, from the player's own settings. Off, every surface
    /// takes the path it took before displacement existed.
    /// </remarks>
    public ReliefSettings Relief { get; set; } = ReliefSettings.Default;

    /// <summary>How many triangles this room's floor was cut into, or zero.</summary>
    public int DisplacedTriangles { get; private set; }

    /// <summary>How long an edge of that subdivision is, in world units, or zero.</summary>
    public float ReliefCell { get; private set; }

    /// <summary>The furthest a displaced vertex moved, in world units, or zero.</summary>
    /// <remarks>
    /// See <see cref="ReliefPlan.Moved"/>. A floor cut into a million triangles and left
    /// flat costs everything displacement costs and shows nothing, and no other number
    /// says so.
    /// </remarks>
    public float ReliefDepth { get; private set; }

    /// <summary>How far the average displaced vertex moved, in world units, or zero.</summary>
    public float ReliefTypically { get; private set; }

    /// <summary>How many of the floor's edges were held down, and how many carried on.</summary>
    public (int Pinned, int Continued) ReliefBoundary { get; private set; }

    /// <summary>How many of the room's objects were rounded off.</summary>
    public int RoundedObjects { get; private set; }

    /// <summary>Which objects those were, in the order they were met.</summary>
    /// <remarks>
    /// Named rather than counted because the list is curated by name
    /// (<see cref="RoundNames"/>) and the failure it hides is silent: a room where nothing
    /// matched looks exactly like a room where everything did.
    /// </remarks>
    public IReadOnlyList<string> Rounded => _rounded;

    /// <summary>How many triangles those objects came to, once rounded.</summary>
    public int RoundedTriangles { get; private set; }

    /// <summary>
    /// How many of the room's surfaces were moved off a surface they coincided with.
    /// </summary>
    /// <remarks>Zero for nearly every surface; see <see cref="CoplanarCards"/>.</remarks>
    public int CardsSeparated { get; private set; }

    /// <summary>Whether a keyed card is given the thickness of the thing drawn on it.</summary>
    /// <remarks>
    /// Set by whoever loads the scene, from the player's own settings. Off, every card takes
    /// the path it took before <see cref="CutoutCards"/> existed. It also gates the
    /// measurement itself, which happens as textures are uploaded and so must be decided
    /// before the first of them is.
    /// </remarks>
    public bool ThickenCutoutCards
    {
        get => _textures.MeasureCutouts;
        set => _textures.MeasureCutouts = value;
    }

    /// <summary>Whether a thickened card is also given a shadow to cast.</summary>
    /// <remarks>
    /// Separate from <see cref="ThickenCutoutCards"/> and not implied by it, because the two
    /// go wrong in unrelated ways and the comparison that tells them apart is worth being
    /// able to make: thickening is geometry anybody can see, shadowing is an instance in the
    /// acceleration structure carrying its own mask. A railing that looks right and shades
    /// the wall behind it wrongly is this, and a railing that looks flat is the other.
    /// </remarks>
    public bool CardShadows { get; set; } = true;

    /// <summary>How many of the room's cards were given a thickness.</summary>
    public int CardsThickened { get; private set; }

    /// <summary>How many triangles those cards came to, shell and all.</summary>
    public int CardTriangles { get; private set; }

    /// <summary>How many triangles their shadows are traced against.</summary>
    /// <remarks>
    /// Not a subset of <see cref="CardTriangles"/> and not comparable to it: those are drawn
    /// and these are not, and the merge that builds these means a card whose shell is two
    /// thousand triangles routinely casts its shadow with forty.
    /// </remarks>
    public int CardShadowTriangles { get; private set; }

    /// <summary>The thickest and thinnest any of them was given, in world units.</summary>
    /// <remarks>
    /// Reported because the thickness is measured rather than chosen, and a measurement that
    /// has gone wrong shows up here as a room full of cards at the clamp — which a triangle
    /// count cannot show and a screenshot of one rail cannot either.
    /// </remarks>
    public (float Thinnest, float Thickest) CardThickness { get; private set; }

    /// <summary>How many triangles the plan expected the cut to come to.</summary>
    public int ReliefExpected { get; private set; }

    /// <summary>How many floor triangles were left uncut because their tiling stood apart.</summary>
    public int ReliefSetApart { get; private set; }

    /// <summary>What each texture's surface is like, for the passes that care.</summary>
    /// <remarks>
    /// Set by whoever loads the scene. Empty by default, which makes every surface matte
    /// and every reflection cost nothing.
    /// </remarks>
    public Rendering.Materials.SurfaceFinishes Materials { get; set; } =
        Rendering.Materials.SurfaceFinishes.Empty;

    /// <summary>How many triangles are in the ray-traced representation.</summary>
    /// <remarks>
    /// Lower than <see cref="TriangleCount"/>, because alpha-tested geometry is left out;
    /// the gap is a useful measure of how much of a scene casts no shadow.
    /// </remarks>
    public int TraceableTriangleCount => _traceable.Sum(m => m.Indices.Length / 3);

    /// <inheritdoc/>
    public Action? Progress { get; set; }

    /// <summary>Lower corner of everything loaded, in world space.</summary>
    public Vector3 Minimum => _batches.Count > 0 ? _minimum : Vector3.Zero;

    /// <summary>Upper corner of everything loaded, in world space.</summary>
    public Vector3 Maximum => _batches.Count > 0 ? _maximum : Vector3.One;

    /// <summary>Creates an empty scene.</summary>
    /// <param name="device">Where the scene is put.</param>
    /// <param name="textures">The device's textures, which outlast any one room.</param>
    /// <returns>The scene.</returns>
    public static SceneGeometry Create(IGeometryDevice device, TextureCache textures)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(textures);

        return new SceneGeometry(device, textures);
    }

    /// <summary>Uploads a texture under a name models can reference.</summary>
    /// <param name="name">Texture name, matched case-insensitively.</param>
    /// <param name="image">The decoded image.</param>
    public void AddTexture(string name, DecodedImage image)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (_textures.Has(name))
        {
            TexturesReused++;
            return;
        }

        _textures.Add(name, image);
    }

    /// <summary>The six sides of the room's sky, once it has been given one.</summary>
    /// <remarks>
    /// Kept rather than uploaded here: building the pipeline needs the shader compiler and
    /// the swapchain's formats, which belong to the renderer. The geometry's job is to know
    /// what the room asked for.
    /// </remarks>
    public IReadOnlyList<DecodedImage>? SkyboxFaces { get; private set; }

    /// <summary>How far the sky is turned, in radians.</summary>
    public float SkyboxAzimuth { get; private set; }

    /// <inheritdoc/>
    public void SetSkybox(IReadOnlyList<DecodedImage> faces, float azimuth)
    {
        ArgumentNullException.ThrowIfNull(faces);

        SkyboxFaces = faces;
        SkyboxAzimuth = azimuth;
    }

    /// <summary>The room's reconstructed horizon, once it has been given one.</summary>
    /// <remarks>
    /// Kept rather than uploaded, for the same reason the sky's faces are: building the
    /// pipeline needs the shader compiler and the swapchain's formats.
    /// </remarks>
    public TerrainBackdrop? Terrain { get; private set; }

    /// <inheritdoc/>
    public void SetTerrain(TerrainBackdrop backdrop)
    {
        ArgumentNullException.ThrowIfNull(backdrop);

        Terrain = backdrop;
    }

    /// <inheritdoc/>
    public void AddNormalMap(string name, DecodedImage image)
    {
        ArgumentNullException.ThrowIfNull(name);

        _textures.AddNormal(name, image);
    }

    /// <inheritdoc/>
    public void AddTexture(string name, CompressedImage image)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (_textures.Has(name))
        {
            TexturesReused++;
            return;
        }

        _textures.Add(name, image);
    }

    /// <inheritdoc/>
    public void AddNormalMap(string name, CompressedImage image)
    {
        ArgumentNullException.ThrowIfNull(name);

        _textures.AddNormal(name, image);
    }

    /// <inheritdoc/>
    public void AddOrmMap(string name, DecodedImage image)
    {
        ArgumentNullException.ThrowIfNull(name);

        _textures.AddOrm(name, image);
    }

    /// <inheritdoc/>
    public void AddOrmMap(string name, CompressedImage image)
    {
        ArgumentNullException.ThrowIfNull(name);

        _textures.AddOrm(name, image);
    }

    /// <inheritdoc/>
    public bool HasOrmMap(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return _textures.HasOrm(name);
    }

    /// <inheritdoc/>
    public void AddHeightMap(string name, DecodedImage image)
    {
        ArgumentNullException.ThrowIfNull(name);

        _textures.AddHeight(name, image, _relief.Contains(name));
    }

    /// <inheritdoc/>
    public void AddHeightMap(string name, CompressedImage image)
    {
        ArgumentNullException.ThrowIfNull(name);

        _textures.AddHeight(name, image, _relief.Contains(name));
    }

    /// <inheritdoc/>
    /// <remarks>
    /// A map that is here as a picture but not as numbers is not here, for a floor that
    /// wants to be displaced: the room before this one uploaded it and had no reason to
    /// keep a readable copy, and the only way to get one is to read the file again.
    /// </remarks>
    public bool HasHeightMap(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return _textures.HasHeight(name) &&
               (!_relief.Contains(name) || _textures.HasField(name));
    }

    /// <inheritdoc/>
    public void KeepRelief(IReadOnlySet<string> textures)
    {
        ArgumentNullException.ThrowIfNull(textures);

        _relief.Clear();
        _reliefEverywhere.Clear();

        foreach (string texture in textures)
        {
            _relief.Add(texture);
        }
    }

    /// <summary>Textures whose relief is cut wherever they appear, floor or not.</summary>
    private readonly HashSet<string> _reliefEverywhere = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public void ReliefEverywhere(IReadOnlySet<string> textures)
    {
        ArgumentNullException.ThrowIfNull(textures);

        foreach (string texture in textures)
        {
            // Into both sets: the everywhere set widens what the plan covers, and the
            // relief set is what makes the height map be kept as numbers at all.
            _relief.Add(texture);
            _reliefEverywhere.Add(texture);
        }
    }

    /// <summary>The leaf cards that move, by the texture they are painted with.</summary>
    private readonly HashSet<string> _wind = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public void MoveInWind(IReadOnlySet<string> textures)
    {
        ArgumentNullException.ThrowIfNull(textures);

        _wind.Clear();

        foreach (string texture in textures)
        {
            _wind.Add(texture);
        }
    }

    /// <summary>
    /// How far the top of a tree travels, as a fraction of its own height.
    /// </summary>
    /// <remarks>
    /// Two per cent, which on a two-hundred-unit maple is four units and on a room's
    /// eighty-unit shrub is one and a half. It is meant to be noticed only when it stops:
    /// a still tree beside a fountain and a walking character is the thing that says
    /// nothing in this room is alive.
    /// </remarks>
    private const float LeafSway = 0.020f;

    /// <summary>How fast the wind runs, in radians a second.</summary>
    /// <remarks>
    /// A gust every five or six seconds once the two waves in the shader have beaten
    /// against each other. Faster than this reads as a gale in what is, in every scene
    /// that has a tree in it, a still summer afternoon.
    /// </remarks>
    private const float WindSpeed = 1.05f;

    /// <summary>The material constants for one batch's texture.</summary>
    /// <remarks>
    /// Scalars, and a map multiplies them rather than replacing them — which is what makes
    /// a corrected roughness in the edit layer still mean something once a generated map
    /// arrives for the same surface. The neutral map is all ones in the two channels that
    /// multiply, so a surface with no map gets its measured finish unchanged.
    /// </remarks>
    private Vector4 MaterialOf(string texture)
    {
        SurfaceFinish finish = Materials.Of(texture);

        // Zero reflectance where there is nothing to shade with, which switches the
        // specular lobe off for that surface. The library's roughness and metalness are a
        // classifier's guess at median confidence 0.32, and GK3's diffuse textures already
        // have their highlights painted in — so a physical lobe over a painted one counts
        // the same light twice and reads as plastic. A generated map is a measurement of
        // the surface, and a hand correction is somebody's judgement of it; either earns
        // the lobe, a guess does not.
        bool measured = _textures.HasOrm(texture);
        float reflectance = measured || finish.Authored ? finish.Specular : 0f;

        // Negative roughness says "this number is the answer, ignore the map's". The sign
        // is free because roughness is clamped to at least 0.03, and it is how a person's
        // correction outranks a generated map for the same surface — which is the one
        // thing the edit layer most needs to be able to do. See SurfaceFinish.
        float roughness = finish.Authored ? -finish.Roughness : finish.Roughness;

        return new Vector4(
            roughness, finish.Metallic, reflectance, finish.NormalStrength);
    }

    /// <inheritdoc/>
    public bool HasNormalMap(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return _textures.HasNormal(name);
    }

    /// <summary>Roughly how much video memory the resident textures take.</summary>
    public long TextureDeviceBytes => _textures.DeviceBytes;

    /// <summary>How many of this room's surfaces have a normal map.</summary>
    public int NormalMapCount => _textures.NormalCount;

    /// <summary>Where the time goes inside the sink, when somebody is measuring.</summary>
    /// <remarks>
    /// The same timeline the loader stamps, so the two interleave into one account of a
    /// load. Building the room is most of a cold arrival and all of a warm one, and it is
    /// one call from the loader's side — without this the breakdown says "AddScene" and
    /// stops exactly where the question starts.
    /// </remarks>
    public LoadTimeline? Timeline { get; set; }

    /// <inheritdoc/>
    public bool HasTexture(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (!_textures.Has(name))
        {
            return false;
        }

        TexturesReused++;
        return true;
    }

    /// <summary>Loads a model.</summary>
    /// <param name="model">The parsed model.</param>
    /// <param name="transform">Where to place it, or null for its authored position.</param>
    /// <param name="meshTurns">Extra rotations for particular meshes, about their own origins.</param>
    public ModelPlacement Add(
        ModFile model,
        Matrix4x4? transform = null,
        IReadOnlyDictionary<int, Matrix4x4>? meshTurns = null)
    {
        ArgumentNullException.ThrowIfNull(model);

        Matrix4x4 placement = transform ?? Matrix4x4.Identity;
        Dictionary<int, List<int>> batches = [];
        Dictionary<(int Mesh, int Submesh), int> parts = [];
        _placements.Add(batches);
        _parts.Add(parts);

        // The colour placeholders first, and before the batch below is opened. A group the
        // artists gave no texture at all is drawn as a one-pixel texture of its own colour,
        // and uploading one is a submission of its own — which on Direct3D means a one-shot
        // command list, which cannot be opened inside the batch. Painted only uploads the
        // first time it sees a colour, so asking twice costs a dictionary lookup.
        foreach (ModMesh ahead in model.Meshes)
        {
            foreach (ModSubmesh submesh in ahead.Submeshes)
            {
                Painted(submesh);
            }
        }

        // As for the room: a character is a dozen meshes and a scene places dozens of
        // models, so the submissions add up even though each model is small.
        using IGeometryUploads uploads = _device.BeginUploads();

        // Asked once for the whole model, so that a character's limbs agree with each
        // other; each group may still overrule it. See ModNormals.
        bool localNormals = ModNormals.AreLocal(model);

        for (int index = 0; index < model.Meshes.Count; index++)
        {
            ModMesh mesh = model.Meshes[index];

            // Before the mesh's own transform, so the turn happens about the mesh's origin
            // - which for a head is where the neck is, because that is where the artist
            // put it - rather than about the model's feet.
            Matrix4x4 meshToLocal =
                meshTurns is not null && meshTurns.TryGetValue(index, out Matrix4x4 turn)
                    ? turn * mesh.MeshToLocal
                    : mesh.MeshToLocal;

            Matrix4x4 meshToWorld = meshToLocal * placement;

            // Characters write their normals in the model's space, not the mesh's, so the
            // mesh transform the vertex shader applies to them is a second copy of one they
            // have already had. It is about a ninety-degree turn, which lays every one of
            // them over on its side. Cancelled here rather than skipped there so that a
            // posed limb still turns its own normals; see ModNormals.
            //
            // Against mesh.MeshToLocal and not the turned transform above, because a turn
            // is a real rotation and its normals should have it.
            Matrix4x4 normalBasis = ModNormals.CorrectionFor(mesh, localNormals);
            bool correcting = !normalBasis.IsIdentity;

            for (int group = 0; group < mesh.Submeshes.Count; group++)
            {
                ModSubmesh submesh = mesh.Submeshes[group];

                if (submesh.Positions.Length == 0 || submesh.Indices.Length == 0)
                {
                    continue;
                }

                MeshVertex[] vertices = new MeshVertex[submesh.Positions.Length];
                // In the model's own space rather than the room's, and placed by the
                // instance transform instead. Baked into world space, an actor's shadow
                // stays wherever they were standing when the room loaded: it does not
                // follow them when they walk, and outdoors, where the room stands them
                // somewhere else on arrival, it is left behind at their authored spot.
                var local = new Vector3[submesh.Positions.Length];

                for (int i = 0; i < vertices.Length; i++)
                {
                    Vector3 normal = i < submesh.Normals.Length
                        ? submesh.Normals[i]
                        : Vector3.UnitY;

                    if (correcting)
                    {
                        Vector3 corrected = Vector3.TransformNormal(normal, normalBasis);

                        if (corrected.LengthSquared() > 1e-12f)
                        {
                            normal = Vector3.Normalize(corrected);
                        }
                    }

                    vertices[i] = new MeshVertex(
                        submesh.Positions[i],
                        normal,
                        i < submesh.TexCoords.Length ? submesh.TexCoords[i] : Vector2.Zero,
                        Vector2.Zero);

                    local[i] = Vector3.Transform(submesh.Positions[i], meshToLocal);
                    Grow(Vector3.Transform(submesh.Positions[i], meshToWorld));
                }

                // What this group is painted with, or what it is *coloured* when the
                // artists gave it no texture at all. See Painted.
                string painted = Painted(submesh);

                RecordTraceable(painted, local, submesh.Indices, _placements.Count);

                if (!batches.TryGetValue(index, out List<int>? owned))
                {
                    owned = [];
                    batches[index] = owned;
                }

                owned.Add(_batches.Count);
                parts[(index, group)] = _batches.Count;

                AddBatch(
                    vertices,
                    _device.CreateBuffer<ushort>(
                        submesh.Indices, GeometryBufferKind.ShortIndices, uploads),
                    shortIndices: true,
                    (uint)submesh.Indices.Length,
                    meshToWorld,
                    painted,
                    useLightmap: false,
                    selfLit: false,
                    local: meshToLocal,
                    isModel: true,
                    into: uploads);
            }
        }

        _placed.Add((model, placement));
        return new ModelPlacement(_placements.Count - 1);
    }

    /// <summary>
    /// The parts of the room that give off light, one entry per object.
    /// </summary>
    /// <returns>
    /// Every object at least one of whose batches is painted with a texture the material
    /// library calls emissive.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>The library, and not the room's own self-lit flag.</b> A BSP surface's bit 8
    /// means the 1999 bake never lit it, which covers two very different things: the lamp
    /// shades, and the <em>painted views</em> — the van parked outside the hotel window,
    /// the grass beyond it, the dining room seen through a doorway. The second kind are
    /// pictures of somewhere else hung on a wall, and a light at the middle of one is a
    /// lamp standing in the middle of the lobby. The library asks a different question —
    /// is this picture bright enough to be giving off light — and only that one is a light.
    /// </para>
    /// <para>
    /// Models as well as room geometry, which is where this differs from anything the
    /// content pipeline could have written down: a room's lamps are as often props as
    /// geometry, and which props are standing depends on the point in the story.
    /// </para>
    /// </remarks>
    public IReadOnlyList<EmissiveSurface> Emitters()
    {
        var found = new List<EmissiveSurface>();

        foreach ((string owner, List<int> batches) in _sceneObjects)
        {
            if (Gather(owner, batches) is { } emitter)
            {
                found.Add(emitter);
            }
        }

        for (int i = 0; i < _placements.Count; i++)
        {
            List<int> batches = [.. _placements[i].Values.SelectMany(b => b)];

            if (batches.Count > 0 &&
                Gather(_placed.Count > i ? _placed[i].Model.Name : string.Empty, batches)
                    is { } emitter)
            {
                found.Add(emitter);
            }
        }

        return found;
    }

    /// <summary>The one emitter an object comes to, or null when none of it glows.</summary>
    /// <remarks>
    /// A lamp shade is six surfaces and a stained-glass window is five; one light apiece is
    /// the answer, and averaging their vertices puts it where the fitting is. The vertices
    /// are the authored ones through the batch's current placement, so a lamp a script has
    /// moved lights where it now stands.
    /// </remarks>
    private EmissiveSurface? Gather(string owner, List<int> batches)
    {
        Vector3 total = Vector3.Zero;
        Vector3 emission = Vector3.Zero;
        string texture = string.Empty;
        int counted = 0;

        foreach (int index in batches)
        {
            if (index < 0 || index >= _batches.Count)
            {
                continue;
            }

            Batch batch = _batches[index];
            Materials.SurfaceFinish finish = Materials.Of(batch.TextureName);

            if (batch.Hidden || !finish.Emits)
            {
                continue;
            }

            emission = Vector3.Max(emission, finish.Emission);

            texture = batch.TextureName;

            Matrix4x4 place = batch.Local * batch.Transform;

            foreach (MeshVertex vertex in batch.Shape)
            {
                total += Vector3.Transform(vertex.Position, place);
                counted++;
            }
        }

        if (counted == 0)
        {
            return null;
        }

        Vector3 centre = total / counted;
        float spread = 0;
        int measured = 0;

        foreach (int index in batches)
        {
            if (index < 0 || index >= _batches.Count)
            {
                continue;
            }

            Batch batch = _batches[index];

            if (batch.Hidden || !Materials.Of(batch.TextureName).Emits)
            {
                continue;
            }

            Matrix4x4 place = batch.Local * batch.Transform;

            foreach (MeshVertex vertex in batch.Shape)
            {
                spread += Vector3.Distance(Vector3.Transform(vertex.Position, place), centre);
                measured++;
            }
        }

        return new EmissiveSurface(
            owner,
            texture,
            centre,
            measured > 0 ? spread / measured : 1f,
            emission);
    }

    /// <inheritdoc/>
    public void SetSelfLit(ModelPlacement placement, bool selfLit)
    {
        if (!placement.Exists || placement.Id >= _placements.Count)
        {
            return;
        }

        foreach (List<int> batches in _placements[placement.Id].Values)
        {
            foreach (int index in batches)
            {
                Batch batch = _batches[index];

                if (batch.SelfLit == selfLit)
                {
                    continue;
                }

                // The material carries which lightmap is bound, and a self-lit surface
                // binds white. A model never uses the bake, so nothing actually changes
                // here today — it is remade anyway, because the rule lives in one place
                // and a model that ever did use it would otherwise keep the wrong one.
                _batches[index] = batch with
                {
                    SelfLit = selfLit,
                    Material = MaterialFor(
                        batch.Painted ?? batch.TextureName,
                        batch.TextureName,
                        batch.UseLightmap && !selfLit),
                };
            }
        }
    }

    /// <inheritdoc/>
    public void Repaint(ModelPlacement placement, string texture, string? painted)
    {
        ArgumentNullException.ThrowIfNull(texture);

        if (!placement.Exists || placement.Id >= _placements.Count)
        {
            return;
        }

        foreach (List<int> batches in _placements[placement.Id].Values)
        {
            foreach (int index in batches)
            {
                Batch batch = _batches[index];

                if (!batch.TextureName.Equals(texture, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(batch.Painted, painted, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                _batches[index] = batch with
                {
                    Painted = painted,
                    Material = painted is { Length: > 0 } picture
                        ? MaterialFor(picture, batch.TextureName)
                        : MaterialFor(batch.TextureName, batch.TextureName),
                };
            }
        }
    }

    /// <summary>A material set drawing one picture with another surface's normal map.</summary>
    /// <remarks>
    /// A model's repaint takes no lightmap, because only the room's own geometry is baked.
    /// The room repaints too — an animation may swap the picture on a wall or a floor — and
    /// that one keeps the bake, or the surface it lands on goes flat and bright in a room
    /// where everything around it is lit. Cached, because a talking face comes back to the
    /// same eight mouth shapes over and over and a flashing floor to the same three.
    /// </remarks>
    private IGeometryMaterial MaterialFor(string picture, string surface, bool lit = false)
    {
        if (_repainted.TryGetValue((picture, surface, lit), out IGeometryMaterial? known))
        {
            return known;
        }

        IGeometryMaterial made = _device.CreateMaterial(
            TextureFor(picture),
            lit ? _lightmap ?? _textures.White : _textures.White,
            _textures.GetNormal(surface),
            _textures.GetOrm(surface),
            _textures.GetHeight(surface));

        _repainted[(picture, surface, lit)] = made;
        return made;
    }

    private readonly HashSet<int> _invisible = [];

    /// <summary>Batches an <c>[MVISIBILITY]</c> line has turned off on their own.</summary>
    /// <remarks>
    /// Held apart from <see cref="_invisible"/> because the two are independent in the
    /// original: a submesh switched off stays off when the model is shown again, and
    /// showing a model does not put back a part an animation took away. So a batch is
    /// drawn only when neither says otherwise.
    /// </remarks>
    private readonly HashSet<int> _invisibleParts = [];

    /// <inheritdoc/>
    public void SetVisible(ModelPlacement placement, bool visible)
    {
        if (!placement.Exists || placement.Id >= _placements.Count)
        {
            return;
        }

        if (visible ? !_invisible.Remove(placement.Id) : !_invisible.Add(placement.Id))
        {
            return;
        }

        foreach (List<int> batches in _placements[placement.Id].Values)
        {
            foreach (int index in batches)
            {
                _batches[index] = _batches[index] with
                {
                    Hidden = !visible || _invisibleParts.Contains(index),
                };
            }
        }

        // And out of the traced world with it. The parts are numbered from one, because
        // part zero is the room itself; see RecordTraceable.
        _rayTracing?.SetTraced(placement.Id + 1, visible);
    }

    /// <inheritdoc/>
    public void SetPartVisible(ModelPlacement placement, int mesh, int submesh, bool visible)
    {
        if (!placement.Exists ||
            placement.Id >= _parts.Count ||
            !_parts[placement.Id].TryGetValue((mesh, submesh), out int index))
        {
            return;
        }

        if (visible ? !_invisibleParts.Remove(index) : !_invisibleParts.Add(index))
        {
            return;
        }

        _batches[index] = _batches[index] with
        {
            Hidden = !visible || _invisible.Contains(placement.Id),
        };

        // Nothing said to the acceleration structure: one instance stands for a whole
        // model — see RecordTraceable — so a part cannot be taken out of the traced world
        // on its own. Every line in the corpus that uses this form hides something small
        // and held — a pair of glasses, a hat, a pipe, a spare hand — whose shadow at this
        // scale is a few pixels, where the cost of splitting every model into an instance
        // per submesh is paid by every room.
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The vertices do not move; the transform they are drawn with does. That keeps a
    /// glance to a handful of matrix multiplies a frame and leaves the acceleration
    /// structure alone — which is also its limit, since a head turned under ray tracing
    /// still casts the shadow of the head it was.
    /// </remarks>
    public void TurnMesh(ModelPlacement placement, int mesh, Matrix4x4 turn)
    {
        if (!placement.Exists || placement.Id >= _placements.Count)
        {
            return;
        }

        if (!_placements[placement.Id].TryGetValue(mesh, out List<int>? batches))
        {
            return;
        }

        (ModFile model, Matrix4x4 where) = _placed[placement.Id];

        if (mesh >= model.Meshes.Count)
        {
            return;
        }

        // On top of the pose the mesh is in now, not the pose the model was authored in.
        // A clip moves every group of a character and the head is one of them; rebuilding
        // the head from its rest transform while the rest of the body follows the clip put
        // it back where the model was placed — which for an absolute move clip, whose
        // correction carries the authored heading, is the wrong place and the wrong way
        // round. That was Emilio walking from the lobby door to the bench with his head
        // twisted a half turn from his shoulders.
        foreach (int index in batches)
        {
            _batches[index] = _batches[index] with
            {
                Transform = turn * _batches[index].Local * where,
            };
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The positions are kept and written into a buffer by <see cref="Flush"/>, not written
    /// here. A vertex buffer the device may still be reading cannot be overwritten from the
    /// CPU, and the only place that knows which frame the device has finished with is the
    /// renderer.
    /// </remarks>
    public void ShapeMesh(
        ModelPlacement placement, int mesh, int submesh, IReadOnlyList<Vector3> positions)
    {
        ArgumentNullException.ThrowIfNull(positions);

        if (!placement.Exists || placement.Id >= _placements.Count)
        {
            return;
        }

        if (!_placements[placement.Id].TryGetValue(mesh, out List<int>? batches) ||
            submesh < 0 || submesh >= batches.Count)
        {
            return;
        }

        _pendingShapes[batches[submesh]] = positions;
    }

    /// <summary>
    /// Writes whatever has been reshaped into the buffers for a frame.
    /// </summary>
    /// <param name="frame">Which of the frames in flight is about to be recorded.</param>
    /// <remarks>
    /// <para>
    /// One vertex buffer per frame in flight, cycled. Writing a single buffer from the CPU
    /// while the device is still reading it for an earlier frame gives a character built
    /// from two different poses at once; waiting for the device instead would give up the
    /// pipelining that makes it worth having frames in flight at all.
    /// </para>
    /// <para>
    /// A batch is only given animated buffers the first time something reshapes it, so a
    /// scene where nothing deforms pays nothing.
    /// </para>
    /// </remarks>
    public void Flush(int frame)
    {
        if (_pendingShapes.Count == 0)
        {
            return;
        }

        foreach ((int index, IReadOnlyList<Vector3> positions) in _pendingShapes)
        {
            Batch batch = _batches[index];

            if (positions.Count != batch.Shape.Length)
            {
                continue;
            }

            IGeometryBuffer[] buffers = batch.Animated ?? Animate(index, ref batch);
            MeshVertex[] shape = batch.Shape;

            for (int i = 0; i < shape.Length; i++)
            {
                shape[i] = shape[i] with { Position = positions[i] };
            }

            int slot = ((frame % buffers.Length) + buffers.Length) % buffers.Length;

            buffers[slot].Write<MeshVertex>(shape);

            // The pose before this one, kept because a motion vector needs where each
            // vertex was, not only where the model it belongs to was: a character standing
            // still while it gestures moves nothing but its own vertices.
            _batches[index] = batch with { Live = buffers[slot], Was = batch.Live ?? buffers[slot] };
            _posed.Add(index);
        }

        _pendingShapes.Clear();
        Retrace();
    }

    /// <summary>Hands the acceleration structure the vertices now being drawn.</summary>
    /// <remarks>
    /// Without this a character's shadow is the shape the model was authored in, wherever
    /// the animation has actually put them: rays leaving a raised arm start inside a body
    /// that is still standing at rest, and report themselves as shadowed. It shows as
    /// smears across whichever parts of them moved.
    /// </remarks>
    private void Retrace()
    {
        if (_posed.Count == 0 || _rayTracing is null)
        {
            return;
        }

        foreach (int index in _posed)
        {
            Batch batch = _batches[index];
            MeshVertex[] shape = batch.Shape;
            var placed = new Vector3[shape.Length];

            for (int i = 0; i < shape.Length; i++)
            {
                placed[i] = Vector3.Transform(shape[i].Position, batch.Local);
            }

            _rayTracing.Reshape(index, placed);
        }

        _posed.Clear();
    }

    /// <summary>Gives a batch the buffers it needs to be animated.</summary>
    private IGeometryBuffer[] Animate(int index, ref Batch batch)
    {
        IGeometryBuffer[] buffers = new IGeometryBuffer[FramesInFlight + 1];

        for (int i = 0; i < buffers.Length; i++)
        {
            buffers[i] = _device.CreateDynamicVertices(
                (ulong)(batch.Shape.Length * Marshal.SizeOf<MeshVertex>()));
        }

        batch = batch with { Animated = buffers };
        _batches[index] = batch;

        return buffers;
    }

    /// <inheritdoc/>
    public void PoseMesh(ModelPlacement placement, int mesh, Matrix4x4 meshToLocal)
    {
        if (!placement.Exists || placement.Id >= _placements.Count)
        {
            return;
        }

        if (!_placements[placement.Id].TryGetValue(mesh, out List<int>? batches))
        {
            return;
        }

        Matrix4x4 meshToWorld = meshToLocal * _placed[placement.Id].Transform;

        foreach (int index in batches)
        {
            _batches[index] = _batches[index] with
            {
                Transform = meshToWorld,
                Local = meshToLocal,
            };

            _posed.Add(index);
        }
    }

    /// <inheritdoc/>
    public Matrix4x4 TransformOf(ModelPlacement placement) =>
        placement.Exists && placement.Id < _placed.Count
            ? _placed[placement.Id].Item2
            : Matrix4x4.Identity;

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// Every batch of every mesh is re-placed against the new transform, <b>each from the
    /// pose it is in now</b> rather than from the pose the model was authored in. A head
    /// that is turned goes on looking where it was looking, and a mesh a clip has moved
    /// stays where the clip put it, which is the same rule <see cref="TurnMesh"/> keeps and
    /// for the same reason.
    /// </para>
    /// <para>
    /// Rebuilding from <c>MeshToLocal</c> instead threw away every pose the frame had just
    /// applied, which is not visible on a model that is only walking — a stride poses every
    /// mesh again on the next frame — and is total for a <b>held prop</b>, whose
    /// pose is applied once and whose placement is then rewritten every frame to follow
    /// whoever is holding it. The Abbé's binoculars are modelled 252 units below his feet
    /// and put in his hands entirely by their clip, so they were being drawn underground:
    /// reported as a man miming binoculars he did not have. Lady Howard's camera and its
    /// lens are the same defect 93 units behind her.
    /// </para>
    /// </remarks>
    public void MoveModel(ModelPlacement placement, Matrix4x4 transform)
    {
        if (!placement.Exists || placement.Id >= _placements.Count)
        {
            return;
        }

        (ModFile model, Matrix4x4 _) = _placed[placement.Id];
        _placed[placement.Id] = (model, transform);

        // And the shadow with it. The structure holds this model's triangles in the
        // model's own space, so where it stands is one transform rather than ten thousand
        // rewritten vertices.
        _rayTracing?.Move(placement.Id + 1, transform);

        foreach ((int mesh, List<int> batches) in _placements[placement.Id])
        {
            if (mesh >= model.Meshes.Count)
            {
                continue;
            }

            foreach (int index in batches)
            {
                _batches[index] = _batches[index] with
                {
                    Transform = _batches[index].Local * transform,
                };
            }
        }
    }

    /// <summary>
    /// Adds a flat, unlit, single-colour mesh drawn over the scene.
    /// </summary>
    /// <param name="name">A name for the colour's texture, unique per colour.</param>
    /// <param name="positions">World-space vertices.</param>
    /// <param name="indices">Triangles over them.</param>
    /// <param name="colour">What to draw it in, each channel from zero to one.</param>
    /// <remarks>
    /// For diagnostic overlays — the walk boundary is the first — so it deliberately does
    /// not participate in anything else: no lightmap, no rig, and nothing in the
    /// acceleration structure, because an overlay that cast shadows would change the
    /// picture it exists to check.
    /// </remarks>
    public void AddOverlay(
        string name, ReadOnlySpan<Vector3> positions, ReadOnlySpan<uint> indices, Vector3 colour)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (positions.Length == 0 || indices.Length == 0)
        {
            return;
        }

        AddTexture(name, Solid(colour));

        MeshVertex[] vertices = new MeshVertex[positions.Length];
        for (int i = 0; i < vertices.Length; i++)
        {
            // The middle of a one-pixel texture, so filtering has nothing to blend with.
            vertices[i] = new MeshVertex(positions[i], Vector3.UnitY, new Vector2(0.5f, 0.5f), Vector2.Zero);
        }

        AddBatch(
            vertices,
            _device.CreateBuffer<uint>(indices, GeometryBufferKind.Indices),
            shortIndices: false,
            (uint)indices.Length,
            Matrix4x4.Identity,
            name,
            useLightmap: false,
            selfLit: true);
    }

    /// <inheritdoc/>
    public bool SetSceneObjectVisible(string objectName, bool visible)
    {
        ArgumentNullException.ThrowIfNull(objectName);

        if (!_sceneObjects.TryGetValue(objectName, out List<int>? belonging))
        {
            return false;
        }

        foreach (int index in belonging)
        {
            _batches[index] = _batches[index] with { Hidden = !visible };
        }

        return true;
    }

    /// <inheritdoc/>
    public IReadOnlyList<(string Name, Vector3 Minimum, Vector3 Maximum)> SceneObjectBoxes()
    {
        var boxes = new List<(string, Vector3, Vector3)>(_sceneObjects.Count);

        foreach ((string owner, List<int> batches) in _sceneObjects)
        {
            var minimum = new Vector3(float.MaxValue);
            var maximum = new Vector3(float.MinValue);
            bool any = false;

            foreach (int index in batches)
            {
                if (index < 0 || index >= _batches.Count)
                {
                    continue;
                }

                Batch batch = _batches[index];
                Matrix4x4 place = batch.Local * batch.Transform;

                foreach (MeshVertex vertex in batch.Shape)
                {
                    Vector3 at = Vector3.Transform(vertex.Position, place);

                    minimum = Vector3.Min(minimum, at);
                    maximum = Vector3.Max(maximum, at);
                    any = true;
                }
            }

            if (any)
            {
                boxes.Add((owner, minimum, maximum));
            }
        }

        return boxes;
    }

    /// <inheritdoc/>
    public bool PaintSceneObject(string objectName, string? texture)
    {
        ArgumentNullException.ThrowIfNull(objectName);

        if (!_sceneObjects.TryGetValue(objectName, out List<int>? belonging))
        {
            return false;
        }

        foreach (int index in belonging)
        {
            Batch batch = _batches[index];

            if (string.Equals(batch.Painted, texture, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            _batches[index] = batch with
            {
                Painted = texture,
                Material = texture is { Length: > 0 } picture
                    ? MaterialFor(picture, batch.TextureName, batch.UseLightmap && !batch.SelfLit)
                    : MaterialFor(batch.TextureName, batch.TextureName, batch.UseLightmap && !batch.SelfLit),
            };
        }

        return true;
    }

    /// <inheritdoc/>
    public bool SwapLightmaps(MulFile lightmaps)
    {
        ArgumentNullException.ThrowIfNull(lightmaps);

        if (_lightmap is null || _lightmapAtlas is null)
        {
            return false;
        }

        DecodedImage repacked = _lightmapAtlas.Repack(lightmaps.Lightmaps);

        _lightmap.Refresh(repacked.Pixels, repacked.Width, repacked.Height);
        return true;
    }

    /// <summary>One triangle's relief, cut before the loop that lays it into a batch.</summary>
    /// <param name="Pieces">Its vertices, in the tessellator's own order.</param>
    /// <param name="Indices">Triangles over those, indexed from zero within this cut.</param>
    private readonly record struct CutTriangle(ReliefVertex[] Pieces, int[] Indices);

    /// <summary>
    /// Cuts the room's relief on every core, a window of polygons at a time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This was 53% of walking through a door.</b> Cutting RC4's ground took 684 ms of
    /// the 1,280 the room took to load, on one core, while fifteen others did nothing and
    /// the screen fade had no frame to present. It parallelises exactly: a triangle's
    /// relief depends on the triangle, the plan and a height field, and the plan is
    /// read-only once <see cref="ReliefPlan.For"/> has built it. The one thing shared is
    /// the "how far did it move" tally, which each cut keeps to itself and merges once.
    /// </para>
    /// <para>
    /// <b>A window at a time, and that is not a detail.</b> Cutting the whole room up front
    /// is simpler and was measurably worse: a room's cut is some tens of megabytes of
    /// vertices, and holding all of it while the loop consumes it cost more in collection
    /// than the parallelism saved — the cut itself fell to 257 ms and every phase *after*
    /// it rose, buffers alone from 285 ms to 841. A window is cut, consumed and dropped
    /// before the next is cut, so the extra live set is a fraction of one room.
    /// </para>
    /// <para>
    /// <b>The order of the result is the order of the room, not the order the work finished
    /// in.</b> Every triangle has a slot decided before any of them runs, so the batches
    /// come out vertex for vertex identical to the serial cut — verified byte for byte on
    /// an outdoor room and an interior. That is not tidiness: two renders of the same scene
    /// are compared byte for byte to tell a shading change from noise.
    /// </para>
    /// </remarks>
    private sealed class ReliefCut
    {
        /// <summary>How much of the room is cut at once.</summary>
        /// <remarks>
        /// Bounded by memory rather than by cores: it decides the live set, and every
        /// window is still spread across every core. A thousand polygons of an outdoor
        /// room is some tens of thousands of cut triangles, which is far more work than a
        /// core needs to be worth waking.
        /// </remarks>
        private const int PolygonsPerWindow = 1024;

        private readonly BspFile _scene;
        private readonly ReliefPlan _plan;
        private readonly bool[] _displaces;
        private readonly float[] _depths;
        private readonly HeightField?[] _fields;
        private readonly int[] _first;
        private readonly Action? _progress;

        private CutTriangle?[] _window = [];
        private int _windowFrom;
        private int _windowTo;

        private ReliefCut(
            BspFile scene,
            ReliefPlan plan,
            bool[] displaces,
            float[] depths,
            HeightField?[] fields,
            int[] first,
            Action? progress)
        {
            _scene = scene;
            _plan = plan;
            _displaces = displaces;
            _depths = depths;
            _fields = fields;
            _first = first;
            _progress = progress;
        }

        /// <summary>Where each polygon's triangles begin, by polygon index.</summary>
        /// <param name="scene">The room.</param>
        /// <returns>One more entry than there are polygons; the last is the total.</returns>
        /// <remarks>
        /// A polygon is fanned from its first vertex, so it is exactly two fewer triangles
        /// than it has corners. Computed once so a window can be written into disjoint
        /// slots without the workers agreeing on anything at run time.
        /// </remarks>
        public static int[] Triangles(BspFile scene)
        {
            int[] first = new int[scene.Polygons.Count + 1];

            for (int i = 0; i < scene.Polygons.Count; i++)
            {
                first[i + 1] = first[i] + Math.Max(0, scene.Polygons[i].VertexIndexCount - 2);
            }

            return first;
        }

        /// <summary>Prepares to cut, or answers null where nothing is displaced.</summary>
        /// <param name="scene">The room.</param>
        /// <param name="plan">The plan, or null when the relief is off.</param>
        /// <param name="first">From <see cref="Triangles"/>.</param>
        /// <param name="geometry">Whose material library and texture set are read.</param>
        /// <returns>The cutter, or null when the loop takes its flat path throughout.</returns>
        /// <remarks>
        /// What each surface displaces at is decided here, once per surface rather than
        /// once per triangle — and on this thread, because the material library and the
        /// texture set are not the tessellator and were never asked to be concurrent.
        /// </remarks>
        public static ReliefCut? For(
            BspFile scene, ReliefPlan? plan, int[] first, SceneGeometry geometry)
        {
            if (plan is null || first[^1] == 0)
            {
                return null;
            }

            int surfaces = scene.Surfaces.Count;
            bool[] displaces = new bool[surfaces];
            float[] depths = new float[surfaces];
            HeightField?[] fields = new HeightField?[surfaces];
            bool any = false;

            for (int i = 0; i < surfaces; i++)
            {
                BspSurface surface = scene.Surfaces[i];

                if (!plan.Covers(surface, geometry.Deep(surface.TextureName)))
                {
                    continue;
                }

                // Ground beyond the floor object gets its depth multiplied: the derived
                // depths average 1.2 units — honest for a floor somebody stands on,
                // invisible on a verge or a roadside seen from a room camera. Capped past
                // even the library's own ceiling, because the boost is the point.
                //
                // <b>Never on the floor object itself.</b> The floor's depth is the number
                // the material library was reviewed at for a surface somebody walks on,
                // and multiplying it is what curled the museum's tiles: MSMFLOOR asked for
                // 1.5 units and was cut at 3.75, so every tile domed and the grout between
                // them wandered. The boost belongs to the ground this feature added — the
                // part that was flat until the horizon was reconstructed — and not to the
                // floor that was already right.
                float depth = geometry.Materials.Of(surface.TextureName).HeightDepth;

                if (surface.ObjectIndex != plan.FloorObject &&
                    geometry._reliefEverywhere.Contains(surface.TextureName))
                {
                    depth = MathF.Min(depth * 2.5f, 12f);
                }

                displaces[i] = true;
                depths[i] = depth;
                fields[i] = geometry._textures.FieldFor(surface.TextureName);
                any = true;
            }

            return any
                ? new ReliefCut(scene, plan, displaces, depths, fields, first, geometry.Progress)
                : null;
        }

        /// <summary>One triangle's cut, or null where it is not displaced.</summary>
        /// <param name="polygon">Which polygon the loop has reached.</param>
        /// <param name="triangle">The triangle's index across the whole room.</param>
        /// <returns>The cut, or null for the flat path.</returns>
        /// <remarks>
        /// The loop walks polygons in order, so reaching one past the window is what asks
        /// for the next — and letting the old one go here is what keeps the live set to a
        /// window. A frame is offered after each, because a worker may not:
        /// <see cref="SceneGeometry.Progress"/> belongs to the loading thread and it draws.
        /// </remarks>
        public CutTriangle? At(int polygon, int triangle)
        {
            if (polygon >= _windowTo)
            {
                Cut(polygon);
                _progress?.Invoke();
            }

            return _window[triangle - _first[_windowFrom]];
        }

        /// <summary>Cuts the window the given polygon begins.</summary>
        private void Cut(int polygon)
        {
            _windowFrom = polygon;
            _windowTo = Math.Min(polygon + PolygonsPerWindow, _scene.Polygons.Count);

            int origin = _first[_windowFrom];
            _window = new CutTriangle?[_first[_windowTo] - origin];

            Parallel.For(
                _windowFrom,
                _windowTo,
                () => (Pieces: new List<ReliefVertex>(), Indices: new List<int>()),
                (index, _, scratch) =>
                {
                    BspPolygon face = _scene.Polygons[index];

                    if (face.SurfaceIndex < 0 ||
                        face.SurfaceIndex >= _displaces.Length ||
                        !_displaces[face.SurfaceIndex])
                    {
                        return scratch;
                    }

                    BspSurface surface = _scene.Surfaces[face.SurfaceIndex];
                    int at = _first[index] - origin;

                    foreach ((ushort a, ushort b, ushort c) in _scene.Triangulate(face))
                    {
                        Vector3 pa = _scene.Vertices[a];
                        Vector3 pb = _scene.Vertices[b];
                        Vector3 pc = _scene.Vertices[c];

                        if (!_plan.Lies(surface, pa, pb, pc))
                        {
                            at++;
                            continue;
                        }

                        _plan.Tessellate(
                            pa, pb, pc,
                            _scene.TexCoordFor(a), _scene.TexCoordFor(b), _scene.TexCoordFor(c),
                            surface.TextureName,
                            _fields[face.SurfaceIndex],
                            _depths[face.SurfaceIndex],
                            scratch.Pieces,
                            scratch.Indices);

                        _window[at++] = new CutTriangle([.. scratch.Pieces], [.. scratch.Indices]);
                    }

                    return scratch;
                },
                _ => { });
        }
    }

    /// <summary>Loads a scene's geometry.</summary>
    /// <param name="scene">The parsed scene.</param>
    /// <param name="lightmaps">The scene's baked lightmaps, in surface order, if any.</param>
    /// <param name="hiddenObjects">Names of objects inside it that must not be drawn.</param>
    /// <param name="hiddenSurfaces">Individual surfaces that must not be drawn, by index.</param>
    /// <param name="floorObject">
    /// The object the scene calls its floor, whose surfaces may have their relief cut into
    /// the geometry rather than only sampled by the shader, or null to displace nothing.
    /// </param>
    /// <remarks>
    /// <para>
    /// BSP files carry no normals, so each triangle gets the normal of its own plane. Flat
    /// shading is wrong for the few curved surfaces a scene contains and right for the
    /// walls, floors and doorways that make up nearly all of them, and it invents no
    /// smoothing groups the data never had.
    /// </para>
    /// <para>
    /// The floor is the exception, and has to be: a displaced surface is subdivided, and
    /// giving every piece of it the plane's normal would make the relief invisible. Those
    /// pieces carry a normal smoothed across the floor instead. See
    /// <see cref="ReliefPlan"/>.
    /// </para>
    /// <para>
    /// And improved geometry is the other exception, because it has normals of its own: an
    /// object somebody bevelled outside the engine arrives with the shading its new edges
    /// need, and putting the plane's normal back on it would throw away the whole reason
    /// for the bevel. See <see cref="Replace"/>.
    /// </para>
    /// </remarks>
    /// <param name="enhanced">
    /// Improved geometry for some of the room's objects, or null to draw every object from
    /// the room itself.
    /// </param>
    public void AddScene(
        BspFile scene,
        MulFile? lightmaps = null,
        IReadOnlySet<string>? hiddenObjects = null,
        string? floorObject = null,
        IReadOnlySet<int>? hiddenSurfaces = null,
        SceneOverlay? enhanced = null)
    {
        ArgumentNullException.ThrowIfNull(scene);

        if (lightmaps is not null)
        {
            LightmapAtlas atlas = LightmapAtlas.Pack(lightmaps.Lightmaps);

            // No mips and clamped addressing: both would sample across tile edges.
            _lightmap?.Dispose();
            _lightmap = _device.CreateTexture(atlas.Image, GeometryTextureKind.Atlas);

            _lightmapRegions = atlas.Regions;

            // Kept, because a script may hand the room a different bake part-way through —
            // a light switch, the bar's disco — and the new one has to land in the layout
            // the vertices were given, which is this one. See SwapLightmaps.
            _lightmapAtlas = atlas;
        }

        Timeline?.Stamp("room: lightmap atlas");

        // Which of the room's surfaces can have their relief cut into the geometry, and how
        // finely this room can afford to cut it. Null when the scene names no floor, when
        // none of the floor's textures has a height map, or when the setting is off — and
        // then everything below takes exactly the path it took before any of this existed.
        ReliefPlan? relief = Relief.Displace
            ? ReliefPlan.For(
                scene, floorObject, Deep, Relief.TriangleBudget,
                _reliefEverywhere.Count > 0
                    ? surface => _reliefEverywhere.Contains(surface.TextureName)
                    : null)
            : null;

        Timeline?.Stamp("room: relief plan");

        // What the loop below is going to need cut, prepared now and then cut on every
        // core a window ahead of where the loop has reached. See ReliefCut: it is the
        // longest single thing in a scene load and it parallelises exactly.
        int[] cutFrom = ReliefCut.Triangles(scene);
        ReliefCut? cut = ReliefCut.For(scene, relief, cutFrom, this);

        // The cut itself is no longer a phase of its own: it happens a window ahead of the
        // loop that consumes it, so its cost lands in the loop's own stamp below.
        Timeline?.Stamp("room: prepare the cut");

        // Where a card's two faces coincide exactly, they are moved a hair apart so the
        // depth test can answer the same way over the whole of them. See CoplanarCards:
        // it is what the original gets for nothing by culling back faces, which is not
        // available here. Zero for nearly every surface in the room.
        Vector3[] apart = CoplanarCards.Apart(scene);
        CardsSeparated = apart.Count(o => o != Vector3.Zero);
        Timeline?.Stamp("room: separate coincident cards");

        ReliefCell = relief?.Cell ?? 0f;
        DisplacedTriangles = 0;
        RoundedObjects = 0;
        RoundedTriangles = 0;
        _rounded.Clear();

        // Keyed by whether the surface lights itself as well as by texture, because one
        // texture serves both states: LAMPSHADE is on a shade that the bake lit and on
        // one that glows on its own.
        //
        // And by whether it was displaced, because that is a different draw of the same
        // texture: geometry that already carries the relief must not also march for it.
        // CONCRETE is on CSE's forecourt and on its walls, and only the forecourt moved.
        Dictionary<(string Texture, bool SelfLit, bool Displaced, string Object, bool Hidden),
                   (List<MeshVertex> Vertices, List<uint> Indices)> groups = [];

        // What a ray can hit, gathered here rather than per batch: the split between
        // surfaces that block light and surfaces that do not cuts across the texture the
        // batches are grouped by.
        List<Vector3> occluders = [];
        List<uint> occluderIndices = [];

        // Which surfaces have already been emitted whole — by an overlay, or by rounding
        // one of the room's own objects off — so the polygon loop below does not emit them
        // a second time triangle by triangle. And which objects have been considered for
        // rounding at all, so one too big to round is decided once rather than re-gathered
        // for every polygon it owns.
        HashSet<int> emitted = [];
        HashSet<int> consideredRound = [];

        EnhancedObjects = 0;
        EnhancedTriangles = 0;
        CardsThickened = 0;
        CardTriangles = 0;
        CardShadowTriangles = 0;
        CardThickness = (0f, 0f);

        // The keyed cards, ahead of everything: a railing is one surface of an object that
        // is otherwise a staircase, and both of the passes below claim whole objects. See
        // ThickenCards.
        if (ThickenCutoutCards)
        {
            ThickenCards(
                scene, hiddenObjects, hiddenSurfaces, relief, apart, emitted, groups);

            Timeline?.Stamp("room: thicken keyed cards");
        }

        // Improved geometry, if any was built for this room and it matched. First, and
        // ahead of the rounding: an object somebody has modelled properly is a better
        // answer than an object the loader curves at load, and the two must not both
        // happen to the same surfaces. See Replace.
        if (enhanced is { IsEmpty: false })
        {
            Replace(
                scene, enhanced, hiddenObjects, hiddenSurfaces, relief, apart,
                emitted, groups, occluders, occluderIndices);

            Timeline?.Stamp("room: improved geometry");
        }

        // How often the caller is offered a frame. Once every few hundred polygons: this
        // loop is where an outdoor room spends most of a cold load — RC1's floor comes out
        // of it as 1.7 million triangles — and offering on every one of forty thousand
        // polygons would cost more in the offer than in the work. See ISceneSink.Progress.
        const int PolygonsBetweenOffers = 256;
        int since = 0;

        for (int polygonIndex = 0; polygonIndex < scene.Polygons.Count; polygonIndex++)
        {
            BspPolygon polygon = scene.Polygons[polygonIndex];

            if (++since >= PolygonsBetweenOffers)
            {
                since = 0;
                Progress?.Invoke();
            }

            if (polygon.SurfaceIndex < 0 || polygon.SurfaceIndex >= scene.Surfaces.Count)
            {
                continue;
            }

            BspSurface surface = scene.Surfaces[polygon.SurfaceIndex];

            // Which of the room's named objects this surface belongs to. It goes in the
            // batch key so that a script can show and hide one of them: the original
            // renders surface by surface and carries a flag on each, and the closest thing
            // to that here is a batch nothing else shares.
            string owner = surface.ObjectIndex >= 0 && surface.ObjectIndex < scene.ObjectNames.Count
                ? scene.ObjectNames[surface.ObjectIndex]
                : string.Empty;

            // A hidden object's geometry is still loaded — it is only not drawn. The scene
            // declares hit-test volumes and things the story brings out later that way, and
            // dropping their triangles is the mistake this file has made twice before:
            // there is no showing something that was never read.
            bool hidden =
                (hiddenObjects is { Count: > 0 } &&
                 owner.Length > 0 &&
                 hiddenObjects.Contains(owner)) ||

                // Or this one surface on its own. An object can be two trees and a painted
                // strip of distant hillside, and hiding it by name takes the hillside with
                // the trees.
                (hiddenSurfaces is { Count: > 0 } &&
                 hiddenSurfaces.Contains(polygon.SurfaceIndex));

            Vector4 region = _lightmapRegions is not null && polygon.SurfaceIndex < _lightmapRegions.Count
                ? _lightmapRegions[polygon.SurfaceIndex]
                : Vector4.Zero;

            bool displace = relief is not null && relief.Covers(surface, Deep(surface.TextureName));

            // A round thing, rounded off. The desk bell, the lamps, the vases: small
            // objects whose whole character is a curve, drawn with the dozen flat faces
            // 1999 could afford. Rounded as one object across every surface it is made of
            // — a rim shared between a bell's side and its top has to move, and it can
            // only move if both are in the same mesh — the first time any of its polygons
            // comes past. The loop then skips what has been emitted.
            if (!displace &&
                !emitted.Contains(polygon.SurfaceIndex) &&
                IsRound(owner) &&
                consideredRound.Add(surface.ObjectIndex))
            {
                RoundOff(
                    scene, surface.ObjectIndex, hidden, emitted,
                    groups, occluders, occluderIndices);
            }

            if (emitted.Contains(polygon.SurfaceIndex))
            {
                continue;
            }

            (string, bool, bool, string, bool) key =
                (surface.TextureName.ToUpperInvariant(), surface.IsSelfLit, displace, owner, hidden);

            if (!groups.TryGetValue(key, out (List<MeshVertex>, List<uint>) group))
            {
                group = ([], []);
                groups[key] = group;
            }

            // Nothing that is not drawn may block a ray either. A hit-test volume is a
            // slab across a doorway with its visibility switched off, and tracing one
            // stands a wall of shadow in the middle of the room.
            bool occludes = !hidden &&
                            surface.CastsShadows &&
                            !_textures.Keyed.Contains(surface.TextureName) &&
                            Materials.Of(surface.TextureName).Occludes;

            int triangle = cutFrom[polygonIndex];

            foreach ((ushort a, ushort b, ushort c) in scene.Triangulate(polygon))
            {
                CutTriangle? made = cut?.At(polygonIndex, triangle);
                triangle++;

                Vector3 shift = apart[polygon.SurfaceIndex];

                Vector3 pa = scene.Vertices[a] + shift;
                Vector3 pb = scene.Vertices[b] + shift;
                Vector3 pc = scene.Vertices[c] + shift;

                Vector3 normal = Vector3.Cross(pb - pa, pc - pa);
                normal = normal.LengthSquared() > 1e-12f ? Vector3.Normalize(normal) : Vector3.UnitY;

                Vector2 ua = scene.TexCoordFor(a);
                Vector2 ub = scene.TexCoordFor(b);
                Vector2 uc = scene.TexCoordFor(c);

                // A cut exists for exactly the triangles the old serial test admitted —
                // ReliefCut applies it and nothing else does — so asking whether one was
                // made *is* the test, and there is no second copy of it to drift.
                if (made is { } piecesMade)
                {
                    ReliefVertex[] pieces = piecesMade.Pieces;
                    int[] pieceIndices = piecesMade.Indices;

                    uint first = (uint)group.Item1.Count;

                    foreach (ReliefVertex piece in pieces)
                    {
                        group.Item1.Add(new MeshVertex(
                            piece.Position + shift,
                            piece.Normal,
                            piece.TexCoord,
                            Lightmap(piece.TexCoord, surface, region)));

                        if (!hidden)
                        {
                            Grow(piece.Position + shift);
                        }
                    }

                    foreach (int index in pieceIndices)
                    {
                        group.Item2.Add(first + (uint)index);
                    }

                    DisplacedTriangles += pieceIndices.Length / 3;

                    if (occludes)
                    {
                        // The cut-up floor, so that a cobble shadows the gutter beside it
                        // — which is the whole reason to have moved the vertices rather
                        // than only the texels. Off, it is the flat triangle that goes in,
                        // and the acceleration structure stays the size it always was.
                        if (Relief.Trace)
                        {
                            int origin = occluders.Count;

                            foreach (ReliefVertex piece in pieces)
                            {
                                occluders.Add(piece.Position + shift);
                            }

                            foreach (int index in pieceIndices)
                            {
                                occluderIndices.Add((uint)(origin + index));
                            }
                        }
                        else
                        {
                            Occlude(occluders, occluderIndices, pa, pb, pc);
                        }
                    }

                    continue;
                }

                (List<MeshVertex>, List<uint>) into = group;

                if (displace)
                {
                    // A wall or a roof of a covered surface stays the flat triangle it
                    // was: cutting them is what blew the village to thirty-six million
                    // triangles and tore every facade at its corners. Emitted into an
                    // undisplaced batch, so its parallax is not marched at the displaced
                    // batch's residual depth.
                    (string, bool, bool, string, bool) flatKey =
                        (surface.TextureName.ToUpperInvariant(), surface.IsSelfLit,
                         false, owner, hidden);

                    if (!groups.TryGetValue(flatKey, out into))
                    {
                        into = ([], []);
                        groups[flatKey] = into;
                    }
                }

                uint at = (uint)into.Item1.Count;
                into.Item1.Add(new MeshVertex(pa, normal, ua, Lightmap(ua, surface, region)));
                into.Item1.Add(new MeshVertex(pb, normal, ub, Lightmap(ub, surface, region)));
                into.Item1.Add(new MeshVertex(pc, normal, uc, Lightmap(uc, surface, region)));
                into.Item2.Add(at);
                into.Item2.Add(at + 1);
                into.Item2.Add(at + 2);

                if (occludes)
                {
                    Occlude(occluders, occluderIndices, pa, pb, pc);
                }

                Grow(pa);
                Grow(pb);
                Grow(pc);
            }
        }

        Timeline?.Stamp("room: polygons, relief and rounding");

        // How far the cut floor actually moved. Reported rather than assumed: every other
        // number a displaced floor produces reads the same whether it moved or not.
        ReliefDepth = relief?.Moved ?? 0f;
        ReliefTypically = relief?.MovedTypically ?? 0f;
        ReliefBoundary = relief?.Boundary ?? (0, 0);
        ReliefExpected = relief?.Triangles ?? 0;
        ReliefSetApart = relief?.SetApart ?? 0;

        if (_device.SupportsRayTracing && occluderIndices.Count > 0)
        {
            _traceable.Add(new TraceableMesh([.. occluders], [.. occluderIndices]));
        }

        // One submission for the room's several hundred buffers rather than one each, and
        // one queue stall rather than several hundred. See BufferUploads: nothing reads
        // any of these until the batch is submitted, which is what makes it sound.
        using IGeometryUploads uploads = _device.BeginUploads();

        foreach (((string texture, bool selfLit, bool displaced, string owner, bool hidden),
                  (List<MeshVertex> vertices, List<uint> indices)) in groups)
        {
            if (indices.Count > 0)
            {
                if (owner.Length > 0)
                {
                    if (!_sceneObjects.TryGetValue(owner, out List<int>? belonging))
                    {
                        belonging = [];
                        _sceneObjects[owner] = belonging;
                    }

                    belonging.Add(_batches.Count);
                }

                // Scene batches routinely pass 65,535 vertices: a single wall texture in
                // the larger scenes covers more geometry than a 16-bit index can address.
                AddBatch(
                    CollectionsMarshal.AsSpan(vertices),
                    _device.CreateBuffer<uint>(
                        CollectionsMarshal.AsSpan(indices), GeometryBufferKind.Indices, uploads),
                    shortIndices: false,
                    (uint)indices.Count,
                    Matrix4x4.Identity,
                    texture,
                    useLightmap: true,
                    selfLit: selfLit,
                    displaced: displaced,
                    hidden: hidden,
                    into: uploads);
            }
        }

        uploads.Submit();
        Timeline?.Stamp("room: vertex and index buffers");

    }

    /// <summary>
    /// Whether this texture's relief is to become geometry: the material says so, it has
    /// depth to give, and its map is here as numbers rather than only as a picture.
    /// </summary>
    /// <param name="texture">The texture's name.</param>
    /// <returns>True where the surface may be cut.</returns>
    /// <remarks>
    /// All three are required — a floor whose map never arrived cannot be displaced, and
    /// one nothing asked to displace must not be. A method rather than a local function
    /// because <see cref="ReliefCut"/> asks it too, and the two have to agree: it decides
    /// which surfaces are cut, and a second copy of it would be a second answer.
    /// </remarks>
    private bool Deep(string texture)
    {
        SurfaceFinish finish = Materials.Of(texture);

        return finish.Displaced && finish.HeightDepth > 0f && _textures.HasField(texture);
    }

    /// <summary>The names of the room's round things, matched by what they contain.</summary>
    /// <remarks>
    /// A curated list rather than a measurement. Curvature could be estimated, and would
    /// then round off things whose faceting is the point — a cut gem, a timber beam — so
    /// the things that are round on purpose are named: bells, lamps, lanterns, candles,
    /// chandeliers, vases and urns.
    /// </remarks>
    private static readonly string[] RoundNames =
        ["bell", "lamp", "lantern", "candle", "chandel", "vase", "urn"];

    /// <summary>How many of the room's objects were drawn from improved geometry.</summary>
    /// <remarks>
    /// Reported rather than folded into the triangle count, because the failure worth
    /// seeing is silent: a room with no overlay built for it and a room whose overlay was
    /// refused draw exactly the same picture, and only a number tells them apart.
    /// </remarks>
    public int EnhancedObjects { get; private set; }

    /// <summary>What those objects came to, once refined.</summary>
    public int EnhancedTriangles { get; private set; }

    /// <summary>
    /// Draws some of the room's objects from geometry somebody improved outside the engine.
    /// </summary>
    /// <param name="scene">The room.</param>
    /// <param name="enhanced">The improved geometry, already matched to this room.</param>
    /// <param name="hiddenObjects">Objects that must not be drawn.</param>
    /// <param name="hiddenSurfaces">Individual surfaces that must not be drawn.</param>
    /// <param name="relief">What the floor is having cut into it, or null.</param>
    /// <param name="apart">How far each surface is moved off one it coincides with.</param>
    /// <param name="emitted">Receives every surface index this handled.</param>
    /// <param name="groups">The batches being built.</param>
    /// <param name="occluders">What a ray can hit.</param>
    /// <param name="occluderIndices">Its indices.</param>
    /// <remarks>
    /// <para>
    /// <b>The overlay supplies positions, normals and texture coordinates. It supplies
    /// nothing else, and it is not asked to.</b> Every triangle names one of the room's own
    /// surfaces, and that surface decides the picture on it, where its lightmap sits, and
    /// whether it lights itself, casts a shadow or is drawn at all — through exactly the
    /// same arithmetic an unmodified surface goes through, three lines below. That is what
    /// makes replacing a chair a change to the chair rather than to the room's lighting.
    /// </para>
    /// <para>
    /// Two things are refused rather than replaced. A hidden surface stays in its batch as
    /// hidden, because there is no showing something that was never read. And a surface
    /// the relief plan is cutting into keeps its own geometry, because the cut and the
    /// replacement are two sets of triangles for one patch of floor and drawing both puts
    /// the floor through itself.
    /// </para>
    /// </remarks>
    private void Replace(
        BspFile scene,
        SceneOverlay enhanced,
        IReadOnlySet<string>? hiddenObjects,
        IReadOnlySet<int>? hiddenSurfaces,
        ReliefPlan? relief,
        Vector3[] apart,
        HashSet<int> emitted,
        Dictionary<(string Texture, bool SelfLit, bool Displaced, string Object, bool Hidden),
                   (List<MeshVertex> Vertices, List<uint> Indices)> groups,
        List<Vector3> occluders,
        List<uint> occluderIndices)
    {
        // What another pass emitted before this one ran, taken now because `emitted` grows
        // as this loop claims objects and an alias to it would refuse everything.
        //
        // Only keyed cards get here first. A railing is one surface of a staircase object,
        // ThickenCards has already given it a thickness, and the overlay's copy of it is
        // the flat quad it always was — emitting both draws one through the other, exactly
        // coincident, which is the depth fighting CoplanarCards exists to stop.
        HashSet<int> claimed = [.. emitted];

        foreach (SceneObjectGeometry piece in enhanced.Objects)
        {
            string owner = piece.ObjectIndex >= 0 && piece.ObjectIndex < scene.ObjectNames.Count
                ? scene.ObjectNames[piece.ObjectIndex]
                : string.Empty;

            if (relief is not null &&
                piece.Surfaces.Any(i =>
                    i >= 0 && i < scene.Surfaces.Count &&
                    relief.Covers(scene.Surfaces[i], Deep(scene.Surfaces[i].TextureName))))
            {
                continue;
            }

            bool hidden = hiddenObjects is { Count: > 0 } &&
                          owner.Length > 0 &&
                          hiddenObjects.Contains(owner);

            foreach (int index in piece.Surfaces)
            {
                emitted.Add(index);
            }

            EnhancedObjects++;

            foreach (SceneTriangle triangle in piece.Triangles)
            {
                if (triangle.Surface < 0 ||
                    triangle.Surface >= scene.Surfaces.Count ||
                    claimed.Contains(triangle.Surface))
                {
                    continue;
                }

                EnhancedTriangles++;

                BspSurface surface = scene.Surfaces[triangle.Surface];

                bool away = hidden ||
                            (hiddenSurfaces is { Count: > 0 } &&
                             hiddenSurfaces.Contains(triangle.Surface));

                Vector4 region = _lightmapRegions is not null &&
                                 triangle.Surface < _lightmapRegions.Count
                    ? _lightmapRegions[triangle.Surface]
                    : Vector4.Zero;

                (string, bool, bool, string, bool) key =
                    (surface.TextureName.ToUpperInvariant(), surface.IsSelfLit, false, owner, away);

                if (!groups.TryGetValue(key, out (List<MeshVertex> Vertices, List<uint> Indices) group))
                {
                    group = ([], []);
                    groups[key] = group;
                }

                // The same hair of separation an unreplaced surface gets, and for the same
                // reason. A tablecloth and the table under it are one card each at exactly
                // the same depth, and the depth test cannot choose between them: the two
                // interleave as speckles across the cloth. Improving the geometry does not
                // make them any less coincident, so the shift has to travel with it.
                Vector3 shift = triangle.Surface < apart.Length
                    ? apart[triangle.Surface]
                    : Vector3.Zero;

                foreach (SceneVertex corner in
                         (ReadOnlySpan<SceneVertex>)[triangle.A, triangle.B, triangle.C])
                {
                    group.Indices.Add((uint)group.Vertices.Count);
                    group.Vertices.Add(new MeshVertex(
                        corner.Position + shift,
                        corner.Normal,
                        corner.TexCoord,
                        Lightmap(corner.TexCoord, surface, region)));

                    if (!away)
                    {
                        Grow(corner.Position + shift);
                    }
                }

                bool occludes = !away &&
                                surface.CastsShadows &&
                                !_textures.Keyed.Contains(surface.TextureName) &&
                                Materials.Of(surface.TextureName).Occludes;

                if (occludes)
                {
                    // The improved triangles, so a shadow has the silhouette the object
                    // now has rather than the one it used to have.
                    Occlude(
                        occluders,
                        occluderIndices,
                        triangle.A.Position + shift,
                        triangle.B.Position + shift,
                        triangle.C.Position + shift);
                }
            }
        }
    }

    /// <summary>
    /// Gives the room's keyed cards the thickness of whatever is drawn on them.
    /// </summary>
    /// <param name="scene">The room.</param>
    /// <param name="hiddenObjects">Objects that must not be drawn.</param>
    /// <param name="hiddenSurfaces">Individual surfaces that must not be drawn.</param>
    /// <param name="relief">What the floor is having cut into it, or null.</param>
    /// <param name="apart">How far each surface is moved off one it coincides with.</param>
    /// <param name="emitted">Receives every surface index this handled.</param>
    /// <param name="groups">The batches being built.</param>
    /// <remarks>
    /// <para>
    /// <b>First, ahead of the improved geometry and the rounding.</b> A railing is a single
    /// surface inside an object that is otherwise a staircase or a wall, and the other two
    /// passes work on whole objects: whichever of them reached the object first would claim
    /// the rail along with it and draw the flat card it has always drawn. Claiming the card
    /// here and letting <see cref="Replace"/> skip what is claimed is the only ordering in
    /// which a rail on an improved staircase gets both treatments.
    /// </para>
    /// <para>
    /// The improved copy of such a card is the same flat quad in any case — a surface on one
    /// plane has no edge to bevel and no curve to recover, so the Blender pass leaves it
    /// exactly as it found it — which is why taking the card from the room's own polygons
    /// here loses nothing.
    /// </para>
    /// <para>
    /// No occluders. Every card this touches is keyed, and keyed geometry is kept out of the
    /// acceleration structure whatever its shape: without an any-hit shader the holes in it
    /// would cast a solid shadow. So a thickened rail casts exactly the shadow a flat one
    /// did, which is none.
    /// </para>
    /// </remarks>
    private void ThickenCards(
        BspFile scene,
        IReadOnlySet<string>? hiddenObjects,
        IReadOnlySet<int>? hiddenSurfaces,
        ReliefPlan? relief,
        Vector3[] apart,
        HashSet<int> emitted,
        Dictionary<(string Texture, bool SelfLit, bool Displaced, string Object, bool Hidden),
                   (List<MeshVertex> Vertices, List<uint> Indices)> groups)
    {
        // Gathered per surface rather than per polygon: a card is two triangles most of the
        // time and twenty on a stair rail, and the shell has to see all of them at once to
        // fit one map from its texture to the room.
        Dictionary<int, (List<Vector3> Positions, List<Vector2> TexCoords, List<int> Indices)> cards
            = [];

        // Kept apart from the room's occluders, because they end up in a different instance
        // of the acceleration structure carrying a different mask.
        List<Vector3> cardOccluders = [];
        List<uint> cardIndices = [];

        foreach (BspPolygon polygon in scene.Polygons)
        {
            if (polygon.SurfaceIndex < 0 || polygon.SurfaceIndex >= scene.Surfaces.Count)
            {
                continue;
            }

            BspSurface surface = scene.Surfaces[polygon.SurfaceIndex];

            // A floor being cut into keeps its own geometry: the cut and the shell are two
            // sets of triangles for one patch of floor, and drawing both puts it through
            // itself. The same refusal Replace makes, for the same reason.
            if (relief is not null && relief.Covers(surface, Deep(surface.TextureName)))
            {
                continue;
            }

            if (_textures.Cutout(surface.TextureName) is null)
            {
                continue;
            }

            if (!cards.TryGetValue(polygon.SurfaceIndex, out var card))
            {
                card = ([], [], []);
                cards[polygon.SurfaceIndex] = card;
            }

            foreach ((ushort a, ushort b, ushort c) in scene.Triangulate(polygon))
            {
                foreach (ushort corner in (ReadOnlySpan<ushort>)[a, b, c])
                {
                    card.Indices.Add(card.Positions.Count);
                    card.Positions.Add(scene.Vertices[corner]);
                    card.TexCoords.Add(scene.TexCoordFor(corner));
                }
            }
        }

        if (cards.Count == 0)
        {
            return;
        }

        float thinnest = float.MaxValue;
        float thickest = 0f;

        // In surface order, so that a room builds the same way twice. A dictionary does not
        // promise one and the vertex buffer it fills is compared byte for byte by the tests.
        foreach (int index in cards.Keys.Order())
        {
            (List<Vector3> positions, List<Vector2> texCoords, List<int> indices) = cards[index];

            BspSurface surface = scene.Surfaces[index];
            CutoutMask mask = _textures.Cutout(surface.TextureName)!;

            if (CutoutCards.Thicken(positions, texCoords, indices, mask) is not { } shell)
            {
                continue;
            }

            string owner = surface.ObjectIndex >= 0 && surface.ObjectIndex < scene.ObjectNames.Count
                ? scene.ObjectNames[surface.ObjectIndex]
                : string.Empty;

            bool hidden =
                (hiddenObjects is { Count: > 0 } &&
                 owner.Length > 0 &&
                 hiddenObjects.Contains(owner)) ||
                (hiddenSurfaces is { Count: > 0 } && hiddenSurfaces.Contains(index));

            Vector4 region = _lightmapRegions is not null && index < _lightmapRegions.Count
                ? _lightmapRegions[index]
                : Vector4.Zero;

            (string, bool, bool, string, bool) key =
                (surface.TextureName.ToUpperInvariant(), surface.IsSelfLit, false, owner, hidden);

            if (!groups.TryGetValue(key, out (List<MeshVertex> Vertices, List<uint> Indices) group))
            {
                group = ([], []);
                groups[key] = group;
            }

            Vector3 shift = index < apart.Length ? apart[index] : Vector3.Zero;

            foreach (CardTriangle triangle in shell.Triangles)
            {
                foreach (CurvedCorner corner in
                         (ReadOnlySpan<CurvedCorner>)[triangle.A, triangle.B, triangle.C])
                {
                    group.Indices.Add((uint)group.Vertices.Count);
                    group.Vertices.Add(new MeshVertex(
                        corner.Position + shift,
                        corner.Normal,
                        corner.TexCoord,
                        Lightmap(corner.TexCoord, surface, region)));

                    if (!hidden)
                    {
                        Grow(corner.Position + shift);
                    }
                }
            }

            // And what the sun is stopped by, which is a different set of triangles from
            // what is drawn — see ThickCard.Occluders. The same three refusals the room's
            // own surfaces make, less the one about keyed textures: answering that is
            // exactly what these triangles are for.
            if (CardShadows &&
                !hidden &&
                surface.CastsShadows &&
                Materials.Of(surface.TextureName).Occludes)
            {
                for (int corner = 0; corner + 2 < shell.Occluders.Count; corner += 3)
                {
                    Occlude(
                        cardOccluders,
                        cardIndices,
                        shell.Occluders[corner] + shift,
                        shell.Occluders[corner + 1] + shift,
                        shell.Occluders[corner + 2] + shift);
                }

                CardShadowTriangles += shell.Occluders.Count / 3;
            }

            emitted.Add(index);
            CardsThickened++;
            CardTriangles += shell.Triangles.Count;
            thinnest = Math.Min(thinnest, shell.Thickness);
            thickest = Math.Max(thickest, shell.Thickness);
        }

        CardThickness = CardsThickened > 0 ? (thinnest, thickest) : (0f, 0f);

        // Its own part, and therefore its own instance and its own mask. Not the room's:
        // the composite credits the room's occlusion against the 1999 bake and cancels it
        // out exactly, because a shadow the bake already holds must not be laid down twice.
        // These the bake does not hold — the artists baked a keyed card as a keyed card, or
        // as nothing — so they belong with the light the bake could not account for, which
        // is the half a character's shadow is spent from. See TracedWorld.CardPart.
        if (_device.SupportsRayTracing && cardIndices.Count > 0)
        {
            _traceable.Add(new TraceableMesh([.. cardOccluders], [.. cardIndices])
            {
                Part = TracedWorld.CardPart,
            });
        }
    }

    /// <summary>Whether an object is one whose silhouette should be a curve.</summary>
    private static bool IsRound(string owner) =>
        owner.Length > 0 &&
        RoundNames.Any(round => owner.Contains(round, StringComparison.OrdinalIgnoreCase));

    /// <summary>The most triangles an object may hold and still be worth rounding.</summary>
    /// <remarks>
    /// Two levels of subdivision are sixteen times the triangles, so five hundred is a cap
    /// of about eight thousand for one object — a chandelier's worth, not a building's. A
    /// "lamp" that is really a street of lampposts stays as authored.
    /// </remarks>
    private const int RoundBudget = 500;

    /// <summary>How many times a rounded object's edges are halved.</summary>
    /// <remarks>
    /// Two, which is sixteen pieces per authored triangle. One is visibly still a polygon on
    /// the eight-sided objects and three buys nothing a bell is large enough on screen to
    /// show. Zero leaves the authored shape and keeps only the crease-aware shading, which
    /// is how the two are told apart in a screenshot.
    /// </remarks>
    public int RoundLevels { get; set; } = 2;

    /// <summary>
    /// Rounds one object off across every surface it is made of, and emits it whole.
    /// </summary>
    /// <param name="scene">The room.</param>
    /// <param name="objectIndex">Which of its objects.</param>
    /// <param name="hidden">Whether the object starts hidden.</param>
    /// <param name="roundedOff">Receives every surface index this handled.</param>
    /// <param name="groups">The batches being built.</param>
    /// <param name="occluders">What a ray can hit.</param>
    /// <param name="occluderIndices">Its indices.</param>
    /// <remarks>
    /// <para>
    /// See <see cref="ObjectRounding"/> for why the object is welded whole: the rim between
    /// a bell's side and its top belongs to two surfaces, and refining each alone pins it,
    /// which is how the first attempt at this left every bell exactly as hexagonal as it
    /// found it.
    /// </para>
    /// <para>
    /// Each refined triangle is emitted into its own surface's batch, with its lightmap
    /// coordinate computed from its texture coordinate through that surface's mapping — the
    /// same arithmetic every unrounded surface uses.
    /// </para>
    /// </remarks>
    private void RoundOff(
        BspFile scene,
        int objectIndex,
        bool hidden,
        HashSet<int> roundedOff,
        Dictionary<(string Texture, bool SelfLit, bool Displaced, string Object, bool Hidden),
                   (List<MeshVertex> Vertices, List<uint> Indices)> groups,
        List<Vector3> occluders,
        List<uint> occluderIndices)
    {
        string owner = objectIndex >= 0 && objectIndex < scene.ObjectNames.Count
            ? scene.ObjectNames[objectIndex]
            : string.Empty;

        // What another pass emitted before this one ran. A lantern is a rounded object and
        // RC1LANTERNSCROLL is a keyed card on it, so the object's own surfaces are not all
        // still this pass's to draw: the card has a thickness now, and rounding it again
        // here would draw the flat one through the middle of it. Snapshotted because the
        // set grows below. See ThickenCards.
        HashSet<int> claimed = [.. roundedOff];

        // Every triangle of every surface the object owns, with its surface remembered.
        List<(Vector3, Vector3, Vector3, Vector2, Vector2, Vector2, int)> raw = [];
        List<int> surfaces = [];

        foreach (BspPolygon polygon in scene.Polygons)
        {
            if (polygon.SurfaceIndex < 0 ||
                polygon.SurfaceIndex >= scene.Surfaces.Count ||
                scene.Surfaces[polygon.SurfaceIndex].ObjectIndex != objectIndex ||
                claimed.Contains(polygon.SurfaceIndex))
            {
                continue;
            }

            if (!surfaces.Contains(polygon.SurfaceIndex))
            {
                surfaces.Add(polygon.SurfaceIndex);
            }

            foreach ((ushort a, ushort b, ushort c) in scene.Triangulate(polygon))
            {
                raw.Add((
                    scene.Vertices[a], scene.Vertices[b], scene.Vertices[c],
                    scene.TexCoordFor(a), scene.TexCoordFor(b), scene.TexCoordFor(c),
                    polygon.SurfaceIndex));
            }
        }

        // Marked handled either way, so a refusal is decided once per object rather than
        // once per polygon of it.
        foreach (int index in surfaces)
        {
            roundedOff.Add(index);
        }

        if (raw.Count < 4 || raw.Count > RoundBudget)
        {
            if (raw.Count > 0)
            {
                foreach (int index in surfaces)
                {
                    roundedOff.Remove(index);
                }
            }

            return;
        }

        // Welded across its surfaces, then curved. Every authored vertex stays where it is
        // and the surface between them bows out to the curve the normals describe; see
        // <see cref="ObjectRounding"/> for why an interpolating scheme and not Loop's, which
        // was tried and left a lamp shade sagging between its ribs.
        List<Vector3> positions = [];
        List<RoundedTriangle> welded = ObjectRounding.Weld(raw, positions);

        List<CurvedTriangle> curved = ObjectRounding.Curve(welded, positions, RoundLevels);

        RoundedObjects++;
        RoundedTriangles += curved.Count;
        _rounded.Add(owner);

        foreach (CurvedTriangle piece in curved)
        {
            BspSurface surface = scene.Surfaces[piece.Surface];

            Vector4 region = _lightmapRegions is not null && piece.Surface < _lightmapRegions.Count
                ? _lightmapRegions[piece.Surface]
                : Vector4.Zero;

            (string, bool, bool, string, bool) key =
                (surface.TextureName.ToUpperInvariant(), surface.IsSelfLit, false, owner, hidden);

            if (!groups.TryGetValue(key, out (List<MeshVertex> Vertices, List<uint> Indices) group))
            {
                group = ([], []);
                groups[key] = group;
            }

            foreach (CurvedCorner corner in (ReadOnlySpan<CurvedCorner>)[piece.A, piece.B, piece.C])
            {
                group.Indices.Add((uint)group.Vertices.Count);
                group.Vertices.Add(new MeshVertex(
                    corner.Position,
                    corner.Normal,
                    corner.TexCoord,
                    Lightmap(corner.TexCoord, surface, region)));

                if (!hidden)
                {
                    Grow(corner.Position);
                }
            }

            bool occludes = !hidden &&
                            surface.CastsShadows &&
                            !_textures.Keyed.Contains(surface.TextureName) &&
                            Materials.Of(surface.TextureName).Occludes;

            if (occludes)
            {
                // The rounded triangles, so the shadow has the silhouette the object has.
                Occlude(
                    occluders,
                    occluderIndices,
                    piece.A.Position,
                    piece.B.Position,
                    piece.C.Position);
            }
        }
    }

    /// <summary>Records one triangle as something a ray can hit.</summary>
    private static void Occlude(
        List<Vector3> occluders, List<uint> indices, Vector3 a, Vector3 b, Vector3 c)
    {
        indices.Add((uint)occluders.Count);
        indices.Add((uint)occluders.Count + 1);
        indices.Add((uint)occluders.Count + 2);

        occluders.Add(a);
        occluders.Add(b);
        occluders.Add(c);
    }

    /// <summary>Records a batch's triangles for ray tracing, if it is opaque.</summary>
    private void RecordTraceable(
        string texture, Vector3[] positions, ReadOnlySpan<ushort> indices, int part = 0)
    {
        var widened = new uint[indices.Length];

        for (int i = 0; i < indices.Length; i++)
        {
            widened[i] = indices[i];
        }

        RecordTraceable(texture, positions, widened, part);
    }

    /// <summary>Records a batch's triangles for ray tracing, if it is opaque.</summary>
    private void RecordTraceable(
        string texture, Vector3[] positions, ReadOnlySpan<uint> indices, int part = 0)
    {
        // Nor anything that is its own light source. A room's own surfaces say so
        // through the flags the BSP carries; a placed model — which is what most of the
        // lamps in this game are — says so only here.
        if (!_device.SupportsRayTracing ||
            _textures.Keyed.Contains(texture) ||
            !Materials.Of(texture).Occludes)
        {
            return;
        }

        // Keyed by the batch this is about to become, so that reshaping the batch can
        // reshape the geometry rays see. Recorded before the batch is added, which is
        // what makes the count the index it will have.
        _traceable.Add(new TraceableMesh(positions, indices.ToArray())
        {
            Part = part,
            Key = _batches.Count,
        });
    }

    /// <summary>Remembers where everything was drawn, ready for the next frame.</summary>
    /// <remarks>
    /// Called after a frame is recorded, not before: what a motion vector needs is where a
    /// thing was when it was last <em>drawn</em>, and something that moved twice between
    /// two frames was only ever drawn at the second place.
    /// </remarks>
    public void Advance()
    {
        for (int i = 0; i < _batches.Count; i++)
        {
            if (_batches[i].Previous != _batches[i].Transform)
            {
                _batches[i] = _batches[i] with { Previous = _batches[i].Transform };
            }
        }
    }

    /// <summary>Makes the traced world agree with the drawn one, once a frame.</summary>
    /// <remarks>
    /// Anything that moved this frame has only been recorded; this is what rebuilds the
    /// structure that shadows are cast against. Called before the frame traces anything.
    /// </remarks>
    public void Settle() => _rayTracing?.Settle();

    /// <summary>How many separately movable things the traced world holds.</summary>
    public int TraceablePartCount => _rayTracing?.PartCount ?? 0;

    /// <summary>Builds the descriptor sets and acceleration structure the batches need.</summary>
    /// <remarks>
    /// Called once, after loading and before the first draw. Everything it builds is
    /// immutable afterwards, so nothing has to be rebuilt or synchronised per frame.
    /// </remarks>
    public void Finish()
    {
        if (_finished || _batches.Count == 0)
        {
            return;
        }

        _finished = true;
        _rayTracing ??= _device.BuildAccelerationStructure(_traceable);

        // Where each model stands. A model's triangles go into the structure in the
        // model's own space and are placed by an instance transform — which is what makes
        // walking across a room a transform rewrite rather than ten thousand rewritten
        // vertices — and RayTracingScene.Build has no transform to place them by, so
        // everything it builds starts at the origin.
        //
        // Nothing else was putting them right. MoveModel is the only other caller of
        // Move, and nothing moves a prop after a room has loaded: a van, a bench, a
        // signpost stayed piled at (0, 0, 0) for the life of the scene, shadowing whatever
        // is there and nothing where it is drawn. An actor came right only once the story
        // first walked them somewhere. Measured on RC1: not one ground pixel in the square
        // was shadowed by the forty-one models standing in it.
        for (int placement = 0; placement < _placed.Count; placement++)
        {
            _rayTracing?.Move(placement + 1, _placed[placement].Item2);
        }

        // Whatever was hidden while the room was being built. The structure did not exist
        // to be told at the time, and a hidden model that still casts a shadow is worse
        // than one that is simply drawn.
        foreach (int hidden in _invisible)
        {
            _rayTracing?.SetTraced(hidden + 1, false);
        }

        // Both of those only recorded. The room is about to be drawn and the first frame
        // traces against whatever the structure holds, so it has to hold this now rather
        // than after a frame has gone by with every model in the wrong place.
        _rayTracing?.Settle();

        // Room for exactly the materials this room loaded. What a device does with that is
        // its own business — Vulkan opens a descriptor pool, Direct3D has a heap already —
        // and either way it is better than growing one batch at a time.
        _device.Reserve(_batches.Count);

        for (int i = 0; i < _batches.Count; i++)
        {
            Batch batch = _batches[i];

            _batches[i] = batch with
            {
                Material = _device.CreateMaterial(
                    TextureFor(batch.TextureName),
                    batch.UseLightmap && !batch.SelfLit
                        ? _lightmap ?? _textures.White
                        : _textures.White,
                    _textures.GetNormal(batch.TextureName),
                    _textures.GetOrm(batch.TextureName),
                    _textures.GetHeight(batch.TextureName)),
            };
        }
    }

    /// <summary>The mirror this frame is about, once <see cref="ChooseMirror"/> has run.</summary>
    public MirrorSurface? Mirror { get; private set; }

    /// <summary>
    /// Decides which piece of glass in the room the frame's reflection is rendered for.
    /// </summary>
    /// <param name="eye">Where the camera is.</param>
    /// <param name="floors">
    /// Whether a polished floor may be reflected where the room has no mirror in it. See
    /// the remarks below for why the two cannot both be had in one frame.
    /// </param>
    /// <returns>The plane, or null if the room has nothing worth reflecting about.</returns>
    /// <remarks>
    /// <para>
    /// Called before <see cref="Draws"/> and remembered, because the two have to agree: the
    /// batch that carries the mirror flag is the batch the reflection was rendered for, and
    /// any other mirror in the room keeps the picture painted on it. A second mirror would
    /// be a second pass over the whole room for a reflection nobody is looking at.
    /// </para>
    /// <para>
    /// <b>The material marks a slab and the geometry picks the glass out of it.</b>
    /// <c>MIRRORL.MOD</c> is a box whose front, back, sides, top and bottom all carry
    /// <c>MIRRORLEFT1</c>, so every one of those is a candidate and only one of them is a
    /// mirror. What separates them is flatness and the way the vertices' own normals point;
    /// see <see cref="MirrorSurfaces"/>.
    /// </para>
    /// <para>
    /// <b>A polished floor is reflected the same way, and only where the room has no
    /// mirror.</b> What a floor shows is mostly what is above the camera — the ceiling, the
    /// beams, the lamps hanging off them — and none of that is ever in the frame the
    /// screen-space march has to work from, which is why a tiled hall reflected nothing
    /// however smooth its material said it was. Rendering the room again from under the
    /// floor has the ceiling because it drew it.
    /// </para>
    /// <para>
    /// One plane a frame, so a room with both keeps its mirror: a mirror that stops
    /// reflecting shows a painted fake of a room that is not there, and a floor that stops
    /// reflecting shows a floor. No room in the game has a mirror over a floor polished
    /// enough for this in any case.
    /// </para>
    /// <para>
    /// <b>Which surfaces are the floor is the room's own answer, not a guess.</b> The
    /// scene file names its floor object, and <see cref="KeepRelief"/> is already told the
    /// textures on it — that is the set displacement is cut into. A floor is therefore a
    /// batch drawn with one of those textures, smooth enough for a reflection to be worth
    /// having, and mostly at one height.
    /// </para>
    /// </remarks>
    public MirrorSurface? ChooseMirror(Vector3 eye, bool floors = false)
    {
        List<MirrorSurface>? found = null;
        List<int>? owners = null;

        for (int index = 0; index < _batches.Count; index++)
        {
            Batch batch = _batches[index];

            if (batch.Material is null || batch.Hidden || !Materials.Of(batch.Drawn).Mirror)
            {
                continue;
            }

            if (MirrorSurfaces.Fit(batch.Shape, batch.Transform) is not { } glass)
            {
                continue;
            }

            (found ??= []).Add(glass);
            (owners ??= []).Add(index);
        }

        bool ground = false;

        if (found is null && floors && Floor() is { } level)
        {
            ground = true;
            found = [level];
            owners = [_floorBatch];
        }

        if (found is null)
        {
            Mirror = null;
            _mirrorBatch = -1;
            _planeBatch = -1;

            return null;
        }

        MirrorSurface? chosen = MirrorSurfaces.Facing(found, eye, Mirror);
        int owner = chosen is { } picked ? owners![found.IndexOf(picked)] : -1;

        // Said once, when it changes. Which piece of a mirror's slab the geometry decided
        // was the glass is the one thing about this that can be wrong while everything
        // reports success: a reflection rendered about the back of the box, or about one of
        // its edges, is a mirror full of the inside of a wall and nothing anywhere says so.
        if (owner != _planeBatch)
        {
            string what = ground ? "Floor" : "Mirror";

            Log.Info(chosen is { } glass
                ? string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"{what}: {_batches[owner].Drawn}, {found.Count} candidate surface(s), " +
                    $"reflecting about ({glass.Plane.X:0.##}, {glass.Plane.Y:0.##}, " +
                    $"{glass.Plane.Z:0.##}) at ({glass.Center.X:0.#}, {glass.Center.Y:0.#}, " +
                    $"{glass.Center.Z:0.#}), {glass.Radius:0.#} units across")
                : $"{what}: none of {found.Count} candidate surface(s) faces the camera");
        }

        Mirror = chosen;
        _planeBatch = owner;

        // <b>A floor is not drawn as a mirror, and that is the whole difference between the
        // two.</b> The batch this flag is set on has its own texture thrown away and the
        // reflection put in its place, which is right for glass and catastrophic for a
        // floor: the church's tiles vanished and the room appeared upside down where they
        // had been. A floor keeps its own surface and has the reflection added over it by
        // the compositing pass, weighed by the angle it is seen at — which is what a
        // polished floor does and what a mirror does not.
        _mirrorBatch = ground ? -1 : owner;

        return chosen;
    }

    /// <summary>
    /// The plane this room's floor is reflected about, worked out once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Cached, and the first version was not, and that cost two thirds of the frame.</b>
    /// Fitting the plane walks every vertex of every polished piece of the floor three
    /// times, and a room's floor object names more than the floor: the hotel lobby's is
    /// eight textures, four of which are its panelling and its beams, so "every batch drawn
    /// with one of the floor's textures" came to most of the room. Done every frame that is
    /// three million transforms on one thread — the lobby went from 142 frames a second to
    /// 35, and the <em>drawing</em> of the reflection was not what cost it: cutting the pass
    /// out and leaving the fitting in still gave 38.
    /// </para>
    /// <para>
    /// A floor does not move, so once is right. Recomputed when the number of batches
    /// changes, which is the one thing that happens to a room after it has loaded — the
    /// hidden models are replayed onto it, and the story shows and hides things.
    /// </para>
    /// </remarks>
    private MirrorSurface? _floor;

    /// <summary>How many batches the room had when that was last worked out.</summary>
    private int _floorAt = -1;

    /// <summary>Which batch to name when the floor's plane is reported.</summary>
    private int _floorBatch = -1;

    /// <summary>Whether this room has already said why it has no reflective floor.</summary>
    private bool _floorReported;

    /// <summary>
    /// The plane this room's floor is reflected about, fitting it if that has not been done.
    /// </summary>
    /// <returns>The plane, or null when the room has no floor worth a pass.</returns>
    /// <remarks>
    /// The room has one floor and gets one plane. Fitted a piece at a time, the church chose
    /// its tiled runner — the largest single piece — and the grey tiles either side sat a
    /// little lower and were not on it, so the reflection appeared on a strip up the middle
    /// of the nave and nowhere else.
    /// </remarks>
    private MirrorSurface? Floor()
    {
        if (_floorAt == _batches.Count)
        {
            return _floor;
        }

        _floorAt = _batches.Count;

        List<(MeshVertex[] Shape, Matrix4x4 Transform)> pieces = [];
        int widest = -1;
        int spread = 0;

        for (int index = 0; index < _batches.Count; index++)
        {
            Batch batch = _batches[index];

            if (batch.Material is null || batch.Hidden ||
                !_relief.Contains(batch.Drawn) ||
                !Materials.Of(batch.Drawn).Reflects)
            {
                continue;
            }

            pieces.Add((batch.Shape, batch.Transform));

            // Which piece to name in the log. The largest one, because a line that named
            // whichever batch happened to come first would say a different thing about the
            // same floor depending on the order the room loaded in.
            if (batch.Shape.Length > spread)
            {
                spread = batch.Shape.Length;
                widest = index;
            }
        }

        _floor = pieces.Count > 0 ? MirrorSurfaces.Ground(pieces) : null;
        _floorBatch = _floor is null ? -1 : widest;

        // Said once a room, because a floor that is not reflecting looks exactly like a
        // floor whose material is too rough to be asked, which looks exactly like a room
        // whose floor object names surfaces that are not the floor. Three different things,
        // one appearance, and the count tells them apart.
        if (_floor is null && !_floorReported)
        {
            Log.Info(pieces.Count == 0
                ? "Floor: nothing on this room's floor is polished enough to reflect"
                : $"Floor: {pieces.Count} polished piece(s), and no one level holds enough " +
                  "of them to be worth reflecting about");

            _floorReported = true;
        }

        return _floor;
    }

    /// <summary>Which batch is drawn as glass, or -1 when none is.</summary>
    private int _mirrorBatch = -1;

    /// <summary>
    /// Which batch the frame's reflection plane came from, or -1.
    /// </summary>
    /// <remarks>
    /// Apart from <see cref="_mirrorBatch"/> because a floor supplies a plane without being
    /// drawn as glass. Kept only so that the line above is said once when it changes rather
    /// than once a frame.
    /// </remarks>
    private int _planeBatch = -1;

    /// <summary>Works out what every loaded batch needs drawn, and with what.</summary>
    /// <param name="previousSeconds">
    /// The wind's clock as it stood a frame ago, so that a leaf reports its own movement to
    /// the temporal filter rather than reporting none.
    /// </param>
    /// <remarks>
    /// <para>
    /// The caller binds the pipeline, the viewport and the frame's descriptor set first;
    /// this only issues what varies per batch.
    /// </para>
    /// <para>
    /// The pipeline is passed in rather than taken from the geometry, because the raster
    /// and ray-traced variants have different set 0 layouts and therefore incompatible
    /// pipeline layouts. Binding a descriptor set or pushing constants through the wrong
    /// one is not an error Vulkan reports: the vertex shader simply reads a garbage
    /// transform and the geometry lands outside the frustum, which looks exactly like
    /// drawing nothing at all.
    /// </para>
    /// </remarks>
    public IEnumerable<SceneDraw> Draws(float previousSeconds = 0f)
    {
        for (int index = 0; index < _batches.Count; index++)
        {
            Batch batch = _batches[index];

            if (batch.Material is null || batch.Hidden)
            {
                continue;
            }

            // The one piece of glass this frame's reflection was rendered for, and not
            // every surface whose texture happens to be a mirror's. A mirror's own slab
            // carries the same texture on its back and its edges, and a room may hold more
            // than one mirror; both draw the picture painted on them, as they always have.
            bool isMirror = index == _mirrorBatch;

            var constants = new DrawConstants(
                batch.Transform,
                batch.Previous,
                new Vector4(
                    _lightmap is not null && batch.UseLightmap ? 1f : 0f,
                    LightmapMultiplier,

                    // Three flags in one number: 1 for self-lit, 2 for a model standing in
                    // the room, 4 for a mirror. The second is what lets a shadow ray
                    // leaving a character skip characters — see RayTracingScene.MaskFor;
                    // the third takes a surface away from the screen-space reflection pass,
                    // which cannot answer a mirror facing the player because what such a
                    // mirror shows is behind the camera and therefore not in the frame.
                    //
                    // <b>They are bits and must be read as bits.</b> The shader tested the
                    // second with `>= 1.5`, which a 4 also passes: adding a flag above an
                    // existing one silently turns every mirror in the game into a
                    // character as far as the shadow rays are concerned.
                    (batch.SelfLit ? 1f : 0f) +
                    (batch.IsModel ? 2f : 0f) +
                    (isMirror ? 4f : 0f),

                    // How deep this surface's height map goes, and zero where it has none —
                    // which is what keeps the level map bound in its place from shifting
                    // every texture in the game by a constant offset.
                    //
                    // Reduced where the geometry already carries the relief. The march and
                    // the vertices read the same field, so a batch that was displaced at
                    // full depth and marched at full depth has its cobbles twice: once
                    // where they are and once painted over them. What is left is the part
                    // of the field finer than a cell, which is most of the field and none
                    // of its amplitude.
                    _textures.HasHeight(batch.TextureName)
                        ? Materials.Of(batch.TextureName).HeightDepth *
                          (batch.Displaced ? ResidualRelief : 1f)
                        : 0f),

                // The finish the material library measured for this texture, which is what
                // the shader uses where no ORM map overrides it. A texture nobody has
                // measured comes back matte and non-metallic, which is the surface the
                // renderer assumed before any of this existed.
                MaterialOf(batch.TextureName),

                // Nothing at all for everything that is not a leaf, which switches the
                // whole of the sway off in the vertex shader on its first line.
                //
                // The fourth slot is a lodger and says so: it is how much of a mirror's
                // texture is drawn frame rather than glass. GK3's mirrors carry their
                // ornate silver frames in the texture itself, so a reflection covering the
                // whole card paints the frame out. It rides here because nothing else in
                // the block has a free slot and the two sets — surfaces that sway and
                // surfaces that reflect — have nothing in common; a leaf's own w stays
                // zero, and a mirror does not sway.
                batch.Foliage
                    ? new Vector4(LeafSway, WindSpeed, previousSeconds, 0f)
                    : new Vector4(
                        0f, 0f, 0f,
                        isMirror ? Materials.Of(batch.Drawn).MirrorInset : 0f),

                // The skin under the coat. The shells over it are below, and a surface with
                // no coat is told so with a zero depth in y rather than by being left out,
                // because the shader darkens the skin under fur and has to be able to tell
                // "the innermost of twelve shells" from "not an animal".
                FurOf(batch.TextureName, 0f));

            // And the coat over it, if it has one: the same triangles again, each shell
            // pushed a little further out along the vertices' own normals and keeping only
            // the texels a hair still reaches at that height.
            //
            // Everything else about the draw is already bound — the material, both vertex
            // streams, the indices — so a shell is one push and one draw. That is what
            // makes twelve of them affordable on a model and would not make them
            // affordable on a room.
            //
            // Nothing is added to the acceleration structure by this. The shells are drawn,
            // not built, so a shadow ray still sees the animal and not its fur, which is
            // the right answer at this scale: a coat one unit deep casts no shadow anybody
            // could see, and the alternative is twelve more structures.
            SurfaceFinish coat = Materials.Of(batch.TextureName);
            DrawConstants[] shells = [];

            if (coat.Furred)
            {
                shells = new DrawConstants[coat.Shells];

                for (int shell = 1; shell <= coat.Shells; shell++)
                {
                    shells[shell - 1] = constants with
                    {
                        // No parallax on a shell. The march reads a height field belonging
                        // to the skin, and running it on a surface standing a centimetre
                        // off that skin shifts the strands sideways against the coat they
                        // are part of.
                        Shading = constants.Shading with { W = 0f },
                        Wind = Vector4.Zero,
                        Fur = FurOf(batch.TextureName, shell / (float)coat.Shells),
                    };
                }
            }

            // Two streams: this pose and the one before it. A batch nothing has animated
            // reports the same buffer twice, which is the truth about it — its vertices are
            // where they have always been, and only its transform can have moved.
            yield return new SceneDraw(
                batch.Live ?? batch.Vertices,
                batch.Was ?? batch.Live ?? batch.Vertices,
                batch.Indices,
                batch.IndexCount,
                batch.ShortIndices,
                batch.Material,
                constants,
                shells);
        }
    }

    /// <summary>What the shader needs to know about one shell of a surface's coat.</summary>
    /// <param name="texture">The batch's texture, which is what a coat is filed under.</param>
    /// <param name="height">Where this shell stands, from zero at the skin to one at the tips.</param>
    /// <returns>The fur constant, all zero for a surface that has no coat.</returns>
    private Vector4 FurOf(string texture, float height)
    {
        SurfaceFinish finish = Materials.Of(texture);

        return finish.Furred
            ? new Vector4(height, finish.ShellDepth, finish.ShellDensity, 0f)
            : Vector4.Zero;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _device.Wait();

        foreach (Batch batch in _batches)
        {
            batch.Vertices.Dispose();
            batch.Indices.Dispose();
        }

        _batches.Clear();

        _repainted.Clear();

        // The textures are the renderer's and outlast this room; see TextureCache. The
        // materials do not: a material is a descriptor that names five of those textures,
        // nothing refers to one once this room's batches are gone, and a session that walks
        // from room to room without releasing them fills a Direct3D heap - 4096 materials,
        // which is a dozen rooms - and then throws part-way through loading the thirteenth.
        // Safe here and nowhere earlier: the wait above is what makes overwriting a
        // descriptor slot something the device is no longer reading.
        _device.ReleaseMaterials();

        _lightmap?.Dispose();
        _lightmap = null;
        _lightmapAtlas = null;
        _rayTracing?.Dispose();
        _rayTracing = null;
        _traceable.Clear();
    }

    /// <summary>Maps a surface's diffuse UV into the lightmap atlas.</summary>
    private static Vector2 Lightmap(Vector2 uv, BspSurface surface, Vector4 region)
    {
        // The original derives the lightmap coordinate as (uv + offset) * scale; see
        // GEngine's Uber.glsl. Clamping keeps a surface whose UVs stray outside the unit
        // square from reading a neighbouring tile once everything shares one atlas.
        Vector2 tile = (uv + surface.LightmapUvOffset) * surface.LightmapUvScale;

        return new Vector2(
            region.X + (Math.Clamp(tile.X, 0f, 1f) * region.Z),
            region.Y + (Math.Clamp(tile.Y, 0f, 1f) * region.W));
    }

    /// <summary>A one-pixel image of a single grey level.</summary>
    private static DecodedImage Solid(byte level) =>
        new(1, 1, [level, level, level, 255], HasAlpha: false, "solid");

    /// <summary>A one-pixel image of a single colour.</summary>
    private static DecodedImage Solid(Vector3 colour) =>
        new(
            1,
            1,
            [Channel(colour.X), Channel(colour.Y), Channel(colour.Z), 255],
            HasAlpha: false,
            "solid");

    private static byte Channel(float value) => (byte)Math.Clamp(value * 255f, 0f, 255f);

    /// <summary>A visibly wrong texture, so a missing one is obvious rather than silent.</summary>
    /// <summary>
    /// Drawn wherever a model asks for a texture the corpus does not contain.
    /// </summary>
    /// <remarks>
    /// A wrong-looking texture is better than a silently black one: the first is a bug you
    /// can see, and the second is a room that merely looks badly lit.
    /// </remarks>
    /// <summary>
    /// What a group of triangles is painted with, which is not always a texture.
    /// </summary>
    /// <param name="submesh">The group.</param>
    /// <returns>A texture name, which may be one made up for a colour.</returns>
    /// <remarks>
    /// <para>
    /// A <c>.MOD</c> group carries a texture name <em>and</em> a colour, and a handful of
    /// the game's models use the second instead of the first: <c>BINO1</c> and
    /// <c>ABEBINOCS</c> — the tour's binoculars — name no texture anywhere in the file and
    /// are a dark teal body and near-black rubber, stored as the two groups' colours.
    /// </para>
    /// <para>
    /// Without this they took the missing-texture fallback, which is a <b>magenta
    /// chequerboard</b>, and the binoculars turned up as a loud purple object. That
    /// fallback is a good thing and it stays: a texture that is <em>named</em> and not
    /// found is a real fault and should be impossible to miss. A group that names none was
    /// never asking for one.
    /// </para>
    /// <para>
    /// One texel, under a name made from the colour, so that every group of the same colour
    /// shares one texture and the batch key keeps working exactly as it did.
    /// </para>
    /// </remarks>
    private string Painted(ModSubmesh submesh)
    {
        if (submesh.TextureName.Length > 0)
        {
            return submesh.TextureName;
        }

        (byte red, byte green, byte blue) = submesh.Color;
        string name = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"#colour{red:X2}{green:X2}{blue:X2}");

        if (!HasTexture(name))
        {
            AddTexture(
                name,
                new DecodedImage(1, 1, [red, green, blue, 255], HasAlpha: false, name));
        }

        return name;
    }

    internal static DecodedImage CheckerBoard()
    {
        const int Size = 64;
        byte[] pixels = new byte[Size * Size * 4];

        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                bool light = ((x / 8) + (y / 8)) % 2 == 0;
                int at = ((y * Size) + x) * 4;

                pixels[at] = light ? (byte)220 : (byte)40;
                pixels[at + 1] = 40;
                pixels[at + 2] = light ? (byte)220 : (byte)40;
                pixels[at + 3] = 255;
            }
        }

        return new DecodedImage(Size, Size, pixels, HasAlpha: false, "fallback");
    }

    private void Grow(Vector3 point)
    {
        _minimum = Vector3.Min(_minimum, point);
        _maximum = Vector3.Max(_maximum, point);
    }

    private void AddBatch(
        ReadOnlySpan<MeshVertex> vertices,
        IGeometryBuffer indices,
        bool shortIndices,
        uint indexCount,
        Matrix4x4 transform,
        string texture,
        bool useLightmap,
        bool selfLit = false,
        Matrix4x4? local = null,
        bool isModel = false,
        bool displaced = false,
        bool hidden = false,
        IGeometryUploads? into = null) =>
        _batches.Add(new Batch
        {
            Hidden = hidden,
            // Identity for the room's own geometry, which is already where it belongs.
            Local = local ?? Matrix4x4.Identity,
            Vertices = _device.CreateBuffer(vertices, GeometryBufferKind.Vertices, into),
            Shape = [.. vertices],
            Indices = indices,
            IndexCount = indexCount,
            ShortIndices = shortIndices,
            Transform = transform,

            // Where it was is where it is, on the frame it first appears. A zero matrix
            // here reports the whole screen as having moved half its width.
            Previous = transform,
            TextureName = texture,
            UseLightmap = useLightmap,
            SelfLit = selfLit,
            IsModel = isModel,
            Displaced = displaced,

            // By the texture rather than by the model, because that is what the loader
            // knows: a grown tree is two batches, one of bark and one of leaves, and only
            // the leaves move. See MoveInWind.
            Foliage = _wind.Contains(Path.GetFileNameWithoutExtension(texture)),
        });

    private IGeometryTexture TextureFor(string name) => _textures.Get(name);

    /// <summary>One drawable piece: a mesh with one diffuse texture.</summary>
    private readonly record struct Batch
    {
        public required IGeometryBuffer Vertices { get; init; }

        /// <summary>The vertices as the model authored them, reused as scratch when animated.</summary>
        public required MeshVertex[] Shape { get; init; }

        /// <summary>One buffer per frame in flight, once anything has animated this batch.</summary>
        public IGeometryBuffer[]? Animated { get; init; }

        /// <summary>Whichever animated buffer was written most recently.</summary>
        public IGeometryBuffer? Live { get; init; }

        /// <summary>The pose before that one.</summary>
        public IGeometryBuffer? Was { get; init; }

        /// <summary>This mesh's place within its model.</summary>
        /// <remarks>
        /// Rays see one structure per model, placed by one transform, so each mesh's own
        /// transform has to be folded into the vertices handed to it — which means
        /// knowing what that transform currently is.
        /// </remarks>
        public Matrix4x4 Local { get; init; }

        public required IGeometryBuffer Indices { get; init; }

        public required uint IndexCount { get; init; }

        /// <summary>Whether the indices are sixteen bits each rather than thirty-two.</summary>
        /// <remarks>
        /// A model's submeshes are small enough for sixteen; a scene batch routinely is not,
        /// because a single wall texture in the larger rooms covers more geometry than a
        /// sixteen-bit index can address.
        /// </remarks>
        public required bool ShortIndices { get; init; }

        public required Matrix4x4 Transform { get; init; }

        /// <summary>Where this batch was drawn last frame.</summary>
        /// <remarks>
        /// Half of a motion vector. Advanced at the end of a frame rather than when the
        /// batch moves, because several things may move it between one drawing and the
        /// next and what a filter needs is where it actually last appeared.
        /// </remarks>
        public Matrix4x4 Previous { get; init; }

        public required string TextureName { get; init; }

        public required bool UseLightmap { get; init; }

        /// <summary>Whether this batch's relief was cut into its geometry.</summary>
        public bool Displaced { get; init; }

        /// <summary>The surface carries its own brightness and the bake does not touch it.</summary>
        public bool SelfLit { get; init; }

        /// <summary>Whether this batch is foliage, and so moves in the wind.</summary>
        public bool Foliage { get; init; }

        /// <summary>A model standing in the room, rather than the room itself.</summary>
        /// <remarks>
        /// Carried through to the shader so a shadow ray leaving this pixel knows to skip
        /// the models: GK3's people are a stack of overlapping shells and a ray leaving a
        /// shirt hits the arm inside it. See <c>RayTracingScene.MaskFor</c>.
        /// </remarks>
        public bool IsModel { get; init; }

        /// <summary>What is drawn on it instead of its own texture, if anything is.</summary>
        /// <remarks>
        /// A character's face while they talk or blink. The original texture's name is kept
        /// beside it because that is what the normal map is filed under, and because
        /// putting the face back is asking for the model's own picture again.
        /// </remarks>
        public string? Painted { get; init; }

        /// <summary>The picture actually on this batch right now.</summary>
        /// <remarks>
        /// <b>Which is not always <see cref="TextureName"/>.</b> It matters wherever what
        /// is asked of the material is a fact about the picture rather than about the
        /// surface it is filed under — and mirrors are the case where the difference is the
        /// whole feature. TE4's two mirrors are <c>MIRRORLEFT1</c> and <c>MIRRORRIGHT1</c>
        /// at rest and are mirrors; the moment the story steps Gabriel up to one, an
        /// <c>[MTEXTURES]</c> line repaints it with <c>MIRRORGABEBAD</c> — a jaundiced,
        /// hollow-eyed Gabriel that is not his reflection at all and is the puzzle. Asking
        /// the original name whether this is a mirror answers yes right through that, and
        /// the reflection paints the story out.
        /// </remarks>
        public string Drawn =>
            Painted is { Length: > 0 } picture ? picture : TextureName;

        /// <summary>Whether it is kept out of the picture.</summary>
        /// <remarks>
        /// A model a scene declares <c>hidden</c>, or one a script has hidden. It is loaded
        /// and placed either way, because <c>ShowModel</c> is how the story brings it out
        /// and there is no way to do that with something that was never read. Written the
        /// negative way round because a batch is a struct and the common case has to be
        /// the default one.
        /// </remarks>
        public bool Hidden { get; init; }

        public IGeometryMaterial? Material { get; init; }
    }
}
