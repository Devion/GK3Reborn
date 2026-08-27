using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;

namespace GK3Reborn.Rendering.Vulkan;

/// <summary>
/// Draws the reconstructed horizon: real terrain, its forest, and a generated sky,
/// where the painted skybox was.
/// </summary>
/// <remarks>
/// <para>
/// The backdrop lives in its own metric space — metres around the scene's centre — and
/// is drawn with its own projection, so no room unit ever meets a terrain metre except
/// at one constant: <see cref="MetersPerUnit"/> turns the camera's offset from the
/// scene centre into a movement through the backdrop, which is what gives the horizon
/// parallax instead of the swimming a camera-glued skybox shows on every cut and glide.
/// </para>
/// <para>
/// It cannot share the room's depth range — the room's projection has no idea what four
/// kilometres are — so the vertex stages squeeze the backdrop's whole depth into the far
/// tail of the buffer, above 0.999. The room always wins the depth test against it, the
/// backdrop still sorts against itself inside the tail, and the generated sky at exactly
/// 1.0 loses to both.
/// </para>
/// <para>
/// When this draws, the painted cubemap does not: its mountains are baked into the
/// picture and would double-expose against the reconstructed ridge. The sky here is a
/// gradient with the scene's own sun in it, near-black when the hour has no sun, and
/// the cubemap survives only as the fallback for a backdrop that would not build.
/// </para>
/// <para>
/// Texturing is four tileable ground textures blended by the offline splat weights,
/// with rock forced onto steep faces, each sampled at two scales so the repeat period
/// never shows, and the vista's colour applied hue-only over the top. The forest is the
/// offline tree instances drawn as cone impostors in one instanced call — stand-ins at
/// backdrop distances, to be traded for the modelled trees when a LOD path exists. The
/// full recipe and why each rule exists:
/// <c>ContentWorkspace/enhanced/skyboxes/terrain-plan.md</c>.
/// </para>
/// </remarks>
public sealed unsafe class TerrainPipeline : IDisposable
{
    /// <summary>How many metres of backdrop one unit of room is worth.</summary>
    /// <remarks>
    /// GK3's people are about seventy units for a grown adult, so a unit is roughly an
    /// inch; 0.025 keeps a walk across a courtyard a walk, not a flight.
    /// </remarks>
    public const float MetersPerUnit = 0.025f;

    private const string VertexSource = """
        #version 450

        layout(location = 0) in vec3 inPosition;
        layout(location = 1) in vec3 inNormal;

        layout(push_constant) uniform Push
        {
            mat4 viewProjection;  // terrain space to clip, camera offset included
            vec4 sun;             // xyz: toward the sun, in terrain space; w: 1 by day
            vec4 params;          // x: tile metres, y: tint amount, z: fog density, w: extent
            vec4 eye;             // xyz: the camera in terrain space
        } push;

        layout(location = 0) out vec3 vWorld;
        layout(location = 1) out vec3 vNormal;

        void main()
        {
            vWorld = inPosition;
            vNormal = inNormal;

            vec4 clip = push.viewProjection * vec4(inPosition, 1.0);

            // The room's projection and this one share nothing, so the backdrop takes
            // the far tail of the depth buffer for itself: every fragment lands in
            // [0.999, 1), the room is always nearer, the sky at 1.0 is always farther,
            // and the backdrop still sorts against itself inside the tail.
            float zNdc = clamp(clip.z / max(clip.w, 1e-4), 0.0, 1.0);
            clip.z = (0.9990 + 0.000999 * zNdc) * clip.w;
            gl_Position = clip;
        }
        """;

    private const string FragmentSource = """
        #version 450

        layout(binding = 0) uniform sampler2D tileForest;
        layout(binding = 1) uniform sampler2D tileRock;
        layout(binding = 2) uniform sampler2D tileGrass;
        layout(binding = 3) uniform sampler2D tileDirt;
        layout(binding = 4) uniform sampler2D splat;
        layout(binding = 5) uniform sampler2D tint;

        layout(push_constant) uniform Push
        {
            mat4 viewProjection;
            vec4 sun;
            vec4 params;
            vec4 eye;
        } push;

        layout(location = 0) in vec3 vWorld;
        layout(location = 1) in vec3 vNormal;

        layout(location = 0) out vec4 outColor;

        // The same texture at two scales, mixed: a single period is visible from one
        // ridge to the next, the pair never lines up.
        vec3 tile2(sampler2D t, vec2 uv)
        {
            return mix(texture(t, uv).rgb, texture(t, uv * 0.23 + vec2(7.31, 3.7)).rgb, 0.45);
        }

        void main()
        {
            vec2 gridUv = (vWorld.xz / (2.0 * push.params.w)) + 0.5;
            vec4 w = texture(splat, gridUv);

            // A cliff is rock whatever grew on the map: the weights were read off a
            // painting seen from face on, and a face-on painting has no slopes in it.
            float slope = 1.0 - clamp(vNormal.y, 0.0, 1.0);
            w.g = max(w.g, smoothstep(0.5, 0.8, slope));
            w /= max(w.r + w.g + w.b + w.a, 1e-4);

            vec2 uv = vWorld.xz / push.params.x;
            vec3 albedo = (w.r * tile2(tileForest, uv))
                        + (w.g * tile2(tileRock, uv))
                        + (w.b * tile2(tileGrass, uv))
                        + (w.a * tile2(tileDirt, uv));

            // Hue only: the vista's colour mood without the old painting's darkness.
            vec3 mood = texture(tint, gridUv).rgb;
            float luminance = dot(mood, vec3(0.299, 0.587, 0.114));
            albedo = mix(albedo, albedo * (mood / max(luminance, 1e-3)), push.params.y);

            // A sunless hour is a dark one: the night sets carry their day sibling's
            // geometry and colours, and the hour's whole difference is made here.
            float toSun = max(dot(normalize(vNormal), push.sun.xyz), 0.0) * push.sun.w;
            vec3 ambient = mix(vec3(0.045, 0.055, 0.085), vec3(0.26, 0.30, 0.38), push.sun.w);
            vec3 lit = albedo * (ambient + (vec3(1.38, 1.26, 1.06) * toSun));

            // The canopy's shadow: ground under a dense wood is darker than the open
            // hillside, which is what visually plants the trees standing on it.
            lit *= 1.0 - (0.32 * w.r);

            // Distance haze against the sky's own horizon colour, from where the camera
            // stands in the backdrop rather than from its centre.
            vec3 haze = mix(vec3(0.05, 0.06, 0.09), vec3(0.75, 0.82, 0.88), push.sun.w);
            float away = length(vWorld - push.eye.xyz);
            float fog = 1.0 - exp(-push.params.z * push.params.z * away * away);
            outColor = vec4(mix(lit, haze, fog), 1.0);
        }
        """;

    private const string TreeVertexSource = """
        #version 450

        layout(location = 0) in vec3 inPosition;
        layout(location = 1) in vec3 inNormal;
        layout(location = 2) in vec4 inPlace;   // xyz: base of the tree; w: scale
        layout(location = 3) in float inTurn;   // yaw, radians

        layout(push_constant) uniform Push
        {
            mat4 viewProjection;
            vec4 sun;
            vec4 params;
            vec4 eye;
        } push;

        layout(location = 0) out vec3 vWorld;
        layout(location = 1) out vec3 vNormal;
        layout(location = 2) out float vSeed;
        layout(location = 3) out float vCrown;

        void main()
        {
            float c = cos(inTurn);
            float s = sin(inTurn);
            mat3 turn = mat3(c, 0.0, -s, 0.0, 1.0, 0.0, s, 0.0, c);

            // Identical cones read as a wall; a second, independent stretch of each
            // tree's height breaks the ridge line into crowns.
            vSeed = fract(inTurn * 7.13 + inPlace.x * 0.017);
            vec3 shaped = inPosition * vec3(1.0, mix(0.75, 1.35, fract(vSeed * 9.7)), 1.0);

            vec3 world = inPlace.xyz + (turn * (shaped * inPlace.w));
            vWorld = world;
            vNormal = turn * inNormal;
            vCrown = clamp(inPosition.y / 14.0, 0.0, 1.0);

            vec4 clip = push.viewProjection * vec4(world, 1.0);
            float zNdc = clamp(clip.z / max(clip.w, 1e-4), 0.0, 1.0);
            clip.z = (0.9990 + 0.000999 * zNdc) * clip.w;
            gl_Position = clip;
        }
        """;

    private const string TreeFragmentSource = """
        #version 450

        layout(binding = 4) uniform sampler2D splat;
        layout(binding = 5) uniform sampler2D tint;

        layout(push_constant) uniform Push
        {
            mat4 viewProjection;
            vec4 sun;
            vec4 params;
            vec4 eye;
        } push;

        layout(location = 0) in vec3 vWorld;
        layout(location = 1) in vec3 vNormal;
        layout(location = 2) in float vSeed;
        layout(location = 3) in float vCrown;

        layout(location = 0) out vec4 outColor;

        void main()
        {
            // A conifer's green, varied per tree, pulled toward the vista's own colour
            // so a wood follows the painting the way the ground does.
            vec3 albedo = mix(vec3(0.075, 0.13, 0.07), vec3(0.13, 0.19, 0.09), vSeed);
            vec2 gridUv = (vWorld.xz / (2.0 * push.params.w)) + 0.5;
            vec3 mood = texture(tint, gridUv).rgb;
            float luminance = dot(mood, vec3(0.299, 0.587, 0.114));
            albedo = mix(albedo, albedo * (mood / max(luminance, 1e-3)), 0.4);

            // The occlusion that makes a mass of cones read as trees. Vertical: a
            // canopy is dark at its floor and lit at its crown. Crowd: a tree deep in
            // the wood is shaded by its neighbours — the forest weight under it says
            // how deep — while a tree on the edge stands in the open.
            float density = texture(splat, gridUv).r;
            float occlusion = mix(0.42, 1.0, vCrown) * (1.0 - (0.45 * density * (1.0 - vCrown)));

            float toSun = max(dot(normalize(vNormal), push.sun.xyz), 0.0) * push.sun.w;
            vec3 ambient = mix(vec3(0.045, 0.055, 0.085), vec3(0.26, 0.30, 0.38), push.sun.w);
            vec3 lit = albedo * ((ambient * occlusion)
                               + (vec3(1.38, 1.26, 1.06) * toSun * mix(0.55, 1.0, vCrown)));

            vec3 haze = mix(vec3(0.05, 0.06, 0.09), vec3(0.75, 0.82, 0.88), push.sun.w);
            float away = length(vWorld - push.eye.xyz);
            float fog = 1.0 - exp(-push.params.z * push.params.z * away * away);
            outColor = vec4(mix(lit, haze, fog), 1.0);
        }
        """;

    private const string SkyVertexSource = """
        #version 450

        // One triangle covering the screen, from the vertex index alone, at the far
        // plane so the terrain and the room have both already claimed their pixels.
        void main()
        {
            vec2 corner = vec2((gl_VertexIndex << 1) & 2, gl_VertexIndex & 2);
            gl_Position = vec4((corner * 2.0) - 1.0, 1.0, 1.0);
        }
        """;

    private const string SkyFragmentSource = """
        #version 450

        layout(push_constant) uniform Push
        {
            vec4 forward;   // xyz: where the camera looks
            vec4 right;     // xyz: its right;  w: tan of half the horizontal fov
            vec4 up;        // xyz: its up;     w: tan of half the vertical fov
            vec4 viewport;  // xy: size in pixels
            vec4 sun;       // xyz: toward the sun, world frame; w: 1 by day
        } push;

        layout(location = 0) out vec4 outColor;

        void main()
        {
            vec2 ndc = ((gl_FragCoord.xy / push.viewport.xy) * 2.0) - 1.0;
            vec3 ray = normalize(push.forward.xyz
                               + (push.right.xyz * (ndc.x * push.right.w))
                               - (push.up.xyz * (ndc.y * push.up.w)));

            // A plain atmosphere: bright at the horizon, deeper overhead, near-black
            // when the hour has no sun. The painted mountains that used to live in the
            // cubemap are real geometry now, so the sky is only sky.
            float day = push.sun.w;
            float height = clamp(ray.y, 0.0, 1.0);
            vec3 zenith = mix(vec3(0.012, 0.018, 0.038), vec3(0.22, 0.42, 0.72), day);
            vec3 horizon = mix(vec3(0.045, 0.055, 0.085), vec3(0.74, 0.81, 0.88), day);
            vec3 sky = mix(horizon, zenith, pow(height, 0.55));

            // The sun itself, and the glow around it, only while it is up.
            float facing = max(dot(ray, push.sun.xyz), 0.0);
            sky += day * (vec3(1.0, 0.92, 0.75) * pow(facing, 900.0) * 4.0
                        + vec3(0.9, 0.82, 0.62) * pow(facing, 12.0) * 0.16);

            outColor = vec4(sky, 1.0);
        }
        """;

    private readonly Vk _vk;
    private readonly VulkanContext _context;

    private ShaderModule _vertexModule;
    private ShaderModule _fragmentModule;
    private ShaderModule _treeVertexModule;
    private ShaderModule _treeFragmentModule;
    private ShaderModule _skyVertexModule;
    private ShaderModule _skyFragmentModule;
    private DescriptorSetLayout _setLayout;
    private DescriptorPool _pool;
    private DescriptorSet _set;
    private PipelineLayout _layout;
    private PipelineLayout _skyLayout;
    private Pipeline _pipeline;
    private Pipeline _treePipeline;
    private Pipeline _skyPipeline;
    private VulkanBuffer? _vertices;
    private VulkanBuffer? _indices;
    private uint _indexCount;
    private VulkanBuffer? _treeVertices;
    private VulkanBuffer? _treeIndices;
    private uint _treeIndexCount;
    private VulkanBuffer? _treeInstances;
    private uint _treeCount;
    private readonly VulkanTexture?[] _textures = new VulkanTexture?[6];
    private float _extent;
    private Vector3? _sunDirection;
    private float _azimuth;
    private Vector3 _anchorUnits;

    private TerrainPipeline(VulkanContext context)
    {
        _context = context;
        _vk = context.Api;
    }

    /// <summary>How many metres of ground one tile of texture covers.</summary>
    public float TileMeters { get; set; } = 60f;

    /// <summary>How far the whole backdrop is raised against the camera, in metres.</summary>
    /// <remarks>
    /// The offline heights put the panorama's own camera at zero, but the room's
    /// cameras stand wherever the scenes put them — often high enough that whole
    /// hillsides sink below the visible horizon. Raising the backdrop is done by
    /// standing the camera lower in it, which carries the fog along for free.
    /// </remarks>
    public float LiftMeters { get; set; } = 12f;

    /// <summary>How strongly the vista's colour is laid over the tiles, zero to one.</summary>
    public float TintAmount { get; set; } = 0.6f;

    /// <summary>Creates the pipeline for one scene's backdrop.</summary>
    /// <param name="context">Device context.</param>
    /// <param name="colorFormat">Colour target format.</param>
    /// <param name="depthFormat">Depth target format.</param>
    /// <param name="compiler">Shader compiler.</param>
    /// <param name="backdrop">The terrain, forest and layers to build and draw.</param>
    /// <returns>The pipeline.</returns>
    public static TerrainPipeline Create(
        VulkanContext context,
        Format colorFormat,
        Format depthFormat,
        ShaderCompiler compiler,
        TerrainBackdrop backdrop)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(compiler);
        ArgumentNullException.ThrowIfNull(backdrop);

        var pipeline = new TerrainPipeline(context)
        {
            _extent = backdrop.ExtentMeters,
            _sunDirection = backdrop.SunDirection,
            _azimuth = backdrop.Azimuth,
            _anchorUnits = backdrop.AnchorUnits,
        };

        try
        {
            pipeline._vertexModule = pipeline.CreateModule(compiler.Compile(
                VertexSource, ShaderStage.Vertex, "terrain.vert", "main", ShaderLanguage.Glsl));
            pipeline._fragmentModule = pipeline.CreateModule(compiler.Compile(
                FragmentSource, ShaderStage.Fragment, "terrain.frag", "main", ShaderLanguage.Glsl));
            pipeline._treeVertexModule = pipeline.CreateModule(compiler.Compile(
                TreeVertexSource, ShaderStage.Vertex, "trees.vert", "main", ShaderLanguage.Glsl));
            pipeline._treeFragmentModule = pipeline.CreateModule(compiler.Compile(
                TreeFragmentSource, ShaderStage.Fragment, "trees.frag", "main", ShaderLanguage.Glsl));
            pipeline._skyVertexModule = pipeline.CreateModule(compiler.Compile(
                SkyVertexSource, ShaderStage.Vertex, "horizon-sky.vert", "main", ShaderLanguage.Glsl));
            pipeline._skyFragmentModule = pipeline.CreateModule(compiler.Compile(
                SkyFragmentSource, ShaderStage.Fragment, "horizon-sky.frag", "main", ShaderLanguage.Glsl));

            pipeline.BuildMesh(backdrop);
            pipeline.BuildTrees(backdrop);

            // The tiles repeat and are colour; the splat is data and must not be
            // sRGB-decoded or wrapped; the tint is colour but clamped like the splat.
            pipeline._textures[0] = VulkanTexture.Create(context, backdrop.TileForest);
            pipeline._textures[1] = VulkanTexture.Create(context, backdrop.TileRock);
            pipeline._textures[2] = VulkanTexture.Create(context, backdrop.TileGrass);
            pipeline._textures[3] = VulkanTexture.Create(context, backdrop.TileDirt);
            pipeline._textures[4] = VulkanTexture.Create(
                context, backdrop.Splat, mipmaps: false,
                SamplerAddressMode.ClampToEdge, linear: true);
            pipeline._textures[5] = VulkanTexture.Create(
                context, backdrop.Tint, mipmaps: false, SamplerAddressMode.ClampToEdge);

            pipeline.CreateDescriptors();
            pipeline.BuildPipelines(colorFormat, depthFormat);

            return pipeline;
        }
        catch
        {
            pipeline.Dispose();
            throw;
        }
    }

    /// <summary>Records the backdrop: terrain, forest, then the sky behind them.</summary>
    /// <param name="command">Command buffer to record into.</param>
    /// <param name="camera">Where the player is looking from, in room units.</param>
    /// <param name="width">Viewport width.</param>
    /// <param name="height">Viewport height.</param>
    public void Record(CommandBuffer command, Camera camera, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(camera);

        if (_vertices is null || _indices is null || width <= 0 || height <= 0)
        {
            return;
        }

        // The camera's offset from the scene centre, turned into backdrop metres and
        // into the backdrop's own frame — the sky's azimuth separates the two. Clamped
        // so no camera the scripts place can leave the grid or dive through a ridge.
        Matrix4x4 intoTerrain = Matrix4x4.CreateRotationY(-_azimuth);
        Vector3 offset = Vector3.TransformNormal(
            (camera.Position - _anchorUnits) * MetersPerUnit, intoTerrain);

        float reach = _extent * 0.25f;
        if (offset.Length() > reach)
        {
            offset = Vector3.Normalize(offset) * reach;
        }

        offset.Y -= LiftMeters;

        Vector3 forward = Vector3.Normalize(camera.Target - camera.Position);
        Vector3 forwardT = Vector3.TransformNormal(forward, intoTerrain);
        Vector3 upT = Vector3.TransformNormal(camera.Up, intoTerrain);

        Matrix4x4 view = Matrix4x4.CreateLookAtLeftHanded(offset, offset + forwardT, upT);
        Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfViewLeftHanded(
            camera.FieldOfView, (float)width / height, 2f, _extent * 3f);
        projection.M22 *= -1;

        // The sun the scene is lit by, brought into the backdrop's frame so a slope
        // faces it the same way the room's shadows say it should.
        Vector4 sun = _sunDirection is { } travelling
            ? new Vector4(
                Vector3.TransformNormal(Vector3.Normalize(-travelling), intoTerrain), 1f)
            : new Vector4(0f, 1f, 0f, 0f);

        var push = new TerrainPush
        {
            ViewProjection = view * projection,
            Sun = sun,
            Params = new Vector4(TileMeters, TintAmount, 1.6e-4f, _extent),
            Eye = new Vector4(offset, 0f),
        };

        var viewport = new Viewport { Width = width, Height = height, MaxDepth = 1f };
        var scissor = new Rect2D { Extent = new Extent2D((uint)width, (uint)height) };

        _vk.CmdSetViewport(command, 0, 1, in viewport);
        _vk.CmdSetScissor(command, 0, 1, in scissor);

        DescriptorSet set = _set;
        _vk.CmdBindDescriptorSets(
            command, PipelineBindPoint.Graphics, _layout, 0, 1, in set, 0, null);
        _vk.CmdPushConstants(
            command, _layout, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, 0,
            (uint)Marshal.SizeOf<TerrainPush>(), &push);

        _vk.CmdBindPipeline(command, PipelineBindPoint.Graphics, _pipeline);
        Silk.NET.Vulkan.Buffer vertexBuffer = _vertices.Handle;
        ulong offsetZero = 0;
        _vk.CmdBindVertexBuffers(command, 0, 1, in vertexBuffer, in offsetZero);
        _vk.CmdBindIndexBuffer(command, _indices.Handle, 0, IndexType.Uint32);
        _vk.CmdDrawIndexed(command, _indexCount, 1, 0, 0, 0);

        if (_treeInstances is not null && _treeCount > 0)
        {
            _vk.CmdBindPipeline(command, PipelineBindPoint.Graphics, _treePipeline);

            Silk.NET.Vulkan.Buffer* treeStreams = stackalloc Silk.NET.Vulkan.Buffer[2]
            {
                _treeVertices!.Handle,
                _treeInstances.Handle,
            };
            ulong* treeOffsets = stackalloc ulong[2] { 0, 0 };
            _vk.CmdBindVertexBuffers(command, 0, 2, treeStreams, treeOffsets);
            _vk.CmdBindIndexBuffer(command, _treeIndices!.Handle, 0, IndexType.Uint16);
            _vk.CmdDrawIndexed(command, _treeIndexCount, _treeCount, 0, 0, 0);
        }

        // The sky last, at the far plane, over exactly the pixels nothing claimed.
        var skyPush = new SkyPush
        {
            Forward = new Vector4(forward, 0f),
            Right = new Vector4(
                Vector3.Normalize(Vector3.Cross(camera.Up, forward)),
                MathF.Tan(camera.FieldOfView / 2f) * width / height),
            Up = new Vector4(camera.Up, MathF.Tan(camera.FieldOfView / 2f)),
            Viewport = new Vector4(width, height, 0f, 0f),
            Sun = _sunDirection is { } sunWorld
                ? new Vector4(Vector3.Normalize(-sunWorld), 1f)
                : new Vector4(0f, 1f, 0f, 0f),
        };

        _vk.CmdBindPipeline(command, PipelineBindPoint.Graphics, _skyPipeline);
        _vk.CmdPushConstants(
            command, _skyLayout, ShaderStageFlags.FragmentBit, 0,
            (uint)Marshal.SizeOf<SkyPush>(), &skyPush);
        _vk.CmdDraw(command, 3, 1, 0, 0);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        DestroyPipeline(ref _pipeline);
        DestroyPipeline(ref _treePipeline);
        DestroyPipeline(ref _skyPipeline);

        if (_layout.Handle != 0)
        {
            _vk.DestroyPipelineLayout(_context.Device, _layout, null);
            _layout = default;
        }

        if (_skyLayout.Handle != 0)
        {
            _vk.DestroyPipelineLayout(_context.Device, _skyLayout, null);
            _skyLayout = default;
        }

        if (_pool.Handle != 0)
        {
            _vk.DestroyDescriptorPool(_context.Device, _pool, null);
            _pool = default;
        }

        if (_setLayout.Handle != 0)
        {
            _vk.DestroyDescriptorSetLayout(_context.Device, _setLayout, null);
            _setLayout = default;
        }

        DestroyModule(ref _vertexModule);
        DestroyModule(ref _fragmentModule);
        DestroyModule(ref _treeVertexModule);
        DestroyModule(ref _treeFragmentModule);
        DestroyModule(ref _skyVertexModule);
        DestroyModule(ref _skyFragmentModule);

        _vertices?.Dispose();
        _vertices = null;
        _indices?.Dispose();
        _indices = null;
        _treeVertices?.Dispose();
        _treeVertices = null;
        _treeIndices?.Dispose();
        _treeIndices = null;
        _treeInstances?.Dispose();
        _treeInstances = null;

        for (int i = 0; i < _textures.Length; i++)
        {
            _textures[i]?.Dispose();
            _textures[i] = null;
        }
    }

    private void DestroyPipeline(ref Pipeline pipeline)
    {
        if (pipeline.Handle != 0)
        {
            _vk.DestroyPipeline(_context.Device, pipeline, null);
            pipeline = default;
        }
    }

    private void DestroyModule(ref ShaderModule module)
    {
        if (module.Handle != 0)
        {
            _vk.DestroyShaderModule(_context.Device, module, null);
            module = default;
        }
    }

    /// <summary>One corner of the grid: where it is and which way its ground faces.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct TerrainVertex(Vector3 Position, Vector3 Normal);

    [StructLayout(LayoutKind.Sequential)]
    private struct TerrainPush
    {
        /// <summary>Backdrop space to clip, the camera's offset included.</summary>
        public Matrix4x4 ViewProjection;

        /// <summary>Toward the sun in the backdrop's frame; w is zero for a sunless hour.</summary>
        public Vector4 Sun;

        /// <summary>Tile metres, tint amount, fog density, grid extent.</summary>
        public Vector4 Params;

        /// <summary>The camera, in backdrop metres, for the haze.</summary>
        public Vector4 Eye;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SkyPush
    {
        /// <summary>Where the camera looks.</summary>
        public Vector4 Forward;

        /// <summary>Its right, with the tangent of half the horizontal field of view in w.</summary>
        public Vector4 Right;

        /// <summary>Its up, with the tangent of half the vertical field of view in w.</summary>
        public Vector4 Up;

        /// <summary>Width and height in pixels.</summary>
        public Vector4 Viewport;

        /// <summary>Toward the sun, world frame; w is zero for a sunless hour.</summary>
        public Vector4 Sun;
    }

    private void BuildMesh(TerrainBackdrop backdrop)
    {
        // Every other grid cell: 512 by 512 corners over the 1024 grid is a quarter of
        // the vertices for a silhouette the eye cannot tell apart at these distances.
        const int Stride = 2;

        int grid = backdrop.Grid;
        float extent = backdrop.ExtentMeters;
        float[] heights = backdrop.Heights;

        if (heights.Length != grid * grid)
        {
            throw new VulkanException(
                $"A terrain backdrop's heights are {heights.Length} values for a " +
                $"{grid} by {grid} grid.");
        }

        int side = ((grid - 1) / Stride) + 1;
        float step = (2f * extent) / (grid - 1);

        var vertices = new TerrainVertex[side * side];

        for (int row = 0; row < side; row++)
        {
            int gz = Math.Min(row * Stride, grid - 1);

            for (int column = 0; column < side; column++)
            {
                int gx = Math.Min(column * Stride, grid - 1);

                // Central differences on the full-resolution grid, so a vertex the
                // stride skipped still bends the normals of its neighbours.
                float left = heights[(gz * grid) + Math.Max(gx - 1, 0)];
                float right = heights[(gz * grid) + Math.Min(gx + 1, grid - 1)];
                float near = heights[(Math.Max(gz - 1, 0) * grid) + gx];
                float far = heights[(Math.Min(gz + 1, grid - 1) * grid) + gx];

                var normal = Vector3.Normalize(
                    new Vector3(left - right, 2f * step, near - far));

                vertices[(row * side) + column] = new TerrainVertex(
                    new Vector3(
                        (gx * step) - extent,
                        heights[(gz * grid) + gx],
                        (gz * step) - extent),
                    normal);
            }
        }

        uint[] indices = new uint[(side - 1) * (side - 1) * 6];
        int write = 0;

        for (int row = 0; row < side - 1; row++)
        {
            for (int column = 0; column < side - 1; column++)
            {
                uint a = (uint)((row * side) + column);
                uint b = a + 1;
                uint c = a + (uint)side;
                uint d = c + 1;

                indices[write++] = a;
                indices[write++] = c;
                indices[write++] = b;
                indices[write++] = b;
                indices[write++] = c;
                indices[write++] = d;
            }
        }

        _vertices = VulkanBuffer.CreateDeviceLocal<TerrainVertex>(
            _context, vertices, BufferUsageFlags.VertexBufferBit);
        _indices = VulkanBuffer.CreateDeviceLocal<uint>(
            _context, indices, BufferUsageFlags.IndexBufferBit);
        _indexCount = (uint)indices.Length;
    }

    private void BuildTrees(TerrainBackdrop backdrop)
    {
        float[] trees = backdrop.Trees;

        if (trees.Length < 5)
        {
            return;
        }

        // A cone, eight sides, fourteen metres for a scale of one: a conifer impostor
        // at distances where a conifer is a silhouette. Base ring at y 0, tip at the top.
        const int Sides = 8;
        const float CrownRadius = 3.5f;
        const float CrownHeight = 14f;

        var mesh = new TerrainVertex[Sides + 1];

        for (int i = 0; i < Sides; i++)
        {
            float angle = i * (2f * MathF.PI / Sides);
            var outward = new Vector3(MathF.Cos(angle), 0f, MathF.Sin(angle));
            mesh[i] = new TerrainVertex(
                outward * CrownRadius,
                Vector3.Normalize(outward + new Vector3(0f, CrownRadius / CrownHeight, 0f)));
        }

        mesh[Sides] = new TerrainVertex(new Vector3(0f, CrownHeight, 0f), Vector3.UnitY);

        ushort[] cone = new ushort[Sides * 3];
        for (int i = 0; i < Sides; i++)
        {
            cone[(i * 3) + 0] = (ushort)i;
            cone[(i * 3) + 1] = (ushort)((i + 1) % Sides);
            cone[(i * 3) + 2] = (ushort)Sides;
        }

        // The instances, five floats each, straight from the offline placement. A cap
        // far above any real set, purely so a malformed file cannot ask for the moon.
        uint count = Math.Min((uint)(trees.Length / 5), 800_000u);

        _treeVertices = VulkanBuffer.CreateDeviceLocal<TerrainVertex>(
            _context, mesh, BufferUsageFlags.VertexBufferBit);
        _treeIndices = VulkanBuffer.CreateDeviceLocal<ushort>(
            _context, cone, BufferUsageFlags.IndexBufferBit);
        _treeInstances = VulkanBuffer.CreateDeviceLocal<float>(
            _context, trees.AsSpan(0, (int)count * 5), BufferUsageFlags.VertexBufferBit);
        _treeIndexCount = (uint)cone.Length;
        _treeCount = count;
    }

    private ShaderModule CreateModule(byte[] spirv)
    {
        fixed (byte* code = spirv)
        {
            var createInfo = new ShaderModuleCreateInfo
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)spirv.Length,
                PCode = (uint*)code,
            };

            if (_vk.CreateShaderModule(_context.Device, in createInfo, null, out ShaderModule module)
                != Result.Success)
            {
                throw new VulkanException("Could not create a terrain shader module.");
            }

            return module;
        }
    }

    private void CreateDescriptors()
    {
        DescriptorSetLayoutBinding* bindings = stackalloc DescriptorSetLayoutBinding[6];

        for (uint i = 0; i < 6; i++)
        {
            bindings[i] = new DescriptorSetLayoutBinding
            {
                Binding = i,
                DescriptorType = DescriptorType.CombinedImageSampler,
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.FragmentBit,
            };
        }

        var layoutInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 6,
            PBindings = bindings,
        };

        if (_vk.CreateDescriptorSetLayout(_context.Device, in layoutInfo, null, out _setLayout)
            != Result.Success)
        {
            throw new VulkanException("Could not create the terrain descriptor layout.");
        }

        var size = new DescriptorPoolSize
        {
            Type = DescriptorType.CombinedImageSampler,
            DescriptorCount = 6,
        };

        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            MaxSets = 1,
            PoolSizeCount = 1,
            PPoolSizes = &size,
        };

        if (_vk.CreateDescriptorPool(_context.Device, in poolInfo, null, out _pool) != Result.Success)
        {
            throw new VulkanException("Could not create the terrain descriptor pool.");
        }

        DescriptorSetLayout setLayout = _setLayout;
        var allocate = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _pool,
            DescriptorSetCount = 1,
            PSetLayouts = &setLayout,
        };

        if (_vk.AllocateDescriptorSets(_context.Device, in allocate, out _set) != Result.Success)
        {
            throw new VulkanException("Could not allocate the terrain descriptor set.");
        }

        DescriptorImageInfo* images = stackalloc DescriptorImageInfo[6];
        WriteDescriptorSet* writes = stackalloc WriteDescriptorSet[6];

        for (int i = 0; i < 6; i++)
        {
            VulkanTexture texture = _textures[i]!;
            images[i] = new DescriptorImageInfo
            {
                ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
                ImageView = texture.View,
                Sampler = texture.Sampler,
            };

            writes[i] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = _set,
                DstBinding = (uint)i,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.CombinedImageSampler,
                PImageInfo = images + i,
            };
        }

        _vk.UpdateDescriptorSets(_context.Device, 6, writes, 0, null);
    }

    private void BuildPipelines(Format colorFormat, Format depthFormat)
    {
        DescriptorSetLayout setLayout = _setLayout;

        var pushConstants = new PushConstantRange
        {
            StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
            Offset = 0,
            Size = (uint)Marshal.SizeOf<TerrainPush>(),
        };

        var layoutInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1,
            PSetLayouts = &setLayout,
            PushConstantRangeCount = 1,
            PPushConstantRanges = &pushConstants,
        };

        if (_vk.CreatePipelineLayout(_context.Device, in layoutInfo, null, out _layout)
            != Result.Success)
        {
            throw new VulkanException("Could not create the terrain pipeline layout.");
        }

        var skyPush = new PushConstantRange
        {
            StageFlags = ShaderStageFlags.FragmentBit,
            Offset = 0,
            Size = (uint)Marshal.SizeOf<SkyPush>(),
        };

        var skyLayoutInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            PushConstantRangeCount = 1,
            PPushConstantRanges = &skyPush,
        };

        if (_vk.CreatePipelineLayout(_context.Device, in skyLayoutInfo, null, out _skyLayout)
            != Result.Success)
        {
            throw new VulkanException("Could not create the horizon sky pipeline layout.");
        }

        // Terrain: one 24-byte stream of position and normal.
        var terrainBinding = new VertexInputBindingDescription
        {
            Binding = 0,
            Stride = (uint)Marshal.SizeOf<TerrainVertex>(),
            InputRate = VertexInputRate.Vertex,
        };

        VertexInputAttributeDescription* terrainAttributes =
            stackalloc VertexInputAttributeDescription[2]
            {
                new() { Location = 0, Binding = 0, Format = Format.R32G32B32Sfloat, Offset = 0 },
                new() { Location = 1, Binding = 0, Format = Format.R32G32B32Sfloat, Offset = 12 },
            };

        _pipeline = BuildOne(
            colorFormat, depthFormat, _vertexModule, _fragmentModule, _layout,
            1, &terrainBinding, 2, terrainAttributes, depthWrite: true);

        // Trees: the cone in stream 0, one 20-byte placement per instance in stream 1.
        VertexInputBindingDescription* treeBindings =
            stackalloc VertexInputBindingDescription[2]
            {
                new()
                {
                    Binding = 0,
                    Stride = (uint)Marshal.SizeOf<TerrainVertex>(),
                    InputRate = VertexInputRate.Vertex,
                },
                new()
                {
                    Binding = 1,
                    Stride = 5 * sizeof(float),
                    InputRate = VertexInputRate.Instance,
                },
            };

        VertexInputAttributeDescription* treeAttributes =
            stackalloc VertexInputAttributeDescription[4]
            {
                new() { Location = 0, Binding = 0, Format = Format.R32G32B32Sfloat, Offset = 0 },
                new() { Location = 1, Binding = 0, Format = Format.R32G32B32Sfloat, Offset = 12 },
                new() { Location = 2, Binding = 1, Format = Format.R32G32B32A32Sfloat, Offset = 0 },
                new() { Location = 3, Binding = 1, Format = Format.R32Sfloat, Offset = 16 },
            };

        _treePipeline = BuildOne(
            colorFormat, depthFormat, _treeVertexModule, _treeFragmentModule, _layout,
            2, treeBindings, 4, treeAttributes, depthWrite: true);

        // The sky: no vertex input at all, and no depth writes — it must lose to
        // everything and stop nothing.
        _skyPipeline = BuildOne(
            colorFormat, depthFormat, _skyVertexModule, _skyFragmentModule, _skyLayout,
            0, null, 0, null, depthWrite: false);
    }

    private Pipeline BuildOne(
        Format colorFormat,
        Format depthFormat,
        ShaderModule vertex,
        ShaderModule fragment,
        PipelineLayout layout,
        uint bindingCount,
        VertexInputBindingDescription* bindings,
        uint attributeCount,
        VertexInputAttributeDescription* attributes,
        bool depthWrite)
    {
        nint entryPoint = SilkMarshal.StringToPtr("main");

        try
        {
            PipelineShaderStageCreateInfo* stages = stackalloc PipelineShaderStageCreateInfo[2];
            stages[0] = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.VertexBit,
                Module = vertex,
                PName = (byte*)entryPoint,
            };
            stages[1] = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.FragmentBit,
                Module = fragment,
                PName = (byte*)entryPoint,
            };

            var vertexInput = new PipelineVertexInputStateCreateInfo
            {
                SType = StructureType.PipelineVertexInputStateCreateInfo,
                VertexBindingDescriptionCount = bindingCount,
                PVertexBindingDescriptions = bindings,
                VertexAttributeDescriptionCount = attributeCount,
                PVertexAttributeDescriptions = attributes,
            };

            var inputAssembly = new PipelineInputAssemblyStateCreateInfo
            {
                SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                Topology = PrimitiveTopology.TriangleList,
            };

            DynamicState* dynamicStates = stackalloc DynamicState[2]
            {
                DynamicState.Viewport,
                DynamicState.Scissor,
            };

            var dynamic = new PipelineDynamicStateCreateInfo
            {
                SType = StructureType.PipelineDynamicStateCreateInfo,
                DynamicStateCount = 2,
                PDynamicStates = dynamicStates,
            };

            var viewport = new PipelineViewportStateCreateInfo
            {
                SType = StructureType.PipelineViewportStateCreateInfo,
                ViewportCount = 1,
                ScissorCount = 1,
            };

            // No culling: whether a grid's winding survives the world's handedness is
            // exactly the kind of thing that would otherwise be diagnosed as a black
            // screen, and a heightfield seen from above has almost no back faces anyway.
            var rasterization = new PipelineRasterizationStateCreateInfo
            {
                SType = StructureType.PipelineRasterizationStateCreateInfo,
                PolygonMode = PolygonMode.Fill,
                LineWidth = 1f,
                CullMode = CullModeFlags.None,
                FrontFace = FrontFace.CounterClockwise,
            };

            var multisample = new PipelineMultisampleStateCreateInfo
            {
                SType = StructureType.PipelineMultisampleStateCreateInfo,
                RasterizationSamples = SampleCountFlags.Count1Bit,
            };

            var depth = new PipelineDepthStencilStateCreateInfo
            {
                SType = StructureType.PipelineDepthStencilStateCreateInfo,
                DepthTestEnable = true,
                DepthWriteEnable = depthWrite,
                DepthCompareOp = CompareOp.LessOrEqual,
            };

            PipelineColorBlendAttachmentState* blendAttachments =
                stackalloc PipelineColorBlendAttachmentState[(int)GBuffer.Targets];

            blendAttachments[GBuffer.Colour] = new PipelineColorBlendAttachmentState
            {
                ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit |
                                 ColorComponentFlags.BBit | ColorComponentFlags.ABit,
            };

            for (int i = 1; i < (int)GBuffer.Targets; i++)
            {
                blendAttachments[i] = default;
            }

            var blend = new PipelineColorBlendStateCreateInfo
            {
                SType = StructureType.PipelineColorBlendStateCreateInfo,
                AttachmentCount = GBuffer.Targets,
                PAttachments = blendAttachments,
            };

            Format* colors = stackalloc Format[(int)GBuffer.Targets]
            {
                colorFormat,
                GBuffer.NormalFormat,
                GBuffer.MotionFormat,
                GBuffer.LightFormat,
            };
            var rendering = new PipelineRenderingCreateInfo
            {
                SType = StructureType.PipelineRenderingCreateInfo,
                ColorAttachmentCount = GBuffer.Targets,
                PColorAttachmentFormats = colors,
                DepthAttachmentFormat = depthFormat,
            };

            var createInfo = new GraphicsPipelineCreateInfo
            {
                SType = StructureType.GraphicsPipelineCreateInfo,
                PNext = &rendering,
                StageCount = 2,
                PStages = stages,
                PVertexInputState = &vertexInput,
                PInputAssemblyState = &inputAssembly,
                PViewportState = &viewport,
                PRasterizationState = &rasterization,
                PMultisampleState = &multisample,
                PDepthStencilState = &depth,
                PColorBlendState = &blend,
                PDynamicState = &dynamic,
                Layout = layout,
            };

            Result created = _vk.CreateGraphicsPipelines(
                _context.Device, default, 1, in createInfo, null, out Pipeline pipeline);

            if (created != Result.Success)
            {
                throw new VulkanException($"Could not create a terrain pipeline: {created}.");
            }

            return pipeline;
        }
        finally
        {
            SilkMarshal.Free(entryPoint);
        }
    }
}
