// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace GK3Reborn.Rendering.Vulkan;

/// <summary>
/// Staging copies submitted in bounded runs, so loading a room does not retain a
/// second copy of all its geometry until the final submission.
/// </summary>
public sealed unsafe class BufferUploads : IDisposable
{
    private readonly VulkanContext _context;
    private readonly List<(Buffer Buffer, DeviceMemory Memory)> _staging = [];
    private bool _submitted;
    private ulong _bytes;

    internal const ulong MaximumBytes = 32UL * 1024 * 1024;
    internal const int MaximumCopies = 128;

    /// <summary>Opens a batch.</summary>
    /// <param name="context">Device context.</param>
    public BufferUploads(VulkanContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        _context = context;
        Commands = context.BeginOneShot();
    }

    /// <summary>The command buffer the copies are recorded into.</summary>
    public CommandBuffer Commands { get; private set; }

    /// <summary>How many copies have been recorded.</summary>
    public int Count => _staging.Count;

    /// <summary>Takes ownership of a staging buffer until the batch has run.</summary>
    /// <param name="buffer">The staging buffer.</param>
    /// <param name="memory">Its memory.</param>
    /// <param name="bytes">The size retained by the copy.</param>
    public void Keep(Buffer buffer, DeviceMemory memory, ulong bytes)
    {
        ObjectDisposedException.ThrowIf(_submitted, this);
        _staging.Add((buffer, memory));
        _bytes += bytes;
    }

    /// <summary>Drains the previous run before allocating the next staging buffer.</summary>
    public void Prepare(ulong bytes)
    {
        ObjectDisposedException.ThrowIf(_submitted, this);
        if (NeedsFlush(_bytes, Count, bytes))
        {
            Submit();
            Commands = _context.BeginOneShot();
            _submitted = false;
        }
    }

    internal static bool NeedsFlush(ulong held, int count, ulong next) =>
        count > 0 && (count >= MaximumCopies || next >= MaximumBytes || held > MaximumBytes - next);

    /// <summary>Submits everything recorded, waits for it, and frees the staging.</summary>
    public void Submit()
    {
        if (_submitted)
        {
            return;
        }

        _submitted = true;
        bool mayRelease = false;
        try
        {
            _context.EndOneShot(Commands, out mayRelease);
        }
        finally
        {
            // A failed submission never used these buffers. A failed wait may still
            // have work in flight, in which case device teardown owns their cleanup.
            if (mayRelease)
            {
                foreach ((Buffer buffer, DeviceMemory memory) in _staging)
                {
                    _context.Api.DestroyBuffer(_context.Device, buffer, null);
                    _context.Api.FreeMemory(_context.Device, memory, null);
                }
                _staging.Clear();
                _bytes = 0;
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose() => Submit();
}
