using System.Globalization;
using GK3Reborn.Platform;
using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;

// Vulkan and the BCL both define Semaphore; the graphics one is meant throughout.
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace GK3Reborn.Rendering.Vulkan;

/// <summary>
/// A minimal Vulkan renderer: opens a device, builds a swapchain, and presents.
/// </summary>
/// <remarks>
/// <para>
/// This is P5's foundation rather than its finished form. It establishes the parts every
/// later pass depends on and which are painful to retrofit: queue family selection,
/// swapchain creation and recreation, per-frame synchronisation, and command recording.
/// A render graph and the passes themselves sit on top of exactly this.
/// </para>
/// <para>
/// Frames are double-buffered with a fence per frame in flight, so the CPU may run ahead
/// but never overwrites a command buffer the GPU is still reading. Getting that wrong
/// produces corruption that only appears under load, which is the worst kind to find
/// late.
/// </para>
/// <para>
/// Swapchain recreation is a normal event, not an error. A resize, a monitor change or a
/// minimise all invalidate it, and the driver says so through <c>ErrorOutOfDateKhr</c>
/// and <c>SuboptimalKhr</c> rather than by failing.
/// </para>
/// </remarks>
public sealed unsafe class VulkanRenderer : IDisposable
{
    private const int FramesInFlight = 2;

    private readonly Vk _vk;
    private readonly IVulkanSurfaceSource _surfaceSource;
    private readonly IGameWindow _window;

    private Instance _instance;
    private KhrSurface _khrSurface = null!;
    private SurfaceKHR _surface;
    private PhysicalDevice _physicalDevice;
    private Device _device;
    private Queue _graphicsQueue;
    private Queue _presentQueue;
    private uint _graphicsFamily;
    private uint _presentFamily;

    private KhrSwapchain _khrSwapchain = null!;
    private SwapchainKHR _swapchain;
    private Image[] _images = [];
    private ImageView[] _imageViews = [];
    private Format _format;
    private Extent2D _extent;

    private CommandPool _commandPool;
    private CommandBuffer[] _commandBuffers = [];
    private Semaphore[] _imageAvailable = [];
    private Semaphore[] _renderFinished = [];
    private Fence[] _inFlight = [];
    private int _frame;
    private bool _needsRecreate;

    private VulkanRenderer(Vk vk, IGameWindow window, IVulkanSurfaceSource surfaceSource)
    {
        _vk = vk;
        _window = window;
        _surfaceSource = surfaceSource;
    }

    /// <summary>The device this renderer is using.</summary>
    public string DeviceName { get; private set; } = "unknown";

    /// <summary>Tiers the chosen device satisfies.</summary>
    public RenderCapabilityTier Tiers { get; private set; }

    /// <summary>Current swapchain size.</summary>
    public (int Width, int Height) SwapchainSize => ((int)_extent.Width, (int)_extent.Height);

    /// <summary>How many images the swapchain holds.</summary>
    public int SwapchainImageCount => _images.Length;

    /// <summary>Creates a renderer for a window.</summary>
    /// <param name="window">Window to present into.</param>
    /// <param name="surfaceSource">Surface provider for that window.</param>
    /// <param name="enableValidation">Whether to turn on validation layers when present.</param>
    /// <returns>The renderer.</returns>
    public static VulkanRenderer Create(
        IGameWindow window, IVulkanSurfaceSource surfaceSource, bool enableValidation = true)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(surfaceSource);

        var renderer = new VulkanRenderer(Vk.GetApi(), window, surfaceSource);

        try
        {
            renderer.CreateInstance(enableValidation);
            renderer.CreateSurface();
            renderer.SelectPhysicalDevice();
            renderer.CreateLogicalDevice();
            renderer.CreateSwapchain();
            renderer.CreateCommandResources();
            renderer.CreateSynchronization();
            return renderer;
        }
        catch
        {
            renderer.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Draws and presents one frame, clearing to a colour.
    /// </summary>
    /// <param name="red">Clear red, 0 to 1.</param>
    /// <param name="green">Clear green, 0 to 1.</param>
    /// <param name="blue">Clear blue, 0 to 1.</param>
    /// <returns>False when the frame was skipped because the swapchain needed rebuilding.</returns>
    public bool DrawFrame(float red, float green, float blue)
    {
        if (_needsRecreate)
        {
            RecreateSwapchain();
            return false;
        }

        Fence fence = _inFlight[_frame];
        _vk.WaitForFences(_device, 1, in fence, true, ulong.MaxValue);

        uint imageIndex = 0;
        Result acquire = _khrSwapchain.AcquireNextImage(
            _device, _swapchain, ulong.MaxValue, _imageAvailable[_frame], default, ref imageIndex);

        if (acquire is Result.ErrorOutOfDateKhr)
        {
            RecreateSwapchain();
            return false;
        }

        if (acquire is not (Result.Success or Result.SuboptimalKhr))
        {
            throw new VulkanException($"Could not acquire a swapchain image: {acquire}.");
        }

        // The fence is only reset once the frame is certain to be submitted; resetting it
        // before a possible early return would deadlock the next wait on it.
        _vk.ResetFences(_device, 1, in fence);

        RecordClear(_commandBuffers[_frame], _images[imageIndex], _imageViews[imageIndex], red, green, blue);

        Semaphore waitSemaphore = _imageAvailable[_frame];
        Semaphore signalSemaphore = _renderFinished[_frame];
        PipelineStageFlags waitStage = PipelineStageFlags.ColorAttachmentOutputBit;
        CommandBuffer commandBuffer = _commandBuffers[_frame];

        var submit = new SubmitInfo
        {
            SType = StructureType.SubmitInfo,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = &waitSemaphore,
            PWaitDstStageMask = &waitStage,
            CommandBufferCount = 1,
            PCommandBuffers = &commandBuffer,
            SignalSemaphoreCount = 1,
            PSignalSemaphores = &signalSemaphore,
        };

        if (_vk.QueueSubmit(_graphicsQueue, 1, in submit, fence) != Result.Success)
        {
            throw new VulkanException("Could not submit the frame.");
        }

        SwapchainKHR swapchain = _swapchain;
        var present = new PresentInfoKHR
        {
            SType = StructureType.PresentInfoKhr,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = &signalSemaphore,
            SwapchainCount = 1,
            PSwapchains = &swapchain,
            PImageIndices = &imageIndex,
        };

        Result presented = _khrSwapchain.QueuePresent(_presentQueue, in present);
        if (presented is Result.ErrorOutOfDateKhr or Result.SuboptimalKhr)
        {
            _needsRecreate = true;
        }
        else if (presented != Result.Success)
        {
            throw new VulkanException($"Could not present: {presented}.");
        }

        _frame = (_frame + 1) % FramesInFlight;
        return true;
    }

    /// <summary>Marks the swapchain as needing rebuilding, after a resize.</summary>
    public void Invalidate() => _needsRecreate = true;

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_device.Handle != 0)
        {
            _vk.DeviceWaitIdle(_device);
            DestroySynchronization();
            DestroyCommandResources();
            DestroySwapchain();
            _vk.DestroyDevice(_device, null);
        }

        if (_surface.Handle != 0)
        {
            _khrSurface?.DestroySurface(_instance, _surface, null);
        }

        if (_instance.Handle != 0)
        {
            _vk.DestroyInstance(_instance, null);
        }

        _vk.Dispose();
    }

    private void CreateInstance(bool enableValidation)
    {
        var applicationInfo = new ApplicationInfo
        {
            SType = StructureType.ApplicationInfo,
            PApplicationName = (byte*)SilkMarshal.StringToPtr("GK3Reborn"),
            PEngineName = (byte*)SilkMarshal.StringToPtr("GK3Reborn"),
            ApiVersion = Vk.Version13,
        };

        string[] extensions = [.. _surfaceSource.RequiredInstanceExtensions];
        nint extensionNames = SilkMarshal.StringArrayToPtr(extensions);
        nint layerNames = 0;

        var createInfo = new InstanceCreateInfo
        {
            SType = StructureType.InstanceCreateInfo,
            PApplicationInfo = &applicationInfo,
            EnabledExtensionCount = (uint)extensions.Length,
            PpEnabledExtensionNames = (byte**)extensionNames,
        };

        if (enableValidation && HasValidationLayer())
        {
            layerNames = SilkMarshal.StringArrayToPtr(["VK_LAYER_KHRONOS_validation"]);
            createInfo.EnabledLayerCount = 1;
            createInfo.PpEnabledLayerNames = (byte**)layerNames;
        }

        try
        {
            if (_vk.CreateInstance(in createInfo, null, out _instance) != Result.Success)
            {
                throw new VulkanException("Could not create a Vulkan instance.");
            }

            if (!_vk.TryGetInstanceExtension(_instance, out _khrSurface))
            {
                throw new VulkanException("The surface extension is unavailable.");
            }
        }
        finally
        {
            SilkMarshal.Free((nint)applicationInfo.PApplicationName);
            SilkMarshal.Free((nint)applicationInfo.PEngineName);
            SilkMarshal.Free(extensionNames);
            if (layerNames != 0)
            {
                SilkMarshal.Free(layerNames);
            }
        }
    }

    private bool HasValidationLayer()
    {
        uint count = 0;
        if (_vk.EnumerateInstanceLayerProperties(ref count, null) != Result.Success || count == 0)
        {
            return false;
        }

        LayerProperties[] layers = new LayerProperties[count];
        fixed (LayerProperties* pointer = layers)
        {
            _vk.EnumerateInstanceLayerProperties(ref count, pointer);
        }

        return layers.Any(l => SilkMarshal.PtrToString((nint)l.LayerName) == "VK_LAYER_KHRONOS_validation");
    }

    private void CreateSurface() =>
        _surface = new SurfaceKHR((ulong)_surfaceSource.CreateSurface((nint)_instance.Handle));

    private void SelectPhysicalDevice()
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

        // Prefer a discrete device that can actually present to this surface. A device
        // that cannot is not a weaker candidate, it is not a candidate.
        PhysicalDevice? best = null;
        bool bestIsDiscrete = false;

        foreach (PhysicalDevice candidate in devices)
        {
            if (!TryFindQueueFamilies(candidate, out uint graphics, out uint present))
            {
                continue;
            }

            _vk.GetPhysicalDeviceProperties(candidate, out PhysicalDeviceProperties properties);
            bool discrete = properties.DeviceType == PhysicalDeviceType.DiscreteGpu;

            if (best is null || (discrete && !bestIsDiscrete))
            {
                best = candidate;
                bestIsDiscrete = discrete;
                _graphicsFamily = graphics;
                _presentFamily = present;
                DeviceName = SilkMarshal.PtrToString((nint)properties.DeviceName) ?? "unknown";
            }
        }

        _physicalDevice = best ?? throw new VulkanException("No device can present to this window.");

        VulkanDeviceReport report = VulkanDeviceSelector.Survey();
        Tiers = report.Devices.FirstOrDefault(d => d.Name == DeviceName)?.Tiers
            ?? RenderCapabilityTier.Compatibility;
    }

    private bool TryFindQueueFamilies(PhysicalDevice device, out uint graphics, out uint present)
    {
        graphics = 0;
        present = 0;
        bool foundGraphics = false;
        bool foundPresent = false;

        uint count = 0;
        _vk.GetPhysicalDeviceQueueFamilyProperties(device, ref count, null);

        QueueFamilyProperties[] families = new QueueFamilyProperties[count];
        fixed (QueueFamilyProperties* pointer = families)
        {
            _vk.GetPhysicalDeviceQueueFamilyProperties(device, ref count, pointer);
        }

        for (uint i = 0; i < count; i++)
        {
            if (!foundGraphics && families[i].QueueFlags.HasFlag(QueueFlags.GraphicsBit))
            {
                graphics = i;
                foundGraphics = true;
            }

            _khrSurface.GetPhysicalDeviceSurfaceSupport(device, i, _surface, out Bool32 supported);
            if (!foundPresent && supported)
            {
                present = i;
                foundPresent = true;
            }
        }

        return foundGraphics && foundPresent;
    }

    private void CreateLogicalDevice()
    {
        // Graphics and present are often the same family; asking for it twice is invalid.
        uint[] families = _graphicsFamily == _presentFamily
            ? [_graphicsFamily]
            : [_graphicsFamily, _presentFamily];

        DeviceQueueCreateInfo[] queues = new DeviceQueueCreateInfo[families.Length];
        float priority = 1f;

        for (int i = 0; i < families.Length; i++)
        {
            queues[i] = new DeviceQueueCreateInfo
            {
                SType = StructureType.DeviceQueueCreateInfo,
                QueueFamilyIndex = families[i],
                QueueCount = 1,
                PQueuePriorities = &priority,
            };
        }

        nint extensionNames = SilkMarshal.StringArrayToPtr([KhrSwapchain.ExtensionName]);

        // Dynamic rendering removes the need for render pass and framebuffer objects,
        // which is a large amount of boilerplate the render graph would otherwise have to
        // manage for every pass.
        var dynamicRendering = new PhysicalDeviceDynamicRenderingFeatures
        {
            SType = StructureType.PhysicalDeviceDynamicRenderingFeatures,
            DynamicRendering = true,
        };

        try
        {
            fixed (DeviceQueueCreateInfo* queuePointer = queues)
            {
                var createInfo = new DeviceCreateInfo
                {
                    SType = StructureType.DeviceCreateInfo,
                    PNext = &dynamicRendering,
                    QueueCreateInfoCount = (uint)queues.Length,
                    PQueueCreateInfos = queuePointer,
                    EnabledExtensionCount = 1,
                    PpEnabledExtensionNames = (byte**)extensionNames,
                };

                if (_vk.CreateDevice(_physicalDevice, in createInfo, null, out _device) != Result.Success)
                {
                    throw new VulkanException("Could not create a logical device.");
                }
            }
        }
        finally
        {
            SilkMarshal.Free(extensionNames);
        }

        _vk.GetDeviceQueue(_device, _graphicsFamily, 0, out _graphicsQueue);
        _vk.GetDeviceQueue(_device, _presentFamily, 0, out _presentQueue);

        if (!_vk.TryGetDeviceExtension(_instance, _device, out _khrSwapchain))
        {
            throw new VulkanException("The swapchain extension is unavailable.");
        }
    }

    private void CreateSwapchain()
    {
        _khrSurface.GetPhysicalDeviceSurfaceCapabilities(
            _physicalDevice, _surface, out SurfaceCapabilitiesKHR capabilities);

        SurfaceFormatKHR surfaceFormat = ChooseFormat();
        _format = surfaceFormat.Format;
        _extent = ChooseExtent(capabilities);

        uint imageCount = capabilities.MinImageCount + 1;
        if (capabilities.MaxImageCount > 0 && imageCount > capabilities.MaxImageCount)
        {
            imageCount = capabilities.MaxImageCount;
        }

        var createInfo = new SwapchainCreateInfoKHR
        {
            SType = StructureType.SwapchainCreateInfoKhr,
            Surface = _surface,
            MinImageCount = imageCount,
            ImageFormat = _format,
            ImageColorSpace = surfaceFormat.ColorSpace,
            ImageExtent = _extent,
            ImageArrayLayers = 1,
            ImageUsage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferDstBit,
            PreTransform = capabilities.CurrentTransform,
            CompositeAlpha = CompositeAlphaFlagsKHR.OpaqueBitKhr,

            // FIFO is the only mode the specification guarantees, so it is the safe
            // default until the settings screen offers the alternatives.
            PresentMode = PresentModeKHR.FifoKhr,
            Clipped = true,
        };

        uint[] families = [_graphicsFamily, _presentFamily];
        fixed (uint* familyPointer = families)
        {
            if (_graphicsFamily != _presentFamily)
            {
                createInfo.ImageSharingMode = SharingMode.Concurrent;
                createInfo.QueueFamilyIndexCount = 2;
                createInfo.PQueueFamilyIndices = familyPointer;
            }
            else
            {
                createInfo.ImageSharingMode = SharingMode.Exclusive;
            }

            if (_khrSwapchain.CreateSwapchain(_device, in createInfo, null, out _swapchain) != Result.Success)
            {
                throw new VulkanException("Could not create a swapchain.");
            }
        }

        uint count = 0;
        _khrSwapchain.GetSwapchainImages(_device, _swapchain, ref count, null);
        _images = new Image[count];
        fixed (Image* pointer = _images)
        {
            _khrSwapchain.GetSwapchainImages(_device, _swapchain, ref count, pointer);
        }

        _imageViews = new ImageView[count];
        for (int i = 0; i < count; i++)
        {
            var viewInfo = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = _images[i],
                ViewType = ImageViewType.Type2D,
                Format = _format,
                SubresourceRange = new ImageSubresourceRange
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    LevelCount = 1,
                    LayerCount = 1,
                },
            };

            if (_vk.CreateImageView(_device, in viewInfo, null, out _imageViews[i]) != Result.Success)
            {
                throw new VulkanException("Could not create a swapchain image view.");
            }
        }
    }

    private SurfaceFormatKHR ChooseFormat()
    {
        uint count = 0;
        _khrSurface.GetPhysicalDeviceSurfaceFormats(_physicalDevice, _surface, ref count, null);

        SurfaceFormatKHR[] formats = new SurfaceFormatKHR[count];
        fixed (SurfaceFormatKHR* pointer = formats)
        {
            _khrSurface.GetPhysicalDeviceSurfaceFormats(_physicalDevice, _surface, ref count, pointer);
        }

        // An sRGB surface means the display does the encoding, so shading stays linear.
        foreach (SurfaceFormatKHR format in formats)
        {
            if (format.Format is Format.B8G8R8A8Srgb or Format.R8G8B8A8Srgb &&
                format.ColorSpace == ColorSpaceKHR.SpaceSrgbNonlinearKhr)
            {
                return format;
            }
        }

        return formats.Length > 0 ? formats[0] : throw new VulkanException("The surface offers no formats.");
    }

    private Extent2D ChooseExtent(SurfaceCapabilitiesKHR capabilities)
    {
        if (capabilities.CurrentExtent.Width != uint.MaxValue)
        {
            return capabilities.CurrentExtent;
        }

        return new Extent2D
        {
            Width = Math.Clamp(
                (uint)_window.FramebufferWidth,
                capabilities.MinImageExtent.Width,
                capabilities.MaxImageExtent.Width),
            Height = Math.Clamp(
                (uint)_window.FramebufferHeight,
                capabilities.MinImageExtent.Height,
                capabilities.MaxImageExtent.Height),
        };
    }

    private void CreateCommandResources()
    {
        var poolInfo = new CommandPoolCreateInfo
        {
            SType = StructureType.CommandPoolCreateInfo,
            QueueFamilyIndex = _graphicsFamily,
            Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
        };

        if (_vk.CreateCommandPool(_device, in poolInfo, null, out _commandPool) != Result.Success)
        {
            throw new VulkanException("Could not create a command pool.");
        }

        _commandBuffers = new CommandBuffer[FramesInFlight];
        var allocateInfo = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = _commandPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = FramesInFlight,
        };

        fixed (CommandBuffer* pointer = _commandBuffers)
        {
            if (_vk.AllocateCommandBuffers(_device, in allocateInfo, pointer) != Result.Success)
            {
                throw new VulkanException("Could not allocate command buffers.");
            }
        }
    }

    private void CreateSynchronization()
    {
        _imageAvailable = new Semaphore[FramesInFlight];
        _renderFinished = new Semaphore[FramesInFlight];
        _inFlight = new Fence[FramesInFlight];

        var semaphoreInfo = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo };
        var fenceInfo = new FenceCreateInfo
        {
            SType = StructureType.FenceCreateInfo,

            // Signalled, so the first frame does not wait for a submission that never
            // happened.
            Flags = FenceCreateFlags.SignaledBit,
        };

        for (int i = 0; i < FramesInFlight; i++)
        {
            if (_vk.CreateSemaphore(_device, in semaphoreInfo, null, out _imageAvailable[i]) != Result.Success ||
                _vk.CreateSemaphore(_device, in semaphoreInfo, null, out _renderFinished[i]) != Result.Success ||
                _vk.CreateFence(_device, in fenceInfo, null, out _inFlight[i]) != Result.Success)
            {
                throw new VulkanException("Could not create frame synchronisation objects.");
            }
        }
    }

    private void RecordClear(CommandBuffer buffer, Image image, ImageView view, float r, float g, float b)
    {
        _vk.ResetCommandBuffer(buffer, 0);

        var begin = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };

        _vk.BeginCommandBuffer(buffer, in begin);

        Transition(buffer, image, ImageLayout.Undefined, ImageLayout.ColorAttachmentOptimal);

        var attachment = new RenderingAttachmentInfo
        {
            SType = StructureType.RenderingAttachmentInfo,
            ImageView = view,
            ImageLayout = ImageLayout.ColorAttachmentOptimal,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.Store,
            ClearValue = new ClearValue(new ClearColorValue(r, g, b, 1f)),
        };

        var rendering = new RenderingInfo
        {
            SType = StructureType.RenderingInfo,
            RenderArea = new Rect2D { Extent = _extent },
            LayerCount = 1,
            ColorAttachmentCount = 1,
            PColorAttachments = &attachment,
        };

        _vk.CmdBeginRendering(buffer, in rendering);
        _vk.CmdEndRendering(buffer);

        Transition(buffer, image, ImageLayout.ColorAttachmentOptimal, ImageLayout.PresentSrcKhr);

        if (_vk.EndCommandBuffer(buffer) != Result.Success)
        {
            throw new VulkanException("Could not record the frame.");
        }
    }

    private void Transition(CommandBuffer buffer, Image image, ImageLayout from, ImageLayout to)
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
            DstAccessMask = to == ImageLayout.PresentSrcKhr
                ? AccessFlags.None
                : AccessFlags.ColorAttachmentWriteBit,
        };

        _vk.CmdPipelineBarrier(
            buffer,
            PipelineStageFlags.ColorAttachmentOutputBit,
            PipelineStageFlags.ColorAttachmentOutputBit,
            0, 0, null, 0, null, 1, in barrier);
    }

    private void RecreateSwapchain()
    {
        // A minimised window has no area to present to; rebuilding would produce an
        // invalid extent, so the frame is simply skipped until it returns.
        if (_window.FramebufferWidth == 0 || _window.FramebufferHeight == 0)
        {
            return;
        }

        _vk.DeviceWaitIdle(_device);
        DestroySwapchain();
        CreateSwapchain();
        _needsRecreate = false;
    }

    private void DestroySwapchain()
    {
        foreach (ImageView view in _imageViews)
        {
            _vk.DestroyImageView(_device, view, null);
        }

        _imageViews = [];

        if (_swapchain.Handle != 0)
        {
            _khrSwapchain.DestroySwapchain(_device, _swapchain, null);
            _swapchain = default;
        }
    }

    private void DestroyCommandResources()
    {
        if (_commandPool.Handle != 0)
        {
            _vk.DestroyCommandPool(_device, _commandPool, null);
            _commandPool = default;
        }
    }

    private void DestroySynchronization()
    {
        foreach (Semaphore semaphore in _imageAvailable)
        {
            _vk.DestroySemaphore(_device, semaphore, null);
        }

        foreach (Semaphore semaphore in _renderFinished)
        {
            _vk.DestroySemaphore(_device, semaphore, null);
        }

        foreach (Fence fence in _inFlight)
        {
            _vk.DestroyFence(_device, fence, null);
        }

        _imageAvailable = [];
        _renderFinished = [];
        _inFlight = [];
    }

    /// <summary>A one-line description of what was created, for logs.</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture,
            $"{DeviceName}: {_extent.Width}x{_extent.Height}, {_images.Length} images, {_format}, tiers {Tiers}");
}
