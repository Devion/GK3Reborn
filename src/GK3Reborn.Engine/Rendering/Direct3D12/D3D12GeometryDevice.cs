using System.Numerics;
using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Rendering.Geometry;
using Silk.NET.Direct3D12;

namespace GK3Reborn.Rendering.Direct3D12;

/// <summary>A Direct3D buffer, as a scene refers to one.</summary>
internal sealed class D3D12GeometryBuffer : IGeometryBuffer
{
    private bool _disposed;

    internal D3D12GeometryBuffer(D3D12Buffer buffer, GeometryBufferKind kind)
    {
        Buffer = buffer;
        Kind = kind;
    }

    /// <summary>The buffer underneath, for whatever binds it.</summary>
    internal D3D12Buffer Buffer { get; }

    /// <summary>What it is for, which decides how it binds.</summary>
    internal GeometryBufferKind Kind { get; }

    /// <inheritdoc/>
    public ulong Bytes => Buffer.Bytes;

    /// <inheritdoc/>
    public void Write<T>(ReadOnlySpan<T> data)
        where T : unmanaged => Buffer.Write(data);

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Buffer.Dispose();
    }
}

/// <summary>A Direct3D texture, as a scene refers to one.</summary>
internal sealed class D3D12GeometryTexture : IGeometryTexture
{
    private readonly D3D12Context _context;
    private bool _disposed;

    internal D3D12GeometryTexture(D3D12Context context, D3D12Texture texture, long bytes)
    {
        _context = context;
        Texture = texture;
        Bytes = bytes;
    }

    /// <summary>The texture underneath.</summary>
    internal D3D12Texture Texture { get; }

    /// <inheritdoc/>
    public long Bytes { get; }

    /// <inheritdoc/>
    public void Refresh(ReadOnlySpan<byte> pixels, int width, int height) =>
        D3D12TextureUpload.Refresh(
            _context,
            Texture,
            new DecodedImage(width, height, pixels.ToArray(), HasAlpha: false, "refresh"));

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Texture.Dispose();
    }
}

/// <summary>
/// A run of descriptors a batch draws with, as a scene refers to one.
/// </summary>
/// <remarks>
/// Where Vulkan has a descriptor set object, Direct3D has a place in a heap. So a material
/// here is a number: the index of the first of five contiguous descriptors, which the draw
/// turns into a GPU handle and binds as a table. Keeping the index rather than the handle is
/// deliberate — a heap that is reset and refilled between rooms gives out the same indices
/// and different handles.
/// </remarks>
internal sealed class D3D12GeometryMaterial : IGeometryMaterial
{
    internal D3D12GeometryMaterial(uint first) => First = first;

    /// <summary>Where the material's five descriptors start.</summary>
    internal uint First { get; }
}

/// <summary>A Direct3D acceleration structure, as a scene refers to one.</summary>
internal sealed class D3D12GeometryStructure : IGeometryAccelerationStructure
{
    private bool _disposed;

    internal D3D12GeometryStructure(D3D12AccelerationStructure structure) => Structure = structure;

    /// <summary>The structure underneath, for whatever traces against it.</summary>
    internal D3D12AccelerationStructure Structure { get; }

    /// <inheritdoc/>
    public int TriangleCount => Structure.TriangleCount;

    /// <inheritdoc/>
    public int PartCount => Structure.PartCount;

    /// <inheritdoc/>
    public void Move(int part, Matrix4x4 transform) => Structure.Move(part, transform);

    /// <inheritdoc/>
    public void SetTraced(int part, bool traced) => Structure.SetTraced(part, traced);

    /// <inheritdoc/>
    public void Reshape(int key, ReadOnlySpan<Vector3> positions) => Structure.Reshape(key, positions);

    /// <inheritdoc/>
    public void Settle() => Structure.Settle();

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Structure.Dispose();
    }
}

/// <summary>A batch of Direct3D staging copies, as a scene refers to one.</summary>
internal sealed class D3D12GeometryUploads : IGeometryUploads
{
    private bool _disposed;

    internal D3D12GeometryUploads(D3D12Uploads uploads) => Uploads = uploads;

    /// <summary>The batch underneath.</summary>
    internal D3D12Uploads Uploads { get; }

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
/// Puts a scene's geometry and textures on a Direct3D device.
/// </summary>
/// <remarks>
/// <para>
/// The Direct3D half of <see cref="IGeometryDevice"/>. It answers the same questions as the
/// Vulkan half and keeps almost nothing in common with it, because the two APIs disagree
/// about what a bound texture <em>is</em>.
/// </para>
/// <para>
/// Vulkan allocates a descriptor set out of a pool and writes five images into it. Direct3D
/// has no such object: there is one shader-visible heap, a material is five contiguous slots
/// in it, and what a draw binds is the address of the first. So the heap is made once and
/// sized for the room, materials are handed out of it in order, and the whole thing is reset
/// when the room is unloaded — which is exactly the lifetime a bump allocator suits and the
/// reason <see cref="D3D12DescriptorHeap"/> is one.
/// </para>
/// <para>
/// <b>The samplers are one run, shared by every material.</b> They cannot be per material:
/// Direct3D keeps samplers in a heap of their own and a shader-visible one holds two thousand
/// and forty-eight descriptors, so five apiece would run out at four hundred and nine batches
/// — which a room reaches. They need not be, either. Which sampler each of the five textures
/// wants is a property of what the texture <em>is</em> — a lightmap and a height map are read
/// once across a surface and must not wrap; a wall and a floor tile — and that is the same
/// for every material in the game. So there is exactly one run of five, and every batch binds
/// it.
/// </para>
/// </remarks>
public sealed unsafe class D3D12GeometryDevice : IGeometryDevice
{
    /// <summary>How many textures one material binds.</summary>
    /// <remarks>
    /// Colour, lightmap, normal, occlusion-roughness-metalness, height. The same five as the
    /// Vulkan side, because it is the same material and the same shader.
    /// </remarks>
    public const uint TexturesPerMaterial = 5;

    /// <summary>How many materials the heap holds.</summary>
    /// <remarks>
    /// A room is a few hundred batches and a face repainting adds a few hundred more over a
    /// conversation. Direct3D allows a million descriptors in a shader-visible view heap, so
    /// this is generous on purpose: running out is a thrown exception rather than a stall,
    /// and there is nothing to be saved by being tight.
    /// </remarks>
    private const uint MaterialCapacity = 4096;

    private readonly D3D12Context _context;
    private readonly D3D12DescriptorHeap _views;
    private readonly D3D12DescriptorHeap _samplers;
    private readonly D3D12Samplers _shared;
    private bool _disposed;

    private D3D12GeometryDevice(
        D3D12Context context,
        D3D12DescriptorHeap views,
        D3D12DescriptorHeap samplers,
        D3D12Samplers shared)
    {
        _context = context;
        _views = views;
        _samplers = samplers;
        _shared = shared;
    }

    /// <summary>The Direct3D device underneath.</summary>
    internal D3D12Context Context => _context;

    /// <summary>The heap the materials are handed out of, which a draw binds.</summary>
    internal D3D12DescriptorHeap Views => _views;

    /// <summary>The sampler heap, which a draw binds beside it.</summary>
    internal D3D12DescriptorHeap Samplers => _samplers;

    /// <summary>Where the one shared run of samplers starts, for a draw to bind.</summary>
    internal GpuDescriptorHandle SamplerTable => _samplers.Gpu(0);

    /// <summary>How many view descriptors the materials have taken.</summary>
    public uint ViewDescriptorsUsed => _views.Used;

    /// <summary>
    /// How many sampler descriptors exist, which is five however many materials there are.
    /// </summary>
    public uint SamplerDescriptorsUsed => _samplers.Used;

    /// <inheritdoc/>
    public bool SupportsRayTracing => _context.SupportsRayTracing;

    /// <inheritdoc/>
    /// <remarks>
    /// Always. BC1 through BC7 are required of every Direct3D 12 device, so unlike the Vulkan
    /// path there is no case here for expanding the blocks on the host — that exists on the
    /// other backend only for Apple silicon, which has no Direct3D.
    /// </remarks>
    public bool BlockCompression => true;

    /// <summary>Creates a device.</summary>
    /// <param name="context">The Direct3D device.</param>
    /// <returns>The geometry device.</returns>
    /// <exception cref="D3D12Exception">Something on the device refused.</exception>
    public static D3D12GeometryDevice Create(D3D12Context context)
    {
        ArgumentNullException.ThrowIfNull(context);

        D3D12DescriptorHeap? views = null;
        D3D12DescriptorHeap? samplers = null;
        D3D12Samplers? shared = null;

        try
        {
            views = D3D12DescriptorHeap.Create(
                context.Device,
                DescriptorHeapType.CbvSrvUav,
                MaterialCapacity * TexturesPerMaterial,
                shaderVisible: true);

            samplers = D3D12DescriptorHeap.Create(
                context.Device, DescriptorHeapType.Sampler, TexturesPerMaterial, shaderVisible: true);

            shared = D3D12Samplers.Create(context);

            var device = new D3D12GeometryDevice(context, views, samplers, shared);
            device.WriteSamplers();
            return device;
        }
        catch
        {
            views?.Dispose();
            samplers?.Dispose();
            shared?.Dispose();
            throw;
        }
    }

    /// <inheritdoc/>
    public IGeometryUploads BeginUploads() => new D3D12GeometryUploads(D3D12Uploads.Begin(_context));

    /// <inheritdoc/>
    public IGeometryBuffer CreateBuffer<T>(
        ReadOnlySpan<T> data, GeometryBufferKind kind, IGeometryUploads? into = null)
        where T : unmanaged
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Vertex and index buffers alike go to the read state a draw wants. Direct3D has one
        // state for both, which is the whole of the difference from Vulkan's usage flags.
        ResourceStates state = kind == GeometryBufferKind.Vertices
            ? ResourceStates.VertexAndConstantBuffer
            : ResourceStates.IndexBuffer;

        return new D3D12GeometryBuffer(
            D3D12Buffer.CreateDeviceLocal(
                _context, data, state, (into as D3D12GeometryUploads)?.Uploads),
            kind);
    }

    /// <inheritdoc/>
    public IGeometryBuffer CreateDynamicVertices(ulong bytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        return new D3D12GeometryBuffer(
            D3D12Buffer.CreateHostVisible(_context, bytes), GeometryBufferKind.Vertices);
    }

    /// <inheritdoc/>
    public IGeometryTexture CreateTexture(
        DecodedImage image,
        GeometryTextureKind kind = GeometryTextureKind.Colour,
        bool mipmaps = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        D3D12Texture texture = D3D12TextureUpload.Create(
            _context,
            image,
            mipmaps && kind != GeometryTextureKind.Atlas,
            linear: kind == GeometryTextureKind.Data);

        return new D3D12GeometryTexture(_context, texture, (long)image.Width * image.Height * 4);
    }

    /// <inheritdoc/>
    public IGeometryTexture CreateTexture(CompressedImage image)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        return new D3D12GeometryTexture(
            _context, D3D12TextureUpload.Create(_context, image), image.Blocks.Length);
    }

    /// <inheritdoc/>
    public IGeometryMaterial CreateMaterial(
        IGeometryTexture diffuse,
        IGeometryTexture lightmap,
        IGeometryTexture normal,
        IGeometryTexture orm,
        IGeometryTexture height)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        D3D12Texture[] images = [Of(diffuse), Of(lightmap), Of(normal), Of(orm), Of(height)];

        uint first = _views.Allocate(TexturesPerMaterial);

        for (uint i = 0; i < TexturesPerMaterial; i++)
        {
            images[i].Describe(_context, _views.Cpu(first + i));
        }

        // No samplers here. There is one run of them for the whole device; see WriteSamplers,
        // and SamplerTable, which is what a draw binds beside this.
        return new D3D12GeometryMaterial(first);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Nothing to do. Vulkan sizes a descriptor pool in advance and pays for getting it
    /// wrong; here the heap was made once, at a size no room reaches, and a material is the
    /// next five slots in it.
    /// </remarks>
    public void Reserve(int materials) => _ = materials;

    /// <inheritdoc/>
    public IGeometryAccelerationStructure? BuildAccelerationStructure(
        IReadOnlyList<TraceableMesh> meshes)
    {
        ArgumentNullException.ThrowIfNull(meshes);

        if (!SupportsRayTracing || meshes.Count == 0)
        {
            return null;
        }

        // One part may be several meshes, and a part is what moves; the structure places a
        // whole part by one transform, so the meshes of a part have to arrive together.
        var parts = new List<TraceablePart>();

        foreach (IGrouping<int, TraceableMesh> group in meshes.GroupBy(m => m.Part).OrderBy(g => g.Key))
        {
            foreach (TraceableMesh mesh in group)
            {
                parts.Add(new TraceablePart(mesh.Positions, mesh.Indices, Matrix4x4.Identity));
            }
        }

        return new D3D12GeometryStructure(D3D12AccelerationStructure.Build(_context, parts));
    }

    /// <inheritdoc/>
    public void Wait() => _context.Wait();

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _shared.Dispose();
        _samplers.Dispose();
        _views.Dispose();
    }

    private static D3D12Texture Of(IGeometryTexture texture) =>
        texture is D3D12GeometryTexture direct
            ? direct.Texture
            : throw new ArgumentException("That texture is not on this device.", nameof(texture));

    /// <summary>Writes the one run of samplers every material binds.</summary>
    /// <remarks>
    /// In the order the shader declares its textures: colour, lightmap, normal,
    /// occlusion-roughness-metalness, height. The lightmap and the height map are read across
    /// a whole surface exactly once and must not wrap; the other three are wall and floor
    /// textures that tile.
    /// </remarks>
    private void WriteSamplers()
    {
        uint first = _samplers.Allocate(TexturesPerMaterial);

        for (uint i = 0; i < TexturesPerMaterial; i++)
        {
            _shared.CopyInto(
                _context,
                i is 1 or 4 ? SamplerAddressing.Clamp : SamplerAddressing.Repeat,
                _samplers.Cpu(first + i));
        }
    }
}
