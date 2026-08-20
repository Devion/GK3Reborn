using System.Numerics;
using System.Runtime.InteropServices;
using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Formats.Lightmaps;
using GK3Reborn.Formats.Models;
using GK3Reborn.Formats.Scenes;
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

    /// <summary>How many frames the renderer keeps in flight.</summary>
    /// <remarks>
    /// Must match <c>VulkanRenderer.FramesInFlight</c>. An animated batch keeps one vertex
    /// buffer per frame so that writing this frame's pose cannot disturb one the device has
    /// not finished reading.
    /// </remarks>
    private const int FramesInFlight = 2;
    private readonly TextureCache _textures;
    private readonly VulkanTexture _whiteTexture;


    private readonly List<RayTracingMesh> _traceable = [];

    private RayTracingScene? _rayTracing;
    private VulkanTexture? _lightmap;
    private IReadOnlyList<Vector4>? _lightmapRegions;
    private DescriptorPool _descriptorPool;
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
    public bool HasNormalMap(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return _textures.HasNormal(name);
    }

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
            Matrix4x4 meshToWorld = meshTurns is not null && meshTurns.TryGetValue(index, out Matrix4x4 turn)
                ? turn * mesh.MeshToLocal * placement
                : mesh.MeshToLocal * placement;

            foreach (ModSubmesh submesh in mesh.Submeshes)
            {
                if (submesh.Positions.Length == 0 || submesh.Indices.Length == 0)
                {
                    continue;
                }

                MeshVertex[] vertices = new MeshVertex[submesh.Positions.Length];
                var world = new Vector3[submesh.Positions.Length];

                for (int i = 0; i < vertices.Length; i++)
                {
                    vertices[i] = new MeshVertex(
                        submesh.Positions[i],
                        i < submesh.Normals.Length ? submesh.Normals[i] : Vector3.UnitY,
                        i < submesh.TexCoords.Length ? submesh.TexCoords[i] : Vector2.Zero,
                        Vector2.Zero);

                    world[i] = Vector3.Transform(submesh.Positions[i], meshToWorld);
                    Grow(world[i]);
                }

                RecordTraceable(submesh.TextureName, world, submesh.Indices);

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
                    useLightmap: false);
            }
        }

        _placed.Add((model, placement));
        return new ModelPlacement(_placements.Count - 1);
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
            _batches[index] = batch with { Live = buffers[slot] };
        }

        _pendingShapes.Clear();
    }

    /// <summary>Gives a batch the buffers it needs to be animated.</summary>
    private VulkanBuffer[] Animate(int index, ref Batch batch)
    {
        VulkanBuffer[] buffers = new VulkanBuffer[FramesInFlight];

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
            _batches[index] = _batches[index] with { Transform = meshToWorld };
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

        foreach ((int mesh, List<int> batches) in _placements[placement.Id])
        {
            if (mesh >= model.Meshes.Count)
            {
                continue;
            }

            Matrix4x4 meshToWorld = model.Meshes[mesh].MeshToLocal * transform;

            foreach (int index in batches)
            {
                _batches[index] = _batches[index] with { Transform = meshToWorld };
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

            bool occludes = surface.CastsShadows && !_textures.Keyed.Contains(surface.TextureName);

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
    private void RecordTraceable(string texture, Vector3[] positions, ReadOnlySpan<ushort> indices)
    {
        var widened = new uint[indices.Length];

        for (int i = 0; i < indices.Length; i++)
        {
            widened[i] = indices[i];
        }

        RecordTraceable(texture, positions, widened);
    }

    /// <summary>Records a batch's triangles for ray tracing, if it is opaque.</summary>
    private void RecordTraceable(string texture, Vector3[] positions, ReadOnlySpan<uint> indices)
    {
        if (!_context.SupportsRayTracing || _textures.Keyed.Contains(texture))
        {
            return;
        }

        _traceable.Add(new RayTracingMesh(positions, indices.ToArray()));
    }

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
                    _textures.GetNormal(batch.TextureName)),
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

        foreach (Batch batch in _batches)
        {
            if (batch.Material.Handle == 0)
            {
                continue;
            }

            DescriptorSet material = batch.Material;
            vk.CmdBindDescriptorSets(
                command, PipelineBindPoint.Graphics, pipeline.Layout, 1, 1, in material, 0, null);

            pipeline.PushConstants(command, new DrawConstants(
                batch.Transform,
                new Vector4(
                    _lightmap is not null && batch.UseLightmap ? 1f : 0f,
                    LightmapMultiplier,
                    batch.SelfLit ? 1f : 0f,
                    0)));

            ulong offset = 0;

            // The animated buffer when something has reshaped this batch, and the one the
            // model was built with otherwise.
            Silk.NET.Vulkan.Buffer vertices = (batch.Live ?? batch.Vertices).Handle;
            vk.CmdBindVertexBuffers(command, 0, 1, in vertices, in offset);
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
        bool selfLit = false) =>
        _batches.Add(new Batch
        {
            Vertices = VulkanBuffer.CreateDeviceLocal<MeshVertex>(
                _context, vertices, BufferUsageFlags.VertexBufferBit),
            Shape = [.. vertices],
            Indices = indices,
            IndexCount = indexCount,
            IndexType = indexType,
            Transform = transform,
            TextureName = texture,
            UseLightmap = useLightmap,
            SelfLit = selfLit,
        });

    private VulkanTexture TextureFor(string name) =>
        _textures.Get(name);

    private DescriptorSet CreateMaterialSet(
        VulkanTexture diffuse, VulkanTexture lightmap, VulkanTexture normal)
    {
        DescriptorSetLayout layout = _pipeline.MaterialLayout;

        var allocateInfo = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _descriptorPool,
            DescriptorSetCount = 1,
            PSetLayouts = &layout,
        };

        if (_context.Api.AllocateDescriptorSets(_context.Device, in allocateInfo, out DescriptorSet set)
            != Result.Success)
        {
            throw new VulkanException("Could not allocate a material descriptor set.");
        }

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

        WriteDescriptorSet* writes = stackalloc WriteDescriptorSet[3];
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

        _context.Api.UpdateDescriptorSets(_context.Device, 3, writes, 0, null);
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

        public required VulkanBuffer Indices { get; init; }

        public required uint IndexCount { get; init; }

        public required IndexType IndexType { get; init; }

        public required Matrix4x4 Transform { get; init; }

        public required string TextureName { get; init; }

        public required bool UseLightmap { get; init; }

        /// <summary>The surface carries its own brightness and the bake does not touch it.</summary>
        public bool SelfLit { get; init; }

        public DescriptorSet Material { get; init; }
    }
}
