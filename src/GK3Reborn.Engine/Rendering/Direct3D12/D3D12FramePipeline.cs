// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Rendering.Geometry;
using GK3Reborn.Rendering.Shaders;
using GK3Reborn.Rendering.Upscaling;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;
using System.Numerics;

namespace GK3Reborn.Rendering.Direct3D12;

/// <summary>A room, drawn and brought together into one picture.</summary>
/// <param name="Colour">The picture, in a state a shader can read.</param>
/// <param name="Width">How wide it is, which is the display size when an upscaler ran.</param>
/// <param name="Height">And how tall.</param>
public readonly record struct FramePicture(D3D12Texture Colour, int Width, int Height);

/// <summary>
/// Everything between a scene and a finished picture, on Direct3D.
/// </summary>
/// <remarks>
/// <para>
/// The room into the G-buffer, the traced occlusion over it, the composite that brings the
/// two together, and the upscale. What it does not do is put the result anywhere: a
/// reference render reads it back and a game presents it, and those are the only two things
/// that differ between the headless renderer and the windowed one.
/// </para>
/// <para>
/// <b>The encode is deliberately not here.</b> The output pass is a graphics pipeline and a
/// graphics pipeline is built for one render target format; a picture read back is sRGB and
/// a swapchain may be sRGB, plain or ten-bit, and changes under the window when the display
/// does. Whoever owns the surface owns the pass that writes it.
/// </para>
/// <para>
/// <b>The composite is not a pass that can be run with nothing to composite.</b> Given empty
/// targets it multiplies the picture by a shadow of zero and an occlusion of zero and
/// returns black, which is what it is for; it was tried, and a black frame is what came out.
/// So the raster path goes straight from the room to the picture, which is what the Vulkan
/// path does and for the same reason.
/// </para>
/// </remarks>
public sealed unsafe class D3D12FramePipeline : IDisposable
{
    private readonly D3D12Context _context;
    private readonly D3D12GeometryDevice _geometry;
    private readonly ShaderCompiler _compiler;
    private readonly D3D12FrameSet _frames;
    private readonly D3D12MeshPass _mesh;
    private readonly D3D12ScreenPass _composite;
    private readonly bool _rayTracing;

    private D3D12DescriptorHeap? _targets;
    private D3D12DescriptorHeap? _depths;
    private D3D12Texture[] _gbuffer = [];
    private D3D12Texture? _depth;
    private D3D12ParticlePass? _particlePass;
    private IReadOnlyList<Particle> _particles = [];
    private D3D12Texture? _lit;

    /// <summary>The room as this frame's mirror sees it, if the room has one.</summary>
    private D3D12Texture? _mirror;

    /// <summary>How much of a reflection to show, and where the floors get theirs from.</summary>
    /// <remarks>
    /// Set by the renderer, which owns the plan; read here, which is where the passes are
    /// recorded. Clamped by the plan itself.
    /// </remarks>
    public ReflectionPlan Reflections { get; set; } = ReflectionPlan.Default;

    private D3D12Texture? _empty;
    private D3D12Texture? _upscaled;
    private D3D12SkyboxPass? _skybox;

    /// <summary>The reconstructed horizon, where the room has one.</summary>
    /// <remarks>
    /// Kept beside the cubemap rather than instead of it: the painted sky is the fallback
    /// for a backdrop that would not build, and a room with neither is a room.
    /// </remarks>
    private D3D12TerrainPass? _terrain;
    private SceneGeometry? _skyOwner;
    private D3D12ShadowDenoiser? _denoiser;
    private D3D12Reflections? _reflections;
    private bool _denoiserBound;
    private D3D12DlssUpscaler? _dlss;
    private D3D12NeuralRenderer? _neural;
    private bool _neuralRefused;
    private D3D12FsrUpscaler? _fsr;
    private Streamline? _streamline;
    private UpscalerRuntimes? _runtimes;
    private UpscalePlan? _plan;
    private int _width;
    private int _height;
    private int _displayWidth;
    private int _displayHeight;
    private long _frame;
    private Vector2 _jitter;
    private (float R, float G, float B) _clear = (float.NaN, 0f, 0f);
    private bool _disposed;

    private D3D12FramePipeline(
        D3D12Context context,
        D3D12GeometryDevice geometry,
        ShaderCompiler compiler,
        D3D12FrameSet frames,
        D3D12MeshPass mesh,
        D3D12ScreenPass composite,
        bool rayTracing)
    {
        _context = context;
        _geometry = geometry;
        _compiler = compiler;
        _frames = frames;
        _mesh = mesh;
        _composite = composite;
        _rayTracing = rayTracing;
    }

    /// <summary>The seam a scene is put on this device through.</summary>
    public D3D12GeometryDevice Geometry => _geometry;

    /// <summary>Where the shaders come from, shared with whoever owns the encode.</summary>
    public ShaderCompiler Compiler => _compiler;

    /// <summary>The frame's uniforms, lights and scene.</summary>
    public D3D12FrameSet Frames => _frames;

    /// <summary>Whether the ray-traced variant was built.</summary>
    public bool RayTracing => _rayTracing;

    /// <summary>The textures the device holds, shared by every scene this draws.</summary>
    public TextureCache Textures =>
        field ??= new TextureCache(_geometry, SceneGeometry.CheckerBoard());

    /// <summary>The size the room is actually drawn at.</summary>
    public (int Width, int Height) RenderSize => (_width, _height);

    /// <summary>What DLSS was asked to do, or null to draw at display resolution.</summary>
    public UpscalePlan? Upscaling
    {
        get => _plan;
        set
        {
            if (_plan == value)
            {
                return;
            }

            _plan = value;
            _dlss?.Dispose();
            _dlss = null;
            _fsr?.Dispose();
            _fsr = null;

            // A refusal is forgotten with the plan that caused it, so that a player who
            // changes something and tries again is answered afresh rather than by a decision
            // taken about a different setting.
            _neuralRefused = false;

            // The neural network is deliberately left standing. Most of what a player can
            // change about it — every strength, the skin controls, the style — is read
            // afresh each frame and changes nothing the feature was built around, and
            // tearing it down for a slider step would stall the queue and drop the history
            // under their hand. Whether the standing one still serves is asked below, where
            // the sizes are known.

            // Forgotten rather than compared: a plan that changed may not have changed these
            // two, but a runtime that refused them last time should be asked again.
            _generating = -1;
            _latency = uint.MaxValue;
        }
    }

    /// <summary>What the upscaler is doing, for the startup report.</summary>
    public string? UpscalerNote =>
        _neural?.Describe() ?? _dlss?.Describe() ?? _fsr?.Describe();

    /// <summary>Whether DLSS is available on this machine at all.</summary>
    public bool HasDlss => _streamline is { Ready: true };

    /// <summary>Whether the runtime offers frame generation.</summary>
    public bool HasFrameGeneration => _streamline is { HasFrameGeneration: true };

    /// <summary>
    /// The runtime itself, for the two things that are not upscaling.
    /// </summary>
    /// <remarks>
    /// The swapchain needs it before it is created, because a chain Streamline did not make
    /// cannot have frames generated into it; and the renderer needs it every frame, for the
    /// sleep and the markers. Both are outside what a frame pipeline is about, so it hands
    /// the runtime over rather than growing methods that only pass through.
    /// </remarks>
    public Streamline? Streamline => _streamline;

    /// <summary>This frame's depth, for a pass that runs after the room is finished.</summary>
    /// <remarks>
    /// Lent rather than given. The neural uplift runs in the renderer, after the picture has
    /// been tone-mapped onto the back buffer, but it still wants the two guides every temporal
    /// pass wants — and those belong to the room, which is drawn here. They are at the size
    /// the room was drawn at, which is why the uplift only runs when that is also the size the
    /// picture is shown at.
    /// </remarks>
    public D3D12Texture? Guides => _depth;

    /// <summary>This frame's motion vectors, in render-resolution pixels.</summary>
    public D3D12Texture? Motion => _gbuffer.Length > 2 ? _gbuffer[2] : null;

    /// <summary>Where inside its pixel this frame sampled.</summary>
    public Vector2 JitterPixels => _jitter;

    /// <summary>Whether the runtime offers ray reconstruction.</summary>
    public bool HasRayReconstruction => _streamline is { HasRayReconstruction: true };

    /// <summary>How long since the last frame, which every temporal upscaler asks for.</summary>
    public float DeltaSeconds { get; set; } = 1f / 60f;

    /// <summary>Whether what the upscaler remembers about the last frame is worthless.</summary>
    /// <remarks>
    /// A cut, a new room, a resize. Cleared once the runtime has been told, because it is a
    /// statement about one frame rather than a mode. An upscaler that is never told smears
    /// the last room across the first frame of the next one.
    /// </remarks>
    public bool Reset { get; set; } = true;

    /// <summary>What the last frame actually issued, for when a room does not appear.</summary>
    public string LastFrame =>
        $"{_mesh.Drawn} draws, {_mesh.Indices} indices, {_width}x{_height}";

    /// <summary>A texel of nothing, for a binding that has to point somewhere.</summary>
    public D3D12Texture? Empty => _empty;

    /// <summary>Builds every pass and the device's geometry seam.</summary>
    /// <param name="context">The device.</param>
    /// <param name="rayTracing">Whether to build the ray-traced variant of the room's pass.</param>
    /// <param name="runtimes">Where the upscaler runtimes are, or null to look beside the executable.</param>
    /// <returns>The pipeline.</returns>
    /// <exception cref="D3D12Exception">Something could not be built.</exception>
    public static D3D12FramePipeline Create(
        D3D12Context context, bool rayTracing, string? runtimes = null)
    {
        ArgumentNullException.ThrowIfNull(context);

        // The traced mesh variant writes unshadowed direct light into a fourth target and
        // leaves the occlusion to the tracing pass and the denoiser, so it is only worth
        // building where those can run. A device with no inline ray tracing gets the raster
        // variant, which draws the game correctly.
        bool traced = rayTracing && context.SupportsRayTracing;

        D3D12GeometryDevice geometry = D3D12GeometryDevice.Create(context);
        var compiler = new ShaderCompiler(ShaderCompiler.DefaultCacheDirectory) { DxilShaderModel = context.DxilShaderModel };

        // Started after the device rather than before it, which is the other way round from
        // Vulkan. There, Streamline has to be consulted while the device is being created
        // because its features ask for extensions and queues; here it is told about a device
        // that already exists.
        UpscalerRuntimes found = UpscalerRuntimes.Find(runtimes);

        Streamline? streamline = Streamline.TryStart(
            found, Streamline.RenderApiDirect3D12);

        if (streamline is not null &&
            !streamline.AttachDirect3D((nint)context.Device, context.AdapterLuid))
        {
            streamline.Dispose();
            streamline = null;
        }

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

        return new D3D12FramePipeline(
            context, geometry, compiler, frames, mesh, composite, traced)
        {
            _streamline = streamline,
            _runtimes = found,
        };
    }

    /// <summary>Somewhere to put a scene, on this device.</summary>
    /// <returns>Empty geometry.</returns>
    public SceneGeometry CreateGeometry() => SceneGeometry.Create(_geometry, Textures);

    /// <summary>
    /// Sizes the frame's targets and works out where inside its pixel this frame samples.
    /// </summary>
    /// <param name="displayWidth">The width the picture will be shown at.</param>
    /// <param name="displayHeight">And the height.</param>
    /// <param name="clear">What the room is cleared to.</param>
    /// <param name="camera">The camera, whose jitter this sets.</param>
    /// <param name="scene">What is being drawn, for its acceleration structure.</param>
    /// <returns>The size the room will be drawn at.</returns>
    /// <remarks>
    /// Before the frame's command list rather than inside it, because sizing the targets can
    /// mean building new ones and clearing them, which is work of its own.
    /// </remarks>
    public (int Width, int Height) Prepare(
        int displayWidth,
        int displayHeight,
        (float R, float G, float B) clear,
        Camera camera,
        SceneGeometry? scene)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(camera);

        // The room is drawn at the render size and shown at the display size, and they are
        // the same number only when nothing is upscaling. Everything between the first
        // triangle and the upscale is the first; everything after it is the second.
        (int renderWidth, int renderHeight) = Upscaling is { Active: true } plan
            ? plan.RenderSize(displayWidth, displayHeight)
            : (displayWidth, displayHeight);

        Resize(renderWidth, renderHeight, displayWidth, displayHeight, clear);
        PrepareUpscaler(renderWidth, renderHeight, displayWidth, displayHeight);

        // A temporal upscaler reconstructs detail from where the samples fell over several
        // frames, so a camera that never moves its sample point gives it the same picture
        // every frame and nothing to reconstruct from — a soft image that never sharpens,
        // which reads as the upscaler not working.
        // Super resolution wants it and the neural uplift does not care, because the uplift
        // runs after the tone map on a picture this has already resolved. The hand-driven
        // network is the exception: it still sits in this slot, and it is told nothing about
        // jitter.
        if (_dlss is not null || (_neural is not null && D3D12NeuralRenderer.WantsJitter))
        {
            int phases = JitterSequence.PhaseCount(renderWidth, displayWidth);
            _jitter = JitterSequence.Offset(_frame, phases);
            camera.Jitter = JitterSequence.ToClip(_jitter, renderWidth, renderHeight);
        }
        else
        {
            _jitter = Vector2.Zero;
            camera.Jitter = Vector2.Zero;
        }

        _frames.JitterPixels = _jitter;
        _frame++;

        if (_rayTracing && scene?.RayTracing is not null)
        {
            _frames.SetScene(scene.RayTracing);
        }

        AdoptSky(scene);

        _frames.Write(
            0, camera, (float)renderWidth / Math.Max(1, renderHeight), renderWidth, renderHeight);

        return (renderWidth, renderHeight);
    }


    /// <summary>
    /// Draws the room as this frame's mirror sees it, before the room itself is drawn.
    /// </summary>
    /// <param name="list">Command list to record into.</param>
    /// <param name="scene">What to draw, or null for an empty frame.</param>
    /// <param name="camera">Where the room is seen from.</param>
    /// <param name="width">Render width in pixels.</param>
    /// <param name="height">Render height in pixels.</param>
    /// <remarks>
    /// <para>
    /// <b>It borrows the frame's own normal, motion and depth targets.</b> The pipeline
    /// declares all of them and a draw has to bind every target its pipeline writes, so a
    /// reflection cannot be drawn into a colour target alone — and it does not need targets
    /// of its own, because the pass that follows clears and overwrites all of them. One
    /// extra image for the whole feature, and nothing downstream ever sees the reflection's
    /// normals.
    /// </para>
    /// <para>
    /// No sky, and no compositing. The reflected camera stands behind the mirror and the sky
    /// is drawn without regard to the clip plane, so it would paint over the reflection from
    /// the far side of the wall the mirror hangs on; and what the glass shows is sampled
    /// directly, so it is the mesh pass's own picture rather than a traced one finished by a
    /// later pass. The reflection is therefore lit by the rig without traced shadows even at
    /// High — a real difference from the room around it, and a small one at the size a
    /// mirror is drawn.
    /// </para>
    /// </remarks>
    private void RecordReflection(
        ID3D12GraphicsCommandList4* list,
        SceneGeometry? scene,
        Camera camera,
        int width,
        int height)
    {
        if (scene is null || _mirror is null || _targets is null || _depths is null)
        {
            return;
        }

        if (scene.ChooseMirror(camera.Position, Reflections.PlanarFloors) is not { } mirror)
        {
            // Nothing in the room is a mirror this frame, so nothing will sample the
            // target — but it is still a descriptor the mesh pass binds, and a resource a
            // bound descriptor names has to be in a state the shader could read it from
            // whether or not the branch behind it runs. It comes back zeroed, being a
            // committed resource, so a black reflection is the worst it can ever be.
            _mirror.Transition(list, ResourceStates.PixelShaderResource);
            _frames.MirrorPlane = Vector4.Zero;

            return;
        }

        _mirror.Transition(list, ResourceStates.RenderTarget);

        float* nothing = stackalloc float[4] { 0f, 0f, 0f, 1f };
        CpuDescriptorHandle mirrorTarget = _targets.Cpu(Slots.Mirror);
        list->ClearRenderTargetView(mirrorTarget, nothing, 0, (Silk.NET.Maths.Box2D<int>*)null);

        var colours = new CpuDescriptorHandle[_gbuffer.Length];
        colours[0] = mirrorTarget;

        for (int i = 1; i < _gbuffer.Length; i++)
        {
            _gbuffer[i].Transition(list, ResourceStates.RenderTarget);
            colours[i] = _targets.Cpu((uint)i);
            list->ClearRenderTargetView(colours[i], nothing, 0, (Silk.NET.Maths.Box2D<int>*)null);
        }

        _depth!.Transition(list, ResourceStates.DepthWrite);
        CpuDescriptorHandle depth = _depths.Cpu(0);
        list->ClearDepthStencilView(
            depth, ClearFlags.Depth, 1f, 0, 0, (Silk.NET.Maths.Box2D<int>*)null);

        fixed (CpuDescriptorHandle* first = colours)
        {
            list->OMSetRenderTargets((uint)colours.Length, first, false, &depth);
        }

        // Not jittered. The jitter turns a sequence of frames into a denser sampling of one
        // picture, and only the picture the player sees is accumulated; carried over, it
        // shakes the reflection by half a pixel against the mirror holding it.
        Vector2 jitter = camera.Jitter;
        camera.Jitter = Vector2.Zero;

        Camera mirrored = camera.Mirrored(mirror.Plane);

        _frames.MirrorPlane = mirror.Plane;
        _frames.Write(
            0, mirrored, (float)width / Math.Max(1, height), width, height, reflection: true);

        camera.Jitter = jitter;
        _frames.MirrorPlane = Vector4.Zero;

        _mesh.Begin(list, _geometry, _frames.Table(0, reflection: true), width, height);
        _mesh.Record(list, _geometry, scene.Draws(_frames.PreviousSeconds));

        _mirror.Transition(list, ResourceStates.PixelShaderResource);
    }

    /// <summary>Records the room, the tracing and the composite.</summary>
    /// <param name="list">Command list to record into.</param>
    /// <param name="scene">What to draw, already finished, or null for an empty frame.</param>
    /// <param name="camera">Where it is seen from.</param>
    /// <param name="clear">What the room is cleared to.</param>
    /// <returns>The finished picture and the size it should be shown at.</returns>
    /// <remarks>
    /// <see cref="Prepare"/> must have been called for this frame first; it is what decides
    /// the size everything here is drawn at.
    /// </remarks>
    public FramePicture Draw(
        ID3D12GraphicsCommandList4* list,
        SceneGeometry? scene,
        Camera camera,
        (float R, float G, float B) clear)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(camera);

        int width = _width;
        int height = _height;

        RecordReflection(list, scene, camera, width, height);

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
        list->ClearDepthStencilView(
            depth, ClearFlags.Depth, 1f, 0, 0, (Silk.NET.Maths.Box2D<int>*)null);

        fixed (CpuDescriptorHandle* first = colours)
        {
            list->OMSetRenderTargets((uint)colours.Length, first, false, &depth);
        }

        _mesh.Begin(list, _geometry, _frames.Table(0), width, height);

        if (scene is not null)
        {
            _mesh.Record(list, _geometry, scene.Draws(_frames.PreviousSeconds));
        }

        // --- the horizon, where this pass is the one producing the picture ---
        //
        // The traced path has a compositing pass to run first and draws its sky over the
        // result, because the sky is not something the compositing pass has any parts for.
        if (!_rayTracing)
        {
            RecordSky(list, _targets!.Cpu(0), camera, width, height);
        }

        // --- the traced light added to the raster picture, where there is any ---
        foreach (D3D12Texture target in _gbuffer)
        {
            target.Transition(list, ResourceStates.AllShaderResource);
        }

        D3D12Texture finished = _gbuffer[0];

        if (_rayTracing)
        {
            finished = Compose(list, scene, camera, width, height);
        }

        // --- the room's smoke and embers, over the finished picture ---
        RecordParticles(list, finished, camera, width, height);

        // --- the upscale, where one was asked for ---
        int shownWidth = _displayWidth;
        int shownHeight = _displayHeight;

        if (_dlss is not null || _neural is not null || _fsr is not null)
        {
            // The states are not decorative. Both runtimes are told what each texture is in
            // and believe it: a wrong one is a read through a barrier nobody issued, and a
            // frame built partly out of whatever was there before.
            finished.Transition(list, ResourceStates.NonPixelShaderResource);
            _depth.Transition(list, ResourceStates.NonPixelShaderResource);
            _gbuffer[2].Transition(list, ResourceStates.NonPixelShaderResource);
            _upscaled!.Transition(list, ResourceStates.UnorderedAccess);

            var described = new StreamlineFrame(
                default,
                default,
                default,
                default,
                _jitter,
                DeltaSeconds,
                Reset,
                camera,
                (float)width / Math.Max(1, height),
                Upscaling?.Sharpen ?? false,
                Upscaling?.Sharpness ?? 0f,
                Upscaling?.HighDynamicRange ?? false);

            bool upscaled = _neural is not null
                ? _neural.Record(list, finished, _depth, _gbuffer[2], _upscaled!, described)
                : _dlss is not null
                    ? _dlss.Record(list, finished, _depth, _gbuffer[2], _upscaled!, described)
                    : _fsr!.Record(list, finished, _depth, _gbuffer[2], _upscaled!, described);

            if (upscaled)
            {
                _upscaled.Transition(list, ResourceStates.AllShaderResource);
                finished = _upscaled;
                Reset = false;
            }
            else
            {
                // The runtime refused the frame. Drawing the small picture stretched is
                // better than drawing nothing, and the note says which happened.
                shownWidth = width;
                shownHeight = height;
                finished.Transition(list, ResourceStates.AllShaderResource);
            }
        }
        else
        {
            finished.Transition(list, ResourceStates.AllShaderResource);
            shownWidth = width;
            shownHeight = height;
        }

        return new FramePicture(finished, shownWidth, shownHeight);
    }

    /// <summary>Forgets every target, so the next frame builds them again.</summary>
    /// <remarks>
    /// What a reference render does between two scenes. The denoiser and the reflection pass
    /// both remember the frame before, and the first frame of a new room must not be able to
    /// see the last frame of the old one.
    /// </remarks>
    public void Forget()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _context.Wait();
        Release();

        _width = 0;
        _height = 0;
        Reset = true;
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

        _streamline?.Dispose();
        _composite.Dispose();
        _mesh.Dispose();
        _frames.Dispose();
        _particlePass?.Dispose();
        _particlePass = null;
        _compiler.Dispose();
        _geometry.Dispose();
    }

    /// <summary>Builds the sky the room names, the first time that room is drawn.</summary>
    /// <param name="scene">What is being drawn.</param>
    /// <remarks>
    /// Here rather than on a setter the game calls, because the sky is a property of the
    /// scene and the scene is loaded by something that has no shader compiler and no target
    /// formats. The same place the Vulkan renderer does it, and for the same reason.
    /// </remarks>
    private void AdoptSky(SceneGeometry? scene)
    {
        if (ReferenceEquals(scene, _skyOwner))
        {
            return;
        }

        _context.Wait();
        _skyOwner = scene;
        _skybox?.Dispose();
        _skybox = null;
        _terrain?.Dispose();
        _terrain = null;

        if (scene?.SkyboxFaces is { Count: 6 } faces)
        {
            try
            {
                _skybox = D3D12SkyboxPass.Create(
                    _context,
                    _compiler,
                    GBufferFormats.Light,
                    GBufferFormats.Depth,
                    faces,
                    scene.SkyboxAzimuth);
            }
            catch (D3D12Exception)
            {
                // A room without a sky is a room; a room that will not draw is not.
                _skybox = null;
            }
        }

        if (scene?.Terrain is { } backdrop)
        {
            try
            {
                _terrain = D3D12TerrainPass.Create(
                    _context,
                    _compiler,
                    GBufferFormats.Light,
                    GBufferFormats.Depth,
                    backdrop);
            }
            catch (Exception exception) when (
                exception is D3D12Exception or ShaderCompilationException or ArgumentException)
            {
                // The painted sky is still there behind it, so a horizon that will not build
                // is a horizon the player already had.
                _terrain = null;
            }
        }
    }

    /// <summary>Gives the room its smoke and embers.</summary>
    /// <param name="particles">The particles, furthest from the eye first.</param>
    public void SetParticles(IReadOnlyList<Particle> particles)
    {
        ArgumentNullException.ThrowIfNull(particles);
        _particles = particles;
    }

    /// <summary>
    /// Draws the room's smoke and embers over the picture it has just made.
    /// </summary>
    /// <param name="list">Command list to record into.</param>
    /// <param name="finished">The picture, whichever target it ended up in.</param>
    /// <param name="camera">Where the frame was looked at from.</param>
    /// <param name="width">Render width.</param>
    /// <param name="height">Its height.</param>
    /// <remarks>
    /// After the picture and before the upscale. The Vulkan renderer says why it goes here
    /// and what it costs a temporal upscaler; see <c>VulkanRenderer.RecordParticles</c>.
    /// </remarks>
    private void RecordParticles(
        ID3D12GraphicsCommandList4* list,
        D3D12Texture finished,
        Camera camera,
        int width,
        int height)
    {
        if (_particles.Count == 0 || _targets is null || _depths is null || _depth is null)
        {
            return;
        }

        _particlePass ??= D3D12ParticlePass.Create(
            _context, _compiler, GBufferFormats.Light, GBufferFormats.Depth, frames: 1);

        _particlePass.Prepare(_particles, 0);

        if (_particlePass.Count == 0)
        {
            return;
        }

        finished.Transition(list, ResourceStates.RenderTarget);

        // Read rather than written, the same as the painted sky above: a sprite is tested
        // against the room and adds nothing to it.
        _depth.Transition(list, ResourceStates.DepthRead);

        _particlePass.Record(
            list,
            _targets.Cpu(_rayTracing ? Slots.Lit : 0),
            _depths.Cpu(0),
            width,
            height,
            ParticleShaders.Describe(
                camera,
                camera.View * camera.Projection((float)width / Math.Max(1, height)),
                _frames.EmissiveGain));
    }

    private D3D12Texture Compose(
        ID3D12GraphicsCommandList4* list,
        SceneGeometry? scene,
        Camera camera,
        int width,
        int height)
    {
        RayTracingSettings settings = _frames.Settings;

        // What the denoiser found, or a texel of nothing where there is no denoiser or
        // nothing to trace against. The composite reads all six either way: a shader binding
        // that points at nothing is not a dark pixel, it is a device removed.
        D3D12Texture shadow = _empty!;
        D3D12Texture occlusion = _empty!;
        D3D12Texture dynamic = _empty!;
        D3D12Texture reflected = _empty!;

        if (_denoiser is not null && scene?.RayTracing is D3D12GeometryStructure built)
        {
            D3D12AccelerationStructure structure = built.Structure;

            // Once for a set of targets. Rewriting a descriptor the device may still be
            // reading is the same hazard as rewriting a vertex buffer mid-frame, and the
            // targets only change when the viewport does — which releases the denoiser along
            // with them. The structure is the one thing that can change under a bound
            // descriptor, and Point is what notices.
            if (!_denoiserBound)
            {
                _denoiserBound = true;
                _denoiser.Bind(_depth!, _gbuffer[1], _gbuffer[2], structure, _frames.Rig(0));
            }

            _denoiser.Point(structure);

            _denoiser.Record(
                list,
                camera,
                _depth!,
                _gbuffer[1],
                _gbuffer[2],
                settings.AmbientOcclusionRadius,
                settings.OcclusionSamples);

            shadow = _denoiser.Shadow;
            occlusion = _denoiser.Occlusion;
            dynamic = _denoiser.DynamicShadow;

            if (_reflections is not null)
            {
                // What it marches over is the finished picture of the frame before, which on
                // the first frame after a resize is a target nothing has drawn into yet.
                // Resize clears it for exactly that frame.
                _reflections.Bind(_depth!, _gbuffer[1], _gbuffer[2], _lit!, _mirror);

                // The plane, where the frame rendered one. A pixel lying on it takes the
                // planar answer outright and is not marched, which is the whole of what
                // makes a floor able to show the ceiling above it.
                _reflections.Record(
                    list,
                    camera,
                    _depth!,
                    _gbuffer[1],
                    _gbuffer[2],
                    _lit!,
                    Materials.SurfaceFinish.Roughest,
                    Reflections.Strength,
                    scene?.Mirror is { } plane ? plane.Plane : Vector4.Zero);

                reflected = _reflections.Reflected;
            }

            // The denoiser reads the G-buffer as a compute shader does and the composite
            // reads it as a fragment shader does, which are different states.
            foreach (D3D12Texture target in _gbuffer)
            {
                target.Transition(list, ResourceStates.AllShaderResource);
            }
        }

        _lit!.Transition(list, ResourceStates.RenderTarget);

        D3D12Texture[] inputs =
        [
            _gbuffer[0],
            _gbuffer[3],
            shadow,
            occlusion,
            reflected,
            dynamic,
        ];

        _composite.Draw(
            list,
            [_targets!.Cpu(Slots.Lit)],
            inputs,
            settings.OcclusionStrength,
            width,
            height);

        // Over the composite, and still against the room's own depth so it fills only what
        // the room left empty.
        RecordSky(list, _targets.Cpu(Slots.Lit), camera, width, height);

        return _lit;
    }

    private void RecordSky(
        ID3D12GraphicsCommandList4* list,
        CpuDescriptorHandle target,
        Camera camera,
        int width,
        int height)
    {
        if (_terrain is null && _skybox is null)
        {
            return;
        }

        // The room's own depth, so the horizon can still be told where the room is. Read
        // where the painted cubemap is what draws — it sits at the far plane and there is
        // nothing after it that could lose to it in turn — and written where the
        // reconstruction is, because a backdrop four kilometres deep has to sort against
        // itself inside the far tail of the buffer.
        _depth!.Transition(
            list, _terrain is not null ? ResourceStates.DepthWrite : ResourceStates.DepthRead);

        CpuDescriptorHandle depth = _depths!.Cpu(0);
        list->OMSetRenderTargets(1, &target, false, &depth);

        // The reconstructed backdrop brings its own sky, and the painted cubemap must not
        // draw behind it — its mountains are baked into the picture and would double-expose
        // against the real ridge. The cubemap is the fallback for a backdrop that would not
        // build, nothing more.
        if (_terrain is not null)
        {
            _terrain.Record(list, camera, width, height);
        }
        else
        {
            _skybox!.Record(list, camera, width, height);
        }
    }

    private void Resize(
        int width, int height, int displayWidth, int displayHeight, (float R, float G, float B) clear)
    {
        // The clear colour is part of this, not just the size. A target is created with the
        // value it expects to be cleared with, and clearing it with another takes the slow
        // path on every driver — which the debug layer says three times a frame.
        if (_width == width &&
            _height == height &&
            _displayWidth == displayWidth &&
            _displayHeight == displayHeight &&
            _clear == clear &&
            _lit is not null)
        {
            return;
        }

        _context.Wait();
        Release();

        _width = width;
        _height = height;
        _displayWidth = displayWidth;
        _displayHeight = displayHeight;
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

        // Sampled as well as tested, when there is a denoiser: it reads the depth to turn a
        // pixel back into the point in the room it came from, and reads the frame before to
        // decide whether the two are the same surface.
        _depth = D3D12Texture.CreateDepthTarget(
            _context, GBufferFormats.Depth, width, height, sampled: _rayTracing);

        // Display-sized, and unordered access rather than a render target: DLSS writes into
        // it with a compute shader of its own, and a resource it may not write to is a frame
        // the runtime refuses.
        _upscaled = D3D12Texture.CreateStorage(
            _context, GBufferFormats.Light, displayWidth, displayHeight);

        _lit = D3D12Texture.CreateRenderTarget(
            _context, GBufferFormats.Light, width, height, opaque);

        // The reflection. The same format and the same size as the picture, because it is a
        // picture of the room — the glass reads it at its own screen position, which is only
        // the right texel if the two renders share a grid.
        _mirror = D3D12Texture.CreateRenderTarget(
            _context, GBufferFormats.Light, width, height, (0f, 0f, 0f, 1f));

        // One texel of nothing, bound wherever a pass reads a target that does not exist yet.
        // Both APIs require every declared binding to point at something valid even when the
        // shader multiplies what it reads by zero.
        _empty = D3D12TextureUpload.Create(
            _context,
            new DecodedImage(1, 1, [0, 0, 0, 0], HasAlpha: true, "empty"),
            mipmaps: false,
            linear: true);

        _targets = D3D12DescriptorHeap.Create(
            _context.Device, DescriptorHeapType.Rtv, Slots.Count);

        _depths = D3D12DescriptorHeap.Create(_context.Device, DescriptorHeapType.Dsv, 1);

        for (uint i = 0; i < Slots.Count; i++)
        {
            _targets.Allocate();
        }

        for (int i = 0; i < _gbuffer.Length; i++)
        {
            _context.Device->CreateRenderTargetView(
                _gbuffer[i].Handle, (RenderTargetViewDesc*)null, _targets.Cpu((uint)i));
        }

        _context.Device->CreateRenderTargetView(
            _lit.Handle, (RenderTargetViewDesc*)null, _targets.Cpu(Slots.Lit));

        _context.Device->CreateRenderTargetView(
            _mirror.Handle, (RenderTargetViewDesc*)null, _targets.Cpu(Slots.Mirror));

        // And every frame's set is pointed at it. A binding a shader declares must be a real
        // descriptor whether the branch that reads it runs or not, so this is written for a
        // room with no mirror in it as readily as for one with.
        _frames.SetReflection(_mirror);

        _depths.Allocate();

        // Stated rather than inferred. A sampled depth target is typeless, and "whatever the
        // resource says" is the one thing a typeless resource cannot answer.
        _depth.DescribeDepth(_context, _depths.Cpu(0));

        if (_rayTracing)
        {
            _denoiser = D3D12ShadowDenoiser.Create(_context, _compiler, width, height);
            _reflections = D3D12Reflections.Create(_context, _compiler, width, height);

            // The reflection march reads the finished picture of the frame before, and on the
            // first frame after a resize there is no frame before: the composite writes this
            // target every frame but has not written it yet. A committed resource does come
            // back zeroed, so this is belt and braces — but it is one clear once a resize
            // against a first frame that would otherwise reflect whatever it found.
            ID3D12GraphicsCommandList4* start = _context.BeginOneShot();
            float* nothing = stackalloc float[4] { 0f, 0f, 0f, 1f };

            _lit.Transition(start, ResourceStates.RenderTarget);
            start->ClearRenderTargetView(
                _targets.Cpu(Slots.Lit), nothing, 0, (Silk.NET.Maths.Box2D<int>*)null);

            _context.EndOneShot();
        }
    }

    /// <summary>How many frames the runtime last said it would generate.</summary>
    private int _generating = -1;

    /// <summary>What the latency mode was last set to, so it is not set again every frame.</summary>
    private uint _latency = uint.MaxValue;

    /// <summary>How many frames the runtime is currently generating for each drawn one.</summary>
    /// <remarks>
    /// What the renderer reads to decide whether the frame owes frame generation a copy of
    /// itself without the interface. Nought means it does not.
    /// </remarks>
    public int Generating => Math.Max(0, _generating);

    /// <summary>Whether the swapchain is one frame generation could run into.</summary>
    /// <remarks>
    /// Set by the renderer, because it is the only thing that knows: the pipeline is built
    /// before the swapchain exists. False means the options below are not sent at all rather
    /// than sent and refused — a runtime asked to generate frames into a chain it never saw
    /// answers with a message about hooks that reads like a missing feature.
    /// </remarks>
    public bool CanGenerate { get; set; }

    /// <summary>What Reflex and frame generation came to, in one line.</summary>
    /// <returns>Something a player or a log can be shown.</returns>
    /// <remarks>
    /// Printed once at startup, because every one of these is invisible when it fails. A
    /// runtime that is present and a feature that is on look identical to a runtime that is
    /// present and a feature the card declined, and the difference is not something anybody
    /// can see in the picture — a game with frame generation quietly off is a game that
    /// works.
    /// </remarks>
    public string LatencyReport()
    {
        if (_streamline is null)
        {
            return "Latency: no Streamline runtime, so no Reflex and no frame generation.";
        }

        string reflex = _streamline.HasLatencyControl
            ? "Reflex available"
            : "Reflex unavailable";

        if (!_streamline.HasFrameGeneration)
        {
            return $"Latency: {reflex}; frame generation unavailable.";
        }

        if (!CanGenerate)
        {
            return $"Latency: {reflex}; frame generation loaded but the swapchain is not " +
                   "Streamline's, so it cannot run.";
        }

        int most = _streamline.FrameGenerationMaximum;

        string generation = most > 0
            ? $"frame generation up to {FrameGenerations.Most(most).Describe()}"
            : "frame generation loaded but this card will generate none";

        return $"Latency: {reflex}; {generation}" +
               (_streamline.FrameGenerationStatus == 0
                   ? "."
                   : $" (status {_streamline.FrameGenerationStatus}).");
    }

    /// <summary>Tells Reflex and frame generation what this frame wants of them.</summary>
    /// <param name="render">The size the room is drawn at.</param>
    /// <param name="display">The size the picture is shown at.</param>
    /// <remarks>
    /// <para>
    /// Both are set only when they change. Neither is free: the latency mode reaches the
    /// driver, and the generation options are copied into the plugin's context and warned
    /// about if they arrive twice for one frame — the plugin says so by name, calling it a
    /// redundant call or a race with the present.
    /// </para>
    /// <para>
    /// <b>Reflex comes on whenever frames are being generated, whatever the player set.</b>
    /// It is not a preference there: the runtime places a generated frame in time using the
    /// measurements Reflex makes, so generation with the latency mode off is generation
    /// pacing against nothing.
    /// </para>
    /// </remarks>
    private void PrepareLatency((uint Width, uint Height) render, (uint Width, uint Height) display)
    {
        if (_streamline is null)
        {
            return;
        }

        UpscalePlan plan = Upscaling ?? UpscalePlan.None;

        int generated = CanGenerate && _streamline.HasFrameGeneration
            ? Math.Min(plan.FrameGeneration.Generated(), _streamline.FrameGenerationMaximum)
            : 0;

        uint latency = generated > 0
            ? Math.Max((uint)plan.Latency, (uint)LatencyMode.On)
            : (uint)plan.Latency;

        if (latency != _latency)
        {
            _streamline.SetLatencyMode(latency);
            _latency = latency;
        }

        if (generated != _generating)
        {
            _streamline.SetFrameGeneration(generated, render, display);
            _generating = generated;
        }
    }

    /// <summary>Builds the upscaler the plan asks for, if it is not already the right one.</summary>
    private void PrepareUpscaler(int width, int height, int displayWidth, int displayHeight)
    {
        PrepareLatency(((uint)width, (uint)height), ((uint)displayWidth, (uint)displayHeight));

        UpscalePlan? asked = Upscaling is { Active: true } ? Upscaling : null;

        var render = ((uint)width, (uint)height);
        var display = ((uint)displayWidth, (uint)displayHeight);

        // At most one of the two, ever. They are alternatives rather than stages: both
        // accumulate across frames from the same inputs, and running one over the other's
        // output would be two histories filtering one picture.
        if (asked is { Kind: UpscalerKind.Dlss })
        {
            _fsr?.Dispose();
            _fsr = null;

            // The network driven by hand, and only where Streamline is not already driving
            // it. Once the driver's feature table has its missing entry filled in, sl.dlss_nr
            // loads and the ordinary path runs the same network with the same settings; this
            // is what is left for the case where that could not be done — an unfamiliar
            // driver build, or one that has closed the gap another way.
            if (asked.Neural.Enabled && _streamline is not { NeuralRenderingLoaded: true })
            {
                // A network that has given up is let go of here rather than asked again, so
                // the frame falls through to super resolution instead of being drawn small
                // and stretched for the rest of the run.
                if (_neural is { Refused: true })
                {
                    RetireNeural();
                    _neuralRefused = true;
                }

                if (_neural is not null && _neural.Serves(asked, render, display))
                {
                    _dlss?.Dispose();
                    _dlss = null;

                    return;
                }

                RetireNeural();

                // Asked once for this plan. Everything that stops the network starting is a
                // fact about the machine rather than about the frame, so trying again every
                // frame would buy nothing and cost a line in the log each time.
                if (!_neuralRefused)
                {
                    _neural = D3D12NeuralRenderer.TryCreate(
                        _context, _runtimes, asked, render, display);

                    _neuralRefused = _neural is null;
                }

                if (_neural is not null)
                {
                    _dlss?.Dispose();
                    _dlss = null;

                    return;
                }

                // Asked for and not to be had. Falling through to super resolution is the
                // right answer rather than drawing nothing: the note on the settings page
                // says which of the two is running.
            }
            else
            {
                RetireNeural();
            }

            if (_streamline is null)
            {
                _dlss?.Dispose();
                _dlss = null;

                return;
            }

            if (_dlss is not null && _dlss.Serves(asked, render, display))
            {
                return;
            }

            _dlss?.Dispose();
            _dlss = D3D12DlssUpscaler.TryCreate(
                _context, _streamline, asked, render, display, _rayTracing);

            return;
        }

        if (asked is { Kind: UpscalerKind.Fsr })
        {
            _dlss?.Dispose();
            _dlss = null;
            RetireNeural();

            if (_fsr is not null && _fsr.Serves(asked, render, display))
            {
                return;
            }

            _fsr?.Dispose();
            _fsr = D3D12FsrUpscaler.TryCreate(_context, _runtimes, asked, render, display);

            return;
        }

        _dlss?.Dispose();
        _dlss = null;
        _fsr?.Dispose();
        _fsr = null;
        RetireNeural();
    }

    /// <summary>Lets go of the neural network, once the card has finished with it.</summary>
    /// <remarks>
    /// NGX frees the network's working memory when the feature is released, so the queue has
    /// to have drained first: freeing memory a frame still in flight reads from is a device
    /// loss rather than a leak. The wait is affordable because this happens only when a
    /// player changes one of the few things the feature was built around — the sizes, the
    /// preset — and never once a frame.
    /// </remarks>
    private void RetireNeural()
    {
        if (_neural is null)
        {
            return;
        }

        _context.Wait();
        _neural.Dispose();
        _neural = null;
    }

    private void Release()
    {
        // Before the targets they have descriptors of, and before the depth the denoiser
        // keeps a copy of. Both are sized for one viewport, so a resize builds new ones
        // rather than resizing these — and both start a scene remembering nothing, which is
        // what a reference render of two scenes through one renderer needs them to do.
        _skybox?.Dispose();
        _skybox = null;
        _terrain?.Dispose();
        _terrain = null;
        _skyOwner = null;
        _reflections?.Dispose();
        _reflections = null;
        _denoiser?.Dispose();
        _denoiser = null;
        _denoiserBound = false;

        foreach (D3D12Texture target in _gbuffer)
        {
            target.Dispose();
        }

        _gbuffer = [];

        _depth?.Dispose();
        _upscaled?.Dispose();
        _lit?.Dispose();
        _mirror?.Dispose();
        _empty?.Dispose();
        _targets?.Dispose();
        _depths?.Dispose();
        _dlss?.Dispose();
        _neural?.Dispose();

        _neural = null;
        _depth = null;
        _upscaled = null;
        _lit = null;
        _mirror = null;
        _empty = null;
        _targets = null;
        _depths = null;
        _dlss = null;
    }

    /// <summary>Where each render target view sits in this pipeline's own heap.</summary>
    private static class Slots
    {
        /// <summary>After the G-buffer, whose targets take the first four.</summary>
        internal const uint Lit = GBufferFormats.Targets;

        /// <summary>And after that, the one a mirror's reflection is drawn into.</summary>
        internal const uint Mirror = GBufferFormats.Targets + 1;

        /// <summary>How many render-target views one frame needs in all.</summary>
        internal const uint Count = GBufferFormats.Targets + 2;
    }
}
