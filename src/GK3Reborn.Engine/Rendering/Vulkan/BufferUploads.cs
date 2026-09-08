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
