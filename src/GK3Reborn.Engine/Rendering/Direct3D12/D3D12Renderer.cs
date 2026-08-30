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
        D3D12OverlayPass overlay)
    {
        _context = context;
        _pipeline = pipeline;
        _ring = ring;
        _swapchain = swapchain;
        _overlay = overlay;
    }

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
    public IReadOnlyList<UpscalerKind> OfferedUpscalers => Vendor is GpuVendor.Nvidia
        ? [UpscalerKind.Off, UpscalerKind.Spatial, UpscalerKind.Dlss]
        : [UpscalerKind.Off, UpscalerKind.Spatial];

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

    /// <summary>Whether the swapchain is actually presenting high dynamic range.</summary>
    public bool HighDynamicRangeActive => _swapchain.HighDynamicRange;

    /// <summary>Whether an interface is being drawn.</summary>
    public bool HasOverlay => _list is not null;

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
                context, windowSource.WindowHandle, width, height);

            overlay = D3D12OverlayPass.Create(
                context, pipeline.Compiler, swapchain.RenderFormat, ring.Frames);

            var renderer = new D3D12Renderer(context, pipeline, ring, swapchain, overlay);
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
    public void SetOverlay(Overlay? overlay) => _list = overlay;

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
        RecordFilm(list, target, display, width, height);

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

        if (!_swapchain.Present(VerticalSync))
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

        _ring.Wait();
        _context.Wait();

        (int width, int height) = _swapchain.Size;
        _swapchain.Resize(width, height, _output_.HighDynamicRange);

        Retarget();

        // The window changed under the upscaler, which is exactly the case it must be told
        // about: whatever it accumulated was accumulated at another size.
        _pipeline.Reset = true;
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
