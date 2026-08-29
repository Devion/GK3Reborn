using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;

namespace GK3Reborn.Rendering.Direct3D12;

/// <summary>
/// Many staging copies recorded once and submitted once.
/// </summary>
/// <remarks>
/// <para>
/// The Direct3D twin of <c>BufferUploads</c>, and it exists because of a measurement made
/// on the other backend rather than a principle. <see cref="D3D12Context.EndOneShot"/>
/// waits for the queue to drain, which is the right shape for the one upload that happens
/// alone; a room is not that. RC4 is 358 batches with a vertex buffer and an index buffer
/// apiece, so building it unbatched was some seven hundred submissions and seven hundred
/// full stalls — around 300 ms of a door, and none of it work.
/// </para>
/// <para>
/// <b>The staging buffers belong to the batch.</b> A recorded copy has not run, so freeing
/// its source when the call returns would be freeing memory the device is about to read.
/// They are kept and freed together after the submission.
/// </para>
/// <para>
/// <b>What may go in one: copies into resources nothing reads until the batch is done.</b>
/// There are no barriers between the copies, which is sound because they write to different
/// resources and nothing in the batch reads any of them. Anything that <em>does</em> read
/// one — an acceleration structure built over a mesh, a texture whose mips are generated
/// from its own top level — must carry its own barriers or stay on its own submission.
/// That is why a batch is asked for explicitly rather than being something the context
/// does behind the caller's back.
/// </para>
/// </remarks>
public sealed unsafe class D3D12Uploads : IDisposable
{
    private readonly D3D12Context _context;
    private readonly List<ComPtr<ID3D12Resource>> _staging = [];
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
        ComPtr<ID3D12Resource> staging = _context.CreateBuffer(bytes, HeapType.Upload);
        _staging.Add(staging);

        void* mapped;
        var nothing = new Silk.NET.Direct3D12.Range { Begin = 0, End = 0 };

        D3D12Exception.ThrowIfFailed(staging.Map(0, &nothing, &mapped), "map a staging buffer");

        try
        {
            data.CopyTo(new Span<T>(mapped, data.Length));
        }
        finally
        {
            staging.Unmap(0, (Silk.NET.Direct3D12.Range*)null);
        }

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
        List->CopyBufferRegion(destination, 0, staging.Handle, 0, bytes);
    }

    /// <summary>Gives the batch a staging buffer somebody else filled.</summary>
    /// <param name="staging">The buffer.</param>
    /// <remarks>
    /// For the copies this class cannot do itself. A texture is copied out of a buffer
    /// whose rows are padded by rules only the device knows, so the caller lays it out; but
    /// it is still a recorded copy reading memory the caller is about to go out of scope
    /// with, and the lifetime rule is the same one. Handing the buffer over is what keeps
    /// it alive until the submission has been waited for.
    /// </remarks>
    public void Keep(ComPtr<ID3D12Resource> staging)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _staging.Add(staging);
    }

    /// <summary>Submits every copy and waits for them.</summary>
    /// <exception cref="D3D12Exception">The batch could not be submitted.</exception>
    public void Submit()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        SubmitCore();
    }

    /// <summary>Submits without minding whether this batch is being disposed.</summary>
    /// <remarks>
    /// Disposal has to submit — the copies are already recorded into the context, and
    /// leaving that list open would fail the next thing that asked for it — but it has
    /// already said the batch is disposed by the time it gets there, so it cannot use the
    /// public form. The same split as D3D12Context.WaitCore, for the same reason.
    /// </remarks>
    private void SubmitCore()
    {
        if (_submitted)
        {
            return;
        }

        _submitted = true;
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
    }
}
