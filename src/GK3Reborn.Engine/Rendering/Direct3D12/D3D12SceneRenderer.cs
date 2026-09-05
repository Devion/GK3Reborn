using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Rendering.Geometry;
using GK3Reborn.Rendering.Shaders;
using GK3Reborn.Rendering.Upscaling;
using Silk.NET.Direct3D12;

namespace GK3Reborn.Rendering.Direct3D12;

/// <summary>
/// Draws a room into a texture with no window anywhere, on Direct3D.
/// </summary>
/// <remarks>
/// <para>
/// The twin of <c>SceneRenderer</c>, and what makes the two backends comparable: the same
/// scene, the same camera and the same shaders on either, with a picture at the end that can
/// be put beside the other one. Nothing about a renderer is proved by a pipeline that
/// creates — only by a picture, and only by a picture somebody looked at.
/// </para>
/// <para>
/// Almost all of it is <see cref="D3D12FramePipeline"/>, which the windowed renderer shares.
/// What is left here is the two things a headless render does differently: the encode goes
/// into a texture of its own rather than onto a swapchain, and that texture is read back.
/// </para>
/// </remarks>
public sealed unsafe class D3D12SceneRenderer : IOffscreenRenderer
{
    private readonly D3D12Context _context;
    private readonly D3D12FramePipeline _pipeline;
    private readonly D3D12ScreenPass _output;

    private D3D12DescriptorHeap? _target;
    private D3D12Texture? _picture;
    private int _displayWidth;
    private int _displayHeight;
    private bool _disposed;

    private D3D12SceneRenderer(
        D3D12Context context, D3D12FramePipeline pipeline, D3D12ScreenPass output)
    {
        _context = context;
        _pipeline = pipeline;
        _output = output;
    }

    /// <inheritdoc/>
    public RenderBackend Backend => RenderBackend.Direct3D12;

    /// <inheritdoc/>
    public string DeviceName => _context.DeviceName;

    /// <inheritdoc/>
    public bool SupportsRayTracing => _pipeline.RayTracing;

    /// <inheritdoc/>
    public RayTracingQuality Quality
    {
        get => _pipeline.Frames.Settings.Quality;
        set => _pipeline.Frames.Settings =
            RayTracingSettings.For(_pipeline.RayTracing ? value : RayTracingQuality.None);
    }

    /// <inheritdoc/>
    public float Seconds
    {
        get => _pipeline.Frames.Seconds;
        set => _pipeline.Frames.Seconds = value;
    }

    /// <inheritdoc/>
    public SceneLightGrid? LightGrid => _pipeline.Frames.Grid;

    /// <summary>The seam a scene is put on this device through.</summary>
    public D3D12GeometryDevice Geometry => _pipeline.Geometry;

    /// <summary>The textures the device holds, shared by every scene this renderer draws.</summary>
    public TextureCache Textures => _pipeline.Textures;

    /// <summary>How much tracing to do, and how.</summary>
    public RayTracingSettings Tracing
    {
        get => _pipeline.Frames.Settings;
        set => _pipeline.Frames.Settings = value;
    }

    /// <summary>What DLSS was asked to do, or null to draw at display resolution.</summary>
    /// <remarks>
    /// Setting this changes what <see cref="Render"/> means. The room is drawn at the render
    /// size the plan chooses and the picture comes back at the size asked for, with the
    /// upscale between them — which is the whole reason the two sizes are separate numbers
    /// everywhere in this renderer rather than one and a multiplier.
    /// </remarks>
    public UpscalePlan? Upscaling
    {
        get => _pipeline.Upscaling;
        set => _pipeline.Upscaling = value;
    }

    /// <summary>What the upscaler is doing, for the startup report.</summary>
    public string? UpscalerNote => _pipeline.UpscalerNote;

    /// <summary>Whether DLSS is available on this machine at all.</summary>
    public bool HasDlss => _pipeline.HasDlss;

    /// <summary>How long since the last frame, which every temporal upscaler asks for.</summary>
    public float DeltaSeconds
    {
        get => _pipeline.DeltaSeconds;
        set => _pipeline.DeltaSeconds = value;
    }

    /// <summary>Whether what the upscaler remembers about the last frame is worthless.</summary>
    public bool Reset
    {
        get => _pipeline.Reset;
        set => _pipeline.Reset = value;
    }

    /// <summary>What the picture is cleared to before anything is drawn.</summary>
    /// <remarks>
    /// Black for a real frame. Anything else is a diagnostic: a clear colour that survives to
    /// the picture proves the output encode and the readback, and leaves only the mesh pass
    /// to account for a room that did not appear.
    /// </remarks>
    public (float R, float G, float B) ClearColour { get; set; }

    /// <summary>What the last frame actually issued, for when a room does not appear.</summary>
    public string LastFrame => _pipeline.LastFrame;

    /// <summary>Everything the debug layer has said since it was last asked.</summary>
    public IReadOnlyList<string> Messages => _context.DrainMessages();

    /// <summary>Creates a headless renderer.</summary>
    /// <param name="rayTracing">Whether to build the ray-traced variant of the room's pipeline.</param>
    /// <param name="runtimes">
    /// Somewhere else to look for the upscaler runtimes, or null to look only beside the
    /// executable. A game finds them in its own libs directory; a tool run out of a build
    /// tree has to be told.
    /// </param>
    /// <returns>The renderer.</returns>
    /// <exception cref="D3D12Exception">There is no usable device.</exception>
    public static D3D12SceneRenderer Create(bool rayTracing = false, string? runtimes = null)
    {
        D3D12Context context = D3D12Context.Create(enableValidation: true);
        D3D12FramePipeline? pipeline = null;

        try
        {
            pipeline = D3D12FramePipeline.Create(context, rayTracing, runtimes);

            D3D12ScreenPass output = D3D12ScreenPass.Create(
                context,
                pipeline.Compiler,
                OutputShaders.Vertex,
                OutputShaders.Fragment,
                "output",
                inputs: 1,
                constantBytes: 32,
                [GBufferFormats.Picture]);

            return new D3D12SceneRenderer(context, pipeline, output);
        }
        catch
        {
            pipeline?.Dispose();
            context.Dispose();
            throw;
        }
    }

    /// <summary>Somewhere to put a scene, on this renderer's device.</summary>
    /// <returns>Empty geometry.</returns>
    public SceneGeometry CreateGeometry() => _pipeline.CreateGeometry();

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

    /// <summary>Draws a scene and returns the picture.</summary>
    /// <param name="geometry">What to draw, already finished.</param>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <param name="camera">Where it is seen from.</param>
    /// <returns>The picture.</returns>
    /// <exception cref="D3D12Exception">Something on the device refused.</exception>
    public DecodedImage Render(SceneGeometry geometry, int width, int height, Camera camera)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(camera);
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Idempotent, and the renderer calling it is what the Vulkan one does too. A scene
        // that has not been finished has no materials, and a batch with no material is not
        // drawn - so forgetting this is a room that loads, reports its four hundred batches,
        // and draws none of them.
        geometry.Finish();

        (float R, float G, float B) clear = ClearColour;

        _pipeline.Prepare(width, height, clear, camera, geometry);
        Resize(width, height);

        ID3D12GraphicsCommandList4* list = _context.BeginOneShot();

        FramePicture picture = _pipeline.Draw(list, geometry, camera, clear);

        // --- the tone curve and the encode, at the size it will be shown ---
        _picture!.Transition(list, ResourceStates.RenderTarget);

        _output.Draw(
            list,
            [_target!.Cpu(0)],
            [picture.Colour],
            new OutputTuning(default, default),
            picture.Width,
            picture.Height);

        _context.EndOneShot();

        return D3D12Readback.Read(
            _context, _picture.Handle, _picture.State, _displayWidth, _displayHeight);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _context.Wait();
        _picture?.Dispose();
        _target?.Dispose();
        _output.Dispose();
        _pipeline.Dispose();
        _context.Dispose();
    }

    private void Resize(int width, int height)
    {
        if (_displayWidth == width && _displayHeight == height && _picture is not null)
        {
            return;
        }

        _context.Wait();
        _picture?.Dispose();
        _target?.Dispose();

        _displayWidth = width;
        _displayHeight = height;

        // The encode is the last thing and happens at display resolution, because everything
        // after the upscale is display-sized. See IRenderer, which keeps the two apart for
        // exactly this reason.
        _picture = D3D12Texture.CreateRenderTarget(
            _context, GBufferFormats.Picture, width, height);

        _target = D3D12DescriptorHeap.Create(_context.Device, DescriptorHeapType.Rtv, 1);
        _target.Allocate();

        _context.Device->CreateRenderTargetView(
            _picture.Handle, (RenderTargetViewDesc*)null, _target.Cpu(0));
    }

    /// <summary>What the output pass is told, which is nothing much without an upscaler.</summary>
    private readonly record struct OutputTuning(
        System.Numerics.Vector4 Tuning, System.Numerics.Vector4 Sharpen);
}
