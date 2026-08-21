using System.Numerics;
using System.Runtime.InteropServices;
using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Formats.Lightmaps;
using GK3Reborn.Formats.Models;
using GK3Reborn.Formats.Scenes;
using GK3Reborn.Rendering.Materials;
using Silk.NET.Vulkan;

namespace GK3Reborn.Rendering.Vulkan;

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

    private readonly VulkanContext _context;
    private readonly MeshPipeline _pipeline;
    private readonly List<Batch> _batches = [];

    /// <summary>Which batches belong to which mesh of which placed model.</summary>
    /// <remarks>
    /// A mesh becomes one batch per submesh, so moving a head means moving all of them
    /// together. Kept beside the batches rather than inside them because only the handful
    /// of models that can move ever need it.
    /// </remarks>
    private readonly List<Dictionary<int, List<int>>> _placements = [];

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
    private readonly VulkanTexture _whiteTexture;


    private readonly List<RayTracingMesh> _traceable = [];

    private RayTracingScene? _rayTracing;
    private VulkanTexture? _lightmap;
    private IReadOnlyList<Vector4>? _lightmapRegions;
    private DescriptorPool _descriptorPool;

    /// <summary>
    /// Pools opened after the room was built, for material sets nothing knew it needed.
    /// </summary>
    /// <remarks>
    /// The pool <see cref="Finish"/> creates is sized for exactly the batches the room
    /// loaded, which is right for everything the loader knows about and wrong the moment a
    /// face starts moving: repainting a texture is a new combination of images and
    /// therefore a new set. Each of these holds a block of them, and another is opened when
    /// one fills, which keeps the common case — a room where nothing repaints — costing
    /// nothing at all.
    /// </remarks>
    private readonly List<DescriptorPool> _extraPools = [];

    /// <summary>How many sets each pool opened after loading holds.</summary>
    private const int ExtraPoolSets = 64;

    /// <summary>Material sets for repainted surfaces, by what they draw.</summary>
    /// <remarks>
    /// Kept because a face comes back to the same mouth shape a dozen times a sentence, and
    /// a set that is only a handful of image views is far cheaper to keep than to build.
    /// </remarks>
    private readonly Dictionary<(string Painted, string Of), DescriptorSet> _repainted =
        new();
    private Vector3 _minimum = new(float.MaxValue);
    private Vector3 _maximum = new(float.MinValue);

    private SceneGeometry(VulkanContext context, MeshPipeline pipeline, TextureCache textures)
    {
        _context = context;
        _pipeline = pipeline;

        // The renderer's, not this room's. A room that threw its textures away on the way
        // out spent most of the next room's load getting them back.
        _textures = textures;

        // Bound wherever a batch has no lightmap. Vulkan requires every declared binding to
        // point at something valid even when the shader ignores what it reads.
        _whiteTexture = VulkanTexture.Create(context, Solid(255));
    }

    /// <summary>Total triangles loaded.</summary>
    public int TriangleCount => _batches.Sum(b => (int)b.IndexCount / 3);

    /// <summary>How many draws a frame costs.</summary>
    public int BatchCount => _batches.Count;

    /// <summary>Distinct textures uploaded.</summary>
    public int TextureCount => _textures.Count;

    /// <summary>How many of this room's textures the device already had.</summary>
    public int TexturesReused { get; private set; }

    /// <summary>The scene as rays see it, once <see cref="Finish"/> has built it.</summary>
    public RayTracingScene? RayTracing => _rayTracing;

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

    /// <summary>Lower corner of everything loaded, in world space.</summary>
    public Vector3 Minimum => _batches.Count > 0 ? _minimum : Vector3.Zero;

    /// <summary>Upper corner of everything loaded, in world space.</summary>
    public Vector3 Maximum => _batches.Count > 0 ? _maximum : Vector3.One;

    /// <summary>Creates an empty scene.</summary>
    /// <param name="context">Device context.</param>
    /// <param name="pipeline">Pipeline its descriptor sets must match.</param>
    /// <param name="textures">The device's textures, which outlast any one room.</param>
    /// <returns>The scene.</returns>
    public static SceneGeometry Create(
        VulkanContext context, MeshPipeline pipeline, TextureCache textures)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(textures);

        return new SceneGeometry(context, pipeline, textures);
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

        _textures.AddHeight(name, image);
    }

    /// <inheritdoc/>
    public void AddHeightMap(string name, CompressedImage image)
    {
        ArgumentNullException.ThrowIfNull(name);

        _textures.AddHeight(name, image);
    }

    /// <inheritdoc/>
    public bool HasHeightMap(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return _textures.HasHeight(name);
    }

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
        _placements.Add(batches);

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

            foreach (ModSubmesh submesh in mesh.Submeshes)
            {
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
                    vertices[i] = new MeshVertex(
                        submesh.Positions[i],
                        i < submesh.Normals.Length ? submesh.Normals[i] : Vector3.UnitY,
                        i < submesh.TexCoords.Length ? submesh.TexCoords[i] : Vector2.Zero,
                        Vector2.Zero);

                    local[i] = Vector3.Transform(submesh.Positions[i], meshToLocal);
                    Grow(Vector3.Transform(submesh.Positions[i], meshToWorld));
                }

                RecordTraceable(
                    submesh.TextureName, local, submesh.Indices, _placements.Count);

                if (!batches.TryGetValue(index, out List<int>? owned))
                {
                    owned = [];
                    batches[index] = owned;
                }

                owned.Add(_batches.Count);

                AddBatch(
                    vertices,
                    VulkanBuffer.CreateDeviceLocal<ushort>(
                        _context, submesh.Indices, BufferUsageFlags.IndexBufferBit),
                    IndexType.Uint16,
                    (uint)submesh.Indices.Length,
                    meshToWorld,
                    submesh.TextureName,
                    useLightmap: false,
                    selfLit: false,
                    local: meshToLocal,
                    isModel: true);
            }
        }

        _placed.Add((model, placement));
        return new ModelPlacement(_placements.Count - 1);
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
    /// Never a lightmap: only the room's own geometry is baked, and the only things that
    /// repaint are models. Cached, because a talking face comes back to the same eight
    /// mouth shapes over and over.
    /// </remarks>
    private DescriptorSet MaterialFor(string picture, string surface)
    {
        if (_repainted.TryGetValue((picture, surface), out DescriptorSet known))
        {
            return known;
        }

        DescriptorSet made = CreateMaterialSet(
            TextureFor(picture),
            _whiteTexture,
            _textures.GetNormal(surface),
            _textures.GetOrm(surface),
            _textures.GetHeight(surface));

        _repainted[(picture, surface)] = made;
        return made;
    }

    private readonly HashSet<int> _invisible = [];

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
                _batches[index] = _batches[index] with { Hidden = !visible };
            }
        }

        // And out of the traced world with it. The parts are numbered from one, because
        // part zero is the room itself; see RecordTraceable.
        _rayTracing?.SetTraced(placement.Id + 1, visible);
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

        Matrix4x4 meshToWorld = turn * model.Meshes[mesh].MeshToLocal * where;

        foreach (int index in batches)
        {
            _batches[index] = _batches[index] with { Transform = meshToWorld };
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

            VulkanBuffer[] buffers = batch.Animated ?? Animate(index, ref batch);
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
    private VulkanBuffer[] Animate(int index, ref Batch batch)
    {
        VulkanBuffer[] buffers = new VulkanBuffer[FramesInFlight + 1];

        for (int i = 0; i < buffers.Length; i++)
        {
            buffers[i] = VulkanBuffer.CreateHostVisible(
                _context,
                (ulong)(batch.Shape.Length * Marshal.SizeOf<MeshVertex>()),
                BufferUsageFlags.VertexBufferBit);
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
    /// <remarks>
    /// Every batch of every mesh is re-placed against the new transform. A mesh that has
    /// been turned keeps its turn, because the turn is folded in when it is applied and the
    /// model's own <c>MeshToLocal</c> is what is being rebuilt from — so a head that was
    /// looking at something goes on looking at it while its owner crosses the room.
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

            Matrix4x4 meshToWorld = model.Meshes[mesh].MeshToLocal * transform;

            foreach (int index in batches)
            {
                _batches[index] = _batches[index] with
                {
                    Transform = meshToWorld,
                    Local = model.Meshes[mesh].MeshToLocal,
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
            VulkanBuffer.CreateDeviceLocal<uint>(_context, indices, BufferUsageFlags.IndexBufferBit),
            IndexType.Uint32,
            (uint)indices.Length,
            Matrix4x4.Identity,
            name,
            useLightmap: false,
            selfLit: true);
    }

    /// <summary>Loads a scene's geometry.</summary>
    /// <param name="scene">The parsed scene.</param>
    /// <param name="lightmaps">The scene's baked lightmaps, in surface order, if any.</param>
    /// <param name="hiddenObjects">Names of objects inside it that must not be drawn.</param>
    /// <remarks>
    /// BSP files carry no normals, so each triangle gets the normal of its own plane. Flat
    /// shading is wrong for the few curved surfaces a scene contains and right for the
    /// walls, floors and doorways that make up nearly all of them, and it invents no
    /// smoothing groups the data never had.
    /// </remarks>
    public void AddScene(
        BspFile scene, MulFile? lightmaps = null, IReadOnlySet<string>? hiddenObjects = null)
    {
        ArgumentNullException.ThrowIfNull(scene);

        if (lightmaps is not null)
        {
            LightmapAtlas atlas = LightmapAtlas.Pack(lightmaps.Lightmaps);

            // No mips and clamped addressing: both would sample across tile edges.
            _lightmap?.Dispose();
            _lightmap = VulkanTexture.Create(
                _context, atlas.Image, mipmaps: false, SamplerAddressMode.ClampToEdge);

            _lightmapRegions = atlas.Regions;
        }

        // Keyed by whether the surface lights itself as well as by texture, because one
        // texture serves both states: LAMPSHADE is on a shade that the bake lit and on
        // one that glows on its own.
        Dictionary<(string Texture, bool SelfLit), (List<MeshVertex> Vertices, List<uint> Indices)>
            groups = [];

        // What a ray can hit, gathered here rather than per batch: the split between
        // surfaces that block light and surfaces that do not cuts across the texture the
        // batches are grouped by.
        List<Vector3> occluders = [];
        List<uint> occluderIndices = [];

        foreach (BspPolygon polygon in scene.Polygons)
        {
            if (polygon.SurfaceIndex < 0 || polygon.SurfaceIndex >= scene.Surfaces.Count)
            {
                continue;
            }

            BspSurface surface = scene.Surfaces[polygon.SurfaceIndex];

            if (hiddenObjects is { Count: > 0 } &&
                surface.ObjectIndex >= 0 &&
                surface.ObjectIndex < scene.ObjectNames.Count &&
                hiddenObjects.Contains(scene.ObjectNames[surface.ObjectIndex]))
            {
                continue;
            }

            Vector4 region = _lightmapRegions is not null && polygon.SurfaceIndex < _lightmapRegions.Count
                ? _lightmapRegions[polygon.SurfaceIndex]
                : Vector4.Zero;

            (string, bool) key = (surface.TextureName.ToUpperInvariant(), surface.IsSelfLit);

            if (!groups.TryGetValue(key, out (List<MeshVertex>, List<uint>) group))
            {
                group = ([], []);
                groups[key] = group;
            }

            bool occludes = surface.CastsShadows &&
                            !_textures.Keyed.Contains(surface.TextureName) &&
                            Materials.Of(surface.TextureName).Occludes;

            foreach ((ushort a, ushort b, ushort c) in scene.Triangulate(polygon))
            {
                Vector3 pa = scene.Vertices[a];
                Vector3 pb = scene.Vertices[b];
                Vector3 pc = scene.Vertices[c];

                Vector3 normal = Vector3.Cross(pb - pa, pc - pa);
                normal = normal.LengthSquared() > 1e-12f ? Vector3.Normalize(normal) : Vector3.UnitY;

                Vector2 ua = scene.TexCoordFor(a);
                Vector2 ub = scene.TexCoordFor(b);
                Vector2 uc = scene.TexCoordFor(c);

                uint at = (uint)group.Item1.Count;
                group.Item1.Add(new MeshVertex(pa, normal, ua, Lightmap(ua, surface, region)));
                group.Item1.Add(new MeshVertex(pb, normal, ub, Lightmap(ub, surface, region)));
                group.Item1.Add(new MeshVertex(pc, normal, uc, Lightmap(uc, surface, region)));
                group.Item2.Add(at);
                group.Item2.Add(at + 1);
                group.Item2.Add(at + 2);

                if (occludes)
                {
                    occluderIndices.Add((uint)occluders.Count);
                    occluderIndices.Add((uint)occluders.Count + 1);
                    occluderIndices.Add((uint)occluders.Count + 2);

                    occluders.Add(pa);
                    occluders.Add(pb);
                    occluders.Add(pc);
                }

                Grow(pa);
                Grow(pb);
                Grow(pc);
            }
        }

        if (_context.SupportsRayTracing && occluderIndices.Count > 0)
        {
            _traceable.Add(new RayTracingMesh([.. occluders], [.. occluderIndices]));
        }

        foreach (((string texture, bool selfLit), (List<MeshVertex> vertices, List<uint> indices))
                 in groups)
        {
            if (indices.Count > 0)
            {
                // Scene batches routinely pass 65,535 vertices: a single wall texture in
                // the larger scenes covers more geometry than a 16-bit index can address.
                AddBatch(
                    CollectionsMarshal.AsSpan(vertices),
                    VulkanBuffer.CreateDeviceLocal<uint>(
                        _context, CollectionsMarshal.AsSpan(indices), BufferUsageFlags.IndexBufferBit),
                    IndexType.Uint32,
                    (uint)indices.Count,
                    Matrix4x4.Identity,
                    texture,
                    useLightmap: true,
                    selfLit: selfLit);
            }
        }
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
        if (!_context.SupportsRayTracing ||
            _textures.Keyed.Contains(texture) ||
            !Materials.Of(texture).Occludes)
        {
            return;
        }

        // Keyed by the batch this is about to become, so that reshaping the batch can
        // reshape the geometry rays see. Recorded before the batch is added, which is
        // what makes the count the index it will have.
        _traceable.Add(new RayTracingMesh(positions, indices.ToArray())
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
        if (_descriptorPool.Handle != 0 || _batches.Count == 0)
        {
            return;
        }

        _rayTracing ??= RayTracingScene.Build(_context, _traceable);

        // Whatever was hidden while the room was being built. The structure did not exist
        // to be told at the time, and a hidden model that still casts a shadow is worse
        // than one that is simply drawn.
        foreach (int hidden in _invisible)
        {
            _rayTracing?.SetTraced(hidden + 1, false);
        }

        var size = new DescriptorPoolSize
        {
            Type = DescriptorType.CombinedImageSampler,
            DescriptorCount = (uint)(_batches.Count * 3),
        };

        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            PoolSizeCount = 1,
            PPoolSizes = &size,
            MaxSets = (uint)_batches.Count,
        };

        if (_context.Api.CreateDescriptorPool(_context.Device, in poolInfo, null, out _descriptorPool)
            != Result.Success)
        {
            throw new VulkanException("Could not create a descriptor pool.");
        }

        for (int i = 0; i < _batches.Count; i++)
        {
            Batch batch = _batches[i];

            _batches[i] = batch with
            {
                Material = CreateMaterialSet(
                    TextureFor(batch.TextureName),
                    batch.UseLightmap && !batch.SelfLit ? _lightmap ?? _whiteTexture : _whiteTexture,
                    _textures.GetNormal(batch.TextureName),
                    _textures.GetOrm(batch.TextureName),
                    _textures.GetHeight(batch.TextureName)),
            };
        }
    }

    /// <summary>Records the draws for every loaded batch.</summary>
    /// <param name="command">Command buffer, inside an active rendering scope.</param>
    /// <param name="pipeline">The pipeline currently bound.</param>
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
    public void Record(CommandBuffer command, MeshPipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);

        Vk vk = _context.Api;

        // Reused for every batch: two vertex streams, both from the start of their buffer.
        Silk.NET.Vulkan.Buffer* streams = stackalloc Silk.NET.Vulkan.Buffer[2];
        ulong* offsets = stackalloc ulong[2] { 0, 0 };

        foreach (Batch batch in _batches)
        {
            if (batch.Material.Handle == 0 || batch.Hidden)
            {
                continue;
            }

            DescriptorSet material = batch.Material;
            vk.CmdBindDescriptorSets(
                command, PipelineBindPoint.Graphics, pipeline.Layout, 1, 1, in material, 0, null);

            pipeline.PushConstants(command, new DrawConstants(
                batch.Transform,
                batch.Previous,
                new Vector4(
                    _lightmap is not null && batch.UseLightmap ? 1f : 0f,
                    LightmapMultiplier,
                    // Two flags in one number: 1 for self-lit, 2 for a model standing in
                    // the room. The second is what lets a shadow ray leaving a character
                    // skip characters — see RayTracingScene.MaskFor.
                    (batch.SelfLit ? 1f : 0f) + (batch.IsModel ? 2f : 0f),

                    // How deep this surface's height map goes, and zero where it has none —
                    // which is what keeps the level map bound in its place from shifting
                    // every texture in the game by a constant offset.
                    _textures.HasHeight(batch.TextureName)
                        ? Materials.Of(batch.TextureName).HeightScale
                        : 0f),

                // The finish the material library measured for this texture, which is what
                // the shader uses where no ORM map overrides it. A texture nobody has
                // measured comes back matte and non-metallic, which is the surface the
                // renderer assumed before any of this existed.
                MaterialOf(batch.TextureName)));

            // The animated buffer when something has reshaped this batch, and the one the
            // model was built with otherwise.
            // Two streams: this pose and the one before it. A batch nothing has animated
            // binds the same buffer twice, which is the truth about it — its vertices are
            // where they have always been, and only its transform can have moved.
            streams[0] = (batch.Live ?? batch.Vertices).Handle;
            streams[1] = (batch.Was ?? batch.Live ?? batch.Vertices).Handle;

            vk.CmdBindVertexBuffers(command, 0, 2, streams, offsets);
            vk.CmdBindIndexBuffer(command, batch.Indices.Handle, 0, batch.IndexType);
            vk.CmdDrawIndexed(command, batch.IndexCount, 1, 0, 0, 0);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _context.Api.DeviceWaitIdle(_context.Device);

        foreach (Batch batch in _batches)
        {
            batch.Vertices.Dispose();
            batch.Indices.Dispose();
        }

        _batches.Clear();

        if (_descriptorPool.Handle != 0)
        {
            _context.Api.DestroyDescriptorPool(_context.Device, _descriptorPool, null);
            _descriptorPool = default;
        }

        foreach (DescriptorPool extra in _extraPools)
        {
            _context.Api.DestroyDescriptorPool(_context.Device, extra, null);
        }

        _extraPools.Clear();
        _repainted.Clear();

        // The textures are the renderer's and outlast this room; see TextureCache.
        _whiteTexture.Dispose();
        _lightmap?.Dispose();
        _lightmap = null;
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
        VulkanBuffer indices,
        IndexType indexType,
        uint indexCount,
        Matrix4x4 transform,
        string texture,
        bool useLightmap,
        bool selfLit = false,
        Matrix4x4? local = null,
        bool isModel = false) =>
        _batches.Add(new Batch
        {
            // Identity for the room's own geometry, which is already where it belongs.
            Local = local ?? Matrix4x4.Identity,
            Vertices = VulkanBuffer.CreateDeviceLocal<MeshVertex>(
                _context, vertices, BufferUsageFlags.VertexBufferBit),
            Shape = [.. vertices],
            Indices = indices,
            IndexCount = indexCount,
            IndexType = indexType,
            Transform = transform,

            // Where it was is where it is, on the frame it first appears. A zero matrix
            // here reports the whole screen as having moved half its width.
            Previous = transform,
            TextureName = texture,
            UseLightmap = useLightmap,
            SelfLit = selfLit,
            IsModel = isModel,
        });

    private VulkanTexture TextureFor(string name) =>
        _textures.Get(name);

    /// <summary>Takes a material set from whichever pool still has room.</summary>
    /// <remarks>
    /// The room's own pool first, then any opened since, then a new one. Allocation
    /// failing is how a pool says it is full — Vulkan reports it rather than trapping —
    /// so it is a case to handle and not an error.
    /// </remarks>
    private DescriptorSet Allocate()
    {
        DescriptorSetLayout layout = _pipeline.MaterialLayout;

        DescriptorSet? From(DescriptorPool pool)
        {
            if (pool.Handle == 0)
            {
                return null;
            }

            DescriptorSetLayout wanted = layout;

            var info = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = pool,
                DescriptorSetCount = 1,
                PSetLayouts = &wanted,
            };

            return _context.Api.AllocateDescriptorSets(_context.Device, in info, out DescriptorSet set)
                   == Result.Success
                ? set
                : null;
        }

        if (From(_descriptorPool) is { } fromRoom)
        {
            return fromRoom;
        }

        for (int i = _extraPools.Count - 1; i >= 0; i--)
        {
            if (From(_extraPools[i]) is { } fromExtra)
            {
                return fromExtra;
            }
        }

        var size = new DescriptorPoolSize
        {
            Type = DescriptorType.CombinedImageSampler,

            // Five images a set: colour, lightmap, normal, ORM, height. Raised with the
            // layout, or a pool runs out partway through a room and the sets after it are
            // never allocated.
            DescriptorCount = ExtraPoolSets * 5,
        };

        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            PoolSizeCount = 1,
            PPoolSizes = &size,
            MaxSets = ExtraPoolSets,
        };

        if (_context.Api.CreateDescriptorPool(_context.Device, in poolInfo, null, out DescriptorPool opened)
            != Result.Success)
        {
            throw new VulkanException("Could not create a descriptor pool.");
        }

        _extraPools.Add(opened);

        return From(opened) ??
               throw new VulkanException("Could not allocate a material descriptor set.");
    }

    private DescriptorSet CreateMaterialSet(
        VulkanTexture diffuse,
        VulkanTexture lightmap,
        VulkanTexture normal,
        VulkanTexture orm,
        VulkanTexture height)
    {
        DescriptorSet set = Allocate();

        var diffuseInfo = new DescriptorImageInfo
        {
            ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
            ImageView = diffuse.View,
            Sampler = diffuse.Sampler,
        };

        var lightmapInfo = new DescriptorImageInfo
        {
            ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
            ImageView = lightmap.View,
            Sampler = lightmap.Sampler,
        };

        var normalInfo = new DescriptorImageInfo
        {
            ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
            ImageView = normal.View,
            Sampler = normal.Sampler,
        };

        var ormInfo = new DescriptorImageInfo
        {
            ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
            ImageView = orm.View,
            Sampler = orm.Sampler,
        };

        var heightInfo = new DescriptorImageInfo
        {
            ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
            ImageView = height.View,
            Sampler = height.Sampler,
        };

        WriteDescriptorSet* writes = stackalloc WriteDescriptorSet[5];
        writes[0] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = set,
            DstBinding = 0,
            DescriptorType = DescriptorType.CombinedImageSampler,
            DescriptorCount = 1,
            PImageInfo = &diffuseInfo,
        };
        writes[1] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = set,
            DstBinding = 1,
            DescriptorType = DescriptorType.CombinedImageSampler,
            DescriptorCount = 1,
            PImageInfo = &lightmapInfo,
        };

        writes[2] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = set,
            DstBinding = 2,
            DescriptorType = DescriptorType.CombinedImageSampler,
            DescriptorCount = 1,
            PImageInfo = &normalInfo,
        };

        writes[3] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = set,
            DstBinding = 3,
            DescriptorType = DescriptorType.CombinedImageSampler,
            DescriptorCount = 1,
            PImageInfo = &ormInfo,
        };

        writes[4] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = set,
            DstBinding = 4,
            DescriptorType = DescriptorType.CombinedImageSampler,
            DescriptorCount = 1,
            PImageInfo = &heightInfo,
        };

        _context.Api.UpdateDescriptorSets(_context.Device, 5, writes, 0, null);
        return set;
    }

    /// <summary>One drawable piece: a mesh with one diffuse texture.</summary>
    private readonly record struct Batch
    {
        public required VulkanBuffer Vertices { get; init; }

        /// <summary>The vertices as the model authored them, reused as scratch when animated.</summary>
        public required MeshVertex[] Shape { get; init; }

        /// <summary>One buffer per frame in flight, once anything has animated this batch.</summary>
        public VulkanBuffer[]? Animated { get; init; }

        /// <summary>Whichever animated buffer was written most recently.</summary>
        public VulkanBuffer? Live { get; init; }

        /// <summary>The pose before that one.</summary>
        public VulkanBuffer? Was { get; init; }

        /// <summary>This mesh's place within its model.</summary>
        /// <remarks>
        /// Rays see one structure per model, placed by one transform, so each mesh's own
        /// transform has to be folded into the vertices handed to it — which means
        /// knowing what that transform currently is.
        /// </remarks>
        public Matrix4x4 Local { get; init; }

        public required VulkanBuffer Indices { get; init; }

        public required uint IndexCount { get; init; }

        public required IndexType IndexType { get; init; }

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

        /// <summary>The surface carries its own brightness and the bake does not touch it.</summary>
        public bool SelfLit { get; init; }

        /// <summary>A model standing in the room, rather than the room itself.</summary>
        /// <remarks>
        /// Carried through to the shader so a shadow ray leaving this pixel knows to skip
        /// the models: GK3's people are a stack of overlapping shells and a ray leaving a
        /// shirt hits the arm inside it. See <see cref="RayTracingScene.MaskFor"/>.
        /// </remarks>
        public bool IsModel { get; init; }

        /// <summary>What is drawn on it instead of its own texture, if anything is.</summary>
        /// <remarks>
        /// A character's face while they talk or blink. The original texture's name is kept
        /// beside it because that is what the normal map is filed under, and because
        /// putting the face back is asking for the model's own picture again.
        /// </remarks>
        public string? Painted { get; init; }

        /// <summary>Whether it is kept out of the picture.</summary>
        /// <remarks>
        /// A model a scene declares <c>hidden</c>, or one a script has hidden. It is loaded
        /// and placed either way, because <c>ShowModel</c> is how the story brings it out
        /// and there is no way to do that with something that was never read. Written the
        /// negative way round because a batch is a struct and the common case has to be
        /// the default one.
        /// </remarks>
        public bool Hidden { get; init; }

        public DescriptorSet Material { get; init; }
    }
}
