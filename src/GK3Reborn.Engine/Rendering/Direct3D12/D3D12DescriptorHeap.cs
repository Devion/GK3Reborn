using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;

namespace GK3Reborn.Rendering.Direct3D12;

/// <summary>
/// A descriptor heap, and a bump allocator over it.
/// </summary>
/// <remarks>
/// <para>
/// Vulkan hands out descriptor sets from a pool and the renderer holds the sets. Direct3D
/// has no such object: a descriptor is a slot in a heap, identified by where it is, and a
/// shader reaches a group of them through a table that points at a starting slot. So the
/// thing worth wrapping is not a set but the heap and the arithmetic — a handle is the
/// heap's base plus an index times a size the device chooses and never states in a header.
/// </para>
/// <para>
/// A bump allocator rather than a free list, deliberately. Descriptors here are made when a
/// scene is loaded and released when it is unloaded, all together; nothing frees one
/// descriptor in the middle of a frame. A free list would be machinery for a case that does
/// not arise, and the case that does arise — a room change — is served by resetting the
/// whole heap at once.
/// </para>
/// <para>
/// Shader-visible heaps are the ones a command list binds and are limited to one of each
/// kind at a time, which is why they are large and shared rather than small and numerous.
/// Render target and depth views live in heaps that are never bound at all: they are read
/// by the CPU when a pass begins, so they can be as small as the count of targets.
/// </para>
/// </remarks>
public sealed unsafe class D3D12DescriptorHeap : IDisposable
{
    private readonly uint _stride;
    private ComPtr<ID3D12DescriptorHeap> _heap;
    private CpuDescriptorHandle _cpuBase;
    private GpuDescriptorHandle _gpuBase;
    private uint _used;
    private bool _disposed;

    private D3D12DescriptorHeap(
        ComPtr<ID3D12DescriptorHeap> heap,
        DescriptorHeapType type,
        uint capacity,
        uint stride,
        bool shaderVisible)
    {
        _heap = heap;
        _stride = stride;

        Type = type;
        Capacity = capacity;
        ShaderVisible = shaderVisible;

        _cpuBase = heap.GetCPUDescriptorHandleForHeapStart();

        if (shaderVisible)
        {
            _gpuBase = heap.GetGPUDescriptorHandleForHeapStart();
        }
    }

    /// <summary>Which kind of descriptor this heap holds.</summary>
    public DescriptorHeapType Type { get; }

    /// <summary>How many descriptors it has room for.</summary>
    public uint Capacity { get; }

    /// <summary>How many have been handed out.</summary>
    public uint Used => _used;

    /// <summary>Whether a command list can bind this heap.</summary>
    public bool ShaderVisible { get; }

    /// <summary>The heap itself, for binding.</summary>
    public ID3D12DescriptorHeap* Handle => _heap.Handle;

    /// <summary>Creates a heap.</summary>
    /// <param name="device">The device.</param>
    /// <param name="type">Which kind of descriptor it holds.</param>
    /// <param name="capacity">How many.</param>
    /// <param name="shaderVisible">Whether a command list can bind it.</param>
    /// <returns>The heap.</returns>
    /// <exception cref="D3D12Exception">The heap could not be created.</exception>
    /// <remarks>
    /// Render target, depth and sampler heaps have their own rules about visibility, and
    /// the runtime refuses rather than ignores a request that breaks one: a render target
    /// heap may never be shader-visible. Asking for it anyway is a device removal on some
    /// drivers and a validation error on others, so the request is corrected here.
    /// </remarks>
    public static D3D12DescriptorHeap Create(
        ID3D12Device5* device,
        DescriptorHeapType type,
        uint capacity,
        bool shaderVisible = false)
    {
        bool visible = shaderVisible
            && type is DescriptorHeapType.CbvSrvUav or DescriptorHeapType.Sampler;

        var description = new DescriptorHeapDesc
        {
            Type = type,
            NumDescriptors = capacity,
            Flags = visible
                ? DescriptorHeapFlags.ShaderVisible
                : DescriptorHeapFlags.None,
            NodeMask = 0,
        };

        ComPtr<ID3D12DescriptorHeap> heap = default;
        Guid heapId = ID3D12DescriptorHeap.Guid;

        D3D12Exception.ThrowIfFailed(
            device->CreateDescriptorHeap(&description, &heapId, (void**)heap.GetAddressOf()),
            $"create a {type} descriptor heap of {capacity}");

        // The device decides how big a descriptor is and the number is not in any header:
        // it differs between vendors and between heap types on one vendor. Every handle
        // below is the base plus an index times this.
        uint stride = device->GetDescriptorHandleIncrementSize(type);

        return new D3D12DescriptorHeap(heap, type, capacity, stride, visible);
    }

    /// <summary>Takes the next free run of descriptors.</summary>
    /// <param name="count">How many, which must be contiguous.</param>
    /// <returns>The index of the first.</returns>
    /// <exception cref="D3D12Exception">The heap is full.</exception>
    /// <remarks>
    /// A full heap is a bug rather than a condition, so it throws rather than returning a
    /// failure nobody would check. The capacities are chosen from what a scene actually
    /// needs, and a scene that outgrows one has outgrown an assumption worth revisiting
    /// rather than a limit worth raising in silence.
    /// </remarks>
    public uint Allocate(uint count = 1)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_used + count > Capacity)
        {
            throw new D3D12Exception(
                $"The {Type} descriptor heap is full: {_used} of {Capacity} used, {count} more asked for.");
        }

        uint at = _used;
        _used += count;
        return at;
    }

    /// <summary>Forgets every descriptor handed out.</summary>
    /// <remarks>
    /// What a room change does. Nothing is destroyed — the descriptors are slots, and the
    /// resources they described are freed by their own owners — so this is only the
    /// allocator going back to the start. Calling it while the device is still reading the
    /// heap is the hazard, which is why the renderer waits for idle before it unloads a
    /// scene.
    /// </remarks>
    public void Reset() => _used = 0;

    /// <summary>Where a descriptor is, for the CPU to write.</summary>
    /// <param name="index">Which descriptor.</param>
    /// <returns>Its handle.</returns>
    public CpuDescriptorHandle Cpu(uint index) =>
        new() { Ptr = _cpuBase.Ptr + (nuint)(index * _stride) };

    /// <summary>Where a descriptor is, for a shader to read.</summary>
    /// <param name="index">Which descriptor.</param>
    /// <returns>Its handle.</returns>
    /// <exception cref="InvalidOperationException">The heap is not shader-visible.</exception>
    public GpuDescriptorHandle Gpu(uint index)
    {
        if (!ShaderVisible)
        {
            throw new InvalidOperationException(
                $"The {Type} heap is not shader-visible, so it has no GPU handles.");
        }

        return new GpuDescriptorHandle { Ptr = _gpuBase.Ptr + (index * _stride) };
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _heap.Dispose();
    }
}
