// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using System.Threading;

namespace GK3Reborn.Rendering.Direct3D12;

/// <summary>
/// A ring of command allocators, so the processor can run ahead of the device.
/// </summary>
public sealed unsafe class D3D12FrameRing : IDisposable
{
    /// <summary>How many frames the processor may be ahead by.</summary>
    public const uint Depth = 2;

    private readonly D3D12Context _context;
    private readonly ComPtr<ID3D12CommandAllocator>[] _allocators;
    private readonly ComPtr<ID3D12GraphicsCommandList4>[] _lists;
    private readonly ulong[] _values;

    private ComPtr<ID3D12Fence1> _fence;
    private AutoResetEvent? _event;
    private ulong _value;
    private uint _index;
    private bool _open;
    private bool _disposed;

    private D3D12FrameRing(
        D3D12Context context,
        ComPtr<ID3D12CommandAllocator>[] allocators,
        ComPtr<ID3D12GraphicsCommandList4>[] lists,
        ComPtr<ID3D12Fence1> fence,
        AutoResetEvent signal)
    {
        _context = context;
        _allocators = allocators;
        _lists = lists;
        _fence = fence;
        _event = signal;
        _values = new ulong[allocators.Length];
    }

    /// <summary>Which slot the frame being recorded is using.</summary>
    public uint Index => _index;

    /// <summary>How many frames deep the ring is.</summary>
    public uint Frames => (uint)_allocators.Length;

    /// <summary>Builds the ring.</summary>
    /// <param name="context">The device.</param>
    /// <param name="depth">How many frames the processor may be ahead by.</param>
    /// <returns>The queue.</returns>
    /// <exception cref="D3D12Exception">It could not be built.</exception>
    public static D3D12FrameRing Create(D3D12Context context, uint depth = Depth)
    {
        ArgumentNullException.ThrowIfNull(context);

        depth = Math.Max(1, depth);

        var allocators = new ComPtr<ID3D12CommandAllocator>[depth];
        var lists = new ComPtr<ID3D12GraphicsCommandList4>[depth];
        ComPtr<ID3D12Fence1> fence = default;
        AutoResetEvent? signal = null;

        try
        {
            for (uint i = 0; i < depth; i++)
            {
                Guid allocatorId = ID3D12CommandAllocator.Guid;

                D3D12Exception.ThrowIfFailed(
                    context.Device->CreateCommandAllocator(
                        CommandListType.Direct,
                        &allocatorId,
                        (void**)allocators[i].GetAddressOf()),
                    "create a frame command allocator");

                Guid listId = ID3D12GraphicsCommandList4.Guid;

                D3D12Exception.ThrowIfFailed(
                    context.Device->CreateCommandList(
                        0,
                        CommandListType.Direct,
                        allocators[i],
                        (ID3D12PipelineState*)null,
                        &listId,
                        (void**)lists[i].GetAddressOf()),
                    "create a frame command list");

                // Created open, and every path here begins by resetting it. Closing it now
                // is what makes the first frame look like every other frame.
                D3D12Exception.ThrowIfFailed(lists[i].Close(), "close a frame command list");
            }

            Guid fenceId = ID3D12Fence1.Guid;

            D3D12Exception.ThrowIfFailed(
                context.Device->CreateFence(
                    0, FenceFlags.None, &fenceId, (void**)fence.GetAddressOf()),
                "create the frame fence");

            // Auto-resetting, not manual. A manual one stays signalled after the first wait
            // and every wait after that returns at once, which reads as the ring working and
            // is an allocator being reset while the device is still reading it.
            signal = new AutoResetEvent(false);

            return new D3D12FrameRing(context, allocators, lists, fence, signal);
        }
        catch
        {
            signal?.Dispose();
            fence.Dispose();

            for (uint i = 0; i < depth; i++)
            {
                lists[i].Dispose();
                allocators[i].Dispose();
            }

            throw;
        }
    }

    /// <summary>Waits for this slot to come free and opens its list.</summary>
    /// <returns>The list to record the frame into.</returns>
    /// <exception cref="D3D12Exception">The list could not be reset.</exception>
    public ID3D12GraphicsCommandList4* Begin()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_open)
        {
            throw new InvalidOperationException(
                "A frame is already being recorded; submit it before starting another.");
        }

        // This slot's own frame, not the newest one. Waiting for the newest would make the
        // ring one frame deep however many allocators it has.
        WaitFor(_values[_index]);

        D3D12Exception.ThrowIfFailed(
            _allocators[_index].Reset(), "reset a frame command allocator");

        D3D12Exception.ThrowIfFailed(
            _lists[_index].Reset(_allocators[_index], (ID3D12PipelineState*)null),
            "reset a frame command list");

        _open = true;
        return _lists[_index].Handle;
    }

    /// <summary>Closes the frame's list, submits it, and moves to the next slot.</summary>
    /// <exception cref="D3D12Exception">It could not be submitted.</exception>
    public void Submit()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_open)
        {
            return;
        }

        _open = false;

        D3D12Exception.ThrowIfFailed(_lists[_index].Close(), "close a frame command list");

        ID3D12CommandList* list = (ID3D12CommandList*)_lists[_index].Handle;
        _context.Queue->ExecuteCommandLists(1, &list);

        // Remembered against the slot rather than against the frame number, because the slot
        // is what the next Begin has to wait for.
        _values[_index] = ++_value;

        D3D12Exception.ThrowIfFailed(
            _context.Queue->Signal(_fence, _value), "signal the frame fence");

        _index = (_index + 1) % (uint)_allocators.Length;
    }

    /// <summary>Waits until the device has finished every frame given to it.</summary>
    public void Wait()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        WaitCore();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        WaitCore();

        _event?.Dispose();
        _event = null;
        _fence.Dispose();

        for (int i = 0; i < _allocators.Length; i++)
        {
            _lists[i].Dispose();
            _allocators[i].Dispose();
        }
    }

    /// <summary>Waits for everything, without minding whether this is being disposed.</summary>
    private void WaitCore()
    {
        foreach (ulong value in _values)
        {
            WaitFor(value);
        }
    }

    private void WaitFor(ulong value)
    {
        if (value == 0 || _fence.GetCompletedValue() >= value)
        {
            return;
        }

        D3D12Exception.ThrowIfFailed(
            _fence.SetEventOnCompletion(
                value, (void*)_event!.SafeWaitHandle.DangerousGetHandle()),
            "wait for a frame");

        _event.WaitOne();
    }
}
