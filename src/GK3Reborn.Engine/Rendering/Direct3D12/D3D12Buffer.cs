using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;

namespace GK3Reborn.Rendering.Direct3D12;

/// <summary>
/// A buffer on the device, and the views a shader reaches it through.
/// </summary>
/// <remarks>
/// <para>
/// Thinner than its Vulkan counterpart, because Direct3D asks for less. There is no usage
/// mask to declare: a buffer is a buffer, and whether it is a vertex buffer, an index
/// buffer or a structured one is decided by how it is bound rather than by how it was made.
/// The only thing that has to be said in advance is whether a shader may write to it,
/// because unordered access is a resource flag.
/// </para>
/// <para>
/// The one asymmetry worth naming is that a constant buffer's size must be a multiple of
/// two hundred and fifty-six bytes, and a view of one that is not is refused. That is not a
/// hint or an alignment preference; it is a rule, and the padding is added here rather than
/// left to every caller that ever writes a uniform block.
/// </para>
/// </remarks>
public sealed unsafe class D3D12Buffer : IDisposable
{
    /// <summary>What a constant buffer's size must be a multiple of.</summary>
    /// <remarks>
    /// <c>D3D12_CONSTANT_BUFFER_DATA_PLACEMENT_ALIGNMENT</c>. A view of a buffer that is
    /// not a multiple of this is refused outright, which is a good way to be told and a
    /// surprising one the first time a sixty-four-byte matrix will not bind.
    /// </remarks>
    public const ulong ConstantAlignment = 256;

    private ComPtr<ID3D12Resource> _resource;
    private void* _mapped;
    private bool _disposed;

    private D3D12Buffer(ComPtr<ID3D12Resource> resource, ulong bytes, HeapType heap, ResourceStates state)
    {
        _resource = resource;
        Bytes = bytes;
        Heap = heap;
        State = state;
    }

    /// <summary>How large it is.</summary>
    public ulong Bytes { get; }

    /// <summary>Which memory it lives in.</summary>
    public HeapType Heap { get; }

    /// <summary>Which state it is in.</summary>
    public ResourceStates State { get; private set; }

    /// <summary>The resource itself.</summary>
    public ID3D12Resource* Handle => _resource.Handle;

    /// <summary>Where it is, for a root descriptor or an acceleration structure input.</summary>
    public ulong Address => _resource.Handle is null ? 0 : _resource.GetGPUVirtualAddress();

    /// <summary>Makes an empty buffer in device memory.</summary>
    /// <param name="context">The device.</param>
    /// <param name="bytes">How large.</param>
    /// <param name="writable">Whether a shader may write to it.</param>
    /// <returns>The buffer.</returns>
    /// <exception cref="D3D12Exception">It could not be created.</exception>
    public static D3D12Buffer CreateEmpty(D3D12Context context, ulong bytes, bool writable = false)
    {
        ArgumentNullException.ThrowIfNull(context);

        ComPtr<ID3D12Resource> resource = context.CreateBuffer(
            bytes, HeapType.Default, ResourceStates.Common, writable);

        return new D3D12Buffer(resource, bytes, HeapType.Default, ResourceStates.Common);
    }

    /// <summary>Makes a buffer in device memory holding a copy of some data.</summary>
    /// <typeparam name="T">Element type.</typeparam>
    /// <param name="context">The device.</param>
    /// <param name="data">What to put in it.</param>
    /// <param name="state">Which state it should end up in.</param>
    /// <param name="into">
    /// An open batch to record the copy into, or null to submit it on its own and wait.
    /// </param>
    /// <param name="writable">Whether a shader may write to it.</param>
    /// <returns>The buffer, whose contents are there once the batch has been submitted.</returns>
    /// <remarks>
    /// <b>Submitting on its own waits for the whole queue, and a room is hundreds of
    /// buffers.</b> See <see cref="D3D12Uploads"/>: batched, the copies are one submission
    /// instead of seven hundred.
    /// </remarks>
    public static D3D12Buffer CreateDeviceLocal<T>(
        D3D12Context context,
        ReadOnlySpan<T> data,
        ResourceStates state,
        D3D12Uploads? into = null,
        bool writable = false)
        where T : unmanaged
    {
        ArgumentNullException.ThrowIfNull(context);

        ulong bytes = (ulong)(data.Length * sizeof(T));
        D3D12Buffer buffer = CreateEmpty(context, bytes, writable);

        if (data.Length == 0)
        {
            return buffer;
        }

        if (into is not null)
        {
            into.Fill(buffer.Handle, data, state);
            buffer.State = state;
            return buffer;
        }

        using var batch = D3D12Uploads.Begin(context);
        batch.Fill(buffer.Handle, data, state);
        batch.Submit();

        buffer.State = state;
        return buffer;
    }

    /// <summary>Makes a buffer the host can write to directly, and keeps it mapped.</summary>
    /// <param name="context">The device.</param>
    /// <param name="bytes">How large.</param>
    /// <param name="forConstants">Whether the size should be rounded up for a constant buffer view.</param>
    /// <returns>The buffer.</returns>
    /// <exception cref="D3D12Exception">It could not be created or mapped.</exception>
    /// <remarks>
    /// Mapped once and left mapped, which Direct3D permits and Vulkan calls persistent
    /// mapping. What the per-frame uniforms are written through: mapping and unmapping
    /// around every write would be two calls a frame per buffer to no purpose, since
    /// nothing here ever needs the pointer to go away.
    /// </remarks>
    public static D3D12Buffer CreateHostVisible(
        D3D12Context context, ulong bytes, bool forConstants = false)
    {
        ArgumentNullException.ThrowIfNull(context);

        ulong size = forConstants ? Align(bytes) : bytes;

        ComPtr<ID3D12Resource> resource = context.CreateBuffer(size, HeapType.Upload);
        var buffer = new D3D12Buffer(resource, size, HeapType.Upload, ResourceStates.GenericRead);

        void* mapped;

        // An empty read range: nothing on the host reads this, and saying so lets a
        // discrete card skip making the contents readable.
        var nothing = new Silk.NET.Direct3D12.Range { Begin = 0, End = 0 };

        D3D12Exception.ThrowIfFailed(
            resource.Map(0, &nothing, &mapped), "map a host-visible buffer");

        buffer._mapped = mapped;
        return buffer;
    }

    /// <summary>Rounds a size up to what a constant buffer view will accept.</summary>
    /// <param name="bytes">The size.</param>
    /// <returns>The rounded size.</returns>
    public static ulong Align(ulong bytes) =>
        (bytes + ConstantAlignment - 1) & ~(ConstantAlignment - 1);

    /// <summary>Writes into a host-visible buffer.</summary>
    /// <typeparam name="T">Element type.</typeparam>
    /// <param name="data">What to write.</param>
    /// <param name="offset">How far in to start, in bytes.</param>
    /// <exception cref="InvalidOperationException">The buffer is not host-visible.</exception>
    public void Write<T>(ReadOnlySpan<T> data, ulong offset = 0)
        where T : unmanaged
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_mapped is null)
        {
            throw new InvalidOperationException(
                "This buffer is in device memory; write to it through an upload batch.");
        }

        ulong bytes = (ulong)(data.Length * sizeof(T));
        if (offset + bytes > Bytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(data), $"{bytes} bytes at {offset} does not fit in {Bytes}.");
        }

        data.CopyTo(new Span<T>((byte*)_mapped + offset, data.Length));
    }

    /// <summary>Moves the buffer into a state, if it is not in it already.</summary>
    /// <param name="list">The list to record into.</param>
    /// <param name="to">The state it should be in.</param>
    public void Transition(ID3D12GraphicsCommandList4* list, ResourceStates to)
    {
        D3D12Context.Transition(list, _resource.Handle, State, to);
        State = to;
    }

    /// <summary>Writes a constant buffer view of this buffer into a descriptor slot.</summary>
    /// <param name="context">The device.</param>
    /// <param name="where">Where to write it.</param>
    /// <param name="offset">How far into the buffer the block starts.</param>
    /// <param name="bytes">How large the block is, or zero for the whole buffer.</param>
    public void DescribeConstants(
        D3D12Context context, CpuDescriptorHandle where, ulong offset = 0, ulong bytes = 0)
    {
        ArgumentNullException.ThrowIfNull(context);

        var description = new ConstantBufferViewDesc
        {
            BufferLocation = Address + offset,
            SizeInBytes = (uint)Align(bytes == 0 ? Bytes - offset : bytes),
        };

        context.Device->CreateConstantBufferView(&description, where);
    }

    /// <summary>Writes a raw shader resource view of this buffer into a descriptor slot.</summary>
    /// <param name="context">The device.</param>
    /// <param name="where">Where to write it.</param>
    /// <remarks>
    /// Raw rather than structured, because SPIRV-Cross turns a read-only GLSL storage
    /// buffer into a <c>ByteAddressBuffer</c> and that is what a raw view binds. A
    /// structured view would need an element stride the generated HLSL never declares.
    /// </remarks>
    public void DescribeRead(D3D12Context context, CpuDescriptorHandle where)
    {
        ArgumentNullException.ThrowIfNull(context);

        var description = new ShaderResourceViewDesc
        {
            Format = Format.FormatR32Typeless,
            ViewDimension = SrvDimension.Buffer,
            Shader4ComponentMapping = D3D12AccelerationStructure.DefaultComponentMapping,
        };

        description.Anonymous.Buffer = new BufferSrv
        {
            FirstElement = 0,
            NumElements = (uint)(Bytes / 4),
            StructureByteStride = 0,
            Flags = BufferSrvFlags.Raw,
        };

        context.Device->CreateShaderResourceView(_resource.Handle, &description, where);
    }

    /// <summary>Writes a raw unordered access view of this buffer into a descriptor slot.</summary>
    /// <param name="context">The device.</param>
    /// <param name="where">Where to write it.</param>
    public void DescribeWrite(D3D12Context context, CpuDescriptorHandle where)
    {
        ArgumentNullException.ThrowIfNull(context);

        var description = new UnorderedAccessViewDesc
        {
            Format = Format.FormatR32Typeless,
            ViewDimension = UavDimension.Buffer,
        };

        description.Anonymous.Buffer = new BufferUav
        {
            FirstElement = 0,
            NumElements = (uint)(Bytes / 4),
            StructureByteStride = 0,
            CounterOffsetInBytes = 0,
            Flags = BufferUavFlags.Raw,
        };

        context.Device->CreateUnorderedAccessView(
            _resource.Handle, (ID3D12Resource*)null, &description, where);
    }

    /// <summary>Where this buffer starts, as a vertex buffer.</summary>
    /// <param name="stride">Bytes from one vertex to the next.</param>
    /// <returns>The binding.</returns>
    public VertexBufferView AsVertices(uint stride) => new()
    {
        BufferLocation = Address,
        SizeInBytes = (uint)Bytes,
        StrideInBytes = stride,
    };

    /// <summary>Where this buffer starts, as an index buffer.</summary>
    /// <param name="sixteenBit">Whether the indices are sixteen bits rather than thirty-two.</param>
    /// <returns>The binding.</returns>
    public IndexBufferView AsIndices(bool sixteenBit = false) => new()
    {
        BufferLocation = Address,
        SizeInBytes = (uint)Bytes,
        Format = sixteenBit ? Format.FormatR16Uint : Format.FormatR32Uint,
    };

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_mapped is not null && _resource.Handle is not null)
        {
            // Nothing was written that the device has not already been told about, so the
            // written range is empty.
            var written = new Silk.NET.Direct3D12.Range { Begin = 0, End = 0 };
            _resource.Unmap(0, &written);
            _mapped = null;
        }

        _resource.Dispose();
    }
}
