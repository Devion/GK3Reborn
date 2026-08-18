using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace GK3Reborn.Rendering.Vulkan;

/// <summary>
/// A device-local buffer, filled through a staging copy.
/// </summary>
/// <remarks>
/// Vertex and index data goes into memory the GPU reads fastest, which the CPU usually
/// cannot write to directly. The data therefore lands first in a small host-visible
/// staging buffer and is copied across, and the staging buffer is destroyed immediately
/// afterwards — keeping it would double the memory cost of every mesh for no benefit,
/// since this data never changes again.
/// </remarks>
public sealed unsafe class VulkanBuffer : IDisposable
{
    private readonly VulkanContext _context;

    private VulkanBuffer(VulkanContext context, Buffer handle, DeviceMemory memory, ulong size)
    {
        _context = context;
        Handle = handle;
        Memory = memory;
        Size = size;
    }

    /// <summary>The buffer handle.</summary>
    public Buffer Handle { get; }

    /// <summary>The memory backing it.</summary>
    public DeviceMemory Memory { get; }

    /// <summary>Size in bytes.</summary>
    public ulong Size { get; }

    /// <summary>Creates a device-local buffer holding a copy of some data.</summary>
    /// <typeparam name="T">Element type.</typeparam>
    /// <param name="context">Device context.</param>
    /// <param name="data">Data to upload.</param>
    /// <param name="usage">What the buffer will be used for.</param>
    /// <returns>The buffer.</returns>
    public static VulkanBuffer CreateDeviceLocal<T>(
        VulkanContext context, ReadOnlySpan<T> data, BufferUsageFlags usage)
        where T : unmanaged
    {
        ArgumentNullException.ThrowIfNull(context);

        ulong size = (ulong)(data.Length * Marshal.SizeOf<T>());
        if (size == 0)
        {
            throw new VulkanException("Cannot create an empty buffer.");
        }

        (Buffer staging, DeviceMemory stagingMemory) = Create(
            context, size,
            BufferUsageFlags.TransferSrcBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);

        try
        {
            void* mapped;
            context.Api.MapMemory(context.Device, stagingMemory, 0, size, 0, &mapped);
            data.CopyTo(new Span<T>(mapped, data.Length));
            context.Api.UnmapMemory(context.Device, stagingMemory);

            (Buffer device, DeviceMemory deviceMemory) = Create(
                context, size,
                usage | BufferUsageFlags.TransferDstBit,
                MemoryPropertyFlags.DeviceLocalBit);

            CommandBuffer command = context.BeginOneShot();
            var region = new BufferCopy { Size = size };
            context.Api.CmdCopyBuffer(command, staging, device, 1, in region);
            context.EndOneShot(command);

            return new VulkanBuffer(context, device, deviceMemory, size);
        }
        finally
        {
            context.Api.DestroyBuffer(context.Device, staging, null);
            context.Api.FreeMemory(context.Device, stagingMemory, null);
        }
    }

    /// <summary>Creates a host-visible buffer that stays mapped for frequent updates.</summary>
    /// <param name="context">Device context.</param>
    /// <param name="size">Size in bytes.</param>
    /// <param name="usage">What the buffer will be used for.</param>
    /// <returns>The buffer.</returns>
    /// <remarks>
    /// Used for per-draw uniforms, which change every frame and are small enough that the
    /// slower memory costs less than a staging copy would.
    /// </remarks>
    public static VulkanBuffer CreateHostVisible(VulkanContext context, ulong size, BufferUsageFlags usage)
    {
        ArgumentNullException.ThrowIfNull(context);

        (Buffer handle, DeviceMemory memory) = Create(
            context, size, usage,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);

        return new VulkanBuffer(context, handle, memory, size);
    }

    /// <summary>Writes data into a host-visible buffer.</summary>
    /// <typeparam name="T">Element type.</typeparam>
    /// <param name="data">Data to write.</param>
    public void Write<T>(ReadOnlySpan<T> data)
        where T : unmanaged
    {
        ulong size = (ulong)(data.Length * Marshal.SizeOf<T>());
        if (size > Size)
        {
            throw new VulkanException($"Writing {size} bytes into a {Size} byte buffer.");
        }

        void* mapped;
        _context.Api.MapMemory(_context.Device, Memory, 0, size, 0, &mapped);
        data.CopyTo(new Span<T>(mapped, data.Length));
        _context.Api.UnmapMemory(_context.Device, Memory);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _context.Api.DestroyBuffer(_context.Device, Handle, null);
        _context.Api.FreeMemory(_context.Device, Memory, null);
    }

    private static (Buffer Buffer, DeviceMemory Memory) Create(
        VulkanContext context, ulong size, BufferUsageFlags usage, MemoryPropertyFlags properties)
    {
        var createInfo = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = size,
            Usage = usage,
            SharingMode = SharingMode.Exclusive,
        };

        if (context.Api.CreateBuffer(context.Device, in createInfo, null, out Buffer buffer) != Result.Success)
        {
            throw new VulkanException("Could not create a buffer.");
        }

        context.Api.GetBufferMemoryRequirements(context.Device, buffer, out MemoryRequirements requirements);
        DeviceMemory memory = context.Allocate(requirements, properties);
        context.Api.BindBufferMemory(context.Device, buffer, memory, 0);

        return (buffer, memory);
    }
}
