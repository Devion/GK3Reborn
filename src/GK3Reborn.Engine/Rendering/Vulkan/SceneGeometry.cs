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
    private readonly Dictionary<string, VulkanTexture> _textures = new(StringComparer.OrdinalIgnoreCase);
    private readonly VulkanTexture _fallbackTexture;
    private readonly VulkanTexture _whiteTexture;

    private VulkanTexture? _lightmap;
    private IReadOnlyList<Vector4>? _lightmapRegions;
    private DescriptorPool _descriptorPool;
    private Vector3 _minimum = new(float.MaxValue);
    private Vector3 _maximum = new(float.MinValue);

    private SceneGeometry(VulkanContext context, MeshPipeline pipeline)
    {
        _context = context;
        _pipeline = pipeline;

        // A model referencing a texture the corpus does not contain still has to draw, and
        // a wrong-looking texture is better than a silently black one.
        _fallbackTexture = VulkanTexture.Create(context, CheckerBoard());

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

    /// <summary>Lower corner of everything loaded, in world space.</summary>
    public Vector3 Minimum => _batches.Count > 0 ? _minimum : Vector3.Zero;

    /// <summary>Upper corner of everything loaded, in world space.</summary>
    public Vector3 Maximum => _batches.Count > 0 ? _maximum : Vector3.One;

    /// <summary>Creates an empty scene.</summary>
    /// <param name="context">Device context.</param>
    /// <param name="pipeline">Pipeline its descriptor sets must match.</param>
    /// <returns>The scene.</returns>
    public static SceneGeometry Create(VulkanContext context, MeshPipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(pipeline);

        return new SceneGeometry(context, pipeline);
    }

    /// <summary>Uploads a texture under a name models can reference.</summary>
    /// <param name="name">Texture name, matched case-insensitively.</param>
    /// <param name="image">The decoded image.</param>
    public void AddTexture(string name, DecodedImage image)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (!_textures.ContainsKey(name))
        {
            // Keying happens before upload so that mip generation never sees the key
            // colour; see TextureKeying.
            _textures[name] = VulkanTexture.Create(_context, TextureKeying.Apply(image));
        }
    }

    /// <summary>Loads a model.</summary>
    /// <param name="model">The parsed model.</param>
    /// <param name="transform">Where to place it, or null for its authored position.</param>
    public void Add(ModFile model, Matrix4x4? transform = null)
    {
        ArgumentNullException.ThrowIfNull(model);

        Matrix4x4 placement = transform ?? Matrix4x4.Identity;

        foreach (ModMesh mesh in model.Meshes)
        {
            Matrix4x4 meshToWorld = mesh.MeshToLocal * placement;

            foreach (ModSubmesh submesh in mesh.Submeshes)
            {
                if (submesh.Positions.Length == 0 || submesh.Indices.Length == 0)
                {
                    continue;
                }

                MeshVertex[] vertices = new MeshVertex[submesh.Positions.Length];

                for (int i = 0; i < vertices.Length; i++)
                {
                    vertices[i] = new MeshVertex(
                        submesh.Positions[i],
                        i < submesh.Normals.Length ? submesh.Normals[i] : Vector3.UnitY,
                        i < submesh.TexCoords.Length ? submesh.TexCoords[i] : Vector2.Zero,
                        Vector2.Zero);

                    Grow(Vector3.Transform(submesh.Positions[i], meshToWorld));
                }

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
    }

    /// <summary>Loads a scene's geometry.</summary>
    /// <param name="scene">The parsed scene.</param>
    /// <param name="lightmaps">The scene's baked lightmaps, in surface order, if any.</param>
    /// <remarks>
    /// BSP files carry no normals, so each triangle gets the normal of its own plane. Flat
    /// shading is wrong for the few curved surfaces a scene contains and right for the
    /// walls, floors and doorways that make up nearly all of them, and it invents no
    /// smoothing groups the data never had.
    /// </remarks>
    public void AddScene(BspFile scene, MulFile? lightmaps = null)
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

        Dictionary<string, (List<MeshVertex> Vertices, List<uint> Indices)> groups =
            new(StringComparer.OrdinalIgnoreCase);

        foreach (BspPolygon polygon in scene.Polygons)
        {
            if (polygon.SurfaceIndex < 0 || polygon.SurfaceIndex >= scene.Surfaces.Count)
            {
                continue;
            }

            BspSurface surface = scene.Surfaces[polygon.SurfaceIndex];

            Vector4 region = _lightmapRegions is not null && polygon.SurfaceIndex < _lightmapRegions.Count
                ? _lightmapRegions[polygon.SurfaceIndex]
                : Vector4.Zero;

            if (!groups.TryGetValue(surface.TextureName, out (List<MeshVertex>, List<uint>) group))
            {
                group = ([], []);
                groups[surface.TextureName] = group;
            }

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

                Grow(pa);
                Grow(pb);
                Grow(pc);
            }
        }

        foreach ((string texture, (List<MeshVertex> vertices, List<uint> indices)) in groups)
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
                    useLightmap: true);
            }
        }
    }

    /// <summary>Builds the descriptor sets the loaded batches need.</summary>
    /// <remarks>
    /// Called once, after loading and before the first draw. The sets are immutable
    /// afterwards, so nothing has to be rebuilt or synchronised per frame.
    /// </remarks>
    public void Finish()
    {
        if (_descriptorPool.Handle != 0 || _batches.Count == 0)
        {
            return;
        }

        var size = new DescriptorPoolSize
        {
            Type = DescriptorType.CombinedImageSampler,
            DescriptorCount = (uint)(_batches.Count * 2),
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
                    batch.UseLightmap ? _lightmap ?? _whiteTexture : _whiteTexture),
            };
        }
    }

    /// <summary>Records the draws for every loaded batch.</summary>
    /// <param name="command">Command buffer, inside an active rendering scope.</param>
    /// <remarks>
    /// The caller binds the pipeline, the viewport and the frame's descriptor set first;
    /// this only issues what varies per batch.
    /// </remarks>
    public void Record(CommandBuffer command)
    {
        Vk vk = _context.Api;

        foreach (Batch batch in _batches)
        {
            if (batch.Material.Handle == 0)
            {
                continue;
            }

            DescriptorSet material = batch.Material;
            vk.CmdBindDescriptorSets(
                command, PipelineBindPoint.Graphics, _pipeline.Layout, 1, 1, in material, 0, null);

            _pipeline.PushConstants(command, new DrawConstants(
                batch.Transform,
                new Vector4(
                    _lightmap is not null && batch.UseLightmap ? 1f : 0f, LightmapMultiplier, 0, 0)));

            ulong offset = 0;
            Silk.NET.Vulkan.Buffer vertices = batch.Vertices.Handle;
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

        foreach (VulkanTexture texture in _textures.Values)
        {
            texture.Dispose();
        }

        _textures.Clear();
        _fallbackTexture.Dispose();
        _whiteTexture.Dispose();
        _lightmap?.Dispose();
        _lightmap = null;
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

    /// <summary>A visibly wrong texture, so a missing one is obvious rather than silent.</summary>
    private static DecodedImage CheckerBoard()
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
        bool useLightmap) =>
        _batches.Add(new Batch
        {
            Vertices = VulkanBuffer.CreateDeviceLocal<MeshVertex>(
                _context, vertices, BufferUsageFlags.VertexBufferBit),
            Indices = indices,
            IndexCount = indexCount,
            IndexType = indexType,
            Transform = transform,
            TextureName = texture,
            UseLightmap = useLightmap,
        });

    private VulkanTexture TextureFor(string name) =>
        name.Length > 0 && _textures.TryGetValue(name, out VulkanTexture? texture)
            ? texture
            : _fallbackTexture;

    private DescriptorSet CreateMaterialSet(VulkanTexture diffuse, VulkanTexture lightmap)
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

        WriteDescriptorSet* writes = stackalloc WriteDescriptorSet[2];
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

        _context.Api.UpdateDescriptorSets(_context.Device, 2, writes, 0, null);
        return set;
    }

    /// <summary>One drawable piece: a mesh with one diffuse texture.</summary>
    private readonly record struct Batch
    {
        public required VulkanBuffer Vertices { get; init; }

        public required VulkanBuffer Indices { get; init; }

        public required uint IndexCount { get; init; }

        public required IndexType IndexType { get; init; }

        public required Matrix4x4 Transform { get; init; }

        public required string TextureName { get; init; }

        public required bool UseLightmap { get; init; }

        public DescriptorSet Material { get; init; }
    }
}
