// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Platform;
using GK3Reborn.Rendering.Geometry;
using GK3Reborn.Rendering.Shaders;
using GK3Reborn.Rendering.Upscaling;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;
using System.Numerics;

namespace GK3Reborn.Rendering.Direct3D12;

/// <summary>
/// Draws and presents frames to a window, on Direct3D.
/// </summary>
/// <remarks>
/// <para>
/// The twin of <c>VulkanRenderer</c>. The room, the tracing and the upscale are
/// <see cref="D3D12FramePipeline"/>, shared with the headless renderer; what is here is
/// everything that only matters when there is a window — the swapchain, the encode onto it,
/// and the four things drawn over the room in the order a player sees them: the sky behind,
/// the film over, the interface over that, and the fade over everything.
/// </para>
/// <para>
/// <b>Every pass that writes the swapchain is built for the swapchain's format.</b> A
/// Direct3D pipeline names its render target formats when it is created, and the swapchain's
/// format changes when the window moves onto an HDR display or off one. So the four are
/// rebuilt together when it does, and nothing outlives a format change.
/// </para>
/// <para>
/// <b>And every one of them applies the same encode.</b> On an HDR surface there is no
/// hardware encode to fall back on, so a pass that writes linear light onto a PQ swapchain
/// is not subtly wrong — it is a correct room with a washed-out menu over it, which is what
/// it looked like on the other backend before <see cref="DisplayEncoding"/> existed.
/// </para>
/// </remarks>
public sealed unsafe class D3D12Renderer : IRenderer
{
    private readonly D3D12Context _context;
    private readonly D3D12FramePipeline _pipeline;
    private readonly D3D12FrameRing _ring;
    private readonly D3D12Swapchain _swapchain;
    private readonly D3D12OverlayPass _overlay;

    private D3D12ScreenPass? _output;
    private D3D12ScreenPass? _movie;
    private D3D12ScreenPass? _fade;
    private D3D12Texture? _film;
    private Format _surface = Format.FormatUnknown;

    private SceneGeometry? _scene;
    private Camera? _camera;
    private Overlay? _list;
    private OverlayAtlas? _atlas;
    private readonly Dictionary<string, int> _pictures = [];

    private bool _needsRecreate;
    /// <summary>The picture without the interface, kept for frame generation.</summary>
    private D3D12Texture? _hudLess;
    private D3D12Texture? _shown;
    private D3D12Texture? _uplifted;
    private (uint Width, uint Height) _upliftFor;
    private NeuralUplift? _upliftAs;

    private bool _presentedAnything;

    /// <summary>
    /// What the wind runs on: wall-clock seconds since the renderer was made.
    /// </summary>
    /// <remarks>
    /// The renderer's own clock rather than the game's, because it drives presentation and
    /// not state. A paused game, a menu over the room and a conversation waiting on a line
    /// of dialogue all leave the trees moving, which is what they should do; nothing that
    /// reads this can affect anything the story can see. <c>VulkanRenderer</c> keeps the
    /// same clock for the same reason.
    /// </remarks>
    private readonly System.Diagnostics.Stopwatch _wind = System.Diagnostics.Stopwatch.StartNew();

    /// <summary>How long the last frame took, which is what a temporal upscaler is paced by.</summary>
    private readonly System.Diagnostics.Stopwatch _sinceLastFrame = System.Diagnostics.Stopwatch.StartNew();
    private bool _coverFilm;
    private OutputPlan _output_ = OutputPlan.Standard;
    private UpscalePlan _upscaling = UpscalePlan.None;
    private bool _disposed;

    private D3D12Renderer(
        D3D12Context context,
        D3D12FramePipeline pipeline,
        D3D12FrameRing ring,
        D3D12Swapchain swapchain,
        D3D12OverlayPass overlay,
        IGameWindow window)
    {
        _context = context;
        _pipeline = pipeline;
        _ring = ring;
        _swapchain = swapchain;
        _overlay = overlay;
        _window = window;
    }

    /// <summary>
    /// The window, kept for the one question only it can answer: how big it is now.
    /// </summary>
    /// <remarks>
    /// The Vulkan renderer has always held one for this. This one took a window, used it to
    /// make a swapchain and let go of it, which left <see cref="Recreate"/> with nowhere to
    /// read a new size from — see what that cost, there.
    /// </remarks>
    private readonly IGameWindow _window;

    /// <summary>Which API is behind this renderer.</summary>
    public RenderBackend Backend => RenderBackend.Direct3D12;

    /// <summary>The adapter's name, as the driver reports it.</summary>
    public string DeviceName => _context.DeviceName;

    /// <summary>Who made the adapter.</summary>
    public GpuVendor Vendor => GpuVendors.Of(_context.Adapter1.VendorId);

    /// <summary>What the device in use can do.</summary>
    public RenderCapabilityTier Tiers => _context.Adapter1.Tiers;

    /// <summary>The size frames are presented at, which is the window's.</summary>
    public (int Width, int Height) SwapchainSize => _swapchain.Size;

    /// <summary>How many buffers the swapchain has.</summary>
    public int SwapchainImageCount => (int)D3D12Swapchain.BufferCount;

    /// <summary>The size the room is actually drawn at.</summary>
    public (int Width, int Height) RenderSize => _pipeline.RenderSize;

    /// <summary>Whether the traced variant of the room's pass was built.</summary>
    public bool SupportsRayTracing => _pipeline.RayTracing;

    /// <summary>How much tracing to do.</summary>
    public RayTracingQuality Quality
    {
        get => _pipeline.Frames.Settings.Quality;
        set => _pipeline.Frames.Settings =
            RayTracingSettings.For(_pipeline.RayTracing ? value : RayTracingQuality.None);
    }

    /// <summary>What the scene's lights come to on a grid, for whoever wants to report it.</summary>
    public SceneLightGrid? LightGrid => _pipeline.Frames.Grid;

    /// <summary>Which upscalers this adapter can be asked for.</summary>
    /// <remarks>
    /// FSR on every adapter, DLSS only on NVIDIA's. That asymmetry is AMD's doing rather
    /// than this renderer's: FidelityFX runs on anything with a compute shader, and NGX
    /// refuses anything that is not a GeForce RTX and says so.
    /// </remarks>
    public IReadOnlyList<UpscalerKind> OfferedUpscalers => Vendor is GpuVendor.Nvidia
        ? [UpscalerKind.Off, UpscalerKind.Spatial, UpscalerKind.Fsr, UpscalerKind.Dlss]
        : [UpscalerKind.Off, UpscalerKind.Spatial, UpscalerKind.Fsr];

    /// <summary>What the upscaler is doing, for the startup report.</summary>
    public string UpscalerName => _pipeline.UpscalerNote ?? "none";

    /// <summary>Whether DLSS is available on this machine at all.</summary>
    public bool DlssAvailable => _pipeline.HasDlss;

    /// <summary>Whether the runtime offers ray reconstruction.</summary>
    public bool DlssRayReconstruction => _pipeline.HasRayReconstruction;

    /// <summary>Why ray reconstruction is not being used, when it is not.</summary>
    public string DlssRayReconstructionNote => DlssRayReconstruction
        ? "offered"
        : "not offered by this runtime";

    /// <summary>Whether the runtime offers frame generation.</summary>
    public bool DlssFrameGeneration => _pipeline.HasFrameGeneration;

    /// <inheritdoc/>
    public int FrameGenerationMaximum =>
        _pipeline.CanGenerate ? _pipeline.Streamline?.FrameGenerationMaximum ?? 0 : 0;

    /// <inheritdoc/>
    public bool LatencyControl => _pipeline.Streamline is { HasLatencyControl: true };

    /// <summary>Whether the swapchain is actually presenting high dynamic range.</summary>
    public bool HighDynamicRangeActive => _swapchain.HighDynamicRange;

    /// <summary>Whether an interface is being drawn.</summary>
    /// <remarks>
    /// Whether this renderer can draw an interface, which it always can — the pass is built
    /// with the renderer. It used to ask whether a mesh had been <em>set</em>, which is a
    /// different question and one whose answer at startup, where this is reported, is always
    /// no: every run said "NOT drawing" over an interface that was drawing perfectly well,
    /// and said it on the one line somebody reads when the interface looks wrong.
    /// </remarks>
    public bool HasOverlay => true;

    /// <summary>Everything the debug layer has said since it was last asked.</summary>
    /// <remarks>
    /// Direct3D writes its diagnostics into a queue on the device and something has to come
    /// and read it. A renderer that never does gets an HRESULT and no more, which for a
    /// whole class of mistake — a resource in the wrong state, a descriptor pointing at the
    /// wrong thing — is a number with no way back to the frame that caused it. Reading the
    /// queue clears it.
    /// </remarks>
    public IReadOnlyList<string> Messages => _context.DrainMessages();

    /// <summary>Where to look for the upscaler runtimes.</summary>
    /// <remarks>
    /// Read once, when the pipeline is built. Setting it afterwards does nothing, and it is
    /// here so the two backends present the same surface to whoever configures them.
    /// </remarks>
    public UpscalerRuntimes? Runtimes { get; set; }

    /// <summary>How far the picture is faded out, from nought to one.</summary>
    public float Fade { get; set; }

    /// <summary>What it is faded towards.</summary>
    public Vector3 FadeColour { get; set; }

    /// <summary>Whether to wait for the display before presenting.</summary>
    public bool VerticalSync { get; set; } = true;

    /// <summary>What DLSS was asked to do.</summary>
    public UpscalePlan Upscaling
    {
        get => _upscaling;
        set
        {
            _upscaling = value ?? UpscalePlan.None;
            _pipeline.Upscaling = _upscaling;
        }
    }

    /// <summary>What the display wants and how bright to drive it.</summary>
    /// <inheritdoc/>
    public ReflectionPlan Reflections
    {
        get => _reflectionPlan;

        set
        {
            _reflectionPlan = value.Sane();

            if (_pipeline is not null)
            {
                _pipeline.Reflections = _reflectionPlan;
            }
        }
    }

    private ReflectionPlan _reflectionPlan = ReflectionPlan.Default;

    /// <inheritdoc/>
    public OutputPlan Output
    {
        get => _output_;
        set
        {
            OutputPlan wanted = value ?? OutputPlan.Standard;

            if (wanted.HighDynamicRange != _output_.HighDynamicRange ||
                wanted.Transfer != _output_.Transfer)
            {
                _needsRecreate = true;
            }

            _output_ = wanted;
        }
    }

    /// <summary>Creates a renderer for a window.</summary>
    /// <param name="window">Window to present into.</param>
    /// <param name="windowSource">Where that window's handle comes from.</param>
    /// <param name="rayTracing">Whether to build the ray-traced variant of the room's pass.</param>
    /// <param name="runtimes">Where the upscaler runtimes are, or null to look beside the executable.</param>
    /// <returns>The renderer.</returns>
    /// <exception cref="D3D12Exception">There is no usable device, or no window to present to.</exception>
    public static D3D12Renderer Create(
        IGameWindow window,
        IWin32WindowSource windowSource,
        bool rayTracing = false,
        string? runtimes = null)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(windowSource);

        D3D12Context context = D3D12Context.Create(enableValidation: true);

        D3D12FramePipeline? pipeline = null;
        D3D12FrameRing? ring = null;
        D3D12Swapchain? swapchain = null;
        D3D12OverlayPass? overlay = null;

        try
        {
            pipeline = D3D12FramePipeline.Create(context, rayTracing, runtimes);
            ring = D3D12FrameRing.Create(context);

            int width = window.FramebufferWidth;
            int height = window.FramebufferHeight;

            swapchain = D3D12Swapchain.Create(
                context, windowSource.WindowHandle, width, height,
                wantHdr: false, pipeline.Streamline);

            // Standard range to begin with, whatever the settings say: the output plan
            // arrives after the renderer exists and asks for a rebuild if it wants
            // something else. Creating in the wide format here would mean guessing what
            // it is going to be.


            overlay = D3D12OverlayPass.Create(
                context, pipeline.Compiler, swapchain.RenderFormat, ring.Frames);

            // Only now is it known: the pipeline was built before the swapchain, and whether
            // the swapchain is one Streamline made is the whole of whether frames can be
            // generated. Asked once here rather than every frame, because what a card will
            // do does not change while the game is running.
            pipeline.CanGenerate = swapchain.Proxied;
            pipeline.Streamline?.RefreshFrameGeneration();

            Foundation.Diagnostics.Log.Info(pipeline.LatencyReport());

            var renderer = new D3D12Renderer(
                context, pipeline, ring, swapchain, overlay, window);
            renderer.Retarget();
            return renderer;
        }
        catch
        {
            overlay?.Dispose();
            swapchain?.Dispose();
            ring?.Dispose();
            pipeline?.Dispose();
            context.Dispose();
            throw;
        }
    }

    /// <summary>Somewhere to put a scene, on this renderer's device.</summary>
    /// <returns>Empty geometry.</returns>
    public SceneGeometry CreateGeometry() => _pipeline.CreateGeometry();

    /// <summary>Sets what to draw, and from where.</summary>
    /// <param name="scene">The geometry, or null to draw nothing.</param>
    /// <param name="camera">Where to look from, or null to leave the view alone.</param>
    /// <remarks>
    /// The renderer does not take ownership: the caller keeps the geometry alive for as long
    /// as it is set, and disposes it afterwards.
    /// </remarks>
    public void SetScene(SceneGeometry? scene, Camera? camera)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // A different room has nothing in common with the last one, so nothing a temporal
        // upscaler accumulated about the last one is worth keeping.
        if (!ReferenceEquals(scene, _scene))
        {
            _pipeline.Reset = true;
        }

        scene?.Finish();

        _scene = scene;

        if (camera is not null)
        {
            _camera = camera;
        }
    }

    /// <summary>Sets the lights anything without baked lighting is lit by.</summary>
    /// <param name="lights">The rig the scene was authored with.</param>
    /// <param name="scene">What the geometry occupies.</param>
    public void SetLights(
        IReadOnlyList<Formats.Scenes.AuthoredLight> lights, SceneExtent scene = default) =>
        _pipeline.Frames.SetLights(lights, scene);

    /// <inheritdoc/>
    public void SetParticles(IReadOnlyList<Particle> particles) =>
        _pipeline.SetParticles(particles);

    /// <inheritdoc/>
    public void SetFog(FogVolume fog) => _pipeline.SetFog(fog);

    /// <summary>Says that whatever a temporal pass remembers is worthless.</summary>
    public void ResetHistory() => _pipeline.Reset = true;

    /// <summary>Says the swapchain is stale and must be rebuilt before the next frame.</summary>
    public void Invalidate() => _needsRecreate = true;

    /// <summary>Waits until the device has finished everything it was given.</summary>
    public void Idle()
    {
        _ring.Wait();
        _context.Wait();
    }

    /// <summary>What this adapter is, for the startup report.</summary>
    /// <returns>What was found and what was chosen.</returns>
    public DeviceReport Survey() => D3D12DeviceSelector.Survey();

    /// <summary>Gives the interface its sheet of glyphs and pictures.</summary>
    /// <param name="atlas">The sheet.</param>
    public void SetOverlayAtlas(OverlayAtlas atlas)
    {
        ArgumentNullException.ThrowIfNull(atlas);

        _atlas = atlas;
        _overlay.SetAtlas(atlas);
    }

    /// <summary>Puts a screen's own picture on the device under a name.</summary>
    /// <param name="name">What to call it.</param>
    /// <param name="image">The picture.</param>
    /// <returns>Its index in the overlay's picture list.</returns>
    public int AddOverlayPicture(string name, DecodedImage image)
    {
        ArgumentNullException.ThrowIfNull(name);

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

    /// <summary>Forgets a screen's picture.</summary>
    /// <param name="name">What it was called.</param>
    /// <remarks>
    /// Only the name is forgotten; the texture stays until every picture is dropped at once.
    /// Removing one from the middle would renumber the rest, and a display list built before
    /// the renumbering would then draw the wrong pictures — which is a worse failure than
    /// holding a few megabytes of a map nobody is looking at.
    /// </remarks>
    public void DropOverlayPicture(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        _pictures.Remove(name);
    }

    /// <summary>Finds a screen's picture by the name it was given.</summary>
    /// <param name="name">What it was called.</param>
    /// <returns>Its index, or a negative number if there is no such picture.</returns>
    public int OverlayPicture(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _pictures.TryGetValue(name, out int number) ? number : -1;
    }

    /// <summary>Sets what the interface draws this frame.</summary>
    /// <param name="overlay">The display list, or null to draw none.</param>
    public void SetOverlay(Overlay? overlay)
    {
        // A display list carries the sheet it was cut from, and the interface has more than
        // one: the room's captions and the menu are drawn at different sizes from different
        // atlases. This used to take the list and keep whichever sheet happened to be on the
        // device, so the menu was drawn with the room's — one atlas sampled with another's
        // coordinates, which is a row of fragments where the words should be. The layout is
        // right, every glyph is wrong, and it looks like a broken font rather than a
        // renderer that swapped one texture for another.
        //
        // The Vulkan renderer has always done this. It was never seen here because Vulkan
        // was the default until the backend moved, and the room's captions are drawn from
        // the sheet that is already loaded — so everything except the menu looked right.
        if (overlay is not null && !ReferenceEquals(overlay.Atlas, _atlas))
        {
            SetOverlayAtlas(overlay.Atlas);
        }

        _list = overlay;
    }

    /// <summary>Shows a frame of film over everything.</summary>
    /// <param name="frame">The frame, or null to stop showing one.</param>
    /// <param name="cover">Whether to fill the window rather than fit inside it.</param>
    public void SetMovieFrame(DecodedImage? frame, bool cover = false)
    {
        _context.Wait();
        _film?.Dispose();
        _film = null;
        _coverFilm = cover;

        if (frame is { } picture && picture.Pixels is not null)
        {
            _film = D3D12TextureUpload.Create(_context, picture, mipmaps: false, linear: false);
        }
    }

    /// <summary>Shows a still picture behind everything.</summary>
    /// <param name="picture">The picture, or null to show none.</param>
    /// <remarks>
    /// The same pass as the film, covering the window rather than fitted into it. A backdrop
    /// is the whole of what is on screen when there is one.
    /// </remarks>
    public void SetBackdrop(DecodedImage? picture) => SetMovieFrame(picture, cover: true);

    /// <summary>Shows a still picture behind everything, without expanding its blocks.</summary>
    /// <param name="picture">The picture.</param>
    public void SetBackdrop(CompressedImage picture)
    {
        _context.Wait();
        _film?.Dispose();
        _film = D3D12TextureUpload.Create(_context, picture);
        _coverFilm = true;
    }

    /// <summary>Draws and presents one frame, clearing to a colour.</summary>
    /// <param name="red">Clear red, 0 to 1.</param>
    /// <param name="green">Clear green, 0 to 1.</param>
    /// <param name="blue">Clear blue, 0 to 1.</param>
    /// <returns>False when the frame was skipped because the swapchain needed rebuilding.</returns>
    /// <exception cref="D3D12Exception">Something on the device refused.</exception>
    public bool DrawFrame(float red, float green, float blue)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_needsRecreate)
        {
            Recreate();
            return false;
        }

        (int width, int height) = _swapchain.Size;

        if (width <= 0 || height <= 0)
        {
            return false;
        }

        // The frame's token, and then the wait. Everything below carries the token — the
        // markers, the tags, the upscale, the present — which is what makes them one frame
        // to a runtime that is timing them.
        //
        // The wait is first because it is the whole of what Reflex does: it returns later
        // than it was called, so that a frame the display is not ready for is begun later
        // rather than queued. Anything done before it is work whose result waits.
        Streamline? streamline = _pipeline.Streamline;

        streamline?.BeginFrame();
        streamline?.Sleep();
        streamline?.Mark(StreamlineMarker.SimulationStart);

        // Whatever the swapchain is now. Checked here rather than only after something asks
        // for a rebuild, because a pipeline outliving the format it was built for is not an
        // error the frame reports — it is a picture made of the wrong bits, and the debug
        // layer is the only thing that ever says so.
        Retarget();

        Camera camera = _camera ?? new Camera();
        (float R, float G, float B) clear = (red, green, blue);

        // Whatever moved since the last frame moves in the traced world too, and whatever
        // changed shape goes into its buffers. Both before the frame is recorded and after
        // the ring has said the device is finished with what they write.
        _ring.Wait();
        _scene?.Settle();
        _scene?.Flush((int)_ring.Index);

        // How far above white a lamp is allowed to burn, and the clock the wind sways on.
        // Both are per-frame facts about presentation rather than about the room, which is
        // why they are set here and not by whoever loaded it.
        _pipeline.Frames.EmissiveGain = _output_.EmissiveGain;
        _pipeline.Frames.Seconds = (float)_wind.Elapsed.TotalSeconds;
        _pipeline.DeltaSeconds = Pace();

        _pipeline.Prepare(width, height, clear, camera, _scene);

        // Everything that decides what this frame contains has now happened, and everything
        // after it is recording. The pair is what Reflex measures the simulation by.
        streamline?.Mark(StreamlineMarker.SimulationEnd);
        streamline?.Mark(StreamlineMarker.RenderSubmitStart);

        ID3D12GraphicsCommandList4* list = _ring.Begin();

        FramePicture picture = _pipeline.Draw(list, _scene, camera, clear);

        // Where everything was drawn, ready for the next frame's motion vectors. After the
        // recording rather than before it: what a motion vector needs is where a thing was
        // when it was last drawn, and something that moved twice between two frames was
        // only ever drawn at the second place.
        _scene?.Advance();

        // --- onto the swapchain ---
        uint buffer = _swapchain.CurrentBuffer;
        _swapchain.Transition(list, buffer, ResourceStates.RenderTarget);

        CpuDescriptorHandle target = _swapchain.RenderTarget(buffer);
        DisplayEncode display = Encoding();

        _output!.Draw(
            list,
            [target],
            [picture.Colour],
            new OutputTuning(
                new Vector4(
                    display.Transfer,
                    display.PaperWhite,
                    display.Headroom,
                    (float)_output_.ToneMap),

                // Only the engine's own upscaler leaves the sharpening to this pass. The
                // vendors' runtimes have their own, tuned against their own accumulation,
                // and running a second one over the top is how a picture ends up crunchy.
                new Vector4(
                    _upscaling.Sharpen && _upscaling.Kind is UpscalerKind.Spatial or UpscalerKind.Off
                        ? _upscaling.Sharpness
                        : 0f,
                    width > 0 ? 1f / width : 0f,
                    height > 0 ? 1f / height : 0f,
                    0f)),
            width,
            height);

        // Over the room, and under the interface. A film covers the window, so what is
        // behind it does not matter; the captions that go with one do.
        // The neural uplift, over the finished picture rather than over the light that made
        // it. Before the film and the interface, so that neither is reworked.
        RecordUplift(list, buffer, width, height, camera, streamline);

        RecordFilm(list, target, display, width, height);

        // Everything that is not the interface is now on the back buffer, which is exactly
        // what frame generation wants a copy of. Taken here rather than reasoned about
        // later: the next three lines put the interface on top of it.
        RecordHudLess(list, buffer, width, height, streamline);

        // On top of everything and at the size of the window, never at the size the room was
        // drawn at. Drawing an interface at render resolution and stretching it is the single
        // most visible way to get the two sizes wrong.
        if (_list is not null)
        {
            _overlay.Display = display;
            _overlay.Prepare(_list, _ring.Index);
            _overlay.Record(list, target, width, height);
        }

        // And last of all, over the interface as well as the room.
        RecordFade(list, target, display, width, height);

        _swapchain.Transition(list, buffer, ResourceStates.Present);
        _ring.Submit();

        streamline?.Mark(StreamlineMarker.RenderSubmitEnd);
        streamline?.Mark(StreamlineMarker.PresentStart);

        bool presented = _swapchain.Present(VerticalSync);

        streamline?.Mark(StreamlineMarker.PresentEnd);

        // Closed here, and not before the present: the present is where frame generation
        // does its work, and it does it against the token this frame was opened with. A
        // token let go of first is a generated frame with nothing to pair against.
        streamline?.EndFrame();

        if (!presented)
        {
            _needsRecreate = true;
        }

        _presentedAnything = true;
        return true;
    }

    /// <summary>Measures how long the last frame took.</summary>
    /// <returns>Seconds, clamped to something a temporal upscaler can be paced by.</returns>
    /// <remarks>
    /// A frame that took longer than a second is a load, a breakpoint or a machine that went
    /// to sleep, and pacing anything against it produces nonsense — so it is reported as a
    /// sixtieth instead. The same rule as <c>VulkanRenderer.Jitter</c>.
    /// </remarks>
    private float Pace()
    {
        float seconds = (float)_sinceLastFrame.Elapsed.TotalSeconds;
        _sinceLastFrame.Restart();

        return float.IsFinite(seconds) && seconds is > 0f and <= 1f ? seconds : 1f / 60f;
    }

    /// <summary>Reads back the last presented frame.</summary>
    /// <returns>The picture, or null if nothing has been presented.</returns>
    public DecodedImage? Capture()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_presentedAnything)
        {
            return null;
        }

        Idle();

        (int width, int height) = _swapchain.Size;
        uint buffer = _swapchain.CurrentBuffer;

        // The buffer that was just presented is in Present, which a copy can begin from.
        // What it holds matters: a screenshot is always eight-bit sRGB, and a frame
        // presented in HDR10 has to be brought back down rather than copied.
        return D3D12Readback.Read(
            _context,
            _swapchain.Buffer(buffer),
            ResourceStates.Present,
            width,
            height,
            swapRedAndBlue: false,
            _swapchain.HighDynamicRange ? _swapchain.Format : Format.FormatUnknown,
            _output_.PaperWhiteNits);
    }

    /// <summary>Reads back the motion vectors of the last frame.</summary>
    /// <returns>Nothing yet.</returns>
    /// <remarks>
    /// The motion target lives inside the frame pipeline and is not kept past the frame that
    /// wrote it, so there is nothing here to read. It exists on the other backend for one
    /// diagnostic image and has no caller in the game.
    /// </remarks>
    public float[]? CaptureMotion() => null;

    /// <summary>The device, the chain and the colour space, in one line.</summary>
    /// <returns>What the Vulkan renderer says about itself, about this one.</returns>
    /// <remarks>
    /// It used to say only the type's name, which is the one thing a reader already knows.
    /// The swapchain's format and colour space are what somebody actually needs when a
    /// picture is wrong — and they are not otherwise discoverable without photographing the
    /// screen and guessing, which is how an encoding changing underneath the interface came
    /// to be diagnosed the slow way.
    /// </remarks>
    public override string ToString() =>
        string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{DeviceName}: {_swapchain.Size.Width}x{_swapchain.Size.Height}, " +
            $"{D3D12Swapchain.BufferCount} buffers, {_swapchain.Format}, " +
            $"{_swapchain.ColorSpace}, tiers {Tiers}");

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _ring.Wait();
        _context.Wait();

        _film?.Dispose();
        _fade?.Dispose();
        _movie?.Dispose();
        _output?.Dispose();
        _hudLess?.Dispose();
        _shown?.Dispose();
        _uplifted?.Dispose();

        _overlay.Dispose();
        _swapchain.Dispose();
        _ring.Dispose();
        _pipeline.Dispose();
        _context.Dispose();
    }

    /// <summary>What every pass writing the swapchain has to do to its colours.</summary>
    /// <remarks>
    /// One answer, derived from the colour space the surface actually gave back rather than
    /// from what was asked for. A frame where the room encoded for HDR10 and the interface
    /// did not is not a subtle mismatch: it is a correct picture with a washed-out menu over
    /// it.
    /// </remarks>
    /// <summary>Which wide encoding this frame wants the swapchain in.</summary>
    /// <remarks>
    /// <para>
    /// The player's choice and nothing else. This used to make
    /// <see cref="HdrTransfer.Automatic"/> mean scRGB while frames were being generated,
    /// because a generated frame is an interpolation between two presented ones and PQ is
    /// not linear in light — averaging two PQ frames averages the wrong quantity.
    /// </para>
    /// <para>
    /// <b>That was wrong, and the interface is what showed it.</b> The interface, the film
    /// and the fade are drawn straight onto the swapchain and blend in whatever space it
    /// carries — see <see cref="DisplayEncoding"/>, which explains why this project blends
    /// in encoded space rather than compositing. On a PQ surface that space is perceptual;
    /// on scRGB it is linear light. A glyph is almost entirely partial coverage, so the
    /// blend space is the whole of how its edges look: the room went on looking right and
    /// every letter in the game came out wrong.
    /// </para>
    /// <para>
    /// So an encoding is not changed underneath a player because an unrelated setting is on.
    /// scRGB is theirs to choose and it is honoured; what it costs is that interpolation
    /// question left as it was, which is NVIDIA's to answer and not visible in the way a
    /// wrecked interface is.
    /// </para>
    /// </remarks>
    private HdrTransfer Transfer() => _output_.Transfer;

    private DisplayEncode Encoding() => _swapchain.HighDynamicRange
        ? new DisplayEncode(
            _swapchain.ColorSpace == ColorSpaceType.RgbFullG2084NoneP2020
                ? DisplayEncode.TransferPerceptualQuantiser
                : DisplayEncode.TransferExtendedLinear,
            _output_.PaperWhiteNits,
            _output_.Headroom)
        : DisplayEncode.Standard;

    private void RecordFilm(
        ID3D12GraphicsCommandList4* list,
        CpuDescriptorHandle target,
        DisplayEncode display,
        int width,
        int height)
    {
        if (_film is null || _movie is null)
        {
            return;
        }

        _film.Transition(list, ResourceStates.AllShaderResource);

        // How much of the window the picture covers. Fitted to whichever dimension runs out
        // first, so a 4:3 cutscene in a widescreen window keeps its shape and the rest is
        // letterboxed; a backdrop covers instead, because a backdrop is the whole picture.
        float pictureAspect = (float)_film.Width / Math.Max(1, _film.Height);
        float windowAspect = (float)width / Math.Max(1, height);

        float sx = 1f;
        float sy = 1f;

        if (_coverFilm)
        {
            // Nothing to fit: the picture is stretched over the whole window.
        }
        else if (pictureAspect > windowAspect)
        {
            sy = windowAspect / pictureAspect;
        }
        else
        {
            sx = pictureAspect / windowAspect;
        }

        var block = new MovieConstants(new Vector4(sx, sy, 0f, 0f), display);
        _movie.Draw(list, [target], [_film], block, width, height);
    }

    private void RecordFade(
        ID3D12GraphicsCommandList4* list,
        CpuDescriptorHandle target,
        DisplayEncode display,
        int width,
        int height)
    {
        if (_fade is null || Fade <= 0f)
        {
            return;
        }

        var block = new FadeConstants(
            new Vector4(FadeColour, Math.Clamp(Fade, 0f, 1f)), display);

        _fade.Draw(list, [target], [], block, width, height);
    }

    /// <summary>Rebuilds the swapchain and everything built for its format.</summary>
    private void Recreate()
    {
        _needsRecreate = false;

        // <b>The window's size, not the swapchain's own.</b> This used to read the size back
        // out of the chain it was about to resize and hand it straight back — so a resize
        // resized nothing. The chain stayed at whatever size it was first made at, and DXGI
        // stretched every presented frame to fill the window: a game that got blurrier the
        // further the window was dragged from the size it started at, and blurriest of all
        // at fullscreen. The interface went with it, which is what made it look like a font
        // problem rather than a swapchain one.
        int width = _window.FramebufferWidth;
        int height = _window.FramebufferHeight;

        // A minimised window is nought by nought, which a swapchain may not be. Nothing is
        // rebuilt until it comes back, and the frame is skipped either way.
        if (width <= 0 || height <= 0)
        {
            _needsRecreate = true;
            return;
        }

        _ring.Wait();
        _context.Wait();

        _swapchain.Resize(width, height, _output_.HighDynamicRange, Transfer());

        Retarget();

        // The window changed under the upscaler, which is exactly the case it must be told
        // about: whatever it accumulated was accumulated at another size.
        _pipeline.Reset = true;
    }

    /// <summary>Runs the neural uplift over the picture as it will be shown.</summary>
    /// <param name="list">The frame's command list.</param>
    /// <param name="buffer">Which back buffer the picture was just drawn onto.</param>
    /// <param name="width">Its width.</param>
    /// <param name="height">Its height.</param>
    /// <param name="camera">Where the frame was seen from.</param>
    /// <param name="streamline">The runtime, or null.</param>
    /// <remarks>
    /// <para>
    /// <b>Here rather than with the upscalers, and that is the whole point.</b> The network
    /// reworks a finished picture: its controls are intensity, local and global tone, and
    /// structure, which are things you can only do to a signal that has been mapped for a
    /// display. It is given no exposure and no high-range flag — <c>nvngx_dlssnr.dll</c> has
    /// neither, so it cannot be told — and it was run for a while from the upscaler slot,
    /// where what it got was linear light with a lamp in it three hundred times over one.
    /// Above its range its channels part company, and channels parting company is colour: the
    /// bright things in a frame fringed and flickered.
    /// </para>
    /// <para>
    /// So it runs on the back buffer, after the tone map and the display encode and before
    /// the film and the interface. That is the same picture a ReShade add-in would hand it,
    /// which is the one configuration this network is known to be happy in.
    /// </para>
    /// <para>
    /// <b>Two copies rather than a render target.</b> The picture is copied off the back
    /// buffer, reworked into a second texture, and copied back. A copy either side is worth
    /// more than the descriptor plumbing a render target would need here, and it borrows a
    /// path this file already trusts — the hud-less copy below does the same thing.
    /// </para>
    /// <para>
    /// The two guides come from the room and are the size the room was drawn at, so this only
    /// runs where that is also the size it is shown at. <see cref="UpscalePlan.Sane"/> pins
    /// the rung to native whenever the uplift is on, which is what makes that true.
    /// </para>
    /// </remarks>
    private void RecordUplift(
        ID3D12GraphicsCommandList4* list,
        uint buffer,
        int width,
        int height,
        Camera? camera,
        Streamline? streamline)
    {
        if (streamline is not { NeuralRenderingLoaded: true } ||
            !_upscaling.Neural.Enabled ||
            _pipeline.Guides is not { } depth ||
            _pipeline.Motion is not { } motion)
        {
            return;
        }

        // The guides are the room's own size. Anything else means something upscaled, and
        // the network would be reading them off the edge of the picture.
        if (depth.Width != width || depth.Height != height)
        {
            return;
        }

        Format format = _swapchain.Format;

        if (_shown is null || _uplifted is null ||
            _shown.Width != width || _shown.Height != height || _shown.Format != format)
        {
            _context.Wait();
            _shown?.Dispose();
            _uplifted?.Dispose();

            _shown = D3D12Texture.CreateSampled(_context, format, width, height);
            _uplifted = D3D12Texture.CreateStorage(_context, format, width, height);

            _upliftFor = default;
        }

        var size = ((uint)width, (uint)height);

        // Told what to do only when what to do changed. The options are plugin state that
        // survives a frame, and a slider the player is dragging must not tear the feature
        // down and build it again under their hand.
        if (_upliftFor != size || _upliftAs != _upscaling.Neural)
        {
            if (!streamline.SetDlssOptions(
                    _upscaling.Quality,
                    _upscaling.DlssPreset,
                    size,
                    _upscaling.HighDynamicRange,
                    rayReconstruction: true,
                    _upscaling.Neural))
            {
                return;
            }

            _upliftFor = size;
            _upliftAs = _upscaling.Neural;
        }

        _swapchain.Transition(list, buffer, ResourceStates.CopySource);
        _shown.Transition(list, ResourceStates.CopyDest);

        list->CopyResource(_shown.Handle, _swapchain.Buffer(buffer));

        _shown.Transition(list, ResourceStates.NonPixelShaderResource);
        _uplifted.Transition(list, ResourceStates.UnorderedAccess);
        depth.Transition(list, ResourceStates.NonPixelShaderResource);
        motion.Transition(list, ResourceStates.NonPixelShaderResource);

        var frame = new StreamlineFrame(
            Surface(_shown),
            Surface(depth),
            Surface(motion),
            Surface(_uplifted),
            _pipeline.JitterPixels,
            _pipeline.DeltaSeconds,
            _pipeline.Reset,
            camera,
            height > 0 ? (float)width / height : 1f,
            Sharpen: false,
            Sharpness: 0f,
            _upscaling.HighDynamicRange);

        bool reworked = streamline.Evaluate((nint)list, frame, rayReconstruction: true);

        if (reworked)
        {
            _uplifted.Transition(list, ResourceStates.CopySource);
            _swapchain.Transition(list, buffer, ResourceStates.CopyDest);

            list->CopyResource(_swapchain.Buffer(buffer), _uplifted.Handle);
        }

        // Back to what the rest of the frame expects either way. A refused frame leaves the
        // picture as it was drawn, which is the right answer and not a visible one.
        _swapchain.Transition(list, buffer, ResourceStates.RenderTarget);
    }

    /// <summary>Says what a texture is in the terms the runtime asks for.</summary>
    private static UpscaleSurface Surface(D3D12Texture texture) => new(
        (nint)texture.Handle,
        0,
        (uint)texture.State,
        (uint)texture.Width,
        (uint)texture.Height,
        (uint)texture.Format);

    /// <summary>Copies the frame as it stands, and hands the copy to frame generation.</summary>
    /// <param name="list">The frame's command list.</param>
    /// <param name="buffer">Which back buffer this frame is drawing into.</param>
    /// <param name="width">Its width.</param>
    /// <param name="height">Its height.</param>
    /// <param name="streamline">The runtime, or null.</param>
    /// <remarks>
    /// <para>
    /// A copy of the whole back buffer, every frame, and it is not free — but the cheaper
    /// arrangements are all worse. Drawing the room into a target of its own and copying
    /// that onto the back buffer costs the same copy; drawing the interface into a target of
    /// its own changes how every existing frame blends, which
    /// <see cref="DisplayEncoding"/> explains this project has already decided against.
    /// </para>
    /// <para>
    /// Skipped entirely when nothing is generating frames, which is the ordinary case: the
    /// copy exists for one feature and should cost nothing when that feature is off.
    /// </para>
    /// </remarks>
    private void RecordHudLess(
        ID3D12GraphicsCommandList4* list, uint buffer, int width, int height,
        Streamline? streamline)
    {
        if (streamline is null || !_pipeline.CanGenerate || _pipeline.Generating <= 0)
        {
            return;
        }

        Format format = _swapchain.Format;

        if (_hudLess is null ||
            _hudLess.Width != width || _hudLess.Height != height || _hudLess.Format != format)
        {
            _context.Wait();
            _hudLess?.Dispose();

            // A plain sampled texture. It is never drawn into — it is only ever the
            // destination of a copy and then something the runtime reads.
            _hudLess = D3D12Texture.CreateSampled(_context, format, width, height);
        }

        _swapchain.Transition(list, buffer, ResourceStates.CopySource);
        _hudLess.Transition(list, ResourceStates.CopyDest);

        list->CopyResource(_hudLess.Handle, _swapchain.Buffer(buffer));

        // Back to what the rest of the frame expects, and into what the runtime will read it
        // as. Both are stated rather than left implied: Streamline inserts its own barriers
        // from the state it is told, so a state that is not the true one is a read of a
        // resource the device is still writing.
        _swapchain.Transition(list, buffer, ResourceStates.RenderTarget);
        _hudLess.Transition(list, ResourceStates.AllShaderResource);

        streamline.TagHudLess(
            (nint)list,
            new UpscaleSurface(
                (nint)_hudLess.Handle,
                0,
                (uint)ResourceStates.AllShaderResource,
                (uint)width,
                (uint)height,
                (uint)format));
    }

    /// <summary>Builds the passes that write the swapchain, for whatever format it now has.</summary>
    private void Retarget()
    {
        if (_surface == _swapchain.RenderFormat && _output is not null)
        {
            return;
        }

        _surface = _swapchain.RenderFormat;

        // Its pipeline, not the interface itself: the atlas and the screens' own pictures
        // outlive a display change, and reloading them would be a blank interface every time
        // somebody dragged the window onto another monitor.
        _overlay.Retarget(_surface);

        _fade?.Dispose();
        _movie?.Dispose();
        _output?.Dispose();

        _output = D3D12ScreenPass.Create(
            _context,
            _pipeline.Compiler,
            OutputShaders.Vertex,
            OutputShaders.Fragment,
            "output",
            inputs: 1,
            constantBytes: 32,
            [_surface]);

        _movie = D3D12ScreenPass.Create(
            _context,
            _pipeline.Compiler,
            MovieShaders.Vertex,
            MovieShaders.Fragment,
            "movie",
            inputs: 1,
            constantBytes: 32,
            [_surface]);

        _fade = D3D12ScreenPass.Create(
            _context,
            _pipeline.Compiler,
            FadeShaders.Vertex,
            FadeShaders.Fragment,
            "fade",
            inputs: 0,
            constantBytes: 32,
            [_surface],
            blend: true);
    }

    /// <summary>What the output pass is told.</summary>
    private readonly record struct OutputTuning(Vector4 Tuning, Vector4 Sharpen);
}
