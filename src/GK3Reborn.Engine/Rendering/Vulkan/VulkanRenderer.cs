using System.Globalization;
using System.Numerics;
using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Foundation.Diagnostics;
using GK3Reborn.Platform;
using GK3Reborn.Rendering.Upscaling;
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
    private ColorSpaceKHR _colorSpace = ColorSpaceKHR.SpaceSrgbNonlinearKhr;
    private Extent2D _extent;

    /// <summary>
    /// The size the room is actually drawn at, which is the window's size divided by
    /// whatever the upscaler was asked for.
    /// </summary>
    /// <remarks>
    /// Everything between the first triangle and the upscale is this size: the depth
    /// buffer, the whole G-buffer, the traced occlusion, the reflections and the lit
    /// picture. Everything after it — the encode onto the swapchain, the movie, the
    /// interface and the fade — is <see cref="_extent"/>. Getting an interface drawn at
    /// render resolution and then stretched is the single most visible way to do this
    /// wrong, which is why the two are separate fields with separate names rather than one
    /// field and a multiplier.
    /// </remarks>
    private Extent2D _renderExtent;

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

    /// <summary>The bring-up triangle, when this renderer was asked for one.</summary>
    /// <remarks>
    /// <b>Not part of the game.</b> It is what a smoke test draws to prove a device, a
    /// swapchain and a present loop work on a machine with nothing else to show, and it is
    /// built only when <c>Create</c> is asked for it. Built always, it is what the frame
    /// with no room and no picture in it fell back to — which is how one frame of a
    /// red-green-blue triangle got in between the publisher's logo and the opening film.
    /// </remarks>
    private TrianglePipeline? _triangle;
    private OverlayPipeline? _overlay;

    /// <summary>The screens' own pictures, by the name they were given.</summary>
    private readonly Dictionary<string, int> _pictures = new(StringComparer.OrdinalIgnoreCase);
    private SkyboxPipeline? _skybox;

    /// <summary>The reconstructed horizon, when the scene carries one.</summary>
    /// <remarks>
    /// Drawn between the room and the sky: real geometry with the far tail of the depth
    /// buffer to itself, so the room occludes it, it occludes itself, and the painted
    /// sky only shows above its ridge line.
    /// </remarks>
    private TerrainPipeline? _terrain;

    /// <summary>The movie over everything, when one is playing.</summary>
    /// <remarks>
    /// Built the first time a frame is handed over rather than at startup, because most of
    /// a session never plays one and a pipeline nobody uses is a pipeline nobody has tested.
    /// </remarks>
    private MoviePipeline? _movie;

    /// <summary>The colour drawn over the finished picture, when the picture is fading.</summary>
    /// <remarks>
    /// Last of everything, over the interface as well as the room, because a scene change
    /// fades the picture rather than what is in it. See <see cref="Fade"/>.
    /// </remarks>
    private FadePipeline? _fadePipeline;
    private SceneGeometry? _skyOwner;
    private OverlayAtlas? _overlayAtlas;

    private VulkanContext? _context;
    private MeshPipeline? _meshPipeline;
    private FrameUniformSet? _frames;
    private MeshPipeline? _rayTracedPipeline;
    private FrameUniformSet? _rayTracedFrames;
    private SceneGeometry? _scene;
    private Camera? _camera;

    /// <summary>
    /// What the wind runs on: wall-clock seconds since the renderer was made.
    /// </summary>
    /// <remarks>
    /// The renderer's own rather than the game's, because it drives presentation and not
    /// state. A paused game, a menu over the room and a conversation waiting on a line of
    /// dialogue all leave the trees moving, which is what they should do; nothing that
    /// reads this can affect anything the story can see.
    /// </remarks>
    private readonly System.Diagnostics.Stopwatch _wind = System.Diagnostics.Stopwatch.StartNew();

    private bool _rayTracingEnabled;

    /// <summary>What the device offered of what was asked for, as the device was made.</summary>
    private DeviceCapabilities _capabilities =
        new(BlockCompression: true, AnisotropicFiltering: true, AstcCompression: false, Etc2Compression: false);
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

    /// <summary>The picture at the size it will be shown, when something upscaled it.</summary>
    /// <remarks>
    /// Absent when nothing is being upscaled, and the output pass reads the lit target
    /// directly. Keeping it optional rather than always allocating one and copying into it
    /// is worth about 32 MB at 4K and, more to the point, means the picture nobody upscaled
    /// is not resampled twice.
    /// </remarks>
    private Image _upscaledImage;
    private DeviceMemory _upscaledMemory;
    private ImageView _upscaledView;

    private OutputPipeline? _outputPipeline;
    private IUpscaler? _upscaler;
    private bool _upscalerFailed;
    private ImageView _outputSource;

    /// <summary>What the player has asked the upscaler for.</summary>
    private UpscalePlan _upscaling = UpscalePlan.None;

    /// <summary>What the player has asked the display for.</summary>
    private OutputPlan _output = OutputPlan.Standard;

    /// <summary>
    /// How many frames have been drawn, which is where the jitter sequence is up to.
    /// </summary>
    /// <remarks>
    /// Never reset. The sequence is taken modulo its own length, so a counter that runs for
    /// the length of a session is a valid index into it, and restarting it at every scene
    /// change would put every room's first frames on the same few sample points.
    /// </remarks>
    private long _frameIndex;

    /// <summary>Whether the next frame has no usable history.</summary>
    /// <remarks>
    /// Set by a resize, a new upscaler, and by whoever loads a room. A temporal upscaler
    /// that is not told smears the last frame of the hotel lobby across the first frame of
    /// the street outside.
    /// </remarks>
    private bool _resetHistory = true;

    private readonly System.Diagnostics.Stopwatch _sinceLastFrame =
        System.Diagnostics.Stopwatch.StartNew();

    /// <summary>Where inside its pixel this frame samples, in pixels.</summary>
    private Vector2 _jitterPixels;

    /// <summary>How long the last frame took, which is what a frame generator is paced by.</summary>
    private float _secondsSinceLastFrame = 1f / 60f;

    /// <summary>
    /// Streamline, when the host started it — which it must do before this device exists.
    /// </summary>
    /// <remarks>
    /// Its features ask for device extensions and for queues of their own, so it has to be
    /// consulted between choosing the physical device and creating the logical one. That is
    /// the whole reason it is passed in rather than started here.
    /// </remarks>
    private readonly Streamline? _streamline;

    private VulkanRenderer(
        Vk vk,
        IGameWindow window,
        IVulkanSurfaceSource surfaceSource,
        bool bringUp,
        Streamline? streamline)
    {
        _vk = vk;
        _window = window;
        _surfaceSource = surfaceSource;
        _bringUp = bringUp;
        _streamline = streamline;
    }

    /// <summary>Whether to build the bring-up triangle. See <see cref="_triangle"/>.</summary>
    private readonly bool _bringUp;

    /// <summary>The device this renderer is using.</summary>
    public string DeviceName { get; private set; } = "unknown";

    /// <summary>Who made it.</summary>
    /// <remarks>
    /// Read by the settings page, which does not offer DLSS on a card that could never run
    /// it. Showing a row that is permanently unavailable teaches the player that the game
    /// does not support their hardware properly, when the truth is that NVIDIA's upscaler
    /// only runs on NVIDIA's cards.
    /// </remarks>
    public GpuVendor Vendor { get; private set; } = GpuVendor.Unknown;

    /// <summary>
    /// Which upscalers it makes sense to offer the player on this machine.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Off and the built-in one always. FSR whatever the card is, because FidelityFX is
    /// compute and runs anywhere — an NVIDIA player without NVIDIA's runtime installed can
    /// still use AMD's. DLSS only on NVIDIA, and only because the alternative is a row that
    /// can never be made to work.
    /// </para>
    /// <para>
    /// A vendor the driver did not identify gets DLSS offered rather than hidden: a card
    /// nobody here has heard of is more likely to be a new one than a wrong one, and
    /// selecting it on a card that cannot run it falls back with a message either way.
    /// </para>
    /// </remarks>
    public IReadOnlyList<UpscalerKind> OfferedUpscalers => Vendor is GpuVendor.Nvidia or GpuVendor.Unknown
        ? [UpscalerKind.Off, UpscalerKind.Spatial, UpscalerKind.Fsr, UpscalerKind.Dlss]
        : [UpscalerKind.Off, UpscalerKind.Spatial, UpscalerKind.Fsr];

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
    /// <summary>How the room's lights are divided up, once a scene has been given some.</summary>
    /// <remarks>
    /// Reported rather than drawn. The whole point of the grid is that nothing looks
    /// different — a fragment gets the same lights, reached more cheaply — so the only way
    /// to know it is working is the numbers: how many cells, and how many lights the
    /// average one holds against how many the room declares.
    /// </remarks>
    public SceneLightGrid? LightGrid { get; private set; }

    public RayTracingQuality Quality { get; set; } = RayTracingQuality.None;

    /// <summary>Which of the vendors' runtimes the player has installed.</summary>
    /// <remarks>
    /// Handed in rather than found here, because where to look is a command-line question
    /// and the renderer is not where command lines are read. Null means nothing was
    /// offered, which is the same as nothing being installed.
    /// </remarks>
    public UpscalerRuntimes? Runtimes { get; set; }

    /// <summary>
    /// What the upscaler is asked to do.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Settable at any time, including in the middle of a game. Changing it marks the
    /// frame's targets for rebuilding at the top of the next frame, which is the only place
    /// it is safe to do — a resize does the same thing for the same reason. The player sees
    /// one frame at the old size and then the new one; there is no stall and no reload.
    /// </para>
    /// <para>
    /// The plan handed back is what was <em>asked for</em>. What is actually running is
    /// <see cref="UpscalerName"/>, which differs whenever a vendor runtime could not be
    /// built and the fallback took over.
    /// </para>
    /// </remarks>
    public UpscalePlan Upscaling
    {
        get => _upscaling;

        set
        {
            UpscalePlan wanted = (value ?? UpscalePlan.None).Sane();

            if (wanted == _upscaling)
            {
                return;
            }

            _upscaling = wanted;

            // A backend that failed once is worth another try when the player changes the
            // setting: they may have changed it *because* it failed.
            _upscalerFailed = false;
            _needsRecreate = true;
        }
    }

    /// <summary>How the finished picture is encoded for the display.</summary>
    /// <remarks>
    /// Also settable at any time. A change of colour space needs a new swapchain and a new
    /// output pipeline, so it goes through the same rebuild; a change of paper white or of
    /// the sun's brightness needs neither and takes effect on the next frame.
    /// </remarks>
    public OutputPlan Output
    {
        get => _output;

        set
        {
            OutputPlan wanted = (value ?? OutputPlan.Standard).Sane();

            if (wanted == _output)
            {
                return;
            }

            bool rebuild = wanted.HighDynamicRange != _output.HighDynamicRange ||
                           wanted.Transfer != _output.Transfer;

            _output = wanted;

            if (rebuild)
            {
                _needsRecreate = true;
            }

            // The sun is packed into the rig on the way to the GPU, so a change to how
            // bright it burns has to re-upload it. Only when it actually changed: this is
            // a couple of hundred kilobytes and a light grid rebuild.
            _frames?.Relight(_output.SunGain);
            _rayTracedFrames?.Relight(_output.SunGain);
        }
    }

    /// <summary>
    /// Whether frames wait for the display.
    /// </summary>
    /// <remarks>
    /// <para>
    /// On means FIFO, which is the only present mode the specification guarantees exists
    /// and the only one that cannot tear. Off asks for mailbox and then for immediate, and
    /// quietly stays on FIFO where the surface offers neither — which is a real outcome on
    /// some Wayland compositors and is not worth failing over.
    /// </para>
    /// <para>
    /// Changing it needs a new swapchain, which is why it goes through the same rebuild as
    /// a resize rather than taking effect on the next frame.
    /// </para>
    /// </remarks>
    public bool VerticalSync
    {
        get;

        set
        {
            if (field == value)
            {
                return;
            }

            field = value;
            _needsRecreate = true;
        }
    } = true;

    /// <summary>What is actually upscaling, or "off".</summary>
    public string UpscalerName => _upscaler is { } running
        ? $"{running.Kind} ({running.Describe()})"
        : "off";

    /// <summary>Whether DLSS started and this device can run it.</summary>
    /// <remarks>
    /// Distinct from the files being installed. A GeForce older than Turing has all the
    /// files and none of the hardware, and the settings page should say which.
    /// </remarks>
    public bool DlssAvailable => _streamline is { Ready: true };

    /// <summary>Whether DLSS can denoise the traced light as well as upscale it.</summary>
    public bool DlssRayReconstruction => _streamline is { HasRayReconstruction: true };

    /// <summary>Why it cannot, when it looked as though it should be able to.</summary>
    public string DlssRayReconstructionNote =>
        _streamline?.RayReconstructionNote ?? string.Empty;

    /// <summary>Whether DLSS can generate frames.</summary>
    public bool DlssFrameGeneration => _streamline is { HasFrameGeneration: true };

    /// <summary>Whether the surface gave back a high dynamic range colour space.</summary>
    /// <remarks>
    /// Asked for is not got. A monitor in SDR mode, a compositor that does not pass HDR
    /// through, a driver that offers the extension and no HDR format: all of them leave
    /// this false with the setting on, and the settings page says so rather than leaving
    /// somebody to wonder why nothing looks different.
    /// </remarks>
    public bool HighDynamicRangeActive =>
        _colorSpace is ColorSpaceKHR.SpaceHdr10ST2084Ext or ColorSpaceKHR.SpaceExtendedSrgbLinearExt;

    /// <summary>The size the room is being drawn at, before any upscale.</summary>
    public (int Width, int Height) RenderSize =>
        ((int)_renderExtent.Width, (int)_renderExtent.Height);

    /// <summary>
    /// Says that the next frame has nothing to accumulate against.
    /// </summary>
    /// <remarks>
    /// Called by whoever changes what is on screen discontinuously: a new room, a camera
    /// cut, the end of a cutscene. Without it a temporal upscaler spends several frames
    /// reconciling the last room with this one, which reads as the new room arriving
    /// smeared.
    /// </remarks>
    public void ResetHistory() => _resetHistory = true;

    /// <summary>Sets the lights anything without baked lighting is lit by.</summary>
    /// <param name="lights">The rig the scene was authored with.</param>
    /// <param name="scene">What the geometry occupies; default decides nothing.</param>
    public void SetLights(
        IReadOnlyList<Formats.Scenes.AuthoredLight> lights, SceneExtent scene = default)
    {
        _frames?.SetLights(lights, scene);
        _rayTracedFrames?.SetLights(lights, scene);

        LightGrid = _frames?.Grid ?? _rayTracedFrames?.Grid;
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
        // A different room has nothing in common with the last one, so nothing a temporal
        // upscaler accumulated about the last one is worth keeping. Told here rather than
        // by the caller because this is the one call every room change goes through.
        if (!ReferenceEquals(scene, _scene))
        {
            _resetHistory = true;
        }

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
    /// <param name="bringUp">
    /// Whether to build the bring-up triangle, which a frame with nothing else to draw falls
    /// back to. For the smoke test that has nothing else to draw; the game never wants it.
    /// </param>
    /// <returns>The renderer.</returns>
    /// <param name="streamline">
    /// NVIDIA's loader, already started, or null. It has to be consulted while the device
    /// is being created — its features ask for extensions and queues — which is why it
    /// arrives here rather than being started when DLSS is first selected.
    /// </param>
    public static VulkanRenderer Create(
        IGameWindow window,
        IVulkanSurfaceSource surfaceSource,
        bool enableValidation = true,
        bool bringUp = false,
        Streamline? streamline = null)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(surfaceSource);

        var renderer = new VulkanRenderer(
            VulkanContext.LoadApi(), window, surfaceSource, bringUp, streamline);

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
            renderer.CreateUpscaleTarget();
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

        Jitter();

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
        _frameIndex++;
        _presentedAnything = true;

        // Whatever the last frame had to reconcile, it has now reconciled.
        _resetHistory = false;

        return true;
    }

    /// <summary>
    /// Moves this frame's sample point, and measures how long the last frame took.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only a temporal upscaler wants a jitter. Moving the camera by half a pixel with
    /// nothing accumulating the result is not anti-aliasing, it is a picture that wobbles —
    /// so with the spatial upscaler or none at all the offset is exactly zero and the
    /// motion vectors, which have this added back into them, are unchanged.
    /// </para>
    /// <para>
    /// Set on the camera the renderer was handed rather than on a copy. Everything in the
    /// frame has to agree about where it sampled — the raster, the depth, the traced
    /// occlusion and the reflections all reproject against each other — and one of them
    /// holding a different matrix is a class of error that looks like a denoiser bug.
    /// </para>
    /// </remarks>
    private void Jitter()
    {
        _secondsSinceLastFrame = (float)_sinceLastFrame.Elapsed.TotalSeconds;
        _sinceLastFrame.Restart();

        // A frame that took longer than a second is a load, a breakpoint or a machine that
        // went to sleep, and pacing anything against it produces nonsense.
        if (!float.IsFinite(_secondsSinceLastFrame) ||
            _secondsSinceLastFrame is <= 0f or > 1f)
        {
            _secondsSinceLastFrame = 1f / 60f;
        }

        if (!_upscaling.Temporal || _renderExtent.Width == 0)
        {
            _jitterPixels = Vector2.Zero;
        }
        else
        {
            int phases = JitterSequence.PhaseCount(
                (int)_renderExtent.Width, (int)_extent.Width);

            _jitterPixels = JitterSequence.Offset(_frameIndex, phases);
        }

        if (_camera is not null)
        {
            _camera.Jitter = JitterSequence.ToClip(
                _jitterPixels, (int)_renderExtent.Width, (int)_renderExtent.Height);
        }
    }

    /// <summary>Reads back the last frame that was presented.</summary>
    /// <returns>The image, or null if nothing has been presented yet.</returns>
    /// <remarks>
    /// <para>
    /// Copies out of the swapchain image rather than re-rendering, so what comes back is
    /// exactly what the player saw — including anything a re-render would get differently.
    /// </para>
    /// <para>
    /// <b>An HDR frame is brought back down.</b> A screenshot is an 8-bit sRGB file and
    /// there is no other kind; a ten-bit PQ frame or a half-float scRGB one is decoded, put
    /// back into a linear scale where paper white is one, and encoded for sRGB. What that
    /// loses is exactly what an HDR display was showing that an ordinary one cannot, which
    /// is unavoidable and worth stating: a screenshot taken in HDR is not a photograph of
    /// what was on the screen, it is the nearest ordinary picture to it.
    /// </para>
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

        // Four bytes a pixel for everything except the half-float surface scRGB is carried
        // in, which is eight. Getting this wrong is not a wrong colour, it is a buffer half
        // the size the copy needs.
        int stride = _format == Format.R16G16B16A16Sfloat ? 8 : 4;

        var bufferInfo = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = (ulong)(width * height * stride),
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

            byte[] raw = new byte[width * height * stride];
            void* mapped;
            _vk.MapMemory(_device, memory, 0, (ulong)raw.Length, 0, &mapped);
            new ReadOnlySpan<byte>(mapped, raw.Length).CopyTo(raw);
            _vk.UnmapMemory(_device, memory);

            byte[] pixels = HighDynamicRangeActive
                ? Ordinary(raw, width, height)
                : raw;

            // Most surfaces hand out a BGRA format; the decoded image is RGBA throughout.
            // The HDR paths above have already put their channels the right way round.
            if (!HighDynamicRangeActive &&
                _format is Format.B8G8R8A8Srgb or Format.B8G8R8A8Unorm)
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

    /// <summary>
    /// Turns a high dynamic range frame back into an ordinary 8-bit sRGB one.
    /// </summary>
    /// <param name="raw">The swapchain's own bytes.</param>
    /// <param name="width">Frame width.</param>
    /// <param name="height">Frame height.</param>
    /// <returns>Four bytes a pixel, RGBA, sRGB-encoded.</returns>
    /// <remarks>
    /// The exact inverse of what <see cref="OutputPipeline"/> did on the way out, followed
    /// by the sRGB encode the hardware would have done had the surface been an ordinary
    /// one. Anything above paper white clips, which is the whole point of the format it is
    /// being converted into.
    /// </remarks>
    private byte[] Ordinary(byte[] raw, int width, int height)
    {
        byte[] pixels = new byte[width * height * 4];
        float paperWhite = MathF.Max(_output.PaperWhiteNits, 1f);

        for (int i = 0; i < width * height; i++)
        {
            float r;
            float g;
            float b;

            if (_format == Format.R16G16B16A16Sfloat)
            {
                // scRGB: linear light in sRGB primaries, where one unit is 80 candelas.
                int at = i * 8;
                float scale = 80f / paperWhite;

                r = (float)BitConverter.ToHalf(raw, at) * scale;
                g = (float)BitConverter.ToHalf(raw, at + 2) * scale;
                b = (float)BitConverter.ToHalf(raw, at + 4) * scale;
            }
            else
            {
                // HDR10: ten bits a channel through ST.2084, in Rec.2020 primaries. The
                // pack order is A2B10G10R10, so red is the low ten bits.
                uint packed = BitConverter.ToUInt32(raw, i * 4);

                float wideRed = Luminance(packed & 0x3FF) / paperWhite;
                float wideGreen = Luminance((packed >> 10) & 0x3FF) / paperWhite;
                float wideBlue = Luminance((packed >> 20) & 0x3FF) / paperWhite;

                // Rec.2020 back to Rec.709, which is the inverse of the matrix the output
                // pass applied. Out-of-gamut colours come back negative and are clamped.
                r = (1.6605f * wideRed) - (0.5876f * wideGreen) - (0.0728f * wideBlue);
                g = (-0.1246f * wideRed) + (1.1329f * wideGreen) - (0.0083f * wideBlue);
                b = (-0.0182f * wideRed) - (0.1006f * wideGreen) + (1.1187f * wideBlue);
            }

            pixels[(i * 4) + 0] = Encode(r);
            pixels[(i * 4) + 1] = Encode(g);
            pixels[(i * 4) + 2] = Encode(b);
            pixels[(i * 4) + 3] = 255;
        }

        return pixels;
    }

    /// <summary>Undoes ST.2084, giving absolute luminance in candelas.</summary>
    private static float Luminance(uint tenBits)
    {
        const float M1 = 0.1593017578125f;
        const float M2 = 78.84375f;
        const float C1 = 0.8359375f;
        const float C2 = 18.8515625f;
        const float C3 = 18.6875f;

        float encoded = MathF.Pow(tenBits / 1023f, 1f / M2);
        float numerator = MathF.Max(encoded - C1, 0f);

        return 10_000f * MathF.Pow(numerator / (C2 - (C3 * encoded)), 1f / M1);
    }

    /// <summary>A linear value as an sRGB byte.</summary>
    private static byte Encode(float linear)
    {
        float value = Math.Clamp(linear, 0f, 1f);

        float encoded = value <= 0.0031308f
            ? value * 12.92f
            : (1.055f * MathF.Pow(value, 1f / 2.4f)) - 0.055f;

        return (byte)Math.Clamp(MathF.Round(encoded * 255f), 0f, 255f);
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

        int width = (int)_renderExtent.Width;
        int height = (int)_renderExtent.Height;
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

    /// <summary>
    /// How far the picture is faded out, from nought for the picture itself to one for
    /// nothing but <see cref="FadeColour"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Held on the renderer rather than passed to <see cref="DrawFrame"/> because the
    /// frames a fade covers are drawn from several places — the room's own loop, and the
    /// pump that keeps the window alive while the next room is being read — and every one
    /// of them has to agree about how dark the screen is.
    /// </para>
    /// <para>
    /// Clamped rather than checked: a fade driven from a clock will overshoot its end by
    /// however long the last frame took, and a transition is not the place to throw.
    /// </para>
    /// </remarks>
    public float Fade
    {
        get => _fade;
        set => _fade = Math.Clamp(value, 0f, 1f);
    }

    /// <summary>What the picture fades to. Black, unless something says otherwise.</summary>
    /// <remarks>
    /// Written straight into the target, which is sRGB — so this is the colour a picker
    /// would give rather than its linear form, and black is black either way. A white flash
    /// would want <see cref="OverlayPipeline"/>'s conversion; nothing asks for one yet.
    /// </remarks>
    public Vector3 FadeColour { get; set; }

    private float _fade;

    /// <summary>Gives the renderer an interface to draw on top of the room.</summary>
    /// <param name="atlas">The sheet it is drawn from.</param>
    /// <remarks>
    /// <para>
    /// Deferred rather than created with the renderer, because the sheet comes out of the
    /// game's archives and the renderer exists before anything has been read. Calling it
    /// again replaces the sheet, which is what changing font — or opening the menu, which
    /// is cut at its own size — means.
    /// </para>
    /// <para>
    /// <b>The pictures survive it.</b> The screens' own art hangs off the pipeline's
    /// descriptor pool, and this used to build a new pipeline with a new pool: the driving
    /// map's seventeen pictures were loaded once at startup and dropped the first time the
    /// front end drew, leaving the map to fall back to a list of names for the rest of the
    /// session. Only the sheet changes now.
    /// </para>
    /// </remarks>
    public void SetOverlayAtlas(OverlayAtlas atlas)
    {
        ArgumentNullException.ThrowIfNull(atlas);

        if (_shaderCompiler is null)
        {
            return;
        }

        _vk.DeviceWaitIdle(_device);

        if (_overlay is null)
        {
            _overlay = OverlayPipeline.Create(
                _context!, _format, SceneRenderer.DepthFormat, _shaderCompiler, atlas);
        }
        else
        {
            _overlay.SetAtlas(atlas);
        }

        _overlayAtlas = atlas;
    }

    /// <summary>
    /// Gives the interface one of the screens' own pictures, and says what to call it.
    /// </summary>
    /// <param name="name">What to look it up by.</param>
    /// <param name="image">The decoded picture.</param>
    /// <returns>Its number for <see cref="Overlay.Picture"/>, or zero if it could not be held.</returns>
    /// <remarks>
    /// The interface is drawn rather than blitted and stays that way. This is for the
    /// places where the game's own art <em>is</em> the content — the driving map is a
    /// painting of the countryside and no arrangement of rectangles is that.
    /// </remarks>
    public int AddOverlayPicture(string name, DecodedImage image)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (_overlay is null)
        {
            return 0;
        }

        if (_pictures.TryGetValue(name, out int already))
        {
            return already;
        }

        int number = _overlay.AddPicture(image);

        if (number > 0)
        {
            _pictures[name] = number;
        }

        return number;
    }

    /// <summary>Forgets the number a picture was given, so the next ask reloads it.</summary>
    /// <param name="name">What it was called.</param>
    /// <remarks>
    /// For a picture whose content has changed under its own name — a save slot written over
    /// with a new game. The picture already uploaded is left where it is: the interface's
    /// sheet grows by one and is thrown away with the room, which is a great deal simpler
    /// than freeing one entry out of the middle of it and costs a few hundred kilobytes in a
    /// session where somebody saved repeatedly over the same slot.
    /// </remarks>
    public void DropOverlayPicture(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        _pictures.Remove(name);
    }

    /// <summary>The number of a picture already given, or zero.</summary>
    /// <param name="name">What it was called.</param>
    /// <returns>Its number.</returns>
    public int OverlayPicture(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return _pictures.GetValueOrDefault(name);
    }

    /// <summary>
    /// Hands over the frame of a movie to draw over everything, or nothing to stop.
    /// </summary>
    /// <param name="frame">The picture, or null when the movie has finished.</param>
    /// <param name="cover">
    /// Whether to fill the window rather than letterbox the picture into it.
    /// </param>
    /// <remarks>
    /// The renderer knows nothing about what is playing or how far through it is: it is
    /// given a picture each frame and draws it, which keeps decoding, timing and sound out
    /// of the one place that has to keep up with the display.
    /// </remarks>
    public void SetMovieFrame(Formats.Bitmaps.DecodedImage? frame, bool cover = false)
    {
        if (frame is null)
        {
            _movie?.Clear();
            return;
        }

        if (_movie is null)
        {
            if (_context is null || _shaderCompiler is null)
            {
                return;
            }

            try
            {
                _movie = MoviePipeline.Create(_context, _shaderCompiler, _format);
            }
            catch (VulkanException error)
            {
                // Said out loud and once: a cutscene that silently does not appear looks
                // like the game having hung, and the sound plays either way.
                Log.Warning(
                    "WARNING GK3R3420: The movie pipeline could not be built, so cutscenes " +
                    "play without a picture. (" + error.Message + ")");

                return;
            }
        }

        _movie.Cover = cover;
        _movie.SetFrame(frame.Value);
    }

    /// <summary>Sets the picture behind the menu.</summary>
    /// <param name="picture">The image, or null to take it away.</param>
    /// <remarks>
    /// The same surface a cutscene uses, so whatever was set last is what shows — which is
    /// right, because a film and a title screen are never both wanted. It fills the window
    /// rather than being letterboxed into it.
    /// </remarks>
    public void SetBackdrop(Formats.Bitmaps.DecodedImage? picture) =>
        SetMovieFrame(picture, cover: true);

    /// <summary>Sets the picture behind the menu, from blocks.</summary>
    /// <param name="picture">The compressed image.</param>
    /// <remarks>
    /// What a shipped game has: the title screen comes out of a pack in the same form as
    /// every other texture, and nothing on the way here decompresses it.
    /// </remarks>
    public void SetBackdrop(Formats.Bitmaps.CompressedImage picture)
    {
        if (_movie is null)
        {
            if (_context is null || _shaderCompiler is null)
            {
                return;
            }

            try
            {
                _movie = MoviePipeline.Create(_context, _shaderCompiler, _format);
            }
            catch (VulkanException error)
            {
                Log.Warning(
                    "WARNING GK3R3420: The movie pipeline could not be built, so the menu "
                    + "has no picture behind it. (" + error.Message + ")");

                return;
            }
        }

        _movie.Cover = true;
        _movie.SetPicture(picture);
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

        // A display list carries the sheet it was cut from, and the interface has more
        // than one: the room's captions and the menu are drawn at different sizes from
        // different atlases. Uploading the one that arrived, rather than trusting whoever
        // called to have done it, is the difference between text and a row of fragments —
        // which is what sampling one atlas with another's coordinates looks like.
        if (!ReferenceEquals(overlay.Atlas, _overlayAtlas))
        {
            SetOverlayAtlas(overlay.Atlas);
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
            _movie?.Dispose();
            _skybox?.Dispose();
            _terrain?.Dispose();
            _overlay?.Dispose();
            _fadePipeline?.Dispose();
            _triangle?.Dispose();
            _shaderCompiler?.Dispose();
            DestroySynchronization();
            DestroyCommandResources();
            DestroyDepthBuffer();
            DestroyGBuffer();
            DestroySceneTarget();
            DestroyLitTarget();
            DestroyUpscaleTarget();
            _upscaler?.Dispose();
            _upscaler = null;
            _outputPipeline?.Dispose();
            _outputPipeline = null;
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

        // The window's own extensions, plus the one that lets a portability driver be
        // enumerated at all. Without it there is no device to find on macOS.
        IEnumerable<string> asked = _surfaceSource.RequiredInstanceExtensions;

        if (_streamline is { InstanceExtensions.Count: > 0 })
        {
            asked = asked.Concat(_streamline.InstanceExtensions);
        }

        string[] extensions = VulkanPortability.InstanceExtensions(
            _vk, asked, out InstanceCreateFlags flags);

        nint extensionNames = SilkMarshal.StringArrayToPtr(extensions);
        nint layerNames = 0;

        var createInfo = new InstanceCreateInfo
        {
            SType = StructureType.InstanceCreateInfo,
            PApplicationInfo = &applicationInfo,
            Flags = flags,
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

                // From the PCI identifier rather than from the name. Which upscalers the
                // settings page may offer hangs off this, and a card whose marketing string
                // changes between driver releases must not change what the menu shows.
                Vendor = properties.VendorID switch
                {
                    0x10DE => GpuVendor.Nvidia,
                    0x1002 or 0x1022 => GpuVendor.Amd,
                    0x8086 => GpuVendor.Intel,
                    0x106B => GpuVendor.Apple,
                    _ => GpuVendor.Unknown,
                };
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

        // Streamline runs some of its own work on queues it asks the application to
        // create. There is nowhere to add a queue after the fact — a device's queues are
        // fixed at creation — so this is the one chance to ask for them, and asking when
        // DLSS is merely *installed* rather than switched on is what lets it be switched
        // on later without restarting the game.
        uint extra = _streamline is null
            ? 0
            : _streamline.GraphicsQueuesWanted + _streamline.ComputeQueuesWanted;

        // And never more than the family actually has. A device that will not create the
        // queues is a device the game cannot draw on at all, which is a far worse outcome
        // than an upscaler that has to share one.
        extra = Math.Min(extra, QueuesAvailable(_graphicsFamily) - 1);
        _streamlineQueue = extra > 0 ? 1u : 0u;

        DeviceQueueCreateInfo[] queues = new DeviceQueueCreateInfo[families.Length];

        float* priorities = stackalloc float[(int)(extra + 1)];

        for (int i = 0; i <= extra; i++)
        {
            // The game's own queue first and highest: an upscaler starved of work is a
            // slower frame, and a renderer starved of work is a frozen window.
            priorities[i] = i == 0 ? 1f : 0.5f;
        }

        for (int i = 0; i < families.Length; i++)
        {
            queues[i] = new DeviceQueueCreateInfo
            {
                SType = StructureType.DeviceQueueCreateInfo,
                QueueFamilyIndex = families[i],
                QueueCount = families[i] == _graphicsFamily ? extra + 1 : 1,
                PQueuePriorities = priorities,
            };
        }

        // Ray tracing is enabled wherever the device offers it. Doing so costs nothing
        // while no rays are traced, and the alternative — recreating the device when the
        // quality setting changes — would mean rebuilding every resource with it.
        _rayTracingEnabled = VulkanContext.CanRayTrace(_vk, _physicalDevice);

        string[] wanted = _rayTracingEnabled
            ? [KhrSwapchain.ExtensionName, .. VulkanContext.RayTracingExtensions]
            : [KhrSwapchain.ExtensionName];

        if (_streamline is { DeviceExtensions.Count: > 0 })
        {
            wanted = [.. wanted, .. _streamline.DeviceExtensions];
        }

        // A portability driver requires its subset extension to be enabled wherever it is
        // advertised, and no driver that is not one advertises it.
        string[] names = VulkanPortability.DeviceExtensions(_vk, _physicalDevice, wanted);

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

        // TextureCompressionBC is what makes a BC5 or BC7 image legal to create, and
        // Apple silicon has none of it. Asking for a feature the device does not have
        // fails device creation outright, so only what is offered is asked for and the
        // texture path reads back which way it went.
        _capabilities = VulkanPortability.Query(_vk, _physicalDevice);
        PhysicalDeviceFeatures features = _capabilities.Requested();

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

        // And now Streamline can be told what was made. It asks the driver whether this
        // device can run DLSS at all, which is the answer the settings page reports.
        _streamline?.Attach(
            (nint)_instance.Handle,
            (nint)_physicalDevice.Handle,
            (nint)_device.Handle,
            _graphicsFamily,
            _streamlineQueue,
            _graphicsFamily,
            _streamlineQueue);
    }

    /// <summary>Which queue index in the graphics family Streamline was given.</summary>
    /// <remarks>Nought when there was no room for one of its own, and it shares.</remarks>
    private uint _streamlineQueue;

    /// <summary>How many queues a family has.</summary>
    private uint QueuesAvailable(uint family)
    {
        uint count = 0;
        _vk.GetPhysicalDeviceQueueFamilyProperties(_physicalDevice, ref count, null);

        if (family >= count)
        {
            return 1;
        }

        var properties = new QueueFamilyProperties[count];

        fixed (QueueFamilyProperties* pointer = properties)
        {
            _vk.GetPhysicalDeviceQueueFamilyProperties(_physicalDevice, ref count, pointer);
        }

        return Math.Max(1, properties[family].QueueCount);
    }

    private void CreateSwapchain()
    {
        _khrSurface.GetPhysicalDeviceSurfaceCapabilities(
            _physicalDevice, _surface, out SurfaceCapabilitiesKHR capabilities);

        SurfaceFormatKHR surfaceFormat = ChooseFormat();
        _format = surfaceFormat.Format;
        _colorSpace = surfaceFormat.ColorSpace;
        _extent = ChooseExtent(capabilities);

        // And the size the room is drawn at, which is the window's divided by whatever the
        // upscaler was asked for. Decided here rather than per frame because every target
        // between the first triangle and the upscale is built to it.
        (int drawnWidth, int drawnHeight) =
            _upscaling.RenderSize((int)_extent.Width, (int)_extent.Height);

        _renderExtent = new Extent2D((uint)drawnWidth, (uint)drawnHeight);

        // Nothing that accumulates across frames has anything worth keeping across a
        // swapchain rebuild: every target it was accumulating into has just been destroyed.
        _resetHistory = true;

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

            PresentMode = ChoosePresentMode(),
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

    /// <summary>
    /// Picks how frames are handed to the display.
    /// </summary>
    /// <returns>The present mode, which is FIFO unless the player asked otherwise.</returns>
    /// <remarks>
    /// FIFO is the only mode the specification guarantees, so it is both the default and
    /// the fallback. With the wait switched off, mailbox first — it is the one that does
    /// not tear — and immediate after it. A surface offering neither leaves the setting on
    /// in fact while the row says off, which is the honest outcome and is why the row does
    /// not promise a frame rate.
    /// </remarks>
    private PresentModeKHR ChoosePresentMode()
    {
        if (VerticalSync)
        {
            return PresentModeKHR.FifoKhr;
        }

        uint count = 0;
        _khrSurface.GetPhysicalDeviceSurfacePresentModes(_physicalDevice, _surface, ref count, null);

        if (count == 0)
        {
            return PresentModeKHR.FifoKhr;
        }

        var modes = new PresentModeKHR[count];

        fixed (PresentModeKHR* pointer = modes)
        {
            _khrSurface.GetPhysicalDeviceSurfacePresentModes(
                _physicalDevice, _surface, ref count, pointer);
        }

        if (Array.IndexOf(modes, PresentModeKHR.MailboxKhr) >= 0)
        {
            return PresentModeKHR.MailboxKhr;
        }

        return Array.IndexOf(modes, PresentModeKHR.ImmediateKhr) >= 0
            ? PresentModeKHR.ImmediateKhr
            : PresentModeKHR.FifoKhr;
    }

    /// <summary>
    /// Picks the swapchain's format and colour space from what the surface actually offers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Standard range is the easy half: an sRGB surface means the display hardware does the
    /// encoding on write, so shading stays linear and there is nothing to decide.
    /// </para>
    /// <para>
    /// High dynamic range is asked for and not demanded. A surface may offer the colour
    /// space and no format the game can write, a monitor may be in SDR mode, a compositor
    /// may not pass HDR through at all — and none of those is a reason to fail to open a
    /// window. What is chosen here is reported through
    /// <see cref="HighDynamicRangeActive"/>, so the settings page can say "asked for, not
    /// available" rather than leaving somebody to wonder why nothing changed.
    /// </para>
    /// <para>
    /// PQ before scRGB when both are offered and the player expressed no preference. Ten
    /// bits through ST.2084 carry further up the luminance range than ten bits of anything
    /// linear, and PQ is what a television and most HDR monitors are actually driven with.
    /// </para>
    /// </remarks>
    private SurfaceFormatKHR ChooseFormat()
    {
        uint count = 0;
        _khrSurface.GetPhysicalDeviceSurfaceFormats(_physicalDevice, _surface, ref count, null);

        SurfaceFormatKHR[] formats = new SurfaceFormatKHR[count];
        fixed (SurfaceFormatKHR* pointer = formats)
        {
            _khrSurface.GetPhysicalDeviceSurfaceFormats(_physicalDevice, _surface, ref count, pointer);
        }

        if (_output.HighDynamicRange)
        {
            if (Match(formats, _output.Transfer) is { } wide)
            {
                Log.Info($"Display: {wide.ColorSpace} in {wide.Format}");
                return wide;
            }

            // Said once, with what the surface actually offered. "HDR did nothing" is the
            // hardest kind of complaint to act on, and the difference between a monitor in
            // SDR mode, a compositor that will not pass HDR through and a driver that
            // never offered the format is only visible in this list.
            Log.Info(
                "Display: high dynamic range was asked for and this surface offers none. " +
                "It offers " + string.Join(
                    ", ",
                    formats.Select(f => f.ColorSpace.ToString()).Distinct()));
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

    /// <summary>The best HDR pair the surface offers, or null for none.</summary>
    /// <param name="formats">What the surface reported.</param>
    /// <param name="wanted">Which encoding the player asked for.</param>
    private static SurfaceFormatKHR? Match(SurfaceFormatKHR[] formats, HdrTransfer wanted)
    {
        // Ten bits for PQ and sixteen-bit float for scRGB, which is what each is defined
        // to be carried in. An 8-bit HDR10 surface would band visibly across a night sky
        // and is not worth taking over an sRGB one.
        SurfaceFormatKHR? pq = First(
            formats,
            ColorSpaceKHR.SpaceHdr10ST2084Ext,
            [Format.A2B10G10R10UnormPack32, Format.A2R10G10B10UnormPack32]);

        SurfaceFormatKHR? extended = First(
            formats,
            ColorSpaceKHR.SpaceExtendedSrgbLinearExt,
            [Format.R16G16B16A16Sfloat]);

        return wanted switch
        {
            HdrTransfer.PerceptualQuantiser => pq ?? extended,
            HdrTransfer.ExtendedLinear => extended ?? pq,
            _ => pq ?? extended,
        };
    }

    private static SurfaceFormatKHR? First(
        SurfaceFormatKHR[] formats, ColorSpaceKHR space, Format[] wanted)
    {
        foreach (Format candidate in wanted)
        {
            foreach (SurfaceFormatKHR format in formats)
            {
                if (format.ColorSpace == space && format.Format == candidate)
                {
                    return format;
                }
            }
        }

        return null;
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

    /// <summary>
    /// Records the whole of a frame: the room, the upscale, the encode and the interface.
    /// </summary>
    /// <param name="buffer">The frame's command buffer.</param>
    /// <param name="image">The swapchain image the frame is going to.</param>
    /// <param name="view">Its view.</param>
    /// <param name="r">Clear red.</param>
    /// <param name="g">Clear green.</param>
    /// <param name="b">Clear blue.</param>
    /// <remarks>
    /// <para>
    /// Four stages, in one order whatever the settings say. The room is drawn at
    /// <see cref="_renderExtent"/> into a floating-point target; something may then upscale
    /// that to <see cref="_extent"/>; the result is tone-mapped and encoded onto the
    /// swapchain; and the movie, the interface and the fade go on top at the size of the
    /// window.
    /// </para>
    /// <para>
    /// It used to be two orders — the traced path composited into a target and copied out,
    /// the plain path drew straight onto the screen — and every feature since has had to be
    /// written twice or has silently only worked on one of them. Unifying them costs one
    /// full-screen pass in the plain path and buys upscaling, HDR and tone mapping in both.
    /// The interface staying at the size of the window is not a detail: an interface drawn
    /// at render resolution and stretched with the room is the most visible way to get an
    /// upscaler wrong.
    /// </para>
    /// </remarks>
    private void RecordClear(CommandBuffer buffer, Image image, ImageView view, float r, float g, float b)
    {
        _vk.ResetCommandBuffer(buffer, 0);

        var begin = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };

        _vk.BeginCommandBuffer(buffer, in begin);

        PrepareDeferred(buffer);

        // Whether the room's pass produces a picture or only the raw materials of one.
        bool deferred = Quality != RayTracingQuality.None &&
                        _scene?.RayTracing is not null &&
                        _rayTracedPipeline is not null &&
                        _denoiser is not null &&
                        _composite is not null;

        int width = (int)_renderExtent.Width;
        int height = (int)_renderExtent.Height;

        RenderingAttachmentInfo* attachments =
            stackalloc RenderingAttachmentInfo[(int)GBuffer.Targets];

        Image roomImage = deferred ? _sceneImage : _litImage;
        ImageView roomView = deferred ? _sceneView : _litView;

        Transition(buffer, roomImage, ImageLayout.Undefined, ImageLayout.ColorAttachmentOptimal);

        attachments[GBuffer.Colour] = new RenderingAttachmentInfo
        {
            SType = StructureType.RenderingAttachmentInfo,
            ImageView = roomView,
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
            RenderArea = new Rect2D { Extent = _renderExtent },
            LayerCount = 1,
            ColorAttachmentCount = GBuffer.Targets,
            PColorAttachments = attachments,
            PDepthAttachment = _depthView.Handle != 0 ? &depthAttachment : null,
        };

        TransitionDepth(buffer);
        _vk.CmdBeginRendering(buffer, in rendering);

        // The room's sky, built the first time this geometry is drawn. It needs the shader
        // compiler and the formats, which the geometry does not have.
        if (_scene is not null && !ReferenceEquals(_scene, _skyOwner))
        {
            _skyOwner = _scene;
            _skybox?.Dispose();
            _skybox = null;
            _terrain?.Dispose();
            _terrain = null;

            if (_scene.SkyboxFaces is { Count: 6 } faces && _shaderCompiler is not null)
            {
                try
                {
                    _skybox = SkyboxPipeline.Create(
                        _context!, GBuffer.LightFormat, SceneRenderer.DepthFormat, _shaderCompiler,
                        faces, _scene.SkyboxAzimuth);
                }
                catch (VulkanException)
                {
                    // A room without a sky is a room; a room that will not draw is not.
                    _skybox = null;
                }
            }

            if (_scene.Terrain is { } backdrop && _shaderCompiler is not null)
            {
                try
                {
                    _terrain = TerrainPipeline.Create(
                        _context!, GBuffer.LightFormat, SceneRenderer.DepthFormat, _shaderCompiler,
                        backdrop);
                }
                catch (VulkanException)
                {
                    // The painted sky is still there behind it, so a horizon that will
                    // not build is a horizon the player already had.
                    _terrain = null;
                }
            }
        }

        if (_scene is not null && _camera is not null && _meshPipeline is not null && _frames is not null)
        {
            // The same condition the attachments were chosen by, and it has to be: the
            // ray-traced pipeline writes light into a target of its own rather than a
            // picture onto the screen, so using it without the pass that finishes the
            // frame leaves the rig's light in a target nothing reads and the room lit by
            // its ambient floor alone. When the compositing stages could not be built,
            // the plain pipeline is what makes the warning true — the room draws with the
            // lighting it had before any of this existed.
            bool tracing = deferred && _rayTracedFrames is not null;

            MeshPipeline pipeline = tracing ? _rayTracedPipeline! : _meshPipeline;
            FrameUniformSet frames = tracing ? _rayTracedFrames! : _frames;

            frames.Seconds = (float)_wind.Elapsed.TotalSeconds;

            // Where inside its pixel this frame samples, and how far above white a lamp is
            // allowed to burn. Both are per-frame facts about presentation rather than
            // about the room, which is why they are set here and not by whoever loaded it.
            frames.JitterPixels = _jitterPixels;
            frames.EmissiveGain = _output.EmissiveGain;

            if (tracing)
            {
                frames.Settings = RayTracingSettings.For(Quality);
            }

            SceneDraw.Record(
                _vk, buffer, pipeline, frames, _scene, _frame, width, height, _camera);
        }
        else if (_triangle is not null)
        {
            // Only the smoke test builds one. See the field: a game frame with no room in it
            // draws the clear colour and whatever is over it, not this.

            var viewport = new Viewport
            {
                Width = width,
                Height = height,
                MaxDepth = 1f,
            };

            var scissor = new Rect2D { Extent = _renderExtent };

            _vk.CmdSetViewport(buffer, 0, 1, in viewport);
            _vk.CmdSetScissor(buffer, 0, 1, in scissor);
            _vk.CmdBindPipeline(buffer, PipelineBindPoint.Graphics, _triangle.Handle);
            _vk.CmdDraw(buffer, 3, 1, 0, 0);
        }

        // The horizon after the room, and only where this scope is producing the picture.
        // The traced path has a compositing pass to run first and draws its sky there,
        // over the result, because the sky is not something the compositing pass has any
        // parts for.
        if (!deferred && _camera is not null)
        {
            // The reconstructed backdrop brings its own sky, and the painted cubemap must
            // not draw behind it — its mountains are baked into the picture and would
            // double-expose against the real ridge. The cubemap is the fallback for a
            // backdrop that would not build, nothing more.
            if (_terrain is not null)
            {
                _terrain.Record(buffer, _camera, width, height);
            }
            else
            {
                _skybox?.Record(buffer, _camera, width, height);
            }
        }

        _vk.CmdEndRendering(buffer);

        // Where everything was drawn, ready for the next frame's motion vectors.
        _scene?.Advance();

        if (deferred)
        {
            // Leaves the lit target holding the finished room, in shader-read layout.
            Compose(buffer);
        }
        else
        {
            Transition(
                buffer, _litImage, ImageLayout.ColorAttachmentOptimal,
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
        }

        _litSettled = true;

        ImageView picture = Upscale(buffer) ? _upscaledView : _litView;

        Present(buffer, image, view, picture);

        Transition(buffer, image, ImageLayout.ColorAttachmentOptimal, ImageLayout.PresentSrcKhr);

        if (_vk.EndCommandBuffer(buffer) != Result.Success)
        {
            throw new VulkanException("Could not record the frame.");
        }
    }

    /// <summary>
    /// Runs whatever upscaler the plan asks for, building it if this is the first frame.
    /// </summary>
    /// <param name="buffer">The frame's command buffer.</param>
    /// <returns>True when the upscaled target holds the picture.</returns>
    /// <remarks>
    /// <para>
    /// Built here rather than at startup for the same reason the denoiser is: it needs to
    /// know the two sizes, and the player can change what it is at any moment. A backend
    /// that will not build, or that declines a frame, is logged once and switched off for
    /// the rest of the session — the fallback is the picture at render resolution, stretched
    /// by the output pass, which is worse and is not nothing.
    /// </para>
    /// <para>
    /// Every backend is handed its inputs in shader-read layout and its output in general
    /// layout, and this is the only place that decides so. See <see cref="IUpscaler"/>.
    /// </para>
    /// </remarks>
    private bool Upscale(CommandBuffer buffer)
    {
        if (!_upscaling.Active || _upscalerFailed || _context is null ||
            _shaderCompiler is null || _upscaledImage.Handle == 0)
        {
            return false;
        }

        if (_upscaler is not null && !_upscaler.Serves(_upscaling, _renderExtent, _extent))
        {
            _upscaler.Dispose();
            _upscaler = null;
        }

        if (_upscaler is null)
        {
            try
            {
                _upscaler = BuildUpscaler();
            }
            catch (Exception error) when (error is VulkanException or DllNotFoundException
                                              or EntryPointNotFoundException or BadImageFormatException)
            {
                Log.Warning(
                    "WARNING GK3R3431: " + _upscaling.Kind + " could not be started, so the "
                    + "picture is drawn at the size of the window. (" + error.Message + ")");

                _upscaler = null;
                _upscalerFailed = true;

                return false;
            }

            if (_upscaler is null)
            {
                _upscalerFailed = true;
                return false;
            }

            Log.Info($"Upscaling: {UpscalerName}");
            _resetHistory = true;
        }

        _context.Transition(
            buffer, _upscaledImage, ImageLayout.Undefined, ImageLayout.General);

        var frame = new UpscaleFrame(
            new UpscaleImage(
                _litImage, _litView, GBuffer.LightFormat, _renderExtent,
                ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit |
                ImageUsageFlags.StorageBit | ImageUsageFlags.TransferSrcBit),
            new UpscaleImage(
                _depthImage, _depthView, SceneRenderer.DepthFormat, _renderExtent,
                ImageUsageFlags.DepthStencilAttachmentBit | ImageUsageFlags.SampledBit),
            new UpscaleImage(
                _extraImages[GBuffer.Motion - 1], _extraViews[GBuffer.Motion - 1],
                GBuffer.MotionFormat, _renderExtent,
                ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit |
                ImageUsageFlags.TransferSrcBit),
            new UpscaleImage(
                _upscaledImage, _upscaledView, GBuffer.LightFormat, _extent,
                ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit |
                ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferSrcBit |
                ImageUsageFlags.TransferDstBit),
            _jitterPixels,
            _secondsSinceLastFrame,
            _resetHistory,
            _camera,
            _renderExtent.Height > 0 ? _renderExtent.Width / (float)_renderExtent.Height : 1f,
            _upscaling.Sharpen,
            _upscaling.Sharpness,
            HighDynamicRangeActive);

        bool worked;

        try
        {
            worked = _upscaler.Record(buffer, in frame);
        }
        catch (Exception error) when (error is VulkanException or InvalidOperationException)
        {
            Log.Warning(
                "WARNING GK3R3432: " + _upscaling.Kind + " stopped upscaling, so the picture "
                + "is drawn at the size of the window. (" + error.Message + ")");

            worked = false;
        }

        if (!worked)
        {
            _upscaler.Dispose();
            _upscaler = null;
            _upscalerFailed = true;

            return false;
        }

        _context.Transition(
            buffer, _upscaledImage, ImageLayout.General, ImageLayout.ShaderReadOnlyOptimal);

        return true;
    }

    /// <summary>Makes the upscaler the plan asks for.</summary>
    /// <returns>It, or null when its runtime is not installed.</returns>
    private IUpscaler? BuildUpscaler() => _upscaling.Kind switch
    {
        UpscalerKind.Spatial =>
            SpatialUpscaler.Create(_context!, _shaderCompiler!, _renderExtent, _extent),

        UpscalerKind.Fsr =>
            (IUpscaler?)FsrUpscaler.TryCreate(
                _context!, Runtimes, _upscaling, _renderExtent, _extent) ??
            Fallback(UpscalerKind.Fsr, UpscalerRuntimes.FidelityFx),

        UpscalerKind.Dlss =>
            (IUpscaler?)DlssUpscaler.TryCreate(
                _context!, Runtimes, _upscaling, _renderExtent, _extent, _streamline,
                tracing: Quality != RayTracingQuality.None && _scene?.RayTracing is not null) ??
            Fallback(UpscalerKind.Dlss, UpscalerRuntimes.StreamlineInterposer),

        _ => null,
    };

    /// <summary>
    /// Says why a vendor upscaler is not running, and uses the engine's own instead.
    /// </summary>
    /// <param name="named">What the player asked for.</param>
    /// <param name="wanted">The file that would have made it possible.</param>
    /// <returns>The spatial upscaler, told which backend it is standing in for.</returns>
    /// <remarks>
    /// <para>
    /// Falling back rather than switching off, because the player asked for the picture to
    /// be drawn small and stretched, and the engine can do that without anybody's runtime.
    /// What they do not get is the quality they were expecting, which is why this is said
    /// out loud and why the settings page says the same thing where they can read it.
    /// </para>
    /// <para>
    /// The stand-in is told what it is standing in for. Without that it answers "no" when
    /// asked whether it serves a plan that names DLSS, and the frame loop dutifully tears
    /// it down and builds another one — every frame, with a warning each time.
    /// </para>
    /// </remarks>
    private SpatialUpscaler Fallback(UpscalerKind named, string wanted)
    {
        // Two quite different reasons, and the player can only act on one of them. A
        // runtime that is not there is a download; a runtime that is there and declined is
        // a card, a driver or a bug, and telling somebody to copy a file they have already
        // copied is the least useful thing this could say.
        bool installed = Runtimes?.For(named).Present ?? false;

        Log.Warning(installed
            ? $"WARNING GK3R3433: {named} is installed but would not start on this device, "
              + "so the built-in upscaler is used instead."
            : $"WARNING GK3R3433: {named} was chosen but {wanted} was not found in "
              + $"{UpscalerRuntimes.LibraryDirectory}, so the built-in upscaler is used instead.");

        return SpatialUpscaler.Create(
            _context!, _shaderCompiler!, _renderExtent, _extent, named);
    }

    /// <summary>
    /// Puts the finished picture on the screen, with the movie, the interface and the fade
    /// over it.
    /// </summary>
    /// <param name="buffer">The frame's command buffer.</param>
    /// <param name="image">The swapchain image.</param>
    /// <param name="view">Its view.</param>
    /// <param name="picture">The linear frame to encode, at the size of the window.</param>
    /// <remarks>
    /// All four in one rendering scope. The encode covers every pixel, so there is nothing
    /// to load; everything after it blends over what it wrote.
    /// </remarks>
    private void Present(CommandBuffer buffer, Image image, ImageView view, ImageView picture)
    {
        Transition(buffer, image, ImageLayout.Undefined, ImageLayout.ColorAttachmentOptimal);

        if (_outputPipeline is null)
        {
            // Nothing to encode with. The room still reaches the screen — a blit converts
            // the format and scales — and the tone curve and the HDR encode are what is
            // lost. Better than a black window, which is the only other answer.
            Blit(
                buffer,
                image,
                picture.Handle == _upscaledView.Handle ? _upscaledImage : _litImage);
        }
        else
        {
            if (_outputSource.Handle != picture.Handle)
            {
                _outputPipeline.Bind(picture);
                _outputSource = picture;
            }
        }

        var attachment = new RenderingAttachmentInfo
        {
            SType = StructureType.RenderingAttachmentInfo,
            ImageView = view,
            ImageLayout = ImageLayout.ColorAttachmentOptimal,

            // The encode covers the frame; a blit already did if there was no encode.
            LoadOp = _outputPipeline is null ? AttachmentLoadOp.Load : AttachmentLoadOp.DontCare,
            StoreOp = AttachmentStoreOp.Store,
        };

        var rendering = new RenderingInfo
        {
            SType = StructureType.RenderingInfo,
            RenderArea = new Rect2D { Extent = _extent },
            LayerCount = 1,
            ColorAttachmentCount = 1,
            PColorAttachments = &attachment,
        };

        // Everything drawn after the room has to encode itself the same way the room was
        // encoded, because on an HDR surface there is no hardware encode to fall back on.
        DisplayEncode display = Encoding();

        if (_overlay is not null)
        {
            _overlay.Display = display;
        }

        if (_movie is not null)
        {
            _movie.Display = display;
        }

        if (_fadePipeline is not null)
        {
            _fadePipeline.Display = display;
        }

        _vk.CmdBeginRendering(buffer, in rendering);

        _outputPipeline?.Record(
            buffer,
            (int)_extent.Width,
            (int)_extent.Height,
            new OutputConstants(
                new Vector4(
                    display.Transfer,
                    display.PaperWhite,
                    display.Headroom,
                    (float)_output.ToneMap),

                // Only the engine's own upscaler leaves the sharpening to this pass. The
                // vendors' runtimes have their own, tuned against their own accumulation,
                // and running a second one over the top is how a picture ends up crunchy.
                new Vector4(
                    _upscaling.Sharpen && _upscaling.Kind is UpscalerKind.Spatial or UpscalerKind.Off
                        ? _upscaling.Sharpness
                        : 0f,
                    _extent.Width > 0 ? 1f / _extent.Width : 0f,
                    _extent.Height > 0 ? 1f / _extent.Height : 0f,
                    0f)));

        // Over the room and under the interface. A movie covers the window, so what is
        // behind it does not matter; the captions that go with one do.
        _movie?.Record(buffer, (int)_extent.Width, (int)_extent.Height);

        // On top of everything and at the size of the window, never at the size the room
        // was drawn at.
        _overlay?.Record(buffer, (int)_extent.Width, (int)_extent.Height);

        // And last of all, over the interface as well as the room. See Fade.
        RecordFade(buffer);

        _vk.CmdEndRendering(buffer);
    }

    /// <summary>
    /// What every pass writing the swapchain has to do to its colours.
    /// </summary>
    /// <remarks>
    /// One answer, derived from the colour space the surface actually gave back rather than
    /// from what was asked for. A frame where the room encoded for HDR10 and the interface
    /// did not is not a subtle mismatch: it is a correct picture with a washed-out menu over
    /// it, which is what it looked like before this existed.
    /// </remarks>
    private DisplayEncode Encoding() => HighDynamicRangeActive
        ? new DisplayEncode(
            _colorSpace == ColorSpaceKHR.SpaceHdr10ST2084Ext
                ? OutputPipeline.TransferPerceptualQuantiser
                : OutputPipeline.TransferExtendedLinear,
            _output.PaperWhiteNits,
            _output.Headroom)
        : DisplayEncode.Standard;

    /// <summary>Copies a picture onto the swapchain, scaling and converting as it goes.</summary>
    /// <param name="buffer">The frame's command buffer.</param>
    /// <param name="image">The swapchain image.</param>
    /// <param name="source">The picture, in shader-read layout.</param>
    /// <remarks>
    /// The path taken only when the output pass could not be built. A blit rather than a
    /// copy because the two differ in format and may differ in size.
    /// </remarks>
    private void Blit(CommandBuffer buffer, Image image, Image source)
    {
        if (source.Handle == 0)
        {
            return;
        }

        Extent2D from = source.Handle == _upscaledImage.Handle ? _extent : _renderExtent;

        _context!.Transition(
            buffer, source, ImageLayout.ShaderReadOnlyOptimal, ImageLayout.TransferSrcOptimal);

        Transition(
            buffer, image, ImageLayout.ColorAttachmentOptimal, ImageLayout.TransferDstOptimal);

        var region = new ImageBlit
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
        };

        region.SrcOffsets.Element1 = new Offset3D((int)from.Width, (int)from.Height, 1);
        region.DstOffsets.Element1 = new Offset3D((int)_extent.Width, (int)_extent.Height, 1);

        _vk.CmdBlitImage(
            buffer,
            source,
            ImageLayout.TransferSrcOptimal,
            image,
            ImageLayout.TransferDstOptimal,
            1,
            in region,
            Filter.Linear);

        Transition(
            buffer, image, ImageLayout.TransferDstOptimal, ImageLayout.ColorAttachmentOptimal);

        _context.Transition(
            buffer, source, ImageLayout.TransferSrcOptimal, ImageLayout.ShaderReadOnlyOptimal);
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
                Log.Warning(
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
                _denoiser.DynamicShadow,
                _reflections.Buffers);

            _composed = true;
        }

        _denoiser.Point(_scene.RayTracing.Handle);
    }

    /// <summary>Traces the occlusion, filters it, and puts the picture together.</summary>
    /// <param name="buffer">Command buffer being recorded.</param>
    /// <remarks>
    /// Between the room's pass and the upscale: the tracing reads the depth and the normals
    /// the first one wrote, which cannot be sampled while they are still attachments, and
    /// the sky belongs on top of what this produces rather than underneath it.
    /// <para>
    /// It leaves the lit target holding the finished room in shader-read layout, which is
    /// where the upscale and the encode expect to find it — and where the <em>next</em>
    /// frame's reflections expect to find last frame's picture.
    /// </para>
    /// </remarks>
    private void Compose(CommandBuffer buffer)
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
        // in shader-read layout, so on every frame but the first there is nothing to do.
        if (!_litSettled)
        {
            Transition(
                buffer, _litImage, ImageLayout.Undefined, ImageLayout.ShaderReadOnlyOptimal);
        }

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
            // next frame has something to reflect and this one has something to upscale.
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
            StoreOp = AttachmentStoreOp.Store,
        };

        var rendering = new RenderingInfo
        {
            SType = StructureType.RenderingInfo,
            RenderArea = new Rect2D { Extent = _renderExtent },
            LayerCount = 1,
            ColorAttachmentCount = 1,
            PColorAttachments = &attachment,
            PDepthAttachment = _depthView.Handle != 0 ? &depthAttachment : null,
        };

        int width = (int)_renderExtent.Width;
        int height = (int)_renderExtent.Height;

        _vk.CmdBeginRendering(buffer, in rendering);

        _composite!.Record(
            buffer, width, height, _reflections.Parity, settings.OcclusionStrength);

        if (_camera is not null)
        {
            if (_terrain is not null)
            {
                _terrain.Record(buffer, _camera, width, height);
            }
            else
            {
                _skybox?.Record(buffer, _camera, width, height);
            }
        }

        _vk.CmdEndRendering(buffer);

        Transition(
            buffer, _litImage, ImageLayout.ColorAttachmentOptimal,
            ImageLayout.ShaderReadOnlyOptimal);

        // And the depth, for whatever upscales this. Left as an attachment it is not
        // something a compute shader or a vendor runtime may read, and the one that
        // notices is the one that produces a frame of noise rather than an error.
        _context.Transition(
            buffer, _depthImage, ImageLayout.DepthStencilAttachmentOptimal,
            ImageLayout.ShaderReadOnlyOptimal, ImageAspectFlags.DepthBit);
    }

    /// <summary>Draws the fade over whatever the frame ended up as.</summary>
    /// <param name="buffer">Command buffer being recorded.</param>
    /// <remarks>
    /// Both places the interface is recorded call this straight afterwards, because a fade
    /// covers the whole picture and the picture is finished in two different passes
    /// depending on whether the room was traced.
    /// </remarks>
    private void RecordFade(CommandBuffer buffer)
    {
        if (_fadePipeline is null || _fade <= 0f)
        {
            return;
        }

        _fadePipeline.Record(
            buffer,
            (int)_extent.Width,
            (int)_extent.Height,
            new Vector4(FadeColour, _fade));
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
        // Compiled shaders are cached beside the executable - or under the user's own
        // directory when the install is read-only - so the compiler runs only when a
        // shader actually changes.
        _shaderCompiler = new ShaderCompiler(ShaderCompiler.DefaultCacheDirectory);

        if (_bringUp)
        {
            _triangle = TrianglePipeline.Create(_vk, _device, _format, _shaderCompiler);
        }

        _context = VulkanContext.Adopt(
            _vk, _instance, _physicalDevice, _device, _graphicsQueue, _graphicsFamily,
            _commandPool, DeviceName, _rayTracingEnabled, _capabilities);

        // Light, not a picture — the same reason the ray-traced pipeline below says so.
        // The room is drawn into a floating-point target now whether or not it is traced,
        // because that is the only form an upscaler or an HDR encode can use, and the
        // swapchain's own format is nothing this pipeline ever writes.
        _meshPipeline = MeshPipeline.Create(
            _context, GBuffer.LightFormat, SceneRenderer.DepthFormat, _shaderCompiler);

        _frames = FrameUniformSet.Create(_context, _meshPipeline, FramesInFlight);

        RebuildForFormat();

        if (_context.SupportsRayTracing)
        {
            // Light, not a picture. The ray-traced room writes half its lighting into the
            // scene target, which is GBuffer.LightFormat because those values run past
            // white; declaring the swapchain's format here instead described a pipeline
            // that never ran against a target of that format.
            _rayTracedPipeline = MeshPipeline.Create(
                _context, GBuffer.LightFormat, SceneRenderer.DepthFormat, _shaderCompiler,
                rayTracing: true);

            _rayTracedFrames = FrameUniformSet.Create(_context, _rayTracedPipeline, FramesInFlight);
        }
    }

    /// <summary>What the swapchain's format was when the passes that write it were built.</summary>
    private Format _builtForFormat = Format.Undefined;

    /// <summary>
    /// Builds, or rebuilds, everything that writes straight onto the swapchain.
    /// </summary>
    /// <remarks>
    /// A graphics pipeline carries the format of the attachment it writes, so the four
    /// passes that end up on the swapchain — the encode, the fade, the interface and a
    /// movie — have to be rebuilt when that format changes. Which it does exactly once in a
    /// session, when somebody turns HDR on: an 8-bit sRGB surface becomes a ten-bit one and
    /// every pipeline built for the first is invalid against the second.
    /// </remarks>
    private void RebuildForFormat()
    {
        if (_context is null || _shaderCompiler is null || _format == _builtForFormat)
        {
            return;
        }

        _vk.DeviceWaitIdle(_device);

        _outputPipeline?.Dispose();
        _outputPipeline = null;
        _outputSource = default;

        _fadePipeline?.Dispose();
        _fadePipeline = null;

        _movie?.Dispose();
        _movie = null;

        if (_bringUp)
        {
            _triangle?.Dispose();
            _triangle = TrianglePipeline.Create(_vk, _device, _format, _shaderCompiler);
        }

        try
        {
            _outputPipeline = OutputPipeline.Create(_context, _shaderCompiler, _format);
        }
        catch (VulkanException error)
        {
            // Without it there is no picture at all: it is the pass that puts the frame on
            // the screen. Said loudly, and the frame falls back to a straight copy — which
            // cannot tone-map or encode, but does show the room.
            Log.Warning(
                "WARNING GK3R3430: The output pass could not be built, so the picture is "
                + "copied to the screen without tone mapping. (" + error.Message + ")");

            _outputPipeline = null;
        }

        // Built at startup rather than the first time something fades, because the first
        // time something fades is a scene change — the one moment in the game where a
        // shader compile would be a stall the player sees. It is one triangle and no
        // descriptors, so building it always costs nothing.
        try
        {
            _fadePipeline = FadePipeline.Create(
                _context, _format, SceneRenderer.DepthFormat, _shaderCompiler);
        }
        catch (VulkanException error)
        {
            // A transition that cuts rather than fades is a transition. Losing the room
            // over it would not be.
            Log.Warning(
                "WARNING GK3R3421: The fade pipeline could not be built, so scene changes "
                + "cut rather than fade. (" + error.Message + ")");

            _fadePipeline = null;
        }

        // The interface's pipeline carries the format too, but the interface owns the
        // sheet of letters and every picture the screens have handed it. Retargeting
        // rebuilds only the pipeline and keeps all of that: disposing it would leave the
        // save menu drawing blank squares where the thumbnails were, with nothing left
        // that knew to put them back.
        _overlay?.Retarget(_format, SceneRenderer.DepthFormat);

        _builtForFormat = _format;
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
                Extent = new Extent3D(_renderExtent.Width, _renderExtent.Height, 1),
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
            Extent = new Extent3D(_renderExtent.Width, _renderExtent.Height, 1),
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

    /// <summary>
    /// Builds the finished room, in linear light and at the size it was drawn.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Floating point rather than the swapchain's format, and that is the change everything
    /// else here rests on. A ray-traced highlight, a lamp on an HDR display and a temporal
    /// upscaler's history all need values above one to survive to the end of the frame, and
    /// an 8-bit target clips every one of them at white. It is also what the two vendor
    /// runtimes expect to be handed.
    /// </para>
    /// <para>
    /// Both paths write into this now — the traced one through the compositing pass and the
    /// plain one directly — which is what lets the upscale and the encode be one place
    /// rather than two. The interface is emphatically not in here: it is drawn afterwards,
    /// onto the swapchain, at the size of the window.
    /// </para>
    /// </remarks>
    private void CreateLitTarget()
    {
        var imageInfo = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = GBuffer.LightFormat,
            Extent = new Extent3D(_renderExtent.Width, _renderExtent.Height, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,

            // Storage as well, because an upscaler that needs no upscaling to do — DLAA, or
            // a ratio of one — may be pointed straight at it.
            Usage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit |
                    ImageUsageFlags.StorageBit | ImageUsageFlags.TransferSrcBit,
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
            Format = GBuffer.LightFormat,
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

    /// <summary>
    /// Builds the image an upscaler fills, at the size of the window.
    /// </summary>
    /// <remarks>
    /// Only when something is upscaling. With no upscaler the lit target is already the
    /// size of the window and the output pass reads it directly — allocating a second
    /// full-resolution float image to copy it into would cost 32 MB at 4K and a resample
    /// nobody asked for.
    /// </remarks>
    private void CreateUpscaleTarget()
    {
        if (!_upscaling.Active)
        {
            return;
        }

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

            // Storage for the compute paths to write, sampled for the output pass to read,
            // and a colour attachment because AMD's runtime asks what a resource may be
            // used as and declines to write into one that says it cannot be written.
            Usage = ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit |
                    ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferSrcBit |
                    ImageUsageFlags.TransferDstBit,
            InitialLayout = ImageLayout.Undefined,
        };

        if (_vk.CreateImage(_device, in imageInfo, null, out _upscaledImage) != Result.Success)
        {
            throw new VulkanException("Could not create the upscaled target.");
        }

        _vk.GetImageMemoryRequirements(_device, _upscaledImage, out MemoryRequirements requirements);

        _upscaledMemory = AllocateDepthMemory(requirements);
        _vk.BindImageMemory(_device, _upscaledImage, _upscaledMemory, 0);

        var viewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = _upscaledImage,
            ViewType = ImageViewType.Type2D,
            Format = GBuffer.LightFormat,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                LevelCount = 1,
                LayerCount = 1,
            },
        };

        if (_vk.CreateImageView(_device, in viewInfo, null, out _upscaledView) != Result.Success)
        {
            throw new VulkanException("Could not create the upscaled target's view.");
        }
    }

    private void DestroyUpscaleTarget()
    {
        if (_upscaledView.Handle != 0)
        {
            _vk.DestroyImageView(_device, _upscaledView, null);
            _upscaledView = default;
        }

        if (_upscaledImage.Handle != 0)
        {
            _vk.DestroyImage(_device, _upscaledImage, null);
            _upscaledImage = default;
        }

        if (_upscaledMemory.Handle != 0)
        {
            _vk.FreeMemory(_device, _upscaledMemory, null);
            _upscaledMemory = default;
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
            Extent = new Extent3D(_renderExtent.Width, _renderExtent.Height, 1),
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

        // The upscaler holds a history at the old size and descriptors pointing at images
        // about to be destroyed. Both go; the next frame builds whatever the plan now asks
        // for, which is also how a change of upscaler from the settings page takes effect.
        _upscaler?.Dispose();
        _upscaler = null;
        _outputSource = default;

        DestroyDepthBuffer();
        DestroyGBuffer();
        DestroySceneTarget();
        DestroyLitTarget();
        DestroyUpscaleTarget();
        DestroySwapchain();
        CreateSwapchain();
        CreateDepthBuffer();
        CreateGBuffer();
        CreateSceneTarget();
        CreateLitTarget();
        CreateUpscaleTarget();

        // A pipeline carries the format it writes into, so one built against an 8-bit sRGB
        // swapchain cannot write a 10-bit HDR one. Rebuilt only when the format actually
        // changed, which is the difference between switching HDR on and dragging a corner.
        if (_format != _builtForFormat)
        {
            RebuildForFormat();
        }

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
