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
    private uint _lastImageIndex;
    private bool _presentedAnything;
    private bool _needsRecreate;
    private ShaderCompiler? _shaderCompiler;
    private TrianglePipeline? _triangle;
    private OverlayPipeline? _overlay;
    private SkyboxPipeline? _skybox;
    private SceneGeometry? _skyOwner;
    private OverlayAtlas? _overlayAtlas;

    private VulkanContext? _context;
    private MeshPipeline? _meshPipeline;
    private FrameUniformSet? _frames;
    private MeshPipeline? _rayTracedPipeline;
    private FrameUniformSet? _rayTracedFrames;
    private SceneGeometry? _scene;
    private Camera? _camera;

    private bool _rayTracingEnabled;
    private ShadowDenoiser? _denoiser;
    private CompositePipeline? _composite;
    private bool _composed;
    private bool _denoiserFailed;

    /// <summary>The picture, while it is only half of one.</summary>
    /// <remarks>
    /// Ray tracing draws the room into this rather than into the swapchain, because what
    /// the mesh pass produces at that point is the indirect half of the lighting and not
    /// yet a picture. The two halves and their two occlusion terms meet in a pass of their
    /// own afterwards.
    /// </remarks>
    private Image _sceneImage;
    private DeviceMemory _sceneMemory;
    private ImageView _sceneView;

    /// <summary>The finished picture, before it is copied out to be shown.</summary>
    /// <remarks>
    /// Reflections need a lit picture to reflect, and the one they are being added to is
    /// not finished yet. They read this one, a frame old, and reproject it — a frame of
    /// lag in a reflection is not something anybody has ever seen. It holds the sky as
    /// well, so a floor can reflect that, but not the interface, which is drawn after the
    /// copy so that it never appears underfoot.
    /// </remarks>
    private Image _litImage;
    private DeviceMemory _litMemory;
    private ImageView _litView;
    private bool _litSettled;

    private Reflections? _reflections;

    private readonly Image[] _extraImages = new Image[GBuffer.Targets - 1];
    private readonly DeviceMemory[] _extraMemory = new DeviceMemory[GBuffer.Targets - 1];
    private readonly ImageView[] _extraViews = new ImageView[GBuffer.Targets - 1];

    private Image _depthImage;
    private DeviceMemory _depthMemory;
    private ImageView _depthView;

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

    /// <summary>A context wrapping this renderer's device, for building scene resources.</summary>
    public VulkanContext Context =>
        _context ?? throw new VulkanException("The renderer has no device yet.");

    /// <summary>The pipeline scene geometry must be built against.</summary>
    public MeshPipeline MeshPipeline =>
        _meshPipeline ?? throw new VulkanException("The renderer has no mesh pipeline yet.");

    /// <summary>Creates geometry this renderer can draw.</summary>
    /// <returns>Empty scene geometry.</returns>
    public SceneGeometry CreateGeometry() =>
        SceneGeometry.Create(Context, MeshPipeline, Textures);

    /// <summary>
    /// The textures the device is holding, across every room it has drawn.
    /// </summary>
    /// <remarks>
    /// A room's geometry used to own them, so going through a door threw away 120 textures
    /// and uploaded the next room's from scratch — about 200 ms of a 350 ms room load spent
    /// getting back what had just been discarded.
    /// </remarks>
    public TextureCache Textures =>
        field ??= new TextureCache(Context, SceneGeometry.CheckerBoard());

    /// <summary>Whether a ray-traced pipeline was built.</summary>
    public bool SupportsRayTracing => _rayTracedPipeline is not null;

    /// <summary>How much ray tracing to do.</summary>
    public RayTracingQuality Quality { get; set; } = RayTracingQuality.None;

    /// <summary>Sets the lights anything without baked lighting is lit by.</summary>
    /// <param name="lights">The rig the scene was authored with.</param>
    public void SetLights(IReadOnlyList<Formats.Scenes.AuthoredLight> lights)
    {
        _frames?.SetLights(lights);
        _rayTracedFrames?.SetLights(lights);
    }

    /// <summary>Sets what to draw, and from where.</summary>
    /// <param name="scene">The geometry, or null to draw nothing.</param>
    /// <param name="camera">Where to look from.</param>
    /// <remarks>
    /// The renderer does not take ownership: the caller keeps the geometry alive for as
    /// long as it is set, and disposes it afterwards.
    /// </remarks>
    public void SetScene(SceneGeometry? scene, Camera? camera)
    {
        scene?.Finish();

        if (scene?.RayTracing is not null)
        {
            _rayTracedFrames?.SetScene(scene.RayTracing);
        }

        _scene = scene;
        _camera = camera;
    }

    /// <summary>What this renderer's instance can see, for the startup report.</summary>
    /// <remarks>
    /// Asked of the instance the renderer already has. Surveying separately means creating a
    /// second instance and throwing it away, which is 145 ms nobody is waiting to read.
    /// </remarks>
    public VulkanDeviceReport Survey() => VulkanDeviceSelector.Survey(_vk, _instance);

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
            renderer.CreateDepthBuffer();
            renderer.CreateGBuffer();
            renderer.CreateSceneTarget();
            renderer.CreateLitTarget();
            renderer.CreateCommandResources();
            renderer.CreateSynchronization();
            renderer.CreatePipelines();
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

        // Whatever moved since the last frame moves in the traced world too. After the
        // fence, because rebuilding a structure the device is still tracing against is the
        // same hazard as rewriting a vertex buffer it is still reading.
        _scene?.Settle();

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

        // Anything that changed shape goes into this frame's own vertex buffers, now that
        // the fence says the device has finished with them. Doing it earlier would write
        // over a pose a frame still in flight is drawing.
        _scene?.Flush(_frame);

        RecordClear(_commandBuffers[_frame], _images[imageIndex], _imageViews[imageIndex], red, green, blue);
        _lastImageIndex = imageIndex;

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
        _presentedAnything = true;
        return true;
    }

    /// <summary>Reads back the last frame that was presented.</summary>
    /// <returns>The image, or null if nothing has been presented yet.</returns>
    /// <remarks>
    /// Copies out of the swapchain image rather than re-rendering, so what comes back is
    /// exactly what the player saw — including anything a re-render would get differently.
    /// </remarks>
    public Formats.Bitmaps.DecodedImage? Capture()
    {
        if (!_presentedAnything || _context is null || _lastImageIndex >= (uint)_images.Length)
        {
            return null;
        }

        _vk.DeviceWaitIdle(_device);

        int width = (int)_extent.Width;
        int height = (int)_extent.Height;
        Image image = _images[_lastImageIndex];

        var bufferInfo = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = (ulong)(width * height * 4),
            Usage = BufferUsageFlags.TransferDstBit,
            SharingMode = SharingMode.Exclusive,
        };

        _vk.CreateBuffer(_device, in bufferInfo, null, out Silk.NET.Vulkan.Buffer buffer);
        _vk.GetBufferMemoryRequirements(_device, buffer, out MemoryRequirements requirements);

        DeviceMemory memory = _context.Allocate(
            requirements, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);

        _vk.BindBufferMemory(_device, buffer, memory, 0);

        try
        {
            CommandBuffer command = _context.BeginOneShot();

            _context.Transition(command, image, ImageLayout.PresentSrcKhr, ImageLayout.TransferSrcOptimal);

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

            // Back to where presentation expects it, so the next frame is not drawn into
            // an image the driver believes is in another layout.
            _context.Transition(command, image, ImageLayout.TransferSrcOptimal, ImageLayout.PresentSrcKhr);

            _context.EndOneShot(command);

            byte[] pixels = new byte[width * height * 4];
            void* mapped;
            _vk.MapMemory(_device, memory, 0, (ulong)pixels.Length, 0, &mapped);
            new ReadOnlySpan<byte>(mapped, pixels.Length).CopyTo(pixels);
            _vk.UnmapMemory(_device, memory);

            // Most surfaces hand out a BGRA format; the decoded image is RGBA throughout.
            if (_format is Format.B8G8R8A8Srgb or Format.B8G8R8A8Unorm)
            {
                for (int i = 0; i < pixels.Length; i += 4)
                {
                    (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
                }
            }

            return new Formats.Bitmaps.DecodedImage(width, height, pixels, HasAlpha: false, "swapchain");
        }
        finally
        {
            _vk.DestroyBuffer(_device, buffer, null);
            _vk.FreeMemory(_device, memory, null);
        }
    }

    /// <summary>Reads the frame's motion vectors back, in pixels.</summary>
    /// <returns>
    /// Two floats a pixel — how far this pixel's surface was from here a frame ago — or
    /// null if nothing has been drawn yet.
    /// </returns>
    /// <remarks>
    /// For checking them. A motion vector is not visible in the picture and is wrong in
    /// ways that look plausible, so the only honest way to know it is right is to read the
    /// numbers: a still camera should give zero everywhere, a pan should give the same
    /// vector across the whole frame, and a walking character should be the only thing
    /// moving in an otherwise still room.
    /// </remarks>
    public float[]? CaptureMotion()
    {
        if (!_presentedAnything || _context is null || _extraImages[GBuffer.Motion - 1].Handle == 0)
        {
            return null;
        }

        _vk.DeviceWaitIdle(_device);

        int width = (int)_extent.Width;
        int height = (int)_extent.Height;
        Image image = _extraImages[GBuffer.Motion - 1];

        var bufferInfo = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,

            // Two channels of sixteen-bit float.
            Size = (ulong)(width * height * 4),
            Usage = BufferUsageFlags.TransferDstBit,
            SharingMode = SharingMode.Exclusive,
        };

        _vk.CreateBuffer(_device, in bufferInfo, null, out Silk.NET.Vulkan.Buffer buffer);
        _vk.GetBufferMemoryRequirements(_device, buffer, out MemoryRequirements requirements);

        DeviceMemory memory = _context.Allocate(
            requirements, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);

        _vk.BindBufferMemory(_device, buffer, memory, 0);

        try
        {
            CommandBuffer command = _context.BeginOneShot();

            _context.Transition(
                command, image, ImageLayout.ColorAttachmentOptimal, ImageLayout.TransferSrcOptimal);

            var region = new BufferImageCopy
            {
                ImageSubresource = new ImageSubresourceLayers
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    LayerCount = 1,
                },
                ImageExtent = new Extent3D((uint)width, (uint)height, 1),
            };

            _vk.CmdCopyImageToBuffer(
                command, image, ImageLayout.TransferSrcOptimal, buffer, 1, in region);

            _context.Transition(
                command, image, ImageLayout.TransferSrcOptimal, ImageLayout.ColorAttachmentOptimal);

            _context.EndOneShot(command);

            byte[] raw = new byte[width * height * 4];
            void* mapped;
            _vk.MapMemory(_device, memory, 0, (ulong)raw.Length, 0, &mapped);
            new ReadOnlySpan<byte>(mapped, raw.Length).CopyTo(raw);
            _vk.UnmapMemory(_device, memory);

            var motion = new float[width * height * 2];

            for (int i = 0; i < motion.Length; i++)
            {
                motion[i] = (float)BitConverter.ToHalf(raw, i * 2);
            }

            return motion;
        }
        finally
        {
            _vk.DestroyBuffer(_device, buffer, null);
            _vk.FreeMemory(_device, memory, null);
        }
    }

    /// <summary>Marks the swapchain as needing rebuilding, after a resize.</summary>
    public void Invalidate() => _needsRecreate = true;

    /// <summary>Waits until the device has finished everything it was given.</summary>
    /// <remarks>
    /// Before throwing away a scene's geometry. Frames are still in flight when the player
    /// walks through a door, and freeing the buffers they are reading is a use-after-free
    /// that shows up as a driver crash somewhere else entirely.
    /// </remarks>
    public void Idle() => _vk.DeviceWaitIdle(_device);

    /// <summary>Whether an interface can be drawn.</summary>
    public bool HasOverlay => _overlay is not null;

    /// <summary>Gives the renderer an interface to draw on top of the room.</summary>
    /// <param name="atlas">The sheet it is drawn from.</param>
    /// <remarks>
    /// Deferred rather than created with the renderer, because the sheet comes out of the
    /// game's archives and the renderer exists before anything has been read. Calling it
    /// again replaces the sheet, which is what changing font would mean.
    /// </remarks>
    public void SetOverlayAtlas(OverlayAtlas atlas)
    {
        ArgumentNullException.ThrowIfNull(atlas);

        if (_shaderCompiler is null)
        {
            return;
        }

        _vk.DeviceWaitIdle(_device);

        _overlay?.Dispose();
        _overlay = OverlayPipeline.Create(
            _context!, _format, SceneRenderer.DepthFormat, _shaderCompiler, atlas);

        _overlayAtlas = atlas;
    }

    /// <summary>Sets what the interface looks like this frame.</summary>
    /// <param name="overlay">The display list, or null to draw nothing over the room.</param>
    public void SetOverlay(Overlay? overlay)
    {
        if (_overlay is null)
        {
            return;
        }

        if (overlay is null)
        {
            _overlay.Prepare(new Overlay(_overlayAtlas!));
            return;
        }

        _overlayAtlas = overlay.Atlas;
        _overlay.Prepare(overlay);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_device.Handle != 0)
        {
            _vk.DeviceWaitIdle(_device);
            _rayTracedFrames?.Dispose();
            _rayTracedPipeline?.Dispose();
            _frames?.Dispose();
            _meshPipeline?.Dispose();
            _skybox?.Dispose();
            _overlay?.Dispose();
            _triangle?.Dispose();
            _shaderCompiler?.Dispose();
            DestroySynchronization();
            DestroyCommandResources();
            DestroyDepthBuffer();
            DestroyGBuffer();
            DestroySceneTarget();
            DestroyLitTarget();
            _reflections?.Dispose();
            _reflections = null;
            _denoiser?.Dispose();
            _denoiser = null;
            _composite?.Dispose();
            _composite = null;
            _composed = false;
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

        // Ray tracing is enabled wherever the device offers it. Doing so costs nothing
        // while no rays are traced, and the alternative — recreating the device when the
        // quality setting changes — would mean rebuilding every resource with it.
        _rayTracingEnabled = VulkanContext.CanRayTrace(_vk, _physicalDevice);

        string[] names = _rayTracingEnabled
            ? [KhrSwapchain.ExtensionName, .. VulkanContext.RayTracingExtensions]
            : [KhrSwapchain.ExtensionName];

        nint extensionNames = SilkMarshal.StringArrayToPtr(names);

        // Dynamic rendering removes the need for render pass and framebuffer objects,
        // which is a large amount of boilerplate the render graph would otherwise have to
        // manage for every pass.
        var dynamicRendering = new PhysicalDeviceDynamicRenderingFeatures
        {
            SType = StructureType.PhysicalDeviceDynamicRenderingFeatures,
            DynamicRendering = true,
        };

        var rayQuery = new PhysicalDeviceRayQueryFeaturesKHR
        {
            SType = StructureType.PhysicalDeviceRayQueryFeaturesKhr,
            RayQuery = true,
        };

        var accelerationStructure = new PhysicalDeviceAccelerationStructureFeaturesKHR
        {
            SType = StructureType.PhysicalDeviceAccelerationStructureFeaturesKhr,
            AccelerationStructure = true,
            PNext = &rayQuery,
        };

        var addresses = new PhysicalDeviceBufferDeviceAddressFeatures
        {
            SType = StructureType.PhysicalDeviceBufferDeviceAddressFeatures,
            BufferDeviceAddress = true,
            PNext = &accelerationStructure,
        };

        if (_rayTracingEnabled)
        {
            dynamicRendering.PNext = &addresses;
        }

        // TextureCompressionBC is what makes a BC5 or BC7 image legal to create. Every
        // desktop driver has it; asking for it is what the specification requires before
        // the content pipeline's DDS textures may be uploaded at all.
        var features = new PhysicalDeviceFeatures
        {
            SamplerAnisotropy = true,
            TextureCompressionBC = true,
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
                    EnabledExtensionCount = (uint)names.Length,
                    PpEnabledExtensionNames = (byte**)extensionNames,
                    PEnabledFeatures = &features,
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
            // TransferSrc as well, so a presented frame can be read back for a
            // screenshot — which is also the only way to prove from a test that the
            // windowed path draws what the offscreen one does.
            ImageUsage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferDstBit |
                         ImageUsageFlags.TransferSrcBit,
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

        PrepareDeferred(buffer);

        // Whether the room's pass produces a picture or only the raw materials of one.
        bool deferred = Quality != RayTracingQuality.None &&
                        _scene?.RayTracing is not null &&
                        _rayTracedPipeline is not null &&
                        _denoiser is not null &&
                        _composite is not null;

        RenderingAttachmentInfo* attachments =
            stackalloc RenderingAttachmentInfo[(int)GBuffer.Targets];

        if (deferred)
        {
            Transition(
                buffer, _sceneImage, ImageLayout.Undefined, ImageLayout.ColorAttachmentOptimal);
        }

        attachments[GBuffer.Colour] = new RenderingAttachmentInfo
        {
            SType = StructureType.RenderingAttachmentInfo,
            ImageView = deferred ? _sceneView : view,
            ImageLayout = ImageLayout.ColorAttachmentOptimal,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.Store,
            ClearValue = new ClearValue(new ClearColorValue(r, g, b, 1f)),
        };

        for (int i = 0; i < _extraViews.Length; i++)
        {
            Transition(
                buffer, _extraImages[i], ImageLayout.Undefined, ImageLayout.ColorAttachmentOptimal);

            attachments[i + 1] = new RenderingAttachmentInfo
            {
                SType = StructureType.RenderingAttachmentInfo,
                ImageView = _extraViews[i],
                ImageLayout = ImageLayout.ColorAttachmentOptimal,
                LoadOp = AttachmentLoadOp.Clear,
                StoreOp = AttachmentStoreOp.Store,

                // Zero motion and a zero normal, which is what a pixel the room never
                // covered should read as: the sky did not move and has no surface.
                ClearValue = new ClearValue(new ClearColorValue(0f, 0f, 0f, 0f)),
            };
        }

        var depthAttachment = new RenderingAttachmentInfo
        {
            SType = StructureType.RenderingAttachmentInfo,
            ImageView = _depthView,
            ImageLayout = ImageLayout.DepthStencilAttachmentOptimal,
            LoadOp = AttachmentLoadOp.Clear,
            // Kept, now that something reads it after the frame is drawn.
            StoreOp = AttachmentStoreOp.Store,
            ClearValue = new ClearValue(depthStencil: new ClearDepthStencilValue(1f, 0)),
        };

        var rendering = new RenderingInfo
        {
            SType = StructureType.RenderingInfo,
            RenderArea = new Rect2D { Extent = _extent },
            LayerCount = 1,
            ColorAttachmentCount = GBuffer.Targets,
            PColorAttachments = attachments,
            PDepthAttachment = _depthView.Handle != 0 ? &depthAttachment : null,
        };

        TransitionDepth(buffer);
        _vk.CmdBeginRendering(buffer, in rendering);

        // The room's sky, built the first time this geometry is drawn. It needs the shader
        // compiler and the swapchain's formats, which the geometry does not have.
        if (_scene is not null && !ReferenceEquals(_scene, _skyOwner))
        {
            _skyOwner = _scene;
            _skybox?.Dispose();
            _skybox = null;

            if (_scene.SkyboxFaces is { Count: 6 } faces && _shaderCompiler is not null)
            {
                try
                {
                    _skybox = SkyboxPipeline.Create(
                        _context!, _format, SceneRenderer.DepthFormat, _shaderCompiler,
                        faces, _scene.SkyboxAzimuth);
                }
                catch (VulkanException)
                {
                    // A room without a sky is a room; a room that will not draw is not.
                    _skybox = null;
                }
            }
        }

        if (_scene is not null && _camera is not null && _meshPipeline is not null && _frames is not null)
        {
            bool tracing = Quality != RayTracingQuality.None &&
                           _rayTracedPipeline is not null &&
                           _rayTracedFrames is not null &&
                           _scene.RayTracing is not null;

            MeshPipeline pipeline = tracing ? _rayTracedPipeline! : _meshPipeline;
            FrameUniformSet frames = tracing ? _rayTracedFrames! : _frames;

            if (tracing)
            {
                frames.Settings = RayTracingSettings.For(Quality);
            }

            SceneDraw.Record(
                _vk,
                buffer,
                pipeline,
                frames,
                _scene,
                _frame,
                (int)_extent.Width,
                (int)_extent.Height,
                _camera);
        }
        else if (_triangle is not null)
        {
            var viewport = new Viewport
            {
                Width = _extent.Width,
                Height = _extent.Height,
                MaxDepth = 1f,
            };

            var scissor = new Rect2D { Extent = _extent };

            _vk.CmdSetViewport(buffer, 0, 1, in viewport);
            _vk.CmdSetScissor(buffer, 0, 1, in scissor);
            _vk.CmdBindPipeline(buffer, PipelineBindPoint.Graphics, _triangle.Handle);
            _vk.CmdDraw(buffer, 3, 1, 0, 0);
        }

        // The sky and the interface only belong here when this scope is producing the
        // picture. When it is not, they wait for the pass that turns its parts into one.
        if (!deferred)
        {
            // The sky after the room, so it fills only what the room left empty rather
            // than shading every pixel and being painted over.
            if (_camera is not null)
            {
                _skybox?.Record(buffer, _camera, (int)_extent.Width, (int)_extent.Height);
            }

            // On top of the room and inside the same pass: the interface has no business
            // in the depth buffer, and starting a second pass to say so would cost a store
            // and a load of the whole colour target.
            _overlay?.Record(buffer, (int)_extent.Width, (int)_extent.Height);
        }

        _vk.CmdEndRendering(buffer);

        // Where everything was drawn, ready for the next frame's motion vectors.
        _scene?.Advance();

        if (deferred)
        {
            Compose(buffer, image, view);
        }

        Transition(buffer, image, ImageLayout.ColorAttachmentOptimal, ImageLayout.PresentSrcKhr);

        if (_vk.EndCommandBuffer(buffer) != Result.Success)
        {
            throw new VulkanException("Could not record the frame.");
        }
    }

    /// <summary>Builds the denoiser and the compositing pass, and keeps them pointed
    /// at the right things.</summary>
    /// <param name="buffer">Command buffer being recorded.</param>
    /// <remarks>
    /// Not at startup: none of it can be built until there is a scene with an acceleration
    /// structure to trace against, and the quality setting can turn the whole path off.
    /// </remarks>
    private void PrepareDeferred(CommandBuffer buffer)
    {
        // Once it has failed it will fail the same way every frame, and retrying five
        // shader compilations a frame is slow enough to look like the renderer hanging.
        if (_denoiserFailed ||
            Quality == RayTracingQuality.None ||
            _context is null ||
            _shaderCompiler is null ||
            _scene?.RayTracing is null ||
            _rayTracedFrames is null)
        {
            return;
        }

        if (_denoiser is null)
        {
            try
            {
                _denoiser = ShadowDenoiser.Create(
                    _context, _shaderCompiler, (int)_extent.Width, (int)_extent.Height);

                _composite ??= CompositePipeline.Create(_context, _shaderCompiler, _format);
            }
            catch (VulkanException error)
            {
                // Said out loud, because the room still draws without it — with the
                // lighting it had before any of this existed — and a renderer that
                // quietly loses a whole stage looks like one that never had it.
                Console.Error.WriteLine(
                    "WARNING GK3R3410: The occlusion denoiser could not be built, so " +
                    "the room is lit without it. (" + error.Message + ")");

                _denoiser?.Dispose();
                _denoiser = null;
                _composed = false;
                _denoiserFailed = true;

                return;
            }

            if (_denoiser is null)
            {
                return;
            }

            _reflections?.Dispose();
            _reflections = Reflections.Create(
                _context, _shaderCompiler, (int)_extent.Width, (int)_extent.Height);

            _denoiser.Settle(buffer);
            _reflections.Settle(buffer);
            _composed = false;
        }

        if (!_composed)
        {
            _denoiser.Bind(
                _depthView,
                _extraViews[GBuffer.Normal - 1],
                _extraViews[GBuffer.Motion - 1],
                _scene.RayTracing.Handle,
                _rayTracedFrames.Rig.Handle,
                _rayTracedFrames.Rig.Size);

            _reflections!.Bind(
                _depthView,
                _extraViews[GBuffer.Normal - 1],
                _extraViews[GBuffer.Motion - 1],
                _litView);

            _composite!.Bind(
                _sceneView,
                _extraViews[GBuffer.Direct - 1],
                _denoiser.Shadow,
                _denoiser.Occlusion,
                _reflections.Buffers);

            _composed = true;
        }

        _denoiser.Point(_scene.RayTracing.Handle);
    }

    /// <summary>Traces the occlusion, filters it, and puts the picture together.</summary>
    /// <param name="buffer">Command buffer being recorded.</param>
    /// <param name="image">The swapchain image the frame is going to.</param>
    /// <param name="view">Its view.</param>
    /// <remarks>
    /// Between the two scopes rather than inside either: the tracing reads the depth and
    /// the normals the first one wrote, which cannot be sampled while they are still
    /// attachments, and the sky and the interface belong on top of what this produces
    /// rather than underneath it.
    /// </remarks>
    private void Compose(CommandBuffer buffer, Image image, ImageView view)
    {
        Transition(
            buffer, _sceneImage, ImageLayout.ColorAttachmentOptimal,
            ImageLayout.ShaderReadOnlyOptimal);

        for (int i = 0; i < _extraViews.Length; i++)
        {
            Transition(
                buffer, _extraImages[i], ImageLayout.ColorAttachmentOptimal,
                ImageLayout.ShaderReadOnlyOptimal);
        }

        _context!.Transition(
            buffer, _depthImage, ImageLayout.DepthStencilAttachmentOptimal,
            ImageLayout.ShaderReadOnlyOptimal, ImageAspectFlags.DepthBit);

        RayTracingSettings settings = RayTracingSettings.For(Quality);

        _denoiser!.Record(
            buffer,
            _camera!,
            _depthImage,
            settings.AmbientOcclusionRadius,
            settings.OcclusionSamples);

        // Last frame's picture, which is the one there is to reflect. It ends every frame
        // as the source of the copy to the screen, so that is where it is coming from.
        Transition(
            buffer,
            _litImage,
            _litSettled ? ImageLayout.TransferSrcOptimal : ImageLayout.Undefined,
            ImageLayout.ShaderReadOnlyOptimal);

        _reflections!.Record(buffer, _camera!, Rendering.Materials.SurfaceFinish.Roughest);

        _context.Transition(
            buffer, _depthImage, ImageLayout.ShaderReadOnlyOptimal,
            ImageLayout.DepthStencilAttachmentOptimal, ImageAspectFlags.DepthBit);

        Transition(
            buffer, _litImage, ImageLayout.ShaderReadOnlyOptimal,
            ImageLayout.ColorAttachmentOptimal);

        var attachment = new RenderingAttachmentInfo
        {
            SType = StructureType.RenderingAttachmentInfo,

            // Into a picture of its own rather than straight onto the screen, so that the
            // next frame has something to reflect.
            ImageView = _litView,
            ImageLayout = ImageLayout.ColorAttachmentOptimal,

            // Nothing to load: the first thing drawn covers every pixel of it.
            LoadOp = AttachmentLoadOp.DontCare,
            StoreOp = AttachmentStoreOp.Store,
        };

        // The room's own depth, kept so the sky can still be told where the room is not.
        var depthAttachment = new RenderingAttachmentInfo
        {
            SType = StructureType.RenderingAttachmentInfo,
            ImageView = _depthView,
            ImageLayout = ImageLayout.DepthStencilAttachmentOptimal,
            LoadOp = AttachmentLoadOp.Load,
            StoreOp = AttachmentStoreOp.DontCare,
        };

        var rendering = new RenderingInfo
        {
            SType = StructureType.RenderingInfo,
            RenderArea = new Rect2D { Extent = _extent },
            LayerCount = 1,
            ColorAttachmentCount = 1,
            PColorAttachments = &attachment,
            PDepthAttachment = _depthView.Handle != 0 ? &depthAttachment : null,
        };

        _vk.CmdBeginRendering(buffer, in rendering);

        _composite!.Record(
            buffer, (int)_extent.Width, (int)_extent.Height, _reflections.Parity);

        if (_camera is not null)
        {
            _skybox?.Record(buffer, _camera, (int)_extent.Width, (int)_extent.Height);
        }

        _vk.CmdEndRendering(buffer);

        // Onto the screen. A copy rather than another full-screen triangle because the two
        // are the same size and the same format, so there is nothing to do but move it.
        Transition(
            buffer, _litImage, ImageLayout.ColorAttachmentOptimal,
            ImageLayout.TransferSrcOptimal);

        Transition(
            buffer, image, ImageLayout.ColorAttachmentOptimal, ImageLayout.TransferDstOptimal);

        var region = new ImageCopy
        {
            SrcSubresource = new ImageSubresourceLayers
            {
                AspectMask = ImageAspectFlags.ColorBit,
                LayerCount = 1,
            },
            DstSubresource = new ImageSubresourceLayers
            {
                AspectMask = ImageAspectFlags.ColorBit,
                LayerCount = 1,
            },
            Extent = new Extent3D(_extent.Width, _extent.Height, 1),
        };

        _vk.CmdCopyImage(
            buffer,
            _litImage,
            ImageLayout.TransferSrcOptimal,
            image,
            ImageLayout.TransferDstOptimal,
            1,
            in region);

        Transition(
            buffer, image, ImageLayout.TransferDstOptimal, ImageLayout.ColorAttachmentOptimal);

        _litSettled = true;

        // The interface last and straight onto the screen, so that it is never part of
        // what the next frame reflects. A floor should show the room, not the inventory.
        var overlayAttachment = new RenderingAttachmentInfo
        {
            SType = StructureType.RenderingAttachmentInfo,
            ImageView = view,
            ImageLayout = ImageLayout.ColorAttachmentOptimal,
            LoadOp = AttachmentLoadOp.Load,
            StoreOp = AttachmentStoreOp.Store,
        };

        var overlayRendering = new RenderingInfo
        {
            SType = StructureType.RenderingInfo,
            RenderArea = new Rect2D { Extent = _extent },
            LayerCount = 1,
            ColorAttachmentCount = 1,
            PColorAttachments = &overlayAttachment,
        };

        _vk.CmdBeginRendering(buffer, in overlayRendering);
        _overlay?.Record(buffer, (int)_extent.Width, (int)_extent.Height);
        _vk.CmdEndRendering(buffer);
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

    private void CreatePipelines()
    {
        // Compiled shaders are cached beside the executable, so the compiler runs only
        // when a shader actually changes.
        _shaderCompiler = new ShaderCompiler(Path.Combine(AppContext.BaseDirectory, "shader-cache"));
        _triangle = TrianglePipeline.Create(_vk, _device, _format, _shaderCompiler);

        _context = VulkanContext.Adopt(
            _vk, _instance, _physicalDevice, _device, _graphicsQueue, _graphicsFamily,
            _commandPool, DeviceName, _rayTracingEnabled);

        _meshPipeline = MeshPipeline.Create(
            _context, _format, SceneRenderer.DepthFormat, _shaderCompiler);

        _frames = FrameUniformSet.Create(_context, _meshPipeline, FramesInFlight);

        if (_context.SupportsRayTracing)
        {
            _rayTracedPipeline = MeshPipeline.Create(
                _context, _format, SceneRenderer.DepthFormat, _shaderCompiler, rayTracing: true);

            _rayTracedFrames = FrameUniformSet.Create(_context, _rayTracedPipeline, FramesInFlight);
        }
    }

    /// <summary>Creates the normal and motion targets the frame writes beside its picture.</summary>
    /// <remarks>
    /// The same size as the swapchain and rebuilt with it. Both are sampled afterwards, so
    /// both carry the transfer and sampled usages a filter needs to read them.
    /// </remarks>
    private void CreateGBuffer()
    {
        Format[] formats = [GBuffer.NormalFormat, GBuffer.MotionFormat, GBuffer.LightFormat];

        for (int i = 0; i < _extraViews.Length; i++)
        {
            var imageInfo = new ImageCreateInfo
            {
                SType = StructureType.ImageCreateInfo,
                ImageType = ImageType.Type2D,
                Format = formats[i],
                Extent = new Extent3D(_extent.Width, _extent.Height, 1),
                MipLevels = 1,
                ArrayLayers = 1,
                Samples = SampleCountFlags.Count1Bit,
                Tiling = ImageTiling.Optimal,
                Usage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit |
                        ImageUsageFlags.TransferSrcBit,
                InitialLayout = ImageLayout.Undefined,
            };

            if (_vk.CreateImage(_device, in imageInfo, null, out _extraImages[i]) != Result.Success)
            {
                throw new VulkanException("Could not create a frame target.");
            }

            _vk.GetImageMemoryRequirements(
                _device, _extraImages[i], out MemoryRequirements requirements);

            _extraMemory[i] = AllocateDepthMemory(requirements);
            _vk.BindImageMemory(_device, _extraImages[i], _extraMemory[i], 0);

            var viewInfo = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = _extraImages[i],
                ViewType = ImageViewType.Type2D,
                Format = formats[i],
                SubresourceRange = new ImageSubresourceRange
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    LevelCount = 1,
                    LayerCount = 1,
                },
            };

            if (_vk.CreateImageView(_device, in viewInfo, null, out _extraViews[i]) != Result.Success)
            {
                throw new VulkanException("Could not create a frame target's view.");
            }
        }
    }

    /// <summary>Builds the half-lit picture the compositing pass finishes.</summary>
    private void CreateSceneTarget()
    {
        var imageInfo = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = GBuffer.LightFormat,
            Extent = new Extent3D(_extent.Width, _extent.Height, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit,
            InitialLayout = ImageLayout.Undefined,
        };

        if (_vk.CreateImage(_device, in imageInfo, null, out _sceneImage) != Result.Success)
        {
            throw new VulkanException("Could not create the scene target.");
        }

        _vk.GetImageMemoryRequirements(_device, _sceneImage, out MemoryRequirements requirements);

        _sceneMemory = AllocateDepthMemory(requirements);
        _vk.BindImageMemory(_device, _sceneImage, _sceneMemory, 0);

        var viewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = _sceneImage,
            ViewType = ImageViewType.Type2D,
            Format = GBuffer.LightFormat,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                LevelCount = 1,
                LayerCount = 1,
            },
        };

        if (_vk.CreateImageView(_device, in viewInfo, null, out _sceneView) != Result.Success)
        {
            throw new VulkanException("Could not create the scene target's view.");
        }
    }

    /// <summary>Builds the picture the swapchain is copied from.</summary>
    private void CreateLitTarget()
    {
        var imageInfo = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = _format,
            Extent = new Extent3D(_extent.Width, _extent.Height, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit |
                    ImageUsageFlags.TransferSrcBit,
            InitialLayout = ImageLayout.Undefined,
        };

        if (_vk.CreateImage(_device, in imageInfo, null, out _litImage) != Result.Success)
        {
            throw new VulkanException("Could not create the lit target.");
        }

        _vk.GetImageMemoryRequirements(_device, _litImage, out MemoryRequirements requirements);

        _litMemory = AllocateDepthMemory(requirements);
        _vk.BindImageMemory(_device, _litImage, _litMemory, 0);

        var viewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = _litImage,
            ViewType = ImageViewType.Type2D,
            Format = _format,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                LevelCount = 1,
                LayerCount = 1,
            },
        };

        if (_vk.CreateImageView(_device, in viewInfo, null, out _litView) != Result.Success)
        {
            throw new VulkanException("Could not create the lit target's view.");
        }

        _litSettled = false;
    }

    private void DestroyLitTarget()
    {
        if (_litView.Handle != 0)
        {
            _vk.DestroyImageView(_device, _litView, null);
            _litView = default;
        }

        if (_litImage.Handle != 0)
        {
            _vk.DestroyImage(_device, _litImage, null);
            _litImage = default;
        }

        if (_litMemory.Handle != 0)
        {
            _vk.FreeMemory(_device, _litMemory, null);
            _litMemory = default;
        }
    }

    private void DestroySceneTarget()
    {
        if (_sceneView.Handle != 0)
        {
            _vk.DestroyImageView(_device, _sceneView, null);
            _sceneView = default;
        }

        if (_sceneImage.Handle != 0)
        {
            _vk.DestroyImage(_device, _sceneImage, null);
            _sceneImage = default;
        }

        if (_sceneMemory.Handle != 0)
        {
            _vk.FreeMemory(_device, _sceneMemory, null);
            _sceneMemory = default;
        }
    }

    private void DestroyGBuffer()
    {
        for (int i = 0; i < _extraViews.Length; i++)
        {
            if (_extraViews[i].Handle != 0)
            {
                _vk.DestroyImageView(_device, _extraViews[i], null);
                _extraViews[i] = default;
            }

            if (_extraImages[i].Handle != 0)
            {
                _vk.DestroyImage(_device, _extraImages[i], null);
                _extraImages[i] = default;
            }

            if (_extraMemory[i].Handle != 0)
            {
                _vk.FreeMemory(_device, _extraMemory[i], null);
                _extraMemory[i] = default;
            }
        }
    }

    /// <summary>Creates the depth buffer the swapchain's images are drawn against.</summary>
    private void CreateDepthBuffer()
    {
        var imageInfo = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = SceneRenderer.DepthFormat,
            Extent = new Extent3D(_extent.Width, _extent.Height, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            // Sampled as well as written: everything that filters over time reads the
            // depth of the frame it is filtering.
            Usage = ImageUsageFlags.DepthStencilAttachmentBit | ImageUsageFlags.SampledBit,
            InitialLayout = ImageLayout.Undefined,
        };

        if (_vk.CreateImage(_device, in imageInfo, null, out _depthImage) != Result.Success)
        {
            throw new VulkanException("Could not create the depth buffer.");
        }

        _vk.GetImageMemoryRequirements(_device, _depthImage, out MemoryRequirements requirements);
        _depthMemory = AllocateDepthMemory(requirements);
        _vk.BindImageMemory(_device, _depthImage, _depthMemory, 0);

        var viewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = _depthImage,
            ViewType = ImageViewType.Type2D,
            Format = SceneRenderer.DepthFormat,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.DepthBit,
                LevelCount = 1,
                LayerCount = 1,
            },
        };

        if (_vk.CreateImageView(_device, in viewInfo, null, out _depthView) != Result.Success)
        {
            throw new VulkanException("Could not create the depth buffer's view.");
        }
    }

    private DeviceMemory AllocateDepthMemory(MemoryRequirements requirements)
    {
        _vk.GetPhysicalDeviceMemoryProperties(_physicalDevice, out PhysicalDeviceMemoryProperties properties);

        for (uint i = 0; i < properties.MemoryTypeCount; i++)
        {
            bool allowed = (requirements.MemoryTypeBits & (1u << (int)i)) != 0;

            if (allowed &&
                properties.MemoryTypes[(int)i].PropertyFlags.HasFlag(MemoryPropertyFlags.DeviceLocalBit))
            {
                var allocateInfo = new MemoryAllocateInfo
                {
                    SType = StructureType.MemoryAllocateInfo,
                    AllocationSize = requirements.Size,
                    MemoryTypeIndex = i,
                };

                if (_vk.AllocateMemory(_device, in allocateInfo, null, out DeviceMemory memory)
                    != Result.Success)
                {
                    throw new VulkanException("Could not allocate the depth buffer's memory.");
                }

                return memory;
            }
        }

        throw new VulkanException("No memory type can back a depth buffer.");
    }

    private void DestroyDepthBuffer()
    {
        if (_depthView.Handle != 0)
        {
            _vk.DestroyImageView(_device, _depthView, null);
            _depthView = default;
        }

        if (_depthImage.Handle != 0)
        {
            _vk.DestroyImage(_device, _depthImage, null);
            _depthImage = default;
        }

        if (_depthMemory.Handle != 0)
        {
            _vk.FreeMemory(_device, _depthMemory, null);
            _depthMemory = default;
        }
    }

    /// <summary>Puts the depth buffer into the layout rendering expects.</summary>
    /// <remarks>
    /// Done every frame rather than once, because the contents are cleared at the start of
    /// each pass and so the previous layout is never worth preserving.
    /// </remarks>
    private void TransitionDepth(CommandBuffer buffer)
    {
        if (_depthImage.Handle == 0)
        {
            return;
        }

        var barrier = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = ImageLayout.Undefined,
            NewLayout = ImageLayout.DepthStencilAttachmentOptimal,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = _depthImage,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.DepthBit,
                LevelCount = 1,
                LayerCount = 1,
            },
            DstAccessMask = AccessFlags.DepthStencilAttachmentWriteBit,
        };

        _vk.CmdPipelineBarrier(
            buffer,
            PipelineStageFlags.TopOfPipeBit,
            PipelineStageFlags.EarlyFragmentTestsBit,
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

        // The denoiser holds a frame's worth of history at one size, and none of it means
        // anything at another.
        _reflections?.Dispose();
        _reflections = null;
        _denoiser?.Dispose();
        _denoiser = null;
        _composite?.Dispose();
        _composite = null;
        _composed = false;

        DestroyDepthBuffer();
        DestroyGBuffer();
        DestroySceneTarget();
        DestroyLitTarget();
        DestroySwapchain();
        CreateSwapchain();
        CreateDepthBuffer();
        CreateGBuffer();
        CreateSceneTarget();
        CreateLitTarget();
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
