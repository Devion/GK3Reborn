using GK3Reborn.Formats.Bitmaps;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;

namespace GK3Reborn.Rendering.Vulkan;

/// <summary>
/// Renders to an image with no window involved.
/// </summary>
/// <remarks>
/// <para>
/// <c>Plan/04-execution-and-quality.md</c> P5 requires headless offscreen image tests, and
/// this is what makes them possible: no surface, no swapchain, no display. It runs on a
/// build agent, and it produces pixels that can be compared against a reference or simply
/// looked at.
/// </para>
/// <para>
/// That matters more than it sounds. A windowed run proves the code does not crash; only
/// reading the pixels back proves anything was actually drawn. The two failure modes look
/// identical from the outside.
/// </para>
/// </remarks>
public sealed unsafe class OffscreenRenderer : IDisposable
{
    private readonly Vk _vk;
    private Instance _instance;
    private PhysicalDevice _physicalDevice;
    private Device _device;
    private Queue _queue;
    private uint _queueFamily;
    private CommandPool _commandPool;

    private OffscreenRenderer(Vk vk) => _vk = vk;

    /// <summary>Name of the device being used.</summary>
    public string DeviceName { get; private set; } = "unknown";

    /// <summary>Creates a headless renderer.</summary>
    /// <returns>The renderer.</returns>
    /// <exception cref="VulkanException">No usable device exists.</exception>
    public static OffscreenRenderer Create()
    {
        var renderer = new OffscreenRenderer(VulkanContext.LoadApi());

        try
        {
            renderer.CreateInstance();
            renderer.SelectDevice();
            renderer.CreateDevice();
            renderer.CreateCommandPool();
            return renderer;
        }
        catch
        {
            renderer.Dispose();
            throw;
        }
    }

    /// <summary>Renders a triangle and returns the pixels.</summary>
    /// <param name="width">Image width.</param>
    /// <param name="height">Image height.</param>
    /// <param name="clear">Background colour as red, green and blue in 0 to 1.</param>
    /// <returns>The rendered image.</returns>
    public DecodedImage RenderTriangle(int width, int height, (float R, float G, float B) clear)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        // UNORM rather than SRGB: the shader writes linear values and this path reads them
        // straight back, so an sRGB target would apply an encoding the comparison would
        // then have to undo.
        const Format Target = Format.R8G8B8A8Unorm;

        using var compiler = new ShaderCompiler(ShaderCompiler.DefaultCacheDirectory);
        using TrianglePipeline pipeline = TrianglePipeline.Create(_vk, _device, Target, compiler);

        (Image image, DeviceMemory imageMemory) = CreateImage(width, height, Target);
        ImageView view = CreateView(image, Target);
        (Silk.NET.Vulkan.Buffer buffer, DeviceMemory bufferMemory) = CreateReadbackBuffer(width, height);

        try
        {
            CommandBuffer command = BeginCommands();

            Transition(command, image, ImageLayout.Undefined, ImageLayout.ColorAttachmentOptimal);

            var attachment = new RenderingAttachmentInfo
            {
                SType = StructureType.RenderingAttachmentInfo,
                ImageView = view,
                ImageLayout = ImageLayout.ColorAttachmentOptimal,
                LoadOp = AttachmentLoadOp.Clear,
                StoreOp = AttachmentStoreOp.Store,
                ClearValue = new ClearValue(new ClearColorValue(clear.R, clear.G, clear.B, 1f)),
            };

            var extent = new Extent2D { Width = (uint)width, Height = (uint)height };
            var rendering = new RenderingInfo
            {
                SType = StructureType.RenderingInfo,
                RenderArea = new Rect2D { Extent = extent },
                LayerCount = 1,
                ColorAttachmentCount = 1,
                PColorAttachments = &attachment,
            };

            _vk.CmdBeginRendering(command, in rendering);

            var viewport = new Viewport { Width = width, Height = height, MaxDepth = 1f };
            var scissor = new Rect2D { Extent = extent };
            _vk.CmdSetViewport(command, 0, 1, in viewport);
            _vk.CmdSetScissor(command, 0, 1, in scissor);
            _vk.CmdBindPipeline(command, PipelineBindPoint.Graphics, pipeline.Handle);
            _vk.CmdDraw(command, 3, 1, 0, 0);

            _vk.CmdEndRendering(command);

            Transition(command, image, ImageLayout.ColorAttachmentOptimal, ImageLayout.TransferSrcOptimal);

            var region = new BufferImageCopy
            {
                ImageSubresource = new ImageSubresourceLayers
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    LayerCount = 1,
                },
                ImageExtent = new Extent3D((uint)width, (uint)height, 1),
            };

            _vk.CmdCopyImageToBuffer(command, image, ImageLayout.TransferSrcOptimal, buffer, 1, in region);

            EndAndWait(command);

            byte[] pixels = new byte[width * height * 4];
            void* mapped;
            _vk.MapMemory(_device, bufferMemory, 0, (ulong)pixels.Length, 0, &mapped);
            new ReadOnlySpan<byte>(mapped, pixels.Length).CopyTo(pixels);
            _vk.UnmapMemory(_device, bufferMemory);

            return new DecodedImage(width, height, pixels, HasAlpha: false, "vulkan-offscreen");
        }
        finally
        {
            _vk.DestroyBuffer(_device, buffer, null);
            _vk.FreeMemory(_device, bufferMemory, null);
            _vk.DestroyImageView(_device, view, null);
            _vk.DestroyImage(_device, image, null);
            _vk.FreeMemory(_device, imageMemory, null);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_device.Handle != 0)
        {
            _vk.DeviceWaitIdle(_device);

            if (_commandPool.Handle != 0)
            {
                _vk.DestroyCommandPool(_device, _commandPool, null);
            }

            _vk.DestroyDevice(_device, null);
        }

        if (_instance.Handle != 0)
        {
            _vk.DestroyInstance(_instance, null);
        }

        _vk.Dispose();
    }

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
            if (_vk.CreateInstance(in createInfo, null, out _instance) != Result.Success)
            {
                throw new VulkanException("Could not create a Vulkan instance.");
            }
        }
        finally
        {
            SilkMarshal.Free((nint)applicationInfo.PApplicationName);
        }
    }

    private void SelectDevice()
    {
        uint count = 0;
        _vk.EnumeratePhysicalDevices(_instance, ref count, null);
        if (count == 0)
        {
            throw new VulkanException("No Vulkan devices are present.");
        }

        PhysicalDevice[] devices = new PhysicalDevice[count];
        fixed (PhysicalDevice* pointer = devices)
        {
            _vk.EnumeratePhysicalDevices(_instance, ref count, pointer);
        }

        // Presentation is irrelevant here, so the only requirement is a graphics queue.
        foreach (PhysicalDevice candidate in devices)
        {
            uint families = 0;
            _vk.GetPhysicalDeviceQueueFamilyProperties(candidate, ref families, null);

            QueueFamilyProperties[] properties = new QueueFamilyProperties[families];
            fixed (QueueFamilyProperties* pointer = properties)
            {
                _vk.GetPhysicalDeviceQueueFamilyProperties(candidate, ref families, pointer);
            }

            for (uint i = 0; i < families; i++)
            {
                if (properties[i].QueueFlags.HasFlag(QueueFlags.GraphicsBit))
                {
                    _physicalDevice = candidate;
                    _queueFamily = i;

                    _vk.GetPhysicalDeviceProperties(candidate, out PhysicalDeviceProperties device);
                    DeviceName = SilkMarshal.PtrToString((nint)device.DeviceName) ?? "unknown";
                    return;
                }
            }
        }

        throw new VulkanException("No device has a graphics queue.");
    }

    private void CreateDevice()
    {
        float priority = 1f;
        var queueInfo = new DeviceQueueCreateInfo
        {
            SType = StructureType.DeviceQueueCreateInfo,
            QueueFamilyIndex = _queueFamily,
            QueueCount = 1,
            PQueuePriorities = &priority,
        };

        var dynamicRendering = new PhysicalDeviceDynamicRenderingFeatures
        {
            SType = StructureType.PhysicalDeviceDynamicRenderingFeatures,
            DynamicRendering = true,
        };

        var createInfo = new DeviceCreateInfo
        {
            SType = StructureType.DeviceCreateInfo,
            PNext = &dynamicRendering,
            QueueCreateInfoCount = 1,
            PQueueCreateInfos = &queueInfo,
        };

        if (_vk.CreateDevice(_physicalDevice, in createInfo, null, out _device) != Result.Success)
        {
            throw new VulkanException("Could not create a logical device.");
        }

        _vk.GetDeviceQueue(_device, _queueFamily, 0, out _queue);
    }

    private void CreateCommandPool()
    {
        var poolInfo = new CommandPoolCreateInfo
        {
            SType = StructureType.CommandPoolCreateInfo,
            QueueFamilyIndex = _queueFamily,
            Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
        };

        if (_vk.CreateCommandPool(_device, in poolInfo, null, out _commandPool) != Result.Success)
        {
            throw new VulkanException("Could not create a command pool.");
        }
    }

    private (Image Image, DeviceMemory Memory) CreateImage(int width, int height, Format format)
    {
        var imageInfo = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = format,
            Extent = new Extent3D((uint)width, (uint)height, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferSrcBit,
            InitialLayout = ImageLayout.Undefined,
        };

        if (_vk.CreateImage(_device, in imageInfo, null, out Image image) != Result.Success)
        {
            throw new VulkanException("Could not create the render target.");
        }

        _vk.GetImageMemoryRequirements(_device, image, out MemoryRequirements requirements);
        DeviceMemory memory = Allocate(requirements, MemoryPropertyFlags.DeviceLocalBit);
        _vk.BindImageMemory(_device, image, memory, 0);

        return (image, memory);
    }

    private ImageView CreateView(Image image, Format format)
    {
        var viewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = image,
            ViewType = ImageViewType.Type2D,
            Format = format,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                LevelCount = 1,
                LayerCount = 1,
            },
        };

        if (_vk.CreateImageView(_device, in viewInfo, null, out ImageView view) != Result.Success)
        {
            throw new VulkanException("Could not create the render target view.");
        }

        return view;
    }

    private (Silk.NET.Vulkan.Buffer Buffer, DeviceMemory Memory) CreateReadbackBuffer(int width, int height)
    {
        var bufferInfo = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = (ulong)(width * height * 4),
            Usage = BufferUsageFlags.TransferDstBit,
            SharingMode = SharingMode.Exclusive,
        };

        if (_vk.CreateBuffer(_device, in bufferInfo, null, out Silk.NET.Vulkan.Buffer buffer) != Result.Success)
        {
            throw new VulkanException("Could not create the readback buffer.");
        }

        _vk.GetBufferMemoryRequirements(_device, buffer, out MemoryRequirements requirements);

        // Host-coherent avoids an explicit invalidate before reading the mapping.
        DeviceMemory memory = Allocate(
            requirements, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);

        _vk.BindBufferMemory(_device, buffer, memory, 0);
        return (buffer, memory);
    }

    private DeviceMemory Allocate(MemoryRequirements requirements, MemoryPropertyFlags flags)
    {
        _vk.GetPhysicalDeviceMemoryProperties(_physicalDevice, out PhysicalDeviceMemoryProperties properties);

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

                if (_vk.AllocateMemory(_device, in allocateInfo, null, out DeviceMemory memory) != Result.Success)
                {
                    throw new VulkanException("Could not allocate device memory.");
                }

                return memory;
            }
        }

        throw new VulkanException($"No memory type satisfies {flags}.");
    }

    private CommandBuffer BeginCommands()
    {
        var allocateInfo = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = _commandPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = 1,
        };

        _vk.AllocateCommandBuffers(_device, in allocateInfo, out CommandBuffer command);

        var begin = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };

        _vk.BeginCommandBuffer(command, in begin);
        return command;
    }

    private void EndAndWait(CommandBuffer command)
    {
        _vk.EndCommandBuffer(command);

        var submit = new SubmitInfo
        {
            SType = StructureType.SubmitInfo,
            CommandBufferCount = 1,
            PCommandBuffers = &command,
        };

        _vk.QueueSubmit(_queue, 1, in submit, default);
        _vk.QueueWaitIdle(_queue);
        _vk.FreeCommandBuffers(_device, _commandPool, 1, in command);
    }

    private void Transition(CommandBuffer command, Image image, ImageLayout from, ImageLayout to)
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
                AspectMask = ImageAspectFlags.ColorBit,
                LevelCount = 1,
                LayerCount = 1,
            },
            SrcAccessMask = from == ImageLayout.Undefined
                ? AccessFlags.None
                : AccessFlags.ColorAttachmentWriteBit,
            DstAccessMask = to == ImageLayout.TransferSrcOptimal
                ? AccessFlags.TransferReadBit
                : AccessFlags.ColorAttachmentWriteBit,
        };

        _vk.CmdPipelineBarrier(
            command,
            PipelineStageFlags.AllCommandsBit,
            PipelineStageFlags.AllCommandsBit,
            0, 0, null, 0, null, 1, in barrier);
    }
}
