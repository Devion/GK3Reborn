using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Rendering.Geometry;
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace GK3Reborn.Rendering.Vulkan;

/// <summary>A Vulkan buffer, as a scene refers to one.</summary>
internal sealed class VulkanGeometryBuffer : IGeometryBuffer
{
    private readonly VulkanBuffer _buffer;
    private bool _disposed;

    internal VulkanGeometryBuffer(VulkanBuffer buffer) => _buffer = buffer;

    /// <summary>The buffer underneath, for whatever binds it.</summary>
    internal VulkanBuffer Buffer => _buffer;

    /// <inheritdoc/>
    public ulong Bytes => _buffer.Size;

    /// <inheritdoc/>
    public void Write<T>(ReadOnlySpan<T> data)
        where T : unmanaged => _buffer.Write(data);

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _buffer.Dispose();
    }
}

/// <summary>A Vulkan texture, as a scene refers to one.</summary>
internal sealed class VulkanGeometryTexture : IGeometryTexture
{
    private readonly bool _owned;
    private bool _disposed;

    internal VulkanGeometryTexture(VulkanTexture texture, long bytes, bool owned)
    {
        Texture = texture;
        Bytes = bytes;
        _owned = owned;
    }

    /// <summary>The texture underneath.</summary>
    internal VulkanTexture Texture { get; }

    /// <inheritdoc/>
    public long Bytes { get; }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed || !_owned)
        {
            return;
        }

        _disposed = true;
        Texture.Dispose();
    }
}

/// <summary>A Vulkan descriptor set, as a scene refers to one.</summary>
internal sealed class VulkanGeometryMaterial : IGeometryMaterial
{
    internal VulkanGeometryMaterial(DescriptorSet set) => Set = set;

    /// <summary>The set underneath.</summary>
    internal DescriptorSet Set { get; }
}

/// <summary>A batch of Vulkan staging copies, as a scene refers to one.</summary>
internal sealed class VulkanGeometryUploads : IGeometryUploads
{
    private bool _disposed;

    internal VulkanGeometryUploads(BufferUploads uploads) => Uploads = uploads;

    /// <summary>The batch underneath.</summary>
    internal BufferUploads Uploads { get; }

    /// <inheritdoc/>
    public void Submit() => Uploads.Submit();

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Uploads.Dispose();
    }
}

/// <summary>
/// Puts a scene's geometry and textures on a Vulkan device.
/// </summary>
/// <remarks>
/// <para>
/// The Vulkan half of <see cref="IGeometryDevice"/>: an adapter rather than an
/// implementation. Everything below already existed — <c>VulkanBuffer</c>,
/// <c>TextureCache</c>, the material descriptor sets that used to live in
/// <c>SceneGeometry</c> — and this is what gives it a shape the Direct3D backend can offer
/// too.
/// </para>
/// <para>
/// The descriptor pools moved here with the material sets, because they are the same
/// subject. Two pools, and the reason for the second is worth keeping: the first is sized
/// for exactly the batches a room loaded, which is right for everything the loader knows
/// about and wrong the moment a face starts moving. Repainting a texture is a new
/// combination of images and therefore a new set, so more pools are opened as they are
/// needed and the common case — a room where nothing repaints — costs nothing.
/// </para>
/// </remarks>
public sealed unsafe class VulkanGeometryDevice : IGeometryDevice
{
    /// <summary>How many images one material set binds.</summary>
    /// <remarks>
    /// Colour, lightmap, normal, occlusion-roughness-metalness, height. It has to match the
    /// layout: a pool sized for fewer runs out partway through a room, and every set after
    /// that falls through to an overflow pool that should not have been needed. The room
    /// pool used to ask for three.
    /// </remarks>
    private const int ImagesPerMaterial = 5;

    /// <summary>How many sets each pool opened after loading holds.</summary>
    private const int ExtraPoolSets = 64;

    private readonly VulkanContext _context;
    private readonly MeshPipeline _pipeline;
    private readonly TextureCache _textures;
    private readonly VulkanGeometryTexture _white;
    private readonly List<DescriptorPool> _pools = [];
    private bool _disposed;

    /// <summary>Creates a device.</summary>
    /// <param name="context">The Vulkan device.</param>
    /// <param name="pipeline">The mesh pipeline whose material layout the sets are made for.</param>
    /// <param name="textures">The renderer's texture cache, which outlives a room.</param>
    public VulkanGeometryDevice(VulkanContext context, MeshPipeline pipeline, TextureCache textures)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(textures);

        _context = context;
        _pipeline = pipeline;
        _textures = textures;

        // Bound wherever a batch has no lightmap. Vulkan requires every declared binding to
        // point at something valid even when the shader ignores what it reads.
        _white = new VulkanGeometryTexture(VulkanTexture.Create(context, Solid(255)), 0, owned: true);
    }

    /// <summary>The Vulkan device underneath.</summary>
    internal VulkanContext Context => _context;

    /// <summary>The texture cache underneath.</summary>
    internal TextureCache Textures => _textures;

    /// <inheritdoc/>
    public bool SupportsRayTracing => _context.SupportsRayTracing;

    /// <inheritdoc/>
    public bool BlockCompression => _context.Capabilities.BlockCompression;

    /// <inheritdoc/>
    public int TextureCount => _textures.Count;

    /// <inheritdoc/>
    public int TexturesReused => _textures.Reused;

    /// <inheritdoc/>
    public long TextureBytes => _textures.DeviceBytes;

    /// <inheritdoc/>
    public IGeometryTexture White => _white;

    /// <inheritdoc/>
    public IGeometryTexture Flat => new VulkanGeometryTexture(_textures.Flat, 0, owned: false);

    /// <inheritdoc/>
    public IGeometryTexture Neutral => new VulkanGeometryTexture(_textures.Neutral, 0, owned: false);

    /// <inheritdoc/>
    public IGeometryTexture Level => new VulkanGeometryTexture(_textures.Level, 0, owned: false);

    /// <inheritdoc/>
    public IGeometryUploads BeginUploads() => new VulkanGeometryUploads(new BufferUploads(_context));

    /// <inheritdoc/>
    public IGeometryBuffer CreateBuffer<T>(
        ReadOnlySpan<T> data, GeometryBufferKind kind, IGeometryUploads? into = null)
        where T : unmanaged
    {
        BufferUsageFlags usage = kind == GeometryBufferKind.Vertices
            ? BufferUsageFlags.VertexBufferBit
            : BufferUsageFlags.IndexBufferBit;

        BufferUploads? batch = (into as VulkanGeometryUploads)?.Uploads;

        return new VulkanGeometryBuffer(
            VulkanBuffer.CreateDeviceLocal(_context, data, usage, batch));
    }

    /// <inheritdoc/>
    public IGeometryBuffer CreateDynamicVertices(ulong bytes) =>
        new VulkanGeometryBuffer(
            VulkanBuffer.CreateHostVisible(_context, bytes, BufferUsageFlags.VertexBufferBit));

    /// <inheritdoc/>
    public bool HasTexture(string name) => _textures.Has(name);

    /// <inheritdoc/>
    public void AddTexture(
        string name, DecodedImage image, GeometryTextureKind kind = GeometryTextureKind.Colour)
    {
        switch (kind)
        {
            case GeometryTextureKind.Data:
                _textures.AddNormal(name, image);
                break;

            default:
                _textures.Add(name, image);
                break;
        }
    }

    /// <inheritdoc/>
    public void AddTexture(
        string name, CompressedImage image, GeometryTextureKind kind = GeometryTextureKind.Colour)
    {
        switch (kind)
        {
            case GeometryTextureKind.Data:
                _textures.AddNormal(name, image);
                break;

            default:
                _textures.Add(name, image);
                break;
        }
    }

    /// <inheritdoc/>
    public IGeometryTexture Texture(string name) =>
        new VulkanGeometryTexture(_textures.Get(name), 0, owned: false);

    /// <inheritdoc/>
    public IGeometryMaterial CreateMaterial(
        IGeometryTexture diffuse,
        IGeometryTexture lightmap,
        IGeometryTexture normal,
        IGeometryTexture orm,
        IGeometryTexture height)
    {
        ArgumentNullException.ThrowIfNull(diffuse);
        ArgumentNullException.ThrowIfNull(lightmap);
        ArgumentNullException.ThrowIfNull(normal);
        ArgumentNullException.ThrowIfNull(orm);
        ArgumentNullException.ThrowIfNull(height);

        return new VulkanGeometryMaterial(
            Write(
                Allocate(),
                Of(diffuse),
                Of(lightmap),
                Of(normal),
                Of(orm),
                Of(height)));
    }

    /// <summary>Opens a pool sized for a room that is about to be built.</summary>
    /// <param name="materials">How many materials it will need.</param>
    /// <exception cref="VulkanException">The pool could not be created.</exception>
    /// <remarks>
    /// Not part of the interface, because only Vulkan has pools. Direct3D allocates a
    /// descriptor table out of a heap that was already there.
    /// </remarks>
    public void Reserve(int materials)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Open(Math.Max(1, materials));
    }

    /// <inheritdoc/>
    public void Wait() => _context.Api.DeviceWaitIdle(_context.Device);

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _white.Dispose();

        foreach (DescriptorPool pool in _pools)
        {
            _context.Api.DestroyDescriptorPool(_context.Device, pool, null);
        }

        _pools.Clear();
    }

    /// <summary>A solid grey square, for the textures a surface does not have.</summary>
    internal static DecodedImage Solid(byte value)
    {
        const int Size = 4;
        byte[] pixels = new byte[Size * Size * 4];
        Array.Fill(pixels, value);

        return new DecodedImage(Size, Size, pixels, HasAlpha: false, "solid");
    }

    private static VulkanTexture Of(IGeometryTexture texture) =>
        texture is VulkanGeometryTexture vulkan
            ? vulkan.Texture
            : throw new ArgumentException("That texture is not on this device.", nameof(texture));

    private DescriptorPool Open(int sets)
    {
        var size = new DescriptorPoolSize
        {
            Type = DescriptorType.CombinedImageSampler,
            DescriptorCount = (uint)(sets * ImagesPerMaterial),
        };

        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            PoolSizeCount = 1,
            PPoolSizes = &size,
            MaxSets = (uint)sets,
        };

        if (_context.Api.CreateDescriptorPool(_context.Device, in poolInfo, null, out DescriptorPool pool)
            != Result.Success)
        {
            throw new VulkanException("Could not create a descriptor pool.");
        }

        _pools.Add(pool);
        return pool;
    }

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

        // Newest first: an older pool that has just been found full will be found full
        // again by every allocation after this one.
        for (int i = _pools.Count - 1; i >= 0; i--)
        {
            if (From(_pools[i]) is { } existing)
            {
                return existing;
            }
        }

        return From(Open(ExtraPoolSets))
               ?? throw new VulkanException("Could not allocate a material descriptor set.");
    }

    private DescriptorSet Write(
        DescriptorSet set,
        VulkanTexture diffuse,
        VulkanTexture lightmap,
        VulkanTexture normal,
        VulkanTexture orm,
        VulkanTexture height)
    {
        VulkanTexture[] images = [diffuse, lightmap, normal, orm, height];

        DescriptorImageInfo* infos = stackalloc DescriptorImageInfo[ImagesPerMaterial];
        WriteDescriptorSet* writes = stackalloc WriteDescriptorSet[ImagesPerMaterial];

        for (int i = 0; i < ImagesPerMaterial; i++)
        {
            infos[i] = new DescriptorImageInfo
            {
                ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
                ImageView = images[i].View,
                Sampler = images[i].Sampler,
            };

            writes[i] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = set,
                DstBinding = (uint)i,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.CombinedImageSampler,
                PImageInfo = &infos[i],
            };
        }

        _context.Api.UpdateDescriptorSets(_context.Device, ImagesPerMaterial, writes, 0, null);
        return set;
    }
}
