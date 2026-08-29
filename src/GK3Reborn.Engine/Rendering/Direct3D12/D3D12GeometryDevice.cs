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
    private readonly bool _owned;
    private bool _disposed;

    internal D3D12GeometryTexture(D3D12Texture texture, long bytes, bool owned)
    {
        Texture = texture;
        Bytes = bytes;
        _owned = owned;
    }

    /// <summary>The texture underneath.</summary>
    internal D3D12Texture Texture { get; }

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

/// <summary>
/// A run of descriptors a batch draws with, as a scene refers to one.
/// </summary>
/// <remarks>
/// Where Vulkan has a descriptor set object, Direct3D has a place in a heap. So a material
/// here is a number: the index of the first of five contiguous descriptors, which the draw
/// turns into a GPU handle and binds as a table. Keeping the index rather than the handle
/// is deliberate — a heap that is reset and refilled between rooms gives out the same
/// indices and different handles.
/// </remarks>
internal sealed class D3D12GeometryMaterial : IGeometryMaterial
{
    internal D3D12GeometryMaterial(uint first) => First = first;

    /// <summary>Where the material's five descriptors start.</summary>
    internal uint First { get; }
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
/// has no such object: there is one shader-visible heap, a material is five contiguous
/// slots in it, and what a draw binds is the address of the first. So the heap is made once
/// and sized for the room, materials are handed out of it in order, and the whole thing is
/// reset when the room is unloaded — which is exactly the lifetime a bump allocator suits
/// and the reason <see cref="D3D12DescriptorHeap"/> is one.
/// </para>
/// <para>
/// <b>The samplers are one run, shared by every material.</b> They cannot be per material:
/// Direct3D keeps samplers in a heap of their own and a shader-visible one holds two
/// thousand and forty-eight descriptors, so five apiece would run out at four hundred and
/// nine batches — which a room reaches. They need not be, either. Which sampler each of the
/// five textures wants is a property of what the texture <em>is</em> — a lightmap and a
/// height map are read once across a surface and must not wrap; a wall and a floor tile —
/// and that is the same for every material in the game. So there is exactly one run of
/// five, and every batch binds it.
/// </para>
/// </remarks>
public sealed unsafe class D3D12GeometryDevice : IGeometryDevice
{
    /// <summary>How many textures one material binds.</summary>
    /// <remarks>
    /// Colour, lightmap, normal, occlusion-roughness-metalness, height. The same five as
    /// the Vulkan side, because it is the same material and the same shader.
    /// </remarks>
    public const uint TexturesPerMaterial = 5;

    /// <summary>How many materials the heap holds before it is grown.</summary>
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
    private readonly Dictionary<string, D3D12Texture> _textures = new(StringComparer.OrdinalIgnoreCase);
    private readonly D3D12Texture _white;
    private readonly D3D12Texture _flat;
    private readonly D3D12Texture _neutral;
    private readonly D3D12Texture _level;
    private bool _disposed;

    private D3D12GeometryDevice(
        D3D12Context context,
        D3D12DescriptorHeap views,
        D3D12DescriptorHeap samplers,
        D3D12Samplers shared,
        D3D12Texture white,
        D3D12Texture flat,
        D3D12Texture neutral,
        D3D12Texture level)
    {
        _context = context;
        _views = views;
        _samplers = samplers;
        _shared = shared;
        _white = white;
        _flat = flat;
        _neutral = neutral;
        _level = level;
    }

    /// <summary>The Direct3D device underneath.</summary>
    internal D3D12Context Context => _context;

    /// <summary>The heap the materials are handed out of, which a draw binds.</summary>
    internal D3D12DescriptorHeap Views => _views;

    /// <summary>The sampler heap, which a draw binds beside it.</summary>
    internal D3D12DescriptorHeap Samplers => _samplers;

    /// <summary>How many view descriptors the materials have taken.</summary>
    /// <remarks>
    /// Five a material, and always equal to <see cref="SamplerDescriptorsUsed"/>. The two
    /// heaps are allocated in step because one index identifies a material in both, and
    /// they would drift the moment anything allocated from one and not the other — which
    /// would not fail, and would look like a tiling texture that had stopped tiling.
    /// </remarks>
    public uint ViewDescriptorsUsed => _views.Used;

    /// <summary>How many sampler descriptors exist, which is five however many materials there are.</summary>
    public uint SamplerDescriptorsUsed => _samplers.Used;

    /// <summary>Where the one shared run of samplers starts, for a draw to bind.</summary>
    internal GpuDescriptorHandle SamplerTable => _samplers.Gpu(0);

    /// <inheritdoc/>
    public bool SupportsRayTracing => _context.SupportsRayTracing;

    /// <inheritdoc/>
    public bool BlockCompression => true;

    /// <inheritdoc/>
    public int TextureCount => _textures.Count;

    /// <inheritdoc/>
    public int TexturesReused { get; private set; }

    /// <inheritdoc/>
    public long TextureBytes { get; private set; }

    /// <inheritdoc/>
    public IGeometryTexture White => new D3D12GeometryTexture(_white, 0, owned: false);

    /// <inheritdoc/>
    public IGeometryTexture Flat => new D3D12GeometryTexture(_flat, 0, owned: false);

    /// <inheritdoc/>
    public IGeometryTexture Neutral => new D3D12GeometryTexture(_neutral, 0, owned: false);

    /// <inheritdoc/>
    public IGeometryTexture Level => new D3D12GeometryTexture(_level, 0, owned: false);

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

            using D3D12Uploads batch = D3D12Uploads.Begin(context);

            // The stand-ins, in the same shades the Vulkan path uses. White for a lightmap
            // a batch has none of; a flat normal that says every surface faces the way it
            // already does; a neutral occlusion, roughness and metalness; a level height
            // that displaces nothing.
            D3D12Texture white = Solid(context, batch, 255, 255, 255);
            D3D12Texture flat = Solid(context, batch, 128, 128, 255, linear: true);
            D3D12Texture neutral = Solid(context, batch, 255, 255, 0, linear: true);
            D3D12Texture level = Solid(context, batch, 0, 0, 0, linear: true);

            batch.Submit();

            var device = new D3D12GeometryDevice(
                context, views, samplers, shared, white, flat, neutral, level);

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

        // Vertex and index buffers alike go to the read state a draw wants. Direct3D has
        // one state for both, which is the whole of the difference from Vulkan's usage
        // flags here.
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
    public bool HasTexture(string name) => _textures.ContainsKey(name);

    /// <inheritdoc/>
    public void AddTexture(
        string name, DecodedImage image, GeometryTextureKind kind = GeometryTextureKind.Colour)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(name);

        if (_textures.ContainsKey(name))
        {
            TexturesReused++;
            return;
        }

        D3D12Texture texture = D3D12TextureUpload.Create(
            _context,
            image,
            mipmaps: kind != GeometryTextureKind.Atlas,
            linear: kind == GeometryTextureKind.Data);

        Remember(name, texture, (long)image.Width * image.Height * 4);
    }

    /// <inheritdoc/>
    public void AddTexture(
        string name, CompressedImage image, GeometryTextureKind kind = GeometryTextureKind.Colour)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(name);

        if (_textures.ContainsKey(name))
        {
            TexturesReused++;
            return;
        }

        D3D12Texture texture = D3D12TextureUpload.Create(_context, image);
        Remember(name, texture, image.Blocks.Length);
    }

    /// <inheritdoc/>
    public IGeometryTexture Texture(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return _textures.TryGetValue(name, out D3D12Texture? found)
            ? new D3D12GeometryTexture(found, 0, owned: false)
            : new D3D12GeometryTexture(_white, 0, owned: false);
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

        // No samplers here. There is one run of them for the whole device; see
        // WriteSamplers, and SamplerTable, which is what a draw binds beside this.
        return new D3D12GeometryMaterial(first);
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

        foreach (D3D12Texture texture in _textures.Values)
        {
            texture.Dispose();
        }

        _textures.Clear();

        _white.Dispose();
        _flat.Dispose();
        _neutral.Dispose();
        _level.Dispose();

        _shared.Dispose();
        _samplers.Dispose();
        _views.Dispose();
    }

    private static D3D12Texture Of(IGeometryTexture texture) =>
        texture is D3D12GeometryTexture direct
            ? direct.Texture
            : throw new ArgumentException("That texture is not on this device.", nameof(texture));

    private static D3D12Texture Solid(
        D3D12Context context, D3D12Uploads batch, byte r, byte g, byte b, bool linear = false)
    {
        const int Size = 4;
        byte[] pixels = new byte[Size * Size * 4];

        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = r;
            pixels[i + 1] = g;
            pixels[i + 2] = b;
            pixels[i + 3] = 255;
        }

        var image = new DecodedImage(Size, Size, pixels, HasAlpha: false, "solid");

        return D3D12TextureUpload.Create(context, image, mipmaps: false, linear, batch);
    }

    /// <summary>Writes the one run of samplers every material binds.</summary>
    /// <remarks>
    /// In the order the shader declares its textures: colour, lightmap, normal,
    /// occlusion-roughness-metalness, height. The lightmap and the height map are read
    /// across a whole surface exactly once and must not wrap; the other three are wall and
    /// floor textures that tile.
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

    private void Remember(string name, D3D12Texture texture, long bytes)
    {
        _textures[name] = texture;
        TextureBytes += bytes;
    }
}
