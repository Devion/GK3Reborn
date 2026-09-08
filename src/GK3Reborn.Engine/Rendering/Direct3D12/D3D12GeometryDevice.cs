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
public sealed unsafe class D3D12GeometryDevice : IGeometryDevice
{
    /// <summary>How many textures one material binds.</summary>
    public const uint TexturesPerMaterial = 5;

    /// <summary>How many materials the heap holds.</summary>
    private const uint MaterialCapacity = 4096;

    private readonly D3D12Context _context;
    private readonly D3D12DescriptorHeap _views;
    private readonly D3D12DescriptorHeap _samplers;
    private readonly D3D12Samplers _shared;

    /// <summary>Where the materials start in the view heap, once one has been made.</summary>
    private uint? _materialsBase;

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

    /// <summary>Where the frame set's own one sampler is, for a draw to bind beside it.</summary>
    internal GpuDescriptorHandle ReflectionSamplerTable => _samplers.Gpu(TexturesPerMaterial);

    /// <summary>Takes a run of slots in the one shader-visible view heap.</summary>
    /// <param name="count">How many, which must be contiguous.</param>
    /// <returns>The index of the first.</returns>
    internal uint AllocateViews(uint count) => _views.Allocate(count);

    /// <summary>Where a view slot is, for the host to write.</summary>
    /// <param name="index">Which slot.</param>
    /// <returns>The handle.</returns>
    internal CpuDescriptorHandle ViewCpu(uint index) => _views.Cpu(index);

    /// <summary>Where a view slot is, for a shader to read.</summary>
    /// <param name="index">Which slot.</param>
    /// <returns>The handle.</returns>
    internal GpuDescriptorHandle ViewGpu(uint index) => _views.Gpu(index);

    /// <summary>How many view descriptors the materials have taken.</summary>
    public uint ViewDescriptorsUsed => _views.Used;

    /// <summary>
    /// How many sampler descriptors exist, which is five however many materials there are.
    /// </summary>
    public uint SamplerDescriptorsUsed => _samplers.Used;

    /// <inheritdoc/>
    public bool SupportsRayTracing => _context.SupportsRayTracing;

    /// <inheritdoc/>
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

            // One more than a material's run: the extra is the frame's own, which is how a
            // mirror reads the reflection drawn for it. See ReflectionSamplerTable.
            samplers = D3D12DescriptorHeap.Create(
                context.Device,
                DescriptorHeapType.Sampler,
                TexturesPerMaterial + 1,
                shaderVisible: true);

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

        // Where this session's materials begin, remembered the first time one is asked for
        // rather than when the device is made: the frame's own descriptors are taken out of
        // this heap too, by D3D12FrameSet, and they are taken after the device exists and
        // are still bound long after any one room has gone.
        _materialsBase ??= _views.Used;

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
    public void Reserve(int materials) => _ = materials;

    /// <inheritdoc/>
    public void ReleaseMaterials()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_materialsBase is uint some)
        {
            _views.Reset(some);
        }
    }

    /// <inheritdoc/>
    public IGeometryAccelerationStructure? BuildAccelerationStructure(
        IReadOnlyList<TraceableMesh> meshes)
    {
        ArgumentNullException.ThrowIfNull(meshes);

        if (!SupportsRayTracing || meshes.Count == 0)
        {
            return null;
        }

        // Grouped by the thing that moves, and everything in a group concatenated into one
        // piece placed by one transform. Not one piece per mesh: a character is a dozen
        // meshes sharing a placement, and giving each its own instance would mean Move —
        // which every caller indexes by part — moving whichever mesh happened to land at
        // that index, so a walking character would leave most of themselves behind.
        var parts = new List<TraceablePart>();
        var shapes = new Dictionary<int, (int Part, int Offset, int Count)>();

        foreach (IGrouping<int, TraceableMesh> group in
            meshes.GroupBy(m => m.Part).OrderBy(g => g.Key))
        {
            List<Vector3> positions = [];
            List<uint> indices = [];

            foreach (TraceableMesh mesh in group)
            {
                var offset = (uint)positions.Count;

                // Where a clip will rewrite this mesh's vertices inside the piece they are
                // now part of. Only meshes something animates carry a key.
                if (mesh.Key >= 0)
                {
                    shapes[mesh.Key] = (parts.Count, (int)offset, mesh.Positions.Length);
                }

                positions.AddRange(mesh.Positions);

                foreach (uint index in mesh.Indices)
                {
                    indices.Add(index + offset);
                }
            }

            if (indices.Count < 3)
            {
                continue;
            }

            parts.Add(new TraceablePart(
                positions.ToArray(), indices.ToArray(), Matrix4x4.Identity, Part: group.Key));
        }

        if (parts.Count == 0)
        {
            return null;
        }

        return new D3D12GeometryStructure(
            D3D12AccelerationStructure.Build(_context, parts, shapes));
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
    private void WriteSamplers()
    {
        uint first = _samplers.Allocate(TexturesPerMaterial + 1);

        for (uint i = 0; i < TexturesPerMaterial; i++)
        {
            _shared.CopyInto(
                _context,
                i is 1 or 4 ? SamplerAddressing.Clamp : SamplerAddressing.Repeat,
                _samplers.Cpu(first + i));
        }

        // And one more, immediately after, for the frame's own table: how a mirror reads the
        // reflection drawn for it. See ReflectionSamplerTable.
        _shared.CopyInto(
            _context, SamplerAddressing.Clamp, _samplers.Cpu(first + TexturesPerMaterial));
    }
}
