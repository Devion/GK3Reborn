using Silk.NET.Core.Native;
using Silk.NET.Vulkan;

namespace GK3Reborn.Rendering.Vulkan;

/// <summary>
/// Owns the Vulkan device and the operations everything else needs from it.
/// </summary>
/// <remarks>
/// <para>
/// Memory allocation, one-shot command submission and layout transitions are needed by
/// buffers, textures and every render path, and duplicating them is how subtly different
/// versions of the same barrier end up scattered through a renderer.
/// </para>
/// <para>
/// Allocation here is one <c>VkDeviceMemory</c> per resource, which is the wrong shape
/// for a shipping renderer — drivers guarantee only a few thousand allocations, and a
/// scene of GK3's size would approach that. A sub-allocator belongs here later; the
/// interface is deliberately narrow so that change stays local.
/// </para>
/// </remarks>
public sealed unsafe class VulkanContext : IDisposable
{
    private readonly bool _owned;

    private VulkanContext(Vk vk, bool owned = true)
    {
        Api = vk;
        _owned = owned;
    }

    /// <summary>The Vulkan API.</summary>
    public Vk Api { get; }

    /// <summary>The instance.</summary>
    public Instance Instance { get; private set; }

    /// <summary>The physical device in use.</summary>
    public PhysicalDevice PhysicalDevice { get; private set; }

    /// <summary>The logical device.</summary>
    public Device Device { get; private set; }

    /// <summary>The queue used for graphics and transfers.</summary>
    public Queue Queue { get; private set; }

    /// <summary>Index of that queue's family.</summary>
    public uint QueueFamily { get; private set; }

    /// <summary>Command pool for one-shot work.</summary>
    public CommandPool CommandPool { get; private set; }

    /// <summary>Name of the device in use.</summary>
    public string DeviceName { get; private set; } = "unknown";

    /// <summary>Creates a headless context, with no surface and no presentation.</summary>
    /// <returns>The context.</returns>
    public static VulkanContext CreateHeadless()
    {
        var context = new VulkanContext(Vk.GetApi());

        try
        {
            context.CreateInstance();
            context.SelectDevice();
            context.CreateDevice();
            context.CreateCommandPool();
            return context;
        }
        catch
        {
            context.Dispose();
            throw;
        }
    }

    /// <summary>Wraps a device someone else created and owns.</summary>
    /// <param name="api">The Vulkan API in use.</param>
    /// <param name="instance">The instance.</param>
    /// <param name="physicalDevice">The physical device.</param>
    /// <param name="device">The logical device.</param>
    /// <param name="queue">A queue supporting graphics and transfers.</param>
    /// <param name="queueFamily">That queue's family index.</param>
    /// <param name="commandPool">A pool for one-shot work on that queue.</param>
    /// <param name="deviceName">Name of the device, for logs.</param>
    /// <returns>The context.</returns>
    /// <remarks>
    /// The windowed renderer creates its own device because it has to match the surface.
    /// Wrapping it lets textures, buffers and pipelines be built the same way there as in
    /// the headless path, rather than through a second set of helpers that drift.
    /// Disposing an adopted context frees nothing: the caller still owns everything.
    /// </remarks>
    public static VulkanContext Adopt(
        Vk api,
        Instance instance,
        PhysicalDevice physicalDevice,
        Device device,
        Queue queue,
        uint queueFamily,
        CommandPool commandPool,
        string deviceName = "unknown")
    {
        ArgumentNullException.ThrowIfNull(api);

        return new VulkanContext(api, owned: false)
        {
            Instance = instance,
            PhysicalDevice = physicalDevice,
            Device = device,
            Queue = queue,
            QueueFamily = queueFamily,
            CommandPool = commandPool,
            DeviceName = deviceName,
        };
    }

    /// <summary>Allocates memory satisfying a resource's requirements.</summary>
    /// <param name="requirements">What the resource needs.</param>
    /// <param name="flags">Properties the memory must have.</param>
    /// <returns>The allocation.</returns>
    public DeviceMemory Allocate(MemoryRequirements requirements, MemoryPropertyFlags flags)
    {
        Api.GetPhysicalDeviceMemoryProperties(PhysicalDevice, out PhysicalDeviceMemoryProperties properties);

        for (uint i = 0; i < properties.MemoryTypeCount; i++)
        {
            bool allowed = (requirements.MemoryTypeBits & (1u << (int)i)) != 0;
            if (allowed && properties.MemoryTypes[(int)i].PropertyFlags.HasFlag(flags))
            {
                var allocateInfo = new MemoryAllocateInfo
                {
                    SType = StructureType.MemoryAllocateInfo,
                    AllocationSize = requirements.Size,
                    MemoryTypeIndex = i,
                };

                if (Api.AllocateMemory(Device, in allocateInfo, null, out DeviceMemory memory) != Result.Success)
                {
                    throw new VulkanException("Could not allocate device memory.");
                }

                return memory;
            }
        }

        throw new VulkanException($"No memory type satisfies {flags}.");
    }

    /// <summary>Begins a command buffer for one-shot work.</summary>
    /// <returns>The command buffer.</returns>
    public CommandBuffer BeginOneShot()
    {
        var allocateInfo = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = CommandPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = 1,
        };

        Api.AllocateCommandBuffers(Device, in allocateInfo, out CommandBuffer command);

        var begin = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };

        Api.BeginCommandBuffer(command, in begin);
        return command;
    }

    /// <summary>Submits one-shot work and waits for it.</summary>
    /// <param name="command">The command buffer from <see cref="BeginOneShot"/>.</param>
    public void EndOneShot(CommandBuffer command)
    {
        Api.EndCommandBuffer(command);

        var submit = new SubmitInfo
        {
            SType = StructureType.SubmitInfo,
            CommandBufferCount = 1,
            PCommandBuffers = &command,
        };

        Api.QueueSubmit(Queue, 1, in submit, default);
        Api.QueueWaitIdle(Queue);
        Api.FreeCommandBuffers(Device, CommandPool, 1, in command);
    }

    /// <summary>Records an image layout transition.</summary>
    /// <param name="command">Command buffer to record into.</param>
    /// <param name="image">Image to transition.</param>
    /// <param name="from">Current layout.</param>
    /// <param name="to">Desired layout.</param>
    /// <param name="aspect">Which aspect of the image to transition.</param>
    public void Transition(
        CommandBuffer command,
        Image image,
        ImageLayout from,
        ImageLayout to,
        ImageAspectFlags aspect = ImageAspectFlags.ColorBit)
    {
        var barrier = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = from,
            NewLayout = to,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = image,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = aspect,
                LevelCount = 1,
                LayerCount = 1,
            },
            SrcAccessMask = AccessMaskFor(from),
            DstAccessMask = AccessMaskFor(to),
        };

        // Conservative stage flags. Precise ones are a later optimisation; getting them
        // wrong is a source of corruption that only shows under load.
        Api.CmdPipelineBarrier(
            command,
            PipelineStageFlags.AllCommandsBit,
            PipelineStageFlags.AllCommandsBit,
            0, 0, null, 0, null, 1, in barrier);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (!_owned)
        {
            return;
        }

        if (Device.Handle != 0)
        {
            Api.DeviceWaitIdle(Device);

            if (CommandPool.Handle != 0)
            {
                Api.DestroyCommandPool(Device, CommandPool, null);
            }

            Api.DestroyDevice(Device, null);
        }

        if (Instance.Handle != 0)
        {
            Api.DestroyInstance(Instance, null);
        }

        Api.Dispose();
    }

    private static AccessFlags AccessMaskFor(ImageLayout layout) => layout switch
    {
        ImageLayout.Undefined => AccessFlags.None,
        ImageLayout.TransferDstOptimal => AccessFlags.TransferWriteBit,
        ImageLayout.TransferSrcOptimal => AccessFlags.TransferReadBit,
        ImageLayout.ShaderReadOnlyOptimal => AccessFlags.ShaderReadBit,
        ImageLayout.ColorAttachmentOptimal => AccessFlags.ColorAttachmentWriteBit,
        ImageLayout.DepthStencilAttachmentOptimal => AccessFlags.DepthStencilAttachmentWriteBit,
        _ => AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit,
    };

    private void CreateInstance()
    {
        var applicationInfo = new ApplicationInfo
        {
            SType = StructureType.ApplicationInfo,
            PApplicationName = (byte*)SilkMarshal.StringToPtr("GK3Reborn"),
            ApiVersion = Vk.Version13,
        };

        var createInfo = new InstanceCreateInfo
        {
            SType = StructureType.InstanceCreateInfo,
            PApplicationInfo = &applicationInfo,
        };

        try
        {
            if (Api.CreateInstance(in createInfo, null, out Instance instance) != Result.Success)
            {
                throw new VulkanException("Could not create a Vulkan instance.");
            }

            Instance = instance;
        }
        finally
        {
            SilkMarshal.Free((nint)applicationInfo.PApplicationName);
        }
    }

    private void SelectDevice()
    {
        uint count = 0;
        Api.EnumeratePhysicalDevices(Instance, ref count, null);
        if (count == 0)
        {
            throw new VulkanException("No Vulkan devices are present.");
        }

        PhysicalDevice[] devices = new PhysicalDevice[count];
        fixed (PhysicalDevice* pointer = devices)
        {
            Api.EnumeratePhysicalDevices(Instance, ref count, pointer);
        }

        PhysicalDevice? chosen = null;
        uint chosenFamily = 0;
        bool chosenDiscrete = false;

        foreach (PhysicalDevice candidate in devices)
        {
            uint families = 0;
            Api.GetPhysicalDeviceQueueFamilyProperties(candidate, ref families, null);

            QueueFamilyProperties[] properties = new QueueFamilyProperties[families];
            fixed (QueueFamilyProperties* pointer = properties)
            {
                Api.GetPhysicalDeviceQueueFamilyProperties(candidate, ref families, pointer);
            }

            for (uint i = 0; i < families; i++)
            {
                if (!properties[i].QueueFlags.HasFlag(QueueFlags.GraphicsBit))
                {
                    continue;
                }

                Api.GetPhysicalDeviceProperties(candidate, out PhysicalDeviceProperties info);
                bool discrete = info.DeviceType == PhysicalDeviceType.DiscreteGpu;

                if (chosen is null || (discrete && !chosenDiscrete))
                {
                    chosen = candidate;
                    chosenFamily = i;
                    chosenDiscrete = discrete;
                    DeviceName = SilkMarshal.PtrToString((nint)info.DeviceName) ?? "unknown";
                }

                break;
            }
        }

        PhysicalDevice = chosen ?? throw new VulkanException("No device has a graphics queue.");
        QueueFamily = chosenFamily;
    }

    private void CreateDevice()
    {
        float priority = 1f;
        var queueInfo = new DeviceQueueCreateInfo
        {
            SType = StructureType.DeviceQueueCreateInfo,
            QueueFamilyIndex = QueueFamily,
            QueueCount = 1,
            PQueuePriorities = &priority,
        };

        var dynamicRendering = new PhysicalDeviceDynamicRenderingFeatures
        {
            SType = StructureType.PhysicalDeviceDynamicRenderingFeatures,
            DynamicRendering = true,
        };

        // Anisotropic filtering matters for GK3's textures: they are small and viewed at
        // grazing angles across floors and walls, where trilinear alone smears badly.
        var features = new PhysicalDeviceFeatures { SamplerAnisotropy = true };

        var createInfo = new DeviceCreateInfo
        {
            SType = StructureType.DeviceCreateInfo,
            PNext = &dynamicRendering,
            QueueCreateInfoCount = 1,
            PQueueCreateInfos = &queueInfo,
            PEnabledFeatures = &features,
        };

        if (Api.CreateDevice(PhysicalDevice, in createInfo, null, out Device device) != Result.Success)
        {
            throw new VulkanException("Could not create a logical device.");
        }

        Device = device;
        Api.GetDeviceQueue(Device, QueueFamily, 0, out Queue queue);
        Queue = queue;
    }

    private void CreateCommandPool()
    {
        var poolInfo = new CommandPoolCreateInfo
        {
            SType = StructureType.CommandPoolCreateInfo,
            QueueFamilyIndex = QueueFamily,
            Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
        };

        if (Api.CreateCommandPool(Device, in poolInfo, null, out CommandPool pool) != Result.Success)
        {
            throw new VulkanException("Could not create a command pool.");
        }

        CommandPool = pool;
    }
}
