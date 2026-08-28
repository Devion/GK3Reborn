// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace GK3Reborn.Rendering.Vulkan;

/// <summary>
/// Many staging copies recorded once and submitted once.
/// </summary>
/// <remarks>
/// <para>
/// <b>A one-shot submission waits for the whole queue to drain.</b> That is what
/// <see cref="VulkanContext.EndOneShot"/> does, and it is the right shape for the one
/// upload that happens on its own; a room is not that. RC4 is 358 batches, each with a
/// vertex buffer and an index buffer, so building it was some seven hundred submissions
/// and seven hundred full queue stalls — around 300 ms of a door, and none of it work.
/// </para>
/// <para>
/// Recorded into one command buffer and submitted once, the same copies cost one stall.
/// The device does the same amount of copying; what goes away is asking it seven hundred
/// times whether it has finished.
/// </para>
/// <para>
/// <b>The staging buffers belong to the batch.</b> A copy that has been recorded has not
/// run, so freeing its source when the call returns — which is what an unbatched upload
/// may do, having waited — would be freeing memory the device is about to read. They are
/// kept and freed together after the submission.
/// </para>
/// <para>
/// <b>What may go in one: copies into buffers nothing reads until the batch is done.</b>
/// There are no barriers between the copies, which is sound because they write to
/// different buffers and nothing in the batch reads any of them. Anything that *does* read
/// one — an acceleration structure built over a mesh, a texture whose layout has to change
/// around its copy — must stay on its own submission or carry its own barriers. That is
/// why this is asked for explicitly rather than being something the context does to every
/// one-shot behind the caller's back.
/// </para>
/// </remarks>
public sealed unsafe class BufferUploads : IDisposable
{
    private readonly VulkanContext _context;
    private readonly List<(Buffer Buffer, DeviceMemory Memory)> _staging = [];
    private bool _submitted;

    /// <summary>Opens a batch.</summary>
    /// <param name="context">Device context.</param>
    public BufferUploads(VulkanContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        _context = context;
        Commands = context.BeginOneShot();
    }

    /// <summary>The command buffer the copies are recorded into.</summary>
    public CommandBuffer Commands { get; }

    /// <summary>How many copies have been recorded.</summary>
    public int Count => _staging.Count;

    /// <summary>Takes ownership of a staging buffer until the batch has run.</summary>
    /// <param name="buffer">The staging buffer.</param>
    /// <param name="memory">Its memory.</param>
    public void Keep(Buffer buffer, DeviceMemory memory) => _staging.Add((buffer, memory));

    /// <summary>Submits everything recorded, waits for it, and frees the staging.</summary>
    /// <remarks>
    /// Idempotent, and <see cref="Dispose"/> calls it: a batch left open by an exception
    /// still has a command buffer allocated and staging memory held, and neither should
    /// outlive the block that opened it.
    /// </remarks>
    public void Submit()
    {
        if (_submitted)
        {
            return;
        }

        _submitted = true;
        _context.EndOneShot(Commands);

        foreach ((Buffer buffer, DeviceMemory memory) in _staging)
        {
            _context.Api.DestroyBuffer(_context.Device, buffer, null);
            _context.Api.FreeMemory(_context.Device, memory, null);
        }

        _staging.Clear();
    }

    /// <inheritdoc/>
    public void Dispose() => Submit();
}
