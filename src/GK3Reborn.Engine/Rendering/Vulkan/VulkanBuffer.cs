using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace GK3Reborn.Rendering.Vulkan;

/// <summary>
/// A device-local buffer, filled through a staging copy.
/// </summary>
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

    /// <summary>This buffer's address on the device.</summary>
    public ulong DeviceAddress
    {
        get
        {
            var info = new BufferDeviceAddressInfo
            {
                SType = StructureType.BufferDeviceAddressInfo,
                Buffer = Handle,
            };

            return _context.Api.GetBufferDeviceAddress(_context.Device, in info);
        }
    }

    /// <summary>Creates a device-local buffer holding a copy of some data.</summary>
    /// <typeparam name="T">Element type.</typeparam>
    /// <param name="context">Device context.</param>
    /// <param name="data">Data to upload.</param>
    /// <param name="usage">What the buffer will be used for.</param>
    /// <returns>The buffer.</returns>
    public static VulkanBuffer CreateDeviceLocal<T>(
        VulkanContext context, ReadOnlySpan<T> data, BufferUsageFlags usage)
        where T : unmanaged =>
        CreateDeviceLocal(context, data, usage, null);

    /// <summary>Creates a device-local buffer and fills it from a staging copy.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="context">Device context.</param>
    /// <param name="data">What to put in it.</param>
    /// <param name="usage">What the buffer will be used for.</param>
    /// <param name="into">
    /// An open batch to record the copy into, or null to submit it on its own and wait.
    /// </param>
    /// <returns>The buffer, whose contents are there once the batch has been submitted.</returns>
    public static VulkanBuffer CreateDeviceLocal<T>(
        VulkanContext context, ReadOnlySpan<T> data, BufferUsageFlags usage, BufferUploads? into)
        where T : unmanaged
    {
        ArgumentNullException.ThrowIfNull(context);

        // Every device-local buffer is addressable where the device supports it. Vertex
        // and index buffers become acceleration structure inputs, and finding out after
        // the fact would mean uploading them twice.
        if (context.SupportsRayTracing)
        {
            usage |= BufferUsageFlags.ShaderDeviceAddressBit;
        }

        ulong size = (ulong)(data.Length * Marshal.SizeOf<T>());
        if (size == 0)
        {
            throw new VulkanException("Cannot create an empty buffer.");
        }

        (Buffer staging, DeviceMemory stagingMemory) = Create(
            context, size,
            BufferUsageFlags.TransferSrcBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);

        bool staged = false;

        try
        {
            void* mapped;
            context.Api.MapMemory(context.Device, stagingMemory, 0, size, 0, &mapped);
            data.CopyTo(new Span<T>(mapped, data.Length));
            context.Api.UnmapMemory(context.Device, stagingMemory);

            (Buffer device, DeviceMemory deviceMemory) = Create(
                context, size,
                usage | BufferUsageFlags.TransferDstBit,
                MemoryPropertyFlags.DeviceLocalBit,
                context.SupportsRayTracing);

            var region = new BufferCopy { Size = size };

            if (into is not null)
            {
                context.Api.CmdCopyBuffer(into.Commands, staging, device, 1, in region);

                // The batch owns the staging buffer now: it may not be freed until the
                // copy has actually run, which is when the batch is submitted.
                into.Keep(staging, stagingMemory);
                staged = true;

                return new VulkanBuffer(context, device, deviceMemory, size);
            }

            CommandBuffer command = context.BeginOneShot();
            context.Api.CmdCopyBuffer(command, staging, device, 1, in region);
            context.EndOneShot(command);

            return new VulkanBuffer(context, device, deviceMemory, size);
        }
        finally
        {
            if (!staged)
            {
                context.Api.DestroyBuffer(context.Device, staging, null);
                context.Api.FreeMemory(context.Device, stagingMemory, null);
            }
        }
    }

    /// <summary>Creates a host-visible buffer that stays mapped for frequent updates.</summary>
    /// <param name="context">Device context.</param>
    /// <param name="size">Size in bytes.</param>
    /// <param name="usage">What the buffer will be used for.</param>
    /// <param name="addressable">Whether its device address will be taken.</param>
    /// <returns>The buffer.</returns>
    public static VulkanBuffer CreateHostVisible(
        VulkanContext context, ulong size, BufferUsageFlags usage, bool addressable = false)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (addressable)
        {
            usage |= BufferUsageFlags.ShaderDeviceAddressBit;
        }

        (Buffer handle, DeviceMemory memory) = Create(
            context, size, usage,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            addressable);

        return new VulkanBuffer(context, handle, memory, size);
    }

    /// <summary>Creates an uninitialised device-local buffer.</summary>
    /// <param name="context">Device context.</param>
    /// <param name="size">Size in bytes.</param>
    /// <param name="usage">What the buffer will be used for.</param>
    /// <param name="addressable">Whether its device address will be taken.</param>
    /// <returns>The buffer.</returns>
    public static VulkanBuffer CreateEmpty(
        VulkanContext context, ulong size, BufferUsageFlags usage, bool addressable = false)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (addressable)
        {
            usage |= BufferUsageFlags.ShaderDeviceAddressBit;
        }

        (Buffer handle, DeviceMemory memory) = Create(
            context, size, usage, MemoryPropertyFlags.DeviceLocalBit, addressable);

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
        VulkanContext context,
        ulong size,
        BufferUsageFlags usage,
        MemoryPropertyFlags properties,
        bool addressable = false)
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
        DeviceMemory memory = context.Allocate(requirements, properties, addressable);
        context.Api.BindBufferMemory(context.Device, buffer, memory, 0);

        return (buffer, memory);
    }
}
