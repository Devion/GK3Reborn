using System.Numerics;
using GK3Reborn.Rendering.Geometry;
using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Foundation.Diagnostics;
using Silk.NET.Vulkan;

using GK3Reborn.Rendering.Shaders;

namespace GK3Reborn.Rendering.Vulkan;

/// <summary>
/// Renders loaded scene geometry into an offscreen image.
/// </summary>
/// <remarks>
/// <para>
/// Headless by design. A render that needs no window runs on a build agent, produces a
/// file that can be compared between runs, and can be inspected without anyone watching
/// the screen at the right moment — none of which is true of a screenshot.
/// </para>
/// <para>
/// It draws the same frame the windowed renderer draws, deferred stages included: the room
/// writes its parts, the occlusion is traced and filtered, and a compositing pass puts them
/// back together. It did not always. For a while it bound the picture alone and threw the
/// rest of the frame away, which at any ray-traced level meant the rig's light went to a
/// target nothing read and every character came out lit by the ambient floor — a whole
/// class of shading bug the tool could not show, and three tests that could not pass.
/// </para>
/// <para>
/// Two differences from the windowed renderer remain, and both are deliberate. There is no
/// sky, because nothing here has a room's cube map to draw. And <b>a single frame has no
/// previous picture to reflect</b>, so the reflection pass marches against black and adds
/// nothing: reflections are the host's to show. What that buys is the thing a regression
/// image needs and the host cannot give — the same scene renders to the same pixels every
/// time, because no stage here carries anything over from a frame before.
/// </para>
/// </remarks>
public sealed unsafe class SceneRenderer : IOffscreenRenderer
{
    /// <summary>
    /// sRGB, not UNORM.
    /// </summary>
    /// <remarks>
    /// Textures decode to linear on sample and shading happens in linear space, so the
    /// target has to encode back on write. Writing linear values into a UNORM target and
    /// calling the result sRGB is what makes an otherwise correct render come out about a
    /// gamma too dark.
    /// </remarks>
    public const Format ColorFormat = Format.R8G8B8A8Srgb;

    /// <summary>Depth format used by the offscreen path and the swapchain alike.</summary>
    public const Format DepthFormat = Format.D32Sfloat;

    private readonly VulkanContext _context;
    private readonly ShaderCompiler _compiler;
    private readonly MeshPipeline _pipeline;
    private readonly FrameUniformSet _frames;
    private readonly MeshPipeline? _rayTraced;
    private readonly FrameUniformSet? _rayTracedFrames;

    private bool _warnedAboutDeferred;
    private ParticlePipeline? _particlePipeline;
    private IReadOnlyList<Particle> _particles = [];
    private FogPipeline? _fogPipeline;
    private FogVolume _fog = FogVolume.None;

    private SceneRenderer(
        VulkanContext context,
        ShaderCompiler compiler,
        MeshPipeline pipeline,
        FrameUniformSet frames,
        MeshPipeline? rayTraced,
        FrameUniformSet? rayTracedFrames)
    {
        _context = context;
        _compiler = compiler;
        _pipeline = pipeline;
        _frames = frames;
        _rayTraced = rayTraced;
        _rayTracedFrames = rayTracedFrames;
    }

    /// <summary>The pipeline, for building geometry that matches it.</summary>
    public MeshPipeline Pipeline => _pipeline;

    /// <summary>Whether a ray-traced pipeline was built.</summary>
    /// <inheritdoc/>
    public RenderBackend Backend => RenderBackend.Vulkan;

    /// <inheritdoc/>
    public string DeviceName => _context.DeviceName;

    public bool SupportsRayTracing => _rayTraced is not null;

    /// <summary>How much ray tracing to do.</summary>
    /// <remarks>
    /// Changing this costs nothing: both pipelines exist from the start, and every level
    /// above <see cref="RayTracingQuality.None"/> differs only in numbers the shader
    /// reads from a uniform. Only <see cref="RayTracingQuality.None"/> switches pipeline,
    /// and it does so to avoid the ray-tracing shader's cost entirely rather than because
    /// it would give a different picture.
    /// </remarks>
    /// <summary>How the room's lights are divided up, once a scene has been given some.</summary>
    /// <remarks>
    /// Reported rather than drawn. The whole point of the grid is that nothing looks
    /// different — a fragment gets the same lights, reached more cheaply — so the only way
    /// to know it is working is the numbers: how many cells, and how many lights the
    /// average one holds against how many the room declares.
    /// </remarks>
    public SceneLightGrid? LightGrid { get; private set; }

    public RayTracingQuality Quality { get; set; } = RayTracingQuality.None;

    /// <summary>The tier's settings, with anything set here in place of them.</summary>
    /// <remarks>
    /// The four levels are what a player chooses between, and setting this is how anything
    /// else asks for a combination they do not offer — one knob moved and the rest of the
    /// tier left alone, which is what it takes to attribute a change in the picture to that
    /// knob rather than to the four differences between two levels.
    /// </remarks>
    public RayTracingSettings? Overriding { get; set; }

    /// <summary>What this frame is actually being traced with.</summary>
    private RayTracingSettings Tracing => Overriding ?? RayTracingSettings.For(Quality);

    /// <summary>Creates a renderer.</summary>
    /// <param name="context">Device context.</param>
    /// <returns>The renderer.</returns>
    public static SceneRenderer Create(VulkanContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var compiler = new ShaderCompiler(ShaderCompiler.DefaultCacheDirectory);

        try
        {
            MeshPipeline pipeline = MeshPipeline.Create(context, ColorFormat, DepthFormat, compiler);
            FrameUniformSet frames = FrameUniformSet.Create(context, pipeline, 1);

            MeshPipeline? rayTraced = null;
            FrameUniformSet? rayTracedFrames = null;

            if (context.SupportsRayTracing)
            {
                // Light, not a picture: the ray-traced room writes half of its lighting
                // into this target and the compositing pass finishes it, so the values in
                // it run past white and it has to be the format with room for them.
                rayTraced = MeshPipeline.Create(
                    context, GBuffer.LightFormat, DepthFormat, compiler, rayTracing: true);

                rayTracedFrames = FrameUniformSet.Create(context, rayTraced, 1);
            }

            var renderer = new SceneRenderer(
                context, compiler, pipeline, frames, rayTraced, rayTracedFrames);

            renderer.BindPlaceholderReflection();

            return renderer;
        }
        catch
        {
            compiler.Dispose();
            throw;
        }
    }

    /// <summary>Creates geometry this renderer can draw.</summary>
    /// <returns>Empty scene geometry.</returns>
    public SceneGeometry CreateGeometry() =>
        SceneGeometry.Create(GeometryDevice, Textures);

    /// <summary>The seam a scene is put on this device through.</summary>
    public VulkanGeometryDevice GeometryDevice =>
        field ??= new VulkanGeometryDevice(_context, _pipeline);

    /// <summary>The textures the device holds, shared by every scene this renderer draws.</summary>
    public TextureCache Textures =>
        field ??= new TextureCache(GeometryDevice, SceneGeometry.CheckerBoard());

    /// <summary>Sets the lights anything without baked lighting is lit by.</summary>
    /// <param name="lights">The rig the scene was authored with.</param>
    /// <param name="scene">What the geometry occupies; default decides nothing.</param>
    public void SetLights(
        IReadOnlyList<Formats.Scenes.AuthoredLight> lights, SceneExtent scene = default)
    {
        _frames.SetLights(lights, scene);
        _rayTracedFrames?.SetLights(lights, scene);

        LightGrid = _frames.Grid;
    }

    /// <summary>
    /// Where the wind stands, in seconds, when this renders.
    /// </summary>
    /// <remarks>
    /// Zero, and it stays zero unless a caller moves it. A headless render is the thing
    /// two versions of this engine are compared with, so it renders a still afternoon by
    /// default; <c>render-scene --wind SECONDS</c> is how the movement itself is looked at.
    /// </remarks>
    public float Seconds { get; set; }

    /// <summary>Gives the room its smoke and embers.</summary>
    /// <param name="particles">The particles, furthest from the eye first.</param>
    /// <remarks>
    /// Empty unless a caller sets it, so a headless render draws a room with its fires
    /// standing still — which is what two versions of this engine are compared with. See
    /// <see cref="Game.FlameParticles"/> for what fills it.
    /// </remarks>
    public void SetParticles(IReadOnlyList<Particle> particles)
    {
        ArgumentNullException.ThrowIfNull(particles);
        _particles = particles;
    }

    /// <summary>Gives the room its fog.</summary>
    /// <param name="fog">The layer, or <see cref="FogVolume.None"/> for a room with none.</param>
    /// <remarks>
    /// None unless a caller sets it. A room that is given one pays for a depth target it can
    /// sample and a pass over the frame; a room that is not pays for neither, which is every
    /// room but the handful <see cref="Game.SceneFog"/> names.
    /// </remarks>
    public void SetFog(FogVolume fog) => _fog = fog;

    /// <summary>Renders geometry and returns the image.</summary>
    /// <param name="geometry">What to draw.</param>
    /// <param name="width">Image width.</param>
    /// <param name="height">Image height.</param>
    /// <param name="camera">Where to look from.</param>
    /// <returns>The rendered image.</returns>
    public DecodedImage Render(SceneGeometry geometry, int width, int height, Camera camera)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentNullException.ThrowIfNull(camera);

        geometry.Finish();

        bool tracing = Quality != RayTracingQuality.None &&
                       _rayTraced is not null &&
                       _rayTracedFrames is not null &&
                       geometry.RayTracing is not null;

        ShadowDenoiser? denoiser = null;
        Reflections? reflections = null;
        CompositePipeline? composite = null;

        if (tracing)
        {
            (denoiser, reflections, composite) = BuildDeferred(width, height);

            // The ray-traced pipeline writes light rather than a picture, so without the
            // pass that finishes it there is no picture at all. Falling back to the plain
            // pipeline is what makes the warning true: the room draws with the lighting it
            // had before any of this existed.
            tracing = denoiser is not null && reflections is not null && composite is not null;
        }

        MeshPipeline pipeline = tracing ? _rayTraced! : _pipeline;
        FrameUniformSet frames = tracing ? _rayTracedFrames! : _frames;

        frames.Seconds = Seconds;

        if (tracing)
        {
            frames.SetScene(VulkanGeometry.Scene(geometry.RayTracing!));
            frames.Settings = Tracing;
        }

        // Everything the frame writes besides its picture. The plain pipeline declares the
        // same four colour outputs as the ray-traced one, so all four are bound either
        // way: a rendering scope that does not match its pipeline is undefined rather than
        // forgiving, and this one used to bind exactly one of them.
        ImageUsageFlags parts = ImageUsageFlags.ColorAttachmentBit |
                                (tracing ? ImageUsageFlags.SampledBit : 0);

        Target picture = CreateTarget(
            width, height, ColorFormat,
            ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferSrcBit,
            ImageAspectFlags.ColorBit);

        Target scene = tracing
            ? CreateTarget(width, height, GBuffer.LightFormat, parts, ImageAspectFlags.ColorBit)
            : default;

        // What the reflection march has to look at. Cleared and never drawn into: a
        // headless frame has no frame before it, and reflecting this frame's own
        // half-finished lighting would put something on the floor the host would never
        // show there.
        Target lit = tracing
            ? CreateTarget(
                width, height, ColorFormat,
                ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit,
                ImageAspectFlags.ColorBit)
            : default;

        Target normal = CreateTarget(
            width, height, GBuffer.NormalFormat, parts, ImageAspectFlags.ColorBit);

        Target motion = CreateTarget(
            width, height, GBuffer.MotionFormat, parts, ImageAspectFlags.ColorBit);

        Target direct = CreateTarget(
            width, height, GBuffer.LightFormat, parts, ImageAspectFlags.ColorBit);

        // Sampled where the compositing pass will trace against it, and where there is fog
        // to march to it. Both read the depth after the room has been drawn; a room with
        // neither leaves it an attachment and nothing else.
        Target depth = CreateTarget(
            width, height, DepthFormat,
            ImageUsageFlags.DepthStencilAttachmentBit |
                (tracing || _fog.Any ? ImageUsageFlags.SampledBit : 0),
            ImageAspectFlags.DepthBit);

        try
        {
            CommandBuffer command = _context.BeginOneShot();

            if (tracing)
            {
                denoiser!.Settle(command);
                reflections!.Settle(command);

                _context.Transition(
                    command, lit.Image, ImageLayout.Undefined, ImageLayout.TransferDstOptimal);

                ClearToBlack(command, lit.Image);

                _context.Transition(
                    command, lit.Image, ImageLayout.TransferDstOptimal,
                    ImageLayout.ShaderReadOnlyOptimal);

                _context.Transition(
                    command, scene.Image, ImageLayout.Undefined,
                    ImageLayout.ColorAttachmentOptimal);
            }

            _context.Transition(
                command, picture.Image, ImageLayout.Undefined, ImageLayout.ColorAttachmentOptimal);

            foreach (Target part in (ReadOnlySpan<Target>)[normal, motion, direct])
            {
                _context.Transition(
                    command, part.Image, ImageLayout.Undefined, ImageLayout.ColorAttachmentOptimal);
            }

            _context.Transition(
                command, depth.Image, ImageLayout.Undefined,
                ImageLayout.DepthStencilAttachmentOptimal, ImageAspectFlags.DepthBit);

            ReadOnlySpan<ImageView> colors =
            [
                tracing ? scene.View : picture.View,
                normal.View,
                motion.View,
                direct.View,
            ];

            VulkanSceneDraw.Begin(
                _context.Api, command, colors, depth.View, width, height, camera.Background,
                keepDepth: tracing || _fog.Any || _particles.Count > 0);

            VulkanSceneDraw.Record(
                _context.Api, command, pipeline, frames, geometry, 0, width, height, camera);

            _context.Api.CmdEndRendering(command);

            if (tracing)
            {
                Compose(
                    command, denoiser!, reflections!, composite!, geometry, frames, camera,
                    scene, normal, motion, direct, depth, lit, picture.View, width, height);
            }

            // The air in the room, over the finished picture and under the smoke in it. A
            // fire's own smoke is drawn where the fire is and is lit by it; fogging it
            // against the wall behind it would dim the near side of a plume by however far
            // away that wall happened to be.
            bool fogged = RecordFog(command, picture.View, depth, frames, camera, width, height, tracing);

            // Over the finished picture and under nothing: smoke is the last thing in the
            // room and the only blended thing in the renderer.
            RecordParticles(
                command, picture.View, depth, camera, width, height, tracing || fogged);

            _context.Transition(
                command, picture.Image, ImageLayout.ColorAttachmentOptimal,
                ImageLayout.TransferSrcOptimal);

            return ReadBack(command, picture.Image, width, height);
        }
        finally
        {
            Destroy(depth);
            Destroy(direct);
            Destroy(motion);
            Destroy(normal);
            Destroy(lit);
            Destroy(scene);
            Destroy(picture);

            composite?.Dispose();
            reflections?.Dispose();
            denoiser?.Dispose();
        }
    }

    /// <summary>Marches the room's fog over the picture it has just made.</summary>
    /// <param name="command">Command buffer to record into.</param>
    /// <param name="picture">The finished picture, which the fog is blended onto.</param>
    /// <param name="depth">How far the room got, which is where each ray stops.</param>
    /// <param name="frames">The set holding the rig the fog is lit by.</param>
    /// <param name="camera">Where the frame was looked at from.</param>
    /// <param name="width">Target width.</param>
    /// <param name="height">Its height.</param>
    /// <param name="tracing">
    /// Whether the compositing pass ran, which is what decides whether the depth is already
    /// in the layout this pass wants to read it in.
    /// </param>
    /// <returns>True when fog was drawn, which leaves the depth readable by a shader.</returns>
    /// <remarks>
    /// The pipeline is built the first time a room with fog in it is rendered and kept — it
    /// is a pipeline and two shader modules, and building it per render would put a compile
    /// in the middle of every frame of a corpus sweep. The descriptors are written every
    /// time, because the depth target is made and destroyed with each render.
    /// </remarks>
    private bool RecordFog(
        CommandBuffer command,
        ImageView picture,
        Target depth,
        FrameUniformSet frames,
        Camera camera,
        int width,
        int height,
        bool tracing)
    {
        if (!_fog.Any)
        {
            return false;
        }

        _fogPipeline ??= FogPipeline.Create(_context, ColorFormat, _compiler);

        if (!tracing)
        {
            _context.Transition(
                command, depth.Image, ImageLayout.DepthStencilAttachmentOptimal,
                ImageLayout.ShaderReadOnlyOptimal, ImageAspectFlags.DepthBit);
        }

        _fogPipeline.Bind(frames.Rig, frames.Cells, frames.Reaching, depth.View);

        var attachment = new RenderingAttachmentInfo
        {
            SType = StructureType.RenderingAttachmentInfo,
            ImageView = picture,
            ImageLayout = ImageLayout.ColorAttachmentOptimal,

            // Loaded, and it has to be: the fog is blended over the room rather than
            // replacing it.
            LoadOp = AttachmentLoadOp.Load,
            StoreOp = AttachmentStoreOp.Store,
        };

        var rendering = new RenderingInfo
        {
            SType = StructureType.RenderingInfo,
            RenderArea = new Rect2D { Extent = new Extent2D((uint)width, (uint)height) },
            LayerCount = 1,
            ColorAttachmentCount = 1,
            PColorAttachments = &attachment,
        };

        _context.Api.CmdBeginRendering(command, in rendering);

        _fogPipeline.Record(
            command,
            width,
            height,
            FogConstants.For(
                _fog, LightGrid, Tracing.Ambient, camera, Seconds, width, height));

        _context.Api.CmdEndRendering(command);

        return true;
    }

    /// <summary>Draws the room's smoke and embers over the picture it has just made.</summary>
    /// <param name="command">Command buffer to record into.</param>
    /// <param name="picture">The finished picture.</param>
    /// <param name="depth">The depth the room left, which the sprites are tested against.</param>
    /// <param name="camera">Where the frame was looked at from.</param>
    /// <param name="width">Target width.</param>
    /// <param name="height">Its height.</param>
    /// <param name="sampled">
    /// Whether anything has already read the depth as a texture — the compositing pass or
    /// the fog — which is what decides the layout it is currently in.
    /// </param>
    private void RecordParticles(
        CommandBuffer command,
        ImageView picture,
        Target depth,
        Camera camera,
        int width,
        int height,
        bool sampled)
    {
        if (_particles.Count == 0)
        {
            return;
        }

        _particlePipeline ??= ParticlePipeline.Create(
            _context, ColorFormat, DepthFormat, _compiler);

        _particlePipeline.Prepare(_particles);

        if (_particlePipeline.Count == 0)
        {
            return;
        }

        // The compositing pass and the fog both leave the depth readable by a shader; the
        // plain path leaves it where the room wrote it. Either way it has to be an
        // attachment again to be tested against, and it is never written here.
        if (sampled)
        {
            _context.Transition(
                command, depth.Image, ImageLayout.ShaderReadOnlyOptimal,
                ImageLayout.DepthStencilAttachmentOptimal, ImageAspectFlags.DepthBit);
        }

        var colour = new RenderingAttachmentInfo
        {
            SType = StructureType.RenderingAttachmentInfo,
            ImageView = picture,
            ImageLayout = ImageLayout.ColorAttachmentOptimal,

            // Loaded rather than cleared: the picture is already in it.
            LoadOp = AttachmentLoadOp.Load,
            StoreOp = AttachmentStoreOp.Store,
        };

        var depthAttachment = new RenderingAttachmentInfo
        {
            SType = StructureType.RenderingAttachmentInfo,
            ImageView = depth.View,
            ImageLayout = ImageLayout.DepthStencilAttachmentOptimal,
            LoadOp = AttachmentLoadOp.Load,
            StoreOp = AttachmentStoreOp.Store,
        };

        var rendering = new RenderingInfo
        {
            SType = StructureType.RenderingInfo,
            RenderArea = new Rect2D { Extent = new Extent2D((uint)width, (uint)height) },
            LayerCount = 1,
            ColorAttachmentCount = 1,
            PColorAttachments = &colour,
            PDepthAttachment = &depthAttachment,
        };

        _context.Api.CmdBeginRendering(command, in rendering);

        _particlePipeline.Record(
            command, width, height,
            Shaders.ParticleShaders.Describe(
                camera, camera.View * camera.Projection((float)width / height)));

        _context.Api.CmdEndRendering(command);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _context.Api.DeviceWaitIdle(_context.Device);

        _particlePipeline?.Dispose();
        _particlePipeline = null;

        _fogPipeline?.Dispose();
        _fogPipeline = null;

        if (_placeholderSampler.Handle != 0)
        {
            _context.Api.DestroySampler(_context.Device, _placeholderSampler, null);
            _placeholderSampler = default;
        }

        Destroy(_placeholder);
        _placeholder = default;

        _rayTracedFrames?.Dispose();
        _rayTraced?.Dispose();
        _frames.Dispose();
        _pipeline.Dispose();
        _compiler.Dispose();
    }

    /// <summary>Builds the three stages that finish a ray-traced frame.</summary>
    /// <param name="width">Viewport width.</param>
    /// <param name="height">Viewport height.</param>
    /// <returns>The stages, or nulls if they could not be built.</returns>
    /// <remarks>
    /// Built for one render and thrown away with it. They are the frame's memory — the
    /// denoiser reprojects the last frame's answer into this one, and the reflection pass
    /// keeps the last picture to march against — and a tool that renders two scenes
    /// through one renderer must not let the first leak into the second. Keeping them
    /// would save a few milliseconds and cost the one property a regression image exists
    /// for.
    /// </remarks>
    private (ShadowDenoiser?, Reflections?, CompositePipeline?) BuildDeferred(int width, int height)
    {
        ShadowDenoiser? denoiser = null;
        Reflections? reflections = null;
        CompositePipeline? composite = null;

        try
        {
            denoiser = ShadowDenoiser.Create(_context, _compiler, width, height);

            if (denoiser is not null)
            {
                reflections = Reflections.Create(_context, _compiler, width, height);
                composite = CompositePipeline.Create(_context, _compiler, ColorFormat);

                return (denoiser, reflections, composite);
            }
        }
        catch (VulkanException error)
        {
            // Once a stage has failed to build it will fail the same way for every render,
            // so this is said once rather than for each of them.
            if (!_warnedAboutDeferred)
            {
                _warnedAboutDeferred = true;

                Log.Warning(
                    "WARNING GK3R3411: The compositing stages could not be built, so the " +
                    "scene is rendered without ray tracing. (" + error.Message + ")");
            }
        }

        composite?.Dispose();
        reflections?.Dispose();
        denoiser?.Dispose();

        return (null, null, null);
    }

    /// <summary>Traces the occlusion, filters it, and puts the picture together.</summary>
    /// <param name="command">Command buffer being recorded.</param>
    /// <param name="denoiser">The tracing and filtering stages.</param>
    /// <param name="reflections">The screen-space reflection stages.</param>
    /// <param name="composite">The pass that multiplies the parts together.</param>
    /// <param name="geometry">What was drawn, for its acceleration structure.</param>
    /// <param name="frames">The frame's uniforms, for the rig the tracing reads.</param>
    /// <param name="camera">Where the frame was drawn from.</param>
    /// <param name="scene">The indirect light the room pass wrote.</param>
    /// <param name="normal">The frame's normals.</param>
    /// <param name="motion">The frame's motion vectors.</param>
    /// <param name="direct">The rig's light, before any of it is blocked.</param>
    /// <param name="depth">The frame's depth.</param>
    /// <param name="lit">What the reflection march looks at.</param>
    /// <param name="picture">Where the finished frame goes.</param>
    /// <param name="width">Viewport width.</param>
    /// <param name="height">Viewport height.</param>
    /// <remarks>
    /// Outside the room's rendering scope, because the tracing reads the depth and the
    /// normals that scope wrote and an attachment cannot be sampled while it is still one.
    /// </remarks>
    private void Compose(
        CommandBuffer command,
        ShadowDenoiser denoiser,
        Reflections reflections,
        CompositePipeline composite,
        SceneGeometry geometry,
        FrameUniformSet frames,
        Camera camera,
        Target scene,
        Target normal,
        Target motion,
        Target direct,
        Target depth,
        Target lit,
        ImageView picture,
        int width,
        int height)
    {
        foreach (Target part in (ReadOnlySpan<Target>)[scene, normal, motion, direct])
        {
            _context.Transition(
                command, part.Image, ImageLayout.ColorAttachmentOptimal,
                ImageLayout.ShaderReadOnlyOptimal);
        }

        _context.Transition(
            command, depth.Image, ImageLayout.DepthStencilAttachmentOptimal,
            ImageLayout.ShaderReadOnlyOptimal, ImageAspectFlags.DepthBit);

        denoiser.Bind(
            depth.View,
            normal.View,
            motion.View,
            VulkanGeometry.Scene(geometry.RayTracing!).Handle,
            frames.Rig.Handle,
            frames.Rig.Size);

        // No planar pass here, so the picture stands in for it and the plane is noughts:
        // nothing is ever on a plane that does not exist, so the branch never runs and the
        // descriptor is bound to something valid rather than to nothing.
        reflections.Bind(depth.View, normal.View, motion.View, lit.View, lit.View);

        composite.Bind(
            scene.View,
            direct.View,
            denoiser.Shadow,
            denoiser.Occlusion,
            denoiser.DynamicShadow,
            reflections.Buffers);

        RayTracingSettings settings = Tracing;

        denoiser.Record(
            command, camera, depth.Image, settings.AmbientOcclusionRadius, settings.OcclusionSamples);

        reflections.Record(command, camera, Rendering.Materials.SurfaceFinish.Roughest);

        var attachment = new RenderingAttachmentInfo
        {
            SType = StructureType.RenderingAttachmentInfo,
            ImageView = picture,
            ImageLayout = ImageLayout.ColorAttachmentOptimal,

            // Nothing to load: the one triangle covers every pixel of it.
            LoadOp = AttachmentLoadOp.DontCare,
            StoreOp = AttachmentStoreOp.Store,
        };

        var rendering = new RenderingInfo
        {
            SType = StructureType.RenderingInfo,
            RenderArea = new Rect2D { Extent = new Extent2D((uint)width, (uint)height) },
            LayerCount = 1,
            ColorAttachmentCount = 1,
            PColorAttachments = &attachment,
        };

        _context.Api.CmdBeginRendering(command, in rendering);
        composite.Record(
            command, width, height, reflections.Parity, settings.OcclusionStrength);
        _context.Api.CmdEndRendering(command);
    }

    private void ClearToBlack(CommandBuffer command, Image image)
    {
        var range = new ImageSubresourceRange
        {
            AspectMask = ImageAspectFlags.ColorBit,
            LevelCount = 1,
            LayerCount = 1,
        };

        var black = new ClearColorValue(0f, 0f, 0f, 0f);

        _context.Api.CmdClearColorImage(
            command, image, ImageLayout.TransferDstOptimal, in black, 1, in range);
    }

    private DecodedImage ReadBack(CommandBuffer command, Image color, int width, int height)
    {
        var bufferInfo = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = (ulong)(width * height * 4),
            Usage = BufferUsageFlags.TransferDstBit,
            SharingMode = SharingMode.Exclusive,
        };

        _context.Api.CreateBuffer(_context.Device, in bufferInfo, null, out Silk.NET.Vulkan.Buffer buffer);
        _context.Api.GetBufferMemoryRequirements(_context.Device, buffer, out MemoryRequirements requirements);

        DeviceMemory memory = _context.Allocate(
            requirements, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);

        _context.Api.BindBufferMemory(_context.Device, buffer, memory, 0);

        try
        {
            var region = new BufferImageCopy
            {
                ImageSubresource = new ImageSubresourceLayers
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    LayerCount = 1,
                },
                ImageExtent = new Extent3D((uint)width, (uint)height, 1),
            };

            _context.Api.CmdCopyImageToBuffer(
                command, color, ImageLayout.TransferSrcOptimal, buffer, 1, in region);

            _context.EndOneShot(command);

            byte[] pixels = new byte[width * height * 4];
            void* mapped;
            _context.Api.MapMemory(_context.Device, memory, 0, (ulong)pixels.Length, 0, &mapped);
            new ReadOnlySpan<byte>(mapped, pixels.Length).CopyTo(pixels);
            _context.Api.UnmapMemory(_context.Device, memory);

            return new DecodedImage(width, height, pixels, HasAlpha: false, "vulkan");
        }
        finally
        {
            _context.Api.DestroyBuffer(_context.Device, buffer, null);
            _context.Api.FreeMemory(_context.Device, memory, null);
        }
    }

    private void Destroy(Target target)
    {
        if (target.View.Handle != 0)
        {
            _context.Api.DestroyImageView(_context.Device, target.View, null);
        }

        if (target.Image.Handle != 0)
        {
            _context.Api.DestroyImage(_context.Device, target.Image, null);
        }

        if (target.Memory.Handle != 0)
        {
            _context.Api.FreeMemory(_context.Device, target.Memory, null);
        }
    }

    /// <summary>Gives the reflection binding something real to point at.</summary>
    /// <remarks>
    /// <para>
    /// A single black texel. <b>Nothing here ever samples it</b> — the mirror flag is only
    /// given to a surface once <see cref="SceneGeometry.ChooseMirror"/> has run, and only
    /// the windowed renderer runs it, so a mirror photographed through this path draws the
    /// picture painted on it exactly as it always has.
    /// </para>
    /// <para>
    /// It exists because a binding a shader declares must be a real descriptor whether or
    /// not the branch that reads it runs. Leaving it unwritten is not "a texture nobody
    /// looks at"; it is a descriptor set the validation layers reject and a driver may do
    /// anything with.
    /// </para>
    /// </remarks>
    private void BindPlaceholderReflection()
    {
        _placeholder = CreateTarget(
            1, 1, ColorFormat,
            ImageUsageFlags.SampledBit | ImageUsageFlags.ColorAttachmentBit,
            ImageAspectFlags.ColorBit);

        var samplerInfo = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Nearest,
            MinFilter = Filter.Nearest,
            AddressModeU = SamplerAddressMode.ClampToEdge,
            AddressModeV = SamplerAddressMode.ClampToEdge,
            AddressModeW = SamplerAddressMode.ClampToEdge,
        };

        _context.Api.CreateSampler(_context.Device, in samplerInfo, null, out _placeholderSampler);

        _frames.SetReflection(_placeholder.View, _placeholderSampler);
        _rayTracedFrames?.SetReflection(_placeholder.View, _placeholderSampler);
    }

    /// <summary>The one texel the reflection binding points at here.</summary>
    private Target _placeholder;

    /// <summary>How it is read, which nothing ever does.</summary>
    private Sampler _placeholderSampler;

    private Target CreateTarget(
        int width, int height, Format format, ImageUsageFlags usage, ImageAspectFlags aspect)
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
            Usage = usage,
            InitialLayout = ImageLayout.Undefined,
        };

        if (_context.Api.CreateImage(_context.Device, in imageInfo, null, out Image image) != Result.Success)
        {
            throw new VulkanException("Could not create a render target.");
        }

        _context.Api.GetImageMemoryRequirements(_context.Device, image, out MemoryRequirements requirements);
        DeviceMemory memory = _context.Allocate(requirements, MemoryPropertyFlags.DeviceLocalBit);
        _context.Api.BindImageMemory(_context.Device, image, memory, 0);

        var viewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = image,
            ViewType = ImageViewType.Type2D,
            Format = format,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = aspect,
                LevelCount = 1,
                LayerCount = 1,
            },
        };

        if (_context.Api.CreateImageView(_context.Device, in viewInfo, null, out ImageView view) != Result.Success)
        {
            throw new VulkanException("Could not create a render target view.");
        }

        return new Target(image, memory, view);
    }

    /// <summary>An image, its memory and its view, which are made and freed together.</summary>
    private readonly record struct Target(Image Image, DeviceMemory Memory, ImageView View);
}

/// <summary>
/// The Vulkan recording steps a scene draw needs, independent of where it is drawn.
/// </summary>
/// <remarks>
/// Shared between the offscreen renderer and the windowed one so that what a regression
/// image shows and what a player sees cannot drift apart.
/// </remarks>
public static unsafe class VulkanSceneDraw
{
    /// <summary>Begins rendering into the frame's colour targets and its depth.</summary>
    /// <param name="vk">Vulkan API.</param>
    /// <param name="command">Command buffer to record into.</param>
    /// <param name="colors">
    /// Every colour target the pipeline declares, the picture first. All of them, always:
    /// a rendering scope that binds fewer attachments than its pipeline writes is not a
    /// smaller frame, it is undefined behaviour.
    /// </param>
    /// <param name="depth">Depth view.</param>
    /// <param name="width">Target width.</param>
    /// <param name="height">Target height.</param>
    /// <param name="background">Colour to clear the picture to.</param>
    /// <param name="keepDepth">Whether anything reads the depth after the scope ends.</param>
    public static void Begin(
        Vk vk,
        CommandBuffer command,
        ReadOnlySpan<ImageView> colors,
        ImageView depth,
        int width,
        int height,
        Vector3 background,
        bool keepDepth = false)
    {
        ArgumentNullException.ThrowIfNull(vk);

        if (colors.Length != (int)GBuffer.Targets)
        {
            throw new ArgumentException(
                $"A frame has {GBuffer.Targets} colour targets, not {colors.Length}.",
                nameof(colors));
        }

        RenderingAttachmentInfo* attachments =
            stackalloc RenderingAttachmentInfo[(int)GBuffer.Targets];

        attachments[GBuffer.Colour] = new RenderingAttachmentInfo
        {
            SType = StructureType.RenderingAttachmentInfo,
            ImageView = colors[GBuffer.Colour],
            ImageLayout = ImageLayout.ColorAttachmentOptimal,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.Store,
            ClearValue = new ClearValue(new ClearColorValue(background.X, background.Y, background.Z, 1f)),
        };

        for (int i = 1; i < colors.Length; i++)
        {
            attachments[i] = new RenderingAttachmentInfo
            {
                SType = StructureType.RenderingAttachmentInfo,
                ImageView = colors[i],
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
            ImageView = depth,
            ImageLayout = ImageLayout.DepthStencilAttachmentOptimal,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = keepDepth ? AttachmentStoreOp.Store : AttachmentStoreOp.DontCare,
            ClearValue = new ClearValue(depthStencil: new ClearDepthStencilValue(1f, 0)),
        };

        var rendering = new RenderingInfo
        {
            SType = StructureType.RenderingInfo,
            RenderArea = new Rect2D { Extent = new Extent2D((uint)width, (uint)height) },
            LayerCount = 1,
            ColorAttachmentCount = GBuffer.Targets,
            PColorAttachments = attachments,
            PDepthAttachment = &depthAttachment,
        };

        vk.CmdBeginRendering(command, in rendering);
    }

    /// <summary>Binds the pipeline and camera, then records the geometry's draws.</summary>
    /// <param name="vk">Vulkan API.</param>
    /// <param name="command">Command buffer to record into.</param>
    /// <param name="pipeline">Pipeline to draw with.</param>
    /// <param name="frames">Per-frame camera resources.</param>
    /// <param name="geometry">What to draw.</param>
    /// <param name="frame">Which frame in flight this is.</param>
    /// <param name="width">Target width.</param>
    /// <param name="height">Target height.</param>
    /// <param name="camera">Where to look from.</param>
    /// <param name="reflection">
    /// Whether this is the mirror's pass rather than the frame's. It takes the second of the
    /// frame's two constant buffers, so that the two passes recorded into one command buffer
    /// do not read each other's camera, and leaves the motion history to the frame.
    /// </param>
    public static void Record(
        Vk vk,
        CommandBuffer command,
        MeshPipeline pipeline,
        FrameUniformSet frames,
        SceneGeometry geometry,
        int frame,
        int width,
        int height,
        Camera camera,
        bool reflection = false)
    {
        ArgumentNullException.ThrowIfNull(vk);
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(geometry);

        var viewport = new Viewport { Width = width, Height = height, MaxDepth = 1f };
        var scissor = new Rect2D { Extent = new Extent2D((uint)width, (uint)height) };

        vk.CmdSetViewport(command, 0, 1, in viewport);
        vk.CmdSetScissor(command, 0, 1, in scissor);
        vk.CmdBindPipeline(command, PipelineBindPoint.Graphics, pipeline.Handle);

        frames.Bind(
            command, pipeline, frame, camera, (float)width / height, width, height, reflection);
        MeshPipeline.Record(vk, command, pipeline, geometry.Draws(frames.PreviousSeconds));
    }
}
