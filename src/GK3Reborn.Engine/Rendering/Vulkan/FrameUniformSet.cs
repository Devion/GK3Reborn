using System.Numerics;
using System.Runtime.InteropServices;
using GK3Reborn.Formats.Scenes;
using Silk.NET.Vulkan;

namespace GK3Reborn.Rendering.Vulkan;

/// <summary>
/// The camera, as the shader sees it: one uniform buffer and its descriptor set per frame
/// in flight.
/// </summary>
/// <remarks>
/// A buffer per frame rather than one shared buffer, because the GPU may still be reading
/// the previous frame's camera when the next frame is recorded. Overwriting it there is
/// the classic cause of a view that jitters by exactly one frame under load — visible,
/// intermittent, and easy to blame on input handling instead.
/// </remarks>
public sealed unsafe class FrameUniformSet : IDisposable
{
    private readonly VulkanContext _context;
    private readonly VulkanBuffer[] _buffers;
    private readonly VulkanBuffer _rig;
    private readonly DescriptorSet[] _sets;
    private readonly bool _rayTracing;
    private DescriptorPool _pool;
    private int _frameCounter;
    private Matrix4x4? _previousViewProjection;

    private FrameUniformSet(
        VulkanContext context,
        VulkanBuffer[] buffers,
        VulkanBuffer rig,
        DescriptorSet[] sets,
        DescriptorPool pool,
        bool rayTracing)
    {
        _context = context;
        _buffers = buffers;
        _rig = rig;
        _sets = sets;
        _pool = pool;
        _rayTracing = rayTracing;
    }

    /// <summary>The buffer of lights, for anything outside this pass that needs them.</summary>
    /// <remarks>
    /// The tracing stage samples a light to shadow, and it has to sample by the same
    /// weights the shading uses or the fraction it estimates is a fraction of something
    /// else. Sharing the buffer is what keeps the two from drifting apart.
    /// </remarks>
    public VulkanBuffer Rig => _rig;

    /// <summary>How much ray tracing the shader is asked to do.</summary>
    public RayTracingSettings Settings { get; set; } = RayTracingSettings.For(RayTracingQuality.None);

    /// <summary>How many frames it covers.</summary>
    public int Count => _sets.Length;

    /// <summary>Creates the set.</summary>
    /// <param name="context">Device context.</param>
    /// <param name="pipeline">Pipeline whose frame layout to match.</param>
    /// <param name="frames">How many frames may be in flight.</param>
    /// <returns>The set.</returns>
    public static FrameUniformSet Create(VulkanContext context, MeshPipeline pipeline, int frames)
    {
        return CreateFor(context, pipeline, frames);
    }

    private static FrameUniformSet CreateFor(VulkanContext context, MeshPipeline pipeline, int frames)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frames);

        var size = new DescriptorPoolSize
        {
            Type = DescriptorType.UniformBuffer,
            DescriptorCount = (uint)(frames * 2),
        };

        DescriptorPoolSize* sizes = stackalloc DescriptorPoolSize[2];
        sizes[0] = size;
        sizes[1] = new DescriptorPoolSize
        {
            Type = DescriptorType.AccelerationStructureKhr,
            DescriptorCount = (uint)frames,
        };

        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            PoolSizeCount = pipeline.RayTracing ? 2u : 1u,
            PPoolSizes = sizes,
            MaxSets = (uint)frames,
        };

        if (context.Api.CreateDescriptorPool(context.Device, in poolInfo, null, out DescriptorPool pool)
            != Result.Success)
        {
            throw new VulkanException("Could not create the frame descriptor pool.");
        }

        var buffers = new VulkanBuffer[frames];
        var sets = new DescriptorSet[frames];

        // Allocated once outside the loop: a stackalloc inside one grows the frame with
        // every iteration.
        WriteDescriptorSet* writes = stackalloc WriteDescriptorSet[2];
        ulong bufferSize = (ulong)Marshal.SizeOf<FrameUniforms>();

        // One rig for every frame: the lights are fixed for as long as a scene is loaded,
        // so there is nothing for a later frame to race against.
        ulong rigSize = (ulong)(16 + (GpuLight.Capacity * Marshal.SizeOf<GpuLight>()));
        VulkanBuffer rig = VulkanBuffer.CreateHostVisible(
            context, rigSize, BufferUsageFlags.UniformBufferBit);

        for (int i = 0; i < frames; i++)
        {
            buffers[i] = VulkanBuffer.CreateHostVisible(
                context, bufferSize, BufferUsageFlags.UniformBufferBit);

            DescriptorSetLayout layout = pipeline.FrameLayout;
            var allocateInfo = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = pool,
                DescriptorSetCount = 1,
                PSetLayouts = &layout,
            };

            if (context.Api.AllocateDescriptorSets(context.Device, in allocateInfo, out sets[i])
                != Result.Success)
            {
                throw new VulkanException("Could not allocate a frame descriptor set.");
            }

            var bufferInfo = new DescriptorBufferInfo
            {
                Buffer = buffers[i].Handle,
                Range = bufferSize,
            };

            var rigInfo = new DescriptorBufferInfo
            {
                Buffer = rig.Handle,
                Range = rigSize,
            };

            writes[0] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = sets[i],
                DstBinding = 0,
                DescriptorType = DescriptorType.UniformBuffer,
                DescriptorCount = 1,
                PBufferInfo = &bufferInfo,
            };
            writes[1] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = sets[i],
                DstBinding = 1,
                DescriptorType = DescriptorType.UniformBuffer,
                DescriptorCount = 1,
                PBufferInfo = &rigInfo,
            };

            context.Api.UpdateDescriptorSets(context.Device, 2, writes, 0, null);
        }

        var created = new FrameUniformSet(context, buffers, rig, sets, pool, pipeline.RayTracing);
        created.SetLights([]);

        return created;
    }

    /// <summary>Uploads the lights a scene was authored with.</summary>
    /// <param name="lights">The rig; more than the shader holds are narrowed down.</param>
    public void SetLights(IReadOnlyList<AuthoredLight> lights)
    {
        ArgumentNullException.ThrowIfNull(lights);

        IReadOnlyList<AuthoredLight> chosen = GpuLight.Choose(lights);

        int stride = Marshal.SizeOf<GpuLight>();
        byte[] bytes = new byte[16 + (GpuLight.Capacity * stride)];

        // The count leads, padded to a float4 so the array that follows starts on the
        // 16-byte boundary the standard uniform layout requires.
        BitConverter.TryWriteBytes(bytes.AsSpan(0, 4), (float)chosen.Count);

        for (int i = 0; i < chosen.Count; i++)
        {
            GpuLight light = GpuLight.From(chosen[i]);
            MemoryMarshal.Write(bytes.AsSpan(16 + (i * stride), stride), in light);
        }

        _rig.Write<byte>(bytes);
    }

    /// <summary>Points the ray-tracing paths at the scene they trace against.</summary>
    /// <param name="scene">The acceleration structure.</param>
    /// <remarks>
    /// Must be called before the first draw of a ray-tracing pipeline. Vulkan requires
    /// every statically used binding to be valid whether its branch runs or not, so an
    /// unwritten acceleration structure is undefined behaviour even at quality
    /// <see cref="RayTracingQuality.None"/>, where no ray is ever traced.
    /// </remarks>
    public void SetScene(RayTracingScene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);

        if (!_rayTracing)
        {
            return;
        }

        AccelerationStructureKHR handle = scene.Handle;

        foreach (DescriptorSet set in _sets)
        {
            var structureInfo = new WriteDescriptorSetAccelerationStructureKHR
            {
                SType = StructureType.WriteDescriptorSetAccelerationStructureKhr,
                AccelerationStructureCount = 1,
                PAccelerationStructures = &handle,
            };

            var write = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                PNext = &structureInfo,
                DstSet = set,
                DstBinding = 2,
                DescriptorType = DescriptorType.AccelerationStructureKhr,
                DescriptorCount = 1,
            };

            _context.Api.UpdateDescriptorSets(_context.Device, 1, in write, 0, null);
        }
    }

    /// <summary>Writes a frame's camera and binds its descriptor set.</summary>
    /// <param name="command">Command buffer to record into.</param>
    /// <param name="pipeline">Pipeline whose layout to bind against.</param>
    /// <param name="frame">Which frame in flight this is.</param>
    /// <param name="camera">The camera.</param>
    /// <param name="aspect">Viewport width divided by height.</param>
    /// <param name="width">Viewport width in pixels, for the motion vectors.</param>
    /// <param name="height">Viewport height in pixels, for the motion vectors.</param>
    public void Bind(
        CommandBuffer command,
        MeshPipeline pipeline,
        int frame,
        Camera camera,
        float aspect,
        float width = 0,
        float height = 0)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(camera);

        int index = frame % _sets.Length;

        RayTracingSettings settings = _rayTracing
            ? Settings
            : RayTracingSettings.For(RayTracingQuality.None);

        Matrix4x4 viewProjection = camera.View * camera.Projection(aspect);

        // The first frame has no previous one, and a motion vector against an identity
        // matrix is the whole screen moving at once. Its own is the honest answer: nothing
        // moved, because there was nothing to move from.
        Matrix4x4 previous = _previousViewProjection ?? viewProjection;

        _previousViewProjection = viewProjection;

        var uniforms = new FrameUniforms(
            viewProjection,
            previous,
            new Vector4(Vector3.Normalize(camera.LightDirection), 0),
            new Vector4(camera.Position, 1),
            new Vector4(
                settings.ShadowLights,
                settings.AmbientOcclusionRays,
                settings.ShadowSamples,
                settings.LightmapIndirect),

            // The viewport in pixels, so the motion vectors come out in pixels rather than
            // in a normalised space nobody can read.
            new Vector4(
                settings.AmbientOcclusionRadius, _frameCounter++ % 64, width, height));

        _buffers[index].Write<FrameUniforms>([uniforms]);

        DescriptorSet set = _sets[index];
        _context.Api.CmdBindDescriptorSets(
            command, PipelineBindPoint.Graphics, pipeline.Layout, 0, 1, in set, 0, null);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (VulkanBuffer buffer in _buffers)
        {
            buffer.Dispose();
        }

        _rig.Dispose();

        if (_pool.Handle != 0)
        {
            _context.Api.DestroyDescriptorPool(_context.Device, _pool, null);
            _pool = default;
        }
    }
}
