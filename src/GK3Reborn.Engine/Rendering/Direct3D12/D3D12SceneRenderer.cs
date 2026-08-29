using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Rendering.Geometry;
using GK3Reborn.Rendering.Shaders;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;

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
/// Two passes, or three. The room is drawn once into the G-buffer — the picture, the
/// normals, the motion vectors — and the output applies the tone curve and the encode. The
/// composite between them exists only for the ray-traced path, because only that path has
/// anything to bring together: the traced pipeline writes light rather than a picture and
/// leaves the occlusion terms to be traced from its own depth and normals afterwards.
/// </para>
/// <para>
/// <b>The composite is not a pass that can be run with nothing to composite.</b> Given empty
/// targets it multiplies the picture by a shadow of zero and an occlusion of zero and returns
/// black, which is what it is for; it was tried, and a black frame is what came out. The
/// raster path therefore goes straight from the mesh pass to the output, which is what the
/// Vulkan path does and for the same reason.
/// </para>
/// </remarks>
public sealed unsafe class D3D12SceneRenderer : IOffscreenRenderer
{
    private readonly D3D12Context _context;
    private readonly D3D12GeometryDevice _geometry;
    private readonly ShaderCompiler _compiler;
    private readonly D3D12FrameSet _frames;
    private readonly D3D12MeshPass _mesh;
    private readonly D3D12ScreenPass _composite;
    private readonly D3D12ScreenPass _output;
    private readonly bool _rayTracing;

    private D3D12DescriptorHeap? _targets;
    private D3D12DescriptorHeap? _depths;
    private D3D12Texture[] _gbuffer = [];
    private D3D12Texture? _depth;
    private D3D12Texture? _lit;
    private D3D12Texture? _picture;
    private D3D12Texture? _empty;
    private int _width;
    private int _height;
    private (float R, float G, float B) _clear = (float.NaN, 0f, 0f);
    private bool _disposed;

    private D3D12SceneRenderer(
        D3D12Context context,
        D3D12GeometryDevice geometry,
        ShaderCompiler compiler,
        D3D12FrameSet frames,
        D3D12MeshPass mesh,
        D3D12ScreenPass composite,
        D3D12ScreenPass output,
        bool rayTracing)
    {
        _context = context;
        _geometry = geometry;
        _compiler = compiler;
        _frames = frames;
        _mesh = mesh;
        _composite = composite;
        _output = output;
        _rayTracing = rayTracing;
    }

    /// <inheritdoc/>
    public RenderBackend Backend => RenderBackend.Direct3D12;

    /// <inheritdoc/>
    public string DeviceName => _context.DeviceName;

    /// <inheritdoc/>
    public bool SupportsRayTracing => _rayTracing;

    /// <inheritdoc/>
    public RayTracingQuality Quality
    {
        get => _frames.Settings.Quality;
        set => _frames.Settings = RayTracingSettings.For(_rayTracing ? value : RayTracingQuality.None);
    }

    /// <inheritdoc/>
    public float Seconds
    {
        get => _frames.Seconds;
        set => _frames.Seconds = value;
    }

    /// <inheritdoc/>
    public SceneLightGrid? LightGrid => _frames.Grid;

    /// <summary>The seam a scene is put on this device through.</summary>
    public D3D12GeometryDevice Geometry => _geometry;

    /// <summary>The textures the device holds, shared by every scene this renderer draws.</summary>
    public TextureCache Textures =>
        field ??= new TextureCache(_geometry, SceneGeometry.CheckerBoard());

    /// <summary>How much tracing to do, and how.</summary>
    public RayTracingSettings Tracing
    {
        get => _frames.Settings;
        set => _frames.Settings = value;
    }

    /// <summary>What the picture is cleared to before anything is drawn.</summary>
    /// <remarks>
    /// Black for a real frame. Anything else is a diagnostic: a clear colour that survives to
    /// the picture proves the output encode and the readback, and leaves only the mesh pass to
    /// account for a room that did not appear.
    /// </remarks>
    public (float R, float G, float B) ClearColour { get; set; }

    /// <summary>What the last frame actually issued, for when a room does not appear.</summary>
    public string LastFrame =>
        $"{_mesh.Drawn} draws, {_mesh.Indices} indices, {_width}x{_height}";

    /// <summary>Everything the debug layer has said since it was last asked.</summary>
    public IReadOnlyList<string> Messages => _context.DrainMessages();

    /// <summary>Creates a headless renderer.</summary>
    /// <param name="rayTracing">Whether to build the ray-traced variant of the room's pipeline.</param>
    /// <returns>The renderer.</returns>
    /// <exception cref="D3D12Exception">There is no usable device.</exception>
    public static D3D12SceneRenderer Create(bool rayTracing = false)
    {
        D3D12Context context = D3D12Context.Create(enableValidation: true);

        try
        {
            // Not yet. The traced mesh variant writes unshadowed direct light and leaves the
            // occlusion to a tracing pass and a denoiser that this backend does not have, so
            // asking for it would give a room lit by half a calculation. The raster variant
            // draws the game correctly, which is the honest thing to offer until the rest is
            // here. See the Vulkan SceneRenderer, which falls back the same way when its own
            // deferred passes cannot be built.
            _ = rayTracing;
            const bool traced = false;

            D3D12GeometryDevice geometry = D3D12GeometryDevice.Create(context);
            var compiler = new ShaderCompiler(ShaderCompiler.DefaultCacheDirectory);

            Format[] colours = traced
                ?
                [
                    GBufferFormats.Light,
                    GBufferFormats.Normal,
                    GBufferFormats.Motion,
                    GBufferFormats.Light,
                ]
                : [GBufferFormats.Light, GBufferFormats.Normal, GBufferFormats.Motion];

            D3D12FrameSet frames = D3D12FrameSet.Create(context, geometry, frames: 1, traced);

            D3D12MeshPass mesh = D3D12MeshPass.Create(
                context, compiler, colours, GBufferFormats.Depth, traced);

            D3D12ScreenPass composite = D3D12ScreenPass.Create(
                context,
                compiler,
                CompositeShaders.Vertex,
                CompositeShaders.Fragment,
                "composite",
                inputs: 6,
                constantBytes: 4,
                [GBufferFormats.Light]);

            D3D12ScreenPass output = D3D12ScreenPass.Create(
                context,
                compiler,
                OutputShaders.Vertex,
                OutputShaders.Fragment,
                "output",
                inputs: 1,
                constantBytes: 32,
                [GBufferFormats.Picture]);

            return new D3D12SceneRenderer(
                context, geometry, compiler, frames, mesh, composite, output, traced);
        }
        catch
        {
            context.Dispose();
            throw;
        }
    }

    /// <summary>Somewhere to put a scene, on this renderer's device.</summary>
    /// <returns>Empty geometry.</returns>
    public SceneGeometry CreateGeometry() => SceneGeometry.Create(_geometry, Textures);

    /// <summary>Sets the lights anything without baked lighting is lit by.</summary>
    /// <param name="lights">The rig the scene was authored with.</param>
    /// <param name="scene">What the geometry occupies.</param>
    public void SetLights(
        IReadOnlyList<Formats.Scenes.AuthoredLight> lights, SceneExtent scene = default) =>
        _frames.SetLights(lights, scene);

    /// <summary>Draws a scene and returns the picture.</summary>
    /// <param name="geometry">What to draw, already finished.</param>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <param name="camera">Where it is seen from.</param>
    /// <returns>The picture.</returns>
    /// <exception cref="D3D12Exception">Something on the device refused.</exception>
    public DecodedImage Render(SceneGeometry geometry, int width, int height, Camera camera)
    {
        SceneGeometry scene = geometry;
        (float R, float G, float B) clear = ClearColour;

        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(camera);
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Idempotent, and the renderer calling it is what the Vulkan one does too. A scene
        // that has not been finished has no materials, and a batch with no material is not
        // drawn - so forgetting this is a room that loads, reports its four hundred batches,
        // and draws none of them.
        scene.Finish();

        Resize(width, height, clear);

        if (_rayTracing && scene.RayTracing is not null)
        {
            _frames.SetScene(scene.RayTracing);
        }

        _frames.Write(0, camera, (float)width / Math.Max(1, height), width, height);

        ID3D12GraphicsCommandList4* list = _context.BeginOneShot();

        // --- the room, into the G-buffer ---
        var colours = new CpuDescriptorHandle[_gbuffer.Length];
        float* black = stackalloc float[4] { clear.R, clear.G, clear.B, 1f };

        for (int i = 0; i < _gbuffer.Length; i++)
        {
            _gbuffer[i].Transition(list, ResourceStates.RenderTarget);
            colours[i] = _targets!.Cpu((uint)i);
            list->ClearRenderTargetView(colours[i], black, 0, (Silk.NET.Maths.Box2D<int>*)null);
        }

        _depth!.Transition(list, ResourceStates.DepthWrite);
        CpuDescriptorHandle depth = _depths!.Cpu(0);
        list->ClearDepthStencilView(depth, ClearFlags.Depth, 1f, 0, 0, (Silk.NET.Maths.Box2D<int>*)null);

        fixed (CpuDescriptorHandle* first = colours)
        {
            list->OMSetRenderTargets((uint)colours.Length, first, false, &depth);
        }

        _mesh.Begin(list, _geometry, _frames.Table(0), width, height);
        _mesh.Record(list, _geometry, scene.Draws(_frames.PreviousSeconds));

        // --- the traced light added to the raster picture, where there is any ---
        foreach (D3D12Texture target in _gbuffer)
        {
            target.Transition(list, ResourceStates.AllShaderResource);
        }

        D3D12Texture finished = _gbuffer[0];

        if (_rayTracing)
        {
            _lit!.Transition(list, ResourceStates.RenderTarget);

            D3D12Texture[] inputs =
            [
                _gbuffer[0],
                _gbuffer[3],
                _empty!,
                _empty!,
                _empty!,
                _empty!,
            ];

            _composite.Draw(list, [_targets!.Cpu(Slots.Lit)], inputs, 0f, width, height);
            _lit.Transition(list, ResourceStates.AllShaderResource);
            finished = _lit;
        }

        // --- the tone curve and the encode ---
        _picture!.Transition(list, ResourceStates.RenderTarget);

        _output.Draw(
            list,
            [_targets!.Cpu(Slots.Picture)],
            [finished],
            new OutputTuning(default, default),
            width,
            height);

        _context.EndOneShot();

        return D3D12Readback.Read(_context, _picture.Handle, _picture.State, width, height);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        Textures.Dispose();
        Release();

        _output.Dispose();
        _composite.Dispose();
        _mesh.Dispose();
        _frames.Dispose();
        _compiler.Dispose();
        _geometry.Dispose();
        _context.Dispose();
    }

    /// <summary>Where each render target view sits in the pass's own heap.</summary>
    private static class Slots
    {
        /// <summary>After the G-buffer, whose targets take the first four.</summary>
        internal const uint Lit = GBufferFormats.Targets;

        /// <summary>And the encoded picture after that.</summary>
        internal const uint Picture = GBufferFormats.Targets + 1;
    }

    /// <summary>What the output pass is told, which is nothing much without an upscaler.</summary>
    private readonly record struct OutputTuning(
        System.Numerics.Vector4 Tuning, System.Numerics.Vector4 Sharpen);

    private void Resize(int width, int height, (float R, float G, float B) clear)
    {
        // The clear colour is part of this, not a per-frame argument. A render target carries
        // the value it expects to be cleared with, and clearing it with another takes the slow
        // path on every driver — which the debug layer says three times a frame.
        if (_width == width && _height == height && _clear == clear && _picture is not null)
        {
            return;
        }

        _context.Wait();
        Release();

        _width = width;
        _height = height;
        _clear = clear;

        int targets = _rayTracing ? 4 : 3;
        _gbuffer = new D3D12Texture[targets];

        (float R, float G, float B, float A) opaque = (clear.R, clear.G, clear.B, 1f);

        _gbuffer[0] = D3D12Texture.CreateRenderTarget(
            _context, GBufferFormats.Light, width, height, opaque);

        _gbuffer[1] = D3D12Texture.CreateRenderTarget(
            _context, GBufferFormats.Normal, width, height, opaque);

        _gbuffer[2] = D3D12Texture.CreateRenderTarget(
            _context, GBufferFormats.Motion, width, height, opaque);

        if (targets > 3)
        {
            _gbuffer[3] = D3D12Texture.CreateRenderTarget(
                _context, GBufferFormats.Light, width, height, opaque);
        }

        _depth = D3D12Texture.CreateDepthTarget(_context, GBufferFormats.Depth, width, height);
        _lit = D3D12Texture.CreateRenderTarget(_context, GBufferFormats.Light, width, height);
        _picture = D3D12Texture.CreateRenderTarget(_context, GBufferFormats.Picture, width, height);

        // One texel of nothing, bound wherever a pass reads a target that does not exist
        // yet. Both APIs require every declared binding to point at something valid even
        // when the shader multiplies what it reads by zero.
        _empty = D3D12TextureUpload.Create(
            _context,
            new DecodedImage(1, 1, [0, 0, 0, 0], HasAlpha: true, "empty"),
            mipmaps: false,
            linear: true);

        _targets = D3D12DescriptorHeap.Create(
            _context.Device, DescriptorHeapType.Rtv, GBufferFormats.Targets + 2);

        _depths = D3D12DescriptorHeap.Create(_context.Device, DescriptorHeapType.Dsv, 1);

        for (uint i = 0; i < GBufferFormats.Targets; i++)
        {
            _targets.Allocate();
        }

        _targets.Allocate();
        _targets.Allocate();

        for (int i = 0; i < _gbuffer.Length; i++)
        {
            _context.Device->CreateRenderTargetView(
                _gbuffer[i].Handle, (RenderTargetViewDesc*)null, _targets.Cpu((uint)i));
        }

        _context.Device->CreateRenderTargetView(
            _lit.Handle, (RenderTargetViewDesc*)null, _targets.Cpu(Slots.Lit));

        _context.Device->CreateRenderTargetView(
            _picture.Handle, (RenderTargetViewDesc*)null, _targets.Cpu(Slots.Picture));

        _depths.Allocate();
        _context.Device->CreateDepthStencilView(
            _depth.Handle, (DepthStencilViewDesc*)null, _depths.Cpu(0));
    }

    private void Release()
    {
        foreach (D3D12Texture target in _gbuffer)
        {
            target.Dispose();
        }

        _gbuffer = [];

        _depth?.Dispose();
        _lit?.Dispose();
        _picture?.Dispose();
        _empty?.Dispose();
        _targets?.Dispose();
        _depths?.Dispose();

        _depth = null;
        _lit = null;
        _picture = null;
        _empty = null;
        _targets = null;
        _depths = null;
    }
}
