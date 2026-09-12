using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;

namespace GK3Reborn.Rendering.Direct3D12;

/// <summary>
/// Many staging copies recorded once and submitted once.
/// </summary>
public sealed unsafe class D3D12Uploads : IDisposable
{
    private readonly D3D12Context _context;
    private readonly List<ComPtr<ID3D12Resource>> _staging = [];
    private readonly List<ComPtr<ID3D12Resource>> _mappings = [];
    private readonly List<IDisposable> _held = [];

    private ComPtr<ID3D12Resource> _arena;
    private byte* _at;
    private ulong _size;
    private ulong _used;
    private bool _submitted;
    private bool _disposed;

    private D3D12Uploads(D3D12Context context, ID3D12GraphicsCommandList4* list)
    {
        _context = context;
        List = list;
    }

    /// <summary>The list every copy in this batch is recorded into.</summary>
    public ID3D12GraphicsCommandList4* List { get; }

    /// <summary>How many staging buffers the batch is holding.</summary>
    public int Count => _staging.Count;

    /// <summary>Opens a batch.</summary>
    /// <param name="context">The device.</param>
    /// <returns>The batch.</returns>
    /// <exception cref="InvalidOperationException">A one-shot list is already open.</exception>
    public static D3D12Uploads Begin(D3D12Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new D3D12Uploads(context, context.BeginOneShot());
    }

    /// <summary>How much upload memory the batch takes at a time.</summary>
    private const ulong Block = 32UL * 1024 * 1024;

    /// <summary>What an offset inside a staging buffer has to be a multiple of for a texture copy.</summary>
    private const ulong Placement = 512;

    /// <summary>Takes a range of upload memory the batch owns until it has run.</summary>
    /// <returns>Where the range is mapped, for the host to write into.</returns>
    /// <param name="bytes">How much is wanted.</param>
    /// <param name="buffer">The staging buffer the range is in.</param>
    /// <param name="offset">Where in that buffer it starts.</param>
    /// <remarks>
    /// One buffer handed out in pieces rather than one buffer each. A room is hundreds of
    /// uploads and every separate staging buffer is its own committed resource — an
    /// allocation the driver makes and then frees a moment later, which cost more than the
    /// copies it carried.
    /// </remarks>
    /// <exception cref="D3D12Exception">The buffer could not be made or mapped.</exception>
    public byte* Reserve(ulong bytes, out ID3D12Resource* buffer, out ulong offset)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        ulong wanted = (bytes + Placement - 1) / Placement * Placement;

        if (_arena.Handle is null || _used + wanted > _size)
        {
            // Whatever the last one was is left mapped and kept: copies already recorded
            // read out of it, and the submit is what waits for them.
            ulong size = Math.Max(wanted, Block);
            ComPtr<ID3D12Resource> next = _context.CreateBuffer(size, HeapType.Upload);

            _staging.Add(next);
            _mappings.Add(next);

            void* mapped;
            var nothing = new Silk.NET.Direct3D12.Range { Begin = 0, End = 0 };

            D3D12Exception.ThrowIfFailed(next.Map(0, &nothing, &mapped), "map a staging buffer");

            _arena = next;
            _at = (byte*)mapped;
            _size = size;
            _used = 0;
        }

        buffer = _arena.Handle;
        offset = _used;
        _used += wanted;

        return _at + offset;
    }

    /// <summary>Puts some data in a device-local buffer, through staging.</summary>
    /// <typeparam name="T">Element type.</typeparam>
    /// <param name="destination">Where it is going.</param>
    /// <param name="data">What to put there.</param>
    /// <param name="state">Which state the destination should be left in.</param>
    /// <exception cref="D3D12Exception">The staging buffer could not be filled.</exception>
    public void Fill<T>(ID3D12Resource* destination, ReadOnlySpan<T> data, ResourceStates state)
        where T : unmanaged
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(destination);

        if (data.Length == 0)
        {
            return;
        }

        ulong bytes = (ulong)(data.Length * sizeof(T));
        byte* at = Reserve(bytes, out ID3D12Resource* staging, out ulong offset);

        data.CopyTo(new Span<T>(at, data.Length));

        // No barriers, either side. Direct3D promotes a buffer out of Common to whatever
        // state it is first used in, automatically and on every queue, and decays it back
        // to Common when the list is submitted. So a buffer written by a copy and then read
        // as vertices needs nothing said about either: it is promoted to CopyDest here and
        // to VertexAndConstantBuffer when something draws with it.
        //
        // This was written the other way round first, with a transition from Common
        // afterwards, and the debug layer refused it: by then the copy had already promoted
        // the buffer to CopyDest, so the barrier described a state it was no longer in. The
        // state a caller asks for is therefore taken as documentation of intent rather than
        // as something to record.
        _ = state;
        List->CopyBufferRegion(destination, 0, staging, offset, bytes);
    }

    /// <summary>Gives the batch a staging buffer somebody else filled.</summary>
    /// <param name="staging">The buffer.</param>
    public void Keep(ComPtr<ID3D12Resource> staging)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _staging.Add(staging);
    }

    /// <summary>Holds something the recorded work reads until the batch has run.</summary>
    /// <param name="held">The descriptor heaps a mip build binds, and anything else of the kind.</param>
    public void Keep(IDisposable held)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(held);

        _held.Add(held);
    }

    /// <summary>Submits every copy and waits for them.</summary>
    /// <exception cref="D3D12Exception">The batch could not be submitted.</exception>
    public void Submit()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        SubmitCore();
    }

    /// <summary>Submits without minding whether this batch is being disposed.</summary>
    private void SubmitCore()
    {
        if (_submitted)
        {
            return;
        }

        _submitted = true;

        foreach (ComPtr<ID3D12Resource> mapped in _mappings)
        {
            mapped.Unmap(0, (Silk.NET.Direct3D12.Range*)null);
        }

        _mappings.Clear();
        _arena = default;
        _at = null;
        _size = 0;
        _used = 0;

        _context.EndOneShot();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Submitting on disposal rather than leaving the list open. A batch that was
        // abandoned has still recorded copies into the context's one-shot list, and
        // leaving that open would fail the next thing that asked for it with an error
        // about a list already being open — a long way from the batch nobody submitted.
        SubmitCore();

        foreach (ComPtr<ID3D12Resource> buffer in _staging)
        {
            buffer.Dispose();
        }

        _staging.Clear();

        // After the submit, because the dispatches recorded into the list read through
        // these and the submit is what waits for them.
        foreach (IDisposable held in _held)
        {
            held.Dispose();
        }

        _held.Clear();
    }
}
